using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using static AssetInventory.AssetInfo;

namespace AssetInventory
{
    /// <summary>
    /// Cache for dependency calculation results to avoid redundant traversal of the same dependency trees.
    /// When File A and File B both depend on Material C, Material C's dependencies are only calculated once.
    /// </summary>
    public sealed class DependencyResultCache
    {
        /// <summary>
        /// Memoization cache: File GUID -> complete dependency calculation result
        /// Thread-safe for potential parallel processing
        /// </summary>
        private readonly ConcurrentDictionary<string, CachedDependencyResult> _guidToDepCache;

        /// <summary>
        /// Pre-fetched AssetFile lookups per package (AssetId -> GUID -> AssetFile)
        /// Eliminates repeated database queries during dependency traversal
        /// </summary>
        private readonly Dictionary<int, Dictionary<string, AssetFile>> _packageFileCache;

        /// <summary>
        /// Cache for materialized asset paths to avoid re-materialization
        /// </summary>
        private readonly ConcurrentDictionary<string, string> _materializedPathCache;

        /// <summary>
        /// Generates a consistent cache key for materialized path storage.
        /// Format: "{assetId}_{guid}" when GUID is available,
        /// or "path:{assetId}_{path}" as fallback for files without GUIDs.
        /// </summary>
        public static string GetMaterializedPathKey(int assetId, string guid, string path = null)
        {
            if (!string.IsNullOrEmpty(guid)) return $"{assetId}_{guid}";
            if (!string.IsNullOrEmpty(path)) return $"path:{assetId}_{path}";
            return $"{assetId}_";
        }

        /// <summary>
        /// Generates a consistent cache key for materialized path storage from AssetInfo.
        /// Uses GUID when available, falls back to Path for files without GUIDs.
        /// </summary>
        public static string GetMaterializedPathKey(AssetInfo info)
        {
            return GetMaterializedPathKey(info.AssetId, info.Guid, info.Path);
        }

        /// <summary>
        /// Generates a consistent cache key for materialized path storage from AssetFile.
        /// Uses GUID when available, falls back to Path for files without GUIDs.
        /// </summary>
        public static string GetMaterializedPathKey(int assetId, AssetFile file)
        {
            return GetMaterializedPathKey(assetId, file?.Guid, file?.Path);
        }

        public DependencyResultCache()
        {
            _guidToDepCache = new ConcurrentDictionary<string, CachedDependencyResult>();
            _packageFileCache = new Dictionary<int, Dictionary<string, AssetFile>>();
            _materializedPathCache = new ConcurrentDictionary<string, string>();
        }

        /// <summary>
        /// Result of a cached dependency calculation for a single file
        /// </summary>
        public sealed class CachedDependencyResult
        {
            public List<AssetFile> Dependencies { get; set; }
            public DependencyStateOptions State { get; set; }
            public bool SRPUsed { get; set; }
            public List<Asset> CrossPackageDependencies { get; set; }

            public CachedDependencyResult()
            {
                Dependencies = new List<AssetFile>();
                CrossPackageDependencies = new List<Asset>();
            }

            /// <summary>
            /// Create a deep copy to prevent cache corruption from external modifications
            /// </summary>
            public CachedDependencyResult Clone()
            {
                return new CachedDependencyResult
                {
                    Dependencies = new List<AssetFile>(Dependencies),
                    State = State,
                    SRPUsed = SRPUsed,
                    CrossPackageDependencies = new List<Asset>(CrossPackageDependencies)
                };
            }
        }

        /// <summary>
        /// Try to get cached dependency result for a file.
        /// Key is scoped by AssetId to prevent cross-package cache contamination
        /// when files share the same GUID across different packages.
        /// </summary>
        public bool TryGetDependencies(int assetId, string guid, out CachedDependencyResult result)
        {
            if (string.IsNullOrEmpty(guid))
            {
                result = null;
                return false;
            }

            string key = $"{assetId}_{guid}";
            if (_guidToDepCache.TryGetValue(key, out CachedDependencyResult cached))
            {
                // Return a clone to prevent external modifications from corrupting cache
                result = cached.Clone();
                return true;
            }

            result = null;
            return false;
        }

