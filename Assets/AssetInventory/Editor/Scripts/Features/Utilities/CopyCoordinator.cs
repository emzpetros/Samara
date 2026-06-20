using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    /// <summary>
    /// Coordinates asset copy operations across parallel tasks to prevent duplicate work,
    /// race conditions, and excessive AssetDatabase.Refresh() calls.
    /// Uses smart caching with reference counting and file-existence validation.
    /// </summary>
    public static class CopyCoordinator
    {
        /// <summary>
        /// Represents a cached copy operation entry.
        /// </summary>
        private sealed class CopyEntry
        {
            public string TargetPath;
            public Task<string> InFlightTask;
            public int RefCount;
            public DateTime CompletedAt;
            public readonly object Lock = new object();
        }

        /// <summary>
        /// Categories for file classification based on metadata availability.
        /// </summary>
        public enum FileCategory
        {
            /// <summary>Has source meta file on disk - can copy directly</summary>
            HasMeta,
            /// <summary>Has known GUID but no meta file - needs Unity to create meta, then GUID update</summary>
            HasGuidNoMeta,
            /// <summary>No meta file, no known GUID - let Unity handle everything</summary>
            NoInfo,
            /// <summary>Script file (cs, dll, asmdef, asmref, rsp) - must be copied last to avoid domain reload</summary>
            Script
        }

        // Cache entries keyed by "GUID:targetFolder" to support same file in different destinations
        private static readonly ConcurrentDictionary<string, CopyEntry> _entries = new ConcurrentDictionary<string, CopyEntry>();

        // Timeout for stale in-flight operations (e.g., if caller crashed before releasing)
        private static readonly TimeSpan StaleTimeout = TimeSpan.FromMinutes(5);

        // Domain reload handling
        private static bool _initialized;

        /// <summary>
        /// Initializes the coordinator and hooks into domain reload events.
        /// Called automatically on first use.
        /// </summary>
        private static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        private static void OnBeforeAssemblyReload()
        {
            // Clear cache on domain reload since static state is lost anyway
            // and file references may become invalid
            Reset();
        }

        /// <summary>
        /// Generates a cache key for a file copy operation.
        /// </summary>
        private static string GetCacheKey(string guid, string targetFolder)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            string normalizedFolder = targetFolder?.Replace("\\", "/").TrimEnd('/').ToLowerInvariant() ?? "";
            return $"{guid}:{normalizedFolder}";
        }

        /// <summary>
        /// Attempts to acquire a copy operation for the given GUID.
        /// If another operation is in-flight for the same GUID+folder, awaits it.
        /// If already completed and valid, returns the cached path.
        /// Otherwise, starts the copy operation.
        /// </summary>
        /// <param name="guid">The GUID of the asset</param>
        /// <param name="targetFolder">The destination folder</param>
        /// <param name="copyFunc">The function to execute the actual copy</param>
        /// <returns>The target path of the copied file</returns>
        public static async Task<string> AcquireOrCopy(string guid, string targetFolder, Func<Task<string>> copyFunc)
        {
            EnsureInitialized();

            string key = GetCacheKey(guid, targetFolder);

            // No GUID - can't deduplicate, just execute
            if (key == null)
            {
                return await copyFunc();
            }

            CopyEntry entry;
            Task<string> taskToAwait = null;
            Task<string> ownTask = null;
            bool weAreExecuting = false;

            // Get or create entry
            entry = _entries.GetOrAdd(key, _ => new CopyEntry());

            lock (entry.Lock)
            {
                entry.RefCount++;

                // Check if there's a completed result we can validate
                if (entry.InFlightTask == null && entry.TargetPath != null)
                {
                    // Validate file still exists
                    string fullPath = AssetUtils.AddProjectRoot(entry.TargetPath);
                    if (File.Exists(fullPath))
                    {
                        return entry.TargetPath;
                    }

                    // File was deleted, clear cached result
                    entry.TargetPath = null;
                }

                // Check for stale in-flight task
                if (entry.InFlightTask != null)
                {
                    if (entry.InFlightTask.IsCompleted)
                    {
                        // Task completed but result wasn't stored (error case)
                        entry.InFlightTask = null;
                    }
                    else if (DateTime.UtcNow - entry.CompletedAt > StaleTimeout && entry.CompletedAt != default)
                    {
                        // Stale task - someone crashed, clear it
                        entry.InFlightTask = null;
                    }
                }

                // If there's an in-flight task, await it
                if (entry.InFlightTask != null)
                {
                    taskToAwait = entry.InFlightTask;
                }
                else
                {
                    // We're the first - start the copy
                    weAreExecuting = true;
                    entry.CompletedAt = DateTime.UtcNow; // Track start time for stale detection
                    TaskCompletionSource<string> tcs = new TaskCompletionSource<string>();
                    entry.InFlightTask = tcs.Task;
                    ownTask = tcs.Task; // Capture locally: ExecuteCopyAsync can clear InFlightTask via reentrant lock if copyFunc completes synchronously

                    // Execute copy outside lock
                    _ = ExecuteCopyAsync(entry, key, copyFunc, tcs);
                }
            }

            if (taskToAwait != null)
            {
                // Wait for another task's result
                try
                {
                    return await taskToAwait;
                }
                finally
                {
                    Release(key);
                }
            }

            // We started the copy, await our own task
            try
            {
                return await ownTask;
            }
            finally
            {
                if (!weAreExecuting)
                {
                    Release(key);
                }
            }
        }

        private static async Task ExecuteCopyAsync(CopyEntry entry, string key, Func<Task<string>> copyFunc, TaskCompletionSource<string> tcs)
        {
            try
            {
                string result = await copyFunc();

                lock (entry.Lock)
                {
                    entry.TargetPath = result;
                    entry.InFlightTask = null;
                    entry.CompletedAt = DateTime.UtcNow;
                }

                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                lock (entry.Lock)
                {
                    entry.InFlightTask = null;
                    entry.TargetPath = null;
                }

                tcs.TrySetException(ex);
            }
            finally
            {
                Release(key);
            }
        }

        /// <summary>
        /// Releases a reference to a copy operation.
        /// When RefCount reaches 0, the entry becomes eligible for eviction.
        /// </summary>
        private static void Release(string key)
        {
            if (key == null) return;

            if (_entries.TryGetValue(key, out CopyEntry entry))
            {
                lock (entry.Lock)
                {
                    entry.RefCount--;

                    // Evict if no references and no in-flight task
                    // Keep completed entries for a short while for rapid reuse
                    if (entry.RefCount <= 0 && entry.InFlightTask == null)
                    {
                        // Could implement TTL-based eviction here if memory is a concern
                        // For now, keep entries until explicit invalidation or Reset()
                    }
                }
            }
        }

        /// <summary>
        /// Validates and returns a cached path if the file still exists.
        /// </summary>
        /// <param name="guid">The GUID of the asset</param>
        /// <param name="targetFolder">The destination folder</param>
        /// <returns>The cached path if valid, null otherwise</returns>
        public static string ValidateAndGet(string guid, string targetFolder)
        {
            EnsureInitialized();

            string key = GetCacheKey(guid, targetFolder);
            if (key == null) return null;

            if (_entries.TryGetValue(key, out CopyEntry entry))
            {
                lock (entry.Lock)
                {
                    if (entry.TargetPath != null)
                    {
                        string fullPath = AssetUtils.AddProjectRoot(entry.TargetPath);
                        if (File.Exists(fullPath))
                        {
                            return entry.TargetPath;
                        }

                        // File was deleted, invalidate
                        entry.TargetPath = null;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Invalidates a specific cached entry by GUID and folder.
        /// </summary>
        public static void Invalidate(string guid, string targetFolder)
        {
            string key = GetCacheKey(guid, targetFolder);
            if (key != null)
            {
                _entries.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// Invalidates all cached entries for files in the specified folder.
        /// </summary>
        /// <param name="folderPath">The folder path (can be partial, will match contains)</param>
        public static void InvalidateFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;

            string normalizedFolder = folderPath.Replace("\\", "/").ToLowerInvariant();

            foreach (string key in _entries.Keys)
            {
                if (key.Contains(normalizedFolder))
                {
                    _entries.TryRemove(key, out _);
                }
            }
        }

        /// <summary>
        /// Clears all cached entries.
        /// Call this at the start of bulk operations to ensure fresh state.
        /// </summary>
        public static void Reset()
        {
            _entries.Clear();
        }

        /// <summary>
        /// Classifies a file based on its metadata availability.
        /// </summary>
        /// <param name="sourcePath">The source file path</param>
        /// <param name="guid">The known GUID, if any</param>
        /// <param name="fileType">The file extension without dot</param>
        /// <returns>The file category</returns>
        public static FileCategory ClassifyFile(string sourcePath, string guid, string fileType)
        {
            // Check for script types first (always processed last)
            if (IsScriptType(fileType))
            {
                return FileCategory.Script;
            }

            // Check if source meta file exists
            string sourceMetaPath = sourcePath + ".meta";
            if (File.Exists(sourceMetaPath))
            {
                return FileCategory.HasMeta;
            }

            // Check if we have a known GUID
            if (!string.IsNullOrEmpty(guid))
            {
                return FileCategory.HasGuidNoMeta;
            }

            return FileCategory.NoInfo;
        }

        /// <summary>
        /// Checks if the file type is a script-related type that triggers domain reload.
        /// </summary>
        public static bool IsScriptType(string fileType)
        {
            if (string.IsNullOrEmpty(fileType)) return false;

            string lower = fileType.ToLowerInvariant();
            return lower == "cs" || lower == "dll" || lower == "asmdef" || lower == "asmref" || lower == "rsp";
        }

        /// <summary>
        /// Gets the current cache statistics for debugging/monitoring.
        /// </summary>
        public static (int totalEntries, int inFlight, int completed) GetStats()
        {
            int total = 0;
            int inFlight = 0;
            int completed = 0;

            foreach (CopyEntry entry in _entries.Values)
            {
                total++;
                lock (entry.Lock)
                {
                    if (entry.InFlightTask != null && !entry.InFlightTask.IsCompleted)
                    {
                        inFlight++;
                    }
                    else if (entry.TargetPath != null)
                    {
                        completed++;
                    }
                }
            }

            return (total, inFlight, completed);
        }
    }
}
