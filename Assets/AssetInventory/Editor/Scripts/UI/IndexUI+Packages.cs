using ImpossibleRobert.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEditor.PackageManager;
using UnityEngine;
using static AssetInventory.AssetTreeViewControl;
using Debug = UnityEngine.Debug;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
#if UNITY_6000_2_OR_NEWER
using BaseTreeViewState = UnityEditor.IMGUI.Controls.TreeViewState<int>;
#else
using BaseTreeViewState = UnityEditor.IMGUI.Controls.TreeViewState;
#endif

namespace AssetInventory
{
    public partial class IndexUI
    {
        private static readonly ProfilerMarker ProfileMarkerBulk = new ProfilerMarker("Bulk Download State");

        private PackageSearch.MaintenanceOption _selectedMaintenance;
        private int _visiblePackageCount;
        private int _selectedMedia;
        private string _assetSearchPhrase;

        private int _usedByCacheAssetId;
        private List<PackageInfo> _usedByCache;

        // New filter fields (prefixed with Pkg to avoid conflicts with Search fields)
        private int _selectedPkgTag;
        private int _selectedPkgPublisher;
        private int _selectedPkgCategory;
        private int _selectedPkgPriceOption;
        private float _pkgSearchPrice;
        private int _selectedPkgUpdateDateOption;
        private DateTime? _pkgUpdateBeforeDate;
        private DateTime? _pkgUpdateAfterDate;
        private int _selectedPkgPurchaseDateOption;
        private DateTime? _pkgPurchaseBeforeDate;
        private DateTime? _pkgPurchaseAfterDate;
        private int _selectedPkgSizeOption;
        private float _pkgSizeMB;
        private int _selectedPkgUnityVersionOption;
        private string[] _updateDateOptions;
        private string[] _purchaseDateOptions;
        private string[] _packageSizeOptions;
        private string[] _unityVersionOptions;

        // Saved package searches
        private int _activeSavedPackageSearchId = -1;
        private List<SavedPackageSearch> _packageSearches;
        private bool _packageSearchesLoaded;

        private List<SavedPackageSearch> PackageSearches
        {
            get
            {
                if (_packageSearches == null || !_packageSearchesLoaded)
                {
                    _packageSearches = DBAdapter.DB.Table<SavedPackageSearch>().ToList();
                    _packageSearchesLoaded = true;
                }
                return _packageSearches;
            }
        }

        private Vector2 _assetsScrollPos;
        private Vector2 _bulkScrollPos;
        private Vector2 _imageScrollPos;
        private Rect _mediaRect;
        private float _nextAssetSearchTime;
        private bool _mouseOverPackageTreeRect;

        private Vector2 _packageScrollPos;
        private GridControl PGrid
        {
            get
            {
                if (_pgrid == null)
                {
                    _pgrid = new GridControl();
                    _pgrid.OnDoubleClick += OnPackageGridDoubleClicked;
                    _pgrid.OnKeyboardSelection += OnPackageGridKeyboardSelection;
                    _pgrid.OnContextMenuPopulate += PopulatePackageGridContextMenu;
                }
                return _pgrid;
            }
        }
        private GridControl _pgrid;

        private SearchField AssetSearchField => _assetSearchField = _assetSearchField ?? new SearchField();
        private SearchField _assetSearchField;

        [SerializeField] private MultiColumnHeaderState assetMchState;
        private TreeViewWithTreeModel<AssetInfo> AssetTreeView
        {
            get
            {
#pragma warning disable CS0618 // Type or member is obsolete
                if (_assetTreeViewState == null) _assetTreeViewState = new BaseTreeViewState();
#pragma warning restore CS0618 // Type or member is obsolete

                if (_assetTreeView == null)
                {
                    // Calculate available width dynamically (accounting for inspector width)
                    float availableWidth = position.width - GetVisiblePackageInspectorWidth() - 40; // 40 for margins
                    if (availableWidth < 300) availableWidth = 300; // minimum width

                    bool hadSerializedState = assetMchState != null;
                    MultiColumnHeaderState headerState = CreateDefaultMultiColumnHeaderState(availableWidth);
                    headerState = GetUsableMultiColumnHeaderState(assetMchState, headerState);
                    RestoreColumnWidths(headerState, AI.Config.packageTreeColumnWidths);
                    if (AI.Config.visiblePackageTreeColumns != null && AI.Config.visiblePackageTreeColumns.Length > 0)
                    {
                        // Ensure Name column is always first
                        List<int> cols = new List<int>(AI.Config.visiblePackageTreeColumns);
                        int nameColumnIndex = (int)Columns.Name;
                        if (cols.Contains(nameColumnIndex) && cols[0] != nameColumnIndex)
                        {
                            cols.Remove(nameColumnIndex);
                            cols.Insert(0, nameColumnIndex);
                        }
                        else if (!cols.Contains(nameColumnIndex))
                        {
                            cols.Insert(0, nameColumnIndex);
                        }
                        headerState.visibleColumns = cols.ToArray();
                    }
                    else
                    {
                        headerState.visibleColumns = new[] {(int)Columns.Name, (int)Columns.Tags, (int)Columns.Version, (int)Columns.Indexed};
                    }
                    assetMchState = headerState;

                    ReorderableMultiColumnHeader mch = new ReorderableMultiColumnHeader(headerState);
                    mch.canSort = true;
                    mch.height = MultiColumnHeader.DefaultGUI.minimumHeight;

                    mch.visibleColumnsChanged += OnVisibleAssetTreeColumnsChanged;
                    mch.sortingChanged += OnAssetTreeSortingChanged;
                    mch.columnOrderChanged += OnAssetTreeColumnOrderChanged;
                    if (!hadSerializedState && AI.Config.packageTreeColumnWidths == null) mch.ResizeToFit();

                    _assetTreeView = new AssetTreeViewControl(_assetTreeViewState, mch, AssetTreeModel);
                    _assetTreeView.OnSelectionChanged += OnAssetTreeSelectionChanged;
                    _assetTreeView.OnDoubleClickedItem += OnAssetTreeDoubleClicked;
                    _assetTreeView.OnContextMenuPopulate += PopulatePackageGridContextMenu;
                    _assetTreeView.Reload();
                }
                return _assetTreeView;
            }
        }

        private void OnAssetTreeSortingChanged(MultiColumnHeader mch)
        {
            AI.Config.assetSorting = mch.sortedColumnIndex;
            AI.Config.sortAssetsDescending = !mch.IsSortedAscending(mch.sortedColumnIndex);
            AI.SaveConfig();
            CreateAssetTree();
        }

        private void OnVisibleAssetTreeColumnsChanged(MultiColumnHeader mch)
        {
            ((ReorderableMultiColumnHeader)mch).SmartResizeToFit();

            AI.Config.visiblePackageTreeColumns = mch.state.visibleColumns;
            AI.Config.packageTreeColumnWidths = ExtractColumnWidths(assetMchState);
            AI.SaveConfig();
        }

        private void OnAssetTreeColumnOrderChanged(int[] newOrder)
        {
            AI.Config.visiblePackageTreeColumns = newOrder;
            AI.Config.packageTreeColumnWidths = ExtractColumnWidths(assetMchState);
            AI.SaveConfig();
        }

        private void SaveColumnWidths()
        {
            AI.Config.packageTreeColumnWidths = ExtractColumnWidths(assetMchState);
            AI.Config.searchTreeColumnWidths = ExtractColumnWidths(searchMchState);
            AI.Config.reportTreeColumnWidths = ExtractColumnWidths(reportMchState);
            AI.SaveConfig();
        }

        private static float[] ExtractColumnWidths(MultiColumnHeaderState mchState)
        {
            if (mchState == null || mchState.columns.Length == 0) return null;

            float[] widths = new float[mchState.columns.Length];
            for (int i = 0; i < mchState.columns.Length; i++)
            {
                widths[i] = mchState.columns[i].width;
            }
            return widths;
        }

        private static void RestoreColumnWidths(MultiColumnHeaderState headerState, float[] savedWidths)
        {
            if (savedWidths == null) return;
            if (savedWidths.Length != headerState.columns.Length) return;

            for (int i = 0; i < savedWidths.Length; i++)
            {
                headerState.columns[i].width = savedWidths[i];
            }
        }

        private AssetTreeViewControl _assetTreeView;
#pragma warning disable CS0618 // Type or member is obsolete
        private BaseTreeViewState _assetTreeViewState;
#pragma warning restore CS0618 // Type or member is obsolete

        private TreeModel<AssetInfo> AssetTreeModel
        {
            get
            {
                if (_assetTreeModel == null) _assetTreeModel = new TreeModel<AssetInfo>(new List<AssetInfo> {new AssetInfo().WithTreeData("Root", depth: -1)});
                return _assetTreeModel;
            }
        }
        private TreeModel<AssetInfo> _assetTreeModel;

        private AssetInfo _selectedTreeAsset;
        private List<AssetInfo> _selectedTreeAssets;

        private Dictionary<int, List<BackupInfo>> _cachedBackupState;

        private long _assetTreeSelectionSize;
        private long _assetTreeSubPackageCount;
        private float _assetTreeSelectionTotalCosts;
        private float _assetTreeSelectionStoreCosts;
        private readonly Dictionary<string, Tuple<int, Color>> _assetBulkTags = new Dictionary<string, Tuple<int, Color>>();
        private int _selectionGeneration;
        private int _packageDetailsTab;
        private bool _metadataEditMode;
        private int _packageInspectorTab;

        private void OnPackageListUpdated()
        {
            if (!AI.IsInitialized) return;
            if (_assets == null) return;

            _usedByCacheAssetId = 0;
            _usedByCache = null;

            RequireTreesRebuild();

            Dictionary<string, PackageInfo> packages = AssetStore.GetAllPackages();
            bool hasChanges = false;
            foreach (KeyValuePair<string, PackageInfo> package in packages)
            {
                AssetInfo info = _assets.FirstOrDefault(a => a.AssetSource == Asset.Source.RegistryPackage && a.SafeName == package.Value.name);
                if (info == null)
                {
                    // new package found, persist
                    if (PackageImporter.Persist(package.Value))
                    {
                        hasChanges = true;
                    }
                    continue;
                }

                info.Refresh();
                if (package.Value.versions.latestCompatible != info.LatestVersion && !package.Value.versions.latestCompatible.ToLowerInvariant().Contains("pre"))
                {
                    AI.SetPackageVersion(info, package.Value);
                    hasChanges = true;
                }
            }
            if (hasChanges)
            {
                _requireLookupUpdate = ChangeImpact.Write;
                RequireTreesRebuild();
                CalculateAssetUsageAutomatically();
            }
            else if (!_usageCalculationDone)
            {
                CalculateAssetUsageAutomatically();
            }
        }

        private void OnTagsChanged()
        {
            _tags = Tagging.LoadTags();
            _tagNames = Assets.ExtractTagNames(_tags);
            _tagPopupItems = Assets.ExtractTagPopupItems(_tags);

            _requireAssetTreeRebuild = true;
        }

        private void OnActionsDone()
        {
            ReloadLookups();
            RequireTreesRebuild();
        }

        private void DrawPackageDownload(AssetInfo info, bool updateMode = false)
        {
            AssetInfo root = info.GetRoot();
            if (!string.IsNullOrEmpty(root.OriginalLocation) && root.UploadId > 0)
            {
                if (!updateMode)
                {
                    if (root.IsLocationUnmappedRelative())
                    {
                        EditorGUILayout.HelpBox("The location of this package is stored relative and no mapping has been done yet in the settings for this system.", MessageType.Warning);
                    }
                    else if (string.IsNullOrWhiteSpace(root.DownloadedActual))
                    {
                        if (root.PackageSize > 0)
                        {
                            EditorGUILayout.HelpBox($"Not cached currently. Download the asset to access its content ({EditorUtility.FormatBytes(root.PackageSize)}).", MessageType.Warning);
                        }
                        else
                        {
                            EditorGUILayout.HelpBox("Not cached currently. Download the asset to access its content.", MessageType.Warning);
                        }
                    }
                    else
                    {
                        EditorGUILayout.HelpBox($"Cache currently contains version {root.DownloadedActual} of a different listing for this package. Download this package to override it.", MessageType.Warning);
                    }
                }

                if (!root.IsDownloadedCompatible)
                {
                    if (root.IsCurrentUnitySupported())
                    {
                        UIBlock("package.hints.incompatibledownload", () => EditorGUILayout.HelpBox($"The cached package is meant for a newer version of Unity ({root.DownloadededUnityVersion}) and might be incompatible. Try downloading the package again to get a compatible version if available.", MessageType.Warning));
                    }
                    else
                    {
                        UIBlock("package.hints.incompatibledownload", () => EditorGUILayout.HelpBox($"The cached package is meant for a newer version of Unity ({root.DownloadededUnityVersion}) and might be incompatible.", MessageType.Warning));
                    }
                }

                if (root.ParentId == 0 && root.PackageDownloader != null)
                {
                    AssetDownloadState state = root.PackageDownloader.GetState();
                    switch (state.state)
                    {
                        case AssetDownloader.State.Downloading:
                            GUILayout.BeginHorizontal();
                            CommonUIStyles.DrawProgressBar(state.progress, $"{EditorUtility.FormatBytes(state.bytesDownloaded)}");
                            if (GUILayout.Button(EditorGUIUtility.IconContent("d_PauseButton On", "|Pause"), GUILayout.ExpandWidth(false)))
                            {
                                root.PackageDownloader.PauseDownload(false);
                            }
                            if (GUILayout.Button(EditorGUIUtility.IconContent("d_PreMatQuad", "|Abort"), GUILayout.ExpandWidth(false)))
                            {
                                root.PackageDownloader.PauseDownload(true);
                            }
                            GUILayout.EndHorizontal();
                            break;

                        case AssetDownloader.State.Unavailable:
                            if (root.PackageDownloader.IsDownloadSupported() && GUILayout.Button("Download", CommonUIStyles.mainButton))
                            {
                                root.PackageDownloader.Download(false);
                            }
                            break;

                        case AssetDownloader.State.Paused:
                            if (root.PackageDownloader.IsDownloadSupported() && GUILayout.Button("Resume Download" + (root.PackageSize > 0 ? $" ({EditorUtility.FormatBytes(root.PackageSize - root.PackageDownloader.GetState().bytesDownloaded)})" : ""), CommonUIStyles.mainButton))
                            {
                                root.PackageDownloader.Download(false);
                            }
                            break;

                        case AssetDownloader.State.UpdateAvailable:
                            if (root.PackageDownloader.IsDownloadSupported() && GUILayout.Button("Download Update" + (root.PackageSize > 0 ? $" ({EditorUtility.FormatBytes(root.PackageSize)})" : ""), CommonUIStyles.mainButton))
                            {
                                root.WasOutdated = true;
                                root.PackageDownloader.SetAsset(root);
                                root.PackageDownloader.Download(false);
                            }
                            break;

                        case AssetDownloader.State.Downloaded:
                            if (!root.IsDownloadedCompatible && root.IsCurrentUnitySupported())
                            {
                                root.PackageDownloader.SetAsset(root);
                                if (root.PackageDownloader.IsDownloadSupported() && GUILayout.Button("Download", CommonUIStyles.mainButton))
                                {
                                    root.PackageDownloader.Download(false);
                                }
                            }
                            break;
                    }
                }
            }
            else
            {
                if (!updateMode)
                {
                    if (info.IsLocationUnmappedRelative())
                    {
                        EditorGUILayout.HelpBox("The location of this package is stored relative and no mapping has been done yet in the settings for this system.", MessageType.Warning);
                    }
                    else if (info.AssetSource == Asset.Source.CustomPackage && !File.Exists(info.GetLocation(true)))
                    {
                        EditorGUILayout.HelpBox("The custom package has been deleted and is not available anymore.", MessageType.Warning);
                    }
                    else
                    {
                        EditorGUILayout.HelpBox("This package is new and metadata has not been collected yet. Update the index to have all metadata up to date.", MessageType.Warning);
                        if (GUILayout.Button(CommonUIStyles.Content("Load Metadata"))) _ = AI.Actions.FetchAssetDetails(true, info.AssetId);
                    }
                }
                else if (info.AssetSource == Asset.Source.CustomPackage)
                {
                    UIBlock("package.hints.noautoupdate", () => EditorGUILayout.HelpBox("Automatic update not possible since package is not from the Asset Store.", MessageType.Info));
                }
            }
        }

        private void DrawPackageInfo(AssetInfo info, bool showMaintenance = false, bool showActions = true, bool startNewSection = true)
        {
            if (info.AssetId == 0)
            {
                EditorGUILayout.HelpBox("This asset has no package association anymore. Use the maintenance wizard to clean up such orphaned files.", MessageType.Error);
                return;
            }

            AssetInfo root = info.GetRoot();
            bool showExpanded = (AI.Config.expandPackageDetails || AI.Config.alwaysShowPackageDetails) && AI.Config.tab == 1;
            List<string> sections = new List<string>();
            if (showExpanded)
            {
                List<Dependency> pDeps = info.GetPackageDependencies();
                List<AssetInfo> pInvDeps = info.GetPackageUsageDependencies(_assets);
                if (info.Media != null && info.Media.Count > 0) sections.Add("Media");
                if (!string.IsNullOrWhiteSpace(info.Description)) sections.Add("About");
                if (!string.IsNullOrWhiteSpace(info.ReleaseNotes)) sections.Add(AI.Config.expandPackageDetails || !AI.Config.projectDetailTabs ? "Release Notes" : "Release");
                if (info.AssetSource == Asset.Source.RegistryPackage || pDeps != null || pInvDeps != null) sections.Add(AI.Config.expandPackageDetails || !AI.Config.projectDetailTabs ? "Dependencies" : "Deps");
            }

            if (startNewSection)
            {
                GUILayout.BeginVertical();
            }

            List<string> order = AI.Config.GetSection("package").sections;
            for (int i = 0; i < order.Count; i++)
            {
                string section = order[i];
                switch (section.ToLowerInvariant())
                {
                    case "packagedata":
                        UISection("package", section, () =>
                        {
                            DrawPackageData(info, showMaintenance, showActions);
                        });
                        break;

                    case "tabbeddetails":
                        if (showExpanded && AI.Config.projectDetailTabs)
                        {
                            UISection("package", section, () =>
                            {
                                DrawTabbedPackageDetails(info, sections);
                            });
                        }
                        break;

                    case "media":
                        if (showExpanded && !AI.Config.projectDetailTabs)
                        {
                            if (sections.Contains("Media"))
                            {
                                UISection("package", section, () =>
                                {
                                    UIBlock("package.media", () =>
                                    {
                                        EditorGUILayout.LabelField("Media", EditorStyles.boldLabel);
                                        ShowMediaDetails(info);
                                        EditorGUILayout.Space();
                                    });
                                });
                            }
                        }
                        break;

                    case "description":
                        if (showExpanded && !AI.Config.projectDetailTabs)
                        {
                            if (sections.Contains("Description") || sections.Contains("About"))
                            {
                                UISection("package", section, () =>
                                {
                                    UIBlock("package.description", () =>
                                    {
                                        EditorGUILayout.LabelField("Description", EditorStyles.boldLabel);
                                        ShowDescriptionDetails(info);
                                        EditorGUILayout.Space();
                                    });
                                });
                            }
                        }
                        break;

                    case "releasenotes":
                        if (showExpanded && !AI.Config.projectDetailTabs)
                        {
                            if (sections.Contains("Release Notes") || sections.Contains("Release"))
                            {
                                UISection("package", section, () =>
                                {
                                    UIBlock("package.releasenotes", () =>
                                    {
                                        EditorGUILayout.LabelField("Release Notes", EditorStyles.boldLabel);
                                        ShowReleaseNotesDetails(info);
                                        EditorGUILayout.Space();
                                    });
                                });
                            }
                        }
                        break;

                    case "dependencies":
                        if (showExpanded && !AI.Config.projectDetailTabs)
                        {
                            if (sections.Contains("Dependencies") || sections.Contains("Deps"))
                            {
                                UISection("package", section, () =>
                                {
                                    UIBlock("package.dependencies", () =>
                                    {
                                        EditorGUILayout.LabelField("Dependencies", EditorStyles.boldLabel);
                                        ShowDependencyDetails(info);
                                        EditorGUILayout.Space();
                                    });
                                });
                            }
                        }
                        break;

                }
                EditorGUILayout.Space(10);
            }

            if (!showExpanded)
            {
                // highly condensed view
                if (root.PreviewTexture != null)
                {
                    UIBlock("package.icon", () =>
                    {
                        EditorGUILayout.Space();
                        GUILayout.FlexibleSpace();
                        DrawPackagePreview(root);
                        GUILayout.FlexibleSpace();
                        EditorGUILayout.Space();
                    });
                }
                else if (info.AssetSource == Asset.Source.RegistryPackage && !string.IsNullOrWhiteSpace(info.Description))
                {
                    UIBlock("package.description", () =>
                    {
                        EditorGUILayout.Space();
                        EditorGUILayout.LabelField(info.Description, EditorStyles.wordWrappedLabel);
                    });
                }
            }

            if (startNewSection) GUILayout.EndVertical();
        }

