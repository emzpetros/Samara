using System;
using System.Collections.Generic;
using System.Linq;
using Brain;
using Database;

namespace AssetInventory
{
    public enum CustomPreviewBackgroundType
    {
        Transparent = 0,
        SolidColor = 1,
        TwoColorGradient = 2,
        FourColorGradient = 3
    }

    public enum FBXAnimationPreviewMode
    {
        BoneVisualization = 0,
        UnityHumanoid = 1
    }

    public enum TagSlashHandling
    {
        TakeAsIs = 0,
        CreateHierarchy = 1
    }

    internal enum SearchScope
    {
        All = 0,
        Project = 1,
        Index = 2
    }

    internal static class SearchScopeModel
    {
        public static SearchScope[] GetToolbarScopes(bool showIndexScope)
        {
            return showIndexScope
                ? new[] {SearchScope.All, SearchScope.Project, SearchScope.Index}
                : new[] {SearchScope.All, SearchScope.Project};
        }

        public static SearchScope Normalize(int searchScope, bool showIndexScope)
        {
            if (!Enum.IsDefined(typeof(SearchScope), searchScope)) return SearchScope.All;
            return Normalize((SearchScope)searchScope, showIndexScope);
        }

        public static SearchScope Normalize(SearchScope searchScope, bool showIndexScope)
        {
            if (searchScope == SearchScope.Index && !showIndexScope) return SearchScope.All;
            return searchScope;
        }

        public static bool UsesIndexSearch(SearchScope searchScope)
        {
            return searchScope == SearchScope.All || searchScope == SearchScope.Index;
        }

        public static bool UsesProjectSearch(SearchScope searchScope)
        {
            return searchScope == SearchScope.All;
        }

        public static bool IsProjectOnly(SearchScope searchScope)
        {
            return searchScope == SearchScope.Project;
        }

        public static bool ShouldAutoCalculateAssetUsage(SearchScope searchScope)
        {
            return searchScope == SearchScope.All || searchScope == SearchScope.Project;
        }
    }

    [Serializable]
    public sealed class AssetInventorySettings
    {
        public static class Size
        {
            public const long KB = 1L << 10; // 1024
            public const long MB = 1L << 20; // 1 048 576
            public const long GB = 1L << 30; // 1 073 741 824
        }

        private const int LOG_MEDIA_DOWNLOADS = 1;
        private const int LOG_IMAGE_RESIZING = 2;
        private const int LOG_AUDIO_PARSING = 4;
        private const int LOG_PACKAGE_PARSING = 8;
        private const int LOG_CUSTOM_ACTION = 16;
        private const int LOG_PREVIEW_CREATION = 32;

