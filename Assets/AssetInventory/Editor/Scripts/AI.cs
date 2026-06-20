using ImpossibleRobert.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;
using Brain;
using ErrorEventArgs = Newtonsoft.Json.Serialization.ErrorEventArgs;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace AssetInventory
{
    public static class AI
    {
        public const string VERSION = "4.5.1";
        public const string DEFINE_SYMBOL = "ASSET_INVENTORY";

        public const string DEFINE_SYMBOL_HIDE_AI = DEFINE_SYMBOL + "_HIDE_AI";
        public const string DEFINE_SYMBOL_HIDE_BROWSER = DEFINE_SYMBOL + "_HIDE_BROWSER";
        public const string DEFINE_SYMBOL_HIDE_TOOLS_MENU = DEFINE_SYMBOL + "_HIDE_TOOLS_MENU";
        public const string DEFINE_SYMBOL_HIDE_PROJECT_TOOLBAR = DEFINE_SYMBOL + "_HIDE_PROJECT_TOOLBAR";
        private static string AutoInitAttemptedKey => DEFINE_SYMBOL + "_AUTO_INIT_ATTEMPTED_" + Application.dataPath;

        internal const string HOME_LINK = "https://www.wetzold.com/tool";
        internal const string DISCORD_LINK = "https://discord.com/invite/uzeHzEMM4B";
        internal const string ASSET_STORE_FOLDER_NAME = "Asset Store-5.x";
        internal const string TEMP_FOLDER = "_AssetInventoryTemp";
        internal const int ASSET_STORE_ID = 349582;
        internal const string SEPARATOR = "-~-";
        internal const string TAG_START = "[";
        internal const string TAG_END = "]";
        internal static readonly bool DEBUG_MODE = false;
        internal const string AFFILIATE_ID = "1100l3Bzsf";
        internal const string AFFILIATE_PARAM = "aid=" + AFFILIATE_ID;
        internal const string ASSET_STORE_LINK = "https://u3d.as/3L7L?" + AFFILIATE_PARAM;
        internal const string CLOUD_HOME_URL = "https://cloud.unity.com/home/organizations";
        internal const string TUTORIALS_VERSION = "5.0.2";

        private const double CACHE_LIMIT_INTERVAL = 10; // to ensure it is only run every X min

        public enum AssetGroup
        {
            Audio = 0,
            Images = 1,
            Videos = 2,
            Prefabs = 3,
            Materials = 4,
            Shaders = 5,
            Effects = 12,
            Models = 6,
            Animations = 7,
            Fonts = 8,
            Scripts = 9,
            Libraries = 10,
            Documents = 11,
            Scenes = 13
        }

        internal static string UsedConfigLocation { get; private set; }

        public static event Action OnPackagesUpdated;
        public static event Action<Asset> OnPackageImageLoaded;
        public static event Action OnDatabaseSwitched;

        public enum InitializationState
        {
            NotInitialized,
            InitializationFailed,
            Initialized
        }

        public static InitializationState InitState { get; private set; } = InitializationState.NotInitialized;
        public static bool IsInitialized => InitState == InitializationState.Initialized;
        private static UpdateObserver _observer;

        internal static List<RelativeLocation> RelativeLocations => Paths.RelativeLocations;

        internal static List<RelativeLocation> UserRelativeLocations => Paths.UserRelativeLocations;

        public static ActionHandler Actions
        {
            get
            {
                if (_actions == null) _actions = new ActionHandler();
                return _actions;
            }
        }
        private static ActionHandler _actions;

        public static UpgradeUtil UpgradeUtil
        {
            get
            {
                if (_upgradeUtil == null) _upgradeUtil = new UpgradeUtil();
                return _upgradeUtil;
            }
        }
        private static UpgradeUtil _upgradeUtil;

        public static Cooldown Cooldown
        {
            get
            {
                if (_cooldown == null)
                {
                    _cooldown = new Cooldown(Config.cooldownInterval, Config.cooldownDuration);
                    _cooldown.Enabled = Config.useCooldown;
                }
                return _cooldown;
            }
        }
        private static Cooldown _cooldown;

        public static MemoryObserver MemoryObserver
        {
            get
            {
                if (_memoryObserver == null)
                {
                    _memoryObserver = new MemoryObserver(Config.memoryLimit);
                    _memoryObserver.Enabled = true;
                }
                return _memoryObserver;
            }
        }
        private static MemoryObserver _memoryObserver;

        public static DirectorySizeManager CacheLimiter
        {
            get
            {
                if (_cacheLimiter == null)
                {
                    _cacheLimiter = new DirectorySizeManager(Paths.GetMaterializeFolder(), Config.cacheLimit, pathToDelete =>
                    {
                        // folder will contain asset Id and optional version, e.g. "MyAsset-~-12345-~-1.0.0"
                        string[] segments = pathToDelete.Split(SEPARATOR);
                        if (segments.Length < 2) return true; // not a valid path, can be removed
                        if (!int.TryParse(segments[1].Trim(), out int assetId)) return true; // not a valid asset Id, can be removed
                        string version = segments.Length > 2 ? segments.Last() : null;

                        Asset asset = DBAdapter.DB.Find<Asset>(assetId);
                        if (asset != null)
                        {
                            // if version is different, this cache entry can be cleaned up
                            if (version != null && asset.GetSafeVersion() != version) return true;

                            if (asset.KeepExtracted ||
                                asset.CurrentState == Asset.State.InProcess ||
                                asset.CurrentState == Asset.State.SubInProcess)
                            {
                                return false;
                            }
                        }

                        return true;
                    });
                    _cacheLimiter.Enabled = Config.limitCacheSize;
                }
                return _cacheLimiter;
            }
        }
        private static DirectorySizeManager _cacheLimiter;

        public static AssetInventorySettings Config
        {
            get
            {
                if (_config == null) LoadConfig();
                return _config;
            }
        }
        private static AssetInventorySettings _config;
        internal static AssetInventorySettings ConfigOverride
        {
            get => _config;
            set
            {
                _config = value;
                _cooldown = null;
            }
        }
        internal static readonly List<string> ConfigErrors = new List<string>();
        internal static bool UICustomizationMode { get; set; }

        public static bool ClearCacheInProgress => Paths.ClearCacheInProgress;

        public static Dictionary<AssetGroup, string[]> TypeGroups { get; } = new Dictionary<AssetGroup, string[]>
        {
            {AssetGroup.Audio, new[] {"wav", "mp3", "ogg", "aiff", "aif", "mod", "it", "s3m", "xm", "flac"}},
            {
                AssetGroup.Images,
                new[]
                {
                    "png", "jpg", "jpeg", "bmp", "tga", "tif", "tiff", "psd", "svg", "webp", "ico", "gif", "hdr", "iff", "pict"
                }
            },
            {AssetGroup.Videos, new[] {"avi", "asf", "dv", "m4v", "mov", "mp4", "mpg", "mpeg", "ogv", "vp8", "webm", "wmv"}},
            {AssetGroup.Prefabs, new[] {"prefab"}},
            {AssetGroup.Materials, new[] {"mat", "physicmaterial", "physicsmaterial", "sbs", "sbsar", "cubemap"}},
            {AssetGroup.Shaders, new[] {"shader", "shadergraph", "shadersubgraph", "compute", "raytrace"}},
            {AssetGroup.Models, new[] {"fbx", "obj", "blend", "dae", "3ds", "dxf", "max", "c4d", "mb", "ma"}},
            {AssetGroup.Effects, new[] {"vfx"}},
            {AssetGroup.Animations, new[] {"anim"}},
            {AssetGroup.Fonts, new[] {"ttf", "otf"}},
            {AssetGroup.Scripts, new[] {"cs", "php", "py", "js", "lua"}},
            {AssetGroup.Libraries, new[] {"zip", "rar", "7z", "unitypackage", "so", "bundle", "dll", "jar"}},
            {AssetGroup.Documents, new[] {"md", "doc", "docx", "txt", "json", "rtf", "pdf", "htm", "html", "readme", "xml", "chm", "csv"}},
            {AssetGroup.Scenes, new[] {"unity"}}
        };

        internal static Dictionary<string, string[]> FilterRestriction { get; } = new Dictionary<string, string[]>
        {
            {"Length", new[] {"Audio", "Videos"}},
            {"Width", new[] {"Images", "Videos"}},
            {"Height", new[] {"Images", "Videos"}},
            {"ImageType", new[] {"Images"}},
            {"VertexCount", new[] {"Models"}}
        };

        [DidReloadScripts(1)]
        private static void AutoInit()
        {
            // Unsubscribe first to prevent accumulation on domain reload
            EditorApplication.update -= UpdateLoop;
            EditorApplication.update += UpdateLoop;

            EditorApplication.delayCall -= InitAfterDomainReload;
            EditorApplication.delayCall += InitAfterDomainReload;
        }

        private static void UpdateLoop()
        {
            if (!IsInitialized) return;
            Assets.ProcessExtractionQueue();
        }

        internal static void ReInit()
        {
            InitState = InitializationState.NotInitialized;
            LoadConfig();
            Init();
        }

        public static void Init(bool secondTry = false, bool force = false)
        {
            if (IsInitialized && !force) return;

            InitState = InitializationState.NotInitialized;
            ThreadUtils.Initialize();
            SetupDefines();

            Paths.ClearCaches();

            string folder = Paths.GetStorageFolder();
            if (!Directory.Exists(folder))
            {
                try
                {
                    Directory.CreateDirectory(folder);
                }
                catch (Exception e)
                {
                    if (secondTry)
                    {
                        Debug.LogError($"Could not create storage folder for database in default location '{folder}' as well. Giving up: {e.Message}");
                    }
                    else
                    {
                        Debug.LogError($"Could not create storage folder '{folder}' for database. Reverting to default location: {e.Message}");
                        Config.customStorageLocation = null;
                        SaveConfig();
                        Init(true);
                        return;
                    }
                }
            }
            UnityPreviewGenerator.CleanUp();
            DependencyAnalysis.CleanUp();

            Paths.GetAssetCacheFolder(); // cache into main thread since GetConfig is not available from threads

            // Check if database initialization failed
            if (DBAdapter.DB == null || !string.IsNullOrEmpty(DBAdapter.DBError))
            {
                InitState = InitializationState.InitializationFailed;
                return;
            }

            DBAdapter.BackupDatabase();
            UpdateSystemData();
            Paths.LoadRelativeLocations();

            UpgradeUtil.PerformUpgrades();

            Tagging.LoadAssignments(null, false);
            Metadata.LoadAssignments(null, false);
            AssetStore.FillBufferOnDemand(true);
            Actions.Init(force);

            InitState = InitializationState.Initialized;

            ShowWelcomeIfNeeded();
        }

        internal static void SwitchDatabase(string targetFolder)
        {
            DBAdapter.Close();
            ClearAllCaches();
            Config.customStorageLocation = targetFolder;
            SaveConfig();

            Init(false, true);

            TriggerPackageRefresh();
            OnDatabaseSwitched?.Invoke();
        }

        /// <summary>
        /// Clears all static caches and state that may contain database-specific data.
        /// Call this when switching database backends to ensure no stale data persists.
        /// </summary>
        internal static void ClearAllCaches()
        {
            AssetUtils.ClearCache();
            AssetBackup.ClearCache();
            Assets.ClearExtractionState();
            UnityIconOverlay.ClearCache();
            CopyCoordinator.Reset();
        }

        internal static void StartCacheObserver()
        {
            GetObserver().Start();
        }

        internal static void StopCacheObserver()
        {
            GetObserver().Stop();
        }

        internal static bool IsObserverActive()
        {
            return GetObserver().IsActive();
        }

        internal static UpdateObserver GetObserver()
        {
            if (_observer == null) _observer = new UpdateObserver(Paths.GetAssetCacheFolder(), new[] {"unitypackage", "tmp"});
            return _observer;
        }

        internal static void RunCacheLimiter()
        {
            if (!CacheLimiter.Enabled || CacheLimiter.IsRunning) return;
            if ((DateTime.Now - CacheLimiter.LastCheckTime).TotalMinutes < CACHE_LIMIT_INTERVAL) return;

            _ = CacheLimiter.CheckAndClean();
        }

        private static void SetupDefines()
        {
            if (!EditorUtils.HasDefine(DEFINE_SYMBOL)) EditorUtils.AddDefine(DEFINE_SYMBOL);
        }

        private static void UpdateSystemData()
        {
            if (!IsInitialized) return;

            SystemData data = new SystemData();
            data.Key = SystemInfo.deviceUniqueIdentifier;
            data.Name = SystemInfo.deviceName;
            data.Type = SystemInfo.deviceType.ToString();
            data.Model = SystemInfo.deviceModel;
            data.OS = SystemInfo.operatingSystem;
            data.LastUsed = DateTime.Now;

            try
            {
                DBAdapter.DB.InsertOrReplace(data);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Could not update system data: {e.Message}");
            }
        }

        internal static bool IsFileType(string path, AssetGroup typeGroup, IEnumerable<string> ignoreExtensions = null)
        {
            if (path == null) return false;

            string ext = IOUtils.GetExtensionWithoutDot(path).ToLowerInvariant();
            if (ignoreExtensions != null && ignoreExtensions.Contains(ext)) return false;

            return TypeGroups[typeGroup].Contains(ext);
        }

        /// <summary>
        /// Resolves an extension list string that may contain type group placeholders.
        /// Tokens like {audio} or {images} are expanded to all extensions registered
        /// in the corresponding AI.TypeGroups entry. Plain tokens are treated as literal
        /// extensions. Returns a deduplicated, lowercased array.
        /// </summary>
        public static string[] ResolveExtensionList(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return Array.Empty<string>();

            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] tokens = input.Split(new[] {';', ','}, StringSplitOptions.RemoveEmptyEntries);

            foreach (string raw in tokens)
            {
                string token = raw.Trim();
                if (token.Length > 2 && token[0] == '{' && token[token.Length - 1] == '}')
                {
                    string groupName = token.Substring(1, token.Length - 2);
                    if (Enum.TryParse(groupName, true, out AssetGroup group) && TypeGroups.TryGetValue(group, out string[] extensions))
                    {
                        foreach (string ext in extensions)
                        {
                            result.Add(ext.ToLowerInvariant());
                        }
                    }
                }
                else if (token.Length > 0)
                {
                    result.Add(token.ToLowerInvariant());
                }
            }

            return result.ToArray();
        }

        public static string GetImportFolder()
        {
            string importFolder = AssetUtils.GetAssetDatabasePath(Config.importFolder, false);
            return !string.IsNullOrEmpty(importFolder) ? importFolder : "Assets/ThirdParty";
        }

        public static async Task CalculateDependencies(AssetInfo info, CancellationToken ct = default(CancellationToken), DependencyResultCache cache = null)
        {
            DependencyAnalysis da = new DependencyAnalysis(cache);
            await da.Analyze(info, ct);
        }

        private static void LoadConfig()
        {
            string configLocation = Paths.GetConfigLocation();
            UsedConfigLocation = configLocation;

            if (configLocation == null || !File.Exists(configLocation))
            {
                _config = new AssetInventorySettings();
                return;
            }

            ConfigErrors.Clear();
            _config = JsonConvert.DeserializeObject<AssetInventorySettings>(File.ReadAllText(configLocation), new JsonSerializerSettings
            {
                Error = delegate(object _, ErrorEventArgs args)
                {
                    ConfigErrors.Add(args.ErrorContext.Error.Message);

                    Debug.LogError("Invalid config file format: " + args.ErrorContext.Error.Message);
                    args.ErrorContext.Handled = true;
                }
            });
            if (_config == null) _config = new AssetInventorySettings();

            // one-time migration: move config and storage defaults from MyDocuments to LocalApplicationData
            string oldConfigDefault = IOUtils.PathCombine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AssetInventoryConfig.json");
            string newConfigDefault = IOUtils.PathCombine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetInventoryConfig.json");
            bool migrateConfigPath = !string.IsNullOrEmpty(UsedConfigLocation)
                && string.Equals(UsedConfigLocation, oldConfigDefault, StringComparison.OrdinalIgnoreCase)
                && !File.Exists(newConfigDefault);
            bool needsSave = migrateConfigPath;

            if (string.IsNullOrEmpty(_config.customStorageLocation))
            {
                string oldStorage = IOUtils.PathCombine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AssetInventory");
                string newStorage = IOUtils.PathCombine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetInventory");
                if (!IOUtils.IsSameDirectory(oldStorage, newStorage) && Directory.Exists(oldStorage))
                {
                    _config.customStorageLocation = oldStorage;
                    needsSave = true;
                    Debug.Log($"Asset Inventory: Storage location preserved at existing path: {oldStorage}");
                }
            }

            if (needsSave)
            {
                if (migrateConfigPath)
                {
                    string dir = Path.GetDirectoryName(newConfigDefault);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    Paths.SetConfigLocation(newConfigDefault);
                    UsedConfigLocation = newConfigDefault;
                }
                SaveConfig();
            }

            _config.InitUISections();

            // init folders & ensure all paths are in the correct format
            if (_config.folders == null) _config.folders = new List<FolderSpec>();
            _config.folders.ForEach(f => f.location = f.location?.Replace("\\", "/"));

            _config.importFolder = GetImportFolder();

            // init actions
            if (_config.actionStates == null) _config.actionStates = new List<UpdateActionStates>();

            // templates
            if (_config.csvExportSettings == null) _config.csvExportSettings = new CSVExportSettings();
            _config.csvExportSettings.EnsureDefaults();
            if (_config.templateExportSettings.environments == null) _config.templateExportSettings.environments = new List<TemplateExportEnvironment>();
            if (_config.templateExportSettings.environments.Count == 0) _config.templateExportSettings.environments.Add(new TemplateExportEnvironment());

            // Initialize Brain settings with Asset Inventory configuration
            Intelligence.Settings = new BrainSettingsAdapter(_config);

            // Sync image logging settings to Common
            ImageUtils.LogImageOperations = _config.LogImageExtraction;
        }

        private static void InitForWelcomeIfNeeded()
        {
            if (Application.isBatchMode || (Config.welcomeShown && ActionHandler.IsProjectSetupDone)) return;

            if (IsInitialized)
            {
                ShowWelcomeIfNeeded();
                return;
            }

            if (SessionState.GetBool(AutoInitAttemptedKey, false)) return;

            SessionState.SetBool(AutoInitAttemptedKey, true);
            Init();
        }

        private static void InitAfterDomainReload()
        {
            if (Config.forceInitOnDomainReload)
            {
                Init();
                return;
            }

            InitForWelcomeIfNeeded();
        }

        private static void ShowWelcomeIfNeeded()
        {
            if (Application.isBatchMode || Config.welcomeShown) return;

            Config.welcomeShown = true;
            SaveConfig();
            WelcomeWindow.ShowWindow();
        }

        public static void SaveConfig()
        {
            if (DEBUG_MODE) Debug.LogWarning("SaveConfig");

            string configFile = Paths.GetConfigLocation();
            if (configFile == null) return;

            if (_config.reportingBatchSize > 500) _config.reportingBatchSize = 500; // SQLite cannot handle more than that

            try
            {
                // Update Brain settings to always reflect latest changes 
                Intelligence.Settings = new BrainSettingsAdapter(_config);

                _config.importFolder = GetImportFolder();

                File.WriteAllText(configFile, JsonConvert.SerializeObject(_config, Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogError($"Could not persist configuration. It might be locked by another application: {e.Message}");
            }
        }

        public static void ResetConfig()
        {
            DBAdapter.Close(); // in case DB path changes

            _config = new AssetInventorySettings();
            SaveConfig();
            AssetDatabase.Refresh();
        }

        public static void ResetUICustomization()
        {
            _config.ResetAdvancedUI();
            SaveConfig();
        }

        internal static void SetAssetExclusion(AssetInfo info, bool exclude)
        {
            Asset asset = DBAdapter.DB.Find<Asset>(info.AssetId);
            if (asset == null) return;

            asset.Exclude = exclude;
            info.Exclude = exclude;

            DBAdapter.DB.Update(asset);
        }

        internal static void SetAssetBackup(AssetInfo info, bool backup, bool invokeUpdate = true)
        {
            Asset asset = DBAdapter.DB.Find<Asset>(info.AssetId);
            if (asset == null) return;

            asset.Backup = backup;
            info.Backup = backup;

            DBAdapter.DB.Update(asset);

            if (invokeUpdate) OnPackagesUpdated?.Invoke();
        }

        internal static void SetAssetAIUse(AssetInfo info, bool useAI, bool invokeUpdate = true)
        {
            Asset asset = DBAdapter.DB.Find<Asset>(info.AssetId);
            if (asset == null) return;

            asset.UseAI = useAI;
            info.UseAI = useAI;

            DBAdapter.DB.Update(asset);

            if (invokeUpdate) OnPackagesUpdated?.Invoke();
        }

        // Cached ShowAdvanced result for performance (evaluated once per frame)
        private static bool _showAdvancedCached;
        private static bool _showAdvancedCacheValid;

        /// <summary>
        /// Resets the ShowAdvanced cache. Call at the start of each OnGUI cycle.
        /// </summary>
        internal static void ResetShowAdvancedCache()
        {
            _showAdvancedCacheValid = false;
        }

        internal static bool ShowAdvanced()
        {
            if (!_showAdvancedCacheValid)
            {
                _showAdvancedCached = !Config.hideAdvanced || Event.current.control;
                _showAdvancedCacheValid = true;
            }
            return _showAdvancedCached;
        }

        internal static void SetVersion(AssetInfo info, string version)
        {
            Asset asset = DBAdapter.DB.Find<Asset>(info.AssetId);
            if (asset == null) return;

            asset.Version = version;
            info.Version = version;

            DBAdapter.DB.Update(asset);
        }

        internal static void SetAICaption(AssetInfo info, string caption)
        {
            AssetFile af = DBAdapter.DB.Find<AssetFile>(info.Id);
            if (af == null) return;

            af.AICaption = caption;
            info.AICaption = caption;

            DBAdapter.DB.Update(af);
        }

        internal static void SetPackageVersion(AssetInfo info, PackageInfo package)
        {
            Asset asset = DBAdapter.DB.Find<Asset>(info.AssetId);
            if (asset == null) return;

            asset.LatestVersion = package.versions.latestCompatible;
            info.LatestVersion = package.versions.latestCompatible;

            DBAdapter.DB.Update(asset);
        }

        internal static void SetAssetExtraction(AssetInfo info, bool extract)
        {
            Asset asset = DBAdapter.DB.Find<Asset>(info.AssetId);
            if (asset == null) return;

            asset.KeepExtracted = extract;
            info.KeepExtracted = extract;

            if (extract) Assets.EnqueueExtraction(asset);

            DBAdapter.DB.Update(asset);
        }

        internal static void SetAssetUpdateStrategy(AssetInfo info, Asset.Strategy strategy)
        {
            Asset asset = DBAdapter.DB.Find<Asset>(info.AssetId);
            if (asset == null) return;

            asset.UpdateStrategy = strategy;
            info.UpdateStrategy = strategy;

            DBAdapter.DB.Update(asset);
        }

        internal static void ConnectToAssetStore(AssetInfo info, AssetDetails details)
        {
            Asset existing = DBAdapter.DB.Find<Asset>(info.AssetId);
            if (existing == null) return;

            existing.ETag = null;
            info.ETag = null;
            existing.ForeignId = int.Parse(details.packageId);
            info.ForeignId = int.Parse(details.packageId);
            existing.LastOnlineRefresh = DateTime.MinValue;
            info.LastOnlineRefresh = DateTime.MinValue;

            DBAdapter.DB.Update(existing);
        }

        internal static void DisconnectFromAssetStore(AssetInfo info, bool removeMetadata)
        {
            Asset existing = DBAdapter.DB.Find<Asset>(info.AssetId);
            if (existing == null) return;

            existing.ForeignId = 0;
            info.ForeignId = 0;

            if (removeMetadata)
            {
                existing.AssetRating = 0;
                info.AssetRating = 0;
                existing.SafePublisher = null;
                info.SafePublisher = null;
                existing.DisplayPublisher = null;
                info.DisplayPublisher = null;
                existing.SafeCategory = null;
                info.SafeCategory = null;
                existing.DisplayCategory = null;
                info.DisplayCategory = null;
                existing.DisplayName = null;
                info.DisplayName = null;
                existing.OfficialState = Asset.OfficialStateType.None;
                info.OfficialState = Asset.OfficialStateType.None;
                existing.PriceCny = 0;
                info.PriceCny = 0;
                existing.PriceEur = 0;
                info.PriceEur = 0;
                existing.PriceUsd = 0;
                info.PriceUsd = 0;
            }

            DBAdapter.DB.Update(existing);
        }

        internal static string CreateDebugReport()
        {
            string result = "Asset Inventory Support Diagnostics\n";
            result += $"\nDate: {DateTime.Now}";
            result += $"\nVersion: {VERSION}";
            result += $"\nUnity: {Application.unityVersion}";
            result += $"\nPlatform: {Application.platform}";
            result += $"\nOS: {Environment.OSVersion}";
            result += $"\nLanguage: {Application.systemLanguage}";

            List<AssetInfo> assets = Assets.Load();
            result += $"\n\n{assets.Count} Packages";
            foreach (AssetInfo asset in assets)
            {
                result += $"\n{asset} ({asset.SafeName}) - {asset.AssetSource} - {asset.GetVersion()}";
            }

            List<Tag> tags = Tagging.LoadTags();
            result += $"\n\n{tags.Count} Tags";
            foreach (Tag tag in tags)
            {
                result += $"\n{tag} ({tag.Id})";
            }

            result += $"\n\n{Tagging.Tags.Count()} Tag Assignments";
            foreach (TagInfo tag in Tagging.Tags)
            {
                result += $"\n{tag})";
            }

            return result;
        }

        internal static string GetSystemId()
        {
            return SystemInfo.deviceUniqueIdentifier;
        }

        internal static void RegisterSelection(List<AssetInfo> assets)
        {
            GetObserver().SetPrioritized(assets);
        }

        public static void TriggerPackageRefresh()
        {
            OnPackagesUpdated?.Invoke();
        }

        public static void TriggerDatabaseSwitched()
        {
            OnDatabaseSwitched?.Invoke();
        }

        public static void TriggerPackageImageRefresh(Asset asset)
        {
            OnPackageImageLoaded?.Invoke(asset);
        }

        public static void OpenURL(string url)
        {
            if (Config.browserType == 1 && !string.IsNullOrWhiteSpace(Config.customBrowserPath))
            {
                try
                {
#if UNITY_EDITOR_OSX
                    System.Diagnostics.Process.Start("open", $"-a \"{Config.customBrowserPath}\" \"{url}\"");
#else
                    System.Diagnostics.Process.Start(Config.customBrowserPath, url);
#endif
                    return;
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogError($"Could not open URL with custom browser '{Config.customBrowserPath}': {e.Message}. Falling back to system default.");
                }
            }
            Application.OpenURL(url);
        }

        public static void OpenStoreURL(string url)
        {
            AskForAffiliate();
            if (Config.useAffiliateLinks) url += $"?{AFFILIATE_PARAM}";
            OpenURL(url);
        }

        internal static void AskForAffiliate()
        {
            if (!Config.askedForAffiliateLinks)
            {
                Config.askedForAffiliateLinks = true;
                Config.useAffiliateLinks = EditorUtility.DisplayDialog("Support Further Development", "When opening links to the Asset Store, Asset Inventory can add a small affiliate parameter to the link. This helps support the future development of Asset Inventory, and has no cost or negative effect on you. You can opt out in settings at any time. Would you like to turn this on?", "Yes", "No");
                SaveConfig();
            }
        }

#if USE_ASSET_MANAGER && USE_CLOUD_IDENTITY
        private static CloudAssetManagement _cam;
        internal static async Task<CloudAssetManagement> GetCloudAssetManagement()
        {
            await PlatformServices.InitOnDemand();
            if (_cam == null) _cam = new CloudAssetManagement();

            return _cam;
        }
#endif
    }
}