        private void DrawTabbedPackageDetails(AssetInfo info, List<string> sections)
        {
            _packageDetailsTab = CommonUIStyles.DrawTabBar(_packageDetailsTab, sections.ToArray());
            EditorGUILayout.Space();
            if (_packageDetailsTab > sections.Count - 1) _packageDetailsTab = sections.Count - 1;
            if (_packageDetailsTab < 0)
            {
                _packageDetailsTab = 0;
                return;
            }
            switch (sections[_packageDetailsTab])
            {
                case "About":
                case "Description":
                    ShowDescriptionDetails(info);
                    break;

                case "Release":
                case "Releases":
                case "Release Notes":
                    ShowReleaseNotesDetails(info);
                    break;

                case "Media":
                    ShowMediaDetails(info);
                    break;

                case "Deps":
                case "Dependencies":
                    ShowDependencyDetails(info);
                    break;

            }
        }

        private void DrawPackageData(AssetInfo info, bool showMaintenance, bool showActions)
        {
            bool mainUsed = false;
            int labelWidth = 95;
            AssetInfo root = info.GetRoot();

            EditorGUILayout.LabelField("Package", EditorStyles.largeLabel);
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();
            if (info.AssetSource == Asset.Source.AssetManager)
            {
                UIBlock("package.organization", () =>
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Organization", EditorStyles.boldLabel, GUILayout.Width(labelWidth - 2));
                    if (GUILayout.Button(CommonUIStyles.Content(info.OriginalLocation), CommonUIStyles.wrappedLinkLabel, GUILayout.ExpandWidth(true)))
                    {
                        AI.OpenURL(info.GetAMOrganizationUrl());
                    }
                    GUILayout.EndHorizontal();
                });

                UIBlock("package.project", () =>
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Project", EditorStyles.boldLabel, GUILayout.Width(labelWidth - 2));
                    if (GUILayout.Button(CommonUIStyles.Content(info.ToAsset().GetRootAsset().DisplayName), CommonUIStyles.wrappedLinkLabel, GUILayout.ExpandWidth(true)))
                    {
                        AI.OpenURL(info.GetAMProjectUrl());
                    }
                    GUILayout.EndHorizontal();
                });

                if (info.ParentId > 0)
                {
                    UIBlock("package.collection", () =>
                    {
                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField("Collection", EditorStyles.boldLabel, GUILayout.Width(labelWidth - 2));
                        if (GUILayout.Button(CommonUIStyles.Content(info.GetDisplayName()), CommonUIStyles.wrappedLinkLabel, GUILayout.ExpandWidth(true)))
                        {
                            AI.OpenURL(info.GetAMCollectionUrl());
                        }
                        GUILayout.EndHorizontal();
                    });
                }
            }
            else
            {
                GUILabelWithText("Name", info.GetDisplayName(), labelWidth, info.Location, true);
            }
            if (info.AssetSource == Asset.Source.RegistryPackage)
            {
                UIBlock("package.id", () => GUILabelWithText("Id", info.SafeName, labelWidth, info.SafeName, true));

                if (info.PackageSource == PackageSource.Local)
                {
                    string version = info.GetVersion(true);
                    UIBlock("package.version", () => GUILabelWithText("Version", string.IsNullOrWhiteSpace(version) ? "-none-" : version, labelWidth));
                }
                else
                {
                    UIBlock("package.version", () =>
                    {
                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField("Version", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                        if (EditorGUILayout.DropdownButton(AssetStore.IsInstalled(info) ? CommonUIStyles.Content(info.InstalledPackageVersion(), "Version to use") : CommonUIStyles.Content("Not installed, select version"), FocusType.Keyboard, GUILayout.ExpandWidth(true), GUILayout.MaxWidth(300)))
                        {
                            VersionSelectionUI versionUI = new VersionSelectionUI();
                            versionUI.Init(info, newVersion =>
                            {
                                InstallPackage(info, newVersion);
                            });
                            PopupWindow.Show(GetPopupPositionAtMouse(), versionUI);
                        }
                        if (AssetStore.IsInstalled(info))
                        {
                            string changeLogURL = info.GetChangeLogURL(info.InstalledPackageVersion());
                            if (!string.IsNullOrWhiteSpace(changeLogURL))
                            {
                                if (GUILayout.Button(CommonUIStyles.Content("?", "Changelog"), GUILayout.Width(20)))
                                {
                                    AI.OpenURL(changeLogURL);
                                }
                            }
                        }
                        GUILayout.EndHorizontal();
                    });

                    UIBlock("package.updatestrategy", () =>
                    {
                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField("Updates", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                        EditorGUI.BeginChangeCheck();
                        info.UpdateStrategy = (Asset.Strategy)EditorGUILayout.EnumPopup(info.UpdateStrategy, GUILayout.ExpandWidth(true), GUILayout.MaxWidth(300));
                        if (EditorGUI.EndChangeCheck())
                        {
                            AI.SetAssetUpdateStrategy(info, info.UpdateStrategy);
                            _requireAssetTreeRebuild = true;
                        }
                        GUILayout.EndHorizontal();
                    });
                }
            }
            else if ((info.AssetSource == Asset.Source.AssetStorePackage || info.AssetSource == Asset.Source.CustomPackage) && info.ForeignId > 0)
            {
                // Unity package version selection when backups exist
                if (_cachedBackupState != null && _cachedBackupState.TryGetValue(info.ForeignId, out List<BackupInfo> backupVersions) && backupVersions != null && backupVersions.Count > 0)
                {
                    UIBlock("package.version", () =>
                    {
                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField("Version", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                        string currentVersion = info.GetVersion(true);
                        string selectedVersion = info.ForcedUnityPackageVersion;
                        string displayVersion = !string.IsNullOrWhiteSpace(selectedVersion) ? selectedVersion : currentVersion;
                        string buttonText = !string.IsNullOrWhiteSpace(displayVersion) ? displayVersion : "Select version";
                        if (backupVersions.Count > 1)
                        {
                            if (EditorGUILayout.DropdownButton(CommonUIStyles.Content(buttonText, "Select backup version"), FocusType.Keyboard, GUILayout.ExpandWidth(true), GUILayout.MaxWidth(300)))
                            {
                                UnityPackageVersionSelectionUI versionUI = new UnityPackageVersionSelectionUI();
                                versionUI.Init(info, _cachedBackupState, newVersion =>
                                {
                                    info.ForcedUnityPackageVersion = newVersion;
                                    _requireAssetTreeRebuild = true;
                                });
                                PopupWindow.Show(GetPopupPositionAtMouse(), versionUI);
                            }
                        }
                        else
                        {
                            EditorGUILayout.LabelField(displayVersion ?? "-none-", GUILayout.ExpandWidth(true));
                        }
                        GUILayout.EndHorizontal();
                    });
                }
                else
                {
                    // Show current version if no backups available
                    string version = info.GetVersion(true);
                    if (!string.IsNullOrWhiteSpace(version))
                    {
                        UIBlock("package.version", () => GUILabelWithText("Version", version, labelWidth));
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(info.License))
            {
                UIBlock("package.license", () =>
                {
                    if (!string.IsNullOrWhiteSpace(info.LicenseLocation))
                    {
                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField("License", EditorStyles.boldLabel, GUILayout.Width(labelWidth - 2));
                        if (GUILayout.Button(CommonUIStyles.Content(info.License), CommonUIStyles.wrappedLinkLabel, GUILayout.ExpandWidth(true)))
                        {
                            AI.OpenURL(info.LicenseLocation);
                        }
                        GUILayout.EndHorizontal();
                    }
                    else
                    {
                        GUILabelWithText("License", $"{info.License}");
                    }
                });
            }

            if (!string.IsNullOrWhiteSpace(info.GetDisplayPublisher()))
            {
                UIBlock("package.publisher", () =>
                {
                    if (info.PublisherId > 0)
                    {
                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField("Publisher", EditorStyles.boldLabel, GUILayout.Width(labelWidth - 2));
                        if (GUILayout.Button(CommonUIStyles.Content(info.GetDisplayPublisher()), CommonUIStyles.wrappedLinkLabel, GUILayout.ExpandWidth(true)))
                        {
                            AI.OpenStoreURL(info.GetPublisherLink());
                        }
                        GUILayout.EndHorizontal();
                    }
                    else
                    {
                        GUILabelWithText("Publisher", $"{info.GetDisplayPublisher()}", 95, null, true);
                    }
                });
            }
            if (!string.IsNullOrWhiteSpace(info.GetDisplayCategory())) UIBlock("package.category", () => GUILabelWithText("Category", $"{info.GetDisplayCategory()}", 95, null, true));
            if (info.PackageSize > 0) UIBlock("package.size", () => GUILabelWithText("Size", EditorUtility.FormatBytes(info.PackageSize)));
            if (!string.IsNullOrWhiteSpace(info.SupportedUnityVersions))
            {
                if ((info.AssetSource != Asset.Source.AssetStorePackage && info.AssetSource != Asset.Source.CustomPackage) || info.IsCurrentUnitySupported())
                {
                    UIBlock("package.unityversions", () => GUILabelWithText("Unity", info.SupportedUnityVersions, labelWidth, null, true));
                }
                else
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Unity", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    EditorGUILayout.LabelField(info.SupportedUnityVersions, EditorStyles.wordWrappedLabel, GUILayout.Width(108));
                    EditorGUILayout.LabelField(EditorGUIUtility.IconContent("console.warnicon@2x", "|Package is potentially incompatible to the current Unity version"), GUILayout.Width(24));
                    GUILayout.EndHorizontal();
                }
            }
            UIBlock("package.srps", () =>
            {
                if (info.BIRPCompatible || info.URPCompatible || info.HDRPCompatible)
                {
                    GUILabelWithText("SRPs", (info.BIRPCompatible ? "BIRP " : "") + (info.URPCompatible ? "URP " : "") + (info.HDRPCompatible ? "HDRP" : ""));
                }
            });
            if (info.FirstRelease.Year > 1) UIBlock("package.releasedate", () => GUILabelWithText("Released", info.FirstRelease.ToString("ddd, MMM d yyyy")));
            if (info.GetPurchaseDate().Year > 1) UIBlock("package.purchasedate", () => GUILabelWithText("Purchased", info.GetPurchaseDate().ToString("ddd, MMM d yyyy")));
            if (info.LastRelease.Year > 1)
            {
                UIBlock("package.lastupdate", () => GUILabelWithText("Last Update",
                    info.LastRelease.ToString("ddd, MMM d yyyy") + (!string.IsNullOrEmpty(info.LatestVersion) ? $" ({info.LatestVersion})" : string.Empty),
                    95,
                    info.LastUpdate.Year > 1 ? info.LastUpdate.ToString("ddd, MMM d yyyy") : string.Empty));
            }
            else if (!string.IsNullOrEmpty(info.LatestVersion))
            {
                UIBlock("package.latestversion", () => GUILabelWithText("Latest Version", info.LatestVersion));
            }
            if (info.AssetSource != Asset.Source.CurrentProject)
            {
                UIBlock("package.price", () =>
                {
                    string price = info.GetPrice() > 0 ? info.GetPriceText() : "Free";
                    GUILabelWithText("Price", price);
                });
            }
            if (info.AssetRating > 0)
            {
                UIBlock("package.rating", () =>
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Rating", $"Rating given by Asset Store users ({info.AssetRating}, Hot value {info.Hotness})"), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    int rating = Mathf.RoundToInt(info.AssetRating);
                    if (rating <= 0)
                    {
                        EditorGUILayout.LabelField("Not enough ratings", GUILayout.MaxWidth(113));
                    }
                    else
                    {
                        Color oldCC = GUI.contentColor;
                        float size = 16;
                        // favicon is not gold anymore                    
                        GUI.contentColor = EditorGUIUtility.isProSkin ? new Color(0.992f, 0.694f, 0.004f) : Color.black;
                        for (int i = 0; i < rating; i++)
                        {
                            GUILayout.Box(EditorGUIUtility.IconContent("Favorite Icon"), EditorStyles.label, GUILayout.Width(size), GUILayout.Height(size));
                        }
                        GUI.contentColor = oldCC;
                        for (int i = rating; i < 5; i++)
                        {
                            GUILayout.Box(EditorGUIUtility.IconContent("Favorite"), EditorStyles.label, GUILayout.Width(size), GUILayout.Height(size));
                        }
                    }
                    EditorGUILayout.LabelField($"({info.RatingCount} ratings)", GUILayout.MaxWidth(83));
                    GUILayout.EndHorizontal();
                });
            }
            if ((ShowAdvanced() || AI.Config.tab == 1) && info.AssetSource != Asset.Source.CurrentProject)
            {
                UIBlock("package.indexedfiles", () => GUILabelWithText("Indexed Files", $"{info.FileCount:N0}"), info.AssetSource == Asset.Source.Directory || info.AssetSource == Asset.Source.AssetManager || info.AssetSource == Asset.Source.Archive);
            }

            if (info.HasChildInfos)
            {
                UIBlock("package.childcount", () => GUILabelWithText(info.AssetSource == Asset.Source.AssetManager ? "Collections" : "Sub-Packages", $"{info.ChildInfoCount:N0}" + (info.CurrentState == Asset.State.SubInProcess ? " (reindexing pending)" : "")));
            }

            UIBlock("package.source", () =>
            {
                string packageTooltip = $"IDs: Asset ({info.AssetId}), Foreign ({info.ForeignId}), Upload ({info.UploadId})\n\nLocation: {info.GetLocation(false)}\n\nResolved Location: {info.GetLocation(true)}\n\nCurrent State: {info.CurrentState}";
                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Source", packageTooltip), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                switch (root.AssetSource)
                {
                    case Asset.Source.AssetStorePackage:
                        if (root.ForeignId > 0)
                        {
                            if (GUILayout.Button(CommonUIStyles.Content("Asset Store"), EditorStyles.linkLabel))
                            {
                                AI.OpenStoreURL(root.GetItemLink());
                            }
                        }
                        else
                        {
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Asset Store", packageTooltip), UIStyles.GetLabelMaxWidth());
                        }
                        break;

                    case Asset.Source.RegistryPackage:
                        if (root.ForeignId > 0)
                        {
                            if (GUILayout.Button(CommonUIStyles.Content("Asset Store"), EditorStyles.linkLabel))
                            {
                                AI.OpenStoreURL(info.GetItemLink());
                            }
                        }
                        else if (info.IsFeaturePackage())
                        {
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Unity Feature (Package Bundle)"), EditorStyles.wordWrappedLabel, UIStyles.GetLabelMaxWidth());
                        }
                        else
                        {
                            EditorGUILayout.LabelField(CommonUIStyles.Content($"{StringUtils.CamelCaseToWords(info.AssetSource.ToString())} ({info.PackageSource})", info.SafeName), EditorStyles.wordWrappedLabel, UIStyles.GetLabelMaxWidth());
                        }
                        break;

                    default:
                        EditorGUILayout.LabelField(CommonUIStyles.Content(StringUtils.CamelCaseToWords(info.AssetSource.ToString()), packageTooltip), EditorStyles.wordWrappedLabel, UIStyles.GetLabelMaxWidth());
                        break;
                }
                GUILayout.EndHorizontal();
            });
            if (info.AssetSource != Asset.Source.AssetStorePackage && info.AssetSource != Asset.Source.RegistryPackage && info.ForeignId > 0)
            {
                UIBlock("package.sourcelink", () =>
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Asset Link", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    if (GUILayout.Button(CommonUIStyles.Content("Asset Store"), EditorStyles.linkLabel))
                    {
                        AI.OpenStoreURL(info.GetItemLink());
                    }
                    GUILayout.EndHorizontal();
                });
            }

            if (showMaintenance)
            {
                if (AI.Actions.CreateBackups && info.AssetSource != Asset.Source.RegistryPackage && info.AssetSource != Asset.Source.CurrentProject && info.ParentId == 0)
                {
                    UIBlock("package.backup", () =>
                    {
                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CommonUIStyles.Content("Backup", "Activate to create backups for this asset (done after every update cycle)."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                        EditorGUI.BeginChangeCheck();
                        info.Backup = EditorGUILayout.Toggle(info.Backup);
                        if (EditorGUI.EndChangeCheck()) AI.SetAssetBackup(info, info.Backup);
                        GUILayout.EndHorizontal();
                    });
                }
            }

            if (AI.Actions.CreateAICaptions && info.AssetSource != Asset.Source.CurrentProject)
            {
                UIBlock("package.aiusage", () =>
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("AI Captions", "Activate to create captions for this asset (done after every update cycle)."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    EditorGUI.BeginChangeCheck();
                    info.UseAI = EditorGUILayout.Toggle(info.UseAI);
                    if (EditorGUI.EndChangeCheck()) AI.SetAssetAIUse(info, info.UseAI);
                    GUILayout.EndHorizontal();
                });
            }

            if (info.AssetSource == Asset.Source.CustomPackage || info.AssetSource == Asset.Source.Archive || info.AssetSource == Asset.Source.AssetStorePackage)
            {
                UIBlock("package.extract", () =>
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Keep Cached", "Will keep the package extracted in the cache to minimize access delays at the cost of more hard disk space."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    EditorGUI.BeginChangeCheck();
                    info.KeepExtracted = EditorGUILayout.Toggle(info.KeepExtracted);
                    if (EditorGUI.EndChangeCheck()) AI.SetAssetExtraction(info, info.KeepExtracted);
                    GUILayout.EndHorizontal();
                });
            }

            if (info.AssetSource != Asset.Source.CurrentProject)
            {
                UIBlock("package.exclude", () =>
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Exclude", "Will not index the asset and not show existing index results in the search."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    EditorGUI.BeginChangeCheck();
                    info.Exclude = EditorGUILayout.Toggle(info.Exclude);
                    if (EditorGUI.EndChangeCheck())
                    {
                        AI.SetAssetExclusion(info, info.Exclude);
                        _requireLookupUpdate = ChangeImpact.Write;
                        _requireSearchUpdate = true;
                        _requireAssetTreeRebuild = true;
                    }
                    GUILayout.EndHorizontal();
                });
            }

            DrawMetadata(info, info.PackageMetadata, labelWidth);

            UIBlock("package.metadata", () =>
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(CommonUIStyles.Content("Add Metadata...")))
                {
                    MetadataSelectionUI metaUI = new MetadataSelectionUI();
                    metaUI.Init(MetadataAssignment.Target.Package, () => _metadataEditMode = true);
                    metaUI.SetAssets(new List<AssetInfo> {info});
                    PopupWindow.Show(GetPopupPositionAtMouse(), metaUI);
                }
                if (info.PackageMetadata.Count > 0)
                {
                    if (_metadataEditMode)
                    {
                        if (GUILayout.Button(CommonUIStyles.Content("Save Metadata"), CommonUIStyles.mainButton))
                        {
                            _metadataEditMode = !_metadataEditMode;
                        }
                    }
                    else
                    {
                        if (GUILayout.Button(CommonUIStyles.Content("Edit Metadata")))
                        {
                            _metadataEditMode = !_metadataEditMode;
                        }
                    }
                }
                GUILayout.EndHorizontal();
            }, _metadataEditMode);
            GUILayout.EndVertical();
            if (AI.Config.expandPackageDetails && AI.Config.tab == 1 && info.PreviewTexture != null)
            {
                UIBlock("package.topicon", () =>
                {
                    GUILayout.BeginVertical();
                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    DrawPackagePreview(info);
                    GUILayout.EndHorizontal();
                    GUILayout.EndVertical();
                });
            }
            GUILayout.EndHorizontal();

            EditorGUILayout.Space();
            if (info.SafeName == Asset.NONE) UIBlock("package.hints.noname", () => EditorGUILayout.HelpBox("This is an automatically created package for managing indexed media files that are not associated with any other package.", MessageType.Info));
            if (AI.Config.tab == 0)
            {
                if (info.IsUnityPackage() || info.IsArchive())
                {
                    AssetInfo child = info.FirstChildInfoOrDefault(a => a.Location == info.Location + "|" + info.Path);
                    if (child != null)
                    {
                        UIBlock("package.actions.opensubpackage", () =>
                        {
                            if (GUILayout.Button("Jump into Sub-Package")) OpenInSearch(child, true, false);
                        });
                    }
                }
            }
            if (info.ParentInfo != null)
            {
                UIBlock("package.hints.subpackage", () => EditorGUILayout.HelpBox($"This is a sub-package inside '{info.ParentInfo.GetDisplayName()}'.", MessageType.Info));
                if (AI.Config.tab == 0 && _selectedAsset > 0)
                {
                    UIBlock("package.actions.showparent", () =>
                    {
                        if (GUILayout.Button("Show Parent Package")) OpenInSearch(info.ParentInfo, true, false);
                    });
                }
            }
            if (info.IsDeprecated) UIBlock("package.hints.deprecation", () => EditorGUILayout.HelpBox("This asset is deprecated.", MessageType.Warning));
            if (info.IsAbandoned) UIBlock("package.hints.abandoned", () => EditorGUILayout.HelpBox("This asset is no longer available for download.", MessageType.Error));
