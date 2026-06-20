using System.Collections.Generic;
using System.Linq;
using ImpossibleRobert.Common;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    public sealed partial class DependenciesUI : BasicEditorUI
    {
        private Vector2 _scrollPos;
        private AssetInfo _info;
        private string _dependencyTypes;

        // Virtualization caches
        private List<ListViewEntry> _listViewEntries;
        private HashSet<int> _scriptDependencyIds;
        private Dictionary<int, Asset> _crossPackageDict;
        private static GUIContent _iconInstalled;
        private static GUIContent _iconImport;
        private static GUIStyle _yellowLabelStyle;
        private float _lastLabelWidth;
        private bool _heightsNeedRecalculation;

        private struct ListViewEntry
        {
            public bool IsHeader;
            public string HeaderText;
            public AssetFile File;
            public string DisplayText;
            public bool IsScriptDependency;
            public bool FromSRPSupport;
            public float CalculatedHeight;
        }

        public static DependenciesUI ShowWindow()
        {
            DependenciesUI window = GetWindow<DependenciesUI>("Asset Dependencies");
            window.minSize = new Vector2(500, 200);

            return window;
        }

        public void Init(AssetInfo info)
        {
            _info = info;
            _serializedAssetInfoId = info?.Id ?? -1;

            if (_info?.Dependencies != null)
            {
                _info.Dependencies.ForEach(i => i.CheckIfInProject());
                _dependencyTypes = string.Join(", ", _info.Dependencies
                    .OrderBy(f => f.Type).GroupBy(f => f.Type)
                    .Select(g => g.Count() + " " + g.Key + " (" + EditorUtility.FormatBytes(g.Sum(f => f.Size)) + ")"));
            }

            // Mark graph for rebuild
            _graphNeedsRebuild = true;

            // Build virtualization caches
            RebuildListViewCache();
        }

        private void RebuildListViewCache()
        {
            if (_info?.Dependencies == null)
            {
                _listViewEntries = null;
                return;
            }

            // Build lookup dictionaries
            _scriptDependencyIds = new HashSet<int>(_info.ScriptDependencies?.Select(f => f.Id) ?? Enumerable.Empty<int>());
            _crossPackageDict = _info.CrossPackageDependencies?.ToDictionary(a => a.Id) ?? new Dictionary<int, Asset>();

            // Pre-build all entries with cached display strings
            _listViewEntries = new List<ListViewEntry>(_info.Dependencies.Count + _crossPackageDict.Count + 1);

            int curAssetId = -1;
            Asset mainAsset = _info.ToAsset();
            int srpSupportId = _info.SRPSupportPackage?.Id ?? -1;

            foreach (AssetFile file in _info.Dependencies)
            {
                // Add header when asset changes
                if (file.AssetId != curAssetId)
                {
                    curAssetId = file.AssetId;
                    Asset curAsset;
                    if (!_crossPackageDict.TryGetValue(curAssetId, out curAsset))
                    {
                        curAsset = mainAsset;
                    }

                    _listViewEntries.Add(new ListViewEntry
                    {
                        IsHeader = true,
                        HeaderText = !string.IsNullOrWhiteSpace(curAsset.DisplayName) ? curAsset.DisplayName : curAsset.SafeName,
                        CalculatedHeight = EditorGUIUtility.singleLineHeight + 6 // Header height
                    });
                }

                // Add file entry with pre-computed display text
                bool fromSupport = srpSupportId == file.AssetId;
                string displayText = file.Path + " (" + EditorUtility.FormatBytes(file.Size) + (fromSupport ? ", SRP Override" : "") + ")";

                _listViewEntries.Add(new ListViewEntry
                {
                    IsHeader = false,
                    File = file,
                    DisplayText = displayText,
                    IsScriptDependency = _scriptDependencyIds.Contains(file.Id),
                    FromSRPSupport = fromSupport,
                    CalculatedHeight = EditorGUIUtility.singleLineHeight + 2 // Default, will be recalculated
                });
            }

            _heightsNeedRecalculation = true;
        }

        private void RecalculateEntryHeights(float labelWidth)
        {
            if (_listViewEntries == null || labelWidth <= 0) return;

            // Ensure styles are initialized
            if (_yellowLabelStyle == null)
            {
                _yellowLabelStyle = new GUIStyle(EditorStyles.wordWrappedLabel);
                _yellowLabelStyle.normal.textColor = Color.yellow;
            }

            float headerHeight = EditorGUIUtility.singleLineHeight + 6;

            for (int i = 0; i < _listViewEntries.Count; i++)
            {
                ListViewEntry entry = _listViewEntries[i];
                if (entry.IsHeader)
                {
                    entry.CalculatedHeight = headerHeight;
                }
                else
                {
                    GUIStyle style = entry.IsScriptDependency ? _yellowLabelStyle : EditorStyles.wordWrappedLabel;
                    GUIContent content = new GUIContent(entry.DisplayText);
                    entry.CalculatedHeight = style.CalcHeight(content, labelWidth) + 2; // +2 for padding
                }
                _listViewEntries[i] = entry; // Write back since struct
            }

            _heightsNeedRecalculation = false;
        }

        private void OnEnable()
        {
            // Handle domain reload - try to restore state
            if (_serializedAssetInfoId != 0 && _info == null)
            {
                // Try to reload the asset info
                // This would need access to the asset database/cache
                // For now, just mark that we need to rebuild
                _graphNeedsRebuild = true;
            }
        }

        public override void OnGUI()
        {
            // Check if we need to reinitialize after domain reload
            if (_info == null && _serializedAssetInfoId != 0)
            {
                EditorGUILayout.HelpBox("Asset data was lost during script recompile. Please reopen the dependencies window.", MessageType.Warning);
                if (GUILayout.Button("Close Window"))
                {
                    Close();
                }
                return;
            }

            if (_info == null || _info.Id == 0)
            {
                EditorGUILayout.HelpBox("Select an asset and trigger the dependency scan to see its dependencies broken down here.", MessageType.Warning);
                return;
            }

            EditorGUILayout.LabelField($"'{_info.FileName}' in asset '{_info.GetDisplayName()}'", EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space();

            // View mode toggle
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            if (GUILayout.Toggle(_viewMode == ViewMode.Graph, "Graph View", EditorStyles.miniButtonRight, GUILayout.Width(80)))
            {
                if (_viewMode != ViewMode.Graph)
                {
                    _viewMode = ViewMode.Graph;
                    InitializeGraph();
                    _needsInitialFrame = true; // Frame view when switching to graph
                }
            }
            if (GUILayout.Toggle(_viewMode == ViewMode.List, "List View", EditorStyles.miniButtonLeft, GUILayout.Width(80)))
            {
                _viewMode = ViewMode.List;
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            EditorGUILayout.Space();

            EditorGUILayout.BeginVertical("box");
            int labelWidth = 130;
            string dependencyCountText = IndexUI.FormatDependencyCount(_info, false);
            if (_info.CrossPackageDependencies.Count > 1)
            {
                GUILabelWithTextNoMax("Dependencies", $"{dependencyCountText} across {_info.CrossPackageDependencies.Count + 1:N0} packages", labelWidth);
            }
            else
            {
                GUILabelWithTextNoMax("Dependencies", dependencyCountText, labelWidth);
            }

            if (IndexUI.IsIncompleteDependencyResult(_info))
            {
                EditorGUILayout.HelpBox("Dependency analysis did not complete. The list and graph show only dependencies found before the scan stopped.", MessageType.Warning);
            }

            if (_info.SRPSupportPackage != null && _info.SRPSupportPackage.Id > 0)
            {
                GUILabelWithTextNoMax("SRP Support", _info.SRPSupportPackage.DisplayName, labelWidth);
            }

            if (ShowAdvanced()) GUILabelWithTextNoMax("File Types", _dependencyTypes, labelWidth, null, true);
            GUILabelWithTextNoMax("Asset Size", EditorUtility.FormatBytes(_info.Size), labelWidth);
            GUILabelWithTextNoMax("Dependencies Size", EditorUtility.FormatBytes(_info.Dependencies.Sum(f => f.Size)), labelWidth);

            if (_info.Dependencies.Any(f => f.InProject))
            {
                GUILabelWithTextNoMax("Remaining", EditorUtility.FormatBytes(_info.Dependencies.Where(f => !f.InProject).Sum(f => f.Size)), labelWidth);
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space();

            // Render based on view mode
            if (_viewMode == ViewMode.Graph)
            {
                RenderGraphView();
            }
            else
            {
                RenderListView();
            }
        }

        private void RenderListView()
        {
            if (_listViewEntries == null || _listViewEntries.Count == 0)
            {
                EditorGUILayout.HelpBox("No dependencies to display.", MessageType.Info);
                return;
            }

            // Cache icons and styles once
            if (_iconInstalled == null) _iconInstalled = EditorGUIUtility.IconContent("Installed", "|Already in project");
            if (_iconImport == null) _iconImport = EditorGUIUtility.IconContent("Import", "|Needs to be imported");
            if (_yellowLabelStyle == null)
            {
                _yellowLabelStyle = new GUIStyle(EditorStyles.wordWrappedLabel);
                _yellowLabelStyle.normal.textColor = Color.yellow;
            }

            // Row dimensions
            float headerHeight = EditorGUIUtility.singleLineHeight + 6; // Space + label

            // Check if we need to recalculate heights based on available width
            // Use a temporary rect to get the current width before the scroll view
            Rect tempRect = GUILayoutUtility.GetRect(0, 0, GUILayout.ExpandWidth(true));
            float currentLabelWidth = tempRect.width - 22; // Subtract icon width
            if (currentLabelWidth > 0 && (Mathf.Abs(currentLabelWidth - _lastLabelWidth) > 1f || _heightsNeedRecalculation))
            {
                _lastLabelWidth = currentLabelWidth;
                RecalculateEntryHeights(_lastLabelWidth);
            }

            // Calculate total height using cached heights
            float totalHeight = 0;
            foreach (ListViewEntry entry in _listViewEntries)
            {
                totalHeight += entry.CalculatedHeight;
            }

            _scrollPos = GUILayout.BeginScrollView(_scrollPos, false, false, GUI.skin.horizontalScrollbar, GUI.skin.verticalScrollbar, GUILayout.ExpandWidth(true));

            // Get visible rect for culling
            Rect visibleRect = CommonUIStyles.GetCurrentVisibleRect();
            float viewTop = _scrollPos.y;
            float viewBottom = viewTop + visibleRect.height;

            // Reserve total space
            Rect totalRect = GUILayoutUtility.GetRect(0, totalHeight, GUILayout.ExpandWidth(true));

            // Render only visible entries
            float currentY = totalRect.y;
            for (int i = 0; i < _listViewEntries.Count; i++)
            {
                ListViewEntry entry = _listViewEntries[i];
                float entryHeight = entry.CalculatedHeight;
                float entryBottom = currentY + entryHeight;

                // Check if entry is visible
                bool isVisible = (entryBottom >= totalRect.y + viewTop) && (currentY <= totalRect.y + viewBottom);

                if (isVisible)
                {
                    if (entry.IsHeader)
                    {
                        // Draw header
                        Rect headerRect = new Rect(totalRect.x, currentY + 4, totalRect.width, EditorGUIUtility.singleLineHeight);
                        GUI.Label(headerRect, entry.HeaderText, EditorStyles.miniLabel);
                    }
                    else
                    {
                        // Draw file entry
                        Rect iconRect = new Rect(totalRect.x, currentY, 20, entryHeight);
                        Rect labelRect = new Rect(totalRect.x + 22, currentY, totalRect.width - 22, entryHeight);

                        GUI.Label(iconRect, entry.File.InProject ? _iconInstalled : _iconImport);
                        string tooltip = !string.IsNullOrEmpty(entry.File.Guid) ? entry.File.Guid : $"{entry.File.AssetId}_{entry.File.Path}";
                        GUI.Label(labelRect, new GUIContent(entry.DisplayText, tooltip),
                            entry.IsScriptDependency ? _yellowLabelStyle : EditorStyles.wordWrappedLabel);
                    }
                }

                currentY = entryBottom;
            }

            GUILayout.EndScrollView();
        }
    }
}