        /// <summary>
        /// Store dependency calculation result for a file.
        /// Key is scoped by AssetId to prevent cross-package cache contamination
        /// when files share the same GUID across different packages.
        /// </summary>
        public void StoreDependencies(int assetId, string guid, CachedDependencyResult result)
        {
            if (string.IsNullOrEmpty(guid) || result == null) return;

            string key = $"{assetId}_{guid}";
            // Store a clone to prevent external modifications from corrupting cache
            _guidToDepCache[key] = result.Clone();
        }

        /// <summary>
        /// Pre-load all AssetFile records for a package to eliminate repeated DB queries
        /// </summary>
        public void PreloadPackageFiles(int assetId, List<AssetFile> files)
        {
            if (_packageFileCache.ContainsKey(assetId)) return;

            Dictionary<string, AssetFile> guidLookup = files
                .Where(f => !string.IsNullOrEmpty(f.Guid))
                .GroupBy(f => f.Guid)
                .ToDictionary(g => g.Key, g => g.First());

            _packageFileCache[assetId] = guidLookup;
        }

        /// <summary>
        /// Try to get AssetFile by GUID from pre-loaded package cache
        /// Returns null if not found or package not pre-loaded
        /// </summary>
        public AssetFile TryGetAssetFile(int assetId, string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            
            if (_packageFileCache.TryGetValue(assetId, out Dictionary<string, AssetFile> guidLookup))
            {
                guidLookup.TryGetValue(guid, out AssetFile result);
                return result;
            }

            return null;
        }

        /// <summary>
        /// Check if package files have been pre-loaded
        /// </summary>
        public bool HasPackageCache(int assetId)
        {
            return _packageFileCache.ContainsKey(assetId);
        }

        /// <summary>
        /// Try to get cached materialized path
        /// </summary>
        public bool TryGetMaterializedPath(string key, out string path)
        {
            return _materializedPathCache.TryGetValue(key, out path);
        }

        /// <summary>
        /// Store materialized asset path
        /// </summary>
        public void StoreMaterializedPath(string key, string path)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(path)) return;
            _materializedPathCache[key] = path;
        }

        /// <summary>
        /// Remove a cached materialized path entry, e.g. when validation discovers
        /// the import at that path is broken (missing nested prefab references).
        /// </summary>
        public void InvalidateMaterializedPath(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            _materializedPathCache.TryRemove(key, out _);
        }

        /// <summary>
        /// Get cache statistics for debugging/logging
        /// </summary>
        public CacheStatistics GetStatistics()
        {
            return new CacheStatistics
            {
                DependencyCacheSize = _guidToDepCache.Count,
                PackageCacheCount = _packageFileCache.Count,
                MaterializedPathCount = _materializedPathCache.Count,
                TotalAssetFilesLoaded = _packageFileCache.Values.Sum(dict => dict.Count)
            };
        }

        /// <summary>
        /// Clear all caches
        /// </summary>
        public void Clear()
        {
            _guidToDepCache.Clear();
            _packageFileCache.Clear();
            _materializedPathCache.Clear();
        }

        public sealed class CacheStatistics
        {
            public int DependencyCacheSize { get; set; }
            public int PackageCacheCount { get; set; }
            public int MaterializedPathCount { get; set; }
            public int TotalAssetFilesLoaded { get; set; }

            public override string ToString()
            {
                return $"DependencyCache: {DependencyCacheSize} entries, " +
                       $"PackageCache: {PackageCacheCount} packages ({TotalAssetFilesLoaded} files), " +
                       $"MaterializedPaths: {MaterializedPathCount} entries";
            }
        }
    }
}

