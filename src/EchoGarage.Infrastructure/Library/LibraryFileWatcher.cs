using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Infrastructure.Library;

/// <summary>
/// Watches every library folder recursively and reports raw add/update/delete events.
/// Debouncing and indexing belong to the consumer, so bursts of events from a single
/// copy operation collapse into one rescan.
/// </summary>
public sealed class LibraryFileWatcher : IFileWatcherService
{
    private readonly IAppLogger _logger;
    private readonly object _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];

    public LibraryFileWatcher(IAppLogger logger)
    {
        _logger = logger;
    }

    public event EventHandler<LibraryTrackChangedEventArgs>? Changed;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _watchers.Count > 0;
            }
        }
    }

    public void Start(IReadOnlyList<LibraryFolder> folders)
    {
        Stop();
        lock (_gate)
        {
            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder.Path))
                {
                    _logger.Warn($"Library folder does not exist and cannot be watched: {folder.Path}");
                    continue;
                }

                try
                {
                    var watcher = new FileSystemWatcher(folder.Path)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                       NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 64 * 1024,
                        Filter = "*.*",
                        EnableRaisingEvents = true
                    };

                    watcher.Created += OnCreated;
                    watcher.Changed += OnChanged;
                    watcher.Deleted += OnDeleted;
                    watcher.Renamed += OnRenamed;
                    watcher.Error += OnError;
                    _watchers.Add(watcher);
                    _logger.Info($"Watching library folder: {folder.Path}");
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to watch folder {folder.Path}", ex);
                }
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Watcher dispose error: {ex.Message}");
                }
            }

            _watchers.Clear();
        }
    }

    public void Dispose() => Stop();

    private void OnCreated(object sender, FileSystemEventArgs e) =>
        Raise(e.FullPath, TrackChangeReason.Created);

    private void OnChanged(object sender, FileSystemEventArgs e) =>
        Raise(e.FullPath, TrackChangeReason.Changed);

    private void OnDeleted(object sender, FileSystemEventArgs e) =>
        Raise(e.FullPath, TrackChangeReason.Deleted);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        Raise(e.OldFullPath, TrackChangeReason.Deleted);
        Raise(e.FullPath, TrackChangeReason.Created);
    }

    private void OnError(object sender, ErrorEventArgs e) =>
        _logger.Warn($"File watcher error: {e.GetException().Message}");

    private void Raise(string path, TrackChangeReason reason)
    {
        if (!PathUtil.IsSupportedAudio(path))
        {
            return;
        }

        try
        {
            Changed?.Invoke(this, new LibraryTrackChangedEventArgs
            {
                Kind = reason switch
                {
                    TrackChangeReason.Created => LibraryChangeKind.Added,
                    TrackChangeReason.Deleted => LibraryChangeKind.Removed,
                    _ => LibraryChangeKind.Updated
                },
                Path = path
            });
        }
        catch (Exception ex)
        {
            _logger.Error("Watcher handler failed", ex);
        }
    }

    private enum TrackChangeReason
    {
        Created,
        Changed,
        Deleted
    }
}
