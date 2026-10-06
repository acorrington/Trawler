using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Logging;
using Trawler.Configuration;

namespace Trawler.Services;

/// <summary>Outcome of processing a single movie.</summary>
public sealed class PipelineResult
{
    public bool Success { get; private set; }
    public bool Skipped { get; private set; }
    public string Detail { get; private set; }

    public static PipelineResult Ok(string detail) => new PipelineResult { Success = true, Detail = detail };
    public static PipelineResult Skip(string detail) => new PipelineResult { Skipped = true, Detail = detail };
    public static PipelineResult Fail(string detail) => new PipelineResult { Detail = detail };
}

/// <summary>
/// Orchestrates the full trailer pipeline for one movie:
/// needs-check -> candidate URLs (RemoteTrailers, then YouTube search) -> stream resolution
/// -> download -> ffmpeg merge -> install next to the movie -> metadata refresh.
/// Shared by the ItemAdded entry point and the scheduled task.
/// </summary>
public sealed class TrailerPipeline
{
    private const int MaxCandidateAttempts = 6;

    private static readonly string[] TrailerExtensions = { ".mp4", ".mkv", ".webm", ".m4v", ".mov" };

    /// <summary>Bounded concurrency across all triggers (N-03): at most 2 downloads at once.</summary>
    private static readonly SemaphoreSlim Gate = new(2, 2);

    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IFfmpegManager _ffmpegManager;
    private readonly ILogger _logger;
    private readonly YouTubeService _youtube;
    private readonly DownloadMerger _merger;

