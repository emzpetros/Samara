using ImpossibleRobert.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Database;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.IMGUI.Controls;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AssetInventory
{
    public partial class IndexUI : BasicEditorUI
    {
        private const float CHECK_INTERVAL = 5;

        private readonly Dictionary<string, string> _staticPreviews = new Dictionary<string, string>
        {
            {"cs", "cs Script Icon"},
            {"php", "TextAsset Icon"},
            {"cg", "TextAsset Icon"},
            {"cginc", "TextAsset Icon"},
            {"js", "d_Js Script Icon"},
            {"prefab", "d_Prefab Icon"},
            {"png", "d_RawImage Icon"},
            {"jpg", "d_RawImage Icon"},
            {"gif", "d_RawImage Icon"},
            {"tga", "d_RawImage Icon"},
            {"tiff", "d_RawImage Icon"},
            {"ico", "d_RawImage Icon"},
            {"bmp", "d_RawImage Icon"},
            {"fbx", "d_PrefabModel Icon"},
            {"dll", "dll Script Icon"},
            {"meta", "MetaFile Icon"},
            {"unity", "d_SceneAsset Icon"},
            {"asset", "EditorSettings Icon"},
            {"txt", "TextScriptImporter Icon"},
            {"md", "TextScriptImporter Icon"},
            {"doc", "TextScriptImporter Icon"},
            {"docx", "TextScriptImporter Icon"},
            {"pdf", "TextScriptImporter Icon"},
            {"rtf", "TextScriptImporter Icon"},
            {"readme", "TextScriptImporter Icon"},
            {"chm", "TextScriptImporter Icon"},
            {"compute", "ComputeShader Icon"},
            {"shader", "Shader Icon"},
            {"shadergraph", "Shader Icon"},
            {"shadersubgraph", "Shader Icon"},
            {"mat", "d_Material Icon"},
            {"wav", "AudioImporter Icon"},
            {"mp3", "AudioImporter Icon"},
            {"ogg", "AudioImporter Icon"},
            {"xml", "UxmlScript Icon"},
            {"html", "UxmlScript Icon"},
            {"uss", "UssScript Icon"},
            {"css", "StyleSheet Icon"},
            {"json", "StyleSheet Icon"},
            {"exr", "d_ReflectionProbe Icon"}
        };

        private enum ChangeImpact
        {
            None,
            ReadOnly,
            Write
        }

        internal static string[] assetFields =
        {
            "Asset/AssetRating", "Asset/AssetSource", "Asset/Backup", "Asset/BIRPCompatible", "Asset/CompatibilityInfo", "Asset/CurrentState", "Asset/CurrentSubState", "Asset/Description", "Asset/DisplayCategory", "Asset/DisplayName", "Asset/DisplayPublisher", "Asset/ETag", "Asset/Exclude",
            "Asset/FirstRelease", "Asset/ForeignId", "Asset/HDRPCompatible", "Asset/Hotness", "Asset/Hue", "Asset/Id", "Asset/IsHidden", "Asset/IsLatestVersion", "Asset/KeepExtracted", "Asset/KeyFeatures", "Asset/Keywords", "Asset/LastOnlineRefresh", "Asset/LastRelease", "Asset/LatestVersion",
            "Asset/License", "Asset/LicenseLocation", "Asset/Location", "Asset/OriginalLocation", "Asset/OriginalLocationKey", "Asset/PackageDependencies", "Asset/PackageSize", "Asset/PackageSource", "Asset/ParentId", "Asset/PriceCny", "Asset/PriceEur", "Asset/PriceUsd",
            "Asset/PublisherId", "Asset/PurchaseDate", "Asset/RatingCount", "Asset/Registry", "Asset/ReleaseNotes", "Asset/Repository", "Asset/Requirements", "Asset/Revision", "Asset/SafeCategory", "Asset/SafeName",
            "Asset/SafePublisher", "Asset/Slug", "Asset/SupportedUnityVersions", "Asset/UpdateStrategy", "Asset/UploadId", "Asset/URPCompatible", "Asset/UseAI", "Asset/Version",
            "AssetFile/AssetId", "AssetFile/FileName", "AssetFile/FileVersion", "AssetFile/FileStatus", "AssetFile/Guid", "AssetFile/Height", "AssetFile/Hue", "AssetFile/Id", "AssetFile/Length", "AssetFile/Path", "AssetFile/PreviewState", "AssetFile/Size", "AssetFile/SourcePath", "AssetFile/Type", "AssetFile/Width",
            "Tag/Color", "Tag/FromAssetStore", "Tag/Id", "Tag/Name",
            "TagAssignment/Id", "TagAssignment/TagId", "TagAssignment/TagTarget", "TagAssignment/TagTargetId"
        };

        internal static readonly string[] FolderTypes = {"Unity Packages", "Media Folder", "Archives", "Dev Packages"};
        internal static readonly string[] MediaTypes = {"-All Media-", "-All Files-", string.Empty, "Audio", "Images", "Models", string.Empty, "-Custom File Pattern-"};

        private List<Tag> _tags;
        private string[] _assetNames;
        private string[] _tagNames;
        private SearchablePopup.PopupItem[] _tagPopupItems;
        private string[] _publisherNames;
        private string[] _colorOptions;
        private string[] _categoryNames;
        private string[] _types;
        private string[] _resultSizes;
        private string[] _sortFields;
        private string[] _searchFields;
        private string[] _tileTitle;
        private string[] _dependencyOptions;
        private string[] _scriptImportOptions;
        private string[] _previewOptions;
        private string[] _doubleClickOptions;
        private string[] _packageSortOptions;
        private string[] _groupByOptions;
        private string[] _packageListingOptions;
        private string[] _imageTypeOptions;
        private GUIContent[] _packageListingOptionsShort;
        private GUIContent[] _packageViewOptions;
        private GUIContent[] _searchScopeOptions;
        private SearchScope[] _searchScopeValues;
        private string[] _deprecationOptions;
        private string[] _srpOptions;
        private string[] _priceOptions;
        private string[] _maintenanceOptions;
        private string[] _importDestinationOptions;
        private string[] _importStructureOptions;
        private string[] _assetCacheLocationOptions;
        private string[] _expertSearchFields;
        private string[] _currencyOptions;
        private string[] _logOptions;
        private string[] _blipOptions;
        private string[] _aiBackendOptions;
        private string[] _browserTypeOptions;

        private int _lastTab = -1;
        private string _newTag;
        private int _lastMainProgress;
        private string _importFolder;
        private bool _blockingInProgress;
        private bool _needsRepaint;

        private string[] _pvSelection;
        private string _pvSelectedPath;
        private string _pvSelectedFolder;
        private InventoryStats _stats;
        private int _availablePackageUpdates;
        private int _activePackageDownloads;

        private List<AssetInfo> _assets;

        internal static bool HasOpenInstances => _openInstanceCount > 0;
        private static int _openInstanceCount;
        private static int _scriptsReloaded;
        private static bool? _cachedVersionMismatch;
        private bool _registeredOpenInstance;
        private bool _requireAssetTreeRebuild;
        private bool _requireReportTreeRebuild;
        private ChangeImpact _requireLookupUpdate;
        private bool _requireSearchUpdate;
        private bool _requireSearchSelectionUpdate;
        private bool _searchSelectionChangedManually;
        private DateTime _lastCheck;
        private bool _initDone;
        private bool _updateAvailable;
        private AssetDetails _onlineInfo;
        private bool _allowLogic;
        private Editor _previewEditor;

        private bool _searchHandlerAdded;
        private bool _selectionHandlerAdded;
        private bool _isCleaningUp;
        private bool _delayedInitQueued;
        private bool _runtimeHooksRegistered;

        private void Init()
        {
            if (_initDone) return;
            _initDone = true;

            _fixedSearchTypeIdx = -1;
            AI.Init();
            InitFolderControl();

            _blockingInProgress = false;
            _dependencyCancellationTokens = new Dictionary<AssetInfo, CancellationTokenSource>();

            if (_requireLookupUpdate == ChangeImpact.None) _requireLookupUpdate = ChangeImpact.ReadOnly;
            _requireSearchUpdate = true;
            _requireAssetTreeRebuild = true;

            _ = CheckForToolUpdates();
            _ = CheckForAssetUpdates();
        }

        private static MultiColumnHeaderState GetUsableMultiColumnHeaderState(MultiColumnHeaderState serializedState, MultiColumnHeaderState defaultState)
        {
            if (serializedState == null) return defaultState;

            try
            {
                if (MultiColumnHeaderState.CanOverwriteSerializedFields(serializedState, defaultState))
                {
                    MultiColumnHeaderState.OverwriteSerializedFields(serializedState, defaultState);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Asset Inventory: Ignoring incompatible serialized tree header state: {e.Message}");
            }

            return defaultState;
        }

        private void OnEnable()
        {
            RegisterOpenInstance();

            if (_usageCalculationInProgress && _usageCalculation == null) _usageCalculationInProgress = false; // process was interrupted
            _pvSelection = null;
            _initDone = false;
            _isCleaningUp = false;

            QueueDelayedInit();
        }

        private void QueueDelayedInit()
        {
            if (_delayedInitQueued) return;

            _delayedInitQueued = true;
            EditorApplication.delayCall -= DelayedInit;
            EditorApplication.delayCall += DelayedInit;
        }

        private void DelayedInit()
        {
            _delayedInitQueued = false;
            if (this == null || _isCleaningUp) return;

            Init();
            RegisterRuntimeHooks();

            AudioTool.AudioManager.StopAudio();
            if (!AI.IsInitialized) return;

            AssetStore.FillBufferOnDemand(true);
            if (!searchMode) SuggestOptimization();
            if (ShowWorkspaces()) InitWorkspace();

            // have to go through preliminary title as OnEnable is called before setting any additional properties
            if (!titleContent.text.Contains("Picker")) AI.StartCacheObserver(); // expensive operation, only do when UI is visible
        }

        private void RegisterRuntimeHooks()
        {
            if (_runtimeHooksRegistered) return;

            _runtimeHooksRegistered = true;
            EditorApplication.update += UpdateLoop;
            AI.Actions.OnActionsDone += OnActionsDone;
            AI.Actions.OnActionsInitialized += OnActionsInitialized;
            AI.OnPackageImageLoaded += OnPackageImageLoaded;
            AI.OnPackagesUpdated += OnPackagesUpdated;
            AI.OnDatabaseSwitched += OnDatabaseSwitched;
            Tagging.OnTagsChanged += OnTagsChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorSceneManager.sceneOpened += OnSceneLoaded;
            ImportUI.OnImportDone += OnImportDone;
            RemovalUI.OnUninstallDone += OnImportDone;
            MaintenanceUI.OnMaintenanceDone += OnMaintenanceDone;
            UpgradeUtil.OnUpgradeDone += OnMaintenanceDone;
            AssetStore.OnPackageListUpdated += OnPackageListUpdated;
            AssetDatabase.importPackageCompleted += ImportCompleted;
            AssetDownloaderUtils.OnDownloadFinished += OnDownloadFinished;
            Events.registeredPackages += OnRegisteredPackages;
#if UNITY_6000_3_OR_NEWER
            DragAndDrop.AddDropHandlerV2(OnSceneDrop);
            DragAndDrop.AddDropHandlerV2(OnHierarchyDrop);
            DragAndDrop.AddDropHandlerV2(OnProjectBrowserDrop);
            DragAndDrop.AddDropHandlerV2(OnInspectorDrop);
#else
            DragAndDrop.AddDropHandler(OnSceneDrop);
            DragAndDrop.AddDropHandler(OnHierarchyDrop);
            DragAndDrop.AddDropHandler(OnProjectBrowserDrop);
            DragAndDrop.AddDropHandler(OnInspectorDrop);
#endif
        }

        private void UnregisterRuntimeHooks()
        {
            if (!_runtimeHooksRegistered) return;

            _runtimeHooksRegistered = false;
            EditorApplication.update -= UpdateLoop;
            AI.Actions.OnActionsDone -= OnActionsDone;
            AI.Actions.OnActionsInitialized -= OnActionsInitialized;
            AI.OnPackageImageLoaded -= OnPackageImageLoaded;
            AI.OnPackagesUpdated -= OnPackagesUpdated;
            AI.OnDatabaseSwitched -= OnDatabaseSwitched;
            Tagging.OnTagsChanged -= OnTagsChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            EditorSceneManager.sceneOpened -= OnSceneLoaded;
            ImportUI.OnImportDone -= OnImportDone;
            RemovalUI.OnUninstallDone -= OnImportDone;
            MaintenanceUI.OnMaintenanceDone -= OnMaintenanceDone;
            UpgradeUtil.OnUpgradeDone -= OnMaintenanceDone;
            AssetStore.OnPackageListUpdated -= OnPackageListUpdated;
            AssetDatabase.importPackageCompleted -= ImportCompleted;
            AssetDownloaderUtils.OnDownloadFinished -= OnDownloadFinished;
            Events.registeredPackages -= OnRegisteredPackages;
#if UNITY_6000_3_OR_NEWER
            DragAndDrop.RemoveDropHandlerV2(OnSceneDrop);
            DragAndDrop.RemoveDropHandlerV2(OnHierarchyDrop);
            DragAndDrop.RemoveDropHandlerV2(OnProjectBrowserDrop);
            DragAndDrop.RemoveDropHandlerV2(OnInspectorDrop);
#else
            DragAndDrop.RemoveDropHandler(OnSceneDrop);
            DragAndDrop.RemoveDropHandler(OnHierarchyDrop);
            DragAndDrop.RemoveDropHandler(OnProjectBrowserDrop);
            DragAndDrop.RemoveDropHandler(OnInspectorDrop);
#endif
        }

        private void OnDisable()
        {
            UnregisterOpenInstance();

            EditorApplication.delayCall -= DelayedInit;
            _delayedInitQueued = false;
            UnregisterRuntimeHooks();

            AudioTool.AudioManager.StopAudio();
            if (AI.IsInitialized) AI.StopCacheObserver();

            // Clean up preview editor to prevent PreviewRenderUtility leak during assembly reload
            CleanupPreviewEditor();

            // Cancel any ongoing operations
            _textureLoading?.Cancel();
            _textureLoading2?.Cancel();
            _textureLoading3?.Cancel();

            // Dispose CancellationTokenSource objects to prevent memory leaks
            _textureLoading?.Dispose();
            _textureLoading2?.Dispose();
            _textureLoading3?.Dispose();
            _extraction?.Dispose();

            // Cancel and dispose all dependency calculation tokens
            if (_dependencyCancellationTokens != null)
            {
                foreach (KeyValuePair<AssetInfo, CancellationTokenSource> kvp in _dependencyCancellationTokens)
                {
                    kvp.Value?.Cancel();
                    kvp.Value?.Dispose();
                }
                _dependencyCancellationTokens.Clear();
            }

            // Unsubscribe grid event handlers if grid was created
            if (_sgrid != null)
            {
                _sgrid.OnDoubleClick -= OnSearchDoubleClick;
                _sgrid.OnKeyboardSelection -= OnSearchKeyboardSelection;
                _sgrid.OnContextMenuPopulate -= PopulateSearchGridContextMenu;
            }

            // Unsubscribe tree view event handlers if created
            if (_searchTreeView != null)
            {
                _searchTreeView.OnSelectionChanged -= OnSearchTreeSelectionChanged;
                _searchTreeView.OnDoubleClickedItem -= OnSearchTreeDoubleClick;
                _searchTreeView.OnContextMenuPopulate -= PopulateSearchTreeContextMenu;
            }

            if (_initDone && AI.IsInitialized) SaveColumnWidths();

            // Dispose preview textures to prevent memory leaks
            DisposeSearchResultTextures();
            ClearFilePreviewCache();
        }

        private void OnDestroy()
        {
            UnregisterOpenInstance();
            EditorApplication.delayCall -= DelayedInit;

            // Final cleanup when window is closed (not just disabled)
            DisposeSearchResultTextures();
            ClearFilePreviewCache();

            // Cleanup any remaining resources
            if (_animationPlayer != null)
            {
                _animationPlayer.Dispose();
                _animationPlayer = null;
            }
        }

        private void RegisterOpenInstance()
        {
            if (_registeredOpenInstance) return;

            _registeredOpenInstance = true;
            _openInstanceCount++;
        }

        private void UnregisterOpenInstance()
        {
            if (!_registeredOpenInstance) return;

            _registeredOpenInstance = false;
            _openInstanceCount = Mathf.Max(0, _openInstanceCount - 1);
        }

        private void UpdateLoop()
        {
            SearchUpdateLoop();
        }

        private void SuggestOptimization()
        {
            // check if last optimization (stored as "yyyy-MM-dd HH:mm:ss" string) was more than a month ago
            AppProperty lastOptimization = DBAdapter.DB.Find<AppProperty>("LastOptimization");
            if (lastOptimization == null || string.IsNullOrWhiteSpace(lastOptimization.Value) || !DateTime.TryParse(lastOptimization.Value, out DateTime lastOpt))
            {
                OptimizeDatabase(true);
                return;
            }
            if ((DateTime.Now - lastOpt).TotalDays < AI.Config.dbOptimizationPeriod) return;

            // check if last optimization request (stored as "yyyy-MM-dd HH:mm:ss" string) was more than a day ago
            AppProperty lastOptRequest = DBAdapter.DB.Find<AppProperty>("LastOptimizationRequest");
            if (lastOptRequest == null || (DateTime.TryParse(lastOptRequest.Value, out DateTime lastOptReq) && (DateTime.Now - lastOptReq).TotalDays > AI.Config.dbOptimizationReminderPeriod))
            {
                lastOptRequest = new AppProperty("LastOptimizationRequest", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                DBAdapter.DB.InsertOrReplace(lastOptRequest);

                if (EditorUtility.DisplayDialog("Asset Inventory Maintenance", "It is recommended to optimize the database regularly to ensure fast search results. Should it be done now?", "OK", "Not Now"))
                {
                    OptimizeDatabase();
                }
            }
        }

        private void RequireTreesRebuild()
        {
            _requireAssetTreeRebuild = true;
            if (_usageCalculationDone) _requireReportTreeRebuild = true;
        }

        private void OnPackagesUpdated()
        {
            _requireLookupUpdate = ChangeImpact.Write;
            _requireSearchUpdate = true;
            RequireTreesRebuild();
            _usageCalculationDone = false;
        }

        private void OnDatabaseSwitched() => ReloadAfterDatabaseSwitch();

        private void OnMaintenanceDone()
        {
            _searches = null;
            _requireLookupUpdate = ChangeImpact.Write;
            _requireSearchUpdate = true;
            RequireTreesRebuild();
        }

        private void OnDownloadFinished(int foreignId)
        {
            RequireTreesRebuild();
            if (AI.Config.tab == 0 && _selectedEntry != null && _selectedEntry.ForeignId == foreignId)
            {
                _selectedEntry.Refresh();
                _selectedEntry.PackageDownloader?.RefreshState();
            }
        }

        private async void OnPackageImageLoaded(Asset asset)
        {
            AssetInfo info = _assets?.FirstOrDefault(a => a.Id == asset.Id);
            if (info == null) return;

            await AssetUtils.LoadPackageTexture(info);
            _requireAssetTreeRebuild = true;
        }

        private void OnSceneLoaded(Scene scene, OpenSceneMode mode)
        {
            // otherwise previews will be empty
            _requireSearchUpdate = true;
            _requireAssetTreeRebuild = true;
        }

        private void ImportCompleted(string packageName)
        {
            OnImportDone();
        }

        private void OnRegisteredPackages(PackageRegistrationEventArgs obj)
        {
            OnImportDone();
        }

        private void OnImportDone()
        {
            AssetStore.GatherProjectMetadata();

            _requireLookupUpdate = ChangeImpact.ReadOnly;
            RequireTreesRebuild();
            _usageCalculationDone = false;
        }

        private void CleanupPreviewEditor()
        {
            _isCleaningUp = true;

            EditorApplication.delayCall -= HandleSearchSelectionChanged;
            _requireSearchSelectionUpdate = false;

            if (_previewEditor != null)
            {
                DestroyImmediate(_previewEditor);
                _previewEditor = null;
            }
        }

        private void OnBeforeAssemblyReload()
        {
            CleanupPreviewEditor();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            AudioTool.AudioManager.StopAudio();

            // Clean up preview editor before assembly reload to prevent PreviewRenderUtility leak
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                CleanupPreviewEditor();
            }

            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                // will crash editor otherwise
                _textureLoading?.Cancel();
                _textureLoading2?.Cancel();
                _textureLoading3?.Cancel();
            }

            // UI will have lost all preview textures during play mode
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                _requireSearchUpdate = true;
                _requireAssetTreeRebuild = true;
            }
        }

        private void ReloadLookups(bool force = true)
        {
            if (AI.DEBUG_MODE) Debug.LogWarning("Reload Lookups");

            _requireLookupUpdate = ChangeImpact.None;
            _resultSizes = new[] {"-all-", string.Empty, "10", "25", "50", "100", "250", "500", "1000", "1500", "2000", "2500", "3000", "4000", "5000"};
            _searchFields = new[] {"Asset Path", "File Name"};
            _sortFields = new[] {"-unsorted (fast)-", string.Empty, "Asset Path", "File Name", "Size", "Type", "Length", "Width", "Height", "Color", "Category", "Last Updated", "Rating", "#Reviews"};
            _packageSortOptions = Enum.GetNames(typeof (AssetTreeViewControl.Columns)).Select(StringUtils.CamelCaseToWords).ToArray();
            _groupByOptions = new[] {"-none-", string.Empty, "Category", "Publisher", "Tag", "State", "Location"};
            _colorOptions = new[] {"-all-", string.Empty, "matching"};
            _tileTitle = new[] {"-Intelligent-", "-None-", string.Empty, "Asset Path", "File Name", "File Name without Extension", "AI Caption or File Name"};
            _dependencyOptions = new[] {"-never-", string.Empty, "Upon Selection"};
            _scriptImportOptions = new[] {"-Never Import-", string.Empty, "Direct Only", "Extended Analysis", "All Scripts"};
            _previewOptions = new[] {"-all-", string.Empty, "Only With Preview", "Only Without Preview"};
            _doubleClickOptions = new[] {"-none-", string.Empty, "Import + Add to Scene", "Import", "Open"};
            _packageListingOptions = new[] {"-all-", "-all except registry packages-", "Only Asset Store Packages", "Only Registry Packages", "Only Custom Packages", "Only Media Folders", "Only Archives", "Only Asset Manager"};
            _packageListingOptionsShort = new[] {new GUIContent("All", ""), new GUIContent("No Reg", _packageListingOptions[1]), new GUIContent("Store", _packageListingOptions[2]), new GUIContent("Reg", _packageListingOptions[3]), new GUIContent("Cust", _packageListingOptions[4]), new GUIContent("Media", _packageListingOptions[5]), new GUIContent("Arch", _packageListingOptions[6]), new GUIContent("AM", _packageListingOptions[7])};
            _packageViewOptions = new[] {CommonUIStyles.IconContent("VerticalLayoutGroup Icon", "d_VerticalLayoutGroup Icon", "|List"), CommonUIStyles.IconContent("GridLayoutGroup Icon", "d_GridLayoutGroup Icon", "|Grid")};
            RefreshSearchScopeOptions();
            _deprecationOptions = new[] {"-all-", string.Empty, "Exclude Deprecated", "Show Only Deprecated", string.Empty, "Exclude Affected (China Store)", "Show Only Affected (China Store)"};
            _srpOptions = new[] {"-all-", "-current-", string.Empty, "BIRP", "URP", "HDRP"};
            _priceOptions = new[] {"-all-", "-free-", "-paid-", string.Empty, "<=", ">="};
            _maintenanceOptions = new[] {"-all-", string.Empty, "Update Available", "Outdated in Unity Cache", "Disabled by Unity", "Custom Asset Store Link", "Indexed", "Not Indexed", "Custom Registry", "Downloaded", "Downloading", "Not Downloaded", "Duplicate", "Marked for Backup", "Not Marked for Backup", "Marked for AI", "Not Marked for AI", "Deleted", "Excluded", "With Sub-Packages", "Incompatible Packages", "Fixable Incompatibilities", "Unfixable Incompatibilities"};
            _updateDateOptions = new[] {"-all-", string.Empty, "Last Week", "Last Month", "Last Year", string.Empty, "Before...", "After..."};
            _purchaseDateOptions = new[] {"-all-", string.Empty, "Last Week", "Last Month", "Last Year", string.Empty, "Before...", "After..."};
            _packageSizeOptions = new[] {"-all-", string.Empty, "<=", ">="};
            _unityVersionOptions = new[] {"-all-", string.Empty, "Unity 2019 or older", "Unity 2020 or older", "Unity 2021 or older", "Unity 2022 or older", "Unity 2023 or older", "Unity 6000 or older"};
            _importDestinationOptions = new[] {"Into Folder Selected in Project View", "Into Assets Root", "Into Specific Folder"};
            _importStructureOptions = new[] {"All Files Flat in Target Folder", "Keep Original Folder Structure"};
            _assetCacheLocationOptions = new[] {"Automatic", "Custom Folder"};
            _currencyOptions = new[] {"EUR", "USD", "CNY"};
            _logOptions = new[] {"Media Downloads", "Image Resizing", "Audio Parsing", "Package Parsing", "Custom Actions", "Preview Creation"};
            _blipOptions = new[] {"Small (1Gb)", "Large (1.8Gb)"};
            _aiBackendOptions = new[] {"Blip", "Ollama", "LM Studio"};
            _browserTypeOptions = new[] {"System Default", "Custom"};
            _imageTypeOptions = new List<string> {"-all-", string.Empty}.Concat(TextureNameSuggester.suffixPatterns.Keys.Select(StringUtils.CamelCaseToWords)).ToArray();
            _expertSearchFields = new List<string> {"-Add Field-", string.Empty}.Concat(assetFields).ToArray();

            UpdateStatistics(force);
            AssetStore.FillBufferOnDemand();

            _assetNames = Assets.ExtractAssetNames(_assets, true);
            _publisherNames = Assets.ExtractPublisherNames(_assets);
            _categoryNames = Assets.ExtractCategoryNames(_assets);
            _tagNames = Assets.ExtractTagNames(_tags);
            _tagPopupItems = Assets.ExtractTagPopupItems(_tags);

            _types = Assets.LoadTypes();
            if (!string.IsNullOrWhiteSpace(fixedSearchType))
            {
                _fixedSearchTypeIdx = Array.IndexOf(_types, fixedSearchType);
            }
        }

        private SearchScope GetConfiguredSearchScope()
        {
            SearchScope normalizedScope = SearchScopeModel.Normalize(AI.Config.searchScope, AI.Config.showIndexSearchScope);
            if ((int)normalizedScope != AI.Config.searchScope)
            {
                AI.Config.searchScope = (int)normalizedScope;
                AI.SaveConfig();
            }
            return normalizedScope;
        }

        private void RefreshSearchScopeOptions()
        {
            _searchScopeValues = SearchScopeModel.GetToolbarScopes(AI.Config.showIndexSearchScope);
            _searchScopeOptions = _searchScopeValues.Select(GetSearchScopeContent).ToArray();
        }

        private GUIContent GetSearchScopeContent(SearchScope searchScope)
        {
            switch (searchScope)
            {
                case SearchScope.Project:
                    return new GUIContent("Project", "Search only the current project");
                case SearchScope.Index:
                    return new GUIContent("Index", "Search only the index without scanning the current project");
                default:
                    return new GUIContent("All", "Search both index and current project");
            }
        }

        private bool DrawSearchScopeToolbar()
        {
            SearchScope currentScope = GetConfiguredSearchScope();
            RefreshSearchScopeOptions();

            int selectedIndex = Array.IndexOf(_searchScopeValues, currentScope);
            if (selectedIndex < 0) selectedIndex = 0;

            EditorGUI.BeginChangeCheck();
            selectedIndex = GUILayout.Toolbar(selectedIndex, _searchScopeOptions, GUILayout.Width(AI.Config.showIndexSearchScope ? 165 : 110), GUILayout.Height(20));
            if (!EditorGUI.EndChangeCheck()) return false;

            AI.Config.searchScope = (int)_searchScopeValues[selectedIndex];
            AI.SaveConfig();
            return true;
        }

        public void ReloadAfterDatabaseSwitch()
        {
            // Reset saved search caches - IDs are database-specific
            _searchesLoaded = false;
            _workspacesLoaded = false;
            _packageSearchesLoaded = false;
            _activeSavedSearchId = -1;
            _activeSavedPackageSearchId = -1;

            // Clear preview caches keyed by database IDs
            ClearFilePreviewCache();
            _cachedBackupState = null;

            ReloadLookups();
            PerformSearch();
        }

        [DidReloadScripts(2)]
        private static void DidReloadScripts()
        {
            _scriptsReloaded++;
            _cachedVersionMismatch = null; // Reset version check on domain reload
        }

        public override void OnGUI()
        {
            base.OnGUI();

            // Reset per-frame caches
            AI.ResetShowAdvancedCache();

            if (_scriptsReloaded > 0)
            {
                _requireAssetTreeRebuild = true;
                _requireReportTreeRebuild = true;
                _requireSearchUpdate = true;
                _requireLookupUpdate = ChangeImpact.Write; // DateTime not serialized properly, so we have to reload everything
                _scriptsReloaded--;
                _calculatingFolderSizes = false;
            }

            if (Application.isPlaying)
            {
                EditorGUILayout.HelpBox("The Asset Inventory is not available during play mode.", MessageType.Info);
                return;
            }

            _allowLogic = Event.current.type == EventType.Layout; // nothing must be changed during repaint

            if (AI.UICustomizationMode)
            {
                GUILayout.BeginHorizontal("box");
                EditorGUILayout.HelpBox("UI customization mode is active. Define which elements should be visible by default (green) and which only in advanced mode (red) when using the eye icon or holding CTRL. Yellow sections can be moved up and down.", MessageType.Warning);
                if (GUILayout.Button("Stop Customizing", CommonUIStyles.mainButton, GUILayout.ExpandWidth(false)))
                {
                    AI.UICustomizationMode = false;
                }
                GUILayout.EndHorizontal();
            }

            if (!_initDone)
            {
                QueueDelayedInit();
                EditorGUILayout.HelpBox("Asset Inventory is initializing.", MessageType.Info);
                return;
            }

            // Check for configuration errors
            if (ShowConfigurationErrorPane()) return;

            if (AI.UpgradeUtil.LongUpgradeRequired)
            {
                AI.UpgradeUtil.DrawUpgradeRequired();
                return;
            }

            if (_assets == null) UpdateStatistics(false);
            _importFolder = DetermineImportFolder();

            if (DragDropAvailable()) HandleDragDrop();

            if (_requireLookupUpdate != ChangeImpact.None || _resultSizes == null || _resultSizes.Length == 0)
            {
                ReloadLookups(_requireLookupUpdate == ChangeImpact.Write || _requireLookupUpdate == ChangeImpact.None);
            }
            if (_allowLogic)
            {
                if (_lastTileSizeChange != DateTime.MinValue && (DateTime.Now - _lastTileSizeChange).TotalMilliseconds > 300f)
                {
                    if (AI.Config.tileText == 0) _requireSearchUpdate = true; // only update search results if tile size influences displayed text
                    _lastTileSizeChange = DateTime.MinValue;
                }

                // don't perform more expensive checks every frame
                if ((DateTime.Now - _lastCheck).TotalSeconds > CHECK_INTERVAL)
                {
                    _availablePackageUpdates = _assets.Count(a => a.ParentId == 0 && a.IsUpdateAvailable(_assets, false));
                    _activePackageDownloads = AI.GetObserver().DownloadCount;
                    _lastCheck = DateTime.Now;
                }
            }

            // Check if we need to show setup view
            if (!AI.Config.wizardCompleted)
            {
                DrawSetupView();
                return;
            }

            bool isNewTab = false;
            if (!hideMainNavigation)
            {
                isNewTab = DrawToolbar();
                if (isNewTab) AudioTool.AudioManager.StopAudio();
                EditorGUILayout.Space();
            }
            else
            {
                AI.Config.tab = 0;
            }

            if (string.IsNullOrEmpty(CloudProjectSettings.accessToken))
            {
                EditorGUILayout.HelpBox("Asset Store connectivity is currently not possible. Please restart Unity and make sure you are logged in in the Unity hub.", MessageType.Warning);
                EditorGUILayout.Space();
            }

            // centrally handle project view selections since used in multiple views
            CheckProjectViewSelection();

            switch (AI.Config.tab)
            {
                case 0:
                    DrawSearchTab();
                    if (_allowLogic)
                    {
                        if (_requireSearchUpdate && AI.Config.searchAutomatically)
                        {
                            if (!_searchHandlerAdded || EditorApplication.delayCall == null)
                            {
                                _searchHandlerAdded = true;
                                EditorApplication.delayCall += () => PerformSearch(_keepSearchResultPage);
                            }
                        }
                        if (_requireSearchSelectionUpdate)
                        {
                            if (!_selectionHandlerAdded || EditorApplication.delayCall == null)
                            {
                                _selectionHandlerAdded = true;
                                EditorApplication.delayCall += HandleSearchSelectionChanged;
                            }
                        }
                    }
                    break;

                case 1:
                    // will have lost asset tree on reload due to missing serialization
                    // Only rebuild during Layout to avoid layout mismatches between Layout/Repaint
                    if (_requireAssetTreeRebuild && _allowLogic) CreateAssetTree();
                    DrawPackagesTab();
                    break;

                case 2:
                    if (_requireReportTreeRebuild && _allowLogic) CreateReportTree();
                    DrawReportingTab();
                    break;

                case 3:
                    if (isNewTab) EditorCoroutineUtility.StartCoroutineOwnerless(UpdateStatisticsDelayed());
                    DrawSettingsTab();
                    break;

                case 4:
                    DrawAboutTab();
                    break;
            }
        }

        private string DetermineImportFolder()
        {
            // determine import targets
            switch (AI.Config.importDestination)
            {
                case 0:
                    return _pvSelectedFolder;

                case 2:
                    return AI.Config.importFolder;

                default:
                    return "Assets";

            }
        }

        private void CheckProjectViewSelection()
        {
            if (_pvSelection != null && Selection.assetGUIDs != null && _pvSelection.SequenceEqual(Selection.assetGUIDs))
            {
                return;
            }

            _pvSelection = Selection.assetGUIDs;
            _pvSelectedPath = null;
            if (_pvSelection != null && _pvSelection.Length > 0)
            {
                _pvSelectedPath = AssetDatabase.GUIDToAssetPath(Selection.assetGUIDs[0]);
                if (_pvSelectedPath.StartsWith("Packages"))
                {
                    _pvSelectedPath = null;
                    _pvSelectedFolder = null;
                }
                else
                {
                    _pvSelectedFolder = Directory.Exists(_pvSelectedPath) ? _pvSelectedPath : Path.GetDirectoryName(_pvSelectedPath);
                    if (!string.IsNullOrWhiteSpace(_pvSelectedFolder)) _pvSelectedFolder = _pvSelectedFolder.Replace('/', Path.DirectorySeparatorChar);
                }
            }
        }

        private bool DrawToolbar()
        {
            GUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            string[] tabLabels = new string[]
            {
                "Search",
                "Packages",
                "Reporting",
                "Settings" + (AI.Actions.AnyActionsInProgress ? " (indexing)" : "")
            };
            AI.Config.tab = CommonUIStyles.DrawTabBar(AI.Config.tab, tabLabels);

            bool newTab = EditorGUI.EndChangeCheck();
            if (newTab && !hideMainNavigation) AI.SaveConfig();

            int iconSize = 18;
            string releaseDate = _onlineInfo?.version?.publishedDate != null ? _onlineInfo.version.publishedDate.Value.ToString() : "Unknown";
            if (_updateAvailable && _onlineInfo != null && GUILayout.Button(CommonUIStyles.Content($"v{_onlineInfo.version?.name} available!", $"Released {releaseDate}"), EditorStyles.linkLabel))
            {
                AI.OpenURL(AI.ASSET_STORE_LINK);
            }
            if (_activePackageDownloads > 0 && GUILayout.Button(EditorGUIUtility.IconContent("Loading", $"|{_activePackageDownloads} Downloads Active"), EditorStyles.label, GUILayout.Width(iconSize), GUILayout.Height(iconSize)))
            {
                AI.Config.tab = 1;
                _selectedMaintenance = PackageSearch.MaintenanceOption.Downloading;
                _requireAssetTreeRebuild = true;
                _packageInspectorTab = 1;
                AI.SaveConfig();
            }
            UILine("toolbar.showupdates", () =>
            {
                if (_availablePackageUpdates > 0 && GUILayout.Button(CommonUIStyles.IconContent("preAudioLoopOff", "Update-Available", $"|{_availablePackageUpdates} Updates Available"), EditorStyles.label, GUILayout.Width(iconSize), GUILayout.Height(iconSize)))
                {
                    ShowPackageMaintenance(PackageSearch.MaintenanceOption.UpdateAvailable);
                }
            });
            UILine("toolbar.toggleadvanced", () =>
            {
                if (GUILayout.Button(CommonUIStyles.IconContent(AI.Config.hideAdvanced ? "animationvisibilitytoggleoff" : "animationvisibilitytoggleon", AI.Config.hideAdvanced ? "d_animationvisibilitytoggleoff" : "d_animationvisibilitytoggleon", "|Visibility of Advanced Features" + (AI.Config.hideAdvanced ? " - Hold CTRL to show temporarily" : "")), EditorStyles.label, GUILayout.Width(iconSize), GUILayout.Height(iconSize)))
                {
                    AI.Config.hideAdvanced = !AI.Config.hideAdvanced;
                    AI.SaveConfig();
                }
            });
            UILine("toolbar.togglecustomization", () =>
            {
                if (GUILayout.Button(CommonUIStyles.IconContent("CustomTool", "d_CustomTool", "|Toggle UI Customization"), EditorStyles.label, GUILayout.Width(iconSize), GUILayout.Height(iconSize)))
                {
                    AI.UICustomizationMode = !AI.UICustomizationMode;
                }
            });
            UILine("toolbar.toggleabout", () =>
            {
                if (GUILayout.Button(EditorGUIUtility.IconContent("_Help", "|About"), EditorStyles.label, GUILayout.Width(iconSize), GUILayout.Height(iconSize)))
                {
                    if (_lastTab >= 0)
                    {
                        AI.Config.tab = _lastTab;
                    }
                    else
                    {
                        _lastTab = AI.Config.tab;
                        AI.Config.tab = 4;
                    }
                }
            });
            if (AI.Config.tab < 4) _lastTab = -1;
            GUILayout.EndHorizontal();
            EditorGUILayout.Space();

            return newTab;
        }

        private void ShowPackageMaintenance(PackageSearch.MaintenanceOption maintenanceOption)
        {
            AI.Config.tab = 1;
            _selectedMaintenance = maintenanceOption;
            _requireAssetTreeRebuild = true;
            _packageInspectorTab = 1;
            AI.SaveConfig();
        }

        private void ShowInterstitial()
        {
            if (EditorUtility.DisplayDialog("Your Support Counts", "This message will only appear once. Thanks for using Asset Inventory! I hope you enjoy using it.\n\n" +
                    "Developing a rather ground-braking asset like this as a solo-dev requires a huge amount of time and work.\n\n" +
                    "Please consider leaving a review and spreading the word. This is so important on the Asset Store and is the only way to make asset development viable.\n\n"
                    , "Leave Review", "Maybe Later"))
            {
                AI.OpenURL(AI.ASSET_STORE_LINK);
            }
        }

        private void GatherTreeChildren(int id, List<AssetInfo> result, HashSet<int> seen, TreeModel<AssetInfo> treeModel)
        {
            AssetInfo info = treeModel.Find(id);
            if (info == null) return;

            GatherTreeChildrenRecursive(info, result, seen);
        }

        private void GatherTreeChildrenRecursive(TreeElement node, List<AssetInfo> result, HashSet<int> seen)
        {
            if (node is AssetInfo info && info.Id != 0 && seen.Add(info.TreeId)) result.Add(info);
            if (node.HasChildren)
            {
                foreach (TreeElement child in node.Children)
                {
                    GatherTreeChildrenRecursive(child, result, seen);
                }
            }
        }

        private void HandleTagShortcuts()
        {
            if ((Event.current.modifiers & EventModifiers.Alt) != 0)
            {
                // Handle ALT+[0-9,a-z] shortcuts
                if (Event.current.type == EventType.KeyDown)
                {
                    KeyCode keyCode = Event.current.keyCode;
                    string keyStr = keyCode.ToString().ToLower();

                    // Convert Alpha1-Alpha9 to 1-9
                    if (keyStr.StartsWith("alpha")) keyStr = keyStr.Substring(5);

                    // Only process single character keys (letters or numbers)
                    if (keyStr.Length == 1 && char.IsLetterOrDigit(keyStr[0]))
                    {
                        // Find tag with matching hotkey
                        List<Tag> tags = Tagging.LoadTags();
                        Tag matchingTag = tags.Find(t => t.Hotkey == keyStr);
                        if (matchingTag != null)
                        {
                            bool isRemoving = (Event.current.modifiers & EventModifiers.Shift) != 0;
                            if (isRemoving)
                            {
                                // Remove tag from all selected assets that have it
                                switch (AI.Config.tab)
                                {
                                    case 0:
                                        Tagging.RemoveAssetAssignments(_sgrid.selectionItems, matchingTag.Name, true);
                                        CalculateSearchBulkSelection();
                                        break;

                                    case 1:
                                        Tagging.RemovePackageAssignments(_selectedTreeAssets, matchingTag.Name, true);
                                        break;
                                }
                            }
                            else
                            {
                                // Add tag to all selected assets that don't have it
                                switch (AI.Config.tab)
                                {
                                    case 0:
                                        Tagging.AddAssignments(_sgrid.selectionItems, matchingTag.Name, TagAssignment.Target.Asset, true);
                                        CalculateSearchBulkSelection();
                                        break;

                                    case 1:
                                        Tagging.AddAssignments(_selectedTreeAssets, matchingTag.Name, TagAssignment.Target.Package, true);
                                        break;
                                }
                            }

                            _requireAssetTreeRebuild = true;
                            Event.current.Use();
                        }
                    }
                }
            }
        }

        private bool ShowConfigurationErrorPane()
        {
            // Check for errors in priority order
            bool hasConfigErrors = AI.ConfigErrors.Count > 0;
            bool hasDbError = DBAdapter.DBError != null;
            bool hasVersionMismatch = false;

            // Check for database version mismatch (only if DB is accessible)
            // Only check once per domain reload for performance 
            if (!hasConfigErrors && !hasDbError)
            {
                if (_cachedVersionMismatch == null)
                {
                    try
                    {
                        AppProperty dbVersion = DBAdapter.DB.Find<AppProperty>("Version");
                        if (dbVersion != null && int.TryParse(dbVersion.Value, out int dbVersionNumber))
                        {
                            _cachedVersionMismatch = dbVersionNumber > UpgradeUtil.CURRENT_DB_VERSION;
                        }
                        else
                        {
                            _cachedVersionMismatch = false;
                        }
                    }
                    catch
                    {
                        // Database might not be accessible, ignore version check
                        _cachedVersionMismatch = false;
                    }
                }
                hasVersionMismatch = _cachedVersionMismatch.Value;
            }

            if (!hasConfigErrors && !hasDbError && !hasVersionMismatch) return false;

            // Generic error introduction
            GUILayout.BeginVertical("box");
            EditorGUILayout.Space();

            GUIStyle headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                wordWrap = true
            };
            EditorGUILayout.LabelField("Asset Inventory Cannot Start", headerStyle);
            EditorGUILayout.LabelField("Resolve the issues below first.", EditorStyles.wordWrappedLabel);

            EditorGUILayout.Space();
            EditorGUILayout.Space();

            // Show only one error type at a time, in priority order
            if (hasConfigErrors)
            {
                ShowConfigurationErrors();
            }
            else if (hasDbError)
            {
                ShowDatabaseError();
            }
            else if (hasVersionMismatch)
            {
                ShowDatabaseVersionError();
            }

            EditorGUILayout.Space();
            GUILayout.EndVertical();

            return true;
        }

        private void ShowConfigurationErrors()
        {
            EditorGUILayout.HelpBox("Configuration errors detected. These need to be fixed to proceed.", MessageType.Error);

            EditorGUILayout.Space();

            // Configuration location section
            GUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Configuration Location", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);
            EditorGUILayout.SelectableLabel(AI.UsedConfigLocation, EditorStyles.wordWrappedLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            if (GUILayout.Button("Open Folder", GUILayout.ExpandWidth(false)))
            {
                EditorUtility.RevealInFinder(AI.UsedConfigLocation);
            }
            GUILayout.EndVertical();

            EditorGUILayout.Space();

            // Error details section
            GUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Configuration Errors", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            foreach (string error in AI.ConfigErrors)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(10);
                EditorGUILayout.LabelField("?", GUILayout.Width(15));
                EditorGUILayout.LabelField(error, EditorStyles.wordWrappedLabel);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();

            EditorGUILayout.Space();

            // Action buttons
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Reload Settings", CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT), GUILayout.MinWidth(150)))
            {
                AI.ReInit();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void ShowDatabaseError()
        {
            string dbType = AI.Config.databaseType ?? DatabaseFactory.SQLITE;
            bool isMySQL = dbType == DatabaseFactory.MYSQL;

            EditorGUILayout.HelpBox(
                isMySQL
                    ? "The database connection failed. Please check your MySQL server settings and credentials."
                    : "The database could not be opened. It is probably corrupted. If you just installed the tool this might be caused by the database being on a network drive where syncing did not work properly. Delete and retry is the best option in that case.",
                MessageType.Error);

            EditorGUILayout.Space();

            // Database information section
            GUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Database Information", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            if (isMySQL)
            {
                EditorGUILayout.LabelField("Database Type:", "MySQL", EditorStyles.label);
                EditorGUILayout.LabelField("Host:", $"{AI.Config.mysqlHost}:{AI.Config.mysqlPort}");
                EditorGUILayout.LabelField("Database:", AI.Config.mysqlDatabase);
            }
            else
            {
                EditorGUILayout.LabelField("Database Type:", "SQLite", EditorStyles.label);
                EditorGUILayout.Space(2);
                EditorGUILayout.SelectableLabel(DBAdapter.GetDBPath(), EditorStyles.wordWrappedLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (GUILayout.Button("Open Folder", GUILayout.ExpandWidth(false)))
                {
                    EditorUtility.RevealInFinder(DBAdapter.GetDBPath());
                }
            }
            GUILayout.EndVertical();

            EditorGUILayout.Space();

            // Error details section
            GUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Error Details", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);
            GUILayout.BeginHorizontal();
            GUILayout.Space(10);
            EditorGUILayout.SelectableLabel(DBAdapter.DBError, EditorStyles.wordWrappedLabel, GUILayout.MinHeight(EditorGUIUtility.singleLineHeight * 2));
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            EditorGUILayout.Space();

            // Action buttons
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Configure Database...", CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT), GUILayout.MinWidth(150)))
            {
                DatabaseConfigurationUI.ShowWindow();
            }
            if (GUILayout.Button("Retry Connection", CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT), GUILayout.MinWidth(150)))
            {
                DBAdapter.Close();
                AI.ReInit();
            }
            if (!isMySQL && GUILayout.Button("Delete Database & Retry", GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT), GUILayout.MinWidth(180)))
            {
                DBAdapter.Close();
                File.Delete(DBAdapter.GetDBPath());
                AI.ReInit();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void ShowDatabaseVersionError()
        {
            string dbType = AI.Config.databaseType ?? DatabaseFactory.SQLITE;
            bool isMySQL = dbType == DatabaseFactory.MYSQL;

            // Get database version
            int dbVersionNumber = 0;
            try
            {
                AppProperty dbVersion = DBAdapter.DB.Find<AppProperty>("Version");
                if (dbVersion != null && int.TryParse(dbVersion.Value, out int parsedVersion))
                {
                    dbVersionNumber = parsedVersion;
                }
            }
            catch
            {
                // If we can't read the version, we shouldn't be here, but handle gracefully
            }

            EditorGUILayout.HelpBox(
                $"The database was created or used by a newer version of Asset Inventory (version {dbVersionNumber}) than the one you are currently using (supports up to version {UpgradeUtil.CURRENT_DB_VERSION}). Using an older tool version with a newer database can lead to data inconsistencies and is not supported. Please update Asset Inventory to the latest version to continue.",
                MessageType.Error);

            EditorGUILayout.Space();

            // Database information section
            GUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Database Information", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            EditorGUILayout.LabelField("Database Version:", dbVersionNumber > 0 ? dbVersionNumber.ToString() : "Unknown", EditorStyles.label);
            EditorGUILayout.LabelField("Supported Version:", UpgradeUtil.CURRENT_DB_VERSION.ToString(), EditorStyles.label);
            if (isMySQL)
            {
                EditorGUILayout.LabelField("Database Type:", "MySQL", EditorStyles.label);
                EditorGUILayout.LabelField("Host:", $"{AI.Config.mysqlHost}:{AI.Config.mysqlPort}");
                EditorGUILayout.LabelField("Database:", AI.Config.mysqlDatabase);
            }
            else
            {
                EditorGUILayout.LabelField("Database Type:", "SQLite", EditorStyles.label);
                EditorGUILayout.Space(2);
                EditorGUILayout.SelectableLabel(DBAdapter.GetDBPath(), EditorStyles.wordWrappedLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (GUILayout.Button("Open Folder", GUILayout.ExpandWidth(false)))
                {
                    EditorUtility.RevealInFinder(DBAdapter.GetDBPath());
                }
            }

            GUILayout.EndVertical();

            EditorGUILayout.Space();

            // Action buttons
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Retry Connection", CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT), GUILayout.MinWidth(150)))
            {
                DBAdapter.Close();
                AI.ReInit();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private CancellationToken InitBlockingToken()
        {
            _blockingInProgress = true;
            InitBlocking();
            return _extraction.Token;
        }

        private void DisposeBlocking()
        {
            _extraction?.Dispose();
            _blockingInProgress = false;
        }

        private void InitBlocking()
        {
            if (_extraction != null && !_extraction.IsDisposed()) _extraction?.Cancel();
            _extraction = new CancellationTokenSource();
        }

        private async Task CheckForToolUpdates()
        {
            _updateAvailable = false;

            await Task.Delay(2000); // let remainder of window initialize first
            if (string.IsNullOrEmpty(CloudProjectSettings.accessToken)) return;

            _onlineInfo = await AssetStore.RetrieveAssetDetails(AI.ASSET_STORE_ID, null, true);
            if (_onlineInfo == null) return;

            _updateAvailable = new SemVer(_onlineInfo.version.name) > new SemVer(AI.VERSION);
        }

        private async Task CheckForAssetUpdates()
        {
            await Task.Delay(2500); // let remainder of window initialize first

            if (!AI.IsInitialized) return; // Skip if initialization failed

            if (AI.Config.autoRefreshPurchases)
            {
                if (AI.Config.lastPurchasesUpdate != DateTime.MinValue && (DateTime.Now - AI.Config.lastPurchasesUpdate).TotalHours < AI.Config.purchasesRefreshPeriod)
                {
                    // no need to check again
                }
                else
                {
                    AI.Config.lastPurchasesUpdate = DateTime.Now;
                    AI.SaveConfig();

                    await AI.Actions.RunAction(ActionHandler.ACTION_ASSET_STORE_PURCHASES);
                }
            }

            if (AI.Config.autoRefreshMetadata)
            {
                if (AI.Config.lastMetadataUpdate != DateTime.MinValue && (DateTime.Now - AI.Config.lastMetadataUpdate).TotalHours < AI.Config.metadataTimeout)
                {
                    // no need to check again
                }
                else
                {
                    AI.Config.lastMetadataUpdate = DateTime.Now;
                    AI.SaveConfig();

                    await AI.Actions.RunAction(ActionHandler.ACTION_ASSET_STORE_DETAILS);
                }
            }
        }

        private void CreateDebugReport()
        {
            string reportFile = Path.Combine(Paths.GetStorageFolder(), "DebugReport.log");
            File.WriteAllText(reportFile, AI.CreateDebugReport());
            EditorUtility.RevealInFinder(reportFile);
        }

        private void OnInspectorUpdate()
        {
            // Only repaint when there's actual state change, avoiding unnecessary redraws
            if (_needsRepaint || _requireSearchUpdate || _requireAssetTreeRebuild || _requireReportTreeRebuild
                || _requireLookupUpdate != ChangeImpact.None || _blockingInProgress || _animationPlayer?.IsLoaded == true)
            {
                _needsRepaint = false;
                Repaint();
            }
        }

        // Shared saved search methods
        private delegate void OnSearchButtonClick();

        private delegate void OnSearchSettingsClick(GenericMenu menu);

        private int FindIndexByValue(string[] items, string value, bool splitPath = false)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;

            // Extract ID from value if it has brackets
            string valueId = null;
            int valueBracketStart = value.LastIndexOf('[');
            if (valueBracketStart > 0)
            {
                valueId = value.Substring(valueBracketStart + 1, value.Length - valueBracketStart - 2);
            }

            return Mathf.Max(0, Array.FindIndex(items, s =>
            {
                string itemToCheck = splitPath ? s.Split('/').LastOrDefault() : s;

                // If we have an ID, try to match by ID
                if (valueId != null)
                {
                    int itemBracketStart = itemToCheck.LastIndexOf('[');
                    if (itemBracketStart > 0)
                    {
                        string itemId = itemToCheck.Substring(itemBracketStart + 1, itemToCheck.Length - itemBracketStart - 2);
                        return itemId == valueId;
                    }
                }

                // Otherwise fall back to exact string match
                return itemToCheck == value;
            }));
        }

        private void RenderSavedSearchButton<T>(T search, int activeSearchId, string searchPhrase, OnSearchButtonClick onButtonClick, OnSearchSettingsClick onSettingsClick, ref float currentX, float availableWidth) where T : class
        {
            // Get properties via reflection or dynamic to work with both SavedSearch and SavedPackageSearch
            dynamic s = search;
            int searchId = s.Id;
            string searchName = s.Name;
            string searchIcon = s.Icon;
            string searchColor = s.Color;

            Color oldCol = GUI.backgroundColor;

            float buttonHeight = EditorStyles.miniButton.CalcSize(GUIContent.none).y;
            Vector2 oldIconSize = EditorGUIUtility.GetIconSize();
            EditorGUIUtility.SetIconSize(new Vector2(buttonHeight, buttonHeight));

            // Apply search color
            if (ColorUtility.TryParseHtmlString($"#{searchColor}", out Color color))
            {
                GUI.backgroundColor = color;
            }

            bool searchIsActive = searchId == activeSearchId;

            GUIContent content;
            if (string.IsNullOrWhiteSpace(searchName))
            {
                content = EditorGUIUtility.IconContent(searchIcon, "|" + searchPhrase);
            }
            else if (string.IsNullOrWhiteSpace(searchIcon))
            {
                content = CommonUIStyles.Content(searchName, searchPhrase);
            }
            else
            {
                content = CommonUIStyles.Content(searchName, EditorGUIUtility.IconContent(searchIcon, "|" + searchPhrase).image, searchPhrase);
            }

            // Calculate button width based on content
            Vector2 contentSize = EditorStyles.miniButton.CalcSize(content);
            EditorGUIUtility.SetIconSize(oldIconSize);
            float buttonWidth = contentSize.x + 20; // Add padding
            float settingsButtonWidth = 20;
            bool showDropdown = ShowAdvanced();
            float totalWidth = buttonWidth + (showDropdown ? settingsButtonWidth : 0) + 5;

            // Check if we need to wrap to next line
            if (currentX + totalWidth > availableWidth && currentX > 0)
            {
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Space(10);
                currentX = 10; // Reset to margin
            }

            if (GUILayout.Button(content, showDropdown ? EditorStyles.miniButtonLeft : EditorStyles.miniButton, GUILayout.Width(buttonWidth)))
            {
                onButtonClick();
            }

            if (showDropdown)
            {
                if (GUILayout.Button(EditorGUIUtility.IconContent("icon dropdown", "|Settings"), EditorStyles.miniButtonRight, GUILayout.Width(settingsButtonWidth)))
                {
                    GenericMenu menu = new GenericMenu();
                    onSettingsClick(menu);
                    menu.ShowAsContext();
                }
            }
            GUI.backgroundColor = oldCol;

            // Draw glowing border for active search after both buttons are drawn
            if (searchIsActive && Event.current.type == EventType.Repaint)
            {
                Rect mainButtonRect = GUILayoutUtility.GetLastRect();
                Rect combinedRect;
                if (showDropdown)
                {
                    Rect mainButtonRectCalculated = new Rect(mainButtonRect.x - buttonWidth, mainButtonRect.y, buttonWidth, mainButtonRect.height);
                    combinedRect = new Rect(
                        mainButtonRectCalculated.x - 3,
                        mainButtonRectCalculated.y - 3,
                        mainButtonRectCalculated.width + mainButtonRect.width + 6,
                        mainButtonRectCalculated.height + 6
                    );
                }
                else
                {
                    combinedRect = new Rect(
                        mainButtonRect.x - 3,
                        mainButtonRect.y - 3,
                        mainButtonRect.width + 6,
                        mainButtonRect.height + 6
                    );
                }

                Color borderColor = Color.white;
                Color oldColor = GUI.color;
                GUI.color = borderColor;

                // Top border
                GUI.Box(new Rect(combinedRect.x, combinedRect.y, combinedRect.width, 4), "");
                // Bottom border
                GUI.Box(new Rect(combinedRect.x, combinedRect.y + combinedRect.height - 4, combinedRect.width, 4), "");
                // Left border
                GUI.Box(new Rect(combinedRect.x, combinedRect.y, 4, combinedRect.height), "");
                // Right border
                GUI.Box(new Rect(combinedRect.x + combinedRect.width - 4, combinedRect.y, 4, combinedRect.height), "");

                GUI.color = oldColor;
            }

            GUILayout.Space(5);
            currentX += totalWidth + 5;
        }
    }
}