        public int version = UpgradeUtil.CURRENT_CONFIG_VERSION;
        public int searchType;
        public int searchScope = 2; // 0 = All (Index + Project), 1 = Project Only, 2 = Index Only
        public int searchField;
        public bool searchAICaptions = true;
        public bool searchPackageNames;
        public int sortField = 2;
        public bool sortDescending;
        public int maxResults = 5;
        public int maxResultsLimit = 10000;
        public int maxInMemoryResults = 50000;
        public int maxProjectSearchResults = 5000;
        public int timeout = 20;
        public int tileText; // 0 - intelligent
        public bool autoPlayAudio = true;
        public int autoCalculateDependencies = 2; // 0 - none, 2 - upon selection
        public bool allowCrossPackageDependencies = true;
        public int scriptImportMode; // 0 = never, 2 = direct only, 3 = extended analysis, 4 = all scripts
        public bool loopAudio;
        public bool pingSelected = true;
        public bool pingImported = true;
        public int doubleClickAction = 2; // 0 = none, 2 = import+add to scene, 3 = import, 4 = open
        public int doubleClickAltAction = 4;
        public bool groupLists = true;
        public bool keepAutoDownloads;
        public bool limitAutoDownloads;
        public int downloadLimit = 500;
        public bool searchAutomatically = true;
        public bool searchWithoutInput = true;
        public bool showIndexSearchScope = true;
        public bool searchSubPackages;
        public bool excludeIncompatibleSRPs = true;
        public bool extractSingleFiles;
        public int previewVisibility;
        public int searchTileSize = 128;
        public int searchListRowHeight = 22;
        public int tileCornerRadius;
        public float searchTileAspectRatio = 1f;
        public float searchDelay = 0.5f;
        public float inMemorySearchDelay = 0.1f;
        public float variableDetectionDelay = 0.3f;
        public float hueRange = 10f;
        public int animationGrid = 4;
        public float animationSpeed = 0.15f;
        public bool playVisibleSearchAnimations; // Play all visible animated previews in search grid
        public int maxVisibleSearchAnimations = 50; // Max concurrent animated tiles in search grid
        public bool excludePreviewExtensions = true;
        public string excludedPreviewExtensions = "{audio}";
        public bool excludeExtensions = true;
        public string excludedExtensions = "asset;json;shadersubgraph;txt;cs;md;md5;uss;asmdef;uxml;editorconfig;signature;yml;cginc;gitattributes;release;collabignore;suo;ds_store;asmref;hlsl;shaderinclude;hlslinc;vfxoperator;vfxblock;shadervariants;sgsubgraphdb;preset;signal;template;scenetemplate;modulecompilationtrigger;api;url;bytes;yaml;xml;gitignore;npmignore;lock;log;tmp;bak;orig;user;csproj;sln;pdb;mdb;rsp;props;targets;ruleset;dll;so;dylib;bundle;jar;aar;jslib;cpp;c;h;hpp;mm;swift;java;kt;gradle;properties;plist;xcprivacy";
        public bool showExtensionsList;
        public bool keepExtractedOnAudio = true;
        public bool disableDragDrop;

        public int workspace;
        public bool wsSearchWithoutInput;
        public bool wsSavedSearchInMemory = true;

        public int rowHeight = 22;
        public int rowHeightMedia = 120;
        public int fontSize = 11;
        public int previewChunkSize = 20;
        public int previewSize = 128;
        public int mediaHeight = 350;
        public bool mediaSameWidth = true;
        public bool mediaMaintainAspect = true;
        public int mediaXSpacing = 4;
        public int mediaYFillRatio = 80;
        public int mediaThumbnailWidth = 120;
        public int mediaThumbnailHeight = 75;
        public int mediaCornerRadius = 10;
        public bool showOriginalPrice;
        public int currency; // 0 - EUR, 1 - USD, 2 - CYN
        public int packageTileSize = 150;
        public int noPackageTileTextBelow = 110;
        public int tagListHeight = 250;
        public int tileMargin = 2;
        public bool enlargeTiles = true;
        public bool centerTiles;
        public bool proposeSaveSceneDialog;
        public int[] visibleSearchTreeColumns;
        public int[] visiblePackageTreeColumns;
        public float[] packageTreeColumnWidths;
        public float[] searchTreeColumnWidths;
        public float[] reportTreeColumnWidths;

        public bool showSearchSideBar = true;
        public bool showSearchHierarchySideBar;
        public bool showPackageSideBar = true;
        public int searchLeftSideBarHierarchy; // 0=Path, 1=Category, 2=Publisher, 3=Package, 4=Type
        public bool expandPackageDetails;
        public bool alwaysShowPackageDetails;
        public bool alwaysShowWorkspaces;
        public bool showPreviews = true;
        public bool showIndexingSettings;
        public bool showFolderSettings;
        public bool showAMSettings;
        public bool showImportSettings;
        public bool showBackupSettings;
        public bool showAISettings;
        public bool showUISettings;
        public int browserType; // 0 = system default, 1 = custom
        public string customBrowserPath;
        public bool showLocationSettings;
        public bool showPreviewSettings;
        public bool showAdvancedSettings;
        public bool showHints = true;
        public int packageViewMode; // 0 = list, 1 = grid
        public int searchViewMode = 1; // 0 = list, 1 = grid
        public bool searchPackageGroupNames = true;
        public bool searchPackageDescriptions;
        public bool showPackageStatsDetails;
        public bool showColoredPackageTreeTags = true;
        public bool onlyInProject; // legacy, migrated to scope
        public bool projectDetailTabs = true;

