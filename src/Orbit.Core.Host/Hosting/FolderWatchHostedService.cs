using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Orbit.Infrastructure.Files;

namespace Orbit.Core.Host.Hosting;

/// <summary>
/// Debounced FileSystemWatcher per attached project folder.
/// </summary>
public sealed class FolderWatchHostedService : IHostedService, IDisposable
{
    private readonly ProjectFolderStore _folders;
    private readonly FileIndexService _index;
    private readonly ConcurrentDictionary<string, WatchState> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _debounce = TimeSpan.FromMilliseconds(750);
    private Timer? _refreshTimer;

    public FolderWatchHostedService(ProjectFolderStore folders, FileIndexService index)
    {
        _folders = folders;
        _index = index;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        SyncWatchers();
        _refreshTimer = new Timer(_ => SyncWatchers(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _refreshTimer?.Change(Timeout.Infinite, 0);
        foreach (var state in _watchers.Values)
        {
            state.Dispose();
        }

        _watchers.Clear();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        foreach (var state in _watchers.Values)
        {
            state.Dispose();
        }

        _watchers.Clear();
    }

    private void SyncWatchers()
    {
        IReadOnlyList<ProjectFolderRecord> folders;
        try
        {
            folders = _folders.ListAll();
        }
        catch (Exception)
        {
            return;
        }

        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            active.Add(folder.Id);
            _watchers.AddOrUpdate(
                folder.Id,
                _ => CreateWatch(folder),
                (_, existing) =>
                {
                    if (!string.Equals(existing.RootPath, folder.RootPath, StringComparison.OrdinalIgnoreCase))
                    {
                        existing.Dispose();
                        return CreateWatch(folder);
                    }

                    return existing;
                });
        }

        foreach (var key in _watchers.Keys.ToList())
        {
            if (!active.Contains(key) && _watchers.TryRemove(key, out var removed))
            {
                removed.Dispose();
            }
        }
    }

    private WatchState CreateWatch(ProjectFolderRecord folder)
    {
        var state = new WatchState(folder.Id, folder.RootPath, _debounce, () =>
        {
            try
            {
                _index.ReindexFolder(folder.Id);
            }
            catch (Exception)
            {
                // keep host alive
            }
        });
        return state;
    }

    private sealed class WatchState : IDisposable
    {
        private readonly FileSystemWatcher? _watcher;
        private readonly Timer _timer;
        private readonly Action _onDebounced;
        private readonly TimeSpan _debounce;
        private int _pending;

        public string RootPath { get; }

        public WatchState(string folderId, string rootPath, TimeSpan debounce, Action onDebounced)
        {
            _ = folderId;
            RootPath = rootPath;
            _debounce = debounce;
            _onDebounced = onDebounced;
            _timer = new Timer(Fire, null, Timeout.Infinite, Timeout.Infinite);

            if (!Directory.Exists(rootPath))
            {
                return;
            }

            try
            {
                _watcher = new FileSystemWatcher(rootPath)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName
                                   | NotifyFilters.DirectoryName
                                   | NotifyFilters.LastWrite
                                   | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                _watcher.Changed += OnChanged;
                _watcher.Created += OnChanged;
                _watcher.Deleted += OnChanged;
                _watcher.Renamed += OnRenamed;
                _watcher.Error += (_, _) => { };
            }
            catch (Exception)
            {
                _watcher?.Dispose();
                _watcher = null;
            }
        }

        private void OnRenamed(object sender, RenamedEventArgs e) => Signal();

        private void OnChanged(object sender, FileSystemEventArgs e) => Signal();

        private void Signal()
        {
            Interlocked.Exchange(ref _pending, 1);
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }

        private void Fire(object? state)
        {
            if (Interlocked.Exchange(ref _pending, 0) == 1)
            {
                _onDebounced();
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
            }
        }
    }
}
