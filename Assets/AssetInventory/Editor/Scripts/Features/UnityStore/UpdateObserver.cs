using ImpossibleRobert.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace AssetInventory
{
    public sealed class UpdateObserver
    {
        public bool InitializationDone;
        public float InitializationProgress;
        public bool PrioInitializationDone;
        public float PrioInitializationProgress;
        public int DownloadCount;

        private readonly FileSystemWatcher _watcher;
        private readonly string[] _fileTypes;

        private List<AssetInfo> _all = new List<AssetInfo>();
        private List<AssetInfo> _prioritized;
        private readonly Dictionary<int, AssetDownloader> _loaders = new Dictionary<int, AssetDownloader>();

        private int _prioCount;
        private int _curIndex;
        private DateTime _lastObserverActivity;

        // Debounce: accumulate FSW paths, process in batches
        private readonly ConcurrentQueue<string> _pendingPaths = new ConcurrentQueue<string>();
        private DateTime _lastDebounceFlush = DateTime.MinValue;
        private const int DEBOUNCE_MS = 500;

#if UNITY_EDITOR_LINUX
        private static readonly StringComparison _pathComparison = StringComparison.Ordinal;
        private static readonly StringComparer _pathComparer = StringComparer.Ordinal;
#else
        private static readonly StringComparison _pathComparison = StringComparison.OrdinalIgnoreCase;
        private static readonly StringComparer _pathComparer = StringComparer.OrdinalIgnoreCase;
#endif

        public UpdateObserver(string path, IEnumerable<string> fileTypes)
        {
            if (!Directory.Exists(path)) return; // will throw error otherwise

            _fileTypes = fileTypes.Select(ft => "." + ft).ToArray(); // ensure fileTypes include the dot prefix

            _watcher = new FileSystemWatcher();
            _watcher.Path = path;
            _watcher.IncludeSubdirectories = true;
            _watcher.Filter = "*.*";
#if UNITY_EDITOR_LINUX
            _watcher.InternalBufferSize = 262144; // 256KB — inotify on Linux generates more events than Windows
#else
            _watcher.InternalBufferSize = 65536;
#endif

            _watcher.NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.LastWrite;

            _watcher.Changed += OnChanged;
            _watcher.Created += OnCreated;
            _watcher.Deleted += OnDeleted;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += (_, args) => { Debug.LogWarning($"Asset cache monitoring error: {args.GetException()}"); };

            _lastObserverActivity = DateTime.Now;

            ScanContinuously();
        }

        private DateTime _lastDownloadScan = DateTime.MinValue;
        private int _lastDownloadCount;

        private async void ScanContinuously()
        {
            while (true)
            {
                // Process debounced FSW events
                FlushPendingRefreshes();

                if (_watcher != null && _watcher.EnableRaisingEvents && AI.Config.autoStopObservation)
                {
                    if (DateTime.Now - _lastObserverActivity > TimeSpan.FromSeconds(AI.Config.observationTimeout))
                    {
                        Stop();
                    }
                }

                if (_all == null || _all.Count == 0)
                {
                    await Task.Delay(2000);
                    continue;
                }

                // refresh currently downloading items faster to show progress bars
                // RW: throttle the full-list fast-scan; without it this loop runs ~336k iterations/sec at idle
                TimeSpan scanInterval = _lastDownloadCount > 0
                    ? TimeSpan.FromMilliseconds(250)
                    : TimeSpan.FromSeconds(2);
                if (DateTime.Now - _lastDownloadScan >= scanInterval)
                {
                    _lastDownloadScan = DateTime.Now;
                    DownloadCount = 0;
                    int scanned = 0;
                    for (int i = 0; i < _all.Count; i++)
                    {
                        AssetDownloader downloader = _all[i].PackageDownloader;
                        if (downloader.GetState().state != AssetDownloader.State.Downloading)
                        {
                            // always refresh for single selections
                            if (_prioCount == 1 && i == 0) downloader.RefreshState();
                            // periodic yield to avoid editor-tick starvation on large libraries
                            if ((++scanned & 0x3FF) == 0) await Task.Yield();
                            continue;
                        }

                        DownloadCount++;

                        _lastObserverActivity = DateTime.Now; // keep observer alive
                        if (_watcher != null && !_watcher.EnableRaisingEvents) Start(); // start observer if it was stopped

                        downloader.RefreshState();
                        await Task.Delay(10);
                    }
                    _lastDownloadCount = DownloadCount;
                }

                if (_curIndex >= _prioCount) PrioInitializationDone = true;
                if (_curIndex >= _all.Count)
                {
                    InitializationDone = true;
                    await Task.Delay(1000);
                    continue;
                }

                AssetInfo info = _all[_curIndex];

                // Skip stable assets unless they were dirtied by a file system event
                bool isPrio = _curIndex < _prioCount;
                bool isDirty = info.PackageDownloader.IsDirty;
                bool isStable = info.PackageDownloader.IsStable;

                // Stable non-priority assets only need checking if dirtied
                if (isStable && !isPrio && !isDirty)
                {
                    // no-op, skip expensive I/O
                }
                else if (DateTime.Now - info.PackageDownloader.lastRefresh > TimeSpan.FromSeconds(isPrio ? 5 : (isDirty ? 2 : 60)))
                {
                    info.Refresh();
                    info.PackageDownloader.RefreshState();
                    if (AI.Config.observationSpeed > 0 && _curIndex % AI.Config.observationSpeed == 0) await Task.Yield();
                }

                InitializationProgress = (float)_curIndex / _all.Count;
                PrioInitializationProgress = _prioCount > 0 ? (float)_curIndex / _prioCount : 1f;
                _curIndex++;
            }
        }

        public void SetPrioritized(List<AssetInfo> prioritized)
        {
            // skip setting the same list twice since that will reset the initialization state
            if (_prioritized != null && _prioritized.Count == prioritized.Count)
            {
                HashSet<int> newIds = new HashSet<int>(prioritized.Select(p => p.AssetId));
                if (newIds.SetEquals(_prioritized.Select(p => p.AssetId))) return;
            }

            // sort prioritized to the beginning of all
            // below two lines are nicer to read but much slower than using a hashset + recreate
            // _all.RemoveAll(prioritized.Contains);
            // _all.InsertRange(0, prioritized);

            _prioritized = prioritized.OrderBy(info => info.PackageDownloader == null ? DateTime.MinValue : info.PackageDownloader.lastRefresh).ToList(); // break reference
            _prioCount = _prioritized.Count;

            // single items will get refreshed automatically, bulk selections need a rescan 
            if (_prioCount > 1)
            {
                InitializationDone = false;
                InitializationProgress = 0;
                PrioInitializationDone = false;
                PrioInitializationProgress = 0;
                _curIndex = 0;
            }

            // Convert prioritized to a HashSet for faster lookups
            HashSet<AssetInfo> prioritizedSet = new HashSet<AssetInfo>(prioritized);

            // Create a new list to hold the re-ordered items
            List<AssetInfo> reordered = new List<AssetInfo>(prioritized);

            // Add non-prioritized items to the reordered list, skipping those in prioritized
            foreach (AssetInfo item in _all)
            {
                if (!prioritizedSet.Contains(item)) reordered.Add(item);
            }
            _all = reordered;

            // only attach downloaders for prioritized items since the rest already have them from SetAll
            foreach (AssetInfo info in prioritized)
            {
                Attach(info);
            }
        }

        public void SetAll(List<AssetInfo> all)
        {
            _curIndex = 0;
            _all = all;
            AttachDownloaders();
        }

        private void AttachDownloaders()
        {
            _all.ForEach(Attach);
        }

        public void Attach(AssetInfo info)
        {
            if (info.PackageDownloader == null)
            {
                // hook up existing downloads if existent
                if (_loaders.TryGetValue(info.AssetId, out AssetDownloader downloader))
                {
                    info.PackageDownloader = downloader;
                }
                else
                {
                    info.PackageDownloader = new AssetDownloader(info);
                    _loaders.Add(info.AssetId, info.PackageDownloader);
                }
            }

            // update reference in case new data was added
            info.PackageDownloader.SetAsset(info);
        }

        public void SetPath(string path)
        {
            _watcher.Path = path;
        }

        private bool IsWatchedType(string path)
        {
            return _fileTypes.Any(ft => path.EndsWith(ft, StringComparison.OrdinalIgnoreCase));
        }

        private void TriggerRefresh(string path)
        {
            _lastObserverActivity = DateTime.Now;
            _pendingPaths.Enqueue(path);
        }

        private void FlushPendingRefreshes()
        {
            if (_pendingPaths.IsEmpty) return;
            if ((DateTime.Now - _lastDebounceFlush).TotalMilliseconds < DEBOUNCE_MS) return;

            _lastDebounceFlush = DateTime.Now;

            // Drain all pending paths and deduplicate to directory level
            HashSet<string> directories = new HashSet<string>(_pathComparer);

            while (_pendingPaths.TryDequeue(out string p))
            {
                if (TryGetDirectory(p, out string dir)) directories.Add(dir);
            }

            if (directories.Count == 0) return;

            // Mark affected assets as dirty
            for (int i = 0; i < _all.Count; i++)
            {
                AssetInfo info = _all[i];
                if (string.IsNullOrEmpty(info.Location)) continue;

                string location = info.GetLocation(true);
                if (string.IsNullOrEmpty(location)) continue;

                if (!TryGetDirectory(location, out string locationDir)) continue;

                bool matches = false;
                foreach (string dir in directories)
                {
                    if (locationDir.StartsWith(dir, _pathComparison) || dir.StartsWith(locationDir, _pathComparison))
                    {
                        matches = true;
                        break;
                    }
                }
                if (!matches) continue;

                if (info.PackageDownloader != null) info.PackageDownloader.IsDirty = true;
                info.Refresh();
                info.PackageDownloader?.RefreshState(true);
            }
        }

        private static bool TryGetDirectory(string path, out string directory)
        {
            directory = null;
            if (string.IsNullOrWhiteSpace(path)) return false;

            int subPathIndex = path.IndexOf(Asset.SUB_PATH);
            if (subPathIndex >= 0) path = path.Substring(0, subPathIndex);
            if (string.IsNullOrWhiteSpace(path)) return false;

            try
            {
                directory = Path.GetExtension(path) != string.Empty ? Path.GetDirectoryName(path) : path;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
            catch (PathTooLongException)
            {
                return false;
            }

            return !string.IsNullOrEmpty(directory);
        }

        private void OnCreated(object source, FileSystemEventArgs e)
        {
            // Debug.Log($"Created File: {e.FullPath} {e.ChangeType}");

            TriggerRefresh(e.FullPath);
        }

        private void OnChanged(object source, FileSystemEventArgs e)
        {
            // Debug.Log($"Changed File: {e.FullPath} {e.ChangeType}");

            TriggerRefresh(e.FullPath);
        }

        private void OnDeleted(object source, FileSystemEventArgs e)
        {
            // Debug.Log($"Deleted File: {e.FullPath} {e.ChangeType}");

            TriggerRefresh(e.FullPath);
        }

        private void OnRenamed(object source, RenamedEventArgs e)
        {
            // Debug.Log($"File: {e.OldFullPath} renamed to {e.FullPath}");

            TriggerRefresh(e.FullPath);
        }

        public async void Start()
        {
            // Debug.Log("Start observer");

            _lastObserverActivity = DateTime.Now;
            if (_watcher == null || _watcher.EnableRaisingEvents) return;

            // enabling the events will scan the directory which can lock up the main thread
            await Task.Run(() =>
            {
                if (!string.IsNullOrEmpty(_watcher?.Path))
                {
                    _watcher.EnableRaisingEvents = true;
                }
            });
        }

        public async void Stop()
        {
            // Debug.Log("Stop observer");

            _lastObserverActivity = DateTime.Now; // set to eliminate potential race conditions stopping it again

            await Task.Run(() =>
            {
                if (_watcher != null && _watcher.EnableRaisingEvents) _watcher.EnableRaisingEvents = false;
            });
        }

        public bool IsActive()
        {
            return _watcher != null && _watcher.EnableRaisingEvents;
        }
    }
}