        public bool excludeHidden = true;
        public int assetStoreRefreshCycle = 3; // days
        public int assetCacheLocationType; // 0 = auto, 1 = custom
        public string assetCacheLocation;
        public int packageCacheLocationType; // 0 = auto, 1 = custom
        public string packageCacheLocation;
        public bool gatherExtendedMetadata = true;
        public bool extractPreviews = true;
        public bool extractAudioColors;
        public bool excludeByDefault;
        public bool extractByDefault;
        public bool captionByDefault;
        public bool useUnityPipelineConverter; // use Unity's built-in converter (BIRP→URP only, requires USE_URP)
        public bool useCustomPipelineConverter = true; // use our custom converter (BIRP→URP and BIRP→HDRP, runs on preview instances)
        public bool skipProjectSettings = true;
        public bool scanFBXDependencies = true;
        public bool scanOBJMaterialDependencies;
        public bool awaitNonBlocking = true;
        public bool indexSubPackages = true;
        public bool indexAssetPackageContents = true;
        public bool verifyPreviews = true;
        public bool directMediaPreviews = true;
        public bool recreatePreviewsAfterIndexing = true;
        public bool downloadPackagesForPreviews = true;
        public bool showIconsForMissingPreviews = true;
        public bool confirmPreviewRescheduling = true;
        public int parallelPreviewBatchSize = 20; // Number of previews to process in parallel (1 = sequential)
        public int bulkPreviewThreshold = 20; // Minimum file count for bulk materialization. Set to <=0 to disable.
        public float minPreviewWait = 9f; // Minimum time in seconds to wait for Unity preview generation before giving up
        public int directoryPackageMediaCount = 7; // Number of media entries to create for directory packages
        public bool importPackageKeywordsAsTags;
        public TagSlashHandling tagSlashHandling = TagSlashHandling.CreateHierarchy;
        public string customStorageLocation;
        public bool showCustomPackageUpdates;
        public bool showIndirectPackageUpdates;
        public bool removeUnresolveableDBFiles = true;
        public bool markUnresolveableForReindexing = true;

        public bool logAICaptions;
        public bool aiForPrefabs = true;
        public bool aiForModels;
        public bool aiForImages;
        public int aiBackend = 1; // 0 - Blip, 1 = Ollama, 2 = LM Studio
        public bool aiContinueOnEmpty;
        public float aiPause;
        public int aiTimeout = 120; // per-request timeout in seconds; 0 = no timeout
        public int aiMinSize = 32; // minimum size for AI processing, in pixels, upscales otherwise
        public int aiMaxCaptionLength = 200; // some model outputs are extremely long and cause crashes
        public string aiCustomPrompt;

        public string ollamaModel = "qwen2.5vl:7b";
        public string ollamaServiceUrl = Intelligence.OLLAMA_SERVICE_URL;
        public int ollamaParallelRequests = 4; // Matches Ollama's default OLLAMA_NUM_PARALLEL

        public string lmStudioModel = "qwen/qwen2.5-vl-7b";
        public string lmStudioServiceUrl = Intelligence.LMSTUDIO_SERVICE_URL;
        public int lmStudioParallelRequests = 20; // Number of parallel requests to send to LMStudio

        public int blipType; // 0 - small, 1 = large
        public int blipChunkSize = 1;
        public bool blipUseGPU;
        public string blipPath;

        public bool upscalePreviews = true;
        public bool upscaleLossless = true;
        public int upscaleSize = 256;

