using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using Trawler.Services;

namespace Trawler.Tasks;

/// <summary>
/// Scheduled task (F-03): scans the whole movie library and downloads trailers
/// for every movie that doesn't have a local trailer yet. Shows progress in the
/// Emby dashboard task list and records a run summary for the config page.
/// </summary>
public class DownloadMissingTrailersTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IFfmpegManager _ffmpegManager;
    private readonly ILogger _logger;

    public DownloadMissingTrailersTask(
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        IFfmpegManager ffmpegManager,
        ILogger logger)
    {
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _ffmpegManager = ffmpegManager;
        _logger = logger;
    }

    public string Key => "DownloadMissingTrailers";

    public string Category => "Trawler";

    public string Description =>
        "Scans your movie library and downloads YouTube trailers for movies that do not have a local trailer yet.";

    public string Name => "Download Missing Trailers";

    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        var pipeline = new TrailerPipeline(_libraryManager, _libraryMonitor, _ffmpegManager, _logger);

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IsVirtualItem = false,
            IncludeItemTypes = new[] { nameof(Movie) }
        });

        var total = items.Length;
        _logger.Info("Trawler: scheduled task scanning {0} movie(s)", total);

        var done = 0;
        var downloaded = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (item is Movie movie)
            {
                try
                {
                    var result = await pipeline.ProcessAsync(movie, cancellationToken).ConfigureAwait(false);
                    if (result.Success)
                    {
                        downloaded++;
                        _logger.Info("Trawler: {0}: {1}", movie.Name, result.Detail);
                    }
                    else if (result.Skipped)
                    {
                        skipped++;
                        _logger.Debug("Trawler: {0}: skipped ({1})", movie.Name, result.Detail);
                    }
                    else
                    {
                        failed++;
                        _logger.Warn("Trawler: {0}: {1}", movie.Name, result.Detail);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.ErrorException($"Trawler: {movie.Name}", ex, Array.Empty<object>());
                }
            }

            done++;
            if (total > 0)
            {
                progress.Report(done * 100.0 / total);
            }
        }

        var summary = $"{DateTime.Now:g}: {total} movies, {downloaded} downloaded, {skipped} skipped, {failed} failed";
        _logger.Info("Trawler: scheduled task finished — {0}", summary);

        // F-42: remember the run summary so the config page can display it.
        try
        {
            var plugin = Plugin.Instance;
            if (plugin != null)
            {
                plugin.Configuration.LastRunSummary = summary;
                plugin.SaveConfiguration();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("Trawler: could not persist last-run summary: {0}", ex);
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Daily safety net at 04:00: self-heals anything ItemAdded missed
        // (server offline during add, monitor hiccups, failed downloads).
        return new List<TaskTriggerInfo>
        {
            new TaskTriggerInfo
            {
                Type = "DailyTrigger",
                TimeOfDayTicks = 4 * TimeSpan.TicksPerHour
            }
        };
    }
}
