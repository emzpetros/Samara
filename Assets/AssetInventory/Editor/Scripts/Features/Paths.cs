using ImpossibleRobert.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    /// <summary>
    /// Path-related operations including folder locations, relative paths, caching, and cache size calculations.
    /// </summary>
    public static class Paths
    {
        private const int FOLDER_CACHE_TIME = 20;
        private const string CONFIG_NAME = "AssetInventoryConfig.json";
    #if UNITY_EDITOR_WIN
        private const StringComparison PATH_COMPARISON = StringComparison.OrdinalIgnoreCase;
    #else
        private const StringComparison PATH_COMPARISON = StringComparison.Ordinal;
    #endif

        /// <summary>
        /// Gets whether a cache clearing operation is currently in progress.
        /// </summary>
        public static bool ClearCacheInProgress { get; private set; }

        // Cache fields - do not use timed cache for these as they are used in threads
        private static string _assetCacheFolder;
        private static string _configLocation;
        private static DateTime _lastAssetCacheCheck;
        private static readonly TimedCache<string> _materializeFolder = new TimedCache<string>();
        private static readonly TimedCache<string> _previewFolder = new TimedCache<string>();

        // Relative locations
        private static List<RelativeLocation> _relativeLocations;
        private static List<RelativeLocation> _userRelativeLocations;

        internal static List<RelativeLocation> RelativeLocations
        {
            get
            {
                if (_relativeLocations == null) LoadRelativeLocations();
                return _relativeLocations;
            }
        }

        internal static List<RelativeLocation> UserRelativeLocations
        {
            get
            {
                if (_userRelativeLocations == null) LoadRelativeLocations();
                return _userRelativeLocations;
            }
        }

        /// <summary>
        /// Clears all cached paths. Called during reinitialization.
        /// </summary>
        internal static void ClearCaches()
        {
            _assetCacheFolder = null;
            _configLocation = null;
            _materializeFolder.Clear();
            _previewFolder.Clear();
        }

        internal static void SetConfigLocation(string path)
        {
            _configLocation = path;
        }

        /// <summary>
        /// Gets the main storage folder for Asset Inventory data.
        /// </summary>
        public static string GetStorageFolder()
        {
            if (!string.IsNullOrEmpty(AI.Config.customStorageLocation)) return Path.GetFullPath(AI.Config.customStorageLocation);

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(appData)) appData = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return IOUtils.PathCombine(appData, "AssetInventory");
        }

        /// <summary>
        /// Gets the location of the configuration file.
        /// </summary>
        public static string GetConfigLocation()
        {
            if (_configLocation != null) return _configLocation;

            // search for local project-specific override first
            string configAssetPath = AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(CONFIG_NAME))
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(path => string.Equals(Path.GetFileName(path), CONFIG_NAME, StringComparison.OrdinalIgnoreCase));
            if (configAssetPath != null) return configAssetPath;

            // second fallback is environment variable
            string configPath = Environment.GetEnvironmentVariable("ASSETINVENTORY_CONFIG_PATH");
            if (!string.IsNullOrWhiteSpace(configPath))
            {
                // if user already specified json file use that one, otherwise use default name
                if (configPath.ToLowerInvariant().EndsWith(".json")) return configPath;
                return IOUtils.PathCombine(configPath, CONFIG_NAME);
            }

            // use from standard application data folder
            string newDefault = IOUtils.PathCombine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), CONFIG_NAME);
            if (File.Exists(newDefault))
            {
                _configLocation = newDefault;
                return _configLocation;
            }

            // one-time migration: check old location (MyDocuments) for existing config
            string oldDefault = IOUtils.PathCombine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), CONFIG_NAME);
            if (File.Exists(oldDefault))
            {
                _configLocation = oldDefault;
                return _configLocation;
            }

            // new installation
            _configLocation = newDefault;
            return _configLocation;
        }

        /// <summary>
        /// Gets the folder where preview images are stored.
        /// </summary>
        public static string GetPreviewFolder(string customFolder = null, bool noCache = false, bool createOnDemand = true)
        {
            if (!noCache && _previewFolder.TryGetValue(out string path)) return path;

            string previewPath = null;
            if (customFolder != null) previewPath = IOUtils.PathCombine(customFolder, "Previews");
            if (previewPath == null)
                previewPath = string.IsNullOrWhiteSpace(AI.Config.previewFolder)
                    ? IOUtils.PathCombine(GetStorageFolder(), "Previews")
                    : AI.Config.previewFolder;
            if (createOnDemand) Directory.CreateDirectory(previewPath);

            if (!noCache) _previewFolder.SetValue(previewPath, TimeSpan.FromSeconds(FOLDER_CACHE_TIME));

            return previewPath;
        }

        /// <summary>
        /// Clears the preview folder cache, forcing recalculation on next access.
        /// </summary>
        public static void RefreshPreviewCache()
        {
            _previewFolder.Clear();
        }

        /// <summary>
        /// Gets the folder where backups are stored.
        /// </summary>
        public static string GetBackupFolder(bool createOnDemand = true, string customFolder = null)
        {
            string backupPath;
            if (customFolder != null)
            {
                backupPath = IOUtils.PathCombine(customFolder, "Backups");
            }
            else
            {
                backupPath = string.IsNullOrWhiteSpace(AI.Config.backupFolder)
                    ? IOUtils.PathCombine(GetStorageFolder(), "Backups")
                    : AI.Config.backupFolder;
            }

            if (createOnDemand) Directory.CreateDirectory(backupPath);

            return backupPath;
        }

        /// <summary>
        /// Gets the folder where extracted/materialized assets are cached.
        /// </summary>
        public static string GetMaterializeFolder(string customFolder = null, bool noCache = false)
        {
            if (!noCache && _materializeFolder.TryGetValue(out string path)) return path;

            string cachePath = null;
            if (customFolder != null) cachePath = IOUtils.PathCombine(customFolder, "Extracted");
            if (cachePath == null)
            {
                cachePath = string.IsNullOrWhiteSpace(AI.Config.cacheFolder)
                    ? IOUtils.PathCombine(GetStorageFolder(), "Extracted")
                    : AI.Config.cacheFolder;
            }

            if (!noCache) _materializeFolder.SetValue(cachePath, TimeSpan.FromSeconds(FOLDER_CACHE_TIME));

            return cachePath;
        }

        /// <summary>
        /// Gets the path where a specific asset will be extracted/materialized.
        /// </summary>
        public static string GetMaterializedAssetPath(Asset asset)
        {
            // append the ID to support identically named packages in different locations
            // also append version if available to support different efficient caching without having to delete the whole folder all the time
            string version = asset.GetSafeVersion();
            return IOUtils.ToLongPath(IOUtils.PathCombine(GetMaterializeFolder(),
                asset.SafeName
                + AI.SEPARATOR
                + asset.Id
                + (!string.IsNullOrWhiteSpace(version) ? AI.SEPARATOR + version : "")));
        }

        /// <summary>
        /// Gets the Unity Asset Store cache folder location.
        /// </summary>
        public static string GetAssetCacheFolder()
        {
            if (_assetCacheFolder != null && (DateTime.Now - _lastAssetCacheCheck).TotalSeconds < FOLDER_CACHE_TIME) return _assetCacheFolder;

            string result;

            try
            {
                string autoDetected = GetAutomaticAssetCacheFolder();
                string customConfigured = NormalizePathForComparison(AI.Config.assetCacheLocationType == 1 ? AI.Config.assetCacheLocation : null);

                if (!string.IsNullOrWhiteSpace(customConfigured) && !AreEquivalentPaths(customConfigured, autoDetected))
                {
                    result = customConfigured;
                }
                else if (!string.IsNullOrWhiteSpace(autoDetected))
                {
                    result = autoDetected;
                }
                else
                {
#if UNITY_EDITOR_WIN
                    result = NormalizePathForComparison(IOUtils.PathCombine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Unity", AI.ASSET_STORE_FOLDER_NAME));
#endif
#if UNITY_EDITOR_OSX
                    result = NormalizePathForComparison(IOUtils.PathCombine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Unity", AI.ASSET_STORE_FOLDER_NAME));
#endif
#if UNITY_EDITOR_LINUX
                    result = NormalizePathForComparison(IOUtils.PathCombine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".local/share/unity3d", AI.ASSET_STORE_FOLDER_NAME));
#endif
                }

                bool pathChanged = _assetCacheFolder != null && !AreEquivalentPaths(_assetCacheFolder, result);
                _lastAssetCacheCheck = DateTime.Now;
                _assetCacheFolder = result;

                if (pathChanged && AI.IsInitialized)
                {
                    LoadRelativeLocations();
                }
            }
            catch (Exception)
            {
                return _assetCacheFolder;
            }
            return result;
        }

        private static string GetAutomaticAssetCacheFolder()
        {
            string unityReported = NormalizePathForComparison(AssetStore.GetAssetCacheFolder());
            if (!string.IsNullOrWhiteSpace(unityReported)) return unityReported;

            string envPath = NormalizePathForComparison(StringUtils.GetEnvVar("ASSETSTORE_CACHE_PATH"));
            if (!string.IsNullOrWhiteSpace(envPath)) return envPath;

            string customLocation = AI.Config.folders.FirstOrDefault(f => f.GetLocation(true).EndsWith(AI.ASSET_STORE_FOLDER_NAME))?.GetLocation(true);
            return NormalizePathForComparison(customLocation);
        }

        /// <summary>
        /// Gets the Unity Package Manager cache folder location.
        /// </summary>
        public static string GetPackageCacheFolder()
        {
            string result;
            if (AI.Config.packageCacheLocationType == 1 && !string.IsNullOrWhiteSpace(AI.Config.packageCacheLocation))
            {
                result = AI.Config.packageCacheLocation;
            }
            else
            {
#if UNITY_EDITOR_WIN
                result = IOUtils.PathCombine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unity", "cache", "packages");
#endif
#if UNITY_EDITOR_OSX
                result = IOUtils.PathCombine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Unity", "cache", "packages");
#endif
#if UNITY_EDITOR_LINUX
                result = IOUtils.PathCombine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".config/unity3d/cache/packages");
#endif
            }
            if (result != null) result = result.Replace("\\", "/");

            return result;
        }

        private static string NormalizePathForComparison(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            SplitStoredPath(path, out string normalized, out string suffix);

            bool isRooted = false;
            if (!IsRel(normalized))
            {
                try
                {
                    isRooted = Path.IsPathRooted(normalized);
                }
                catch (Exception)
                {
                    isRooted = false;
                }
            }

            if (!IsRel(normalized) && isRooted)
            {
                try
                {
                    normalized = Path.GetFullPath(normalized);
                }
                catch (Exception)
                {
                    // keep original path if it cannot be resolved yet
                }
            }

            try
            {
                normalized = IOUtils.ToShortPath(normalized)?.Replace("\\", "/") ?? normalized.Replace("\\", "/");
            }
            catch (Exception)
            {
                normalized = normalized.Replace("\\", "/");
            }

            string root = null;
            try
            {
                root = Path.GetPathRoot(normalized)?.Replace("\\", "/");
            }
            catch (Exception)
            {
                // ignore root detection failures for malformed or partial paths
            }

            if (string.IsNullOrEmpty(root) || normalized.Length > root.Length)
            {
                normalized = normalized.TrimEnd('/');
            }

            return normalized + suffix;
        }

        private static void SplitStoredPath(string path, out string filesystemPath, out string suffix)
        {
            int separatorIndex = path.IndexOf(Asset.SUB_PATH);
            if (separatorIndex < 0)
            {
                filesystemPath = path;
                suffix = string.Empty;
                return;
            }

            filesystemPath = path.Substring(0, separatorIndex);
            suffix = path.Substring(separatorIndex);
        }

        internal static bool AreEquivalentPaths(string left, string right)
        {
            string normalizedLeft = NormalizePathForComparison(left);
            string normalizedRight = NormalizePathForComparison(right);

            if (string.IsNullOrWhiteSpace(normalizedLeft) || string.IsNullOrWhiteSpace(normalizedRight)) return false;

            return string.Equals(normalizedLeft, normalizedRight, PATH_COMPARISON);
        }

        private static bool IsSameOrChildPath(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
            if (string.Equals(path, root, PATH_COMPARISON)) return true;
            if (path.Length <= root.Length) return false;
            if (!path.StartsWith(root, PATH_COMPARISON)) return false;

            return path[root.Length] == '/';
        }

        private static string TryMakeRelative(string normalizedPath, RelativeLocation location)
        {
            if (string.IsNullOrWhiteSpace(normalizedPath) || string.IsNullOrWhiteSpace(location?.Location)) return null;

            string normalizedRoot = NormalizePathForComparison(location.Location);
            if (!IsSameOrChildPath(normalizedPath, normalizedRoot)) return null;

            string suffix = normalizedPath.Length == normalizedRoot.Length ? string.Empty : normalizedPath.Substring(normalizedRoot.Length);
            return $"{AI.TAG_START}{location.Key}{AI.TAG_END}{suffix}";
        }

        #region Cache Size Methods

        /// <summary>
        /// Gets the total size of the extracted/materialized cache folder.
        /// </summary>
        public static async Task<long> GetCacheFolderSize()
        {
            return await IOUtils.GetFolderSize(GetMaterializeFolder());
        }

        /// <summary>
        /// Gets the total size of persisted (kept extracted) cache entries.
        /// </summary>
        public static async Task<long> GetPersistedCacheSize()
        {
            if (!Directory.Exists(GetMaterializeFolder())) return 0;

            long result = 0;

            List<Asset> keepAssets = DBAdapter.DB.Table<Asset>().Where(a => a.KeepExtracted).ToList();
            List<string> keepPaths = keepAssets.Select(a => IOUtils.ToShortPath(GetMaterializedAssetPath(a)).ToLowerInvariant()).ToList();
            string[] packages = Directory.GetDirectories(GetMaterializeFolder());
            foreach (string package in packages)
            {
                if (!keepPaths.Contains(IOUtils.ToShortPath(package).ToLowerInvariant())) continue;
                result += await IOUtils.GetFolderSize(package);
            }

            return result;
        }

        /// <summary>
        /// Gets the total size of the backup folder.
        /// </summary>
        public static async Task<long> GetBackupFolderSize()
        {
            return await IOUtils.GetFolderSize(GetBackupFolder());
        }

        /// <summary>
        /// Gets the total size of the preview images folder.
        /// </summary>
        public static async Task<long> GetPreviewFolderSize()
        {
            return await IOUtils.GetFolderSize(GetPreviewFolder());
        }

        /// <summary>
        /// Clears the cache folder, preserving packages marked as "keep extracted".
        /// </summary>
        /// <param name="callback">Optional callback to invoke when clearing is complete</param>
        public static async void ClearCache(Action callback = null)
        {
            ClearCacheInProgress = true;
            try
            {
                string cachePath = GetMaterializeFolder();
                if (Directory.Exists(cachePath))
                {
                    List<Asset> keepAssets = DBAdapter.DB.Table<Asset>().Where(a => a.KeepExtracted).ToList();
                    List<string> keepPaths = keepAssets.Select(a => GetMaterializedAssetPath(a).ToLowerInvariant()).ToList();

                    // go through 1 by 1 to keep persisted packages in the cache
                    string[] packages = Directory.GetDirectories(cachePath);
                    foreach (string package in packages)
                    {
                        if (keepPaths.Contains(package.ToLowerInvariant())) continue;
                        await IOUtils.DeleteFileOrDirectory(package);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Could not delete full cache directory: {e.Message}");
            }

            ClearCacheInProgress = false;
            callback?.Invoke();
        }

        #endregion

        #region Relative Path Methods

        /// <summary>
        /// Loads relative location mappings from the database.
        /// </summary>
        internal static void LoadRelativeLocations()
        {
            if (!AI.IsInitialized) return;

            string curSystem = GetSystemId();

            string dataQuery = "SELECT * from RelativeLocation order by `Key`, Location";
            List<RelativeLocation> locations = DBAdapter.DB.Query<RelativeLocation>($"{dataQuery}").ToList();
            locations.ForEach(l => l.SetLocation(l.Location)); // ensure all paths use forward slashes

            // ensure additional folders don't contain additional unmapped keys (e.g. after database cleanup)
            foreach (FolderSpec spec in AI.Config.folders)
            {
                if (!string.IsNullOrWhiteSpace(spec.relativeKey) && !locations.Any(rl => rl.Key == spec.relativeKey))
                {
                    // self-heal
                    RelativeLocation rel = new RelativeLocation();
                    rel.System = curSystem;
                    rel.Key = spec.relativeKey;
                    DBAdapter.DB.Insert(rel);
                    locations.Add(rel);
                }
            }

            _relativeLocations = locations.Where(l => l.System == curSystem).ToList();

            // add predefined locations
            _relativeLocations.Insert(0, new RelativeLocation("ac", curSystem, GetAssetCacheFolder()));
            _relativeLocations.Insert(1, new RelativeLocation("pc", curSystem, GetPackageCacheFolder()));

            foreach (RelativeLocation location in locations.Where(l => l.System != curSystem))
            {
                // add key as undefined if not there
                if (!_relativeLocations.Any(rl => rl.Key == location.Key))
                {
                    _relativeLocations.Add(new RelativeLocation(location.Key, curSystem, null));
                }

                // add location inside other systems for reference
                RelativeLocation loc = _relativeLocations.First(rl => rl.Key == location.Key);
                if (loc.otherLocations == null) loc.otherLocations = new List<string>();
                loc.otherLocations.Add(location.Location);
            }

            // ensure never null
            _relativeLocations.ForEach(rl =>
            {
                if (rl.otherLocations == null) rl.otherLocations = new List<string>();
            });

            _userRelativeLocations = _relativeLocations.Where(rl => rl.Key != "ac" && rl.Key != "pc").ToList();
        }

        /// <summary>
        /// Checks if a path uses relative location tags.
        /// </summary>
        internal static bool IsRel(string path)
        {
            return path != null && path.StartsWith(AI.TAG_START);
        }

        /// <summary>
        /// Extracts the relative location key from a tagged path.
        /// </summary>
        internal static string GetRelKey(string path)
        {
            return path.Replace(AI.TAG_START, "").Replace(AI.TAG_END, "");
        }

        /// <summary>
        /// Converts a relative path (with tags) to an absolute path.
        /// </summary>
        internal static string DeRel(string path, bool emptyIfMissing = false)
        {
            if (path == null) return null;
            if (!IsRel(path)) return path;

            // If RelativeLocations not loaded yet, return path unchanged to avoid NullReferenceException
            // This can happen when DeRel is called during/after recompilation before initialization completes
            if (RelativeLocations == null) return path;

            foreach (RelativeLocation location in RelativeLocations)
            {
                string segment = $"{AI.TAG_START}{location.Key}{AI.TAG_END}";

                if (string.IsNullOrWhiteSpace(location.Location))
                {
                    if (emptyIfMissing && path.Contains(segment))
                    {
                        return null;
                    }
                    continue;
                }

                path = path.Replace(segment, location.Location.TrimEnd('/'));
            }

            // check if some rule caught it
            if (IsRel(path) && emptyIfMissing) return null;

            return path;
        }

        /// <summary>
        /// Converts an absolute path to a relative path with location tags.
        /// </summary>
        internal static string MakeRelative(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            if (IsRel(path)) return path;

            path = NormalizePathForComparison(path);

            // During startup/recompilation the relative location table may not be initialized yet.
            if (RelativeLocations == null) return path;

            foreach (RelativeLocation location in RelativeLocations.OrderByDescending(loc => NormalizePathForComparison(loc.Location)?.Length ?? 0))
            {
                string relativePath = TryMakeRelative(path, location);
                if (relativePath != null) return relativePath;
            }

            return path;
        }

        /// <summary>
        /// Gets the unique system identifier for this machine.
        /// </summary>
        internal static string GetSystemId()
        {
            return SystemInfo.deviceUniqueIdentifier;
        }

        #endregion
    }
}