        // Custom Preview Pipeline Settings
        public bool generateCustomModelPreviews = true;
        public bool generateAnimatedModelPreviews;
        public bool generateUIPreviews = true;
        public bool generateParticlePreviews = true;
        public bool generateAnimatedParticlePreviews = true;
        public bool generateVFXPreviews = true;
        public bool generateAnimatedVFXPreviews = true;
        public bool generateFontPreviews = true;
        public bool generateVideoPreviews = true;
        public bool generateAnimatedVideoPreviews = true;
        public bool generateFBXPreviews = true;
        public bool generateAnimatedFBXPreviews = true;
        public bool generate360FBXPreviews; // 360° camera rotation for all FBX (independent of animation)
        public FBXAnimationPreviewMode fbxAnimationPreviewMode = FBXAnimationPreviewMode.UnityHumanoid; // How to visualize humanoid animation-only FBX without avatar
        public bool generateAnimPreviews = true; // Generate previews for standalone .anim files
        public bool generateAnimatedAnimPreviews = true; // Generate animated preview spritesheets for .anim files
        public bool generateScenePreviews;
        public bool generateMaterialPreviews = true; // Generate custom previews for materials
        public int materialPreviewMesh; // 0 = Sphere, 1 = Cube, 2 = Plane, 3 = Cylinder
        public bool overrideProjectPreviews = true; // Show custom preview icons for prefabs in Unity's Project window
        public bool playProjectWindowAnimations = true; // Play animations for prefabs with spritesheets in Project window
        public int maxAnimatedProjectPreviews = 30; // Maximum number of animated previews in Project window (memory limit)
        public bool embedAnimatedPreviewIndicator = true; // Embed play icon indicator in static previews when animated preview exists

        // Rendering Quality (super-sampling)
        public int cpSuperSamplingMultiplier = 4; // Multiplier applied to preview size for native render resolution
        public int cpDepth = 24;

        // Camera Settings
        public float cpCameraFOV = 30f;
        public bool cpRotateLightWith360 = true;
        public float cpCameraAngleX = 70f;
        public float cpCameraAngleY = 240f;
        public float cpFramingPadding; // Padding in % of preview size for 3D model framing

        // Lighting Settings
        public bool cpUseDirectionalLight = true;
        public string cpLightColor = "FFFFFFFF";
        public float cpLightIntensity = 0.8f; // BiRP
        public float cpLightIntensityURP = 0.5f;
        public float cpLightIntensityHDRP = 5000f; // Lux
        public float cpLightAngleX = 58f;
        public float cpLightRotationX = 58f;
        public float cpLightRotationY = 249f;

        // Secondary Light Settings (rim/fill light)
        public bool cpUseSecondaryLight;
        public string cpSecondaryLightColor = "6666FFFF"; // Subtle blue-grey
        public float cpSecondaryLightIntensityMultiplier = 0.7f; // 70% of main light
        public float cpSecondaryLightRotationX = 340f; // Unity's Light1 angle
        public float cpSecondaryLightRotationY = 341f;

        // Background Settings
        public CustomPreviewBackgroundType cpBackgroundType = CustomPreviewBackgroundType.SolidColor;
        public string cpBackgroundColor = "525252FF"; // RGBA hex (BiRP/URP)
        public string cpBackgroundColorHDRP = "222222FF"; // RGBA hex (HDRP)

        // 2-Color Gradient Settings
        public string cpGradient2TopColor = "808080FF"; // Top color
        public string cpGradient2BottomColor = "404040FF"; // Bottom color

        // 4-Color Gradient Settings
        public string cpGradient4TopLeftColor = "808080FF"; // Top-left corner
        public string cpGradient4TopRightColor = "606060FF"; // Top-right corner
        public string cpGradient4BottomLeftColor = "404040FF"; // Bottom-left corner
        public string cpGradient4BottomRightColor = "303030FF"; // Bottom-right corner

        // Gradient Rotation
        public float cpGradientRotation; // Rotation angle in degrees (0-360)