    public TrailerPipeline(ILibraryManager libraryManager, ILibraryMonitor libraryMonitor, IFfmpegManager ffmpegManager, ILogger logger)
    {
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _ffmpegManager = ffmpegManager;
        _logger = logger;
        _youtube = new YouTubeService(logger);
        _merger = new DownloadMerger(logger);
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    // ------------------------------------------------------------------ checks

    public static bool IsMovieEligible(BaseItem item)
    {
        if (!(item is Movie) || string.IsNullOrEmpty(item.Path))
        {
            return false;
        }

        // LocationType.FileSystem (or a folder on disk); reject virtual/remote items.
        try
        {
            return File.Exists(item.Path) || Directory.Exists(item.Path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Directory the trailer belongs in + the base name Emby's trailer regex matches on.</summary>
    public static (string Directory, string BaseName) GetMovieLocation(BaseItem item)
    {
        var path = item.Path;
        if (Directory.Exists(path))
        {
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return (trimmed, Path.GetFileName(trimmed));
        }

        return (Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>
    /// Returns the existing local trailer file for this movie (non-empty), or null.
    /// A 0-byte leftover from a failed download is deleted and reported as missing (edge case table).
    /// </summary>
    public string FindExistingTrailerFile(Movie movie)
    {
        var (dir, baseName) = GetMovieLocation(movie);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return null;
        }

        try
        {
            foreach (var ext in TrailerExtensions)
            {
                var candidate = Path.Combine(dir, baseName + "-trailer" + ext);
                if (!File.Exists(candidate))
                {
                    continue;
                }

                if (new FileInfo(candidate).Length > 0)
                {
                    return candidate;
                }

                try
                {
                    File.Delete(candidate);
                    _logger.Info("Trawler: removed 0-byte trailer leftover {0}", candidate);
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Trawler: could not delete 0-byte trailer {candidate}: {{0}}", ex);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("Trawler: error scanning for existing trailer: {0}", ex);
        }

        return null;
    }

    public bool NeedsTrailer(Movie movie)
    {
        if (FindExistingTrailerFile(movie) != null)
        {
            return false;
        }

        var localIds = movie.LocalTrailerIds;
        return localIds == null || localIds.Length == 0;
    }

    // ------------------------------------------------------------------ candidates

    private List<string> CollectRemoteTrailerIds(Movie movie)
    {
        var ids = new List<string>();
        var remote = movie.RemoteTrailers;
        if (remote == null)
        {
            return ids;
        }

        foreach (var entry in remote)
        {
            if (entry != null && YouTubeService.TryGetVideoId(entry, out var id) && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private async Task<List<string>> CollectSearchIdsAsync(Movie movie, PluginConfiguration config, CancellationToken ct)
    {
        if (!config.EnableYouTubeSearchFallback)
        {
            return new List<string>();
        }

        var year = movie.ProductionYear ?? 0;
        return await _youtube.SearchTrailerIdsAsync(movie.Name, year, Math.Max(1, config.MaxSearchResults), ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ ffmpeg

    private string ResolveFfmpegPath(PluginConfiguration config)
    {
        var overridePath = config?.FfmpegPathOverride;
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        try
        {
            var encoderPath = _ffmpegManager?.FfmpegConfiguration?.EncoderPath;
            if (!string.IsNullOrWhiteSpace(encoderPath) && File.Exists(encoderPath))
            {
                return encoderPath;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug("Trawler: IFfmpegManager.EncoderPath unavailable: {0}", ex.Message);
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            var exe = Path.Combine(dir.Trim(), "ffmpeg.exe");
            if (File.Exists(exe))
            {
                return exe;
            }

            exe = Path.Combine(dir.Trim(), "ffmpeg");
            if (File.Exists(exe))
            {
                return exe;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ install

    private async Task InstallAsync(string sourcePath, Movie movie, CancellationToken ct)
    {
        var (dir, baseName) = GetMovieLocation(movie);
        var savePath = Path.Combine(dir, baseName + "-trailer.mp4");

        _libraryMonitor.ReportFileSystemChangeBeginning(savePath);
        try
        {
            try
            {
                File.Move(sourcePath, savePath, overwrite: true);
            }
            catch (IOException)
            {
                // Cross-volume move (temp dir is on C:, library on E:) — copy then delete.
                File.Copy(sourcePath, savePath, true);
                try
                {
                    File.Delete(sourcePath);
                }
                catch (Exception ex)
                {
                    _logger.Debug("Trawler: could not delete temp file {0}: {1}", sourcePath, ex.Message);
                }
            }

            _logger.Info("Trawler: installed trailer {0}", savePath);
        }
        finally
        {
            _libraryMonitor.ReportFileSystemChangeComplete(savePath, true);
        }

        // F-35: refresh the movie so Emby picks up the local trailer without a full scan.
        try
        {
            await movie.RefreshMetadata(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Trawler: metadata refresh failed for {movie.Name}: {{0}}", ex);
        }
    }

    // ------------------------------------------------------------------ main pipeline

    /// <summary>Run the full pipeline for one movie. Never throws except on cancellation.</summary>
    public async Task<PipelineResult> ProcessAsync(Movie movie, CancellationToken ct)
    {
        if (!IsMovieEligible(movie))
        {
            return PipelineResult.Skip("not an eligible file-based movie");
        }

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate: another trigger (event vs task) may have won the race (E-08).
            if (FindExistingTrailerFile(movie) != null)
            {
                return PipelineResult.Skip("local trailer already exists");
            }

            var config = Config;
            var remoteIds = CollectRemoteTrailerIds(movie);
            var attempted = new List<string>();

            async Task<PipelineResult> TryCandidatesAsync(List<string> ids)
            {
                foreach (var videoId in ids)
                {
                    if (attempted.Count >= MaxCandidateAttempts)
                    {
                        break;
                    }

                    attempted.Add(videoId);
                    ct.ThrowIfCancellationRequested();

                    var result = await TryOneAsync(movie, videoId, config, ct).ConfigureAwait(false);
                    if (result != null)
                    {
                        return result;
                    }
                }

                return null;
            }

            var success = await TryCandidatesAsync(remoteIds).ConfigureAwait(false);
            if (success != null)
            {
                return success;
            }

            // F-11: no RemoteTrailers (or every remote candidate failed) — try a YouTube title search.
            if (config.EnableYouTubeSearchFallback && attempted.Count == 0)
            {
                var searchIds = await CollectSearchIdsAsync(movie, config, ct).ConfigureAwait(false);
                searchIds.RemoveAll(id => attempted.Contains(id));
                success = await TryCandidatesAsync(searchIds).ConfigureAwait(false);
                if (success != null)
                {
                    return success;
                }
            }

            if (attempted.Count == 0)
            {
                return PipelineResult.Fail("no trailer candidates (RemoteTrailers empty and search disabled or empty)");
            }

            return PipelineResult.Fail($"all {attempted.Count} candidate video(s) failed to produce a downloadable trailer");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.ErrorException($"Trawler: unexpected error processing {movie.Name}", ex, Array.Empty<object>());
            return PipelineResult.Fail("unexpected error: " + ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Resolve + download + merge + install one candidate. Returns a result on success, null to try the next candidate.</summary>
    private async Task<PipelineResult> TryOneAsync(Movie movie, string videoId, PluginConfiguration config, CancellationToken ct)
    {
        var streams = await _youtube.ResolveAsync(videoId, config.MaxVideoHeight, ct).ConfigureAwait(false);
        if (streams == null)
        {
            _logger.Warn("Trawler: could not resolve any downloadable stream for {0} ({1}) — trying next candidate", videoId, movie.Name);
            return null;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "Trawler", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var videoFile = Path.Combine(tempDir, "video.part");
            var haveVideo = await _merger.DownloadToFileAsync(streams.VideoUrl, videoFile, streams.DownloadUserAgent, ct).ConfigureAwait(false);
            if (!haveVideo)
            {
                _logger.Warn("Trawler: video stream download failed for {0} ({1})", videoId, movie.Name);
                return null;
            }

            string finalTempFile;
            if (streams.IsProgressive)
            {
                finalTempFile = videoFile;
            }
            else
            {
                var audioFile = Path.Combine(tempDir, "audio.part");
                var haveAudio = await _merger.DownloadToFileAsync(streams.AudioUrl, audioFile, streams.DownloadUserAgent, ct).ConfigureAwait(false);
                if (!haveAudio)
                {
                    _logger.Warn("Trawler: audio stream download failed for {0} ({1})", videoId, movie.Name);
                    return null;
                }

                var ffmpegPath = ResolveFfmpegPath(config);
                if (ffmpegPath == null)
                {
                    _logger.Error("Trawler: ffmpeg not found — cannot merge trailer for {0}. Set an ffmpeg path in the plugin config.", movie.Name);
                    return PipelineResult.Fail("ffmpeg not found");
                }

                var mergedFile = Path.Combine(tempDir, "merged.mp4");
                var merged = await _merger.MergeAsync(videoFile, audioFile, mergedFile, ffmpegPath, ct).ConfigureAwait(false);
                if (!merged)
                {
                    _logger.Warn("Trawler: ffmpeg merge failed for {0} ({1})", videoId, movie.Name);
                    return null;
                }

                finalTempFile = mergedFile;
            }

            await InstallAsync(finalTempFile, movie, ct).ConfigureAwait(false);
            return PipelineResult.Ok($"downloaded trailer for {movie.Name} from {videoId} ({streams.ResolvedBy}, {streams.Height}p{(streams.IsProgressive ? ", progressive" : "")})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.ErrorException($"Trawler: candidate {videoId} failed for {movie.Name}", ex, Array.Empty<object>());
            return null;
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