#if !USE_ASSET_MANAGER || !USE_CLOUD_IDENTITY
            if (info.AssetSource == Asset.Source.AssetManager)
            {
                UIBlock("package.hints.noassetmanager", () => EditorGUILayout.HelpBox("This package links to the Unity Asset Manager but the SDK is not installed. No actions will be possible.", MessageType.Info));
            }
#endif
            if (showActions)
            {
                EditorGUI.BeginDisabledGroup(AI.Actions.ActionsInProgress);

                bool showDelete = false;
                if (info.CurrentSubState == Asset.SubState.Outdated)
                {
                    AssetDownloader.State? state = info.PackageDownloader?.GetState().state;
                    if (state == AssetDownloader.State.Downloaded || state == AssetDownloader.State.UpdateAvailable)
                    {
                        showDelete = true;
                        UIBlock("package.hints.outdated", () => EditorGUILayout.HelpBox("This asset is outdated in the cache. It is recommended to delete it from the database and the file system.", MessageType.Info));
                    }
                }
                if (info.AssetSource == Asset.Source.AssetStorePackage
                    || info.AssetSource == Asset.Source.CustomPackage
                    || info.AssetSource == Asset.Source.AssetManager
                    || info.AssetSource == Asset.Source.RegistryPackage
                    || info.AssetSource == Asset.Source.Archive
                    || info.AssetSource == Asset.Source.Directory)
                {
                    EditorGUILayout.Space();
                    if (info.AssetSource == Asset.Source.RegistryPackage)
                    {
                        if (info.IsIndirectPackageDependency())
                        {
                            UIBlock("package.hints.indirectdependency", () =>
                            {
                                EditorGUILayout.HelpBox("This package is an indirect dependency and changing the version will decouple it from the dependency lifecycle which can potentially lead to issues.", MessageType.Info);
                                EditorGUILayout.Space();
                            });
                        }
                        if (info.InstalledPackageVersion() != null)
                        {
                            if (info.TargetPackageVersion() != null)
                            {
                                if (info.InstalledPackageVersion() != info.TargetPackageVersion())
                                {
                                    UIBlock("package.actions.update", () =>
                                    {
                                        EditorGUILayout.BeginHorizontal();

                                        string command;
                                        string tooltip;
                                        if (new SemVer(info.InstalledPackageVersion()) > new SemVer(info.TargetPackageVersion()))
                                        {
                                            command = "Downgrade";
                                            tooltip = "Downgrade package to a compatible version calculated from the selected update strategy.";
                                        }
                                        else
                                        {
                                            command = "Update";
                                            tooltip = "Update package to the version calculated from the selected update strategy.";
                                        }
                                        if (CommonUIStyles.MainButton(ref mainUsed, CommonUIStyles.Content($"{command} to {info.TargetPackageVersion()}", tooltip)))
                                        {
                                            ImportUI importUI = ImportUI.ShowWindow();
                                            importUI.Init(new List<AssetInfo> {info}, true);
                                        }
                                        string changeLogURL = info.GetChangeLogURL(info.TargetPackageVersion());
                                        if (!string.IsNullOrWhiteSpace(changeLogURL) && GUILayout.Button(CommonUIStyles.Content("?", "Changelog"), GUILayout.Width(20)))
                                        {
                                            AI.OpenURL(changeLogURL);
                                        }
                                        EditorGUILayout.EndHorizontal();
                                    });
                                }
                            }
                            if (info.HasSamples())
                            {
                                UIBlock("package.actions.samples", () =>
                                {
                                    if (GUILayout.Button(CommonUIStyles.Content("Add/Remove Samples...")))
                                    {
                                        SampleSelectionUI samplesUI = new SampleSelectionUI();
                                        samplesUI.Init(info);
                                        PopupWindow.Show(GetPopupPositionAtMouse(), samplesUI);
                                    }
                                });
                            }

                            UIBlock("package.actions.remove", () =>
                            {
                                if (CommonUIStyles.MainButton(ref mainUsed, CommonUIStyles.Content("Uninstall Package", "Remove package from current project.")))
                                {
                                    PackageInfo pInfo = AssetStore.GetPackageInfo(info);
                                    if (pInfo != null)
                                    {
                                        if (pInfo.source == PackageSource.Embedded)
                                        {
                                            // embedded packages need to be deleted manually
                                            FileUtil.DeleteFileOrDirectory(pInfo.resolvedPath);
                                            AssetDatabase.Refresh();
                                        }
                                        else
                                        {
                                            Client.Remove(pInfo.name);
                                        }
                                        AssetStore.GatherProjectMetadata();
                                    }
                                }
                                EditorGUILayout.Space();
                            });
                        }
                        else if (info.TargetPackageVersion() != null)
                        {
                            UIBlock("package.actions.install", () =>
                            {
                                if (CommonUIStyles.MainButton(ref mainUsed, CommonUIStyles.Content($"Install Version {info.TargetPackageVersion()}", "Installs package into the current project.")))
                                {
                                    ImportUI importUI = ImportUI.ShowWindow();
                                    importUI.Init(new List<AssetInfo> {info}, true);
                                }
                            });
                        }
                        else if (info.PackageSource == PackageSource.Local)
                        {
                            UIBlock("package.actions.install", () =>
                            {
                                if (CommonUIStyles.MainButton(ref mainUsed, CommonUIStyles.Content("Install (link locally)", "Links package to the current project.")))
                                {
                                    ImportUI importUI = ImportUI.ShowWindow();
                                    importUI.Init(new List<AssetInfo> {info}, true);
                                }
                            });
                            EditorGUI.EndDisabledGroup(); // Enable non-conflicting actions
                            UIBlock("package.actions.openlocation", () =>
                            {
                                if (GUILayout.Button(CommonUIStyles.Content("Open Package Location..."))) ShowInExplorer(info);
                            });
                            EditorGUI.BeginDisabledGroup(AI.Actions.ActionsInProgress); // Re-disable for conflicting actions
                        }
                        else if (info.PackageSource == PackageSource.Git)
                        {
                            UIBlock("package.actions.install", () =>
                            {
                                if (CommonUIStyles.MainButton(ref mainUsed, CommonUIStyles.Content("Install Indexed Version", info.LatestVersion)))
                                {
                                    InstallPackage(info, info.LatestVersion);
                                }
                            });
                        }
                        if (info.IsFeaturePackage())
                        {
                            UIBlock("package.actions.remove", () =>
                            {
                                List<AssetInfo> installed = info.GetInstalledFeaturePackageContent(_assets);
                                if (installed.Count > 0 && CommonUIStyles.MainButton(ref mainUsed, CommonUIStyles.Content($"Uninstall {installed.Count} Packages", "Remove installed packages out of this feature from current project.")))
                                {
                                    RemovalUI removalUI = RemovalUI.ShowWindow();
                                    removalUI.Init(installed);
                                }
                                EditorGUILayout.Space();
                            });
                        }
                    }
                    else if (info.AssetSource != Asset.Source.AssetManager && info.SafeName != Asset.NONE
                        && (info.IsDownloaded || info.AssetSource == Asset.Source.AssetStorePackage)
                        && !info.IsAbandoned)
                    {
                        bool pkgDownloadable = !info.IsDownloaded && info.AssetSource == Asset.Source.AssetStorePackage;
                        if (info.AssetSource != Asset.Source.Directory)
                        {
                            if (showMaintenance && info.IsDownloaded && (info.IsUpdateAvailable(_assets) || info.WasOutdated || !info.IsDownloadedCompatible))
                            {
                                EditorGUI.EndDisabledGroup(); // Enable downloads
                                DrawPackageDownload(info, true);
                                EditorGUI.BeginDisabledGroup(AI.Actions.ActionsInProgress); // Re-disable for conflicting actions
                            }
                            if (AssetStore.IsInstalled(info) || (_usedPackages != null && _usedPackages.ContainsKey(info.AssetId)))
                            {
                                UIBlock("package.actions.remove", () =>
                                {
                                    if (CommonUIStyles.MainButton(ref mainUsed, CommonUIStyles.Content("Uninstall Package...", "Remove package from current project.")))
                                    {
                                        UninstallPackageUI.ShowWindow().Init(info, _usedPackages != null && _usedPackages.ContainsKey(info.AssetId) ? _usedPackages[info.AssetId] : null);
                                    }
                                });
                            }
                            else
                            {
                                UIBlock("package.actions.import", () =>
                                {
                                    GUIContent importContent = pkgDownloadable
                                        ? CommonUIStyles.Content("Import Package... ↓", "Download the package first, then open import dialog")
                                        : CommonUIStyles.Content("Import Package...", "Open import dialog");
                                    if (CommonUIStyles.MainButton(ref mainUsed, importContent))
                                    {
                                        ImportUI importUI = ImportUI.ShowWindow();
                                        importUI.Init(new List<AssetInfo> {info});
                                    }
                                });
                                if (!pkgDownloadable)
                                {
                                    EditorGUI.EndDisabledGroup(); // Enable non-conflicting actions
                                    UIBlock("package.actions.openlocation", () =>
                                    {
                                        if (GUILayout.Button(CommonUIStyles.Content("Open Package Location..."))) ShowInExplorer(info);
                                    });
                                    EditorGUI.BeginDisabledGroup(AI.Actions.ActionsInProgress); // Re-disable for conflicting actions
                                }
                            }
                        }
                        else
                        {
                            EditorGUI.EndDisabledGroup(); // Enable non-conflicting actions
                            UIBlock("package.actions.openlocation", () =>
                            {
                                string locName = info.AssetSource == Asset.Source.Archive ? "Archive" : "Directory";
                                if (GUILayout.Button(CommonUIStyles.Content($"Open {locName} Location..."))) ShowInExplorer(info);
                            });
                            EditorGUI.BeginDisabledGroup(AI.Actions.ActionsInProgress); // Re-disable for conflicting actions
                        }
                    }
                    EditorGUI.EndDisabledGroup(); // Enable non-conflicting navigation actions
                    if (info.ForeignId > 0 || info.AssetSource == Asset.Source.RegistryPackage)
                    {
                        UIBlock("package.actions.openinpackagemanager", () =>
                        {
                            if (GUILayout.Button(CommonUIStyles.Content("Open in Package Manager...")))
                            {
                                AssetStore.OpenInPackageManager(info);
                            }
                        });
                    }

                    if (AI.Config.tab == 0 && _selectedAsset == 0 && info.IsIndexed && info.FileCount > 0)
                    {
                        UIBlock("package.actions.filter", () =>
                        {
                            if (GUILayout.Button("Filter for this package only")) OpenInSearch(info, true);
                        });
                    }
                    if (AI.Config.tab != 1 && info.AssetId > 0)
                    {
                        UIBlock("package.actions.packageview", () =>
                        {
                            if (GUILayout.Button("Show in Package View")) OpenInPackageView(info);
                        });
                    }
                    if (AI.Config.tab > 0 && info.IsIndexed && info.FileCount > 0)
                    {
                        UIBlock("package.actions.openinsearch", () =>
                        {
                            if (CommonUIStyles.MainButton(ref mainUsed, "Open in Search")) OpenInSearch(info);
                        });
                    }
                    EditorGUI.BeginDisabledGroup(AI.Actions.ActionsInProgress); // Re-disable for conflicting actions
#if USE_ASSET_MANAGER && USE_CLOUD_IDENTITY
                    if (showMaintenance && info.SafeName != Asset.NONE)
                    {
                        if (info.AssetSource == Asset.Source.AssetManager)
                        {
                            EditorGUI.BeginDisabledGroup(CloudAssetManagement.IsBusy);
                            EditorGUILayout.Space();
                            UIBlock("package.actions.createcollection", () =>
                            {
                                if (GUILayout.Button("Create Collection..."))
                                {
                                    NameUI nameUI = new NameUI();
                                    nameUI.Init("New Collection", colName => CreateCollection(info, colName));
                                    PopupWindow.Show(GetPopupPositionAtMouse(), nameUI);
                                }
                            });
                            if (info.ParentInfo != null)
                            {
                                UIBlock("package.actions.deletecollection", () =>
                                {
                                    if (GUILayout.Button("Delete Collection"))
                                    {
                                        DeleteCollection(info);
                                    }
                                });
                            }
                            EditorGUI.EndDisabledGroup();
                        }
                    }
#endif
                    EditorGUILayout.Space();
                    if (showMaintenance && info.SafeName != Asset.NONE)
                    {
                        if (info.ForeignId > 0)
                        {
                            UIBlock("package.actions.refreshmetadata", () =>
                            {
                                if (GUILayout.Button(CommonUIStyles.Content("Refresh Metadata", "Will fetch most up-to-date metadata from the Asset Store.")))
                                {
                                    _ = AI.Actions.FetchAssetDetails(true, info.AssetId);
                                }
                            });
                        }
                    }
                    EditorGUI.EndDisabledGroup(); // Enable Previews Wizard
                    if (info.IsIndexed && info.FileCount > 0)
                    {
                        UIBlock("package.actions.recreatepreviews", () =>
                        {
                            if (GUILayout.Button("Previews Wizard..."))
                            {
                                PreviewWizardUI previewsUI = PreviewWizardUI.ShowWindow();
                                previewsUI.Init(new List<AssetInfo> {info}, _assets);
                            }
                        });
                    }
                    if (!info.IsDownloaded)
                    {
                        if (info.ParentId <= 0 && (info.AssetSource == Asset.Source.CustomPackage || info.AssetSource == Asset.Source.Archive || info.AssetSource == Asset.Source.Directory))
                        {
                            showDelete = true;
                            EditorGUILayout.Space();
                            EditorGUILayout.HelpBox("This package does not exist anymore on the file system and was probably deleted.", MessageType.Error);
                        }
                        else if (!info.IsAbandoned)
                        {
                            EditorGUILayout.Space();
                            DrawPackageDownload(info);
                        }
                    }
                    if (showMaintenance)
                    {
                        if (info.ForeignId <= 0 && info.ParentId <= 0 && info.AssetId > 0
                            && info.AssetSource != Asset.Source.RegistryPackage)
                        {
                            UIBlock("package.actions.connecttoassetstore", () =>
                            {
                                GUILayout.BeginHorizontal();
                                if (GUILayout.Button("Connect to Asset Store..."))
                                {
                                    AssetConnectionUI assetUI = new AssetConnectionUI();
                                    assetUI.Init(details => ConnectToAssetStore(info, details));
                                    PopupWindow.Show(GetPopupPositionAtMouse(), assetUI);
                                }
                                if (GUILayout.Button("Edit Data..."))
                                {
                                    PackageUI packageUI = PackageUI.ShowWindow();
                                    packageUI.Init(info, _ =>
                                    {
                                        _requireAssetTreeRebuild = true;
                                        UpdateStatistics(true); // to reload asset data
                                    });
                                }
                                GUILayout.EndHorizontal();
                            });
                        }
                    }
                }
                if (showMaintenance && info.AssetSource != Asset.Source.CurrentProject)
                {
                    if (info.AssetSource != Asset.Source.RegistryPackage)
                    {
                        UIBlock("package.actions.export", () =>
                        {
                            if (GUILayout.Button("Export Package..."))
                            {
                                ExportUI exportUI = ExportUI.ShowWindow();
                                exportUI.Init(_selectedTreeAssets, false, 0, assetMchState.visibleColumns);
                            }
                            EditorGUILayout.Space();
                        });
                    }

                    if (info.ForeignId > 0 && (info.AssetSource == Asset.Source.CustomPackage || info.AssetSource == Asset.Source.Archive))
                    {
                        UIBlock("package.actions.removeassetstoreconnection", () =>
                        {
                            if (GUILayout.Button("Remove Asset Store Connection"))
                            {
                                bool removeMetadata = EditorUtility.DisplayDialog("Remove Metadata", "Remove or keep the additional metadata from the Asset Store like ratings, category etc.?", "Remove", "Keep");
                                AI.DisconnectFromAssetStore(info, removeMetadata);
                                _requireAssetTreeRebuild = true;
                            }
                        });
                    }
                    EditorGUI.BeginDisabledGroup(AI.Actions.ActionsInProgress); // Re-disable for conflicting actions
                    if (AI.Config.tab > 0 && info.IsIndexed && info.FileCount > 0)
                    {
                        UIBlock("package.actions.reindexnextrun", () =>
                        {
                            if ((info.IsDownloaded || info.AssetSource == Asset.Source.AssetStorePackage) && GUILayout.Button(CommonUIStyles.Content("Reindex Package on Next Run", "Will mark this package as outdated and force a reindex the next time actions are run on the Settings tab.")))
                            {
                                Assets.ForgetPackage(info, true);
                                _requireLookupUpdate = ChangeImpact.Write;
                                _requireSearchUpdate = true;
                                _requireAssetTreeRebuild = true;
                            }
                        });
                    }
                    if (info.IsDownloaded || info.AssetSource == Asset.Source.AssetStorePackage)
                    {
                        bool showIndexAction = (info.CurrentState == Asset.State.New || info.CurrentState == Asset.State.InProcess)
                            && (info.AssetSource != Asset.Source.RegistryPackage || AI.Actions.IndexPackageCache);
                        UIBlock("package.actions.reindexnow", () =>
                        {
                            string reindexCaption = info.IsIndexed ? "Reindex" : "Index";
                            if (GUILayout.Button(CommonUIStyles.Content($"{reindexCaption} Package Now", "Will instantly reindex the full package.")))
                            {
                                ReindexPackageNow(info);
                            }
                        }, showIndexAction);
                    }
                    UIBlock("package.actions.hidecontent", () =>
                    {
                        if (info.IsIndexed && info.FileCount > 0 && GUILayout.Button(CommonUIStyles.Content("Hide Content...", "Open editor to manage which files from this package are hidden from search results.")))
                        {
                            HideContentUI hideUI = HideContentUI.ShowWindow();
                            hideUI.Init(info);
                        }
                    });
                    UIBlock("package.actions.delete", () =>
                    {
                        if (GUILayout.Button(CommonUIStyles.Content("Delete Package...", "Delete the package from the database and optionally the file system.")))
                        {
                            PackageDeletionUI deletionUI = PackageDeletionUI.ShowWindow();
                            deletionUI.Init(info, () =>
                            {
                                _selectedTreeAsset = null;
                                _requireLookupUpdate = ChangeImpact.Write;
                                _requireAssetTreeRebuild = true;
                            });
                        }
                    }, showDelete);
                }
                EditorGUI.EndDisabledGroup();

                if (info.AssetId > 0)
                {
                UIBlock("package.actions.tag", () =>
                {
                    DrawAddPackageTag(new List<AssetInfo> {info});
                    if (info.PackageTags != null && info.PackageTags.Count > 0)
                    {
                        float x = 0f;
                        foreach (TagInfo tagInfo in info.PackageTags)
                        {
                            x = CalcTagSize(x, tagInfo.Name);
                            UIStyles.DrawTag(tagInfo, () =>
                            {
                                Tagging.RemoveAssignment(info, tagInfo);
                                _requireAssetTreeRebuild = true;
                            }, GetMaxDetailTagWidth());
                        }
                    }
                    GUILayout.EndHorizontal();
                });
                }
            }
        }

        private void DrawMetadata(AssetInfo info, List<MetadataInfo> metadata, int labelWidth)
        {
            foreach (MetadataInfo metaInfo in metadata)
            {
                if (metaInfo.RestrictAssetSource && info.AssetSource != metaInfo.ApplicableSource) continue;
                if (metaInfo.Name == MetadataDefinition.FIELD_HIDE) continue;

                UIBlock($"package.metadata.{metaInfo.Id}", () =>
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(metaInfo.Name, EditorStyles.boldLabel, GUILayout.Width(labelWidth));

                    EditorGUI.BeginChangeCheck();
                    if (_metadataEditMode)
                    {
                        switch (metaInfo.Type)
                        {
                            case MetadataDefinition.DataType.Boolean:
                                metaInfo.BoolValue = EditorGUILayout.Toggle(metaInfo.BoolValue);
                                break;

                            case MetadataDefinition.DataType.Text:
                            case MetadataDefinition.DataType.Url:
                                metaInfo.StringValue = EditorGUILayout.DelayedTextField(metaInfo.StringValue);
                                break;

                            case MetadataDefinition.DataType.Date:
                            case MetadataDefinition.DataType.DateTime:
                                if (DateTime.TryParse(EditorGUILayout.DelayedTextField(metaInfo.DateTimeValue.ToString("o")), out DateTime dateTime))
                                {
                                    metaInfo.DateTimeValue = dateTime;
                                }
                                break;

                            case MetadataDefinition.DataType.BigText:
                                metaInfo.StringValue = EditorGUILayout.TextArea(metaInfo.StringValue, GUILayout.MinWidth(100));
                                break;

                            case MetadataDefinition.DataType.Number:
                                metaInfo.IntValue = EditorGUILayout.DelayedIntField(metaInfo.IntValue);
                                break;

                            case MetadataDefinition.DataType.DecimalNumber:
                                metaInfo.FloatValue = EditorGUILayout.DelayedFloatField(metaInfo.FloatValue);
                                break;

                            case MetadataDefinition.DataType.SingleSelect:
                                if (string.IsNullOrEmpty(metaInfo.ValueList))
                                {
                                    EditorGUILayout.LabelField("-No values specified yet-", EditorStyles.wordWrappedLabel);
                                }
                                else
                                {
                                    List<string> rawValues = new List<string>
                                    {
                                        "-none-",
                                        string.Empty
                                    };
                                    rawValues.AddRange(metaInfo.ValueList.Split(',').Select(s => s.Trim()));
                                    string[] values = rawValues.ToArray();
                                    int oldIdx = Mathf.Max(0, Array.IndexOf(values, metaInfo.StringValue));
                                    int newIdx = EditorGUILayout.Popup(oldIdx, values);
                                    metaInfo.StringValue = newIdx <= 1 ? null : values[newIdx];
                                }
                                break;

                            case MetadataDefinition.DataType.List:
                                metaInfo.StringValue = GUIStringListField(null, metaInfo.StringValue, newValue => metaInfo.StringValue = newValue, ";", "Items", 0);
                                break;

                        }
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button(EditorGUIUtility.IconContent("TreeEditor.Trash", "|Remove metadata"), GUILayout.Width(30)))
                        {
                            Metadata.RemoveAssignment(info, metaInfo);
                        }
                    }
                    else
                    {
                        switch (metaInfo.Type)
                        {
                            case MetadataDefinition.DataType.Boolean:
                                metaInfo.BoolValue = EditorGUILayout.Toggle(metaInfo.BoolValue);
                                break;

                            case MetadataDefinition.DataType.Text:
                            case MetadataDefinition.DataType.BigText:
                            case MetadataDefinition.DataType.SingleSelect:
                            case MetadataDefinition.DataType.List:
                                EditorGUILayout.LabelField(metaInfo.StringValue, EditorStyles.wordWrappedLabel, UIStyles.GetLabelMaxWidth());
                                break;

                            case MetadataDefinition.DataType.Number:
                                EditorGUILayout.LabelField(metaInfo.IntValue.ToString(), UIStyles.GetLabelMaxWidth());
                                break;

                            case MetadataDefinition.DataType.DecimalNumber:
                                EditorGUILayout.LabelField($"{metaInfo.FloatValue:N1}", UIStyles.GetLabelMaxWidth());
                                break;

                            case MetadataDefinition.DataType.Url:
                                if (GUILayout.Button(metaInfo.StringValue?.Replace("https://", "").Replace("www.", ""), EditorStyles.linkLabel))
                                {
                                    AI.OpenURL(metaInfo.StringValue);
                                }
                                break;

                            case MetadataDefinition.DataType.Date:
                                EditorGUILayout.LabelField(metaInfo.DateTimeValue.ToShortDateString(), UIStyles.GetLabelMaxWidth());
                                break;
                        }
                    }
                    if (EditorGUI.EndChangeCheck())
                    {
                        DBAdapter.DB.Update(metaInfo.ToAssignment());
                    }
                    GUILayout.EndHorizontal();
                });
            }
        }

        private static void InstallPackage(AssetInfo info, string version)
        {
            info.ForceTargetVersion(version);

            ImportUI importUI = ImportUI.ShowWindow();
            importUI.Init(new List<AssetInfo> {info}, true);
        }

        private void ReindexPackageNow(AssetInfo info)
        {
            Assets.ForgetPackage(info, true);
            AI.Actions.Reindex(info);
            _requireLookupUpdate = ChangeImpact.Write;
            _requireSearchUpdate = true;
            _requireAssetTreeRebuild = true;
        }

        private void ReindexPackagesNow(IEnumerable<AssetInfo> packages)
        {
            foreach (AssetInfo package in packages)
            {
                if (package != null && package.IsDownloaded)
                {
                    ReindexPackageNow(package);
                }
            }
        }

        private void ShowDependencyDetails(AssetInfo info)
        {
            if (info.AssetSource == Asset.Source.RegistryPackage)
            {
                PackageInfo pInfo = AssetStore.GetPackageInfo(info);
                if (!AssetStore.IsMetadataAvailable())
                {
                    EditorGUILayout.HelpBox("Loading data...", MessageType.Info);
                }
                else if (pInfo == null || pInfo.dependencies == null)
                {
                    EditorGUILayout.HelpBox("Could not find matching package metadata.", MessageType.Warning);
                }
                else
                {
                    if (AI.Config.expandPackageDetails) EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.BeginVertical(GUILayout.Width(UIStyles.INSPECTOR_WIDTH - 30));
                    EditorGUILayout.LabelField("Is Using", EditorStyles.boldLabel);
                    if (pInfo.dependencies.Length > 0)
                    {
                        foreach (DependencyInfo dependency in pInfo.dependencies.OrderBy(d => d.name))
                        {
                            AssetInfo package = _assets.FirstOrDefault(a => a.SafeName == dependency.name);
                            if (package != null)
                            {
                                if (GUILayout.Button(package.GetDisplayName() + $" - {dependency.version}", CommonUIStyles.wrappedButton))
                                {
                                    OpenInPackageView(package);
                                }
                            }
                            else
                            {
                                EditorGUILayout.LabelField($"{dependency.name} - {dependency.version}");
                            }
                        }
                    }
                    else
                    {
                        EditorGUILayout.LabelField("-none-");
                    }
                    EditorGUILayout.EndVertical();

                    EditorGUILayout.Space();
                    EditorGUILayout.BeginVertical();
                    if (_usedByCacheAssetId != info.AssetId)
                    {
                        _usedByCacheAssetId = info.AssetId;
                        Dictionary<string, PackageInfo> allPackages = AssetStore.GetPackages();
                        _usedByCache = allPackages.Values
                            .Where(p => p.dependencies != null && p.dependencies.Any(d => d.name == info.SafeName))
                            .OrderBy(d => d.displayName)
                            .ToList();
                    }
                    EditorGUILayout.LabelField("Used By", EditorStyles.boldLabel);
                    if (_usedByCache != null && _usedByCache.Count > 0)
                    {
                        foreach (PackageInfo dependency in _usedByCache)
                        {
                            AssetInfo package = _assets.FirstOrDefault(a => a.SafeName == dependency.name);
                            if (package != null)
                            {
                                if (GUILayout.Button(package.GetDisplayName() + (package.IsFeaturePackage() ? " (feature)" : "") + $" - {dependency.version}", CommonUIStyles.wrappedButton))
                                {
                                    OpenInPackageView(package);
                                }
                            }
                            else
                            {
                                EditorGUILayout.LabelField($"{dependency.name} - {dependency.version}");
                            }
                        }
                    }
                    else
                    {
                        EditorGUILayout.LabelField("-none-");
                    }
                    EditorGUILayout.EndVertical();
                    if (AI.Config.expandPackageDetails) EditorGUILayout.EndHorizontal();
                }
            }
            else if (info.GetPackageDependencies() != null)
            {
                EditorGUILayout.BeginVertical();
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox("The following items might be required in order to use the package, but could also be optional", MessageType.Info);
                EditorGUILayout.Space();
                foreach (Dependency dependency in info.GetPackageDependencies().OrderBy(d => d.name).ThenBy(d => d.location))
                {
                    AssetInfo package = _assets.FirstOrDefault(a => a.ForeignId == dependency.id);
                    if (package != null)
                    {
                        if (GUILayout.Button(dependency.name, CommonUIStyles.wrappedButton))
                        {
                            OpenInPackageView(package);
                        }
                    }
                    else
                    {
                        package = _assets.FirstOrDefault(a => a.SafeName == dependency.location);
                        if (package != null)
                        {
                            if (GUILayout.Button(package.GetDisplayName() + (package.IsFeaturePackage() ? " (feature)" : ""), CommonUIStyles.wrappedButton))
                            {
                                OpenInPackageView(package);
                            }
                        }
                        else
                        {
                            if (GUILayout.Button((string.IsNullOrWhiteSpace(dependency.name) ? dependency.location : dependency.name) + "*", CommonUIStyles.wrappedButton))
                            {
                                AI.OpenURL(dependency.location);
                            }
                        }
                    }
                }
                EditorGUILayout.EndVertical();
            }
            if (info.GetPackageUsageDependencies(_assets) != null)
            {
                EditorGUILayout.BeginVertical();
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox("The following Asset Store packages list this one as a dependency.", MessageType.Info);
                EditorGUILayout.Space();
                foreach (AssetInfo package in info.GetPackageUsageDependencies(_assets).OrderBy(d => d.GetDisplayName()))
                {
                    if (GUILayout.Button(package.GetDisplayName(), CommonUIStyles.wrappedButton))
                    {
                        OpenInPackageView(package);
                    }
                }
                EditorGUILayout.EndVertical();
            }
        }

        private void ShowMediaDetails(AssetInfo info)
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (_selectedMedia < 0 || _selectedMedia >= info.Media.Count) _selectedMedia = 0;

            // Load full media on-demand
            AssetMedia selectedMedia = info.Media[_selectedMedia];
            if (selectedMedia.Texture == null && !selectedMedia.IsDownloading)
            {
                Task _ = MediaManager.LoadFullMediaOnDemand(info, selectedMedia);
            }

            // Show placeholder while loading to prevent flickering
            float maxHeight = AI.Config.mediaHeight / (AI.Config.expandPackageDetails ? 1f : 2f);
            if (selectedMedia.Texture == null)
            {
                GUILayout.Box("Loading...", CommonUIStyles.centerLabel, GUILayout.MaxWidth(GetInspectorWidth() - 20), GUILayout.Height(maxHeight));
            }
            else
            {
                GUILayout.Box(selectedMedia.Texture, CommonUIStyles.centerLabel, GUILayout.MaxWidth(GetInspectorWidth() - 20), GUILayout.MaxHeight(maxHeight));
            }
            if (Event.current.type == EventType.Repaint) _mediaRect = GUILayoutUtility.GetLastRect();
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0)
            {
                if (_mediaRect.Contains(Event.current.mousePosition))
                {
                    // start process from thread as otherwise GUI reports layouting errors
                    string path = info.ToAsset().GetMediaFile(info.Media[_selectedMedia], Paths.GetPreviewFolder());
                    Task _ = Task.Run(() => Process.Start(path));
                }
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            _imageScrollPos = EditorGUILayout.BeginScrollView(_imageScrollPos, false, false, GUILayout.Height(AI.Config.mediaThumbnailHeight + 30));
            GUILayout.BeginHorizontal();
            for (int i = 0; i < info.Media.Count; i++)
            {
                AssetMedia media = info.Media[i];
                // Thumbnails are always loaded, full media loaded on-demand
                // Use thumbnail preferably, fall back to full media if already loaded
                Texture2D texture = media.ThumbnailTexture != null ? media.ThumbnailTexture : media.Texture;
                if (GUILayout.Button(
                        CommonUIStyles.Content(texture == null ? "Loading..." : string.Empty, texture),
                        GUILayout.Width(AI.Config.mediaThumbnailWidth),
                        GUILayout.Height(AI.Config.mediaThumbnailHeight + (i == _selectedMedia ? 10 : 0))))
                {
                    if (media.Type == "youtube" || media.Type == "vimeo" || media.Type == "sketchfab" || media.Type == "soundcloud" || media.Type == "mixcloud" || media.Type == "attachment_video" || media.Type == "attachment_audio")
                    {
                        if (Event.current.button == 0)
                        {
                            // open URL in browser
                            AI.OpenURL(media.GetUrl());
                        }
                        else
                        {
                            // copy URL to clipboard
                            EditorGUIUtility.systemCopyBuffer = media.GetUrl();
                        }
                    }
                    else
                    {
                        _selectedMedia = i;
                    }
                }
            }
            EditorGUILayout.Space();
            GUILayout.EndHorizontal();
            EditorGUILayout.EndScrollView();
        }

        private static void ShowReleaseNotesDetails(AssetInfo info)
        {
            if (!string.IsNullOrWhiteSpace(info.ReleaseNotes))
            {
                DrawTextWithLinks(info.ReleaseNotes);
            }
        }

        private static void ShowDescriptionDetails(AssetInfo info)
        {
            if (!string.IsNullOrWhiteSpace(info.Description))
            {
                DrawTextWithLinks(info.Description);
            }
        }

        private static void DrawTextWithLinks(string rawText)
        {
            TextWithLinks parsed = StringUtils.ToLabelWithLinks(rawText);
            if (!string.IsNullOrEmpty(parsed.Text))
            {
                EditorGUILayout.LabelField(parsed.Text, EditorStyles.wordWrappedLabel);
            }
            if (parsed.HasLinks)
            {
                EditorGUILayout.Space();
                foreach (TextLink link in parsed.Links)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(10);
                    if (GUILayout.Button(CommonUIStyles.Content(link.DisplayText, link.Url), CommonUIStyles.wrappedLinkLabel, GUILayout.ExpandWidth(false)))
                    {
                        AI.OpenURL(link.Url);
                    }
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();
                }
            }
        }

        private static void DrawPackagePreview(AssetInfo info)
        {
            GUILayout.Box(info.PreviewTexture, EditorStyles.centeredGreyMiniLabel, GUILayout.MaxWidth(GetInspectorWidth()), GUILayout.MaxHeight(100));
        }

        private static async void ShowInExplorer(AssetInfo info)
        {
            string location = await info.GetLocation(true, true);
            EditorUtility.RevealInFinder(location);
        }

        private static void RemovePackage(AssetInfo info)
        {
            Client.Remove(info.SafeName);
        }

        private async void ConnectToAssetStore(AssetInfo info, AssetDetails details)
        {
            AI.ConnectToAssetStore(info, details);
            await new AssetStoreImporter().FetchAssetsDetails(false, info.AssetId);
            _requireLookupUpdate = ChangeImpact.Write;
            _requireAssetTreeRebuild = true;
        }

        private async void RefreshMetadataAsync(List<AssetInfo> bulkAssets)
        {
            // Filter to parent packages only and convert to Asset objects
            List<Asset> assetsToRefresh = bulkAssets
                .Where(info => info.ParentId <= 0 && info.ForeignId > 0)
                .Select(info => info.ToAsset())
                .ToList();

            if (assetsToRefresh.Count == 0) return;

            // Use existing chunked processing with global progress tracking
            await AI.Actions.RunWithProgress<AssetStoreImporter>(
                ActionHandler.ACTION_ASSET_STORE_DETAILS,
                "Updating package details",
                imp => imp.FetchAssetsDetails(assetsToRefresh, forceUpdate: true, resetEtag: true));

            // Trigger UI refresh
            AI.TriggerPackageRefresh();
        }

        private float CalcTagSize(float x, string tagName)
        {
            float tagWidth = UIStyles.CalcTagWidth(tagName, UIStyles.TagStyle.Remove, GetMaxDetailTagWidth());
            x += tagWidth + UIStyles.TAG_SIZE_SPACING + UIStyles.tag.margin.right * 2f;
            if (x > GetInspectorWidth() - UIStyles.TAG_OUTER_MARGIN * 3)
            {
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Space(95 + 3);
                x = tagWidth + UIStyles.TAG_SIZE_SPACING + UIStyles.tag.margin.right * 2f;
            }
            return x;
        }

        private static float GetMaxDetailTagWidth()
        {
            return Mathf.Max(80f, GetInspectorWidth() - 120f);
        }

        private void DrawAddPackageTag(List<AssetInfo> infos)
        {
            EditorGUILayout.Space();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(CommonUIStyles.Content("Add Tag..."), GUILayout.Width(80)))
            {
                TagSelectionUI tagUI = new TagSelectionUI();
                tagUI.Init(TagAssignment.Target.Package);
                tagUI.SetAssets(infos);
                PopupWindow.Show(GetPopupPositionAtMouse(), tagUI);
            }
            GUILayout.Space(15);
        }

        private void DrawPackagesTab()
        {
            if (_stats == null || _stats.AllPackages == 0)
            {
                EditorGUILayout.HelpBox("No packages were indexed yet. Start the indexing process to fill this list.", MessageType.Info);
                return;
            }

            // Display saved package searches
            UIBlock("package.savedsearches", () =>
            {
                if (PackageSearches.Count > 0)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(10);
                    float currentX = 10;
                    float availableWidth = position.width - 20;

                    foreach (SavedPackageSearch search in PackageSearches)
                    {
                        RenderSavedSearchButton(
                            search,
                            _activeSavedPackageSearchId,
                            search.SearchPhrase ?? "",
                            () =>
                            {
                                if (_activeSavedPackageSearchId == search.Id)
                                {
                                    ResetPackageFilters();
                                    _activeSavedPackageSearchId = -1;
                                    _assetSearchPhrase = "";
                                }
                                else
                                {
                                    LoadPackageSearch(search);
                                }
                            },
                            (menu) =>
                            {
                                menu.AddItem(new GUIContent("Edit..."), false, () =>
                                {
                                    SavedPackageSearchUI savedSearchUI = SavedPackageSearchUI.ShowWindow();
                                    savedSearchUI.Init(search);
                                });
                                menu.AddItem(new GUIContent("Override with Current Filters"), false, () =>
                                {
                                    OverrideSavedPackageSearch(search);
                                });
                                menu.AddSeparator("");
                                menu.AddItem(new GUIContent("Delete"), false, () =>
                                {
                                    DeletePackageSearch(search);
                                });
                            },
                            ref currentX,
                            availableWidth
                        );
                    }

                    GUILayout.EndHorizontal();
                    EditorGUILayout.Space();
                }
            });

            GUILayout.BeginVertical("box");
            GUILayout.BeginHorizontal();

            UIBlock2("package.actions.search", () =>
            {
                EditorGUILayout.LabelField("Search:", GUILayout.Width(50));
                EditorGUI.BeginChangeCheck();
                _assetSearchPhrase = AssetSearchField.OnGUI(_assetSearchPhrase, GUILayout.ExpandWidth(true));
                if (EditorGUI.EndChangeCheck())
                {
                    // delay search to allow fast typing
                    _nextAssetSearchTime = Time.realtimeSinceStartup + AI.Config.searchDelay;
                }
                else if (!_allowLogic && _nextAssetSearchTime > 0 && Time.realtimeSinceStartup > _nextAssetSearchTime) // don't do when logic allowed as otherwise there will be GUI errors
                {
                    _nextAssetSearchTime = 0;
                    _requireAssetTreeRebuild = true;
                }
            });
            /*
            UIBlock2("package.actions.add", () =>
            {
                if (GUILayout.Button(CommonUIStyles.Content("+", "Add or Install Packages"), GUILayout.Width(20)))
                {
                    GenericMenu menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Add Git Package..."), false, () => CreatePackage(Asset.Source.RegistryPackage, PackageSource.Git));
                    menu.AddItem(new GUIContent("Add Asset Store Package to Watch..."), false, () => CreatePackage(Asset.Source.AssetStorePackage));
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Install Package by Name..."), false, () => { });
                    menu.ShowAsContext();
                }
            });
            */

            EditorGUI.BeginChangeCheck();

            UIBlock2("package.actions.typeselector", () =>
            {
                EditorGUILayout.Space();
                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Types:", GUILayout.Width(42));
                AI.Config.packagesListing = GUILayout.Toolbar(AI.Config.packagesListing, _packageListingOptionsShort, GUILayout.Width(420));
                GUILayout.EndHorizontal();
            });

            if (AI.Config.assetGrouping == 0 || AI.Config.packageViewMode == 1)
            {
                UIBlock2("package.actions.sort", () =>
                {
                    EditorGUILayout.Space();
                    GUILayout.BeginHorizontal();
                    EditorGUIUtility.labelWidth = 50;
                    AI.Config.assetSorting = EditorGUILayout.Popup(CommonUIStyles.Content("Sort by:", "Specify how packages should be sorted"), AI.Config.assetSorting, _packageSortOptions, GUILayout.Width(160));
                    if (GUILayout.Button(AI.Config.sortAssetsDescending ? CommonUIStyles.Content("˅", "Descending") : CommonUIStyles.Content("˄", "Ascending"), GUILayout.Width(17)))
                    {
                        AI.Config.sortAssetsDescending = !AI.Config.sortAssetsDescending;
                        AI.SaveConfig();
                    }
                    GUILayout.EndHorizontal();
                });
            }

            if (AI.Config.packageViewMode == 0)
            {
                UIBlock2("package.actions.group", () =>
                {
                    EditorGUILayout.Space();
                    EditorGUIUtility.labelWidth = 60;
                    AI.Config.assetGrouping = EditorGUILayout.Popup(CommonUIStyles.Content("Group by:", "Select if packages should be grouped or not"), AI.Config.assetGrouping, _groupByOptions, GUILayout.Width(140));

                    EditorGUIUtility.labelWidth = 0;

                    if (AI.Config.assetGrouping > 0)
                    {
                        if (GUILayout.Button("Expand All", GUILayout.ExpandWidth(false)))
                        {
                            AssetTreeView.ExpandAll();
                        }
                        if (GUILayout.Button("Collapse All", GUILayout.ExpandWidth(false)))
                        {
                            AssetTreeView.SetSelection(new List<int>(), TreeViewSelectionOptions.FireSelectionChanged);
                            AssetTreeView.CollapseAll();
                        }
                    }
                });
            }

            if (EditorGUI.EndChangeCheck())
            {
                CreateAssetTree();
                AI.SaveConfig();
            }

            UIBlock("package.actions.savedsearches", () =>
            {
                if (GUILayout.Button(EditorGUIUtility.IconContent("d_saveas", "|Save current filters as a saved search to quickly pull up the same filter configuration later"), EditorStyles.miniButton))
                {
                    NameUI nameUI = new NameUI();
                    nameUI.Init(string.IsNullOrEmpty(_assetSearchPhrase) ? "My Package Search" : _assetSearchPhrase, SavePackageSearch);
                    PopupWindow.Show(GetPopupPositionAtMouse(), nameUI);
                }
                GUILayout.Space(2);
            });

            GUILayout.EndHorizontal();
            EditorGUILayout.Space();
            GUILayout.EndVertical();

            EditorGUILayout.Space();
            GUILayout.BeginHorizontal();

            // packages
            GUILayout.BeginVertical();
            if (AI.Config.packageViewMode == 0)
            {
                // Use automatic layout instead of hardcoded positioning
                GUILayout.BeginVertical(GUILayout.ExpandHeight(true));
                AssetTreeView.OnGUI(GUILayoutUtility.GetRect(0, 0, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true)));
                GUILayout.EndVertical();
            }
            else
            {
                _packageScrollPos = EditorGUILayout.BeginScrollView(_packageScrollPos, false, false);
                EditorGUI.BeginChangeCheck();
                float availableWidth = position.width - GetVisiblePackageInspectorWidth();
                PGrid.Draw(availableWidth, AI.Config.packageTileSize, 1f, UIStyles.packageTile, UIStyles.selectedPackageTile);
                if (_visiblePackageCount == 0)
                {
                    GUILayout.Label("No matching results", CommonUIStyles.whiteCenter, GUILayout.MinHeight(AI.Config.packageTileSize));
                }
                if (EditorGUI.EndChangeCheck() || (_allowLogic && _searchDone))
                {
                    _packageInspectorTab = 0;

                    // interactions
                    if (!_searchDone) PGrid.HandleMouseClicks();
                    HandleAssetGridSelectionChanged();
                }
                EditorGUILayout.EndScrollView();

                // Only auto-scroll after keyboard navigation occurred (allows free manual scrolling)
                if (PGrid.CheckAndResetKeyboardNavigation())
                {
                    PGrid.EnsureSelectedTileVisible(ref _packageScrollPos, CommonUIStyles.GetCurrentVisibleRect().height);
                }
            }

            // view settings
            GUILayout.BeginHorizontal();
            UILine("package.actions.viewmode", () =>
            {
                EditorGUI.BeginChangeCheck();
                AI.Config.packageViewMode = GUILayout.Toolbar(AI.Config.packageViewMode, _packageViewOptions, GUILayout.Width(50), GUILayout.Height(20));
                if (EditorGUI.EndChangeCheck())
                {
                    CreateAssetTree();
                    AI.SaveConfig();
                }
            });

            if (AI.Config.packageViewMode == 1)
            {
                UILine("package.actions.tilesize", () =>
                {
                    EditorGUILayout.Space();
                    EditorGUI.BeginChangeCheck();
                    AI.Config.packageTileSize = EditorGUILayout.IntSlider(AI.Config.packageTileSize, 50, 200, GUILayout.Width(150));
                    if (EditorGUI.EndChangeCheck()) AI.SaveConfig();
                });
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField($"{_visiblePackageCount:N0} packages", CommonUIStyles.centerLabel, GUILayout.Width(120));

            UILine("package.actions.scope", () =>
            {
                if (DrawSearchScopeToolbar())
                {
                    CreateAssetTree();
                    _requireSearchUpdate = true;
                }
            });

            GUILayout.FlexibleSpace();
            UILine("package.actions.sidebar", () =>
            {
                if (GUILayout.Button(CommonUIStyles.IconContent("unityeditor.scenehierarchywindow", "d_unityeditor.hierarchywindow", "|Show/Hide Details Inspector"), EditorStyles.miniButtonRight))
                {
                    AI.Config.showPackageSideBar = !AI.Config.showPackageSideBar;
                    _assetTreeView = null;
                    AI.SaveConfig();
                }
            });
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            if (!AI.Config.showPackageSideBar)
            {
                GUILayout.EndHorizontal();
                if (AI.Config.packageViewMode == 1)
                {
                    PGrid.HandleKeyboardCommands();
                }
                HandleTagShortcuts();
                return;
            }

            // inspector
            EditorGUILayout.Space();
            GUILayout.BeginVertical(GUILayout.Width(GetInspectorWidth()));
            GUILayout.BeginHorizontal();
            UIBlock("package.actions.expand", () =>
            {
                if (GUILayout.Button(CommonUIStyles.Content(AI.Config.expandPackageDetails ? ">" : "<", "Toggle between compact and expanded view"), GUILayout.Width(20)))
                {
                    AI.Config.expandPackageDetails = !AI.Config.expandPackageDetails;
                    AI.SaveConfig();
                    LoadMediaOnDemand(_selectedTreeAsset);
                }
            });
            List<string> strings = new List<string>
            {
                "Details",
                "Filters" + (IsPackageFilterActive() ? "*" : ""),
                "Stats"
            };
            _packageInspectorTab = CommonUIStyles.DrawTabBar(_packageInspectorTab, strings.ToArray());
            UIBlock("package.actions.settings", () =>
            {
                if (GUILayout.Button(EditorGUIUtility.IconContent("Settings", "|Manage View"), EditorStyles.miniButton, GUILayout.ExpandWidth(false), GUILayout.Height(18)))
                {
                    _packageInspectorTab = -1;
                }
                GUILayout.Space(2);
            });
            GUILayout.EndHorizontal();
            EditorGUILayout.Space();

            GUILayout.BeginVertical(GUILayout.Width(GetInspectorWidth()));
            _assetsScrollPos = EditorGUILayout.BeginScrollView(_assetsScrollPos, GUIStyle.none, GUI.skin.verticalScrollbar);
            switch (_packageInspectorTab)
            {
                case -1:
                    int width = 150;

                    EditorGUI.BeginChangeCheck();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Preferred Currency", "Currency to show asset prices in."), EditorStyles.boldLabel, GUILayout.Width(width));
                    AI.Config.currency = EditorGUILayout.Popup(AI.Config.currency, _currencyOptions, GUILayout.Width(70));
                    GUILayout.EndHorizontal();

                    EditorGUI.BeginChangeCheck();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Search In Group Names", "Will also search for the entered search text in group names (category, publisher, tags, etc.) when packages are grouped."), EditorStyles.boldLabel, GUILayout.Width(width));
                    AI.Config.searchPackageGroupNames = EditorGUILayout.Toggle(AI.Config.searchPackageGroupNames);
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Search In Description", "Will also search for the entered search text in package descriptions in addition to the name."), EditorStyles.boldLabel, GUILayout.Width(width));
                    AI.Config.searchPackageDescriptions = EditorGUILayout.Toggle(AI.Config.searchPackageDescriptions);
                    GUILayout.EndHorizontal();

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("UI", EditorStyles.largeLabel);

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Details when Compact", "Will show package details like media, description, dependencies etc. also when the inspector is not expanded."), EditorStyles.boldLabel, GUILayout.Width(width));
                    AI.Config.alwaysShowPackageDetails = EditorGUILayout.Toggle(AI.Config.alwaysShowPackageDetails);
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Details in Tabs", "Will group package details like media, description, dependencies etc. into tabs. Otherwise they are shown below each other."), EditorStyles.boldLabel, GUILayout.Width(width));
                    AI.Config.projectDetailTabs = EditorGUILayout.Toggle(AI.Config.projectDetailTabs);
                    GUILayout.EndHorizontal();

                    if (ShowAdvanced())
                    {
                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CommonUIStyles.Content("Colored Tree Tags", "Will render the Tags column in the package list with colored tag chips instead of plain text."), EditorStyles.boldLabel, GUILayout.Width(width));
                        AI.Config.showColoredPackageTreeTags = EditorGUILayout.Toggle(AI.Config.showColoredPackageTreeTags);
                        GUILayout.EndHorizontal();
                        if (EditorGUI.EndChangeCheck()) AI.SaveConfig();

                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CommonUIStyles.Content("Row Height", "Row height when no media column is visible."), EditorStyles.boldLabel, GUILayout.Width(width));
                        AI.Config.rowHeight = EditorGUILayout.DelayedIntField(AI.Config.rowHeight, GUILayout.Width(50));
                        EditorGUILayout.LabelField("px", EditorStyles.miniLabel);
                        GUILayout.EndHorizontal();

                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CommonUIStyles.Content("Row Height with Media", "Row height when media column is visible."), EditorStyles.boldLabel, GUILayout.Width(width));
                        AI.Config.rowHeightMedia = EditorGUILayout.DelayedIntField(AI.Config.rowHeightMedia, GUILayout.Width(50));
                        EditorGUILayout.LabelField("px", EditorStyles.miniLabel);
                        GUILayout.EndHorizontal();

                        EditorGUILayout.Space();

                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CommonUIStyles.Content("Maintain Media Aspect", "Will render media images with the correct aspect ratio and not squashed."), EditorStyles.boldLabel, GUILayout.Width(width));
                        AI.Config.mediaMaintainAspect = EditorGUILayout.Toggle(AI.Config.mediaMaintainAspect);
                        GUILayout.EndHorizontal();

                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CommonUIStyles.Content("Align Media Width", "Will make all media images the same width for a calmer UI, not utilizing the full row height potentially."), EditorStyles.boldLabel, GUILayout.Width(width));
                        AI.Config.mediaSameWidth = EditorGUILayout.Toggle(AI.Config.mediaSameWidth);
                        GUILayout.EndHorizontal();

                        if (!AI.Config.mediaSameWidth)
                        {
                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Media Height", "Space a media image should occupy vertically in percent."), EditorStyles.boldLabel, GUILayout.Width(width));
                            AI.Config.mediaYFillRatio = EditorGUILayout.DelayedIntField(AI.Config.mediaYFillRatio, GUILayout.Width(50));
                            EditorGUILayout.LabelField("%", EditorStyles.miniLabel);
                            GUILayout.EndHorizontal();
                        }

                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CommonUIStyles.Content("Media Spacing", "Horizontal space of media images."), EditorStyles.boldLabel, GUILayout.Width(width));
                        AI.Config.mediaXSpacing = EditorGUILayout.DelayedIntField(AI.Config.mediaXSpacing, GUILayout.Width(50));
                        EditorGUILayout.LabelField("px", EditorStyles.miniLabel);
                        GUILayout.EndHorizontal();

                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CommonUIStyles.Content("Media Corner Radius", "Roundness of corners of media images."), EditorStyles.boldLabel, GUILayout.Width(width));
                        AI.Config.mediaCornerRadius = EditorGUILayout.DelayedIntField(AI.Config.mediaCornerRadius, GUILayout.Width(50));
                        EditorGUILayout.LabelField("px", EditorStyles.miniLabel);
                        GUILayout.EndHorizontal();
                    }

                    if (EditorGUI.EndChangeCheck())
                    {
                        _requireAssetTreeRebuild = true;
                        AI.SaveConfig();
                    }

                    break;

                case 0:
                    if (_selectedTreeAsset != null)
                    {
                        DrawPackageInfo(_selectedTreeAsset, true);
                    }
                    else if (_selectedTreeAsset == null && _selectedTreeAssets != null && _selectedTreeAssets.Count > 0)
                    {
                        DrawBulkPackageActions(_selectedTreeAssets, _assetTreeSubPackageCount, _assetBulkTags, _assetTreeSelectionSize, _assetTreeSelectionTotalCosts, _assetTreeSelectionStoreCosts, true);
                    }
                    else
                    {
                        EditorGUILayout.HelpBox("Select one or more packages to see details.", MessageType.Info);
                    }
                    break;

                case 1:
                    int labelWidth = 85;

                    EditorGUI.BeginChangeCheck();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Packages", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.packagesListing = EditorGUILayout.Popup(AI.Config.packagesListing, _packageListingOptions, GUILayout.ExpandWidth(true));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("SRPs", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
                    AI.Config.assetSRPs = EditorGUILayout.Popup(AI.Config.assetSRPs, _srpOptions, GUILayout.ExpandWidth(true));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Deprecation", "Filter by deprecation status or China Store migration. China Store affected assets are still available for download but will receive no more updates."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.assetDeprecation = EditorGUILayout.Popup(AI.Config.assetDeprecation, _deprecationOptions, GUILayout.ExpandWidth(true));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Package Tag", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
                    _selectedPkgTag = SearchablePopup.PopupField(_selectedPkgTag, _tagPopupItems, "pkgFilter_tag", AI.Config.colorTagFilterClosedField, false, true, GUILayout.ExpandWidth(true));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Publisher", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
                    _selectedPkgPublisher = SearchablePopup.PopupField(_selectedPkgPublisher, _publisherNames, "pkgFilter_publisher", false, false, GUILayout.ExpandWidth(true), GUILayout.MinWidth(150));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Category", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
                    _selectedPkgCategory = SearchablePopup.PopupField(_selectedPkgCategory, _categoryNames, "pkgFilter_category", false, false, GUILayout.ExpandWidth(true), GUILayout.MinWidth(150));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Updated", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
                    _selectedPkgUpdateDateOption = EditorGUILayout.Popup(_selectedPkgUpdateDateOption, _updateDateOptions, GUILayout.Width(labelWidth + 2));

                    if (_selectedPkgUpdateDateOption == 6) // Before Date
                    {
                        string dateStr = _pkgUpdateBeforeDate?.ToString("yyyy-MM-dd") ?? DateTime.Now.ToString("yyyy-MM-dd");
                        string newDateStr = EditorGUILayout.DelayedTextField(dateStr, GUILayout.Width(100));
                        if (DateTime.TryParse(newDateStr, out DateTime parsedDate))
                        {
                            _pkgUpdateBeforeDate = parsedDate;
                        }
                    }
                    else if (_selectedPkgUpdateDateOption == 7) // After Date
                    {
                        string dateStr = _pkgUpdateAfterDate?.ToString("yyyy-MM-dd") ?? DateTime.Now.ToString("yyyy-MM-dd");
                        string newDateStr = EditorGUILayout.DelayedTextField(dateStr, GUILayout.Width(100));
                        if (DateTime.TryParse(newDateStr, out DateTime parsedDate))
                        {
                            _pkgUpdateAfterDate = parsedDate;
                        }
                    }
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Purchased", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
                    _selectedPkgPurchaseDateOption = EditorGUILayout.Popup(_selectedPkgPurchaseDateOption, _purchaseDateOptions, GUILayout.Width(labelWidth + 2));

                    if (_selectedPkgPurchaseDateOption == 6) // Before Date
                    {
                        string dateStr = _pkgPurchaseBeforeDate?.ToString("yyyy-MM-dd") ?? DateTime.Now.ToString("yyyy-MM-dd");
                        string newDateStr = EditorGUILayout.DelayedTextField(dateStr, GUILayout.Width(100));
                        if (DateTime.TryParse(newDateStr, out DateTime parsedDate))
                        {
                            _pkgPurchaseBeforeDate = parsedDate;
                        }
                    }
                    else if (_selectedPkgPurchaseDateOption == 7) // After Date
                    {
                        string dateStr = _pkgPurchaseAfterDate?.ToString("yyyy-MM-dd") ?? DateTime.Now.ToString("yyyy-MM-dd");
                        string newDateStr = EditorGUILayout.DelayedTextField(dateStr, GUILayout.Width(100));
                        if (DateTime.TryParse(newDateStr, out DateTime parsedDate))
                        {
                            _pkgPurchaseAfterDate = parsedDate;
                        }
                    }
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Price", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
                    _selectedPkgPriceOption = EditorGUILayout.Popup(_selectedPkgPriceOption, _priceOptions, GUILayout.Width(labelWidth + 2));
                    if (_selectedPkgPriceOption == 4 || _selectedPkgPriceOption == 5)
                    {
                        _pkgSearchPrice = EditorGUILayout.DelayedFloatField(_pkgSearchPrice, GUILayout.Width(58));
                        string currencySymbol = AI.Config.currency == 0 ? "�" : (AI.Config.currency == 1 ? "$" : "�");
                        EditorGUILayout.LabelField(currencySymbol, GUILayout.Width(15));
                    }
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Package Size", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
                    _selectedPkgSizeOption = EditorGUILayout.Popup(_selectedPkgSizeOption, _packageSizeOptions, GUILayout.Width(labelWidth + 2));
                    if (_selectedPkgSizeOption == 2 || _selectedPkgSizeOption == 3)
                    {
                        _pkgSizeMB = EditorGUILayout.DelayedFloatField(_pkgSizeMB, GUILayout.Width(58));
                        EditorGUILayout.LabelField("MB", GUILayout.Width(25));
                    }
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Unity Version", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
                    _selectedPkgUnityVersionOption = EditorGUILayout.Popup(_selectedPkgUnityVersionOption, _unityVersionOptions, GUILayout.ExpandWidth(true));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Maintenance", "A collection of various special-purpose filters"), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    _selectedMaintenance = (PackageSearch.MaintenanceOption)EditorGUILayout.Popup((int)_selectedMaintenance, _maintenanceOptions, GUILayout.ExpandWidth(true));
                    GUILayout.EndHorizontal();

                    if (EditorGUI.EndChangeCheck())
                    {
                        AI.SaveConfig();
                        _requireAssetTreeRebuild = true;
                    }

                    if (_selectedMaintenance == PackageSearch.MaintenanceOption.Duplicate)
                    {
                        EditorGUILayout.Space();
                        if (GUILayout.Button("Select Older Duplicates")) SelectOlderDuplicates();
                        if (GUILayout.Button("Select Newer Duplicates")) SelectNewerDuplicates();
                        if (GUILayout.Button("Select Without Relative Location")) SelectWithoutRelativeLocation();
                        if (GUILayout.Button("Select Custom Packages")) SelectCustomPackages();
                    }

                    EditorGUILayout.Space();
                    if (IsPackageFilterActive() && GUILayout.Button("Reset Filters", CommonUIStyles.mainButton))
                    {
                        ResetPackageFilters();
                    }
                    break;

                case 2:
                    DrawPackageStats(false);
                    break;
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndScrollView();
            GUILayout.EndVertical();

            if (!ShowAdvanced() && AI.Config.showHints)
            {
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField("Hold down CTRL for additional options.", EditorStyles.centeredGreyMiniLabel);
            }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            if (AI.Config.packageViewMode == 1)
            {
                PGrid.HandleKeyboardCommands();
            }
            HandleTagShortcuts();
        }

        private void SelectOlderDuplicates()
        {
            SelectDuplicatesByAge(true);
        }

        private void SelectNewerDuplicates()
        {
            SelectDuplicatesByAge(false);
        }

        private void SelectDuplicatesByAge(bool selectOlder)
        {
            List<AssetInfo> assets = GetSelectablePackages();

            // Find duplicate groups by ForeignId
            IEnumerable<IGrouping<int, AssetInfo>> duplicateGroupsByForeignId = assets
                .Where(a => a.ForeignId > 0)
                .GroupBy(a => a.ForeignId)
                .Where(g => g.Count() > 1);

            // Find duplicate groups by Location
            IEnumerable<IGrouping<string, AssetInfo>> duplicateGroupsByLocation = assets
                .Where(a => !string.IsNullOrEmpty(a.Location))
                .GroupBy(a => a.Location)
                .Where(g => g.Count() > 1);

            HashSet<int> idsToSelect = new HashSet<int>();

            // Process ForeignId duplicate groups
            foreach (IGrouping<int, AssetInfo> group in duplicateGroupsByForeignId)
            {
                IEnumerable<AssetInfo> duplicatesToSelect = selectOlder
                    ? group.OrderBy(a => a.AssetId).Take(1) // Select older (first)
                    : group.OrderBy(a => a.AssetId).Skip(1); // Select newer (all but first)

                foreach (AssetInfo asset in duplicatesToSelect)
                {
                    idsToSelect.Add(asset.AssetId);
                }
            }

            // Process Location duplicate groups
            foreach (IGrouping<string, AssetInfo> group in duplicateGroupsByLocation)
            {
                IEnumerable<AssetInfo> duplicatesToSelect = selectOlder
                    ? group.OrderBy(a => a.AssetId).Take(1) // Select older (first)
                    : group.OrderBy(a => a.AssetId).Skip(1); // Select newer (all but first)

                foreach (AssetInfo asset in duplicatesToSelect)
                {
                    idsToSelect.Add(asset.AssetId);
                }
            }

            if (idsToSelect.Count > 0)
            {
                ApplyPackageSelection(assets.Where(a => idsToSelect.Contains(a.AssetId)).ToList());
            }
        }

        private void SelectWithoutRelativeLocation()
        {
            List<AssetInfo> assets = GetSelectablePackages();

            List<AssetInfo> selectedAssets = assets
                .Where(a => !string.IsNullOrEmpty(a.Location) && !a.Location.StartsWith("[ac]"))
                .ToList();

            if (selectedAssets.Count > 0)
            {
                ApplyPackageSelection(selectedAssets);
            }
        }

        private void SelectCustomPackages()
        {
            List<AssetInfo> selectedAssets = GetSelectablePackages().Where(a => a.AssetSource == Asset.Source.CustomPackage).ToList();

            if (selectedAssets.Count > 0)
            {
                ApplyPackageSelection(selectedAssets);
            }
        }

        private void ApplyPackageSelection(List<AssetInfo> selectedAssets)
        {
            if (selectedAssets == null || selectedAssets.Count == 0) return;

            if (AI.Config.packageViewMode == 0)
            {
                List<int> idsToSelect = selectedAssets.Select(a => a.AssetId).ToList();
                AssetTreeView.SetSelection(idsToSelect, TreeViewSelectionOptions.RevealAndFrame);
                OnAssetTreeSelectionChanged(idsToSelect);
            }
            else
            {
                if (PGrid.packages == null) CreateAssetTree();
                PGrid.SetVisualBulkSelection(selectedAssets);
                HandleAssetGridSelectionChanged();
            }
        }

        private List<AssetInfo> GetSelectablePackages()
        {
            if (AI.Config.packageViewMode == 1)
            {
                if (PGrid.packages == null) CreateAssetTree();
                if (PGrid.packages != null) return PGrid.packages.Where(a => a != null && a.AssetId > 0).ToList();
            }

            return AssetTreeModel.GetData().Where(a => a.AssetId > 0).ToList();
        }

        private void CreatePackage(Asset.Source source, PackageSource packageSource = PackageSource.Unknown)
        {
            AssetInfo info = new AssetInfo();
            info.AssetSource = source;
            info.PackageSource = packageSource;

            PackageUI packageUI = PackageUI.ShowWindow();
            packageUI.Init(info, _ =>
            {
                AI.TriggerPackageRefresh();
            });
        }

        private bool IsPackageFilterActive()
        {
            return AI.Config.packagesListing != 1 || AI.Config.assetDeprecation > 0 || _selectedMaintenance != PackageSearch.MaintenanceOption.All || AI.Config.assetSRPs > 0
                || _selectedPkgPriceOption > 0 || _selectedPkgTag > 0 || _selectedPkgPublisher > 0 || _selectedPkgCategory > 0
                || _selectedPkgUpdateDateOption > 0 || _selectedPkgPurchaseDateOption > 0 || _selectedPkgSizeOption > 0 || _selectedPkgUnityVersionOption > 0;
        }

        private void ResetPackageFilters(bool setType = true)
        {
            if (setType) AI.Config.packagesListing = 1;
            AI.Config.assetDeprecation = 0;
            AI.Config.assetSRPs = 0;
            _selectedMaintenance = PackageSearch.MaintenanceOption.All;
            _selectedPkgPriceOption = 0;
            _pkgSearchPrice = 0f;
            _selectedPkgTag = 0;
            _selectedPkgPublisher = 0;
            _selectedPkgCategory = 0;
            _selectedPkgUpdateDateOption = 0;
            _pkgUpdateBeforeDate = null;
            _pkgUpdateAfterDate = null;
            _selectedPkgPurchaseDateOption = 0;
            _pkgPurchaseBeforeDate = null;
            _pkgPurchaseAfterDate = null;
            _selectedPkgSizeOption = 0;
            _pkgSizeMB = 0f;
            _selectedPkgUnityVersionOption = 0;
            _requireAssetTreeRebuild = true;

            AI.SaveConfig();
        }

        private void DrawPackageStats(bool allowCollapse)
        {
            int labelWidth = 130;
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Total Packages", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
            EditorGUILayout.LabelField($"{_assets.Count:N0}", EditorStyles.label, GUILayout.Width(50));
            if (allowCollapse)
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(AI.Config.showPackageStatsDetails ? "Hide Details" : "Show Details", GUILayout.ExpandWidth(false)))
                {
                    AI.Config.showPackageStatsDetails = !AI.Config.showPackageStatsDetails;
                    AI.SaveConfig();
                }
            }
            GUILayout.EndHorizontal();
            if (!allowCollapse || AI.Config.showPackageStatsDetails)
            {
                GUILabelWithText($"{CommonUIStyles.INDENT}Indexed", $"{_stats.IndexedPackages:N0}/{_stats.IndexablePackages:N0}", labelWidth, "Indexable packages depend on configuration settings and package availability. Abandoned packages cannot be downloaded and indexed anymore if they are not already in the cache. If registry packages are not activated for indexing, they will also be left unindexed. If active, they can only be indexed if in the cache, which means they must have been installed at least once sometime in a project on this machine. Switch to the Not Indexed maintenance view to see all non-indexed packages and discover the reason for each.");
                if (_stats.PurchasedAssets > 0) GUILabelWithText($"{CommonUIStyles.INDENT}Asset Store", $"{_stats.PurchasedAssets:N0}", labelWidth);
                if (_stats.RegistryPackages > 0) GUILabelWithText($"{CommonUIStyles.INDENT}Registries", $"{_stats.RegistryPackages:N0}", labelWidth);
                if (_stats.CustomPackages > 0) GUILabelWithText($"{CommonUIStyles.INDENT}Other Sources", $"{_stats.CustomPackages:N0}", labelWidth);
                if (_stats.DeprecatedPackages > 0) GUILabelWithText($"{CommonUIStyles.INDENT}Deprecated", $"{_stats.DeprecatedPackages:N0}", labelWidth);
                if (_stats.AbandonedPackages > 0) GUILabelWithText($"{CommonUIStyles.INDENT}Abandoned", $"{_stats.AbandonedPackages:N0}", labelWidth);
                if (_stats.ExcludedPackages > 0)
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content($"{CommonUIStyles.INDENT}Excluded"), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    EditorGUILayout.LabelField(CommonUIStyles.Content($"{_stats.ExcludedPackages:N0}"), EditorStyles.label, GUILayout.Width(50));
                    if (ShowAdvanced())
                    {
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button(EditorGUIUtility.IconContent("d_animationvisibilitytoggleon", "|Show")))
                        {
                            ShowPackageMaintenance(PackageSearch.MaintenanceOption.Excluded);
                        }
                    }
                    GUILayout.EndHorizontal();
                }
                if (_stats.SubPackages > 0) GUILabelWithText($"{CommonUIStyles.INDENT}Sub-Packages", $"{_stats.SubPackages:N0}", labelWidth);
            }
            if (_stats.TotalFiles > 0) GUILabelWithText("Indexed Files", $"{_stats.TotalFiles:N0}", labelWidth);
        }

        private void DrawBulkPackageActions(List<AssetInfo> bulkAssets, long bulkSubAssetCount, Dictionary<string, Tuple<int, Color>> bulkTags, long size, float totalCosts, float storeCosts, bool useScroll)
        {
            int labelWidth = 130;
            bool mainUsed = false;

            GUILayout.BeginVertical(GUILayout.ExpandHeight(false));
            EditorGUILayout.LabelField("Bulk Info", EditorStyles.largeLabel);
            UIBlock("package.bulk.count", () => GUILabelWithText("Selected Items", $"{bulkAssets.Count - bulkSubAssetCount:N0}", labelWidth));
            if (bulkSubAssetCount > 0) UIBlock("package.bulk.childcount", () => GUILabelWithText($"{CommonUIStyles.INDENT}Sub-Packages", $"{bulkSubAssetCount:N0}", labelWidth));
            UIBlock("package.bulk.size", () => GUILabelWithText("Size on Disk", EditorUtility.FormatBytes(size), labelWidth));
            if (totalCosts > 0)
            {
                UIBlock("package.bulk.price", () =>
                {
                    GUILabelWithText("Total Price", bulkAssets[0].GetPriceText(totalCosts), labelWidth);
                });
            }
            if (storeCosts > 0 && totalCosts > storeCosts)
            {
                UIBlock("package.bulk.storeprice", () => GUILabelWithText($"{CommonUIStyles.INDENT}Asset Store", bulkAssets[0].GetPriceText(storeCosts), labelWidth));
                UIBlock("package.bulk.otherprice", () => GUILabelWithText($"{CommonUIStyles.INDENT}Other Sources", bulkAssets[0].GetPriceText(totalCosts - storeCosts), labelWidth));
                EditorGUILayout.Space();
            }
            GUILayout.EndVertical();

            labelWidth = 100;
            EditorGUILayout.Space();
            GUILayout.BeginVertical();
            EditorGUILayout.LabelField("Bulk Actions", EditorStyles.largeLabel);
            if (useScroll) _bulkScrollPos = EditorGUILayout.BeginScrollView(_bulkScrollPos, false, false);
            UpdateObserver updateObserver = AI.GetObserver();
            if (!updateObserver.PrioInitializationDone)
            {
                int progress = Mathf.RoundToInt(updateObserver.PrioInitializationProgress * 100f);
                EditorGUILayout.HelpBox($"Gathering data (*): {progress}%", MessageType.Info);
                EditorGUILayout.Space();
            }

            UIBlock("package.bulk.actions.extract", () =>
            {
                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Keep Cached", "Will keep the package extracted in the cache to minimize access delays at the cost of more hard disk space."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                if (GUILayout.Button("All", GUILayout.ExpandWidth(false)))
                {
                    bulkAssets.ForEach(info => AI.SetAssetExtraction(info, true));
                }
                if (GUILayout.Button("None", GUILayout.ExpandWidth(false)))
                {
                    bulkAssets.ForEach(info => AI.SetAssetExtraction(info, false));
                }
                GUILayout.EndHorizontal();
            });

            if (AI.Actions.CreateBackups)
            {
                UIBlock("package.bulk.actions.backup", () =>
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Backup", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    if (GUILayout.Button("All", GUILayout.ExpandWidth(false)))
                    {
                        bulkAssets.ForEach(info => AI.SetAssetBackup(info, true, false));
                        AI.TriggerPackageRefresh();
                    }
                    if (GUILayout.Button("None", GUILayout.ExpandWidth(false)))
                    {
                        bulkAssets.ForEach(info => AI.SetAssetBackup(info, false, false));
                        AI.TriggerPackageRefresh();
                    }
                    GUILayout.EndHorizontal();
                });
            }

            if (AI.Actions.CreateAICaptions)
            {
                UIBlock("package.bulk.actions.aiusage", () =>
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("AI Captions", "Activate to create backups for this asset (done after every update cycle)."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    if (GUILayout.Button("All", GUILayout.ExpandWidth(false)))
                    {
                        bulkAssets.ForEach(info => AI.SetAssetAIUse(info, true, false));
                        AI.TriggerPackageRefresh();
                    }
                    if (GUILayout.Button("None", GUILayout.ExpandWidth(false)))
                    {
                        bulkAssets.ForEach(info => AI.SetAssetAIUse(info, false, false));
                        AI.TriggerPackageRefresh();
                    }
                    GUILayout.EndHorizontal();
                });
            }

            UIBlock("package.bulk.actions.exclude", () =>
            {
                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Exclude", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                if (GUILayout.Button("All", GUILayout.ExpandWidth(false)))
                {
                    bulkAssets.ForEach(info => AI.SetAssetExclusion(info, true));
                    _requireLookupUpdate = ChangeImpact.Write;
                    _requireSearchUpdate = true;
                    _requireAssetTreeRebuild = true;
                }
                if (GUILayout.Button("None", GUILayout.ExpandWidth(false)))
                {
                    bulkAssets.ForEach(info => AI.SetAssetExclusion(info, false));
                    _requireLookupUpdate = ChangeImpact.Write;
                    _requireSearchUpdate = true;
                    _requireAssetTreeRebuild = true;
                }
                GUILayout.EndHorizontal();
            });

            // determine download status, a bit expensive but happens only in bulk selections
            ProfileMarkerBulk.Begin();
            int notDownloaded = 0;
            int updateAvailable = 0;
            int packageUpdateAvailable = 0;
            int updateAvailableButCustom = 0;
            int downloading = 0;
            int paused = 0;
            long remainingBytes = 0;
            foreach (AssetInfo info in bulkAssets.Where(a => a.ParentId == 0 && a.PackageDownloader != null && (a.WasOutdated || !a.IsDownloaded || a.IsUpdateAvailable(_assets, false))))
            {
                if (info.AssetSource == Asset.Source.RegistryPackage)
                {
                    if (info.IsUpdateAvailable()) packageUpdateAvailable++;
                }
                else
                {
                    AssetDownloadState state = info.PackageDownloader.GetState();
                    switch (state.state)
                    {
                        case AssetDownloader.State.Unavailable:
                            notDownloaded++;
                            break;

                        case AssetDownloader.State.Downloading:
                            downloading++;
                            remainingBytes += state.bytesTotal - state.bytesDownloaded;
                            break;

                        case AssetDownloader.State.Paused:
                            paused++;
                            break;

                        case AssetDownloader.State.UpdateAvailable:
                            updateAvailable++;
                            break;

                        case AssetDownloader.State.Unknown:
                            if (info.AssetSource == Asset.Source.CustomPackage && info.IsUpdateAvailable(_assets))
                            {
                                updateAvailableButCustom++;
                            }
                            break;
                    }
                }
            }
            ProfileMarkerBulk.End();

            string initializing = updateObserver.PrioInitializationDone ? "" : "*";
            if (notDownloaded > 0)
            {
                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Not Cached" + initializing, EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                if (GUILayout.Button($"Download remaining {notDownloaded}", GUILayout.ExpandWidth(false)))
                {
                    foreach (AssetInfo info in bulkAssets.Where(a => !a.IsDownloaded))
                    {
                        info.PackageDownloader.Download(false);
                    }
                }
                GUILayout.EndHorizontal();
            }
            if (updateAvailable > 0)
            {
                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Updates" + initializing, EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                if (CommonUIStyles.MainButton(ref mainUsed, "Download " + (downloading > 0 ? "remaining " : "") + updateAvailable, GUILayout.ExpandWidth(false)))
                {
                    foreach (AssetInfo info in bulkAssets.Where(a => a.IsUpdateAvailable(_assets) && a.PackageDownloader != null))
                    {
                        AssetDownloader.State state = info.PackageDownloader.GetState().state;
                        if (state == AssetDownloader.State.UpdateAvailable)
                        {
                            info.WasOutdated = true;
                            info.PackageDownloader.Download(false);
                        }
                    }
                }
                GUILayout.EndHorizontal();
            }
            if (packageUpdateAvailable > 0)
            {
                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Packages" + initializing, EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                if (GUILayout.Button($"Update {packageUpdateAvailable} registry packages", GUILayout.ExpandWidth(false)))
                {
                    List<AssetInfo> bulkList = bulkAssets
                        .Where(a => a.AssetSource == Asset.Source.RegistryPackage && a.IsUpdateAvailable())
                        .ToList();
                    ImportUI importUI = ImportUI.ShowWindow();
                    importUI.Init(bulkList, true);
                }
                GUILayout.EndHorizontal();
            }
            if (updateAvailableButCustom > 0)
            {
                GUILayout.BeginHorizontal();
                EditorGUILayout.HelpBox($"{updateAvailableButCustom}{initializing} updates cannot be performed since the assets are local custom packages and not from the Asset Store.", MessageType.Info);
                GUILayout.EndHorizontal();
            }

            if (downloading > 0)
            {
                GUILabelWithText("Downloading" + initializing, $"{downloading}", labelWidth);
                GUILabelWithText("Remaining" + initializing, $"{EditorUtility.FormatBytes(remainingBytes)}", labelWidth);
            }
            if (paused > 0)
            {
                GUILabelWithText("Paused", $"{paused}", labelWidth);
            }
            EditorGUILayout.Space();

            int packageCount = bulkAssets.Count(a => a.AssetSource == Asset.Source.RegistryPackage);
            int installedPackageCount = bulkAssets.Count(a => a.AssetSource == Asset.Source.RegistryPackage && a.InstalledPackageVersion() != null);
            string buttonText = "Import...";
            if (packageCount == bulkAssets.Count)
            {
                buttonText = "Install...";
            }
            else if (packageCount > 0)
            {
                buttonText = "Import & Install...";
            }
            if (bulkAssets.Count > installedPackageCount)
            {
                UIBlock("package.bulk.actions.import", () =>
                {
                    if (GUILayout.Button(buttonText, CommonUIStyles.mainButton))
                    {
                        ImportUI importUI = ImportUI.ShowWindow();
                        importUI.Init(bulkAssets);
                    }
                });
            }
            if (installedPackageCount > 0)
            {
                UIBlock("package.bulk.actions.uninstall", () =>
                {
                    if (GUILayout.Button($"Uninstall {installedPackageCount} Package{(installedPackageCount > 1 ? "s" : "")}"))
                    {
                        if (EditorUtility.DisplayDialog("Confirm", $"Are you sure you want to uninstall {installedPackageCount} packages?", "OK", "Cancel"))
                        {
                            RemovalUI removalUI = RemovalUI.ShowWindow();
                            removalUI.Init(bulkAssets.Where(a => a.AssetSource == Asset.Source.RegistryPackage && a.InstalledPackageVersion() != null).ToList());
                        }
                    }
                });
            }
            UIBlock("package.bulk.actions.openlocation", () =>
            {
                if (GUILayout.Button(CommonUIStyles.Content("Open Package Locations...")))
                {
                    IEnumerable<AssetInfo> assetInfos = bulkAssets.Where(info => info.ParentId <= 0);
                    bool show = true;
                    if (assetInfos.Count() > AI.Config.massOpenWarnThreshold)
                    {
                        show = EditorUtility.DisplayDialog("Open Locations", $"You are about to open {assetInfos.Count()} locations. This may take a while and will open a lot of windows.\n\nDo you want to continue?", "Continue", "Cancel");
                    }
                    if (show) assetInfos.ForEach(info => { EditorUtility.RevealInFinder(info.GetLocation(true)); });
                }
            });

            UIBlock("package.bulk.actions.refreshmetadata", () =>
            {
                if (GUILayout.Button(CommonUIStyles.Content("Refresh Metadata", "Will fetch most up-to-date metadata from the Asset Store.")))
                {
                    RefreshMetadataAsync(bulkAssets);
                }
            });
            UIBlock("package.bulk.actions.recreatepreviews", () =>
            {
                if (GUILayout.Button("Previews Wizard..."))
                {
                    PreviewWizardUI previewsUI = PreviewWizardUI.ShowWindow();
                    previewsUI.Init(bulkAssets, _assets);
                }
            });
            UIBlock("package.bulk.actions.export", () =>
            {
                if (GUILayout.Button("Export Packages..."))
                {
                    ExportUI exportUI = ExportUI.ShowWindow();
                    exportUI.Init(bulkAssets, false, 0, assetMchState.visibleColumns);
                }
            });

            EditorGUILayout.Space();
            UIBlock("package.bulk.actions.reindexnextrun", () =>
            {
                if (GUILayout.Button(CommonUIStyles.Content("Reindex Packages on Next Run", "Will mark packages as outdated and force a reindex the next time Update Index is called on the Settings tab.")))
                {
                    if (EditorUtility.DisplayDialog("Confirm", $"Are you sure you want to reindex {bulkAssets.Count} packages? They will be parsed once you start the next action run.", "OK", "Cancel"))
                    {
                        bulkAssets.ForEach(info => Assets.ForgetPackage(info, true));
                        _requireLookupUpdate = ChangeImpact.Write;
                        _requireSearchUpdate = true;
                        _requireAssetTreeRebuild = true;
                    }
                }
            });
            UIBlock("package.bulk.actions.delete", () =>
            {
                if (GUILayout.Button(CommonUIStyles.Content("Delete Packages...", "Delete the packages from the database and optionally the file system.")))
                {
                    PackageDeletionUI deletionUI = PackageDeletionUI.ShowWindow(true);
                    deletionUI.Init(bulkAssets, () =>
                    {
                        _selectedTreeAsset = null;
                        _requireLookupUpdate = ChangeImpact.Write;
                        _requireAssetTreeRebuild = true;
                        _requireSearchUpdate = true;
                    });
                }
            });

            UIBlock("package.bulk.actions.tag", () =>
            {
                DrawAddPackageTag(bulkAssets);

                float x = 0f;
                foreach (KeyValuePair<string, Tuple<int, Color>> bulkTag in bulkTags)
                {
                    string tagName = $"{bulkTag.Key} ({bulkTag.Value.Item1})";
                    x = CalcTagSize(x, tagName);
                    UIStyles.DrawTag(tagName, bulkTag.Value.Item2, () =>
                    {
                        Tagging.RemovePackageAssignments(bulkAssets, bulkTag.Key, true);
                        _requireAssetTreeRebuild = true;
                    }, UIStyles.TagStyle.Remove, GetMaxDetailTagWidth());
                }
                GUILayout.EndHorizontal();
            });

            if (useScroll) EditorGUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void CreateAssetTree()
        {
            if (AI.DEBUG_MODE) Debug.LogWarning("CreateAssetTree");

            // sync sort state between column headers and model
            if (assetMchState != null)
            {
                try
                {
                    assetMchState.sortedColumnIndex = AI.Config.assetSorting;
                    if (assetMchState.columns.Length > assetMchState.sortedColumnIndex)
                    {
                        assetMchState.columns[assetMchState.sortedColumnIndex].sortedAscending = !AI.Config.sortAssetsDescending;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"Failed to sync asset tree sort state: {e.Message}");
                }
            }

            _requireAssetTreeRebuild = false;
            _visiblePackageCount = 0;
            List<AssetInfo> data = new List<AssetInfo>();
            AssetInfo root = new AssetInfo().WithTreeData("Root", depth: -1);
            data.Add(root);

            // Use PackageSearch to apply all filters
            PackageSearch.Options opt = new PackageSearch.Options
            {
                SearchPhrase = _assetSearchPhrase,
                SelectedPackageListing = AI.Config.packagesListing,
                SelectedSRPs = AI.Config.assetSRPs,
                SelectedDeprecation = AI.Config.assetDeprecation,
                SelectedMaintenance = _selectedMaintenance,
                OnlyInProject = SearchScopeModel.IsProjectOnly(GetConfiguredSearchScope()),
                UsedPackages = _usedPackages,
                AllAssets = _assets,
                SearchDescription = AI.Config.searchPackageDescriptions,
                SearchGroupNames = AI.Config.searchPackageGroupNames,
                CurrentGrouping = AI.Config.packageViewMode == 0 ? AI.Config.assetGrouping : 0,
                SelectedPriceOption = _selectedPkgPriceOption,
                SearchPrice = _pkgSearchPrice,
                SelectedPackageTag = _selectedPkgTag,
                TagNames = _tagNames,
                Tags = _tags,
                SelectedPublisher = _selectedPkgPublisher,
                PublisherNames = _publisherNames,
                SelectedCategory = _selectedPkgCategory,
                CategoryNames = _categoryNames,
                SelectedUpdateDateOption = _selectedPkgUpdateDateOption,
                UpdateBeforeDate = _pkgUpdateBeforeDate,
                UpdateAfterDate = _pkgUpdateAfterDate,
                SelectedPurchaseDateOption = _selectedPkgPurchaseDateOption,
                PurchaseBeforeDate = _pkgPurchaseBeforeDate,
                PurchaseAfterDate = _pkgPurchaseAfterDate,
                SelectedPackageSizeOption = _selectedPkgSizeOption,
                PackageSizeMB = _pkgSizeMB,
                SelectedUnityVersionOption = _selectedPkgUnityVersionOption
            };

            // Handle OnlyInProject calculation if needed
            if (!_usageCalculationDone || _usedPackages == null)
            {
                CalculateAssetUsageAutomatically();
            }

            PackageSearch.Result result = PackageSearch.Execute(opt);
            IEnumerable<AssetInfo> filteredAssets = result.Packages;

            string[] lastGroups = Array.Empty<string>();
            int catIdx = 0;
            IOrderedEnumerable<AssetInfo> orderedAssets;

            // grouping not supported for grid view
            int usedGrouping = AI.Config.packageViewMode == 0 ? AI.Config.assetGrouping : 0;
            switch (usedGrouping)
            {
                case 0: // none
                    orderedAssets = AddPackageOrdering(filteredAssets);
                    foreach (AssetInfo a in orderedAssets)
                    {
                        data.Add(a.WithTreeData(a.GetDisplayName(), a.AssetId));
                    }
                    break;

                case 2: // category
                    orderedAssets = filteredAssets.OrderBy(a => a.GetDisplayCategory(), new PathComparer())
                        .ThenBy(a => a.GetDisplayName(), StringComparer.OrdinalIgnoreCase);

                    string[] noCat = {"-no category-"};
                    foreach (AssetInfo info in orderedAssets)
                    {
                        // create hierarchy
                        string[] cats = string.IsNullOrEmpty(info.GetDisplayCategory()) ? noCat : info.GetDisplayCategory().Split('/');

                        lastGroups = AddCategorizedItem(cats, lastGroups, data, info, ref catIdx);
                    }
                    break;

                case 3: // publisher
                    IOrderedEnumerable<AssetInfo> orderedAssetsPub = filteredAssets.OrderBy(a => a.GetDisplayPublisher(), StringComparer.OrdinalIgnoreCase)
                        .ThenBy(a => a.GetDisplayName(), StringComparer.OrdinalIgnoreCase);

                    string[] noPub = {"-no publisher-"};
                    foreach (AssetInfo info in orderedAssetsPub)
                    {
                        // create hierarchy
                        string[] pubs = string.IsNullOrEmpty(info.GetDisplayPublisher()) ? noPub : new[] {info.GetDisplayPublisher()};

                        lastGroups = AddCategorizedItem(pubs, lastGroups, data, info, ref catIdx);
                    }
                    break;

                case 4: // tags
                    List<Tag> tags = Tagging.LoadTags();
                    foreach (Tag tag in tags)
                    {
                        IOrderedEnumerable<AssetInfo> taggedAssets = filteredAssets
                            .Where(a => a.PackageTags != null && a.PackageTags.Any(t => t.Name == tag.Name))
                            .OrderBy(a => a.GetDisplayName(), StringComparer.OrdinalIgnoreCase);

                        string[] cats = {tag.Name};
                        foreach (AssetInfo info in taggedAssets)
                        {
                            // create hierarchy
                            lastGroups = AddCategorizedItem(cats, lastGroups, data, info, ref catIdx);
                        }
                    }

                    IOrderedEnumerable<AssetInfo> remainingAssets = filteredAssets
                        .Where(a => a.PackageTags == null || a.PackageTags.Count == 0)
                        .OrderBy(a => a.GetDisplayName(), StringComparer.OrdinalIgnoreCase);
                    string[] untaggedCat = {"-untagged-"};
                    foreach (AssetInfo info in remainingAssets)
                    {
                        lastGroups = AddCategorizedItem(untaggedCat, lastGroups, data, info, ref catIdx);
                    }
                    break;

                case 5: // state
                    IOrderedEnumerable<AssetInfo> orderedAssetsState = filteredAssets.OrderBy(a => a.OfficialState)
                        .ThenBy(a => a.GetDisplayName(), StringComparer.OrdinalIgnoreCase);

                    string[] noState = {"-no state-"};
                    foreach (AssetInfo info in orderedAssetsState)
                    {
                        // create hierarchy
                        string[] pubs = info.OfficialState == Asset.OfficialStateType.None ? noState : new[] {info.OfficialState.ToString()};

                        lastGroups = AddCategorizedItem(pubs, lastGroups, data, info, ref catIdx);
                    }
                    break;

                case 6: // location
                    IOrderedEnumerable<AssetInfo> orderedAssetsLocation = filteredAssets.OrderBy(a => GetLocationDirectory(a.Location), StringComparer.OrdinalIgnoreCase)
                        .ThenBy(a => a.GetDisplayName(), StringComparer.OrdinalIgnoreCase);

                    string[] noLocation = {"-no location-"};
                    foreach (AssetInfo info in orderedAssetsLocation)
                    {
                        // create hierarchy
                        string[] pubs = string.IsNullOrEmpty(GetLocationDirectory(info.Location)) ? noLocation : new[] {GetLocationDirectory(info.Location)};

                        lastGroups = AddCategorizedItem(pubs, lastGroups, data, info, ref catIdx);
                    }
                    break;
            }

            _textureLoading2?.Cancel();
            _textureLoading2?.Dispose();
            _textureLoading2 = new CancellationTokenSource();

            if (AI.Config.packageViewMode == 0)
            {
                // re-add parents to sub-packages if they were filtered out
                ReAddMissingParents(filteredAssets, data);

                // reorder sub-packages
                ReorderSubPackages(data);

                if (_selectedMaintenance != PackageSearch.MaintenanceOption.Excluded)
                {
                    // add sub-packages from _assets where missing in data since we filtered them out initially
                    AddSubPackagesToTree(data);
                    AddSubPackagesToFeatures(data);
                }

                // remember selected asset IDs before rebuilding so we can restore visibility
                List<int> previousSelectionIds = null;
                if (_selectedTreeAsset != null && _selectedTreeAsset.AssetId > 0)
                {
                    previousSelectionIds = new List<int> {_selectedTreeAsset.AssetId};
                }
                else if (_selectedTreeAssets != null && _selectedTreeAssets.Count > 0)
                {
                    previousSelectionIds = _selectedTreeAssets
                        .Where(a => a != null && a.AssetId > 0)
                        .Select(a => a.AssetId)
                        .ToList();
                }

                AssetTreeModel.SetData(data, AI.Config.assetGrouping > 0);
                AssetTreeView.Reload();

                // restore selection with RevealAndFrame to ensure parents are expanded and item is scrolled into view
                if (previousSelectionIds != null && previousSelectionIds.Count > 0)
                {
                    HashSet<int> availableIds = new HashSet<int>(data.Select(d => d.AssetId));
                    List<int> validIds = previousSelectionIds.Where(id => availableIds.Contains(id)).ToList();
                    if (validIds.Count > 0)
                    {
                        AssetTreeView.SetSelection(validIds, TreeViewSelectionOptions.RevealAndFrame);
                    }
                }

                HandleAssetTreeSelectionChanged(AssetTreeView.GetSelection());

                AssetUtils.LoadTextures(data, _textureLoading2.Token);
                _visiblePackageCount = data.Count(a => a.AssetId > 0 && a.ParentId == 0);
            }
            else
            {
                // grid does not support grouping or sub-packages
                List<AssetInfo> visiblePackages = data.Where(a => a.AssetId > 0 && a.ParentId == 0).ToList();
                List<AssetInfo> selectedPackages = _selectedTreeAssets?.Where(a => a != null && a.AssetId > 0).ToList();
                PGrid.contents = visiblePackages.Select(a => new GUIContent(a.GetDisplayName())).ToArray();
                PGrid.noTextBelow = AI.Config.noPackageTileTextBelow;
                PGrid.enlargeTiles = AI.Config.enlargeTiles;
                PGrid.centerTiles = AI.Config.centerTiles;
                PGrid.Init(_assets, visiblePackages, HandleAssetGridSelectionChanged, info => info.GetDisplayName());

                if (selectedPackages?.Count > 0)
                {
                    PGrid.SetVisualBulkSelection(selectedPackages);
                }

                AssetUtils.LoadTextures(visiblePackages, _textureLoading2.Token, (idx, texture) =>
                {
                    // validate in case dataset changed in the meantime
                    if (PGrid.contents.Length > idx) PGrid.contents[idx].image = texture != null ? texture : PGrid.packages.ElementAt(idx).GetFallbackIcon();
                    _needsRepaint = true;
                });
                _visiblePackageCount = visiblePackages.Count;
            }
        }

        private void ReAddMissingParents(IEnumerable<AssetInfo> filteredAssets, List<AssetInfo> data)
        {
            foreach (AssetInfo info in filteredAssets.Where(a => a.ParentId > 0 && !data.Any(d => d.AssetId == a.ParentId)))
            {
                AssetInfo parent = _assets.FirstOrDefault(a => a.AssetId == info.ParentId);
                if (parent != null)
                {
                    data.Add(parent.WithTreeData(parent.GetDisplayName(), parent.AssetId));
                }
            }
        }

        private void AddSubPackagesToTree(List<AssetInfo> data)
        {
            if (_assets.Count == 0) return; // will cause invalid operation exception otherwise

            int maxChildDepth = _assets.Max(a => a.GetChildDepth());
            HashSet<int> existingAssetIds = new HashSet<int>(data.Select(d => d.AssetId));

            for (int depth = 1; depth <= maxChildDepth; depth++)
            {
                Dictionary<int, List<AssetInfo>> subAssets = _assets
                    .Where(a => !a.Exclude && a.GetChildDepth() == depth && !existingAssetIds.Contains(a.AssetId))
                    .OrderByDescending(a => a.GetDisplayName(), StringComparer.OrdinalIgnoreCase)
                    .GroupBy(a => a.ParentId)
                    .ToDictionary(g => g.Key, g => g.ToList());

                foreach (KeyValuePair<int, List<AssetInfo>> pair in subAssets)
                {
                    int parentIndex = data.FindIndex(a => a.AssetId == pair.Key);
                    if (parentIndex < 0) continue;

                    foreach (AssetInfo asset in pair.Value)
                    {
                        asset.Depth = data[parentIndex].Depth + 1;
                        AssetInfo newAsset = asset.WithTreeData(asset.GetDisplayName(), asset.AssetId, asset.Depth);
                        data.Insert(parentIndex + 1, newAsset);
                        existingAssetIds.Add(newAsset.AssetId);
                    }
                }
            }
        }

        private void AddSubPackagesToFeatures(List<AssetInfo> data)
        {
            if (!AssetStore.IsMetadataAvailable()) return;

            for (int i = 0; i < data.Count; i++)
            {
                AssetInfo info = data[i];
                if (!info.IsFeaturePackage()) continue;

                PackageInfo pInfo = AssetStore.GetPackageInfo(info);
                if (pInfo?.dependencies == null) continue; // in case not loaded yet

                foreach (DependencyInfo dependency in pInfo.dependencies.OrderByDescending(d => d.name))
                {
                    AssetInfo package = _assets.FirstOrDefault(a => !a.Exclude && a.SafeName == dependency.name);
                    if (package != null)
                    {
                        AssetInfo newAsset = new AssetInfo(package.ToAsset()).WithTreeData(package.GetDisplayName(), package.AssetId, package.Depth + 1);
                        data.Insert(i + 1, newAsset);
                    }
                }
            }
        }

        private static void ReorderSubPackages(List<AssetInfo> data)
        {
            int maxChildDepth = data.Max(a => a.GetChildDepth());
            for (int depth = 1; depth <= maxChildDepth; depth++)
            {
                Dictionary<int, List<AssetInfo>> subAssets = data.Where(a => a.GetChildDepth() == depth)
                    .OrderBy(a => a.GetDisplayName(), StringComparer.OrdinalIgnoreCase)
                    .GroupBy(a => a.ParentId).ToDictionary(g => g.Key, g => g.ToList());
                foreach (KeyValuePair<int, List<AssetInfo>> pair in subAssets)
                {
                    // remove items at existing positions
                    pair.Value.ForEach(a =>
                    {
                        data.Remove(a);
                    });

                    // find item with id pair.Key and insert items afterward
                    int idx = data.FindIndex(a => a.AssetId == pair.Key);
                    if (idx >= 0)
                    {
                        pair.Value.ForEach(a =>
                        {
                            a.Depth = data[idx].Depth + 1;
                        });
                        data.InsertRange(idx + 1, pair.Value);
                    }
                }
            }
        }

        private string GetLocationDirectory(string location)
        {
            if (string.IsNullOrWhiteSpace(location)) return null;
            try
            {
                string[] arr = location.Split(Asset.SUB_PATH);
                return Path.GetDirectoryName(arr[0]);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private bool MatchesGroupName(AssetInfo asset, string searchTerm, int groupingMode)
        {
            if (string.IsNullOrWhiteSpace(searchTerm)) return false;

            switch (groupingMode)
            {
                case 2: // category
                    return !string.IsNullOrEmpty(asset.GetDisplayCategory()) && asset.GetDisplayCategory().Contains(searchTerm, StringComparison.OrdinalIgnoreCase);

                case 3: // publisher
                    return !string.IsNullOrEmpty(asset.GetDisplayPublisher()) && asset.GetDisplayPublisher().Contains(searchTerm, StringComparison.OrdinalIgnoreCase);

                case 4: // tags
                    return asset.PackageTags != null && asset.PackageTags.Any(t => t.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase));

                case 5: // state
                    return asset.OfficialState != Asset.OfficialStateType.None && asset.OfficialState.ToString().Contains(searchTerm, StringComparison.OrdinalIgnoreCase);

                case 6: // location
                    string locationDir = GetLocationDirectory(asset.Location);
                    return !string.IsNullOrEmpty(locationDir) && locationDir.Contains(searchTerm, StringComparison.OrdinalIgnoreCase);

                default:
                    return false;
            }
        }

        private static SemVer GetFirstUnityVersionOrDefault(AssetInfo asset)
        {
            if (asset == null)
            {
                return new SemVer("0.0.0");
            }

            if (string.IsNullOrWhiteSpace(asset.SupportedUnityVersions))
            {
                return new SemVer("0.0.0");
            }

            string[] parts = asset.SupportedUnityVersions.Split(',');
            if (parts.Length == 0)
            {
                return new SemVer("0.0.0");
            }

            string firstPart = parts[0].Trim();
            if (string.IsNullOrEmpty(firstPart))
            {
                return new SemVer("0.0.0");
            }

            try
            {
                return new SemVer(firstPart);
            }
            catch (Exception)
            {
                return new SemVer("0.0.0");
            }
        }

        private IOrderedEnumerable<AssetInfo> AddPackageOrdering(IEnumerable<AssetInfo> list)
        {
            IOrderedEnumerable<AssetInfo> result = null;
            bool asc = !AI.Config.sortAssetsDescending;
            switch (AI.Config.assetSorting)
            {
                case (int)Columns.AICaptions:
                    result = list.SortBy(a => a.AICaption, asc);
                    break;

                case (int)Columns.Backup:
                    result = list.SortBy(a => a.Backup, asc);
                    break;

                case (int)Columns.Category:
                    result = list.SortBy(a => a.GetDisplayCategory(), asc, StringComparer.OrdinalIgnoreCase);
                    break;

                case (int)Columns.InternalState:
                    result = list.SortBy(a => a.CurrentState, asc);
                    break;

                case (int)Columns.Deprecated:
                    result = list.SortBy(a => a.IsDeprecated, asc);
                    break;

                case (int)Columns.Downloaded:
                    result = list.SortBy(a => a.IsDownloaded, asc);
                    break;

                case (int)Columns.ModifiedDate:
                case (int)Columns.ModifiedDateRelative:
                    result = list.SortBy(a => a.ModifiedDate, asc);
                    break;

                case (int)Columns.Exclude:
                    result = list.SortBy(a => a.Exclude, asc);
                    break;

                case (int)Columns.Extract:
                    result = list.SortBy(a => a.KeepExtracted, asc);
                    break;

                case (int)Columns.ForeignId:
                    result = list.SortBy(a => a.ForeignId, asc);
                    break;

                case (int)Columns.Popularity:
                    result = list.SortBy(a => a.Hotness, asc);
                    break;

                case (int)Columns.Indexed:
                    result = list.SortBy(a => a.IsIndexed, asc);
                    break;

                case (int)Columns.FileCount:
                    result = list.SortBy(a => a.FileCount, asc);
                    break;

                case (int)Columns.License:
                    result = list.SortBy(a => a.License, asc);
                    break;

                case (int)Columns.Location:
                    result = list.SortBy(a => a.Location, asc);
                    break;

                case (int)Columns.Materialized:
                    result = list.SortBy(a => a.IsMaterialized, asc);
                    break;

                case (int)Columns.Name:
                    result = list.SortBy(a => a.GetDisplayName(), asc, StringComparer.OrdinalIgnoreCase);
                    break;

                case (int)Columns.Outdated:
                    result = list.SortBy(a => a.CurrentSubState == Asset.SubState.Outdated, asc);
                    break;

                case (int)Columns.Price:
                    result = list.SortBy(a => a.GetPrice(), asc);
                    break;

                case (int)Columns.Publisher:
                    result = list.SortBy(a => a.GetDisplayPublisher(), asc, StringComparer.OrdinalIgnoreCase);
                    break;

                case (int)Columns.PurchaseDate:
                case (int)Columns.PurchaseDateRelative:
                    result = list.SortBy(a => a.GetPurchaseDate(), asc);
                    break;

                case (int)Columns.Rating:
                    result = list.SortBy(a => a.AssetRating, asc).ThenSortBy(a => a.RatingCount, asc);
                    break;

                case (int)Columns.RatingCount:
                    result = list.SortBy(a => a.RatingCount, asc).ThenSortBy(a => a.AssetRating, asc);
                    break;

                case (int)Columns.ReleaseDate:
                case (int)Columns.ReleaseDateRelative:
                    result = list.SortBy(a => a.FirstRelease, asc);
                    break;

                case (int)Columns.Size:
                    result = list.SortBy(a => a.PackageSize, asc);
                    break;

                case (int)Columns.Source:
                    result = list.SortBy(a => a.AssetSource, asc);
                    break;

                case (int)Columns.State:
                    result = list.SortBy(a => a.OfficialState, asc);
                    break;

                case (int)Columns.Tags:
                    result = list.SortBy(a => string.Join(", ", a.PackageTags.Select(t => t.Name)), asc);
                    break;

                case (int)Columns.UnityVersions:
                    result = list.SortBy(a => GetFirstUnityVersionOrDefault(a), asc);
                    break;

                case (int)Columns.Update:
                    result = list.SortBy(a => a.AssetSource == Asset.Source.AssetStorePackage && a.IsUpdateAvailable(), asc);
                    break;

                case (int)Columns.UpdateDate:
                case (int)Columns.UpdateDateRelative:
                    result = list.SortBy(a => a.LastRelease, asc);
                    break;

                case (int)Columns.Version:
                    result = list.SortBy(a => new SemVer(a.GetVersion()), asc);
                    break;

                default:
                    int metaId = AssetTreeView.multiColumnHeader.GetColumn(AI.Config.assetSorting).userData;
                    MetadataInfo metaDef = list.Where(a => a.PackageMetadata != null && a.PackageMetadata.Any(pm => pm.DefinitionId == metaId)).Select(a => a.PackageMetadata.First(pm => pm.DefinitionId == metaId)).FirstOrDefault();
                    if (metaDef != null)
                    {
                        switch (metaDef.Type)
                        {
                            case MetadataDefinition.DataType.Boolean:
                                result = list.SortBy(a =>
                                {
                                    MetadataInfo meta = a.PackageMetadata?.FirstOrDefault(m => m.DefinitionId == metaId);
                                    if (meta == null) return 0;
                                    return meta.BoolValue ? 1 : 0;
                                }, asc);
                                break;

                            case MetadataDefinition.DataType.Text:
                            case MetadataDefinition.DataType.BigText:
                            case MetadataDefinition.DataType.Url:
                            case MetadataDefinition.DataType.SingleSelect:
                            case MetadataDefinition.DataType.List:
                                result = list.SortBy(a =>
                                {
                                    MetadataInfo meta = a.PackageMetadata?.FirstOrDefault(m => m.DefinitionId == metaId);
                                    if (meta == null) return null;
                                    return meta.StringValue;
                                }, asc);
                                break;

                            case MetadataDefinition.DataType.Date:
                            case MetadataDefinition.DataType.DateTime:
                                result = list.SortBy(a =>
                                {
                                    MetadataInfo meta = a.PackageMetadata?.FirstOrDefault(m => m.DefinitionId == metaId);
                                    if (meta == null) return default;
                                    return meta.DateTimeValue;
                                }, asc);
                                break;

                            case MetadataDefinition.DataType.Number:
                                result = list.SortBy(a =>
                                {
                                    MetadataInfo meta = a.PackageMetadata?.FirstOrDefault(m => m.DefinitionId == metaId);
                                    if (meta == null) return 0;
                                    return meta.IntValue;
                                }, asc);
                                break;

                            case MetadataDefinition.DataType.DecimalNumber:
                                result = list.SortBy(a =>
                                {
                                    MetadataInfo meta = a.PackageMetadata?.FirstOrDefault(m => m.DefinitionId == metaId);
                                    if (meta == null) return 0f;
                                    return meta.FloatValue;
                                }, asc);
                                break;

                        }
                    }
                    break;

            }
            if (result == null) result = list.OrderBy(a => a.LastRelease);

            return result.ThenSortBy(a => a.GetDisplayName(), asc, StringComparer.OrdinalIgnoreCase);
        }

        private static string[] AddCategorizedItem(string[] cats, string[] lastCats, List<AssetInfo> data, AssetInfo info, ref int catIdx)
        {
            // find first difference to previous cat
            if (!ArrayUtility.ArrayEquals(cats, lastCats))
            {
                int firstDiff = 0;
                bool diffFound = false;
                for (int i = 0; i < Mathf.Min(cats.Length, lastCats.Length); i++)
                {
                    if (cats[i] != lastCats[i])
                    {
                        firstDiff = i;
                        diffFound = true;
                        break;
                    }
                }
                if (!diffFound) firstDiff = lastCats.Length;

                for (int i = firstDiff; i < cats.Length; i++)
                {
                    catIdx--;
                    AssetInfo catItem = new AssetInfo().WithTreeData(cats[i], catIdx, i);
                    data.Add(catItem);
                }
            }

            AssetInfo item = info.WithTreeData(info.GetDisplayName(), info.AssetId, cats.Length);
            data.Add(item);

            return cats;
        }

        private async void OpenInPackageView(AssetInfo info)
        {
            AI.Config.tab = 1;
            _assetSearchPhrase = "";

            ResetPackageFilters(false);

            if (AI.Config.packageViewMode == 0)
            {
                await SelectAndFrame(info);

                // ensure package is visible
                if (!AssetTreeModel.GetData().Contains(info))
                {
                    AI.Config.packagesListing = 0;
                    ResetPackageFilters(false);
                    await SelectAndFrame(info);
                }

                HandleAssetTreeSelectionChanged(AssetTreeView.GetSelection());
            }
            else
            {
                if (PGrid.packages == null) CreateAssetTree();
                PGrid.Select(info.GetRoot());
                HandleAssetGridSelectionChanged();
            }
        }

        private async Task SelectAndFrame(AssetInfo info)
        {
            await Task.Delay(100); // let the tree view update first
            AssetTreeView.SetSelection(new[] {info.AssetId}, TreeViewSelectionOptions.RevealAndFrame);
        }

        private void HandleAssetTreeSelectionChanged(IList<int> ids)
        {
            AssetInfo oldSelection = _selectedTreeAsset;
            _selectedTreeAsset = null;
            _selectedTreeAssets = _selectedTreeAssets ?? new List<AssetInfo>();
            _selectedTreeAssets.Clear();

            if (ids.Count == 1 && ids[0] > 0)
            {
                _selectedTreeAsset = AssetTreeModel.Find(ids[0]);

                // restore single selections as otherwise after e.g. a download the selected package disappears from the inspector 
                if (_selectedTreeAsset == null) _selectedTreeAsset = oldSelection;

                if (_selectedTreeAsset != null)
                {
                    // refresh immediately for single selections to have all buttons correct at once
                    _selectedTreeAsset.Refresh();
                    _selectedTreeAsset.PackageDownloader?.RefreshState(true);

                    LoadMediaOnDemand(_selectedTreeAsset);

                    // Gather backup state once per asset selection for performance
                    if (_selectedTreeAsset.ForeignId > 0 &&
                        (_selectedTreeAsset.AssetSource == Asset.Source.AssetStorePackage ||
                            _selectedTreeAsset.AssetSource == Asset.Source.CustomPackage))
                    {
                        _cachedBackupState = AssetBackup.GatherState();
                    }
                    else
                    {
                        _cachedBackupState = null;
                    }
                }
            }
            else
            {
                _cachedBackupState = null;
            }

            // load all selected items but count each only once
            HashSet<int> seen = new HashSet<int>();
            foreach (int id in ids)
            {
                GatherTreeChildren(id, _selectedTreeAssets, seen, AssetTreeModel);
            }

            _assetBulkTags.Clear();

            // initialize download status, debounce for bulk selections to avoid expensive re-initialization on rapid clicks
            if (_selectedTreeAssets.Count <= 1)
            {
                AI.RegisterSelection(_selectedTreeAssets);
            }
            else
            {
                int generation = ++_selectionGeneration;
                EditorApplication.delayCall += () =>
                {
                    if (_selectionGeneration == generation) AI.RegisterSelection(_selectedTreeAssets);
                };
            }

            // merge tags
            _selectedTreeAssets.ForEach(info => info.PackageTags?.ForEach(t =>
            {
                if (_assetBulkTags.TryGetValue(t.Name, out Tuple<int, Color> existing))
                {
                    _assetBulkTags[t.Name] = new Tuple<int, Color>(existing.Item1 + 1, existing.Item2);
                }
                else
                {
                    _assetBulkTags.Add(t.Name, new Tuple<int, Color>(1, t.GetColor()));
                }
            }));

            _assetTreeSubPackageCount = 0;
            _assetTreeSelectionSize = 0;
            _assetTreeSelectionTotalCosts = 0;
            _assetTreeSelectionStoreCosts = 0;
            foreach (AssetInfo a in _selectedTreeAssets)
            {
                if (a.ParentId > 0)
                {
                    _assetTreeSubPackageCount++;
                }
                else
                {
                    _assetTreeSelectionSize += a.PackageSize;
                    float price = a.GetPrice();
                    _assetTreeSelectionTotalCosts += price;
                    if (a.AssetSource == Asset.Source.AssetStorePackage) _assetTreeSelectionStoreCosts += price;
                }
            }

            // refresh metadata automatically for single selections
            if (_selectedTreeAsset != null && AI.Config.autoRefreshMetadata && _selectedTreeAsset.ForeignId > 0 && (DateTime.Now - _selectedTreeAsset.LastOnlineRefresh).TotalHours >= AI.Config.metadataTimeout)
            {
                _ = AI.Actions.FetchAssetDetails(true, _selectedTreeAsset.AssetId, _selectedTreeAsset.LastOnlineRefresh > DateTime.MinValue); // skip downstream events to avoid hick-ups
                _selectedTreeAsset.LastOnlineRefresh = DateTime.Now; // safety in case the above fails, e.g. for deleted packages
            }
        }

        private void HandleAssetGridSelectionChanged()
        {
            _selectedTreeAssets = PGrid.selectionItems?.Distinct().ToList() ?? new List<AssetInfo>();
            _selectedTreeAsset = _selectedTreeAssets.Count == 1 ? _selectedTreeAssets[0] : null;

            if (_selectedTreeAsset != null)
            {
                // refresh immediately for single selections to have all buttons correct at once
                _selectedTreeAsset.Refresh();
                _selectedTreeAsset.PackageDownloader?.RefreshState(true);

                LoadMediaOnDemand(_selectedTreeAsset);
            }

            _assetBulkTags.Clear();

            // initialize download status, debounce for bulk selections to avoid expensive re-initialization on rapid clicks
            if (_selectedTreeAssets.Count <= 1)
            {
                AI.RegisterSelection(_selectedTreeAssets);
            }
            else
            {
                int generation = ++_selectionGeneration;
                EditorApplication.delayCall += () =>
                {
                    if (_selectionGeneration == generation) AI.RegisterSelection(_selectedTreeAssets);
                };
            }

            // merge tags
            _selectedTreeAssets.ForEach(info => info.PackageTags?.ForEach(t =>
            {
                if (_assetBulkTags.TryGetValue(t.Name, out Tuple<int, Color> existing))
                {
                    _assetBulkTags[t.Name] = new Tuple<int, Color>(existing.Item1 + 1, existing.Item2);
                }
                else
                {
                    _assetBulkTags.Add(t.Name, new Tuple<int, Color>(1, t.GetColor()));
                }
            }));

            _assetTreeSubPackageCount = 0;
            _assetTreeSelectionSize = 0;
            _assetTreeSelectionTotalCosts = 0;
            _assetTreeSelectionStoreCosts = 0;
            foreach (AssetInfo a in _selectedTreeAssets)
            {
                if (a.ParentId == 0)
                {
                    _assetTreeSelectionSize += a.PackageSize;
                    float price = a.GetPrice();
                    _assetTreeSelectionTotalCosts += price;
                    if (a.AssetSource == Asset.Source.AssetStorePackage) _assetTreeSelectionStoreCosts += price;
                }
            }
        }

        private void LoadMediaOnDemand(AssetInfo info)
        {
            if (info == null) return;
            if (info.IsMediaLoading()) return;
            if (info.AllMedia != null) return; // already loaded

            if (AI.Config.expandPackageDetails || AI.Config.alwaysShowPackageDetails)
            {
                // clear all existing media to conserve memory
                if (AI.Config.packageViewMode == 0)
                {
                    AssetTreeModel.GetData().ForEach(d => d.DisposeMedia());
                }
                else
                {
                    PGrid.packages.ForEach(d => d.DisposeMedia());
                }
                MediaManager.Load(info);
            }
        }

        private void OnAssetTreeSelectionChanged(IList<int> ids)
        {
            _selectedMedia = 0;
            _packageInspectorTab = 0;
            HandleAssetTreeSelectionChanged(ids);
        }

        private void OnAssetTreeDoubleClicked(int id)
        {
            if (id <= 0) return;

            AssetInfo info = AssetTreeModel.Find(id);
            OpenInSearch(info);
        }

        private void OnPackageGridDoubleClicked(AssetInfo info)
        {
            OpenInSearch(info);
        }

        private void OnPackageGridKeyboardSelection(int selectionIndex)
        {
            // Trigger the same logic that happens on mouse selection
            HandleAssetGridSelectionChanged();
        }

        private void PopulatePackageGridContextMenu(GenericMenu menu, IReadOnlyList<AssetInfo> selection, int clickedIndex)
        {
            if (selection == null || selection.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent("No Selection"));
                return;
            }

            // Determine eligible items for reindex
            List<AssetInfo> reindexable = selection
                .Where(info => info != null && !info.IsAbandoned && info.AssetSource != Asset.Source.CurrentProject && (info.IsDownloaded || info.AssetSource == Asset.Source.AssetStorePackage))
                .ToList();

            // Determine eligible items for import
            List<AssetInfo> importable = selection
                .Where(info => info != null
                    && info.AssetSource != Asset.Source.Directory
                    && info.AssetSource != Asset.Source.CurrentProject
                    && info.SafeName != Asset.NONE
                    && !info.IsAbandoned
                    && (info.IsDownloaded || info.AssetSource == Asset.Source.AssetStorePackage))
                .ToList();

            // If all selected are registry packages and already installed, skip import
            importable = importable
                .Where(info => !AssetStore.IsInstalled(info))
                .ToList();

            // Show package name header if only one item is selected
            if (selection.Count == 1 && selection[0] != null)
            {
                menu.AddDisabledItem(new GUIContent(selection[0].GetDisplayName()));
                menu.AddSeparator("");
            }

            bool hasActions = false;

            if (importable.Count > 0)
            {
                bool needsDl = importable.Any(info => !info.IsDownloaded);
                string dlSuffix = needsDl ? " (will download)" : "";
                string caption = importable.Count == 1 ? $"Import Package...{dlSuffix}" : $"Import {importable.Count} Packages...{dlSuffix}";
                menu.AddItem(new GUIContent(caption), false, () =>
                {
                    ImportUI importUI = ImportUI.ShowWindow();
                    importUI.Init(importable);
                });
                hasActions = true;
            }

            if (reindexable.Count > 0)
            {
                bool needsDl = reindexable.Any(info => !info.IsDownloaded);
                string dlSuffix = needsDl ? " (will download)" : "";
                string reindexCaption = reindexable.Count == 1 ? $"Reindex Now{dlSuffix}" : $"Reindex {reindexable.Count} Packages Now{dlSuffix}";
                menu.AddItem(new GUIContent(reindexCaption), false, () =>
                {
                    ReindexPackagesNow(reindexable);
                });
                hasActions = true;
            }

            if (!hasActions)
            {
                menu.AddDisabledItem(new GUIContent("No actions available"));
            }
        }

        private static int GetInspectorWidth()
        {
            return UIStyles.INSPECTOR_WIDTH * (AI.Config.expandPackageDetails && AI.Config.tab == 1 ? 2 : 1);
        }

        private static int GetVisiblePackageInspectorWidth()
        {
            return AI.Config.showPackageSideBar ? GetInspectorWidth() : 0;
        }

        private void PopulateSavedPackageSearchFromCurrentState(SavedPackageSearch search)
        {
            search.SearchPhrase = _assetSearchPhrase;
            search.PackagesListing = AI.Config.packagesListing;
            search.SRPs = AI.Config.assetSRPs;
            search.Deprecation = AI.Config.assetDeprecation;
            search.Maintenance = (int)_selectedMaintenance;
            search.PriceOption = _selectedPkgPriceOption;
            search.Price = _pkgSearchPrice;
            search.PackageSizeOption = _selectedPkgSizeOption;
            search.PackageSizeMB = _pkgSizeMB;
            search.UpdateDateOption = _selectedPkgUpdateDateOption;
            search.UpdateBeforeDate = _pkgUpdateBeforeDate?.ToString("yyyy-MM-dd");
            search.UpdateAfterDate = _pkgUpdateAfterDate?.ToString("yyyy-MM-dd");
            search.PurchaseDateOption = _selectedPkgPurchaseDateOption;
            search.PurchaseBeforeDate = _pkgPurchaseBeforeDate?.ToString("yyyy-MM-dd");
            search.PurchaseAfterDate = _pkgPurchaseAfterDate?.ToString("yyyy-MM-dd");
            search.UnityVersionOption = _selectedPkgUnityVersionOption;

            // Store full selection strings (will extract IDs during restore if needed)
            search.Publisher = _selectedPkgPublisher > 0 && _publisherNames.Length > _selectedPkgPublisher
                ? _publisherNames[_selectedPkgPublisher].Split('/').LastOrDefault()
                : null;

            search.Category = _selectedPkgCategory > 0 && _categoryNames.Length > _selectedPkgCategory
                ? _categoryNames[_selectedPkgCategory]
                : null;

            search.PackageTag = _selectedPkgTag > 0 && _tagNames.Length > _selectedPkgTag
                ? _tagNames[_selectedPkgTag]
                : null;
        }

        private void SavePackageSearch(string name)
        {
            SavedPackageSearch search = new SavedPackageSearch();
            search.Name = name;
            search.Color = ColorUtility.ToHtmlStringRGB(UnityEngine.Random.ColorHSV());

            PopulateSavedPackageSearchFromCurrentState(search);

            DBAdapter.DB.Insert(search);
            PackageSearches.Add(search);
            _activeSavedPackageSearchId = search.Id;
            _packageSearchesLoaded = false; // Force reload
        }

        private void LoadPackageSearch(SavedPackageSearch search)
        {
            _assetSearchPhrase = search.SearchPhrase ?? string.Empty;
            AI.Config.packagesListing = search.PackagesListing;
            AI.Config.assetSRPs = search.SRPs;
            AI.Config.assetDeprecation = search.Deprecation;
            _selectedMaintenance = (PackageSearch.MaintenanceOption)search.Maintenance;
            _selectedPkgPriceOption = search.PriceOption;
            _pkgSearchPrice = search.Price;
            _selectedPkgSizeOption = search.PackageSizeOption;
            _pkgSizeMB = search.PackageSizeMB;
            _selectedPkgUpdateDateOption = search.UpdateDateOption;
            _selectedPkgPurchaseDateOption = search.PurchaseDateOption;
            _selectedPkgUnityVersionOption = search.UnityVersionOption;

            // Parse date strings
            if (!string.IsNullOrEmpty(search.UpdateBeforeDate) && DateTime.TryParse(search.UpdateBeforeDate, out DateTime beforeDate))
            {
                _pkgUpdateBeforeDate = beforeDate;
            }
            else
            {
                _pkgUpdateBeforeDate = null;
            }

            if (!string.IsNullOrEmpty(search.UpdateAfterDate) && DateTime.TryParse(search.UpdateAfterDate, out DateTime afterDate))
            {
                _pkgUpdateAfterDate = afterDate;
            }
            else
            {
                _pkgUpdateAfterDate = null;
            }

            if (!string.IsNullOrEmpty(search.PurchaseBeforeDate) && DateTime.TryParse(search.PurchaseBeforeDate, out DateTime purchaseBeforeDate))
            {
                _pkgPurchaseBeforeDate = purchaseBeforeDate;
            }
            else
            {
                _pkgPurchaseBeforeDate = null;
            }

            if (!string.IsNullOrEmpty(search.PurchaseAfterDate) && DateTime.TryParse(search.PurchaseAfterDate, out DateTime purchaseAfterDate))
            {
                _pkgPurchaseAfterDate = purchaseAfterDate;
            }
            else
            {
                _pkgPurchaseAfterDate = null;
            }

            // Restore dropdowns (match by ID if brackets exist, otherwise by string)
            _selectedPkgPublisher = FindIndexByValue(_publisherNames, search.Publisher, splitPath: true);
            _selectedPkgCategory = FindIndexByValue(_categoryNames, search.Category, splitPath: false);
            _selectedPkgTag = FindIndexByValue(_tagNames, search.PackageTag, splitPath: false);

            _activeSavedPackageSearchId = search.Id;
            _requireAssetTreeRebuild = true;
            AI.SaveConfig();
        }

        private void OverrideSavedPackageSearch(SavedPackageSearch search)
        {
            PopulateSavedPackageSearchFromCurrentState(search);
            DBAdapter.DB.Update(search);
        }

        private void DeletePackageSearch(SavedPackageSearch search)
        {
            if (EditorUtility.DisplayDialog("Delete Saved Search", $"Are you sure you want to delete the saved search '{search.Name}'?", "Delete", "Cancel"))
            {
                DBAdapter.DB.Delete(search);
                PackageSearches.Remove(search);
                _packageSearchesLoaded = false; // Force reload
                if (_activeSavedPackageSearchId == search.Id)
                {
                    _activeSavedPackageSearchId = -1;
                }
            }
        }
    }
}