        // VFX/Animation Settings (frameCount uses existing AI.Config.animationGrid squared)
        public float cpVFXMaxDuration = 5f;
        public uint cpParticleSeed = 1;
        public float cpParticleSimulateTime = 10f;
        public float cpParticleMinimumVisibleDuration;
        public float cpVFXMinimumVisibleDuration;

        // Font Settings
        public string cpFontColor = "FFFFFFFF";

        // Environment Settings
        public bool cpUseCustomSkybox;
        public string cpSkyboxPath = ""; // Asset path to skybox material
        public float cpAmbientIntensity = 0.25f;

        public bool hideAdvanced = true;
        public bool forceInitOnDomainReload;
        public bool colorTagFilterClosedField;
        public bool useCooldown = true;
        public int cooldownInterval = 20; // minutes
        public int cooldownDuration = 20; // seconds
        public int purchaseBatchSize = 100;
        public int reportingBatchSize = 500;
        public bool reportingAutoResolve = true;
        public long memoryLimit = 10 * Size.GB; // every X gigabytes
        public bool limitCacheSize = true;
        public int cacheLimit = 60; // in gigabyte
        public int massOpenWarnThreshold = 7;
        public int logAreas = LOG_IMAGE_RESIZING | LOG_AUDIO_PARSING | LOG_MEDIA_DOWNLOADS | LOG_PACKAGE_PARSING | LOG_CUSTOM_ACTION | LOG_PREVIEW_CREATION;
        public int dbOptimizationPeriod = 30; // days
        public int dbOptimizationReminderPeriod = 1; // days
        public string dbJournalMode = "WAL"; // DELETE is an alternative for better compatibility while WAL is faster

        // Database type configuration
        public string databaseType = DatabaseFactory.SQLITE; // "SQLite" or "MySQL"
        public string mysqlHost = "localhost";
        public int mysqlPort = 3306;
        public string mysqlDatabase; // MySQL database name
        public string mysqlUser;
        public string mysqlEncryptedPassword; // Encrypted password using EncryptionUtil (same as FTP)
        public bool mysqlUseSSL;
        public int mysqlConnectionTimeout = 30; // seconds
        public int mysqlUpgradeTimeout = 1800; // seconds, command execution timeout during DB upgrades/migrations

        public bool askedForAffiliateLinks;
        public bool useAffiliateLinks;

        public bool backupByDefault;
        public bool onlyLatestPatchVersion = true;
        public int backupsPerAsset = 5;
        public string backupFolder;

        public bool enableDatabaseBackup = true;
        public int databaseBackupInterval = 7; // days
        public int databaseBackupsToKeep = 2;
        public DateTime lastDatabaseBackup;
        public string cacheFolder;
        public string previewFolder;
        public string exportFolder;
        public string exportFolder2;
        public string exportFolder3;
        public string customTemplateFolder;
        public CSVExportSettings csvExportSettings = new CSVExportSettings();
        public TemplateExportSettings templateExportSettings = new TemplateExportSettings();

        public int importStructure = 1;
        public int importDestination = 2;
        public string importFolder = "Assets/ThirdParty";
        public bool reorganizeOnReimport;
        public bool deleteEmptyFoldersOnReorganize = true;
        public bool removeLODs;

        public int assetSorting;
        public bool sortAssetsDescending;
        public int assetGrouping;
        public int assetDeprecation;
        public int assetSRPs;
        public int packagesListing = 1; // only assets per default
        public int maxConcurrentUnityRequests = 100;
        public int observationSpeed = 5;
        public bool autoRefreshMetadata = true;
        public int metadataTimeout = 12; // in hours
        public bool autoStopObservation = true;
        public int observationTimeout = 10; // in seconds
        public bool autoRefreshPurchases = true;
        public int purchasesRefreshPeriod = 12; // in hours
        public DateTime lastPurchasesUpdate;
        public DateTime lastMetadataUpdate;

