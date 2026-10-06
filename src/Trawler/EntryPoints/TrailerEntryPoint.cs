using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using Trawler.Services;

namespace Trawler.EntryPoints;

/// <summary>
/// Reacts to new movies being added to the library: waits a configurable delay (so metadata
/// providers can populate RemoteTrailers), then runs the trailer pipeline for the item.
/// Everything is swallowed and logged — a broken download must never reach Emby's event loop.
/// </summary>
public class TrailerEntryPoint : IServerEntryPoint, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IFfmpegManager _ffmpegManager;
    private readonly ILogger _logger;

    /// <summary>Item ids already scheduled — prevents duplicate queues for the same movie (E-08).</summary>
    private readonly ConcurrentDictionary<Guid, byte> _queued = new ConcurrentDictionary<Guid, byte>();

    private TrailerPipeline _pipeline;
    private CancellationTokenSource _cts;
    private volatile bool _disposed;

    public TrailerEntryPoint(
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

    public void Run()
    {
        _cts = new CancellationTokenSource();
        _pipeline = new TrailerPipeline(_libraryManager, _libraryMonitor, _ffmpegManager, _logger);
        _libraryManager.ItemAdded += OnItemAdded;

        var config = Plugin.Instance?.Configuration;
        _logger.Info(
            "Trawler: entry point started (auto-download: {0}, delay: {1}s)",
            config?.EnableAutoDownload ?? true,
            config?.TriggerDelaySeconds ?? 30);
    }

    private void OnItemAdded(object sender, ItemChangeEventArgs e)
    {
        try
        {
            if (_disposed)
            {
                return;
            }

            if (!(e.Item is Movie movie) || !TrailerPipeline.IsMovieEligible(movie))
            {
                return;
            }

            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.EnableAutoDownload)
            {
                return;
            }

            var id = movie.Id;
            if (!_queued.TryAdd(id, 0))
            {
                return; // already scheduled
            }

            var ct = _cts.Token;
            var delaySeconds = Math.Max(0, config.TriggerDelaySeconds);

            _ = Task.Run(async () =>
            {
                try
                {
                    // F-01: give metadata providers time to fill in RemoteTrailers first.
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);

                    // Re-fetch so we process the item with its post-refresh metadata.
                    var current = _libraryManager.GetItemById(id) as Movie;
                    if (current == null || !TrailerPipeline.IsMovieEligible(current))
                    {
                        return;
                    }

                    var result = await _pipeline.ProcessAsync(current, ct).ConfigureAwait(false);
                    if (result.Success)
                    {
                        _logger.Info("Trawler: {0}: {1}", current.Name, result.Detail);
                    }
                    else if (result.Skipped)
                    {
                        _logger.Debug("Trawler: {0}: skipped ({1})", current.Name, result.Detail);
                    }
                    else
                    {
                        _logger.Warn("Trawler: {0}: {1}", current.Name, result.Detail);
                    }
                }
                catch (OperationCanceledException)
                {
                    // server shutting down
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Trawler: error while processing a newly added movie", ex, Array.Empty<object>());
                }
                finally
                {
                    _queued.TryRemove(id, out _);
                }
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The event handler itself must never throw into Emby's event loop.
            _logger.ErrorException("Trawler: ItemAdded handler failed", ex, Array.Empty<object>());
        }
    }

    public void Dispose()
    {
        _disposed = true;

        // Symmetric with Run(): exactly the subscription made there is removed here.
        _libraryManager.ItemAdded -= OnItemAdded;

        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }

        _cts?.Dispose();
        _cts = null;
    }
}
