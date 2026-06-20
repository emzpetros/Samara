using ImpossibleRobert.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AudioTool;
using Newtonsoft.Json;
#if !AUDIO_TOOL_NOAUDIO
using JD.EditorAudioUtils;
#endif
using UnityEditor;
using UnityEditor.IMGUI.Controls;
#if !USE_TUTORIALS
using UnityEditor.PackageManager;
#endif
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;
#if UNITY_6000_2_OR_NEWER
using BaseTreeViewState = UnityEditor.IMGUI.Controls.TreeViewState<int>;
#else
using BaseTreeViewState = UnityEditor.IMGUI.Controls.TreeViewState;
#endif
#pragma warning disable CS0618 // Type or member is obsolete

namespace AssetInventory
{
    public partial class IndexUI
    {
        private const float DRAG_THRESHOLD = 5f; // pixels
        private const float DRAG_DELAY = 0.5f; // seconds

        private enum InMemoryModeState
        {
            None,
            Init,
            Active
        }

        // customizable interaction modes, search mode will only show search tab contents and no actions except "Select"
        public bool searchMode;

        // will show additional workspace layer
        public bool workspaceMode;

        // special mode that will return accompanying textures to the selected one, trying to identify normal, metallic etc. 
        public bool textureMode;

        // will hide right-side inspector pane
        public bool hideDetailsPane;
        public bool hideMainNavigation;

        // will not select items in the project window upon selection
        public bool disablePings;

        // will cause clicking on a grid tile to return the selection to the caller and close the window
        public bool instantSelection;

        // locks the search to a specific type, e.g. "Prefabs" 
        public string fixedSearchType;

        // event handler during search mode
        protected Action<string> searchModeCallback;
        protected Action<Dictionary<string, string>> searchModeTextureCallback;

        private List<AssetInfo> _files;
        private List<AssetInfo> _filteredFiles;

        private GridControl SGrid
        {
            get
            {
                if (_sgrid == null)
                {
                    _sgrid = new GridControl();
                    _sgrid.onlySingleSelection = searchMode;
                    _sgrid.OnDoubleClick += OnSearchDoubleClick;
                    _sgrid.OnKeyboardSelection += OnSearchKeyboardSelection;
                    _sgrid.OnContextMenuPopulate += PopulateSearchGridContextMenu;
                }
                return _sgrid;
            }
        }
        private GridControl _sgrid;

        [SerializeField] private MultiColumnHeaderState searchMchState;
        private TreeViewWithTreeModel<AssetInfo> SearchTreeView
        {
            get
            {
                if (_searchTreeView == null && _searchTreeModel != null)
                {
                    bool hadSerializedState = searchMchState != null;
                    MultiColumnHeaderState headerState = SearchTreeViewControl.CreateDefaultMultiColumnHeaderState();
                    headerState = GetUsableMultiColumnHeaderState(searchMchState, headerState);
                    RestoreColumnWidths(headerState, AI.Config.searchTreeColumnWidths);
                    if (AI.Config.visibleSearchTreeColumns != null && AI.Config.visibleSearchTreeColumns.Length > 0)
                    {
                        int columnCount = headerState.columns.Length;
                        int[] validColumns = AI.Config.visibleSearchTreeColumns.Where(c => c >= 0 && c < columnCount).ToArray();
                        if (validColumns.Length > 0)
                        {
                            headerState.visibleColumns = validColumns;
                        }
                    }
                    searchMchState = headerState;

                    ReorderableMultiColumnHeader mch = new ReorderableMultiColumnHeader(searchMchState);
                    mch.canSort = false;
                    mch.height = MultiColumnHeader.DefaultGUI.minimumHeight;
                    mch.visibleColumnsChanged += OnVisibleSearchTreeColumnsChanged;
                    mch.columnOrderChanged += OnSearchTreeColumnOrderChanged;
                    if (!hadSerializedState && AI.Config.searchTreeColumnWidths == null) mch.ResizeToFit();

                    _searchTreeView = new SearchTreeViewControl(new BaseTreeViewState(), mch, _searchTreeModel, this);
                    _searchTreeView.OnSelectionChanged += OnSearchTreeSelectionChanged;
                    _searchTreeView.OnDoubleClickedItem += OnSearchTreeDoubleClick;
                    _searchTreeView.OnContextMenuPopulate += PopulateSearchTreeContextMenu;
                    _searchTreeView.Reload();
                }
                return _searchTreeView;
            }
        }
        private TreeViewWithTreeModel<AssetInfo> _searchTreeView;
        private TreeModel<AssetInfo> _searchTreeModel;
        private Dictionary<int, Texture2D> _filePreviewCache = new Dictionary<int, Texture2D>();
        private Dictionary<int, AssetInfo> _pendingVirtualPreviews = new Dictionary<int, AssetInfo>();
        private bool _virtualPreviewRetryRunning;

        public Texture2D GetFilePreview(int fileId)
        {
            _filePreviewCache.TryGetValue(fileId, out Texture2D texture);
            return texture;
        }

        private void OnVisibleSearchTreeColumnsChanged(MultiColumnHeader mch)
        {
            ((ReorderableMultiColumnHeader)mch).SmartResizeToFit();
            AI.Config.visibleSearchTreeColumns = mch.state.visibleColumns;
            AI.Config.searchTreeColumnWidths = ExtractColumnWidths(searchMchState);
            AI.SaveConfig();
        }

        private void OnSearchTreeColumnOrderChanged(int[] newOrder)
        {
            AI.Config.visibleSearchTreeColumns = newOrder;
            AI.Config.searchTreeColumnWidths = ExtractColumnWidths(searchMchState);
            AI.SaveConfig();
        }

        private void PopulateSearchTreeContextMenu(GenericMenu menu, IReadOnlyList<AssetInfo> selection, int clickedIndex)
        {
            PopulateSearchGridContextMenu(menu, selection, clickedIndex);
        }

        private void OnSearchTreeSelectionChanged(IList<int> ids)
        {
            if (ids == null || ids.Count == 0)
            {
                _selectedEntry = null;
                SGrid.SetBulkSelection(null);
                return;
            }

            // Resolve all selected ids to AssetInfo objects
            List<AssetInfo> selectedItems = ids
                .Select(id => _searchTreeModel?.Find(id))
                .Where(item => item != null)
                .ToList();

            // Populate bulk selection data for the inspector panel
            SGrid.SetBulkSelection(selectedItems);

            // Set single selection for detail view
            _selectedEntry = selectedItems.FirstOrDefault();
            if (_selectedEntry != null)
            {
                _requireSearchSelectionUpdate = true;
                _searchInspectorTab = 0;
            }
        }

        private void OnSearchTreeDoubleClick(int id)
        {
            AssetInfo info = _searchTreeModel?.Find(id);
            if (info != null)
            {
                OnSearchDoubleClick(info);
            }
        }

        private void PopulateSearchGridContextMenu(GenericMenu menu, IReadOnlyList<AssetInfo> selection, int clickedIndex)
        {
            if (selection == null || selection.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent("No Selection"));
                return;
            }

            // Header with single selection name
            if (selection.Count == 1 && selection[0] != null)
            {
                menu.AddDisabledItem(new GUIContent(selection[0].FileName));
                menu.AddSeparator("");
            }

            // Import action (for asset packages/files that can be imported)
            List<AssetInfo> importable = selection
                .Where(info => info != null
                    && info.AssetSource != Asset.Source.Directory
                    && info.SafeName != Asset.NONE
                    && !info.IsAbandoned
                    && !info.InProject
                    && (info.IsDownloaded || info.AssetSource == Asset.Source.AssetStorePackage))
                .Where(info => !AssetStore.IsInstalled(info))
                .ToList();

            bool needsDownload = importable.Any(info => !info.IsDownloaded);
            string actionName = searchMode ? "Select" : "Import";
            if (importable.Count > 0)
            {
                string caption = searchMode || importable.Count == 1 ? actionName : $"{actionName} {importable.Count} Files";
                if (needsDownload) caption += " (will download)";
                menu.AddItem(new GUIContent(caption), false, () =>
                {
                    if (searchMode)
                    {
                        ExecuteSingleAction();
                    }
                    else
                    {
                        ImportBulkFiles(importable);
                    }
                });
            }
            else
            {
                menu.AddDisabledItem(new GUIContent(actionName));
            }

            // Open Create/Recreate AI Caption
            List<AssetInfo> aiCaptionTargets = selection
                .Where(info => info != null && !info.IsVirtual)
                .ToList();
            if (aiCaptionTargets.Count > 0 && AI.Actions.CreateAICaptions)
            {
                string aiCaptionLabel;
                if (aiCaptionTargets.Count == 1)
                {
                    bool hasCaption = !string.IsNullOrWhiteSpace(aiCaptionTargets[0].AICaption);
                    aiCaptionLabel = hasCaption ? "Recreate AI Caption" : "Create AI Caption";
                }
                else
                {
                    aiCaptionLabel = "Create AI Captions";
                }
                menu.AddItem(new GUIContent(aiCaptionLabel), false, () =>
                {
                    RecreateAICaptions(aiCaptionTargets);
                });
            }

            // Recreate Preview
            List<AssetInfo> previewable = selection
                .Where(info => info != null && !info.IsVirtual && PreviewManager.IsPreviewable(info.FileName, true, info))
                .ToList();
            if (previewable.Count > 0)
            {
                string previewLabel = previewable.Count == 1 ? "Recreate Preview" : "Recreate Previews";
                menu.AddItem(new GUIContent(previewLabel), false, () =>
                {
                    RecreatePreviews(previewable);
                });
            }

            // Remove from Project
            List<AssetInfo> removable = selection
                .Where(info => info != null && info.InProject)
                .ToList();
            if (removable.Count > 0)
            {
                string removeLabel = removable.Count == 1 ? "Remove from Project..." : $"Remove {removable.Count} from Project...";
                menu.AddItem(new GUIContent(removeLabel), false, () =>
                {
                    UninstallPackageUI.ShowWindow().Init(removable);
                });
            }

            // Hide / Unhide
            List<AssetInfo> hideable = selection.Where(info => info != null && !info.IsVirtual).ToList();
            if (hideable.Count > 0)
            {
                menu.AddSeparator("");
                bool anyVisible = hideable.Any(info => !info.Hidden);
                bool anyHidden = hideable.Any(info => info.Hidden);

                if (anyVisible)
                {
                    string hideLabel = hideable.Count == 1 ? "Hide from Results" : $"Hide {hideable.Count} from Results";
                    menu.AddItem(new GUIContent(hideLabel), false, () =>
                    {
                        Assets.SetFilesHidden(hideable.Select(i => i.Id).ToList(), true);
                        _requireSearchUpdate = true;
                    });
                }
                if (anyHidden)
                {
                    string unhideLabel = hideable.Count == 1 ? "Unhide" : $"Unhide {hideable.Count}";
                    menu.AddItem(new GUIContent(unhideLabel), false, () =>
                    {
                        Assets.SetFilesHidden(hideable.Select(i => i.Id).ToList(), false);
                        _requireSearchUpdate = true;
                    });
                }
            }
        }

        private InMemoryModeState _inMemoryMode = InMemoryModeState.None;
        private string _searchPhrase;
        private string _previousSearchPhrase;
        private string _searchPhraseInMemory;
        private string _searchWidth;
        private string _searchHeight;
        private string _searchLength;
        private string _searchSize;
        private bool _checkMaxWidth;
        private bool _checkMaxHeight;
        private bool _checkMaxLength;
        private bool _checkMaxSize;
        private string _searchVertexCount;
        private bool _checkMaxVertexCount;
        private int _selectedPublisher;
        private int _selectedCategory;
        private int _selectedExpertSearchField;
        private int _selectedAsset;
        private int _selectedPackageTypes = 1;
        private int _selectedPackageSRPs = 1;
        private int _selectedPackageTag;
        private int _selectedFileTag;
        private int _selectedPriceOption;
        private float _searchPrice;
        private int _selectedImageType;
        private int _selectedColorOption;
        private Color _selectedColor;
        private int _selectedHiddenFilter;
        private string[] _hiddenFilterOptions = {"Hide", "Show", "Only Hidden"};

        private Vector2 _searchScrollPos;
        private Vector2 _inspectorScrollPos;

        private int _resultCount;
        private int _originalResultCount;
        private int _curPage = 1;
        private int _pageCount;

        private CancellationTokenSource _textureLoading;
        private CancellationTokenSource _textureLoading2;
        private CancellationTokenSource _textureLoading3;
        private CancellationTokenSource _extraction;
        private Dictionary<AssetInfo, CancellationTokenSource> _dependencyCancellationTokens;

        private AssetInfo _selectedEntry;
        private Workspace _selectedWorkspace;

        private SearchField SearchField => _searchField = _searchField ?? new SearchField();
        private SearchField _searchField;

        private int _searchInspectorTab;
        private float _nextSearchTime;
        private float _nextVariableDetectionTime;
        private DateTime _lastTileSizeChange;
        private string _searchError;
        private bool _searchDone;
        private bool _lockSelection;
        private string _curOperation;
        private int _fixedSearchTypeIdx;
        private bool _draggingPossible;
        private bool _dragging;
        private Vector2 _dragStartPosition;
        private float _dragStartTime;
        private bool _keepSearchResultPage = true;
        private readonly Dictionary<string, Tuple<int, Color>> _assetFileBulkTags = new Dictionary<string, Tuple<int, Color>>();
        private AnimationPlayer _animationPlayer;
        private int _animatedTileIndex = -1;
        private AssetInfo _animatedEntry;

        // Multi-animation for visible tiles using the shared manager
        private readonly AnimationPlaybackManager<int> _visibleAnimations = new AnimationPlaybackManager<int>(
            maxConcurrentLoads: 3, 
            isEnabledCheck: () => AI.Config.playVisibleSearchAnimations
        );
        private Vector2 _lastSearchScrollPos;
        private float _lastViewHeight;
        private float _searchGridViewHeight; // Captured during OnGUI inside scroll view
        private int _visibleAnimationTriggerFrames; // Retry counter for loading animations after grid dimensions are ready

        private int _assetFileAMProjectCount;
        private int _assetFileAMCollectionCount;
        private int _assetFileAICaptionCount;

        // Cached project search results to avoid re-execution on page navigation
        private List<AssetInfo> _cachedProjectFiles;
        private List<AssetInfo> _cachedProjectOnlyFiles;
        private string _cachedProjectSearchKey;

        // Track the currently active saved search
        private int _activeSavedSearchIdBacking = -1;
        private int _activeSavedSearchId
        {
            get => _activeSavedSearchIdBacking;
            set
            {
                if (_activeSavedSearchIdBacking != value)
                {
                    _activeSavedSearchIdBacking = value;
                    // Reset restoration flag when active search changes
                    _variablesRestoredFromDb = false;
                }
            }
        }

        // Search query variables
        private Dictionary<string, SearchVariable> _searchVariables = new Dictionary<string, SearchVariable>();
        [NonSerialized] private bool _hasSearchVariables;
        [NonSerialized] private bool _variablesRestoredFromDb;

        private List<SavedSearch> Searches
        {
            get
            {
                if (_searches == null || !_searchesLoaded)
                {
                    _searches = DBAdapter.DB.Table<SavedSearch>().ToList();
                    _searchesLoaded = true;
                }
                return _searches;
            }
        }
        private List<SavedSearch> _searches;
        private bool _searchesLoaded;

        private List<Workspace> Workspaces
        {
            get
            {
                if (_workspaces == null || !_workspacesLoaded)
                {
                    _workspaces = DBAdapter.DB.Table<Workspace>().ToList();
                    _workspacesLoaded = true;
                }
                return _workspaces;
            }
        }
        private List<Workspace> _workspaces;
        private bool _workspacesLoaded;

        private void InitWorkspace()
        {
            if (!ShowWorkspaces() || AI.Config.workspace <= 0)
            {
                _selectedWorkspace = null;
                return;
            }
            SetWorkspace(Workspaces.FirstOrDefault(ws => ws.Id == AI.Config.workspace));
        }

        private void SetWorkspace(Workspace ws)
        {
            _selectedWorkspace = ws;
            List<WorkspaceSearch> searches = _selectedWorkspace?.LoadSearches();
            if (searches == null || searches.Count == 0)
            {
                // deactivate current in-memory mode if no searches are available
                _inMemoryMode = InMemoryModeState.None;
                _searchPhrase = "";
                _previousSearchPhrase = "";
                _requireSearchUpdate = true;
            }

            int oldWorkspace = AI.Config.workspace;
            AI.Config.workspace = ws == null ? 0 : ws.Id;
            if (oldWorkspace != AI.Config.workspace) AI.SaveConfig();
        }

        public void SetInitialSearch(string searchPhrase)
        {
            _searchPhrase = searchPhrase;
            _previousSearchPhrase = searchPhrase;
            AI.Config.tab = 0;
            _activeSavedSearchId = -1;
            DetectVariablesInSearchPhrase();
        }

        private void OnSearchDoubleClick(AssetInfo obj)
        {
            if ((searchMode || AI.Config.doubleClickAction > 0 || AI.Config.doubleClickAltAction > 0) && _selectedEntry != null)
            {
                if (searchMode)
                {
                    ExecuteSingleAction();
                }
                else
                {
                    int action = SGrid.LastClickAlt ? AI.Config.doubleClickAltAction : AI.Config.doubleClickAction;

                    switch (action)
                    {
                        case 2:
                            _ = PerformCopyTo(_selectedEntry, _importFolder, false, true);
                            break;

                        case 3:
                            _ = PerformCopyTo(_selectedEntry, _importFolder);
                            break;

                        case 4:
                            Open(_selectedEntry);
                            break;
                    }
                }
            }
        }

        private void OnSearchKeyboardSelection(int selectionIndex)
        {
            int count = _filteredFiles?.Count ?? 0;
            if (count == 0) return;

            SGrid.LimitSelection(count);
            if (selectionIndex < 0 || selectionIndex >= count) selectionIndex = SGrid.selectionTile;
            _selectedEntry = _filteredFiles[selectionIndex];
            _requireSearchSelectionUpdate = true;
            DisposeAnimTexture();

            // Mark that selection was changed via keyboard navigation
            // Used event is thrown if user manually selected the entry
            _searchSelectionChangedManually = Event.current.type == EventType.Used;
        }

        private void RecreatePreviewEditor()
        {
            if (_isCleaningUp) return;

            Object previewObject = _selectedEntry.InProject ? AssetDatabase.LoadAssetAtPath<Object>(_selectedEntry.ProjectPath) : null;
            if (_previewEditor != null)
            {
                DestroyImmediate(_previewEditor);
                _previewEditor = null;
            }

            if (previewObject != null)
            {
                _previewEditor = Editor.CreateEditor(previewObject);
            }
        }