        // non-preferences for convenience
        public int tab;
        public bool quickIndexingDone;
        public ulong statsImports;

        // wizard state
        public bool wizardCompleted;
        public int wizardCurrentPage;
        public bool welcomeShown; // show welcome window once

        public List<UpdateActionStates> actionStates = new List<UpdateActionStates>();
        public List<FolderSpec> folders = new List<FolderSpec>();
        public List<FTPConnection> ftpConnections = new List<FTPConnection>();

        // log helpers
        public bool LogMediaDownloads => (logAreas & LOG_MEDIA_DOWNLOADS) != 0;
        public bool LogImageExtraction => (logAreas & LOG_IMAGE_RESIZING) != 0;
        public bool LogAudioParsing => (logAreas & LOG_AUDIO_PARSING) != 0;
        public bool LogPackageParsing => (logAreas & LOG_PACKAGE_PARSING) != 0;
        public bool LogCustomActions => (logAreas & LOG_CUSTOM_ACTION) != 0;
        public bool LogPreviewCreation => (logAreas & LOG_PREVIEW_CREATION) != 0;

        // UI customization
        public List<UISection> uiSections = new List<UISection>();
        public HashSet<string> advancedUI;

        // outdated
        public List<OutdatedSavedSearch> searches = new List<OutdatedSavedSearch>();

        public AssetInventorySettings()
        {
            ResetAdvancedUI();
        }

        public void InitUISections()
        {
            if (uiSections == null) uiSections = new List<UISection>();
            if (!uiSections.Any(uis => uis.name == "package"))
            {
                uiSections.Add(new UISection {name = "package", sections = new List<string> {"PackageData", "TabbedDetails", "Media", "Description", "ReleaseNotes", "Dependencies"}});
            }
        }

        public UISection GetSection(string name)
        {
            InitUISections(); // ensure sections are always initialized

            return uiSections.FirstOrDefault(s => s.name == name);
        }

        public void ResetAdvancedUI()
        {
            // list of UI elements that should be hidden by default
            advancedUI = new HashSet<string>
            {
                "settings.actions.clearcache",
                "settings.actions.cleardb",
                "settings.actions.resetconfig",
                "settings.actions.resetuiconfig",
                "settings.actions.closedb",
                "settings.actions.openassetcache",
                "settings.actions.openpackagecache",
                "settings.actions.dblocation",
                "package.category",
                "package.childcount",
                "package.exclude",
                "package.extract",
                "package.indexedfiles",
                "package.metadata",
                "package.price",
                "package.purchasedate",
                "package.releasedate",
                "package.srps",
                "package.unityversions",
                "package.actions.layout",
                "package.actions.sidebar",
                "package.actions.openinpackagemanager",
                "package.actions.reindexnextrun",
                "package.actions.recreatemissingpreviews",
                "package.actions.recreateimagepreviews",
                "package.actions.recreateallpreviews",
                "package.actions.recreatepreviews",
                "package.actions.delete",
                "package.actions.openlocation",
                "package.actions.refreshmetadata",
                "package.actions.export",
                "package.actions.deletefile",
                "package.actions.nameonly",
                "package.actions.reindexnow",
                "package.actions.removeassetstoreconnection",
                "package.actions.sort",
                "reporting.projectviewselection",
                "asset.meshes",
                "asset.triangles",
                "asset.bones",
                "asset.actions.openexplorer",
                "asset.actions.inmemorymode",
                "asset.actions.delete",
                "asset.bulk.actions.export",
                "asset.bulk.actions.delete",
                "asset.bulk.actions.openexplorer",
                "package.bulk.actions.refreshmetadata",
                "package.bulk.actions.delete",
                "package.bulk.actions.deletefile",
                "package.bulk.actions.openlocation",
                "search.actions.tilesize",
                "search.actions.viewmode",
                "search.actions.previewanim",
                "search.actions.sidebar"
            };
        }
    }
}