        private void DrawSearchTab()
        {
            if (_lockSelection)
            {
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField("Making asset available in project...", CommonUIStyles.centerLabel);
                EditorGUILayout.LabelField("This can take a while depending on the size of the source package.", EditorStyles.centeredGreyMiniLabel);
                EditorGUILayout.Space(30);
                EditorGUILayout.LabelField(_curOperation, EditorStyles.centeredGreyMiniLabel);
                GUILayout.FlexibleSpace();
            }
            else
            {
                bool dirty = false;

                // Restore variables from database after recompile if we have an active saved search
                if (!_variablesRestoredFromDb && _activeSavedSearchId > 0 && _searchVariables.Count == 0 && !string.IsNullOrEmpty(_searchPhrase))
                {
                    SavedSearch search = Searches.FirstOrDefault(s => s.Id == _activeSavedSearchId);
                    if (search != null && !string.IsNullOrEmpty(search.VariableDefinitions))
                    {
                        _searchVariables = DeserializeSearchVariables(search.VariableDefinitions);
                        _hasSearchVariables = _searchVariables.Count > 0;
                    }
                    _variablesRestoredFromDb = true;
                }

                // Ensure variables are detected if search phrase has content but variables haven't been detected yet
                if (!string.IsNullOrEmpty(_searchPhrase) && !_hasSearchVariables && VariableResolver.ContainsVariables(_searchPhrase))
                {
                    DetectVariablesInSearchPhrase();
                    dirty = true;
                }

                // saved searches bar
                if (Searches.Count > 0)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.BeginVertical();
                    GUILayout.Space(5);

                    // Calculate available width for wrapping
                    float availableWidth = position.width - 50; // Account for margins + workspace dropdown
                    float currentX = 0;

                    GUILayout.BeginHorizontal();
                    GUILayout.Space(10);

                    // Get the searches to display, respecting workspace order if in workspace mode
                    IEnumerable<SavedSearch> searchesToDisplay;
                    if (ShowWorkspaces() && _selectedWorkspace != null && _selectedWorkspace.Searches != null)
                    {
                        // In workspace mode, use the order from _selectedWorkspace.Searches
                        searchesToDisplay = _selectedWorkspace.Searches
                            .OrderBy(ws => ws.OrderIdx)
                            .Select(ws => Searches.FirstOrDefault(s => s.Id == ws.SavedSearchId))
                            .Where(s => s != null);
                    }
                    else
                    {
                        // Normal mode, use all searches
                        searchesToDisplay = Searches;
                    }

                    foreach (SavedSearch search in searchesToDisplay)
                    {
                        RenderSavedSearchButton(
                            search,
                            _activeSavedSearchId,
                            search.SearchPhrase,
                            () =>
                            {
                                if (_activeSavedSearchId == search.Id)
                                {
                                    ResetSearch(false, false);
                                    _requireSearchUpdate = true;
                                }
                                else
                                {
                                    if (workspaceMode && AI.Config.wsSavedSearchInMemory)
                                    {
                                        _inMemoryMode = InMemoryModeState.Init;
                                    }
                                    LoadSearch(search);
                                }
                            },
                            (menu) =>
                            {
                                menu.AddItem(new GUIContent("Edit..."), false, () =>
                                {
                                    SavedSearchUI savedSearchUI = SavedSearchUI.ShowWindow();
                                    savedSearchUI.Init(search);
                                });
                                menu.AddItem(new GUIContent("Override with Current Search"), false, () =>
                                {
                                    OverrideSavedSearch(search);
                                });
                                menu.AddSeparator("");
                                menu.AddItem(new GUIContent("Delete"), false, () =>
                                {
                                    if (!EditorUtility.DisplayDialog("Confirm", $"Do you really want to delete the saved search '{search.Name}'?", "Yes", "No")) return;

                                    DBAdapter.DB.Delete(search);
                                    Searches.Remove(search);
                                    DBAdapter.DB.Execute("delete from WorkspaceSearch where SavedSearchId = ?", search.Id);
                                    _selectedWorkspace?.LoadSearches();
                                });
                            },
                            ref currentX,
                            availableWidth
                        );
                    }

                    GUILayout.EndHorizontal();
                    GUILayout.EndVertical();
                    if (ShowWorkspaces())
                    {
                        GUILayout.FlexibleSpace();
                        GUILayout.BeginVertical();
                        GUILayout.Space(5);
                        if (GUILayout.Button(EditorGUIUtility.IconContent("icon dropdown", "|Workspaces"), EditorStyles.miniButton, GUILayout.Width(28)))
                        {
                            GenericMenu menu = new GenericMenu();
                            menu.AddItem(new GUIContent("-No Workspace-"), _selectedWorkspace == null, () => SetWorkspace(null));
                            if (Workspaces.Count > 0)
                            {
                                menu.AddSeparator("");
                                foreach (Workspace ws in Workspaces)
                                {
                                    menu.AddItem(new GUIContent(ws.Name), _selectedWorkspace != null && _selectedWorkspace.Id == ws.Id, () => SetWorkspace(ws));
                                }
                            }
                            menu.AddSeparator("");
                            menu.AddItem(new GUIContent("New..."), false, () =>
                            {
                                NameUI nameUI = new NameUI();
                                nameUI.Init("My Workspace", SaveWorkspace);
                                PopupWindow.Show(GetPopupPositionAtMouse(), nameUI);
                            });
                            if (_selectedWorkspace != null)
                            {
                                menu.AddItem(new GUIContent("Edit..."), false, () =>
                                {
                                    WorkspaceUI workspaceUI = WorkspaceUI.ShowWindow();
                                    workspaceUI.Init(_selectedWorkspace);
                                });
                                menu.AddItem(new GUIContent("Delete"), false, () =>
                                {
                                    if (!EditorUtility.DisplayDialog("Confirm", $"Do you really want to delete workspace '{_selectedWorkspace.Name}'?", "Yes", "No")) return;

                                    Workspaces.Remove(_selectedWorkspace);
                                    DBAdapter.DB.Execute("delete from WorkspaceSearch where WorkspaceId = ?", _selectedWorkspace.Id);
                                    DBAdapter.DB.Delete(_selectedWorkspace);
                                    SetWorkspace(null);
                                });
                            }
                            menu.ShowAsContext();
                        }
                        GUILayout.EndVertical();
                        GUILayout.Space(2);
                    }
                    GUILayout.EndHorizontal();
                    EditorGUILayout.Space();
                }

                // search bar
                GUILayout.BeginVertical("box");
                GUILayout.BeginHorizontal();
                if (_inMemoryMode == InMemoryModeState.None)
                {
                    EditorGUILayout.LabelField("Search:", GUILayout.Width(50));
                    EditorGUIUtility.labelWidth = 60;
                    EditorGUI.BeginChangeCheck();
                    _searchPhrase = SearchField.OnGUI(_searchPhrase, GUILayout.ExpandWidth(true));
                    if (EditorGUI.EndChangeCheck())
                    {
                        // Only trigger if actual text changed, not just cursor movement
                        if (_searchPhrase != _previousSearchPhrase)
                        {
                            _previousSearchPhrase = _searchPhrase;

                            // delay search to allow fast typing
                            _nextSearchTime = Time.realtimeSinceStartup + AI.Config.searchDelay;
                            // Delay variable detection to avoid lag while typing
                            _nextVariableDetectionTime = Time.realtimeSinceStartup + AI.Config.variableDetectionDelay;
                            // Clear active saved search when manually changing search phrase
                            _activeSavedSearchId = -1;
                        }
                    }
                    else if (_nextSearchTime > 0 && Time.realtimeSinceStartup > _nextSearchTime)
                    {
                        _nextSearchTime = 0;
                        if (AI.Config.searchAutomatically && !_searchPhrase.StartsWith("=")) dirty = true;
                    }

                    // Check if variable detection should run
                    // Only run in OnGUI if searchAutomatically is off, otherwise let PerformSearch handle it
                    if (!AI.Config.searchAutomatically && _nextVariableDetectionTime > 0 && Time.realtimeSinceStartup > _nextVariableDetectionTime)
                    {
                        _nextVariableDetectionTime = 0;
                        DetectVariablesInSearchPhrase();
                    }

                    if (_allowLogic && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
                    {
                        PerformSearch();
                    }
                    if (!AI.Config.searchAutomatically)
                    {
                        if (GUILayout.Button("Go", GUILayout.Width(30)))
                        {
                            PerformSearch();
                        }
                    }

                    if (_searchPhrase != null && _searchPhrase.StartsWith("="))
                    {
                        EditorGUI.BeginChangeCheck();
                        GUILayout.Space(2);
                        _selectedExpertSearchField = EditorGUILayout.Popup(_selectedExpertSearchField, _expertSearchFields, GUILayout.Width(90));
                        if (EditorGUI.EndChangeCheck())
                        {
                            string field = _expertSearchFields[_selectedExpertSearchField];
                            if (!string.IsNullOrEmpty(field) && !field.StartsWith("-"))
                            {
                                _searchPhrase += field.Replace('/', '.');
                                SearchField.SetFocus();
                            }
                            _selectedExpertSearchField = 0;
                        }
                    }
                    UILine("search.actions.assistant", () =>
                    {
                        if (GUILayout.Button(CommonUIStyles.Content("?", "Show example searches"), GUILayout.Width(20)))
                        {
                            AdvancedSearchUI searchUI = new AdvancedSearchUI();
                            searchUI.Init((searchPhrase, searchType) =>
                            {
                                _searchPhrase = searchPhrase;
                                _previousSearchPhrase = searchPhrase;
                                if (searchType == null)
                                {
                                    AI.Config.searchType = 0;
                                }
                                else
                                {
                                    int typeIdx = Array.IndexOf(_types, searchType);
                                    if (typeIdx >= 0) AI.Config.searchType = typeIdx;
                                }
                                _requireSearchUpdate = true;
                            });
                            PopupWindow.Show(GetPopupPositionAtMouse(), searchUI);
                        }
                    });
                    if (_fixedSearchTypeIdx < 0)
                    {
                        EditorGUI.BeginChangeCheck();
                        GUILayout.Space(2);
                        AI.Config.searchType = EditorGUILayout.Popup(AI.Config.searchType, _types, GUILayout.ExpandWidth(false), GUILayout.MinWidth(85));
                        if (EditorGUI.EndChangeCheck())
                        {
                            AI.SaveConfig();
                            dirty = true;
                            // Clear active saved search when search type changes
                            _activeSavedSearchId = -1;
                        }
                    }
                    UIBlock("asset.actions.savedsearches", () =>
                    {
                        if (GUILayout.Button(EditorGUIUtility.IconContent("d_saveas", "|Save current search to quickly pull up the results later again"), EditorStyles.miniButton))
                        {
                            NameUI nameUI = new NameUI();
                            nameUI.Init(string.IsNullOrEmpty(_searchPhrase) ? "My Search" : _searchPhrase, SaveSearch);
                            PopupWindow.Show(GetPopupPositionAtMouse(), nameUI);
                        }
                        GUILayout.Space(2);
                    });
                    ShowInMemoryButton();
                }
                else
                {
                    UIBlock("asset.hints.inmemoryactive", () =>
                    {
                        EditorGUILayout.HelpBox($"In-Memory search is active. The {_originalResultCount:N0} results of the initial search are now the foundation for any subsequent, much faster, search.", MessageType.Info);
                    });
                }

                GUILayout.EndHorizontal();

                if (_inMemoryMode != InMemoryModeState.None)
                {
                    EditorGUILayout.Space();
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Refine Search:", GUILayout.Width(90));
                    EditorGUI.BeginChangeCheck();
                    _searchPhraseInMemory = SearchField.OnGUI(_searchPhraseInMemory, GUILayout.ExpandWidth(true));
                    if (EditorGUI.EndChangeCheck())
                    {
                        // delay search to allow fast typing
                        _nextSearchTime = Time.realtimeSinceStartup + AI.Config.inMemorySearchDelay;
                    }
                    else if (_nextSearchTime > 0 && Time.realtimeSinceStartup > _nextSearchTime)
                    {
                        _nextSearchTime = 0;
                        UpdateFilteredFiles();
                    }
                    if (_allowLogic && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
                    {
                        UpdateFilteredFiles();
                    }
                    ShowInMemoryButton();
                    GUILayout.EndHorizontal();
                }

                // variable input UI
                if (_hasSearchVariables)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(53);

                    foreach (KeyValuePair<string, SearchVariable> kvp in _searchVariables.OrderBy(v => v.Key))
                    {
                        EditorGUILayout.LabelField(kvp.Key + ":", GUILayout.Width(40));

                        // Text field
                        EditorGUI.BeginChangeCheck();
                        string newValue = EditorGUILayout.TextField(kvp.Value.currentValue ?? "", GUILayout.ExpandWidth(true));
                        if (EditorGUI.EndChangeCheck())
                        {
                            kvp.Value.currentValue = newValue;
                            _requireSearchUpdate = true;
                        }

                        // Only show dropdown for saved searches (where options/defaults are useful)
                        if (_activeSavedSearchId > 0)
                        {
                            // Dropdown button
                            if (EditorGUILayout.DropdownButton(CommonUIStyles.Content(string.Empty, "Select value"), FocusType.Keyboard))
                            {
                                ShowVariableDropdown(kvp.Value);
                            }
                        }

                        EditorGUILayout.Space();
                    }
                    GUILayout.FlexibleSpace();

                    GUILayout.EndHorizontal();
                }

                // error display
                if (!string.IsNullOrEmpty(_searchError))
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(55);
                    EditorGUILayout.LabelField($"Error: {_searchError}", CommonUIStyles.ColoredText(Color.red));
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndVertical();

                EditorGUILayout.Space();
                GUILayout.BeginHorizontal();

                // left hierarchy sidebar
                if (!searchMode && AI.Config.showSearchHierarchySideBar)
                {
                    DrawLeftHierarchySidebar();
                    EditorGUILayout.Space();
                }

                if (SGrid == null || (SGrid.contents != null && SGrid.contents.Length > 0 && _files == null)) PerformSearch(); // happens during recompilation
                GUILayout.BeginVertical();
                GUILayout.BeginHorizontal();

                // assets
                GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
                bool isAudio = AI.IsFileType(_selectedEntry?.Path, AI.AssetGroup.Audio);
                bool hasGridResults = SGrid.contents != null && SGrid.contents.Length > 0;
                bool hasListResults = AI.Config.searchViewMode == 0 && SearchTreeView != null;
                if (hasGridResults || hasListResults)
                {
                    if (AI.Config.searchViewMode == 0)
                    {
                        // List view - wrap in scroll view like hierarchy sidebar
                        _searchScrollPos = GUILayout.BeginScrollView(_searchScrollPos, false, false);
                        Rect treeRect = GUILayoutUtility.GetRect(0, 10000, 0, 10000);
                        SearchTreeView.OnGUI(treeRect);
                        GUILayout.EndScrollView();
                    }
                    else
                    {
                        // Grid view
                        _searchScrollPos = GUILayout.BeginScrollView(_searchScrollPos, false, false);

                        // Capture the visible height while inside scroll view context
                        Rect visibleRect = CommonUIStyles.GetCurrentVisibleRect();
                        if (visibleRect.height > 0)
                        {
                            _searchGridViewHeight = visibleRect.height;
                        }

                        // draw contents
                        EditorGUI.BeginChangeCheck();

                        int inspectorCount = (hideDetailsPane || !AI.Config.showSearchSideBar) ? 0 : 1;
                        int leftSidebarWidth = (!searchMode && AI.Config.showSearchHierarchySideBar) ? UIStyles.INSPECTOR_WIDTH : 0;
                        float availableWidth = position.width - UIStyles.INSPECTOR_WIDTH * inspectorCount - leftSidebarWidth;
                        SGrid.Draw(availableWidth, AI.Config.searchTileSize, AI.Config.searchTileAspectRatio, UIStyles.searchTile, UIStyles.selectedSearchTile);

                        if (EditorGUI.EndChangeCheck() || (_allowLogic && _searchDone))
                        {
                            // interactions
                            if (!_searchDone) SGrid.HandleMouseClicks();
                            OnSearchKeyboardSelection(SGrid.selectionTile);
                        }
                        GUILayout.FlexibleSpace();
                        GUILayout.EndScrollView();

                        // Only auto-scroll after keyboard navigation occurred (allows free manual scrolling)
                        if (SGrid.CheckAndResetKeyboardNavigation())
                        {
                            SGrid.EnsureSelectedTileVisible(ref _searchScrollPos, CommonUIStyles.GetCurrentVisibleRect().height);
                        }
                    }
                }
                else
                {
                    if (!_lockSelection) _selectedEntry = null;
                    if (!SearchWithoutInput() && !IsSearchFilterActive() && string.IsNullOrWhiteSpace(_searchPhrase))
                    {
                        GUILayout.Label("Enter search phrase to start searching", EditorStyles.centeredGreyMiniLabel, GUILayout.MinHeight(AI.Config.searchTileSize));
                    }
                    else
                    {
                        GUILayout.Label("No matching results", CommonUIStyles.whiteCenter, GUILayout.MinHeight(AI.Config.searchTileSize));

                        bool isIndexing = AI.Actions.ActionsInProgress;
                        bool hasHiddenExtensions = AI.Config.searchType == 0 && !string.IsNullOrWhiteSpace(AI.Config.excludedExtensions);
                        bool hasHiddenPreviews = AI.Config.previewVisibility > 0;
                        if (isIndexing || hasHiddenExtensions || hasHiddenPreviews)
                        {
                            GUILayout.Label("Search result is potentially limited", EditorStyles.centeredGreyMiniLabel);
                            if (isIndexing) GUILayout.Label("Index is currently being updated", EditorStyles.centeredGreyMiniLabel);
                            if (hasHiddenExtensions)
                            {
                                EditorGUILayout.Space();
                                GUILayout.Label($"Hidden extensions: {FormatHiddenExtensionsForDisplay(AI.Config.excludedExtensions)}", CommonUIStyles.centeredGreyWrappedMiniLabel);
                                GUILayout.BeginHorizontal();
                                GUILayout.FlexibleSpace();
                                if (GUILayout.Button("Ignore Once", GUILayout.Width(100))) PerformSearch(false, true);
                                GUILayout.FlexibleSpace();
                                GUILayout.EndHorizontal();
                                EditorGUILayout.Space();
                            }
                            if (hasHiddenPreviews) GUILayout.Label("Results depend on preview availability", EditorStyles.centeredGreyMiniLabel);
                        }
                    }
                }

                // navigation toolbar (always visible)
                GUILayout.FlexibleSpace();
                EditorGUILayout.Space();
                GUILayout.BeginHorizontal();

                if (!searchMode)
                {
                    UILine("search.actions.leftsidebar", () =>
                    {
                        if (GUILayout.Button(CommonUIStyles.IconContent("d_UnityEditor.SceneHierarchyWindow", "UnityEditor.SceneHierarchyWindow", "|Show/Hide Hierarchy Browser"), EditorStyles.miniButtonLeft))
                        {
                            AI.Config.showSearchHierarchySideBar = !AI.Config.showSearchHierarchySideBar;
                            AI.SaveConfig();
                            _requireHierarchyRebuild = true;
                        }
                        EditorGUILayout.Space();
                    });
                }

                UILine("search.actions.viewmode", () =>
                {
                    // view mode toggle
                    EditorGUI.BeginChangeCheck();
                    AI.Config.searchViewMode = GUILayout.Toolbar(AI.Config.searchViewMode, _packageViewOptions, GUILayout.Width(50), GUILayout.Height(20));
                    if (EditorGUI.EndChangeCheck())
                    {
                        // Reset selection when switching view modes to avoid stale bulk selection state
                        SGrid.DeselectAll();
                        _selectedEntry = null;
                        AI.SaveConfig();
                    }
                });

                if (AI.Config.searchViewMode == 1)
                {
                    UILine("search.actions.tilesize", () =>
                    {
                        EditorGUI.BeginChangeCheck();
                        AI.Config.searchTileSize = EditorGUILayout.IntSlider(AI.Config.searchTileSize, 50, 300, GUILayout.Width(150));
                        if (EditorGUI.EndChangeCheck())
                        {
                            _lastTileSizeChange = DateTime.Now;
                            AI.SaveConfig();
                        }
                    });

                    UILine("search.actions.previewanim", () =>
                    {
                        EditorGUI.BeginChangeCheck();
                        bool newValue = GUILayout.Toggle(AI.Config.playVisibleSearchAnimations,
                            EditorGUIUtility.IconContent("d_PlayButton", "|Play all visible animated previews automatically to quickly evaluate particle systems and animations"),
                            EditorStyles.miniButton, GUILayout.Width(24));
                        if (EditorGUI.EndChangeCheck())
                        {
                            AI.Config.playVisibleSearchAnimations = newValue;
                            AI.SaveConfig();
                            if (newValue)
                            {
                                TriggerVisibleAnimationsUpdate();
                            }
                            else
                            {
                                DisposeAllVisibleAnimations(true);
                            }
                        }
                    });
                }

                GUILayout.FlexibleSpace();
                if (hasGridResults || hasListResults)
                {
                    if (_pageCount > 1)
                    {
                        EditorGUI.BeginDisabledGroup(_curPage <= 1);
                        if (GUILayout.Button("<", GUILayout.ExpandWidth(false))) SetPage(_curPage - 1);
                        EditorGUI.EndDisabledGroup();

                        if (EditorGUILayout.DropdownButton(CommonUIStyles.Content($"Page {_curPage:N0}/{_pageCount:N0}", $"{_resultCount:N0} results in total"), FocusType.Keyboard, CommonUIStyles.centerPopup, GUILayout.MinWidth(100)))
                        {
                            DropDownUI pageUI = new DropDownUI();
                            pageUI.Init(1, _pageCount, _curPage, "Page ", null, SetPage);
                            PopupWindow.Show(GetPopupPositionAtMouse(), pageUI);
                        }

                        EditorGUI.BeginDisabledGroup(_curPage >= _pageCount);
                        if (GUILayout.Button(">", GUILayout.ExpandWidth(false))) SetPage(_curPage + 1);
                        EditorGUI.EndDisabledGroup();
                    }
                    else
                    {
                        EditorGUILayout.LabelField($"{_resultCount:N0} results", CommonUIStyles.centerLabel, GUILayout.Width(120));
                    }
                }

                if (!searchMode)
                {
                    UILine("search.actions.scope", () =>
                    {
                        if (DrawSearchScopeToolbar())
                        {
                            _requireSearchUpdate = true;
                            CreateAssetTree();
                        }
                    });
                }

                GUILayout.FlexibleSpace();

                if (!hideDetailsPane && !searchMode)
                {
                    UILine("search.actions.sidebar", () =>
                    {
                        if (GUILayout.Button(CommonUIStyles.IconContent("unityeditor.scenehierarchywindow", "d_unityeditor.hierarchywindow", "|Show/Hide Details Inspector"), EditorStyles.miniButtonRight))
                        {
                            AI.Config.showSearchSideBar = !AI.Config.showSearchSideBar;
                            AI.SaveConfig();
                        }
                    });
                }

                GUILayout.EndHorizontal();
                EditorGUILayout.Space();
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();

                // inspector
                if (!hideDetailsPane && AI.Config.showSearchSideBar)
                {
                    EditorGUILayout.Space();

                    int labelWidth = 95;
                    GUILayout.BeginVertical(GUILayout.Width(UIStyles.INSPECTOR_WIDTH));

                    GUILayout.BeginHorizontal();
                    List<string> strings = new List<string>
                    {
                        "Details",
                        "Filters" + (IsSearchFilterActive() ? "*" : "")
                    };
                    _searchInspectorTab = CommonUIStyles.DrawTabBar(_searchInspectorTab, strings.ToArray());
                    UIBlock("search.actions.settings", () =>
                    {
                        if (GUILayout.Button(EditorGUIUtility.IconContent("Settings", "|Manage View"), EditorStyles.miniButton, GUILayout.ExpandWidth(false), GUILayout.Height(18)))
                        {
                            _searchInspectorTab = -1;
                        }
                        GUILayout.Space(2);
                    });
                    GUILayout.EndHorizontal();

                    GUILayout.BeginVertical(GUILayout.Width(UIStyles.INSPECTOR_WIDTH));
                    EditorGUIUtility.labelWidth = 1;
                    _inspectorScrollPos = EditorGUILayout.BeginScrollView(_inspectorScrollPos, GUIStyle.none, GUI.skin.verticalScrollbar);
                    switch (_searchInspectorTab)
                    {
                        case -1:
                            EditorGUILayout.Space();

                            EditorGUI.BeginChangeCheck();

                            int width = 135;

                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Search In", "Field to use for finding assets when doing plain searches and no expert search."), EditorStyles.boldLabel, GUILayout.Width(width));
                            AI.Config.searchField = EditorGUILayout.Popup(AI.Config.searchField, _searchFields);
                            GUILayout.EndHorizontal();

                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField("", EditorStyles.boldLabel, GUILayout.Width(width));
                            AI.Config.searchPackageNames = EditorGUILayout.ToggleLeft(CommonUIStyles.Content("Package Name", "Search also in package names for hits."), AI.Config.searchPackageNames);
                            GUILayout.EndHorizontal();

                            if (AI.Actions.CreateAICaptions)
                            {
                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField("", EditorStyles.boldLabel, GUILayout.Width(width));
                                AI.Config.searchAICaptions = EditorGUILayout.ToggleLeft(CommonUIStyles.Content("AI Captions", "Search also in AI captions for hits."), AI.Config.searchAICaptions);
                                GUILayout.EndHorizontal();
                            }

                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Sort by", "Specify the sort order. Unsorted will result in the fastest experience."), EditorStyles.boldLabel, GUILayout.Width(width));
                            AI.Config.sortField = EditorGUILayout.Popup(AI.Config.sortField, _sortFields);
                            if (GUILayout.Button(AI.Config.sortDescending ? CommonUIStyles.Content("˅", "Descending") : CommonUIStyles.Content("˄", "Ascending"), GUILayout.Width(17)))
                            {
                                AI.Config.sortDescending = !AI.Config.sortDescending;
                            }
                            GUILayout.EndHorizontal();

                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Results", $"Maximum number of results to show. A (configurable) hard limit of {AI.Config.maxResultsLimit} will be enforced to keep Unity responsive."), EditorStyles.boldLabel, GUILayout.Width(width));
                            AI.Config.maxResults = EditorGUILayout.Popup(AI.Config.maxResults, _resultSizes);
                            GUILayout.EndHorizontal();

                            if (ShowAdvanced())
                            {
                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField(CommonUIStyles.Content("In-Memory Results", "Maximum number of results to show when high-speed mode is active. The higher this value the more results you can browse but the more memory will also be consumed."), EditorStyles.boldLabel, GUILayout.Width(width));
                                AI.Config.maxInMemoryResults = EditorGUILayout.DelayedIntField(AI.Config.maxInMemoryResults, GUILayout.Width(80));
                                GUILayout.EndHorizontal();

                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField(CommonUIStyles.Content("In-Project Results", "Maximum number of project search results to include. Set to 0 or less for no limit. Higher values may slow down search in large projects."), EditorStyles.boldLabel, GUILayout.Width(width));
                                AI.Config.maxProjectSearchResults = EditorGUILayout.DelayedIntField(AI.Config.maxProjectSearchResults, GUILayout.Width(80));
                                GUILayout.EndHorizontal();

                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField(CommonUIStyles.Content("Show Index Scope", "Expose an index-only search scope that avoids scanning the current project."), EditorStyles.boldLabel, GUILayout.Width(width));
                                AI.Config.showIndexSearchScope = EditorGUILayout.Toggle(AI.Config.showIndexSearchScope);
                                GUILayout.EndHorizontal();
                            }

                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Hide Extensions", "File extensions to hide from search results when searching for all file types, e.g. asset;json;txt. These will still be indexed."), EditorStyles.boldLabel, GUILayout.Width(width));
                            AI.Config.excludeExtensions = EditorGUILayout.Toggle(AI.Config.excludeExtensions, GUILayout.Width(20));
                            if (AI.Config.excludeExtensions)
                            {
                                if (GUILayout.Button(EditorGUIUtility.IconContent("editicon.sml", "|Edit list"), GUILayout.Width(24)))
                                {
                                    StringListUI listUI = new StringListUI();
                                    listUI.Init(AI.Config.excludedExtensions, ";", result =>
                                    {
                                        AI.Config.excludedExtensions = result;
                                        AI.SaveConfig();
                                    }, "Hidden Extensions");
                                    PopupWindow.Show(GetPopupPositionAtMouse(), listUI);
                                }
                                GUILayout.EndHorizontal();

                                AI.Config.excludedExtensions = EditorGUILayout.TextArea(AI.Config.excludedExtensions, CommonUIStyles.wrappedTextArea, GUILayout.Height(50), GUILayout.ExpandWidth(true));
                                AI.Config.excludedExtensions = AI.Config.excludedExtensions.Replace("\n", "").Replace("\r", "");
                            }
                            else
                            {
                                GUILayout.EndHorizontal();
                            }

                            if (EditorGUI.EndChangeCheck())
                            {
                                GetConfiguredSearchScope();
                                dirty = true;
                                _curPage = 1;
                                AI.SaveConfig();
                            }

                            EditorGUI.BeginChangeCheck();
                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Tile Text", "Text to be shown on the tile"), EditorStyles.boldLabel, GUILayout.Width(width));
                            AI.Config.tileText = EditorGUILayout.Popup(AI.Config.tileText, _tileTitle);
                            GUILayout.EndHorizontal();
                            if (EditorGUI.EndChangeCheck())
                            {
                                dirty = true;
                                AI.SaveConfig();
                            }

                            EditorGUILayout.Space();

                            EditorGUI.BeginChangeCheck();
                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Search While Typing", "Will search immediately while typing and update results constantly."), EditorStyles.boldLabel, GUILayout.Width(width));
                            AI.Config.searchAutomatically = EditorGUILayout.Toggle(AI.Config.searchAutomatically);
                            GUILayout.EndHorizontal();
                            if (EditorGUI.EndChangeCheck()) AI.SaveConfig();

                            EditorGUI.BeginChangeCheck();
                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Search Without Input", "Will always show search results also when no keywords or filters are set."), EditorStyles.boldLabel, GUILayout.Width(width));
                            AI.Config.searchWithoutInput = EditorGUILayout.Toggle(AI.Config.searchWithoutInput);
                            GUILayout.EndHorizontal();

                            if (ShowAdvanced())
                            {
                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField(CommonUIStyles.Content("Sub-Packages", "Will search through sub-packages as well if a filter is set for a specific package."), EditorStyles.boldLabel, GUILayout.Width(width));
                                AI.Config.searchSubPackages = EditorGUILayout.Toggle(AI.Config.searchSubPackages);
                                GUILayout.EndHorizontal();

                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField(CommonUIStyles.Content("Exclude Wrong SRPs", "Automatically exclude packages that don't match the current render pipeline (URP/HDRP) based on package name keywords."), EditorStyles.boldLabel, GUILayout.Width(width));
                                AI.Config.excludeIncompatibleSRPs = EditorGUILayout.Toggle(AI.Config.excludeIncompatibleSRPs);
                                GUILayout.EndHorizontal();
                            }

                            if (EditorGUI.EndChangeCheck())
                            {
                                dirty = true;
                                AI.SaveConfig();
                            }

                            EditorGUI.BeginChangeCheck();

                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Auto-Play Audio", "Will automatically extract Unity packages to play the sound file if they were not extracted yet. This is the most convenient option but will require sufficient hard disk space."), EditorStyles.boldLabel, GUILayout.Width(width));
                            AI.Config.autoPlayAudio = EditorGUILayout.Toggle(AI.Config.autoPlayAudio);
                            GUILayout.EndHorizontal();

                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Ping Selected", "Highlight selected items in the Unity project tree if they are found in the current project."), EditorStyles.boldLabel, GUILayout.Width(width));
                            AI.Config.pingSelected = EditorGUILayout.Toggle(AI.Config.pingSelected);
                            GUILayout.EndHorizontal();

                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Ping Imported", "Highlight items in the Unity project tree after import."), EditorStyles.boldLabel, GUILayout.Width(width));
                            AI.Config.pingImported = EditorGUILayout.Toggle(AI.Config.pingImported);
                            GUILayout.EndHorizontal();

                            if (ShowAdvanced())
                            {
                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField(CommonUIStyles.Content("Disable Drag & Drop", "Will not allow to drag & drop items from the search into other Unity views. Can improve selection behavior in case you are struggling with these in the search results."), EditorStyles.boldLabel, GUILayout.Width(width));
                                AI.Config.disableDragDrop = EditorGUILayout.Toggle(AI.Config.disableDragDrop);
                                GUILayout.EndHorizontal();
                            }

                            if (EditorGUI.EndChangeCheck()) AI.SaveConfig();

                            EditorGUI.BeginChangeCheck();
                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("On Double-Click", "Define what should happen when double-clicking on search results."), EditorStyles.boldLabel, GUILayout.Width(width));
                            AI.Config.doubleClickAction = EditorGUILayout.Popup(AI.Config.doubleClickAction, _doubleClickOptions);
                            GUILayout.EndHorizontal();
                            if (EditorGUI.EndChangeCheck()) AI.SaveConfig();

                            EditorGUI.BeginChangeCheck();
                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("On Alt+Double-Click", "Define what should happen when double-clicking on search results while holding the ALT key."), EditorStyles.boldLabel, GUILayout.Width(width));
                            AI.Config.doubleClickAltAction = EditorGUILayout.Popup(AI.Config.doubleClickAltAction, _doubleClickOptions);
                            GUILayout.EndHorizontal();
                            if (EditorGUI.EndChangeCheck()) AI.SaveConfig();

                            EditorGUI.BeginChangeCheck();
                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Dependency Calc", "Can automatically calculate dependencies for assets that are already extracted."), EditorStyles.boldLabel, GUILayout.Width(width));
                            AI.Config.autoCalculateDependencies = EditorGUILayout.Popup(AI.Config.autoCalculateDependencies, _dependencyOptions);
                            GUILayout.EndHorizontal();

                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Previews", "Optionally restricts search results to those with either preview images available or not."), EditorStyles.boldLabel, GUILayout.Width(width));
                            AI.Config.previewVisibility = EditorGUILayout.Popup(AI.Config.previewVisibility, _previewOptions);
                            GUILayout.EndHorizontal();
                            if (EditorGUI.EndChangeCheck())
                            {
                                dirty = true;
                                AI.SaveConfig();
                            }

                            if (ShowAdvanced())
                            {
                                EditorGUILayout.Space();
                                EditorGUILayout.LabelField("UI", EditorStyles.largeLabel);

                                EditorGUI.BeginChangeCheck();
                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField(CommonUIStyles.Content("Group Lists", "Add a second level hierarchy to dropdowns if they become too long to scroll."), EditorStyles.boldLabel, GUILayout.Width(width));
                                AI.Config.groupLists = EditorGUILayout.Toggle(AI.Config.groupLists);
                                GUILayout.EndHorizontal();
                                if (EditorGUI.EndChangeCheck())
                                {
                                    AI.SaveConfig();
                                    ReloadLookups();
                                }

                                EditorGUI.BeginChangeCheck();
                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField(CommonUIStyles.Content("Show Workspaces", "Shows workspaces even when not in browser mode."), EditorStyles.boldLabel, GUILayout.Width(width));
                                AI.Config.alwaysShowWorkspaces = EditorGUILayout.Toggle(AI.Config.alwaysShowWorkspaces);
                                GUILayout.EndHorizontal();
                                if (EditorGUI.EndChangeCheck())
                                {
                                    AI.SaveConfig();
                                }

                                EditorGUI.BeginChangeCheck();
                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField(CommonUIStyles.Content("Tile Aspect Ratio", "Adjusts the height of the tiles."), EditorStyles.boldLabel, GUILayout.Width(width));
                                AI.Config.searchTileAspectRatio = EditorGUILayout.Slider(AI.Config.searchTileAspectRatio, 0.3f, 3f);
                                GUILayout.EndHorizontal();

                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField(CommonUIStyles.Content("Tile Margins", "Adjusts the space between tiles."), EditorStyles.boldLabel, GUILayout.Width(width));
                                AI.Config.tileMargin = EditorGUILayout.IntSlider(AI.Config.tileMargin, -3, 30);
                                GUILayout.EndHorizontal();
                                if (EditorGUI.EndChangeCheck())
                                {
                                    _lastTileSizeChange = DateTime.Now;
                                    AI.SaveConfig();
                                }

                                EditorGUI.BeginChangeCheck();
                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField(CommonUIStyles.Content("Tile Corner Radius", "Roundness of corners of tiles."), EditorStyles.boldLabel, GUILayout.Width(width));
                                AI.Config.tileCornerRadius = EditorGUILayout.DelayedIntField(AI.Config.tileCornerRadius, GUILayout.Width(50));
                                EditorGUILayout.LabelField("px", EditorStyles.miniLabel, GUILayout.Width(50));
                                GUILayout.EndHorizontal();
                                if (EditorGUI.EndChangeCheck())
                                {
                                    dirty = true;
                                    AI.SaveConfig();
                                }

                                EditorGUI.BeginChangeCheck();
                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField(CommonUIStyles.Content("Play All Animations", "Automatically play all visible animated previews in the grid to quickly evaluate particle systems and animations."), EditorStyles.boldLabel, GUILayout.Width(width));
                                AI.Config.playVisibleSearchAnimations = EditorGUILayout.Toggle(AI.Config.playVisibleSearchAnimations);
                                GUILayout.EndHorizontal();
                                if (EditorGUI.EndChangeCheck())
                                {
                                    AI.SaveConfig();
                                    if (AI.Config.playVisibleSearchAnimations)
                                    {
                                        TriggerVisibleAnimationsUpdate();
                                    }
                                    else
                                    {
                                        DisposeAllVisibleAnimations(true);
                                    }
                                }

                                if (AI.Config.playVisibleSearchAnimations)
                                {
                                    EditorGUI.BeginChangeCheck();
                                    GUILayout.BeginHorizontal();
                                    EditorGUILayout.LabelField(CommonUIStyles.Content("Max Animations", "Maximum number of animated previews to play simultaneously."), EditorStyles.boldLabel, GUILayout.Width(width));
                                    AI.Config.maxVisibleSearchAnimations = EditorGUILayout.DelayedIntField(AI.Config.maxVisibleSearchAnimations, GUILayout.Width(50));
                                    GUILayout.EndHorizontal();
                                    if (EditorGUI.EndChangeCheck()) AI.SaveConfig();
                                }

                                EditorGUI.BeginChangeCheck();
                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField(CommonUIStyles.Content("List Row Height", "Height of rows in list view mode. Icon scales accordingly."), EditorStyles.boldLabel, GUILayout.Width(width));
                                AI.Config.searchListRowHeight = EditorGUILayout.IntSlider(AI.Config.searchListRowHeight, 16, 256);
                                GUILayout.EndHorizontal();
                                if (EditorGUI.EndChangeCheck())
                                {
                                    _searchTreeView = null;
                                    AI.SaveConfig();
                                }
                            }

                            break;

                        case 0:
                            if (SGrid.selectionCount <= 1)
                            {
                                EditorGUILayout.Space();
                                if (_selectedEntry == null || string.IsNullOrEmpty(_selectedEntry.SafeName))
                                {
                                    // will happen after script reload
                                    EditorGUILayout.HelpBox("Select an asset for details", MessageType.Info);
                                }
                                else
                                {
                                    DrawFileInfo(_selectedEntry, !searchMode);

                                    // render preview if available
                                    if (_previewEditor != null && !_isCleaningUp)
                                    {
                                        if (_previewEditor.HasPreviewGUI())
                                        {
                                            AI.Config.showPreviews = EditorGUILayout.BeginFoldoutHeaderGroup(AI.Config.showPreviews, "Preview");
                                            if (AI.Config.showPreviews)
                                            {
                                                // Allocate space for the preview
                                                Rect previewRect = GUILayoutUtility.GetRect(AI.Config.previewSize, AI.Config.previewSize, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                                                _previewEditor.OnPreviewGUI(previewRect, EditorStyles.whiteLabel);
                                            }
                                            EditorGUILayout.EndFoldoutHeaderGroup();
                                        }
                                    }
                                    EditorGUILayout.Space();

                                    DrawPackageInfo(_selectedEntry, false, !searchMode, false);
                                }

                            }
                            else
                            {
                                EditorGUILayout.Space();
                                EditorGUILayout.LabelField("Bulk Actions", EditorStyles.largeLabel);
                                UIBlock("asset.bulk.count", () => GUILabelWithText("Selected", $"{SGrid.selectionCount:N0}"));
                                UIBlock("asset.bulk.packages", () => GUILabelWithText("Packages", $"{SGrid.selectionPackageCount:N0}"));
                                UIBlock("asset.bulk.size", () => GUILabelWithText("Size", EditorUtility.FormatBytes(SGrid.selectionSize)));

                                int inProject = SGrid.selectionItems.Count(item => item.InProject);
                                UIBlock("asset.bulk.inproject", () =>
                                {
                                    GUILabelWithText("In Project", $"{inProject:N0}/{SGrid.selectionCount:N0}");
                                });

                                // Bulk cancel dependency calculations
                                int calculatingCount = SGrid.selectionItems.Count(item => item.DependencyState == AssetInfo.DependencyStateOptions.Calculating);
                                if (calculatingCount > 0)
                                {
                                    UIBlock("asset.bulk.actions.cancelcalculations", () =>
                                    {
                                        GUILayout.BeginHorizontal();
                                        EditorGUILayout.LabelField("Deps Calc", EditorStyles.boldLabel, GUILayout.Width(95));
                                        EditorGUILayout.LabelField($"{calculatingCount:N0}", GUILayout.Width(40), GUILayout.ExpandWidth(true));
                                        GUILayout.EndHorizontal();
                                        if (GUILayout.Button("Cancel Calculations"))
                                        {
                                            foreach (AssetInfo item in SGrid.selectionItems)
                                            {
                                                if (item.DependencyState == AssetInfo.DependencyStateOptions.Calculating)
                                                {
                                                    CancelDependencyCalculation(item);
                                                }
                                            }
                                        }
                                    });
                                }

                                EditorGUI.BeginDisabledGroup(_blockingInProgress);
                                bool bulkNeedsDownload = SGrid.selectionItems.Any(item => IsDownloadable(item));
                                string bulkDlHint = bulkNeedsDownload ? " ↓" : "";
                                string bulkDlTip = bulkNeedsDownload ? "Some packages will be downloaded first." : null;
                                if (!searchMode && !string.IsNullOrEmpty(_importFolder))
                                {
                                    if (inProject < SGrid.selectionCount)
                                    {
                                        UIBlock("asset.bulk.actions.import", () =>
                                        {
                                            string command = "Import";
                                            if (inProject > 0) command += $" {SGrid.selectionCount - inProject} Remaining";

                                            GUILabelWithText("Import To", _importFolder, 95, null, true);
                                            EditorGUILayout.Space();
                                            if (GUILayout.Button(CommonUIStyles.Content($"{command} Files{bulkDlHint}", bulkDlTip), CommonUIStyles.mainButton)) ImportBulkFiles(SGrid.selectionItems);
                                        });
                                    }
                                }

                                if (!searchMode)
                                {
                                    UIBlock("asset.bulk.actions.open", () =>
                                    {
                                        if (GUILayout.Button(CommonUIStyles.Content($"Open{bulkDlHint}", bulkDlTip ?? "Open the files with the assigned system application")))
                                        {
                                            bool show = true;
                                            if (SGrid.selectionItems.Count > AI.Config.massOpenWarnThreshold)
                                            {
                                                show = EditorUtility.DisplayDialog("Open Files", $"You are about to open {SGrid.selectionItems.Count} files. This may take a while and will open a lot of windows.\n\nDo you want to continue?", "Continue", "Cancel");
                                            }
                                            if (show) SGrid.selectionItems.ForEach(Open);
                                        }
                                    });
                                    UIBlock("asset.bulk.actions.openexplorer", () =>
                                    {
                                        string explorerLabel = (Application.platform == RuntimePlatform.OSXEditor ? "Show in Finder" : "Show in Explorer") + bulkDlHint;
                                        if (GUILayout.Button(CommonUIStyles.Content(explorerLabel, bulkDlTip)))
                                        {
                                            bool show = true;
                                            if (SGrid.selectionItems.Count > AI.Config.massOpenWarnThreshold)
                                            {
                                                show = EditorUtility.DisplayDialog("Show Files", $"You are about to open {SGrid.selectionItems.Count} locations. This may take a while and will open a lot of windows.\n\nDo you want to continue?", "Continue", "Cancel");
                                            }
                                            if (show) SGrid.selectionItems.ForEach(OpenExplorer);
                                        }
                                    });
                                    UIBlock("asset.bulk.actions.recreatepreviews", () =>
                                    {
                                        EditorGUI.BeginDisabledGroup(_blockingInProgress);
                                        if (GUILayout.Button("Recreate Previews")) RecreatePreviews(SGrid.selectionItems);
                                        EditorGUI.EndDisabledGroup();
                                    });
                                    UIBlock("asset.bulk.actions.recreateaicaptions", () =>
                                    {
                                        if (AI.Actions.CreateAICaptions && (ShowAdvanced() || _assetFileAICaptionCount > 0))
                                        {
                                            EditorGUILayout.BeginHorizontal();
                                            string captionButtonText = _assetFileAICaptionCount == 0 ? "Create AI Captions" : "Recreate AI Captions";
                                            if (ShowAdvanced() && GUILayout.Button(captionButtonText))
                                            {
                                                RecreateAICaptions(SGrid.selectionItems);
                                            }
                                            EditorGUI.BeginDisabledGroup(_assetFileAICaptionCount == 0);
                                            if (GUILayout.Button(EditorGUIUtility.IconContent("TreeEditor.Trash", "|Remove AI Captions"), GUILayout.Width(30)))
                                            {
                                                SGrid.selectionItems.ForEach(info => AI.SetAICaption(info, null));
                                                _requireSearchUpdate = true;
                                            }
                                            EditorGUI.EndDisabledGroup();
                                            EditorGUILayout.EndHorizontal();
                                        }
                                    });

#if USE_ASSET_MANAGER && USE_CLOUD_IDENTITY
                                    if (AI.Actions.IndexAssetManager)
                                    {
                                        EditorGUI.BeginDisabledGroup(CloudAssetManagement.IsBusy);
                                        EditorGUILayout.Space();
                                        if (_assetFileAMProjectCount + _assetFileAMCollectionCount > 0)
                                        {
                                            if (_assetFileAMProjectCount > 0)
                                            {
                                                if (GUILayout.Button(CommonUIStyles.Content("Delete from Project", "Delete the files from the Asset Manager project.")))
                                                {
                                                    DeleteAssetsFromProject(SGrid.selectionItems);
                                                }
                                            }
                                            if (_assetFileAMCollectionCount > 0)
                                            {
                                                if (GUILayout.Button(CommonUIStyles.Content("Remove from Collection", "Remove the files from the Asset Manager collection.")))
                                                {
                                                    RemoveAssetsFromCollection(SGrid.selectionItems);
                                                }
                                            }
                                        }
                                        else
                                        {
                                            if (GUILayout.Button("Upload to Asset Manager..."))
                                            {
                                                ProjectSelectionUI projectUI = new ProjectSelectionUI();
                                                projectUI.Init(project =>
                                                {
                                                    AddAssetsToProject(project, SGrid.selectionItems);
                                                });
                                                projectUI.SetAssets(_assets);
                                                PopupWindow.Show(GetPopupPositionAtMouse(), projectUI);
                                            }
                                        }
                                        EditorGUI.EndDisabledGroup();
                                    }
#endif
                                    if (inProject > 0)
                                    {
                                        UIBlock("asset.bulk.actions.remove", () =>
                                        {
                                            string label = inProject == 1 ? "Remove 1 File from Project..." : $"Remove {inProject:N0} Files from Project...";
                                            if (GUILayout.Button(CommonUIStyles.Content(label, "Remove selected files and their dependencies from the current project.")))
                                            {
                                                List<AssetInfo> inProjectFiles = SGrid.selectionItems.Where(item => item.InProject).ToList();
                                                UninstallPackageUI.ShowWindow().Init(inProjectFiles);
                                            }
                                        });
                                    }
                                    UIBlock("asset.bulk.actions.export", () =>
                                    {
                                        if (GUILayout.Button("Export Files..."))
                                        {
                                            ExportUI exportUI = ExportUI.ShowWindow();
                                            exportUI.Init(SGrid.selectionItems, true, 2);
                                        }
                                    });
                                    UIBlock("asset.bulk.actions.delete", () =>
                                    {
                                        EditorGUILayout.Space();
                                        if (SGrid.selectionItems.Any(s => !s.Hidden))
                                        {
                                            if (GUILayout.Button(CommonUIStyles.Content("Hide from Results", "Will hide the selected files from search results. They can be shown again using the Hidden Files filter.")))
                                            {
                                                Assets.SetFilesHidden(SGrid.selectionItems.Select(s => s.Id).ToList(), true);
                                                _requireSearchUpdate = true;
                                            }
                                        }
                                        if (SGrid.selectionItems.Any(s => s.Hidden))
                                        {
                                            if (GUILayout.Button(CommonUIStyles.Content("Unhide", "Will make the selected files visible in search results again.")))
                                            {
                                                Assets.SetFilesHidden(SGrid.selectionItems.Select(s => s.Id).ToList(), false);
                                                _requireSearchUpdate = true;
                                            }
                                        }
                                    });
                                }
                                EditorGUI.EndDisabledGroup();
                                if (_blockingInProgress) EditorGUILayout.LabelField("Operation in progress...", CommonUIStyles.centeredWhiteMiniLabel);

                                if (!SGrid.selectionItems.All(s => s.IsVirtual))
                                {
                                UIBlock("asset.bulk.actions.tag", () =>
                                {
                                    // tags
                                    DrawAddFileTag(SGrid.selectionItems);

                                    float x = 0f;
                                    List<string> toRemove = new List<string>();
                                    foreach (KeyValuePair<string, Tuple<int, Color>> bulkTag in _assetFileBulkTags)
                                    {
                                        string tagName = $"{bulkTag.Key} ({bulkTag.Value.Item1})";
                                        x = CalcTagSize(x, tagName);
                                        UIStyles.DrawTag(tagName, bulkTag.Value.Item2, () =>
                                        {
                                            Tagging.RemoveAssetAssignments(SGrid.selectionItems, bulkTag.Key, true);
                                            toRemove.Add(bulkTag.Key);
                                        }, UIStyles.TagStyle.Remove, GetMaxDetailTagWidth());
                                    }
                                    toRemove.ForEach(key => _assetFileBulkTags.Remove(key));
                                    GUILayout.EndHorizontal();
                                });
                                }

                            }
                            break;

                        case 1:
                            EditorGUI.BeginDisabledGroup(_inMemoryMode != InMemoryModeState.None);
                            EditorGUILayout.Space();

                            bool projectOnlyScope = SearchScopeModel.IsProjectOnly(GetConfiguredSearchScope());
                            if (projectOnlyScope)
                            {
                                EditorGUILayout.HelpBox("Package and store metadata filters are not available in Project scope.", MessageType.Info);
                            }

                            EditorGUI.BeginChangeCheck();

                            // Category B filters: disabled in project scope (require index data)
                            EditorGUI.BeginDisabledGroup(projectOnlyScope);

                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField("Package Tag", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                            _selectedPackageTag = SearchablePopup.PopupField(_selectedPackageTag, _tagPopupItems, "searchFilter_pkgTag", AI.Config.colorTagFilterClosedField, false, true, GUILayout.ExpandWidth(true));
                            GUILayout.EndHorizontal();

                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField("File Tag", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                            _selectedFileTag = SearchablePopup.PopupField(_selectedFileTag, _tagPopupItems, "searchFilter_fileTag", AI.Config.colorTagFilterClosedField, false, true, GUILayout.ExpandWidth(true));
                            GUILayout.EndHorizontal();

                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField("Package", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                            _selectedAsset = SearchablePopup.PopupField(_selectedAsset, _assetNames, "searchFilter_package", false, false, GUILayout.ExpandWidth(true), GUILayout.MinWidth(150));
                            GUILayout.EndHorizontal();

                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField("Publisher", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                            _selectedPublisher = SearchablePopup.PopupField(_selectedPublisher, _publisherNames, "searchFilter_publisher", false, false, GUILayout.ExpandWidth(true), GUILayout.MinWidth(150));
                            GUILayout.EndHorizontal();

                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField("Category", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                            _selectedCategory = SearchablePopup.PopupField(_selectedCategory, _categoryNames, "searchFilter_category", false, false, GUILayout.ExpandWidth(true), GUILayout.MinWidth(150));
                            GUILayout.EndHorizontal();

                            EditorGUI.EndDisabledGroup(); // projectOnlyScope (Category B top)

                            // Category A filters: always available (extractable from project assets)

                            if (IsFilterApplicable("ImageType"))
                            {
                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField("Image Type", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                                _selectedImageType = EditorGUILayout.Popup(_selectedImageType, _imageTypeOptions, GUILayout.ExpandWidth(true));
                                GUILayout.EndHorizontal();
                            }

                            if (IsFilterApplicable("Width"))
                            {
                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField("Width", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                                if (GUILayout.Button(_checkMaxWidth ? "<=" : ">=", GUILayout.Width(25))) _checkMaxWidth = !_checkMaxWidth;
                                _searchWidth = EditorGUILayout.DelayedTextField(_searchWidth, GUILayout.Width(58));
                                EditorGUILayout.LabelField("px", EditorStyles.miniLabel, GUILayout.Width(50));
                                GUILayout.EndHorizontal();
                            }

                            if (IsFilterApplicable("Height"))
                            {
                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField("Height", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                                if (GUILayout.Button(_checkMaxHeight ? "<=" : ">=", GUILayout.Width(25))) _checkMaxHeight = !_checkMaxHeight;
                                _searchHeight = EditorGUILayout.DelayedTextField(_searchHeight, GUILayout.Width(58));
                                EditorGUILayout.LabelField("px", EditorStyles.miniLabel, GUILayout.Width(50));
                                GUILayout.EndHorizontal();
                            }

                            if (IsFilterApplicable("Length"))
                            {
                                // Check if we're searching for FBX/Models/Animations where Length = animation count
                                string rawType = GetRawSearchType();
                                bool isFBXContext = rawType == "Models" || rawType == "Animations" || rawType == "Models/fbx";

                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField(isFBXContext ? "Animations" : "Length", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                                if (GUILayout.Button(_checkMaxLength ? "<=" : ">=", GUILayout.Width(25))) _checkMaxLength = !_checkMaxLength;
                                _searchLength = EditorGUILayout.DelayedTextField(_searchLength, GUILayout.Width(58));
                                EditorGUILayout.LabelField(isFBXContext ? "" : "sec", EditorStyles.miniLabel, GUILayout.Width(50));
                                GUILayout.EndHorizontal();
                            }

                            if (IsFilterApplicable("VertexCount"))
                            {
                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField(CommonUIStyles.Content("Vertices", "Number of vertices in the model"), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                                if (GUILayout.Button(_checkMaxVertexCount ? "<=" : ">=", GUILayout.Width(25))) _checkMaxVertexCount = !_checkMaxVertexCount;
                                _searchVertexCount = EditorGUILayout.DelayedTextField(_searchVertexCount, GUILayout.Width(58));
                                EditorGUILayout.LabelField("", EditorStyles.miniLabel, GUILayout.Width(50));
                                GUILayout.EndHorizontal();
                            }

                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("File Size", "File size in kilobytes"), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                            if (GUILayout.Button(_checkMaxSize ? "<=" : ">=", GUILayout.Width(25))) _checkMaxSize = !_checkMaxSize;
                            _searchSize = EditorGUILayout.DelayedTextField(_searchSize, GUILayout.Width(58));
                            EditorGUILayout.LabelField("kb", EditorStyles.miniLabel, GUILayout.Width(50));
                            GUILayout.EndHorizontal();

                            // Category B filters: disabled in project scope (require index data)
                            EditorGUI.BeginDisabledGroup(projectOnlyScope);

                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField("Price", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
                            _selectedPriceOption = EditorGUILayout.Popup(_selectedPriceOption, _priceOptions, GUILayout.Width(labelWidth + 2));
                            if (_selectedPriceOption == 4 || _selectedPriceOption == 5)
                            {
                                _searchPrice = EditorGUILayout.DelayedFloatField(_searchPrice, GUILayout.Width(58));
                                string currencySymbol = AI.Config.currency == 0 ? "€" : (AI.Config.currency == 1 ? "$" : "¥");
                                EditorGUILayout.LabelField(currencySymbol, EditorStyles.miniLabel, GUILayout.Width(20));
                            }
                            GUILayout.EndHorizontal();

                            if (AI.Actions.ExtractColors)
                            {
                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField("Color", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                                _selectedColorOption = EditorGUILayout.Popup(_selectedColorOption, _colorOptions, GUILayout.Width(labelWidth + 2));
                                if (_selectedColorOption > 0) _selectedColor = EditorGUILayout.ColorField(_selectedColor);
                                GUILayout.EndHorizontal();
                            }

                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField("Packages", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                            _selectedPackageTypes = EditorGUILayout.Popup(_selectedPackageTypes, _packageListingOptions, GUILayout.ExpandWidth(true));
                            GUILayout.EndHorizontal();

                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField("SRPs", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
                            _selectedPackageSRPs = EditorGUILayout.Popup(_selectedPackageSRPs, _srpOptions, GUILayout.ExpandWidth(true));
                            GUILayout.EndHorizontal();

                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField("Hidden Files", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
                            _selectedHiddenFilter = EditorGUILayout.Popup(_selectedHiddenFilter, _hiddenFilterOptions, GUILayout.ExpandWidth(true));
                            GUILayout.EndHorizontal();

                            EditorGUI.EndDisabledGroup(); // projectOnlyScope (Category B bottom)

                            if (EditorGUI.EndChangeCheck())
                            {
                                dirty = true;
                                // Clear active saved search when filters change
                                _activeSavedSearchId = -1;
                            }

                            EditorGUILayout.Space();
                            bool filterActive = IsSearchFilterActive();
                            using (new EditorGUI.DisabledScope(!filterActive))
                            {
                                if (GUILayout.Button("Reset Filters", CommonUIStyles.mainButton) && filterActive)
                                {
                                    ResetSearch(true, false);
                                    _requireSearchUpdate = true;
                                }
                            }

                            EditorGUI.EndDisabledGroup(); // inmemory
                            break;

                    }
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndScrollView();
                    GUILayout.EndVertical();
                    if (searchMode)
                    {
                        if (_selectedEntry == null)
                        {
                            EditorGUILayout.HelpBox("Select an item from the search results.", MessageType.Info);
                        }
                        else
                        {
                            if (!_selectedEntry.InProject && string.IsNullOrEmpty(_importFolder))
                            {
                                EditorGUILayout.HelpBox("Select a folder in the Project View to import to", MessageType.Warning);
                            }
                            else
                            {
                                if (GUILayout.Button("Select", GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT))) ExecuteSingleAction();
                            }
                        }
                    }
                    else
                    {
                        if (!ShowAdvanced() && AI.Config.showHints)
                        {
                            GUILayout.FlexibleSpace();
                            EditorGUILayout.LabelField("Hold down CTRL for additional options.", EditorStyles.centeredGreyMiniLabel);
                        }
                    }

                    GUILayout.EndVertical();
                }
                GUILayout.EndHorizontal();

                HandleKeyboardPagination();
                SGrid.HandleKeyboardCommands();
                HandleTagShortcuts();

                if (dirty)
                {
                    _requireSearchUpdate = true;
                    _keepSearchResultPage = false;
                }
                EditorGUIUtility.labelWidth = 0;
            }
        }

        private bool ShowWorkspaces()
        {
            return (workspaceMode || AI.Config.alwaysShowWorkspaces);
        }

        private void ShowInMemoryButton()
        {
            if (SearchScopeModel.IsProjectOnly(GetConfiguredSearchScope())) return; // not applicable in Project mode

            UIBlock("asset.actions.inmemorymode", () =>
            {
                EditorGUI.BeginDisabledGroup(_resultCount <= 0);
                EditorGUI.BeginChangeCheck();
                bool inMemory = _inMemoryMode != InMemoryModeState.None;
                inMemory = GUILayout.Toggle(inMemory, EditorGUIUtility.IconContent("d_lighting", "|High-Speed Mode: Load all current results into memory for extremely fast sub-searches."), EditorStyles.miniButton, GUILayout.Width(28), GUILayout.ExpandHeight(true));
                if (EditorGUI.EndChangeCheck())
                {
                    _inMemoryMode = inMemory ? InMemoryModeState.Init : InMemoryModeState.None;
                    _requireSearchUpdate = true;
                    _searchPhraseInMemory = "";

                    RefreshSearchField();
                }
                EditorGUI.EndDisabledGroup();
                GUILayout.Space(2);
            });
        }

        private static void RefreshSearchField()
        {
            // force IMGUI to drop its TextEditor cache
            GUIUtility.keyboardControl = 0;
            GUIUtility.hotControl = 0;
            EditorGUIUtility.editingTextField = false;
        }

        private bool SearchWithoutInput()
        {
            return workspaceMode ? AI.Config.wsSearchWithoutInput : AI.Config.searchWithoutInput;
        }

        private void HandleSearchSelectionChanged()
        {
            if (AI.DEBUG_MODE) Debug.LogWarning("HandleSearchSelectionChanged");

            _requireSearchSelectionUpdate = false;
            _selectionHandlerAdded = false;
            EditorApplication.delayCall -= HandleSearchSelectionChanged;

            AudioManager.StopAudio();
            DisposeAnimTexture();
            bool isAudio = AI.IsFileType(_selectedEntry?.Path, AI.AssetGroup.Audio);
            if (_selectedEntry != null)
            {
                _selectedEntry.Refresh();
                Assets.ResolveChildren(_selectedEntry, _assets);
                AI.GetObserver().SetPrioritized(new List<AssetInfo> {_selectedEntry});
                _selectedEntry.PackageDownloader?.RefreshState();

                _selectedEntry.CheckIfInProject();
                _selectedEntry.IsMaterialized = _selectedEntry.IsVirtual || Assets.IsMaterialized(_selectedEntry.ToAsset(), _selectedEntry);
                if (_selectedEntry.IsVirtual && _selectedEntry.Size == 0 && !string.IsNullOrEmpty(_selectedEntry.ProjectPath))
                {
                    FileInfo fi = new FileInfo(_selectedEntry.ProjectPath);
                    if (fi.Exists) _selectedEntry.Size = fi.Length;
                }
                if (!_selectedEntry.IsVirtual) _ = AssetUtils.LoadPackageTexture(_selectedEntry);

                // Stop all visible animations when selecting a single item, restoring their static previews
                DisposeAllVisibleAnimations(true);
                LoadAnimTexture(_selectedEntry);

                CalcDependenciesOnDemand(_selectedEntry);
                RecreatePreviewEditor();

                if (!_searchDone && AI.Config.pingSelected && _selectedEntry.InProject) PingAsset(_selectedEntry);
            }
            _searchDone = false;

            if (_searchSelectionChangedManually)
            {
                _searchSelectionChangedManually = false;
                _searchInspectorTab = 0;
                if (instantSelection)
                {
                    ExecuteSingleAction();
                }
                else if (AI.Config.autoPlayAudio && isAudio) PlayAudio(_selectedEntry);
            }
        }

        private void CalcDependenciesOnDemand(AssetInfo entry)
        {
            if (entry.IsVirtual)
            {
                if (entry.DependencyState == AssetInfo.DependencyStateOptions.Unknown && !string.IsNullOrEmpty(entry.ProjectPath))
                {
                    List<AssetFile> deps = ProjectDependencyAnalysis.GetDependencies(entry.ProjectPath);
                    entry.Dependencies = deps;
                    entry.MediaDependencies = deps;
                    entry.ScriptDependencies = new List<AssetFile>();
                    entry.CrossPackageDependencies = new List<Asset>();
                    entry.DependencyState = AssetInfo.DependencyStateOptions.Done;
                }
                return;
            }

            if (AI.Config.autoCalculateDependencies == 2)
            {
                // if entry is already materialized calculate dependencies immediately
                if ((entry.DependencyState == AssetInfo.DependencyStateOptions.Unknown || entry.DependencyState == AssetInfo.DependencyStateOptions.Partial) &&
                    entry.IsMaterialized &&
                    DependencyAnalysis.NeedsScan(entry.Type))
                {
                    // must run in same thread
                    _ = CalculateDependencies(entry);
                }
            }
        }

        private bool IsSearchFilterActive()
        {
            return IsProjectFilterActive() || IsIndexOnlyFilterActive();
        }

        private bool IsProjectFilterActive()
        {
            return _selectedImageType > 0
                || !string.IsNullOrEmpty(_searchWidth)
                || !string.IsNullOrEmpty(_searchHeight)
                || !string.IsNullOrEmpty(_searchLength)
                || !string.IsNullOrEmpty(_searchSize)
                || !string.IsNullOrEmpty(_searchVertexCount);
        }

        private bool IsIndexOnlyFilterActive()
        {
            return _selectedPackageTag > 0
                || _selectedFileTag > 0
                || _selectedAsset > 0
                || _selectedPublisher > 0
                || _selectedCategory > 0
                || _selectedColorOption > 0
                || _selectedPackageTypes != 1
                || _selectedPackageSRPs != 1
                || _selectedPriceOption > 0
                || _selectedHiddenFilter > 0;
        }

        private ProjectAssetSearch.Options CreateProjectSearchOptions(bool ignoreExcludedExtensions)
        {
            return new ProjectAssetSearch.Options
            {
                SearchPhrase = _searchPhrase,
                RawSearchType = GetRawSearchType(),
                IgnoreExcludedExtensions = ignoreExcludedExtensions,
                SearchWidth = _searchWidth,
                CheckMaxWidth = _checkMaxWidth,
                SearchHeight = _searchHeight,
                CheckMaxHeight = _checkMaxHeight,
                SearchLength = _searchLength,
                CheckMaxLength = _checkMaxLength,
                SearchSize = _searchSize,
                CheckMaxSize = _checkMaxSize,
                SearchVertexCount = _searchVertexCount,
                CheckMaxVertexCount = _checkMaxVertexCount,
                SelectedImageType = _selectedImageType,
                ImageTypeOptions = _imageTypeOptions,
                MaxResults = AI.Config.maxProjectSearchResults > 0 ? AI.Config.maxProjectSearchResults : 0,
                SortField = AI.Config.sortField,
                SortDescending = AI.Config.sortDescending
            };
        }

        private string GetProjectSearchCacheKey(bool ignoreExcludedExtensions)
        {
            return $"{_searchPhrase}|{GetRawSearchType()}|{_searchWidth}|{_checkMaxWidth}|{_searchHeight}|{_checkMaxHeight}|{_searchLength}|{_checkMaxLength}|{_searchSize}|{_checkMaxSize}|{_searchVertexCount}|{_checkMaxVertexCount}|{_selectedImageType}|{AI.Config.maxProjectSearchResults}|{AI.Config.sortField}|{AI.Config.sortDescending}|{ignoreExcludedExtensions}|{AI.Config.excludeExtensions}|{AI.Config.searchType}|{AI.Config.excludedExtensions}";
        }

        private async Task<bool> EnsureDownloaded(AssetInfo info)
        {
            if (info.IsDownloaded) return true;

            AssetInfo downloadTarget = info.ParentInfo ?? info;
            if (downloadTarget.IsAbandoned)
            {
                Debug.LogError($"Cannot download '{downloadTarget.GetDisplayName()}' as it is an abandoned package.");
                return false;
            }

            if (downloadTarget.PackageDownloader == null)
            {
                downloadTarget.PackageDownloader = new AssetDownloader(downloadTarget);
            }
            if (!downloadTarget.PackageDownloader.IsDownloadSupported()) return false;

            AI.GetObserver().Attach(downloadTarget);
            _curOperation = $"Downloading {downloadTarget.GetDisplayName()}...";
            downloadTarget.PackageDownloader.Download(true);
            do
            {
                await Task.Delay(200);
                downloadTarget.PackageDownloader.RefreshState();
                float progress = downloadTarget.PackageDownloader.GetState().progress * 100f;
                _curOperation = $"Downloading {downloadTarget.GetDisplayName()}: {progress:N0}%...";
            } while (downloadTarget.IsDownloading());
            await Task.Delay(3000); // ensure all file operations have finished
            downloadTarget.Refresh();
            _curOperation = null;

            return downloadTarget.IsDownloaded;
        }

        private bool IsDownloadable(AssetInfo info)
        {
            if (info.IsDownloaded) return false;
            if (info.IsAbandoned) return false;
            AssetInfo downloadTarget = info.ParentInfo ?? info;
            return downloadTarget.AssetSource == Asset.Source.AssetStorePackage;
        }

        private async void ImportBulkFiles(List<AssetInfo> items)
        {
            _blockingInProgress = true;
            foreach (AssetInfo info in items)
            {
                if (!info.IsDownloaded)
                {
                    if (!await EnsureDownloaded(info))
                    {
                        Debug.LogWarning($"Skipping import of '{info.GetDisplayName()}': download failed.");
                        continue;
                    }
                }
                // must be done consecutively to avoid IO conflicts
                await Assets.CopyTo(info, _importFolder, true);
            }
            _blockingInProgress = false;
        }

        private void DrawAddFileTag(List<AssetInfo> assets)
        {
            assets = assets.Where(a => a != null && !a.IsVirtual).ToList();
            if (assets.Count == 0) return;

            EditorGUILayout.Space();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(CommonUIStyles.Content("Add Tag..."), GUILayout.Width(70)))
            {
                TagSelectionUI tagUI = new TagSelectionUI();
                tagUI.Init(TagAssignment.Target.Asset, CalculateSearchBulkSelection);
                tagUI.SetAssets(assets);
                PopupWindow.Show(GetPopupPositionAtMouse(), tagUI);
            }
            GUILayout.Space(15);
        }

        private async void ExecuteSingleAction()
        {
            if (_selectedEntry == null) return;
            if (!_selectedEntry.InProject && string.IsNullOrEmpty(_importFolder))
            {
                EditorUtility.DisplayDialog("Missing Target", "Select a target folder in the Project View first to proceed.", "OK");
                return;
            }

            List<AssetInfo> files = new List<AssetInfo>();
            Dictionary<string, AssetInfo> identifiedTextures = null;
            if (textureMode)
            {
                identifiedTextures = IdentifyTextures(_selectedEntry);
                files.AddRange(identifiedTextures.Values); // TODO: one file will be duplicate, not an issue but will save time to eliminate it
            }
            else
            {
                files.Add(_selectedEntry);
            }

            foreach (AssetInfo info in files)
            {
                info.CheckIfInProject();
                if (!info.InProject)
                {
                    _blockingInProgress = true;
                    _lockSelection = true;

                    // download on-demand
                    if (!info.IsDownloaded)
                    {
                        if (!await EnsureDownloaded(info))
                        {
                            _lockSelection = false;
                            return;
                        }
                    }

                    _curOperation = $"Extracting & Importing '{info.FileName}'...";
                    await Assets.CopyTo(info, _importFolder, true);
                    _blockingInProgress = false;

                    if (!info.InProject)
                    {
                        Debug.LogError("The file could not be materialized into the project.");
                        _lockSelection = false;
                        return;
                    }
                }
            }

            Close();
            AudioManager.StopAudio();

            if (textureMode)
            {
                Dictionary<string, string> result = new Dictionary<string, string>();
                foreach (KeyValuePair<string, AssetInfo> file in identifiedTextures)
                {
                    result.Add(file.Key, file.Value.ProjectPath);
                }
                searchModeTextureCallback?.Invoke(result);
            }
            else
            {
                searchModeCallback?.Invoke(_selectedEntry.ProjectPath);
            }
            _lockSelection = false;
        }

        private Dictionary<string, AssetInfo> IdentifyTextures(AssetInfo info)
        {
            TextureNameSuggester tns = new TextureNameSuggester();
            Dictionary<string, string> files = tns.SuggestFileNames(info.Path, path =>
            {
                string sep = info.Path.Contains("/") ? "/" : "\\";
                string toCheck = info.Path.Substring(0, info.Path.LastIndexOf(sep) + 1) + Path.GetFileName(path);
                AssetInfo ai = Assets.GetAssetByPath(toCheck, info.ToAsset());
                return ai?.Path; // capitalization could be different from actual validation request, so use result
            });

            Dictionary<string, AssetInfo> result = new Dictionary<string, AssetInfo>();
            foreach (KeyValuePair<string, string> file in files)
            {
                AssetInfo ai = Assets.GetAssetByPath(file.Value, info.ToAsset());
                if (ai != null) result.Add(file.Key, ai);
            }
            return result;
        }

        private void DeleteFromIndex(AssetInfo info)
        {
            Assets.ForgetAssetFile(info);
            _requireSearchUpdate = true;
        }

        private async void RecreatePreviews(List<AssetInfo> infos)
        {
            _blockingInProgress = true;

            await AI.Actions.RunWithProgress<PreviewPipeline>(
                ActionHandler.ACTION_PREVIEWS_RECREATE,
                "Recreating previews",
                async imp =>
                {
                    if (await imp.RecreatePreviews(infos, false, null, false, req =>
                        {
                            if (infos.Count > 1) return;
                            if (req == null)
                            {
                                EditorUtility.DisplayDialog("Error", "Preview could not be created.", "OK");
                            }
                            else if (req.IncompatiblePipeline)
                            {
                                string message = string.IsNullOrWhiteSpace(req.FailureReason)
                                    ? "Preview could not be created. The item is incompatible to the currently used render pipeline."
                                    : $"Preview could not be created.\n\nCause: {req.FailureReason}";
                                EditorUtility.DisplayDialog("Pipeline Error", message, "OK");
                            }
                            else if (!string.IsNullOrWhiteSpace(req.FailureReason))
                            {
                                EditorUtility.DisplayDialog("Preview Recreation Failed", $"Preview could not be created.\n\nCause: {req.FailureReason}", "OK");
                            }
                        }) > 0) _requireSearchUpdate = true;
                });

            _blockingInProgress = false;
        }

        private async void RecreateAICaptions(List<AssetInfo> infos)
        {
            _blockingInProgress = true;

            await AI.Actions.RunWithProgress<CaptionCreator>(
                ActionHandler.ACTION_AI_CAPTIONS,
                "Creating selective AI captions",
                imp => imp.Run(infos));

            _requireSearchUpdate = true;
            _blockingInProgress = false;
        }

        private void LoadSearch(SavedSearch search)
        {
            _searchPhrase = search.SearchPhrase;
            _previousSearchPhrase = search.SearchPhrase;
            _selectedPackageTypes = search.PackageTypes;
            _selectedPackageSRPs = search.PackageSrPs;
            _selectedPriceOption = search.PriceOption;
            _searchPrice = search.Price;
            _selectedImageType = search.ImageType;
            _selectedColorOption = search.ColorOption;
            _selectedHiddenFilter = search.Hidden;
            _selectedColor = ImageUtils.FromHex(search.SearchColor);
            _searchWidth = search.Width;
            _searchHeight = search.Height;
            _searchLength = search.Length;
            _searchSize = search.Size;
            _checkMaxWidth = search.CheckMaxWidth;
            _checkMaxHeight = search.CheckMaxHeight;
            _checkMaxLength = search.CheckMaxLength;
            _checkMaxSize = search.CheckMaxSize;
            _searchVertexCount = search.VertexCount;
            _checkMaxVertexCount = search.CheckMaxVertexCount;

            // Restore dropdowns (match by ID if brackets exist, otherwise by string)
            AI.Config.searchType = string.IsNullOrWhiteSpace(search.Type) ? 0 : Mathf.Max(0, Array.FindIndex(_types, s => s.Split('/').LastOrDefault() == search.Type));
            _selectedPublisher = FindIndexByValue(_publisherNames, search.Publisher, splitPath: true);
            _selectedAsset = FindIndexByValue(_assetNames, search.Package, splitPath: true);
            _selectedCategory = FindIndexByValue(_categoryNames, search.Category, splitPath: false);
            _selectedPackageTag = FindIndexByValue(_tagNames, search.PackageTag, splitPath: false);
            _selectedFileTag = FindIndexByValue(_tagNames, search.FileTag, splitPath: false);

            // Load variable definitions
            if (!string.IsNullOrEmpty(search.VariableDefinitions))
            {
                _searchVariables = DeserializeSearchVariables(search.VariableDefinitions);
            }

            // Always detect variables from the search phrase to ensure UI renders correctly
            // This also handles the case where the phrase has variables but no stored definitions
            DetectVariablesInSearchPhrase();

            _activeSavedSearchId = search.Id;
            _variablesRestoredFromDb = true;
            _requireSearchUpdate = true;
            RefreshSearchField();
        }

        private void PopulateSavedSearchFromCurrentState(SavedSearch search)
        {
            search.SearchPhrase = _searchPhrase;
            search.PackageTypes = _selectedPackageTypes;
            search.PackageSrPs = _selectedPackageSRPs;
            search.PriceOption = _selectedPriceOption;
            search.Price = _searchPrice;
            search.ImageType = _selectedImageType;
            search.ColorOption = _selectedColorOption;
            search.SearchColor = "#" + ColorUtility.ToHtmlStringRGB(_selectedColor);
            search.Width = _searchWidth;
            search.Height = _searchHeight;
            search.Length = _searchLength;
            search.Size = _searchSize;
            search.CheckMaxWidth = _checkMaxWidth;
            search.CheckMaxHeight = _checkMaxHeight;
            search.CheckMaxLength = _checkMaxLength;
            search.CheckMaxSize = _checkMaxSize;
            search.VertexCount = _searchVertexCount;
            search.CheckMaxVertexCount = _checkMaxVertexCount;
            search.Hidden = _selectedHiddenFilter;

            // Store type (extract last component as full types don't have IDs)
            if (AI.Config.searchType > 0 && _types.Length > AI.Config.searchType)
            {
                search.Type = _types[AI.Config.searchType].Split('/').LastOrDefault();
            }
            else
            {
                search.Type = null;
            }

            // Store full selection strings (will extract IDs during restore if needed)
            search.Publisher = _selectedPublisher > 0 && _publisherNames.Length > _selectedPublisher
                ? _publisherNames[_selectedPublisher].Split('/').LastOrDefault()
                : null;

            search.Package = _selectedAsset > 0 && _assetNames.Length > _selectedAsset
                ? _assetNames[_selectedAsset].Split('/').LastOrDefault()
                : null;

            search.Category = _selectedCategory > 0 && _categoryNames.Length > _selectedCategory
                ? _categoryNames[_selectedCategory]
                : null;

            search.PackageTag = _selectedPackageTag > 0 && _tagNames.Length > _selectedPackageTag
                ? _tagNames[_selectedPackageTag]
                : null;

            search.FileTag = _selectedFileTag > 0 && _tagNames.Length > _selectedFileTag
                ? _tagNames[_selectedFileTag]
                : null;

            // Serialize variable metadata
            search.VariableDefinitions = SerializeSearchVariables(_searchVariables);
        }

        private void SaveSearch(string value)
        {
            SavedSearch search = new SavedSearch();
            search.Name = value;
            search.Color = ColorUtility.ToHtmlStringRGB(Random.ColorHSV());

            PopulateSavedSearchFromCurrentState(search);

            DBAdapter.DB.Insert(search);
            Searches.Add(search);
            _activeSavedSearchId = search.Id;

            // add to current workspace as well
            if (_selectedWorkspace != null)
            {
                WorkspaceSearch wsSearch = new WorkspaceSearch
                {
                    WorkspaceId = _selectedWorkspace.Id,
                    SavedSearchId = search.Id,
                    OrderIdx = _selectedWorkspace.Searches.Count
                };
                DBAdapter.DB.Insert(wsSearch);
                _selectedWorkspace.Searches.Add(wsSearch);
            }
        }

        private void OverrideSavedSearch(SavedSearch search)
        {
            PopulateSavedSearchFromCurrentState(search);
            DBAdapter.DB.Update(search);

            // Set as active search
            _activeSavedSearchId = search.Id;
            _variablesRestoredFromDb = true;
        }

        private void SaveWorkspace(string value)
        {
            Workspace ws = new Workspace();
            ws.Name = value;

            DBAdapter.DB.Insert(ws);
            Workspaces.Add(ws);

            WorkspaceUI workspaceUI = WorkspaceUI.ShowWindow();
            workspaceUI.Init(ws);
        }

        private async void PlayAudio(AssetInfo info)
        {
            // play instantly if no extraction is required
            if (_blockingInProgress)
            {
                if (Assets.IsMaterialized(info.ToAsset(), info)) await Assets.PlayAudio(info);
                return;
            }

            await Assets.PlayAudio(info, InitBlockingToken());
            DisposeBlocking();
        }

        private void OpenAudioEditor(AssetInfo info, string importFolder)
        {
            if (info == null || string.IsNullOrEmpty(importFolder)) return;

            AudioManager.StopAudio();

            // Use the AudioTool package with AssetInfoAudioSource bridge
            // Force CreateCopy mode when embedded in AssetInventory
            AssetInfoAudioSource audioSource = new AssetInfoAudioSource(info);
            AudioEditorUI window = AudioEditorUI.ShowWindow();
            window.Init(audioSource, importFolder);
        }

        private async void PingAsset(AssetInfo info)
        {
            if (disablePings) return;

            string projectPath = info.ProjectPath;
            if (!AssetUtils.IsAssetDatabasePath(projectPath))
            {
                info.ProjectPath = null;
                return;
            }

            // requires pauses in-between to allow editor to catch up
            EditorApplication.ExecuteMenuItem("Window/General/Project");
            await Task.Yield();

            Selection.activeObject = null;
            await Task.Yield();

            Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(projectPath);
            if (Selection.activeObject == null) info.ProjectPath = null; // probably got deleted again

            // For virtual assets, schedule a preview retry since pinging causes Unity to generate previews
            if (info.IsVirtual && _filteredFiles != null)
            {
                int idx = _filteredFiles.IndexOf(info);
                if (idx >= 0)
                {
                    _pendingVirtualPreviews[idx] = info;
                    if (_textureLoading != null)
                    {
                        RetryPendingVirtualPreviews(_textureLoading.Token);
                    }
                }
            }
        }

        private async Task CalculateDependencies(AssetInfo info)
        {
            // If already calculating, don't start another calculation
            if (_dependencyCancellationTokens != null && _dependencyCancellationTokens.ContainsKey(info)) return;

            CancellationTokenSource cts = new CancellationTokenSource();
            if (_dependencyCancellationTokens == null) _dependencyCancellationTokens = new Dictionary<AssetInfo, CancellationTokenSource>();
            _dependencyCancellationTokens[info] = cts;

            try
            {
                await AI.CalculateDependencies(info, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is expected, don't log as error
                // Reset state if canceled
                if (info.DependencyState == AssetInfo.DependencyStateOptions.Calculating)
                {
                    info.DependencyState = AssetInfo.DependencyStateOptions.Unknown;
                }
            }
            finally
            {
                // Clean up the token source
                _dependencyCancellationTokens?.Remove(info);
                cts.Dispose();
            }
        }

        private void CancelDependencyCalculation(AssetInfo info)
        {
            if (_dependencyCancellationTokens == null || info == null) return;

            if (_dependencyCancellationTokens.TryGetValue(info, out CancellationTokenSource cts))
            {
                cts?.Cancel();
                _dependencyCancellationTokens.Remove(info);
                cts?.Dispose();

                // Reset state
                if (info.DependencyState == AssetInfo.DependencyStateOptions.Calculating)
                {
                    info.DependencyState = AssetInfo.DependencyStateOptions.Unknown;
                }
            }
        }

        private void ShowWhereUsed(AssetInfo info)
        {
            string path = info.ProjectPath;
            if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(info.Guid))
            {
                path = AssetDatabase.GUIDToAssetPath(info.Guid);
            }
            if (string.IsNullOrEmpty(path)) return;

            WhereUsedUI window = WhereUsedUI.ShowWindow();
            window.Init(path);
        }

        private void ShowProjectDependencies(AssetInfo info)
        {
            string path = info.ProjectPath;
            if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(info.Guid))
            {
                path = AssetDatabase.GUIDToAssetPath(info.Guid);
            }
            if (string.IsNullOrEmpty(path)) return;

            List<AssetFile> deps = ProjectDependencyAnalysis.GetDependencies(path);
            info.Dependencies = deps;
            info.MediaDependencies = deps;
            info.ScriptDependencies = new List<AssetFile>();
            info.CrossPackageDependencies = new List<Asset>();
            info.SRPSupportPackage = null;
            info.DependencyState = AssetInfo.DependencyStateOptions.Done;

            DependenciesUI depUI = DependenciesUI.ShowWindow();
            depUI.Init(info);
        }

        private async void Open(AssetInfo info)
        {
            if (!info.IsDownloaded && !info.IsMaterialized)
            {
                if (!await EnsureDownloaded(info)) return;
            }

            _blockingInProgress = true;
            string targetPath;
            if (info.InProject)
            {
                targetPath = info.ProjectPath;
            }
            else
            {
                targetPath = await Assets.EnsureMaterialized(info);
                if (info.Id == 0) _requireSearchUpdate = true; // was deleted
            }

            if (targetPath != null) EditorUtility.OpenWithDefaultApp(targetPath);
            _blockingInProgress = false;
        }

        private async void OpenExplorer(AssetInfo info)
        {
            if (!info.IsDownloaded && !info.IsMaterialized)
            {
                if (!await EnsureDownloaded(info)) return;
            }

            _blockingInProgress = true;
            string targetPath;
            if (info.InProject)
            {
                targetPath = info.ProjectPath;
            }
            else
            {
                targetPath = await Assets.EnsureMaterialized(info);
                if (info.Id == 0) _requireSearchUpdate = true; // was deleted
            }

            if (targetPath != null) EditorUtility.RevealInFinder(IOUtils.ToShortPath(targetPath));
            _blockingInProgress = false;
        }

        private async void CopyTo(AssetInfo info, string targetFolder, bool withDependencies = false, int scriptMode = 0, bool autoPing = true, bool fromDragDrop = false, bool reimport = false, bool addToScene = false, Vector3? worldPosition = null, Transform parentTransform = null)
        {
            if (_blockingInProgress) return;

            if (!info.IsDownloaded && !info.IsMaterialized)
            {
                if (!await EnsureDownloaded(info)) return;
            }

            _blockingInProgress = true;

            string mainFile = await Assets.CopyTo(info, targetFolder, withDependencies, scriptMode, fromDragDrop, false, reimport);
            if (mainFile != null)
            {
                if (addToScene && AssetUtils.CanAddToScene(mainFile)) // auto ping would remove selection otherwise
                {
                    if (worldPosition.HasValue)
                    {
                        AssetUtils.AddToScene(mainFile, worldPosition.Value, parentTransform);
                    }
                    else
                    {
                        AssetUtils.AddToScene(mainFile);
                    }
                }
                else
                {
                    if (autoPing && AI.Config.pingImported) PingAsset(new AssetInfo().WithProjectPath(mainFile));
                }
                if (AI.Config.statsImports == 5) ShowInterstitial();
            }

            _blockingInProgress = false;
        }

        private void SetPage(int newPage)
        {
            SetPage(newPage, false);
        }

        private void SetPage(int newPage, bool ignoreExcludedExtensions)
        {
            newPage = Mathf.Clamp(newPage, 1, _pageCount);
            if (newPage != _curPage)
            {
                _curPage = newPage;
                SGrid.DeselectAll();
                _searchScrollPos = Vector2.zero;
                if (_curPage > 0)
                {
                    if (_inMemoryMode == InMemoryModeState.Active)
                    {
                        UpdateFilteredFiles();

                        if (_filteredFiles.Count > 0)
                        {
                            SGrid.LimitSelection(_filteredFiles.Count);
                            _selectedEntry = _filteredFiles[SGrid.selectionTile];
                        }
                        _requireSearchSelectionUpdate = true;
                        StopAnimation();
                    }
                    else
                    {
                        PerformSearch(true, ignoreExcludedExtensions);
                    }
                }
            }
        }

        private void HandleKeyboardPagination()
        {
            if (Event.current == null) return;
            if (Event.current.type != EventType.KeyDown) return;
            if ((Event.current.modifiers & EventModifiers.Alt) == 0) return;
            if (AI.Config.tab != 0) return;
            if (_pageCount <= 1) return;

            bool handled = false;
            if (Event.current.keyCode == KeyCode.LeftArrow && _curPage > 1)
            {
                SetPage(_curPage - 1);
                handled = true;
            }
            else if (Event.current.keyCode == KeyCode.RightArrow && _curPage < _pageCount)
            {
                SetPage(_curPage + 1);
                handled = true;
            }

            if (handled)
            {
                Event.current.Use();
            }
        }

        private void UpdateFilteredFiles()
        {
            StopSearchPreviewLoading();
            ClearFilePreviewCache();

            if (_inMemoryMode != InMemoryModeState.None)
            {
                int maxResults = GetMaxResults();
                _filteredFiles = BuildInMemoryFilteredPage(_files, _curPage, maxResults, out _resultCount);
                _pageCount = AssetUtils.GetPageCount(_resultCount, maxResults);
                if (_curPage > _pageCount)
                {
                    _curPage = 1;
                    _filteredFiles = BuildInMemoryFilteredPage(_files, _curPage, maxResults, out _resultCount);
                }
            }
            else
            {
                _filteredFiles = _files ?? new List<AssetInfo>();
                _pageCount = AssetUtils.GetPageCount(_resultCount, GetMaxResults());
            }

            DisposeSearchResultTextures();
            GUIContent[] gridContents = new GUIContent[_filteredFiles.Count];
            for (int i = 0; i < _filteredFiles.Count; i++)
            {
                gridContents[i] = new GUIContent(GetSearchTileText(_filteredFiles[i]));
            }
            SGrid.contents = gridContents;

            SGrid.enlargeTiles = AI.Config.enlargeTiles;
            SGrid.centerTiles = AI.Config.centerTiles;
            SGrid.Init(_assets, _filteredFiles, CalculateSearchBulkSelection);

            // Update tree model for list view
            UpdateSearchTreeModel();

            UpdateSearchPreviews();
        }

        private List<AssetInfo> BuildInMemoryFilteredPage(List<AssetInfo> source, int currentPage, int maxResults, out int resultCount)
        {
            resultCount = 0;
            if (source == null || source.Count == 0) return new List<AssetInfo>();

            int skip = (Math.Max(1, currentPage) - 1) * maxResults;
            int capacity = Math.Min(maxResults, source.Count);
            List<AssetInfo> result = new List<AssetInfo>(capacity);

            for (int i = 0; i < source.Count; i++)
            {
                AssetInfo file = source[i];
                if (!MatchesInMemorySearch(file, _searchPhraseInMemory)) continue;

                if (resultCount >= skip && result.Count < maxResults)
                {
                    result.Add(file);
                }
                resultCount++;
            }

            return result;
        }

        private static readonly char[] InMemorySearchSeparators = {' '};

        private bool MatchesInMemorySearch(AssetInfo file, string searchPhrase)
        {
            if (file == null) return false;
            if (string.IsNullOrWhiteSpace(searchPhrase)) return true;

            if (searchPhrase.StartsWith("~"))
            {
                string term = searchPhrase.Substring(1);
                return AnyConfiguredSearchFieldContains(file, term);
            }

            string[] fuzzyWords = searchPhrase.Split(InMemorySearchSeparators, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < fuzzyWords.Length; i++)
            {
                string word = fuzzyWords[i].Trim();
                if (string.IsNullOrWhiteSpace(word)) continue;

                bool isNeg = word.StartsWith("-");
                string term = isNeg || word.StartsWith("+") ? word.Substring(1) : word;
                if (string.IsNullOrWhiteSpace(term)) continue;

                if (isNeg)
                {
                    if (!NoConfiguredSearchFieldContains(file, term)) return false;
                }
                else if (!AnyConfiguredSearchFieldContains(file, term))
                {
                    return false;
                }
            }

            return true;
        }

        private bool AnyConfiguredSearchFieldContains(AssetInfo file, string term)
        {
            if (term == null) return false;

            switch (AI.Config.searchField)
            {
                case 0:
                    if (file.Path?.Contains(term, StringComparison.OrdinalIgnoreCase) == true) return true;
                    break;

                case 1:
                    if (file.FileName?.Contains(term, StringComparison.OrdinalIgnoreCase) == true) return true;
                    break;
            }

            if (AI.Config.searchAICaptions && AI.Actions.CreateAICaptions && file.AICaption?.Contains(term, StringComparison.OrdinalIgnoreCase) == true) return true;
            if (AI.Config.searchPackageNames && file.DisplayName?.Contains(term, StringComparison.OrdinalIgnoreCase) == true) return true;

            return false;
        }

        private bool NoConfiguredSearchFieldContains(AssetInfo file, string term)
        {
            return !AnyConfiguredSearchFieldContains(file, term);
        }

        private string GetSearchTileText(AssetInfo file)
        {
            if (file == null) return "";

            string text = "";
            int tileTextToUse = AI.Config.tileText;
            if (tileTextToUse == 6 && string.IsNullOrEmpty(file.AICaption))
            {
                tileTextToUse = 4;
            }
            if (tileTextToUse == 0)
            {
                if (AI.Config.searchTileSize < 70)
                {
                    tileTextToUse = 1;
                }
                else if (AI.Config.searchTileSize < 90)
                {
                    tileTextToUse = 5;
                }
                else if (AI.Config.searchTileSize < 150)
                {
                    tileTextToUse = 4;
                }
                else
                {
                    tileTextToUse = 3;
                }
            }
            switch (tileTextToUse)
            {
                case 3:
                    text = file.ShortPath;
                    break;

                case 4:
                    text = file.FileName;
                    break;

                case 5:
                    text = Path.GetFileNameWithoutExtension(file.FileName);
                    break;

                case 6:
                    text = file.AICaption;
                    break;
            }
            return text == null ? "" : text.Replace('/', Path.DirectorySeparatorChar);
        }

        private void UpdateSearchTreeModel()
        {
            // Create root element
            AssetInfo root = new AssetInfo();
            root.TreeId = -1;
            root.Depth = -1;
            root.TreeName = "Root";

            // Set tree properties for each file
            int id = 0;
            if (_filteredFiles == null) _filteredFiles = new List<AssetInfo>();
            foreach (AssetInfo file in _filteredFiles)
            {
                file.TreeId = id++;
                file.Depth = 0;
                file.TreeName = file.FileName;
            }

            // Create model with root + files
            List<AssetInfo> treeData = new List<AssetInfo>(_filteredFiles.Count + 1) {root};
            treeData.AddRange(_filteredFiles);

            _searchTreeModel = new TreeModel<AssetInfo>(treeData);
            _searchTreeView = null; // Force recreation with new model
        }

        private bool IsFilterApplicable(string filterName)
        {
            return AssetSearch.IsFilterApplicable(filterName, GetRawSearchType());
        }

        private string GetRawSearchType()
        {
            int searchType = _fixedSearchTypeIdx >= 0 ? _fixedSearchTypeIdx : AI.Config.searchType;
            return searchType > 0 && _types.Length > searchType ? _types[searchType] : null;
        }

        private int GetMaxResults()
        {
            // Validate index bounds to prevent IndexOutOfRangeException from corrupted/outdated settings
            if (AI.Config.maxResults < 0 || AI.Config.maxResults >= _resultSizes.Length)
            {
                AI.Config.maxResults = 5; // Default: "100" results
            }

            string selectedSize = _resultSizes[AI.Config.maxResults];
            int.TryParse(selectedSize, out int maxResults);
            if (maxResults <= 0 || maxResults > AI.Config.maxResultsLimit) maxResults = AI.Config.maxResultsLimit;

            return maxResults;
        }

        private void PerformSearch(bool keepPage = false, bool ignoreExcludedExtensions = false)
        {
            if (AI.DEBUG_MODE) Debug.LogWarning("Perform Search");

            // Detect variables immediately before search if detection is pending
            if (_nextVariableDetectionTime > 0)
            {
                _nextVariableDetectionTime = 0;
                DetectVariablesInSearchPhrase();
            }

            _requireSearchUpdate = false;
            _searchHandlerAdded = false;
            _keepSearchResultPage = true;
            StopSearchPreviewLoading();
            StopAnimation();

            // check if something was searched for actually, good for reducing initial load time if user is not interested in seeing full catalog
            if (!SearchWithoutInput())
            {
                if (!IsSearchFilterActive() && string.IsNullOrWhiteSpace(_searchPhrase))
                {
                    _resultCount = 0;
                    _pageCount = 0;
                    _curPage = 1;
                    _filteredFiles = new List<AssetInfo>();
                    SGrid.contents = Array.Empty<GUIContent>();
                    ClearFilePreviewCache();
                    return;
                }
            }

            // use shared AssetSearch to execute search logic once
            int lastCount = _resultCount;
            int maxResults = GetMaxResults();

            // Build variables dictionary for search execution
            Dictionary<string, string> searchVariables = null;
            if (_hasSearchVariables && _searchVariables.Count > 0)
            {
                searchVariables = new Dictionary<string, string>();
                foreach (KeyValuePair<string, SearchVariable> kvp in _searchVariables)
                {
                    searchVariables[kvp.Key] = kvp.Value.currentValue ?? kvp.Value.defaultValue ?? "";
                }
            }

            AssetSearch.Options opt = new AssetSearch.Options
            {
                SearchPhrase = _searchPhrase,
                SearchVariables = searchVariables,
                SelectedPackageSRPs = _selectedPackageSRPs,
                SelectedPriceOption = _selectedPriceOption,
                SearchPrice = _searchPrice,
                SearchWidth = _searchWidth,
                CheckMaxWidth = _checkMaxWidth,
                SearchHeight = _searchHeight,
                CheckMaxHeight = _checkMaxHeight,
                SearchLength = _searchLength,
                CheckMaxLength = _checkMaxLength,
                SearchSize = _searchSize,
                CheckMaxSize = _checkMaxSize,
                SearchVertexCount = _searchVertexCount,
                CheckMaxVertexCount = _checkMaxVertexCount,
                SelectedPackageTag = _selectedPackageTag,
                SelectedFileTag = _selectedFileTag,
                TagNames = _tagNames,
                Tags = _tags,
                SelectedPackageTypes = _selectedPackageTypes,
                SelectedPublisher = _selectedPublisher,
                PublisherNames = _publisherNames,
                SelectedAsset = _selectedAsset,
                AssetNames = _assetNames,
                SelectedCategory = _selectedCategory,
                CategoryNames = _categoryNames,
                SelectedColorOption = _selectedColorOption,
                SelectedColor = _selectedColor,
                SelectedImageType = _selectedImageType,
                ImageTypeOptions = _imageTypeOptions,
                SelectedPreviewFilter = AI.Config.previewVisibility,
                SelectedHiddenFilter = _selectedHiddenFilter,
                RawSearchType = GetRawSearchType(),
                IgnoreExcludedExtensions = ignoreExcludedExtensions,
                CurrentPage = _curPage,
                MaxResults = maxResults,
                InMemory = _inMemoryMode == InMemoryModeState.None ? AssetSearch.InMemoryMode.None : (_inMemoryMode == InMemoryModeState.Init ? AssetSearch.InMemoryMode.Init : AssetSearch.InMemoryMode.Active),
                AllAssets = _assets
            };

            SearchScope searchScope = GetConfiguredSearchScope();

            // Invalidate cached metadata for assets that were reimported
            if (AssetOriginPostprocessor.HasChanges)
            {
                ProjectMetadataCache.Invalidate(AssetOriginPostprocessor.ConsumeChangedGuids());
                _cachedProjectSearchKey = null;
                _cachedProjectFiles = null;
                _cachedProjectOnlyFiles = null;
            }

            if (SearchScopeModel.UsesIndexSearch(searchScope))
            {
                // All/Index: run indexed database search first, then optionally merge project results.
                AssetSearch.Result res = AssetSearch.Execute(opt);
                _searchError = res.Error;
                int indexCount = res.ResultCount;
                _originalResultCount = res.OriginalResultCount;
                _files = res.Files;
                if (_inMemoryMode != InMemoryModeState.None && res.InMemory == AssetSearch.InMemoryMode.None) _inMemoryMode = InMemoryModeState.None;
                if (_inMemoryMode == InMemoryModeState.Init) _inMemoryMode = InMemoryModeState.Active;

                // Skip project search when index-only filters are active that project results cannot satisfy
                bool hasIndexOnlyFilters = IsIndexOnlyFilterActive();
                if (SearchScopeModel.UsesProjectSearch(searchScope) && !hasIndexOnlyFilters)
                {
                    // Build cache key from project search parameters
                    string projectSearchKey = GetProjectSearchCacheKey(ignoreExcludedExtensions);

                    // Reuse cached project results if search parameters haven't changed (e.g. page navigation only)
                    if (_cachedProjectFiles == null || _cachedProjectSearchKey != projectSearchKey)
                    {
                        // Project search
                        ProjectAssetSearch.Options projOpt = CreateProjectSearchOptions(ignoreExcludedExtensions);
                        ProjectAssetSearch.Result projRes = ProjectAssetSearch.Execute(projOpt);

                        _cachedProjectFiles = projRes.Files;
                        _cachedProjectOnlyFiles = null;
                        _cachedProjectSearchKey = projectSearchKey;
                    }

                    // Derive index-excluded subset (lazy, since FindIndexedGuids depends on current index query)
                    if (_cachedProjectOnlyFiles == null)
                    {
                        // Find which project GUIDs already exist in the full index results (not just the current page)
                        List<string> projectGuids = _cachedProjectFiles
                            .Where(f => !string.IsNullOrEmpty(f.Guid))
                            .Select(f => f.Guid)
                            .ToList();
                        HashSet<string> indexedGuids = AssetSearch.FindIndexedGuids(opt, projectGuids);

                        // Filter to project-only files (not in index or no GUID)
                        _cachedProjectOnlyFiles = _cachedProjectFiles
                            .Where(f => string.IsNullOrEmpty(f.Guid) || !indexedGuids.Contains(f.Guid))
                            .ToList();
                    }

                    List<AssetInfo> projectOnlyFiles = _cachedProjectOnlyFiles;

                    if (_inMemoryMode != InMemoryModeState.None)
                    {
                        // In-memory mode: merge everything, UpdateFilteredFiles handles pagination
                        _files.AddRange(projectOnlyFiles);
                        _resultCount = _files.Count;
                        _originalResultCount = _resultCount;
                    }
                    else
                    {
                        // DB pagination: index results first, project-only files appended after
                        _resultCount = indexCount + projectOnlyFiles.Count;
                        _originalResultCount = _resultCount;

                        int offset = (opt.CurrentPage - 1) * maxResults;
                        if (offset < indexCount)
                        {
                            // Page starts within index results
                            int remainingSlots = maxResults - _files.Count;
                            if (remainingSlots > 0 && projectOnlyFiles.Count > 0)
                            {
                                // Last index page: fill remaining slots with project-only files
                                _files.AddRange(projectOnlyFiles.Take(remainingSlots));
                            }
                        }
                        else
                        {
                            // Page is entirely project-only files
                            int projectOffset = offset - indexCount;
                            _files = projectOnlyFiles.Skip(projectOffset).Take(maxResults).ToList();
                        }
                    }
                }
                else
                {
                    _resultCount = indexCount;
                }
            }
            else
            {
                // Project only
                string projectSearchKey = GetProjectSearchCacheKey(ignoreExcludedExtensions);
                if (_cachedProjectFiles == null || _cachedProjectSearchKey != projectSearchKey)
                {
                    ProjectAssetSearch.Options projOpt = CreateProjectSearchOptions(ignoreExcludedExtensions);
                    ProjectAssetSearch.Result projRes = ProjectAssetSearch.Execute(projOpt);

                    _cachedProjectFiles = projRes.Files;
                    _cachedProjectOnlyFiles = null;
                    _cachedProjectSearchKey = projectSearchKey;
                }

                _files = _cachedProjectFiles;
                _resultCount = _cachedProjectFiles.Count;
                _originalResultCount = _resultCount;
                _searchError = null;
            }

            _requireHierarchyRebuild = true;

            // pagination
            UpdateFilteredFiles();
            if (!keepPage && lastCount != _resultCount)
            {
                SetPage(1, ignoreExcludedExtensions);
            }
            else
            {
                SetPage(_curPage, ignoreExcludedExtensions);
            }
            _searchDone = true;

            // Trigger visible animations update after search completes
            if (AI.Config.playVisibleSearchAnimations)
            {
                TriggerVisibleAnimationsUpdate();
            }
        }

        private void StopSearchPreviewLoading()
        {
            _textureLoading?.Cancel();
            _textureLoading?.Dispose();
            _textureLoading = new CancellationTokenSource();
        }

        private void UpdateSearchPreviews()
        {
            StopSearchPreviewLoading();
            LoadTextures(false, _textureLoading.Token); // TODO: should be true once pages endless scrolling is supported
        }

        private async void LoadAnimTexture(AssetInfo info)
        {
            _animatedTileIndex = SGrid.selectionTile;
            _animatedEntry = info;

            if (_animationPlayer != null)
            {
                _animationPlayer.Dispose();
                _animationPlayer = null;
            }

            string animPreviewFile = info.GetPreviewFile(Paths.GetPreviewFolder(), true);
            if (!File.Exists(animPreviewFile)) return;

            _animationPlayer = new AnimationPlayer(info.Guid);
            bool success = await _animationPlayer.LoadAnimation(info, Paths.GetPreviewFolder());
            if (!success)
            {
                _animationPlayer?.Dispose();
                _animationPlayer = null;
            }
        }

        /// <summary>
        /// Gets the range of tile indices that are currently visible in the scroll view.
        /// </summary>
        private void GetVisibleTileRange(float viewHeight, out int firstIndex, out int lastIndex)
        {
            firstIndex = 0;
            lastIndex = -1;

            if (SGrid.contents == null || SGrid.contents.Length == 0) return;
            if (SGrid.ActualTileHeight <= 0 || SGrid.CellsPerRow <= 0) return;

            int firstVisibleRow = Mathf.FloorToInt(_searchScrollPos.y / SGrid.ActualTileHeight);
            int lastVisibleRow = Mathf.CeilToInt((_searchScrollPos.y + viewHeight) / SGrid.ActualTileHeight);

            // Add buffer rows for smoother loading
            firstVisibleRow = Mathf.Max(0, firstVisibleRow - 1);
            lastVisibleRow = lastVisibleRow + 1;

            firstIndex = firstVisibleRow * SGrid.CellsPerRow;
            lastIndex = Mathf.Min((lastVisibleRow + 1) * SGrid.CellsPerRow - 1, SGrid.contents.Length - 1);
        }

        /// <summary>
        /// Updates visible animations when scroll position or viewport changes.
        /// </summary>
        private void UpdateVisibleAnimations(float viewHeight)
        {
            if (!AI.Config.playVisibleSearchAnimations) return;
            if (_filteredFiles == null || SGrid.contents == null) return;

            GetVisibleTileRange(viewHeight, out int firstVisible, out int lastVisible);
            if (lastVisible < 0) return;

            // Update visibility - dispose animations that scrolled out of view
            _visibleAnimations.UpdateVisibility(
                tileIndex => tileIndex >= firstVisible && tileIndex <= lastVisible,
                tileIndex => RestoreStaticPreviewAsync(tileIndex)
            );

            // Clear and rebuild the queue with visible tiles that need loading
            _visibleAnimations.ClearQueue();

            // Calculate how many new animations we can queue
            int availableSlots = AI.Config.maxVisibleSearchAnimations - _visibleAnimations.TotalActiveCount;

            // Queue animations for visible tiles that aren't loaded or loading
            for (int i = firstVisible; i <= lastVisible && availableSlots > 0; i++)
            {
                if (_visibleAnimations.IsActive(i)) continue;

                // Check if this tile has an animated preview
                if (i >= _filteredFiles.Count) continue;
                AssetInfo info = _filteredFiles[i];
                if (info == null) continue;

                if (!AnimationPlaybackManager<int>.HasAnimatedPreview(info)) continue;

                // Queue for loading
                if (_visibleAnimations.QueueAnimation(i, info))
                {
                    availableSlots--;
                }
            }

            // Process animation load queue
            _visibleAnimations.ProcessQueue();
        }

        private async void RestoreStaticPreviewAsync(int tileIndex)
        {
            if (_filteredFiles == null || tileIndex >= _filteredFiles.Count) return;
            if (SGrid.contents == null || tileIndex >= SGrid.contents.Length) return;

            AssetInfo info = _filteredFiles[tileIndex];
            if (info == null) return;

            string previewFile = info.GetPreviewFile(Paths.GetPreviewFolder(), false);
            if (string.IsNullOrEmpty(previewFile) || !File.Exists(previewFile)) return;

            try
            {
                Texture2D staticTexture = await AssetUtils.LoadLocalTexture(
                    previewFile,
                    false,
                    (AI.Config.upscalePreviews && !AI.Config.upscaleLossless) ? AI.Config.upscaleSize : 0
                );

                if (staticTexture != null && SGrid.contents != null && tileIndex < SGrid.contents.Length)
                {
                    if (AI.Config.tileCornerRadius > 0)
                    {
                        Texture2D roundedTexture = staticTexture.WithRoundedCorners(AI.Config.tileCornerRadius);
                        SGrid.contents[tileIndex].image = roundedTexture;
                        DestroyImmediate(staticTexture);
                    }
                    else
                    {
                        SGrid.contents[tileIndex].image = staticTexture;
                    }
                }
            }
            catch
            {
                // Ignore errors during preview restoration
            }
        }

        private void DisposeAllVisibleAnimations(bool restoreStaticPreviews = false)
        {
            List<int> affectedTiles = _visibleAnimations.DisposeAll();

            // Restore static previews for all tiles that were animated
            if (restoreStaticPreviews && affectedTiles.Count > 0)
            {
                RestoreStaticPreviews(affectedTiles);
            }
        }

        private async void RestoreStaticPreviews(List<int> tileIndices)
        {
            if (_filteredFiles == null || SGrid.contents == null) return;

            foreach (int tileIndex in tileIndices)
            {
                if (tileIndex < 0 || tileIndex >= _filteredFiles.Count) continue;
                if (tileIndex >= SGrid.contents.Length) continue;

                AssetInfo info = _filteredFiles[tileIndex];
                if (info == null) continue;

                string previewFile = info.GetPreviewFile(Paths.GetPreviewFolder(), false);
                if (string.IsNullOrEmpty(previewFile) || !File.Exists(previewFile)) continue;

                try
                {
                    Texture2D staticTexture = await AssetUtils.LoadLocalTexture(
                        previewFile,
                        false,
                        (AI.Config.upscalePreviews && !AI.Config.upscaleLossless) ? AI.Config.upscaleSize : 0
                    );

                    if (staticTexture != null && SGrid.contents != null && tileIndex < SGrid.contents.Length)
                    {
                        if (AI.Config.tileCornerRadius > 0)
                        {
                            Texture2D roundedTexture = staticTexture.WithRoundedCorners(AI.Config.tileCornerRadius);
                            SGrid.contents[tileIndex].image = roundedTexture;
                            DestroyImmediate(staticTexture);
                        }
                        else
                        {
                            SGrid.contents[tileIndex].image = staticTexture;
                        }
                    }
                }
                catch
                {
                    // Ignore errors during preview restoration
                }
            }
        }

        private void TriggerVisibleAnimationsUpdate()
        {
            // Force update on next frames by invalidating saved scroll position
            // and setting retry counter to try for several frames until grid dimensions are ready
            _lastSearchScrollPos = new Vector2(-1, -1);
            _lastViewHeight = -1;
            _visibleAnimationTriggerFrames = 5; // Try for 5 frames to account for grid layout delay
        }


        private async void LoadTextures(bool firstPageOnly, CancellationToken ct)
        {
            int chunkSize = AI.Config.previewChunkSize;

            List<AssetInfo> files;
            if (_filteredFiles == null)
            {
                files = new List<AssetInfo>();
            }
            else if (firstPageOnly)
            {
                int count = Math.Min(20 * 8, _filteredFiles.Count);
                files = _filteredFiles.GetRange(0, count);
            }
            else
            {
                files = new List<AssetInfo>(_filteredFiles);
            }

            for (int i = 0; i < files.Count; i += chunkSize)
            {
                try
                {
                    if (ct.IsCancellationRequested) return;

                    List<Task> tasks = new List<Task>();

                    int chunkEnd = Math.Min(i + chunkSize, files.Count);
                    for (int idx = i; idx < chunkEnd; idx++)
                    {
                        if (ct.IsCancellationRequested) return;

                        int localIdx = idx; // capture value
                        AssetInfo info = files.ElementAt(localIdx);

                        tasks.Add(ProcessAssetInfoAsync(info, localIdx, ct));
                    }

                    await Task.WhenAll(tasks).WithCancellation(ct);
                }
                catch (OperationCanceledException)
                {
                    // Task was canceled, exit the loop
                    return;
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error processing asset: {e.Message}");
                }
            }

            if (_pendingVirtualPreviews.Count > 0)
            {
                RetryPendingVirtualPreviews(ct);
            }
        }

        private async Task ProcessAssetInfoAsync(AssetInfo info, int idx, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // Virtual (in-project) assets use Unity's preview system instead of indexed previews
            if (info.IsVirtual)
            {
                await LoadVirtualAssetPreview(info, idx, ct);
                return;
            }

            string previewFile = null;
            if (info.HasPreview(true)) previewFile = AssetImporter.ValidatePreviewFile(info, Paths.GetPreviewFolder());
            if (previewFile == null || !info.HasPreview(true))
            {
                if (!AI.Config.showIconsForMissingPreviews) return;

                // check if well-known extension
                if (_staticPreviews.TryGetValue(info.Type, out string preview))
                {
                    SGrid.contents[idx].image = EditorGUIUtility.IconContent(preview).image;
                }
                else
                {
                    SGrid.contents[idx].image = EditorGUIUtility.IconContent("d_DefaultAsset Icon").image;
                }
                return;
            }

            Texture2D texture = await AssetUtils.LoadLocalTexture(
                previewFile,
                false,
                // _inMemoryMode != InMemoryModeState.None,
                (AI.Config.upscalePreviews && !AI.Config.upscaleLossless) ? AI.Config.upscaleSize : 0
            );

            if (texture == null)
            {
                info.PreviewState = AssetFile.PreviewOptions.None;
                if (!info.IsVirtual) DBAdapter.DB.Execute("update AssetFile set PreviewState=? where Id=?", info.PreviewState, info.Id);
            }
            else if (SGrid.contents.Length > idx)
            {
                Texture2D textureToUse = texture;
                if (AI.Config.tileCornerRadius > 0)
                {
                    textureToUse = texture.WithRoundedCorners(AI.Config.tileCornerRadius);
                    // Destroy the original texture since we're using the rounded version
                    DestroyImmediate(texture);
                }

                // Store in file preview cache for TreeView access
                _filePreviewCache[info.Id] = textureToUse;
                SGrid.contents[idx].image = textureToUse;
                _needsRepaint = true;
            }
        }

        private Texture2D CopyPreviewTexture(Texture2D preview)
        {
            RenderTexture rt = RenderTexture.GetTemporary(preview.width, preview.height, 0, RenderTextureFormat.ARGB32);
            UnityEngine.Graphics.Blit(preview, rt);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D copy = new Texture2D(preview.width, preview.height, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, preview.width, preview.height), 0, 0);
            copy.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            if (AI.Config.tileCornerRadius > 0)
            {
                Texture2D rounded = copy.WithRoundedCorners(AI.Config.tileCornerRadius);
                DestroyImmediate(copy);
                copy = rounded;
            }

            return copy;
        }

        private async Task LoadVirtualAssetPreview(AssetInfo info, int idx, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(info.ProjectPath) || SGrid.contents == null || idx >= SGrid.contents.Length) return;

            UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(info.ProjectPath);
            if (asset == null) return;

            // Try full preview first, fall back to thumbnail
            Texture2D preview = AssetPreview.GetAssetPreview(asset);

            // AssetPreview.GetAssetPreview can return null if not ready yet - retry a few times
            int retries = 0;
            while (preview == null && retries < 10 && !ct.IsCancellationRequested)
            {
                await Task.Delay(50, ct);
                preview = AssetPreview.GetAssetPreview(asset);
                retries++;
            }

            bool usedFallback = false;
            if (preview == null)
            {
                preview = AssetPreview.GetMiniThumbnail(asset);
                usedFallback = preview != null;
            }

            if (preview != null && SGrid.contents != null && idx < SGrid.contents.Length)
            {
                Texture2D copy = CopyPreviewTexture(preview);

                _filePreviewCache[info.Id] = copy;
                SGrid.contents[idx].image = copy;
                _needsRepaint = true;

                if (usedFallback)
                {
                    _pendingVirtualPreviews[idx] = info;
                }
                else
                {
                    info.PreviewState = AssetFile.PreviewOptions.Provided;
                }
            }
            else if (AI.Config.showIconsForMissingPreviews && SGrid.contents != null && idx < SGrid.contents.Length)
            {
                if (_staticPreviews.TryGetValue(info.Type, out string iconName))
                {
                    SGrid.contents[idx].image = EditorGUIUtility.IconContent(iconName).image;
                }
                else
                {
                    SGrid.contents[idx].image = EditorGUIUtility.IconContent("d_DefaultAsset Icon").image;
                }
                _pendingVirtualPreviews[idx] = info;
            }
        }

        private async void RetryPendingVirtualPreviews(CancellationToken ct)
        {
            if (_virtualPreviewRetryRunning) return;
            _virtualPreviewRetryRunning = true;

            try
            {
                int maxIterations = 30;
                for (int iteration = 0; iteration < maxIterations && _pendingVirtualPreviews.Count > 0; iteration++)
                {
                    await Task.Delay(1000, ct);
                    if (ct.IsCancellationRequested) return;

                    List<int> resolved = new List<int>();
                    foreach (KeyValuePair<int, AssetInfo> entry in _pendingVirtualPreviews)
                    {
                        if (ct.IsCancellationRequested) return;

                        int idx = entry.Key;
                        AssetInfo info = entry.Value;

                        if (string.IsNullOrEmpty(info.ProjectPath) || SGrid.contents == null || idx >= SGrid.contents.Length)
                        {
                            resolved.Add(idx);
                            continue;
                        }

                        Object asset = AssetDatabase.LoadAssetAtPath<Object>(info.ProjectPath);
                        if (asset == null)
                        {
                            resolved.Add(idx);
                            continue;
                        }

                        Texture2D preview = AssetPreview.GetAssetPreview(asset);
                        if (preview != null)
                        {
                            Texture2D copy = CopyPreviewTexture(preview);

                            // Destroy old cached texture
                            if (_filePreviewCache.TryGetValue(info.Id, out Texture2D oldTex) && oldTex != null)
                            {
                                DestroyImmediate(oldTex);
                            }

                            _filePreviewCache[info.Id] = copy;
                            if (SGrid.contents != null && idx < SGrid.contents.Length)
                            {
                                SGrid.contents[idx].image = copy;
                            }
                            info.PreviewState = AssetFile.PreviewOptions.Provided;
                            _needsRepaint = true;
                            resolved.Add(idx);
                        }
                        else if (!UnityEditorCompat.IsLoadingPreview(asset))
                        {
                            resolved.Add(idx);
                        }
                    }

                    foreach (int idx in resolved)
                    {
                        _pendingVirtualPreviews.Remove(idx);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on new search
            }
            finally
            {
                _virtualPreviewRetryRunning = false;
            }
        }

        private void CalculateSearchBulkSelection()
        {
            _assetFileBulkTags.Clear();
            SGrid.selectionItems.ForEach(info => info.AssetTags?.ForEach(t =>
            {
                if (!_assetFileBulkTags.ContainsKey(t.Name)) _assetFileBulkTags.Add(t.Name, new Tuple<int, Color>(0, t.GetColor()));
                _assetFileBulkTags[t.Name] = new Tuple<int, Color>(_assetFileBulkTags[t.Name].Item1 + 1, _assetFileBulkTags[t.Name].Item2);
            }));
            _assetFileAMProjectCount = SGrid.selectionItems.Count(info => info.AssetSource == Asset.Source.AssetManager && string.IsNullOrEmpty(info.Location));
            _assetFileAMCollectionCount = SGrid.selectionItems.Count(info => info.AssetSource == Asset.Source.AssetManager && !string.IsNullOrEmpty(info.Location));
            _assetFileAICaptionCount = SGrid.selectionItems.Count(info => !string.IsNullOrWhiteSpace(info.AICaption));
        }

        public void OpenInSearch(AssetInfo info, bool force = false, bool showFilterTab = true, string searchPhrase = null)
        {
            if (info != null && info.Id <= 0) return;
            if (info != null && !force && info.FileCount <= 0) return;
            AssetInfo oldEntry = _selectedEntry;

            if (info != null && info.Exclude)
            {
                if (!EditorUtility.DisplayDialog("Package is Excluded", "This package is currently excluded from the search. Should it be included again?", "Include Again", "Cancel"))
                {
                    return;
                }
                AI.SetAssetExclusion(info, false);
                ReloadLookups();
            }
            ResetSearch(false, true);
            if (force) _selectedEntry = oldEntry;

            AI.Config.tab = 0;

            // Switch away from Project scope when drilling into a specific package since ProjectAssetSearch does not support metadata filters
            if (info != null && SearchScopeModel.IsProjectOnly(GetConfiguredSearchScope()))
            {
                AI.Config.searchScope = (int)SearchScope.All;
                AI.SaveConfig();
            }

            // Set asset filter if info is provided
            if (info != null)
            {
                // search for exact match first
                string displayName = info.GetDisplayName().Replace("/", " ");
                if (info.SafeName == Asset.NONE)
                {
                    _selectedAsset = 1;
                }
                else
                {
                    _selectedAsset = Array.IndexOf(_assetNames, _assetNames.FirstOrDefault(a => a == displayName + $" [{info.AssetId}]"));
                }
                if (_selectedAsset < 0) _selectedAsset = Array.IndexOf(_assetNames, _assetNames.FirstOrDefault(a => a == displayName.Substring(0, 1) + "/" + displayName + $" [{info.AssetId}]"));
                if (_selectedAsset < 0) _selectedAsset = Array.IndexOf(_assetNames, _assetNames.FirstOrDefault(a => a.EndsWith(displayName + $" [{info.AssetId}]")));

                if (info.AssetSource == Asset.Source.RegistryPackage && _selectedPackageTypes == 1) _selectedPackageTypes = 0;
            }
            else
            {
                // No asset filter - search all packages
                _selectedAsset = 0;
            }

            // Set custom search phrase if provided
            if (!string.IsNullOrEmpty(searchPhrase))
            {
                _searchPhrase = searchPhrase;
                _previousSearchPhrase = searchPhrase;
            }

            _curPage = 1;
            if (showFilterTab) _searchInspectorTab = 1;
            PerformSearch(); // search immediately as "search automatically" setting might be off 
        }

        private void ResetSearch(bool filterBarOnly, bool keepAssetType)
        {
            if (!filterBarOnly)
            {
                _searchPhrase = "";
                _previousSearchPhrase = "";
                if (!keepAssetType) AI.Config.searchType = 0;
            }

            _selectedEntry = null;
            _selectedAsset = 0;
            _selectedPackageTypes = 1;
            _selectedPackageSRPs = 1;
            _selectedPriceOption = 0;
            _searchPrice = 0f;
            _selectedImageType = 0;
            _selectedColorOption = 0;
            _selectedHiddenFilter = 0;
            _selectedColor = Color.clear;
            _selectedPackageTag = 0;
            _selectedFileTag = 0;
            _selectedPublisher = 0;
            _selectedCategory = 0;
            _searchHeight = "";
            _checkMaxHeight = false;
            _searchWidth = "";
            _checkMaxWidth = false;
            _searchLength = "";
            _checkMaxLength = false;
            _searchSize = "";
            _checkMaxSize = false;
            _searchVertexCount = "";
            _checkMaxVertexCount = false;

            // Clear active saved search when resetting
            _activeSavedSearchId = -1;
        }

        private async Task PerformCopyTo(AssetInfo info, string path, bool fromDragDrop = false, bool addToScene = false, Vector3? worldPosition = null, Transform parentTransform = null)
        {
            if (info.InProject && !addToScene) return;
            if (string.IsNullOrEmpty(path)) return;

            while (info.DependencyState == AssetInfo.DependencyStateOptions.Calculating) await Task.Yield();
            if (info.DependencyState == AssetInfo.DependencyStateOptions.Unknown) await CalculateDependencies(info);
            if (info.DependencySize > 0 && DependencyAnalysis.NeedsScan(info.Type))
            {
                CopyTo(info, path, true, AI.Config.scriptImportMode, false, fromDragDrop, false, addToScene, worldPosition, parentTransform);
            }
            else
            {
                CopyTo(info, path, false, 0, true, fromDragDrop, false, addToScene, worldPosition, parentTransform);
            }
        }

        private static bool DragDropAvailable()
        {
            return true;
        }

#if UNITY_6000_3_OR_NEWER
        private void InitDragAndDrop()
        {
            DragAndDrop.ProjectBrowserDropHandlerV2 dropHandler = OnProjectWindowDrop;
            if (!DragAndDrop.HasHandler("ProjectBrowser".GetHashCode(), dropHandler))
            {
                DragAndDrop.AddDropHandlerV2(dropHandler);
            }
        }

        private void DeinitDragAndDrop()
        {
            DragAndDrop.ProjectBrowserDropHandlerV2 dropHandler = OnProjectWindowDrop;
            if (DragAndDrop.HasHandler("ProjectBrowser".GetHashCode(), dropHandler))
            {
                DragAndDrop.RemoveDropHandlerV2(dropHandler);
            }
        }

        private DragAndDropVisualMode OnProjectWindowDrop(EntityId dragEntityId, string dropUponPath, bool perform)
        {
            return DoOnProjectWindowDrop(dropUponPath, perform);
        }

#else
        private void InitDragAndDrop()
        {
            DragAndDrop.ProjectBrowserDropHandler dropHandler = OnProjectWindowDrop;
            if (!DragAndDrop.HasHandler("ProjectBrowser".GetHashCode(), dropHandler))
            {
                DragAndDrop.AddDropHandler(dropHandler);
            }
        }

        private void DeinitDragAndDrop()
        {
            DragAndDrop.ProjectBrowserDropHandler dropHandler = OnProjectWindowDrop;
            if (DragAndDrop.HasHandler("ProjectBrowser".GetHashCode(), dropHandler))
            {
                DragAndDrop.RemoveDropHandler(dropHandler);
            }
        }

        private DragAndDropVisualMode OnProjectWindowDrop(int dragInstanceId, string dropUponPath, bool perform)
        {
            return DoOnProjectWindowDrop(dropUponPath, perform);
        }
#endif

        private DragAndDropVisualMode DoOnHierarchyDrop(Object dropTarget, Transform parentForDraggedObjects, bool perform)
        {
            List<AssetInfo> infos = (List<AssetInfo>)DragAndDrop.GetGenericData("AssetInfo");
            if (infos == null || infos.Count == 0)
            {
                return DragAndDropVisualMode.None;
            }

            if (perform)
            {
                _dragging = false;
                StopDragDrop();

                // Use provided parent, or try to get GameObject from drop target
                Transform finalParent = parentForDraggedObjects;
                if (finalParent == null && dropTarget != null)
                {
                    GameObject targetGameObject = dropTarget as GameObject;
                    if (targetGameObject != null && targetGameObject.scene.IsValid())
                    {
                        finalParent = targetGameObject.transform;
                    }
                }

                // Determine world position: use parent's position if available, otherwise scene view pivot
                Vector3 worldPosition = Vector3.zero;
                if (finalParent != null)
                {
                    worldPosition = finalParent.position;
                }
                else
                {
                    SceneView sceneView = SceneView.lastActiveSceneView;
                    worldPosition = sceneView != null ? sceneView.pivot : Vector3.zero;
                }

                PerformSceneDrop(infos, worldPosition, finalParent);
                DragAndDrop.AcceptDrag();
            }

            return DragAndDropVisualMode.Copy;
        }

#if UNITY_6000_3_OR_NEWER
        private DragAndDropVisualMode OnHierarchyDrop(EntityId dropTargetEntityId, HierarchyDropFlags dropMode, Transform parentForDraggedObjects, bool perform)
        {
#if UNITY_6000_5_OR_NEWER
            Object dropTarget = dropTargetEntityId.IsValid() ? EditorUtility.EntityIdToObject(dropTargetEntityId) : null;
#else
            Object dropTarget = dropTargetEntityId.IsValid() ? EditorUtility.InstanceIDToObject(dropTargetEntityId.GetHashCode()) : null;
#endif
            return DoOnHierarchyDrop(dropTarget, parentForDraggedObjects, perform);
        }

        private DragAndDropVisualMode OnProjectBrowserDrop(EntityId dragEntityId, string dropUponPath, bool perform)
        {
            if (perform) StopDragDrop();
            return DragAndDropVisualMode.None;
        }
#endif

        private DragAndDropVisualMode DoOnProjectWindowDrop(string dropUponPath, bool perform)
        {
            if (perform && _dragging)
            {
                _dragging = false;
                DeinitDragAndDrop();

                List<AssetInfo> infos = (List<AssetInfo>)DragAndDrop.GetGenericData("AssetInfo");
                if (infos != null && infos.Count > 0) // can happen in some edge asynchronous scenarios
                {
                    if (File.Exists(dropUponPath)) dropUponPath = Path.GetDirectoryName(dropUponPath);
                    PerformCopyToBulk(infos, dropUponPath);
                }
                DragAndDrop.AcceptDrag();
            }
            return DragAndDropVisualMode.Copy;
        }

        private async void PerformCopyToBulk(List<AssetInfo> infos, string targetPath)
        {
            if (infos.Count == 0) return;

            foreach (AssetInfo info in infos)
            {
                await PerformCopyTo(info, targetPath, true);
            }
            if (AI.Config.pingImported) PingAsset(infos[0]);
        }

        private async void PerformSceneDrop(List<AssetInfo> infos, Vector3 worldPosition, Transform parentTransform)
        {
            if (infos == null || infos.Count == 0) return;

            foreach (AssetInfo info in infos)
            {
                string projectPath = null;

                if (info.InProject)
                {
                    // Already imported, just add to scene
                    projectPath = info.ProjectPath;
                }
                else
                {
                    // Not imported, need to download (if needed) and import first
                    if (!info.IsDownloaded && !info.IsMaterialized)
                    {
                        if (!await EnsureDownloaded(info)) continue;
                    }

                    while (info.DependencyState == AssetInfo.DependencyStateOptions.Calculating) await Task.Yield();
                    if (info.DependencyState == AssetInfo.DependencyStateOptions.Unknown) await CalculateDependencies(info);

                    string targetPath = AI.GetImportFolder();
                    string importedPath = null;
                    if (info.DependencySize > 0 && DependencyAnalysis.NeedsScan(info.Type))
                    {
                        importedPath = await Assets.CopyTo(info, targetPath, true, AI.Config.scriptImportMode, false, true, false);
                    }
                    else
                    {
                        importedPath = await Assets.CopyTo(info, targetPath, false, 0, false, true, false);
                    }

                    // Use ProjectPath if available (updated by CopyTo), otherwise use returned path
                    projectPath = !string.IsNullOrEmpty(info.ProjectPath) ? info.ProjectPath : importedPath;
                }

                // Add to scene if it's a type that can be instantiated
                if (!string.IsNullOrEmpty(projectPath) && AssetUtils.CanAddToScene(projectPath))
                {
                    AssetUtils.AddToScene(projectPath, worldPosition, parentTransform);
                }
            }
        }

        private DragAndDropVisualMode OnSceneDrop(Object dropUpon, Vector3 worldPosition, Vector2 viewportPosition, Transform parentForDraggedObjects, bool perform)
        {
            List<AssetInfo> infos = (List<AssetInfo>)DragAndDrop.GetGenericData("AssetInfo");
            if (infos == null || infos.Count == 0)
            {
                return DragAndDropVisualMode.None;
            }

            if (perform)
            {
                _dragging = false;
                StopDragDrop();
                PerformSceneDrop(infos, worldPosition, parentForDraggedObjects);
                DragAndDrop.AcceptDrag();
            }

            return DragAndDropVisualMode.Copy;
        }

#if !UNITY_6000_3_OR_NEWER
        private DragAndDropVisualMode OnHierarchyDrop(int dropTargetInstanceID, HierarchyDropFlags dropMode, Transform parentForDraggedObjects, bool perform)
        {
            Object dropTarget = dropTargetInstanceID != 0 ? EditorUtility.InstanceIDToObject(dropTargetInstanceID) : null;
            return DoOnHierarchyDrop(dropTarget, parentForDraggedObjects, perform);
        }

        private DragAndDropVisualMode OnProjectBrowserDrop(int dragInstanceId, string dropUponPath, bool perform)
        {
            if (perform) StopDragDrop();
            return DragAndDropVisualMode.None;
        }
#endif

        private DragAndDropVisualMode OnInspectorDrop(Object[] targets, bool perform)
        {
            if (perform) StopDragDrop();
            return DragAndDropVisualMode.None;
        }

        private void HandleDragDrop()
        {
            if (AI.Config.disableDragDrop) return;
            if (AI.Config.tab != 0) return;

            switch (Event.current.type)
            {
                case EventType.MouseDrag:
                    if (!SGrid.IsMouseOverGrid) return;
                    if (!_draggingPossible || _dragging || _selectedEntry == null) return;

                    // Check if we've moved far enough and waited long enough to start dragging
                    float dragDistance = Vector2.Distance(Event.current.mousePosition, _dragStartPosition);
                    float timeSinceStart = Time.realtimeSinceStartup - _dragStartTime;

                    if (dragDistance < DRAG_THRESHOLD && timeSinceStart < DRAG_DELAY) return;

                    _dragging = true;

                    InitDragAndDrop();
                    DragAndDrop.PrepareStartDrag();

                    if (SGrid.selectionCount > 0)
                    {
                        DragAndDrop.SetGenericData("AssetInfo", SGrid.selectionItems);
                        DragAndDrop.objectReferences = SGrid.selectionItems
                            .Where(item => !string.IsNullOrWhiteSpace(item.ProjectPath))
                            .Select(item => AssetDatabase.LoadMainAssetAtPath(item.ProjectPath))
                            .ToArray();
                    }
                    else
                    {
                        DragAndDrop.SetGenericData("AssetInfo", new List<AssetInfo> {_selectedEntry});
                        if (!string.IsNullOrWhiteSpace(_selectedEntry.ProjectPath))
                        {
                            DragAndDrop.objectReferences = new[] {AssetDatabase.LoadMainAssetAtPath(_selectedEntry.ProjectPath)};
                        }
                    }
                    DragAndDrop.StartDrag("Dragging " + _selectedEntry);
                    Event.current.Use();
                    break;

                case EventType.MouseDown:
                    if (SGrid.IsMouseOverGrid)
                    {
                        _draggingPossible = true;
                        _dragStartPosition = Event.current.mousePosition;
                        _dragStartTime = Time.realtimeSinceStartup;
                    }
                    break;

                case EventType.MouseUp:
                    _draggingPossible = false;
                    StopDragDrop();
                    break;
            }
        }

        private void StopDragDrop()
        {
            if (_dragging)
            {
                _dragging = false;
                GUIUtility.hotControl = 0; // otherwise scene gizmos are still blocked
                DeinitDragAndDrop();
            }
        }

        private void SearchUpdateLoop()
        {
            // Use the captured scroll view height (not current visible rect which may be wrong outside OnGUI)
            float viewHeight = _searchGridViewHeight > 0 ? _searchGridViewHeight : 400f; // fallback if not yet captured
            bool scrollChanged = _searchScrollPos != _lastSearchScrollPos || Math.Abs(viewHeight - _lastViewHeight) > 1f;

            // If scrolling while a single item is selected and visible animations are enabled,
            // stop the single selection and resume visible animations
            if (scrollChanged && AI.Config.playVisibleSearchAnimations && _animatedTileIndex >= 0)
            {
                StopSingleSelectionAnimation();
            }

            // Single selection animation (always works regardless of playVisibleSearchAnimations)
            if (_animationPlayer != null && _animationPlayer.IsLoaded
                && _animatedEntry != null
                && _animatedTileIndex >= 0 && SGrid.contents != null && SGrid.contents.Length > _animatedTileIndex)
            {
                // Get the current frame from the animation player
                Texture2D curTexture = _animationPlayer.GetCurrentFrame();

                if (curTexture != null && SGrid.contents[_animatedTileIndex].image != curTexture)
                {
                    // Only update if frame has changed (AnimationPlayer now caches frames)
                    SGrid.contents[_animatedTileIndex].image = curTexture;
                }
            }

            // Multi-animation for all visible tiles (only when no single item is selected)
            if (AI.Config.playVisibleSearchAnimations && SGrid.contents != null && SGrid.contents.Length > 0 && _animatedTileIndex < 0)
            {
                // Trigger update on scroll change, pending trigger frames, OR if no animations are loaded yet
                bool gridIsReady = SGrid.ActualTileHeight > 0 && SGrid.CellsPerRow > 1 && viewHeight > 50;
                bool needsInitialLoad = _visibleAnimations.TotalActiveCount == 0 && gridIsReady;
                bool hasPendingTrigger = _visibleAnimationTriggerFrames > 0;

                if (scrollChanged || needsInitialLoad || hasPendingTrigger)
                {
                    // Only process if grid is ready (dimensions have been calculated by Draw())
                    if (gridIsReady)
                    {
                        _lastSearchScrollPos = _searchScrollPos;
                        _lastViewHeight = viewHeight;
                        UpdateVisibleAnimations(viewHeight);
                        
                        // Clear trigger if animations started loading
                        if (_visibleAnimations.TotalActiveCount > 0)
                        {
                            _visibleAnimationTriggerFrames = 0;
                        }
                    }
                    
                    // Decrement trigger counter each frame
                    if (hasPendingTrigger)
                    {
                        _visibleAnimationTriggerFrames--;
                    }
                }

                // Update all visible animation frames
                foreach (int tileIndex in _visibleAnimations.LoadedKeys)
                {
                    if (tileIndex < 0 || tileIndex >= SGrid.contents.Length) continue;

                    // Skip if this is the single-selection animated tile (handled above)
                    if (tileIndex == _animatedTileIndex) continue;

                    Texture2D curTexture = _visibleAnimations.GetCurrentFrame(tileIndex);
                    if (curTexture != null && SGrid.contents[tileIndex].image != curTexture)
                    {
                        SGrid.contents[tileIndex].image = curTexture;
                    }
                }
            }
        }

        private void DisposeSearchResultTextures()
        {
            if (SGrid.contents == null) return;

            for (int i = 0; i < SGrid.contents.Length; i++)
            {
                GUIContent content = SGrid.contents[i];
                if (content != null && content.image != null)
                {
                    // Skip built-in Unity icons which shouldn't be destroyed
                    if (content.image.name != "d_DefaultAsset Icon" &&
                        !AssetDatabase.GetAssetPath(content.image).StartsWith("Library/"))
                    {
                        DestroyImmediate(content.image);
                        content.image = null;
                    }
                }
            }

            DisposeAnimTexture();
            DisposeAllVisibleAnimations();
        }

        private void ClearFilePreviewCache()
        {
            // Destroy all cached textures before clearing to prevent memory leaks
            foreach (Texture2D texture in _filePreviewCache.Values)
            {
                if (texture != null)
                {
                    // Skip built-in Unity icons which shouldn't be destroyed
                    if (texture.name != "d_DefaultAsset Icon" &&
                        !AssetDatabase.GetAssetPath(texture).StartsWith("Library/"))
                    {
                        DestroyImmediate(texture);
                    }
                }
            }
            _filePreviewCache.Clear();
            _pendingVirtualPreviews.Clear();
            ProjectMetadataCache.Clear();
        }

        private void StopSingleSelectionAnimation()
        {
            // Stop only the single selection animation, preserve visible animations
            if (_animationPlayer != null)
            {
                _animationPlayer.Dispose();
                _animationPlayer = null;
            }
            _animatedTileIndex = -1;
            _animatedEntry = null;
        }

        private void StopAnimation()
        {
            // Immediately stop animation by clearing state variables
            // This is synchronous and safe to call before grid contents are recreated
            StopSingleSelectionAnimation();

            // Also stop all visible animations
            DisposeAllVisibleAnimations();
        }

        private async void DisposeAnimTexture()
        {
            if (_animationPlayer != null)
            {
                // Capture state immediately to local variables
                AnimationPlayer animPlayer = _animationPlayer;
                AssetInfo animatedEntry = _animatedEntry;
                int tileIndex = _animatedTileIndex;

                // Clear instance fields FIRST to prevent race condition
                // If LoadAnimTexture() runs during async work, it won't be affected by our cleanup
                _animationPlayer = null;
                _animatedTileIndex = -1;
                _animatedEntry = null;

                // Restore the static preview before disposing (use local variables)
                if (animatedEntry != null && tileIndex >= 0 && SGrid.contents != null && SGrid.contents.Length > tileIndex)
                {
                    // Keep reference to the current animated frame (don't destroy it yet to avoid flicker)
                    Texture2D oldAnimFrame = null;
                    if (SGrid.contents[tileIndex].image != null)
                    {
                        // Skip built-in Unity icons which shouldn't be destroyed
                        if (SGrid.contents[tileIndex].image.name != "d_DefaultAsset Icon" &&
                            !AssetDatabase.GetAssetPath(SGrid.contents[tileIndex].image).StartsWith("Library/"))
                        {
                            oldAnimFrame = SGrid.contents[tileIndex].image as Texture2D;
                        }
                    }

                    // Load and restore the static preview
                    string previewFile = null;
                    if (animatedEntry.HasPreview(true))
                    {
                        previewFile = AssetImporter.ValidatePreviewFile(animatedEntry, Paths.GetPreviewFolder());
                    }

                    if (previewFile != null)
                    {
                        Texture2D staticTexture = await AssetUtils.LoadLocalTexture(
                            previewFile,
                            false,
                            (AI.Config.upscalePreviews && !AI.Config.upscaleLossless) ? AI.Config.upscaleSize : 0
                        );

                        // Re-check bounds after async operation in case the grid was resized
                        if (staticTexture != null && SGrid.contents != null && SGrid.contents.Length > tileIndex && tileIndex >= 0)
                        {
                            // Now destroy the old animated frame AFTER we have the static preview ready
                            if (oldAnimFrame != null)
                            {
                                DestroyImmediate(oldAnimFrame);
                            }

                            if (AI.Config.tileCornerRadius > 0)
                            {
                                Texture2D roundedTexture = staticTexture.WithRoundedCorners(AI.Config.tileCornerRadius);
                                SGrid.contents[tileIndex].image = roundedTexture;
                                DestroyImmediate(staticTexture);
                            }
                            else
                            {
                                SGrid.contents[tileIndex].image = staticTexture;
                            }
                        }
                        else if (staticTexture != null)
                        {
                            // Grid was resized during async operation, clean up the texture
                            DestroyImmediate(staticTexture);
                        }
                    }
                }

                // Destroy the captured animation player (not the instance field which may have new data)
                animPlayer?.Dispose();
            }
        }

        private void DetectVariablesInSearchPhrase()
        {
            if (string.IsNullOrEmpty(_searchPhrase))
            {
                _searchVariables.Clear();
                _hasSearchVariables = false;
                return;
            }

            // Find all variable references
            List<string> varNames = VariableResolver.FindVariableReferences(_searchPhrase);

            // Update existing variables or add new ones
            HashSet<string> currentVars = new HashSet<string>(varNames);

            // Remove variables that are no longer referenced
            List<string> toRemove = new List<string>();
            foreach (string key in _searchVariables.Keys)
            {
                if (!currentVars.Contains(key))
                {
                    toRemove.Add(key);
                }
            }
            foreach (string key in toRemove)
            {
                _searchVariables.Remove(key);
            }

            // Add new variables (keep existing ones unchanged to preserve user values)
            foreach (string varName in varNames)
            {
                if (!_searchVariables.ContainsKey(varName))
                {
                    _searchVariables[varName] = new SearchVariable
                    {
                        name = varName,
                        defaultValue = "",
                        currentValue = ""
                    };
                }
            }

            bool hadVariables = _hasSearchVariables;
            _hasSearchVariables = _searchVariables.Count > 0;

            // Trigger search update if variables were newly detected
            if (!hadVariables && _hasSearchVariables)
            {
                _requireSearchUpdate = true;
            }
        }

        private void ShowVariableDropdown(SearchVariable variable)
        {
            GenericMenu menu = new GenericMenu();

            // Capture mouse position now, before any lambdas
            Vector2 mousePosition = Event.current != null ? Event.current.mousePosition : Vector2.zero;

            // Predefined options
            if (variable.options != null && variable.options.Count > 0)
            {
                menu.AddDisabledItem(new GUIContent("Predefined Options"));
                foreach (string option in variable.options)
                {
                    string capturedOption = option;
                    menu.AddItem(new GUIContent("  " + option), false, () =>
                    {
                        variable.currentValue = capturedOption;
                        _requireSearchUpdate = true;
                    });
                }
                menu.AddSeparator("");
            }

            // Actions
            if (variable.currentValue != variable.defaultValue)
            {
                menu.AddItem(new GUIContent("Set Current as Default"), false, () =>
                {
                    variable.defaultValue = variable.currentValue;
                    // Update saved search if currently viewing one
                    if (_activeSavedSearchId > 0)
                    {
                        SavedSearch savedSearch = Searches.FirstOrDefault(s => s.Id == _activeSavedSearchId);
                        if (savedSearch != null)
                        {
                            savedSearch.VariableDefinitions = SerializeSearchVariables(_searchVariables);
                            DBAdapter.DB.Update(savedSearch);
                        }
                    }
                });
            }

            menu.AddItem(new GUIContent("Edit Options..."), false, () =>
            {
                string currentOptions = variable.options != null && variable.options.Count > 0
                    ? string.Join(", ", variable.options)
                    : "";

                NameUI nameUI = new NameUI();
                nameUI.Init(currentOptions, (optionsText) =>
                {
                    // Parse comma-separated options
                    List<string> updatedOptions = new List<string>();
                    if (!string.IsNullOrWhiteSpace(optionsText))
                    {
                        updatedOptions = optionsText
                            .Split(',')
                            .Select(s => s.Trim())
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .Distinct()
                            .ToList();
                    }

                    variable.options = updatedOptions;

                    // Update saved search if currently viewing one
                    if (_activeSavedSearchId > 0)
                    {
                        SavedSearch savedSearch = Searches.FirstOrDefault(s => s.Id == _activeSavedSearchId);
                        if (savedSearch != null)
                        {
                            savedSearch.VariableDefinitions = SerializeSearchVariables(_searchVariables);
                            DBAdapter.DB.Update(savedSearch);
                        }
                    }
                }, allowEmpty: true, title: "Comma-separated options");
                PopupWindow.Show(new Rect(mousePosition.x, mousePosition.y, 0, 0), nameUI);
            });

            menu.ShowAsContext();
        }

        private string SerializeSearchVariables(Dictionary<string, SearchVariable> variables)
        {
            if (variables == null || variables.Count == 0) return null;

            SearchVariableCollection collection = SearchVariableCollection.FromDictionary(variables);
            return collection.ToJson();
        }

        private Dictionary<string, SearchVariable> DeserializeSearchVariables(string json)
        {
            if (string.IsNullOrEmpty(json)) return new Dictionary<string, SearchVariable>();

            SearchVariableCollection collection = SearchVariableCollection.FromJson(json);
            return collection.Variables ?? new Dictionary<string, SearchVariable>();
        }

        private void DrawFileInfo(AssetInfo info, bool showActions = true)
        {
            if (info == null) return;

            int labelWidth = 95;
            bool mainUsed = false;
            bool isAudio = AI.IsFileType(info.Path, AI.AssetGroup.Audio);

            EditorGUILayout.LabelField("File", EditorStyles.largeLabel);
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(CommonUIStyles.Content("Name", $"Internal Id: {info.Id:N0}\nPreview State: {info.PreviewState.ToString()}\nGuid: {info.Guid}"), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
            if (info.AssetSource == Asset.Source.AssetManager)
            {
                if (GUILayout.Button(CommonUIStyles.Content(Path.GetFileName(info.GetPath(true))), CommonUIStyles.wrappedLinkLabel, GUILayout.ExpandWidth(true)))
                {
                    AI.OpenURL(info.GetAMAssetUrl());
                }
            }
            else
            {
                EditorGUILayout.LabelField(CommonUIStyles.Content(Path.GetFileName(info.GetPath(true)), info.GetPath(true)), EditorStyles.wordWrappedLabel);
            }
            GUILayout.EndHorizontal();
            if (info.AssetSource == Asset.Source.Directory) UIBlock("asset.location", () => GUILabelWithText("Location", $"{Path.GetDirectoryName(info.GetPath(true))}", 95, null, true));
            if (!string.IsNullOrWhiteSpace(info.FileStatus)) UIBlock("asset.status", () => GUILabelWithText("Status", $"{info.FileStatus}"));
            UIBlock("asset.size", () => GUILabelWithText("Size", EditorUtility.FormatBytes(info.Size)));
            if (info.Width > 0) UIBlock("asset.dimensions", () => GUILabelWithText("Dimensions", $"{info.Width:N0} x {info.Height:N0} px"));

            // For FBX files, show animations and mesh statistics
            if (info.Type == "fbx")
            {
                // Show animation count if available
                if (info.Length > 0)
                {
                    int animCount = (int)info.Length;
                    UIBlock("asset.animations", () =>
                    {
                        GUILayout.BeginHorizontal();
                        GUILabelWithText("Animations", $"{animCount}");
                        if (GUILayout.Button(EditorGUIUtility.IconContent("d_animationvisibilitytoggleon", "|Show...")))
                        {
                            AnimationsUI animUI = AnimationsUI.ShowWindow();
                            animUI.Init(info);
                        }
                        GUILayout.EndHorizontal();
                    });
                }

                // Display FBX mesh statistics if available
                if (!string.IsNullOrEmpty(info.FileData))
                {
                    try
                    {
                        FBXData fbxData = JsonConvert.DeserializeObject<FBXData>(info.FileData);
                        if (fbxData != null)
                        {
                            if (fbxData.meshCount > 0) UIBlock("asset.meshes", () => GUILabelWithText("Meshes", $"{fbxData.meshCount:N0}"));
                            if (fbxData.materialCount > 0) UIBlock("asset.materials", () => GUILabelWithText("Materials", $"{fbxData.materialCount:N0}"));
                            if (fbxData.vertexCount > 0) UIBlock("asset.vertices", () => GUILabelWithText("Vertices", $"{fbxData.vertexCount:N0}"));
                            if (fbxData.triangleCount > 0) UIBlock("asset.triangles", () => GUILabelWithText("Triangles", $"{fbxData.triangleCount:N0}"));
                            if (fbxData.boneCount > 0) UIBlock("asset.bones", () => GUILabelWithText("Bones", $"{fbxData.boneCount:N0}"));
                        }
                    }
                    catch
                    {
                        // Silently ignore parsing errors
                    }
                }
            }
            else if (info.Length > 0)
            {
                // For non-FBX files, show duration
                UIBlock("asset.length", () => GUILabelWithText("Length", $"{StringUtils.FormatDuration(info.Length)}"));
            }
            if (ShowAdvanced() || info.InProject) GUILabelWithText("In Project", info.InProject ? "Yes" : "No");
            bool needsDependencyScan = false;
            bool showDependencyBlock = info.InProject || (!info.IsVirtual && (info.IsDownloaded || info.IsMaterialized) && (info.AssetSource == Asset.Source.AssetManager || DependencyAnalysis.NeedsScan(info.Type)));
            if (showDependencyBlock)
            {
                UIBlock("asset.dependencies", () =>
                {
                    if (info.InProject)
                    {
                        // In-project assets: show dependency count + eye icon + where-used icon
                        GUILayout.BeginHorizontal();
                        if (info.DependencyState == AssetInfo.DependencyStateOptions.Done && info.Dependencies != null)
                        {
                            GUILabelWithText("Dependencies", $"{info.Dependencies.Count}");
                        }
                        else
                        {
                            EditorGUILayout.LabelField("Dependencies", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                        }
                        if (GUILayout.Button(EditorGUIUtility.IconContent("d_animationvisibilitytoggleon", "|Show dependencies..."), GUILayout.Width(30)))
                        {
                            ShowProjectDependencies(info);
                        }
                        if (GUILayout.Button(EditorGUIUtility.IconContent("d_Linked", "|Where used..."), GUILayout.Width(30)))
                        {
                            ShowWhereUsed(info);
                        }
                        GUILayout.EndHorizontal();
                    }
                    else
                    {
                        // Indexed assets not in project: existing dependency state machine
                        switch (info.DependencyState)
                        {
                            case AssetInfo.DependencyStateOptions.Unknown:
                                needsDependencyScan = true;
                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField("Dependencies", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                                if (GUILayout.Button("Calculate", GUILayout.ExpandWidth(false)))
                                {
                                    // must run in same thread
                                    _ = CalculateDependencies(info);
                                }
                                GUILayout.EndHorizontal();
                                break;

                            case AssetInfo.DependencyStateOptions.Partial:
                                GUILayout.BeginHorizontal();
                                GUILabelWithText("Dependencies", FormatDependencyCount(info, ShowAdvanced()));
                                if (CanShowDependencyTree(info) && GUILayout.Button(EditorGUIUtility.IconContent("d_animationvisibilitytoggleon", "|Show incomplete dependency tree...")))
                                {
                                    DependenciesUI depUI = DependenciesUI.ShowWindow();
                                    depUI.Init(info);
                                }
                                if (GUILayout.Button("Retry", GUILayout.ExpandWidth(false)))
                                {
                                    _ = CalculateDependencies(info);
                                }
                                GUILayout.EndHorizontal();
                                break;

                            case AssetInfo.DependencyStateOptions.Calculating:
                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField("Dependencies", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                                EditorGUILayout.LabelField("Calculating...", GUILayout.ExpandWidth(true));
                                if (GUILayout.Button("Cancel", GUILayout.ExpandWidth(false)))
                                {
                                    CancelDependencyCalculation(info);
                                }
                                GUILayout.EndHorizontal();
                                break;

                            case AssetInfo.DependencyStateOptions.NotPossible:
                                if (IsIncompleteDependencyResult(info))
                                {
                                    GUILayout.BeginHorizontal();
                                    GUILabelWithText("Dependencies", FormatDependencyCount(info, ShowAdvanced()));
                                    if (CanShowDependencyTree(info) && GUILayout.Button(EditorGUIUtility.IconContent("d_animationvisibilitytoggleon", "|Show incomplete dependency tree...")))
                                    {
                                        DependenciesUI depUI = DependenciesUI.ShowWindow();
                                        depUI.Init(info);
                                    }
                                    if (GUILayout.Button("Retry", GUILayout.ExpandWidth(false)))
                                    {
                                        _ = CalculateDependencies(info);
                                    }
                                    GUILayout.EndHorizontal();
                                }
                                else
                                {
                                    GUILabelWithText("Dependencies", "Cannot determine (binary)");
                                }
                                break;

                            case AssetInfo.DependencyStateOptions.Failed:
                                GUILabelWithText("Dependencies", "Failed to determine");
                                break;

                            case AssetInfo.DependencyStateOptions.Done:
                                GUILayout.BeginHorizontal();
                                GUILabelWithText("Dependencies", FormatDependencyCount(info, ShowAdvanced()));
                                if (CanShowDependencyTree(info) && GUILayout.Button(EditorGUIUtility.IconContent("d_animationvisibilitytoggleon", "|Show...")))
                                {
                                    DependenciesUI depUI = DependenciesUI.ShowWindow();
                                    depUI.Init(info);
                                }
                                GUILayout.EndHorizontal();
                                break;
                        }

                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CommonUIStyles.Content("Script Import", "Controls how scripts are handled when importing with dependencies.\n\n• Never Import: Exclude all scripts\n• Direct Only: Include scripts directly referenced via GUID\n• Extended Analysis: Analyze C# code for type dependencies and resolve assembly definitions\n• All Scripts: Include all script-related files (cs, dll, asmdef, asmref, rsp)"), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                        EditorGUI.BeginChangeCheck();
                        AI.Config.scriptImportMode = EditorGUILayout.Popup(AI.Config.scriptImportMode, _scriptImportOptions);
                        if (EditorGUI.EndChangeCheck())
                        {
                            AI.SaveConfig();
                            _files.ForEach(f => f.DependencyState = AssetInfo.DependencyStateOptions.Unknown);
                            CalcDependenciesOnDemand(_selectedEntry);
                        }
                        GUILayout.EndHorizontal();
                    }
                });
            }
            bool downloadable = IsDownloadable(info);
            bool availableOrDownloadable = info.IsDownloaded || info.IsMaterialized || downloadable;
            string dlHint = downloadable ? " ↓" : "";
            string dlTip = downloadable ? "Package will be downloaded first." : null;
            if (availableOrDownloadable)
            {
                if (showActions)
                {
                    if (!info.InProject && string.IsNullOrEmpty(_importFolder))
                    {
                        EditorGUILayout.Space();
                        EditorGUILayout.LabelField("Select a folder in Project View for import options", EditorStyles.centeredGreyMiniLabel);
                        EditorGUI.BeginDisabledGroup(true);
                        GUILayout.Button("Import File");
                        EditorGUI.EndDisabledGroup();
                    }
                    else
                    {
                        EditorGUILayout.Space();

                        if (ShowAdvanced())
                        {
                            EditorGUI.BeginDisabledGroup(_blockingInProgress);
                            if ((!info.InProject || ShowAdvanced()) && !string.IsNullOrEmpty(_importFolder))
                            {
                                string command = info.InProject ? "Reimport" : "Import";
                                GUILabelWithText($"{command} To", _importFolder, 95, null, true);

                                if (needsDependencyScan)
                                {
                                    EditorGUILayout.LabelField("Dependency scan needed to determine additional import options.", CommonUIStyles.centeredGreyWrappedMiniLabel);
                                }

                                if (AssetUtils.CanAddToScene(info.FileName))
                                {
                                    EditorGUI.BeginDisabledGroup(_blockingInProgress);
                                    if (CommonUIStyles.MainButton(ref mainUsed, CommonUIStyles.Content($"Add to Scene{dlHint}", dlTip)))
                                    {
                                        _ = PerformCopyTo(info, _importFolder, false, true);
                                    }
                                    EditorGUI.EndDisabledGroup();
                                }

                                if (needsDependencyScan || downloadable)
                                {
                                    EditorGUI.BeginDisabledGroup(_blockingInProgress);
                                    if (CommonUIStyles.MainButton(ref mainUsed, CommonUIStyles.Content($"Import{dlHint}", dlTip)))
                                    {
                                        CopyTo(info, _importFolder, true);
                                    }
                                    EditorGUI.EndDisabledGroup();
                                }
                                else
                                {
                                    if (info.DependencySize > 0 && DependencyAnalysis.NeedsScan(info.Type))
                                    {
                                        if (CommonUIStyles.MainButton(ref mainUsed, CommonUIStyles.Content($"{command} With Dependencies{dlHint}", dlTip)))
                                        {
                                            CopyTo(info, _importFolder, true, AI.Config.scriptImportMode, true, false, info.InProject);
                                        }
                                    }
                                    if (CommonUIStyles.MainButton(ref mainUsed, CommonUIStyles.Content($"{command} File" + (info.DependencySize > 0 ? " Only" : "") + dlHint, dlTip)))
                                    {
                                        CopyTo(info, _importFolder, false, 0, true, false, info.InProject);
                                    }
                                    EditorGUILayout.Space();
                                }
                            }
                            EditorGUI.EndDisabledGroup();
                        }
                        else
                        {
                            if (AssetUtils.CanAddToScene(info.FileName))
                            {
                                EditorGUI.BeginDisabledGroup(_blockingInProgress);
                                if (CommonUIStyles.MainButton(ref mainUsed, CommonUIStyles.Content($"Add to Scene{dlHint}", dlTip)))
                                {
                                    _ = PerformCopyTo(info, _importFolder, false, true);
                                }
                                EditorGUI.EndDisabledGroup();
                            }

                            if (!info.InProject)
                            {
                                EditorGUI.BeginDisabledGroup(_blockingInProgress);
                                if (CommonUIStyles.MainButton(ref mainUsed, CommonUIStyles.Content($"Import{dlHint}", dlTip)))
                                {
                                    CopyTo(info, _importFolder, true, AI.Config.scriptImportMode);
                                }
                                EditorGUI.EndDisabledGroup();
                            }
                        }
                    }
                }

#if !AUDIO_TOOL_NOAUDIO
                if (isAudio)
                {
                    UIBlock("asset.actions.audiopreview", () =>
                    {
                        bool isPreviewClipPlaying = EditorAudioUtility.IsPreviewClipPlaying();

                        GUILayout.BeginHorizontal();
                        if (GUILayout.Button(EditorGUIUtility.IconContent("d_PlayButton", "|Play"), GUILayout.Width(40))) PlayAudio(info);
                        EditorGUI.BeginDisabledGroup(!isPreviewClipPlaying);
                        if (GUILayout.Button(EditorGUIUtility.IconContent("d_PreMatQuad", "|Stop"), GUILayout.Width(40))) EditorAudioUtility.StopAllPreviewClips();
                        EditorGUI.EndDisabledGroup();
                        EditorGUILayout.Space();
                        EditorGUI.BeginChangeCheck();
                        AI.Config.autoPlayAudio = GUILayout.Toggle(AI.Config.autoPlayAudio, "Auto-Play");
                        AI.Config.loopAudio = GUILayout.Toggle(AI.Config.loopAudio, "Loop");
                        if (EditorGUI.EndChangeCheck())
                        {
                            AI.SaveConfig();
                            if (AI.Config.autoPlayAudio) PlayAudio(info);
                        }
                        GUILayout.EndHorizontal();

                        // scrubbing (Unity 2020.1+)
                        if (isPreviewClipPlaying && EditorAudioUtility.LastPlayedPreviewClip != null)
                        {
                            AudioClip currentClip = EditorAudioUtility.LastPlayedPreviewClip;
                            EditorGUI.BeginChangeCheck();
                            float newVal = EditorGUILayout.Slider(EditorAudioUtility.GetPreviewClipPosition(), 0, currentClip.length);
                            if (EditorGUI.EndChangeCheck())
                            {
                                AudioManager.StopAudio();
                                EditorAudioUtility.PlayPreviewClip(currentClip, Mathf.RoundToInt(currentClip.samples * newVal / currentClip.length), false);
                            }
                        }
                        EditorGUILayout.Space();
                    });

                    // Edit & Import Audio button
                    if (showActions && !string.IsNullOrEmpty(_importFolder))
                    {
                        UIBlock("asset.actions.audioedit", () =>
                        {
                            EditorGUI.BeginDisabledGroup(_blockingInProgress);
                            if (GUILayout.Button(CommonUIStyles.Content("Edit Audio...", "Open audio editor to trim, process, and import a portion of the audio file")))
                            {
                                OpenAudioEditor(info, _importFolder);
                            }
                            EditorGUI.EndDisabledGroup();
                        });
                    }
                }
#endif

                if (info.InProject && !AI.Config.pingSelected)
                {
                    UIBlock("asset.actions.ping", () =>
                    {
                        if (GUILayout.Button("Ping")) PingAsset(info);
                    });
                }

                if (showActions)
                {
                    EditorGUI.BeginDisabledGroup(_blockingInProgress);
                    UIBlock("asset.actions.open", () =>
                    {
                        if (GUILayout.Button(CommonUIStyles.Content($"Open{dlHint}", dlTip ?? "Open the file with the assigned system application")))
                        {
                            Open(info);
                        }
                    });
                    UIBlock("asset.actions.openexplorer", () =>
                    {
                        string explorerLabel = (Application.platform == RuntimePlatform.OSXEditor ? "Show in Finder" : "Show in Explorer") + dlHint;
                        if (GUILayout.Button(CommonUIStyles.Content(explorerLabel, dlTip)))
                        {
                            OpenExplorer(info);
                        }
                    });
                    if (!info.IsVirtual)
                    {
                    UIBlock("asset.actions.recreatepreview", () =>
                    {
                        if (((ShowAdvanced()
                                || info.PreviewState == AssetFile.PreviewOptions.Error
                                || info.PreviewState == AssetFile.PreviewOptions.None
                                || info.PreviewState == AssetFile.PreviewOptions.Redo
                                || info.PreviewState == AssetFile.PreviewOptions.RedoMissing))
                            && PreviewManager.IsPreviewable(info.FileName, true, info)
                            && GUILayout.Button("Recreate Preview"))
                        {
                            RecreatePreviews(new List<AssetInfo> {info});
                        }
                    });
                    UIBlock("asset.actions.recreateaicaption", () =>
                    {
                        if (AI.Actions.CreateAICaptions && !info.IsVirtual)
                        {
                            if (ShowAdvanced() || string.IsNullOrWhiteSpace(info.AICaption))
                            {
                                EditorGUILayout.BeginHorizontal();
                                if (GUILayout.Button(string.IsNullOrWhiteSpace(info.AICaption) ? "Create AI Caption" : "Recreate AI Caption"))
                                {
                                    RecreateAICaptions(new List<AssetInfo> {info});
                                }
                                if (GUILayout.Button("Enter Manually..."))
                                {
                                    NameUI nameUI = new NameUI();
                                    nameUI.Init(info.AICaption, text =>
                                    {
                                        if (text == info.AICaption) return;

                                        AI.SetAICaption(info, text);
                                        _requireSearchUpdate = true;
                                    });
                                    PopupWindow.Show(GetPopupPositionAtMouse(), nameUI);
                                }
                                EditorGUI.BeginDisabledGroup(string.IsNullOrWhiteSpace(info.AICaption));
                                if (GUILayout.Button(EditorGUIUtility.IconContent("TreeEditor.Trash", "|Remove AI Caption"), GUILayout.Width(30)))
                                {
                                    AI.SetAICaption(info, null);
                                    _requireSearchUpdate = true;
                                }
                                EditorGUI.EndDisabledGroup();
                                EditorGUILayout.EndHorizontal();
                            }
                        }
                    });
                    EditorGUI.EndDisabledGroup();

#if USE_ASSET_MANAGER && USE_CLOUD_IDENTITY
                    if (AI.Actions.IndexAssetManager)
                    {
                        EditorGUI.BeginDisabledGroup(CloudAssetManagement.IsBusy);
                        EditorGUILayout.Space();
                        if (info.AssetSource == Asset.Source.AssetManager)
                        {
                            if (info.ParentInfo == null)
                            {
                                if (GUILayout.Button(CommonUIStyles.Content("Delete from Project", "Delete the file from the Asset Manager project.")))
                                {
                                    DeleteAssetsFromProject(new List<AssetInfo> {info});
                                }
                            }
                            else
                            {
                                if (GUILayout.Button(CommonUIStyles.Content("Remove from Collection", "Remove the file from the Asset Manager collection.")))
                                {
                                    RemoveAssetsFromCollection(new List<AssetInfo> {info});
                                }
                            }
                        }
                        else
                        {
                            if (GUILayout.Button("Upload to Asset Manager..."))
                            {
                                ProjectSelectionUI projectUI = new ProjectSelectionUI();
                                projectUI.Init(project =>
                                {
                                    AddAssetsToProject(project, new List<AssetInfo> {info});
                                });
                                projectUI.SetAssets(_assets);
                                PopupWindow.Show(GetPopupPositionAtMouse(), projectUI);
                            }
                        }
                        EditorGUI.EndDisabledGroup();
                    }
#endif
                    } // end if (!info.IsVirtual)

                    if (info.InProject)
                    {
                        UIBlock("asset.actions.remove", () =>
                        {
                            if (GUILayout.Button(CommonUIStyles.Content("Remove from Project...", "Remove the file and its dependencies from the current project.")))
                            {
                                UninstallPackageUI.ShowWindow().Init(new List<AssetInfo> {info});
                            }
                        });
                    }

                    if (!info.IsVirtual)
                    {
                    UIBlock("asset.actions.delete", () =>
                    {
                        EditorGUILayout.Space();
                        if (!info.Hidden)
                        {
                            if (GUILayout.Button(CommonUIStyles.Content("Hide from Results", "Will hide this file from search results. It can be shown again using the Hidden Files filter.")))
                            {
                                Assets.SetFilesHidden(new List<int> {info.Id}, true);
                                _requireSearchUpdate = true;
                            }
                        }
                        else
                        {
                            if (GUILayout.Button(CommonUIStyles.Content("Unhide", "Will make this file visible in search results again.")))
                            {
                                Assets.SetFilesHidden(new List<int> {info.Id}, false);
                                _requireSearchUpdate = true;
                            }
                        }
                    });
                    }
                }
                if (!info.IsMaterialized && !_blockingInProgress)
                {
                    UIBlock("asset.actions.extraction", () =>
                    {
                        if (downloadable)
                        {
                            long size = info.GetRoot().PackageSize;
                            string sizeText = size > 0 ? $" ({EditorUtility.FormatBytes(size)})" : "";
                            EditorGUILayout.LabelField($"Package will be downloaded{sizeText} and extracted first", CommonUIStyles.centeredGreyWrappedMiniLabel);
                        }
                        else if (info.AssetSource == Asset.Source.AssetManager)
                        {
                            EditorGUILayout.LabelField($"{EditorUtility.FormatBytes(info.Size)} will be downloaded first", EditorStyles.centeredGreyMiniLabel);
                        }
                        else
                        {
                            EditorGUILayout.LabelField($"{EditorUtility.FormatBytes(info.GetRoot().PackageSize)} will be extracted first", EditorStyles.centeredGreyMiniLabel);
                        }
                    });
                }
            }
            else if (info.IsLocationUnmappedRelative())
            {
                EditorGUILayout.HelpBox("The location of this package is stored relative and no mapping has been done yet for this system in the settings: " + info.Location, MessageType.Info);
            }

            if (_blockingInProgress)
            {
                EditorGUI.EndDisabledGroup();
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField("Working...", EditorStyles.miniLabel, GUILayout.Width(55));
                if (_extraction != null)
                {
                    EditorGUI.BeginDisabledGroup(_extraction.IsCancellationRequested);
                    if (GUILayout.Button(CommonUIStyles.Content("x", "Cancel Activity"), EditorStyles.miniButton))
                    {
                        _extraction?.Cancel();
                    }
                    EditorGUI.EndDisabledGroup();
                }
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
                EditorGUI.BeginDisabledGroup(_blockingInProgress);
            }

            // AI
            if (!string.IsNullOrWhiteSpace(info.AICaption))
            {
                EditorGUILayout.LabelField(info.AICaption, EditorStyles.wordWrappedLabel);
            }

            if (!info.IsVirtual)
            {
            UIBlock("asset.actions.tag", () =>
            {
                // tags
                DrawAddFileTag(new List<AssetInfo> {info});

                if (info.AssetTags != null && info.AssetTags.Count > 0)
                {
                    float x = 0f;
                    foreach (TagInfo tagInfo in info.AssetTags)
                    {
                        x = CalcTagSize(x, tagInfo.Name);
                        UIStyles.DrawTag(tagInfo, () =>
                        {
                            Tagging.RemoveAssignment(info, tagInfo, true, true);
                            _requireAssetTreeRebuild = true;
                            _requireSearchUpdate = true;
                        }, GetMaxDetailTagWidth());
                    }
                }
                GUILayout.EndHorizontal();
            });
            }

            EditorGUILayout.Space();
        }

        internal static string FormatDependencyCount(AssetInfo info, bool showAdvanced)
        {
            int dependencyCount = GetDependencyCount(info);
            string result;

            if (showAdvanced && HasSeparatedDependencyCounts(info))
            {
                int mediaDependencyCount = info?.MediaDependencies?.Count ?? 0;
                int scriptDependencyCount = info?.ScriptDependencies?.Count ?? 0;
                string scriptDeps = scriptDependencyCount > 0 ? $" + {scriptDependencyCount} scripts" : string.Empty;
                result = $"{mediaDependencyCount}{scriptDeps}";
            }
            else
            {
                result = $"{dependencyCount}";
            }

            return IsIncompleteDependencyResult(info)
                ? $"{result} (incomplete)"
                : result;
        }

        internal static bool CanShowDependencyTree(AssetInfo info)
        {
            return info?.Dependencies != null && info.Dependencies.Count > 0 &&
                (info.DependencyState == AssetInfo.DependencyStateOptions.Done ||
                    IsIncompleteDependencyResult(info));
        }

        internal static string FormatHiddenExtensionsForDisplay(string extensions)
        {
            if (string.IsNullOrWhiteSpace(extensions)) return string.Empty;

            string[] parts = extensions
                .Split(new[] {';'}, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => !string.IsNullOrEmpty(part))
                .ToArray();

            return string.Join("; ", parts);
        }

        internal static bool IsIncompleteDependencyResult(AssetInfo info)
        {
            return HasDependencyRows(info) &&
                (info.DependencyState == AssetInfo.DependencyStateOptions.Partial ||
                    info.DependencyState == AssetInfo.DependencyStateOptions.NotPossible);
        }

        internal static bool HasDependencyRows(AssetInfo info)
        {
            return GetDependencyCount(info) > 0;
        }

        internal static int GetDependencyCount(AssetInfo info)
        {
            if (info?.Dependencies != null) return info.Dependencies.Count;
            return (info?.MediaDependencies?.Count ?? 0) + (info?.ScriptDependencies?.Count ?? 0);
        }

        private static bool HasSeparatedDependencyCounts(AssetInfo info)
        {
            return info?.MediaDependencies != null || info?.ScriptDependencies != null;
        }
    }
}
