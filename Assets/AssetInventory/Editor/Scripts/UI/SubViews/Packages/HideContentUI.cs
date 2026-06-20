using System.Collections.Generic;
using System.Linq;
using ImpossibleRobert.Common;
using UnityEditor;
using UnityEngine;
#pragma warning disable CS0618 // Type or member is obsolete
#if UNITY_6000_2_OR_NEWER
using BaseTreeViewState = UnityEditor.IMGUI.Controls.TreeViewState<int>;
#else
using BaseTreeViewState = UnityEditor.IMGUI.Controls.TreeViewState;
#endif

namespace AssetInventory
{
    public class HideContentUI : BasicEditorUI
    {
        private static readonly string[] RuleModeOptions = {"Hide Rules", "Include Rules"};

        private AssetInfo _info;
        private string _displayName;
        private bool _initialized;

        private FileTreeViewControl _treeView;
        private BaseTreeViewState _treeViewState;
        private Dictionary<string, FileTreeElement> _pathToElementMap;

        private string _exclusionRules = "";
        private List<string> _rules = new List<string>();
        private List<string> _manualRules = new List<string>();
        private HideRuleMode _ruleMode = HideRuleMode.Hide;
        private string _newRule = "";
        private float _splitterPos = 0.55f;
        private Vector2 _rulesScrollPos;

        public static HideContentUI ShowWindow()
        {
            HideContentUI window = GetWindow<HideContentUI>("Hide Package Content");
            window.minSize = new Vector2(700, 450);
            return window;
        }

        public void Init(AssetInfo info)
        {
            _info = info;
            _displayName = info.GetDisplayName();
            _initialized = false;
            BuildTree();
        }

        internal void InitForTests(AssetInfo info, List<string> rules)
        {
            InitForTests(info, HideRuleMode.Hide, rules);
        }

        internal void InitForTests(AssetInfo info, HideRuleMode ruleMode, List<string> rules)
        {
            _info = info;
            _displayName = info.GetDisplayName();
            _ruleMode = ruleMode;
            _rules = rules != null ? new List<string>(rules) : new List<string>();
            _manualRules = new List<string>();
            SyncRulesString();
        }

        internal void SaveExclusionRulesForTests()
        {
            SaveExclusionRules();
        }

        internal void InitSelectionForTests(AssetInfo info, HideRuleMode ruleMode, Dictionary<string, FileTreeElement> pathToElementMap)
        {
            InitSelectionForTests(info, ruleMode, pathToElementMap, null);
        }

        internal void InitSelectionForTests(AssetInfo info, HideRuleMode ruleMode, Dictionary<string, FileTreeElement> pathToElementMap, List<string> rules)
        {
            _info = info;
            _displayName = info.GetDisplayName();
            _ruleMode = ruleMode;
            SetStoredRules(rules);
            _pathToElementMap = pathToElementMap;
            _initialized = true;
            SyncRulesString();
        }

        internal void SwitchRuleModeForTests(HideRuleMode newMode)
        {
            SwitchRuleMode(newMode);
        }

        internal List<string> GetRulesForTests()
        {
            return new List<string>(_rules);
        }

        internal List<string> GetManualRulesForTests()
        {
            return new List<string>(_manualRules);
        }

        internal void RefreshRulesFromSelectionForTests()
        {
            RefreshRulesFromTreeSelection();
        }

        internal void ApplyPatternMarkingsForTests()
        {
            ApplyPatternMarkings();
        }

        internal void ApplyLoadedRulesForTests()
        {
            ApplyManualRuleSelection();
            ApplyPatternMarkings();
        }

        private void BuildTree()
        {
            List<AssetFile> files = DBAdapter.DB.Query<AssetFile>("SELECT * FROM AssetFile WHERE AssetId=?", _info.AssetId);
            if (files.Count == 0)
            {
                _initialized = true;
                return;
            }

            if (_treeViewState == null) _treeViewState = new BaseTreeViewState();

            FileTreeBuilder.Result result = FileTreeBuilder.Build(files, _treeViewState);
            _treeView = result.TreeView;
            _treeView.CheckStateChanged += RefreshRulesFromTreeSelection;
            _pathToElementMap = result.PathToElementMap;

            // Load current hidden state: deselect hidden files
            HashSet<string> hiddenPaths = new HashSet<string>(
                files.Where(f => f.Hidden).Select(f => f.Path));

            foreach (KeyValuePair<string, FileTreeElement> kvp in _pathToElementMap)
            {
                if (hiddenPaths.Contains(kvp.Key))
                {
                    kvp.Value.IsSelected = false;
                }
            }

            // Load exclusion rules from metadata
            LoadExclusionRules();

            if (_ruleMode == HideRuleMode.Include)
            {
                SetSelectionToModeDefault();
            }

            ApplyManualRuleSelection();

            // Apply pattern markings
            ApplyPatternMarkings();

            _initialized = true;
        }

        private void LoadExclusionRules()
        {
            Dictionary<int, HideRuleSet> hideRuleSets = Metadata.GetHideRuleSets();
            if (hideRuleSets.TryGetValue(_info.AssetId, out HideRuleSet ruleSet))
            {
                _ruleMode = ruleSet.Mode;
                SetStoredRules(ruleSet.Rules);
            }
            else
            {
                _ruleMode = HideRuleMode.Hide;
                _rules = new List<string>();
                _manualRules = new List<string>();
            }
            SyncRulesString();
        }

        private void SetStoredRules(IEnumerable<string> rules)
        {
            _rules = new List<string>();
            _manualRules = new List<string>();

            foreach (string rule in HideRuleSet.NormalizeRules(rules))
            {
                if (IsManualTreeRule(rule))
                {
                    _manualRules.Add(rule);
                }
                else
                {
                    _rules.Add(rule);
                }
            }
        }

        private static bool IsManualTreeRule(string rule)
        {
            if (string.IsNullOrWhiteSpace(rule)) return false;

            string trimmedRule = rule.Trim();
            if (trimmedRule.StartsWith("*", System.StringComparison.Ordinal)) return false;
            return trimmedRule.EndsWith("/", System.StringComparison.Ordinal) || trimmedRule.Contains("/");
        }

        private void SyncRulesString()
        {
            _exclusionRules = string.Join("\n", _rules);
        }

        private void ApplyPatternMarkings()
        {
            if (_pathToElementMap == null) return;

            // Reset auto-exclusion state
            foreach (KeyValuePair<string, FileTreeElement> kvp in _pathToElementMap)
            {
                kvp.Value.IsAutoExcluded = false;
                kvp.Value.IsAutoIncluded = false;
            }

            foreach (string pattern in _rules)
            {
                if (string.IsNullOrWhiteSpace(pattern)) continue;

                foreach (KeyValuePair<string, FileTreeElement> kvp in _pathToElementMap)
                {
                    bool matchedByRule = MatchesPattern(kvp.Key, pattern);
                    bool ancestorOfIncludeFolderRule = !matchedByRule
                        && _ruleMode == HideRuleMode.Include
                        && kvp.Value.IsFolder
                        && IsAncestorOfIncludeFolderRule(kvp.Key, pattern);

                    if (kvp.Value.IsFolder && _ruleMode != HideRuleMode.Include) continue;
                    if (matchedByRule || ancestorOfIncludeFolderRule)
                    {
                        if (_ruleMode == HideRuleMode.Include)
                        {
                            kvp.Value.IsAutoIncluded = matchedByRule;
                            kvp.Value.IsSelected = true;
                        }
                        else
                        {
                            kvp.Value.IsAutoExcluded = true;
                            kvp.Value.IsSelected = false;
                        }
                    }
                }
            }
        }

        private bool IsMatchedByAnyRule(string path)
        {
            foreach (string pattern in _rules)
            {
                if (!string.IsNullOrWhiteSpace(pattern) && MatchesPattern(path, pattern)) return true;
            }
            return false;
        }

        private void ApplyManualRuleSelection()
        {
            if (_pathToElementMap == null) return;

            bool selected = _ruleMode == HideRuleMode.Include;
            foreach (string rule in _manualRules)
            {
                if (string.IsNullOrWhiteSpace(rule)) continue;

                foreach (KeyValuePair<string, FileTreeElement> kvp in _pathToElementMap)
                {
                    bool matchedByRule = MatchesPattern(kvp.Key, rule);
                    bool ancestorOfIncludeFolderRule = !matchedByRule
                        && selected
                        && kvp.Value.IsFolder
                        && IsAncestorOfIncludeFolderRule(kvp.Key, rule);

                    if (matchedByRule || ancestorOfIncludeFolderRule)
                    {
                        kvp.Value.IsSelected = selected;
                    }
                }
            }
        }

        private List<string> GetManuallyHiddenPaths()
        {
            return GetPathsMatchingSelection(false, true);
        }

        private List<string> GetManuallyIncludedPaths()
        {
            return GetPathsMatchingSelection(true, true);
        }

        private List<string> GetRulesFromCurrentSelection(HideRuleMode mode)
        {
            return GetPathsMatchingSelection(mode == HideRuleMode.Include, false);
        }

        private List<string> GetManualRulesFromCurrentSelection()
        {
            return _ruleMode == HideRuleMode.Include ? GetManuallyIncludedPaths() : GetManuallyHiddenPaths();
        }

        private List<string> GetPathsMatchingSelection(bool selected, bool skipRuleControlled)
        {
            if (_pathToElementMap == null) return new List<string>();

            // Collect all target file paths.
            HashSet<string> targetFiles = new HashSet<string>();
            foreach (KeyValuePair<string, FileTreeElement> kvp in _pathToElementMap)
            {
                if (kvp.Value.IsFolder) continue;
                if (skipRuleControlled && IsRuleControlled(kvp.Value)) continue;
                if (kvp.Value.IsSelected == selected)
                {
                    targetFiles.Add(kvp.Key);
                }
            }

            if (targetFiles.Count == 0) return new List<string>();

            // Group files by folder and check if entire folders share the target selection state.
            Dictionary<string, List<string>> folderToAllFiles = new Dictionary<string, List<string>>();
            foreach (KeyValuePair<string, FileTreeElement> kvp in _pathToElementMap)
            {
                if (kvp.Value.IsFolder) continue;
                if (skipRuleControlled && IsRuleControlled(kvp.Value)) continue;

                string folder = GetParentFolder(kvp.Key);
                if (folder == null) continue;

                if (!folderToAllFiles.TryGetValue(folder, out List<string> list))
                {
                    list = new List<string>();
                    folderToAllFiles[folder] = list;
                }
                list.Add(kvp.Key);
            }

            // Find folders where all relevant files share the target state.
            // Only collapse if subfolders also have no files outside the target state.
            HashSet<string> collapsedFolders = new HashSet<string>();
            foreach (KeyValuePair<string, List<string>> kvp in folderToAllFiles)
            {
                if (!kvp.Value.All(f => targetFiles.Contains(f))) continue;

                bool hasSelectedSubFiles = folderToAllFiles.Any(other =>
                    other.Key != kvp.Key && other.Key.StartsWith(kvp.Key + "/") &&
                    other.Value.Any(f => !targetFiles.Contains(f)));
                if (!hasSelectedSubFiles)
                {
                    collapsedFolders.Add(kvp.Key);
                }
            }

            // Collapse upward: if all child folders of a parent are collapsed, collapse the parent
            bool changed = true;
            while (changed)
            {
                changed = false;
                Dictionary<string, List<string>> parentToChildren = new Dictionary<string, List<string>>();
                foreach (string folder in collapsedFolders)
                {
                    string parent = GetParentFolder(folder);
                    if (parent == null) continue;

                    if (!parentToChildren.TryGetValue(parent, out List<string> children))
                    {
                        children = new List<string>();
                        parentToChildren[parent] = children;
                    }
                    children.Add(folder);
                }

                foreach (KeyValuePair<string, List<string>> kvp in parentToChildren)
                {
                    // Check parent has no direct relevant files outside collapsed children.
                    bool hasUncoveredFiles = folderToAllFiles.TryGetValue(kvp.Key, out List<string> directFiles)
                        && directFiles.Any(f => !targetFiles.Contains(f));
                    if (hasUncoveredFiles) continue;

                    // Check all child folders of this parent are collapsed
                    List<string> allChildFolders = collapsedFolders.Where(f => GetParentFolder(f) == kvp.Key).ToList();
                    // Also check there are no selected child folders with files
                    bool allChildrenCollapsed = folderToAllFiles.Keys
                        .Where(f => GetParentFolder(f) == kvp.Key)
                        .All(f => collapsedFolders.Contains(f));

                    if (allChildrenCollapsed && allChildFolders.Count > 0)
                    {
                        collapsedFolders.Add(kvp.Key);
                        foreach (string child in allChildFolders)
                        {
                            collapsedFolders.Remove(child);
                        }
                        changed = true;
                        break;
                    }
                }
            }

            // Build result: collapsed folders + individual files not covered by collapsed folders
            List<string> result = new List<string>();
            foreach (string folder in collapsedFolders.OrderBy(f => f))
            {
                result.Add(folder + "/");
            }
            foreach (string file in targetFiles.OrderBy(f => f))
            {
                bool coveredByFolder = collapsedFolders.Any(f => file.StartsWith(f + "/") || file.StartsWith(f));
                if (!coveredByFolder)
                {
                    result.Add(file);
                }
            }
            return HideRuleSet.NormalizeRules(result);
        }

        private bool IsRuleControlled(FileTreeElement element)
        {
            return _ruleMode == HideRuleMode.Include ? element.IsAutoIncluded : element.IsAutoExcluded;
        }

        private static string GetParentFolder(string path)
        {
            int lastSlash = path.LastIndexOf('/');
            return lastSlash > 0 ? path.Substring(0, lastSlash) : null;
        }

        private void SetSelectionToModeDefault()
        {
            if (_pathToElementMap == null) return;

            bool selected = _ruleMode == HideRuleMode.Hide;
            foreach (KeyValuePair<string, FileTreeElement> kvp in _pathToElementMap)
            {
                kvp.Value.IsSelected = selected;
            }
        }

        private static bool IsAncestorOfIncludeFolderRule(string folderPath, string pattern)
        {
            if (string.IsNullOrEmpty(folderPath) || string.IsNullOrWhiteSpace(pattern)) return false;

            string trimmedPattern = pattern.Trim();
            if (!trimmedPattern.EndsWith("/", System.StringComparison.Ordinal)) return false;

            string normalizedFolder = folderPath.TrimEnd('/');
            string ruleFolder = trimmedPattern.TrimEnd('/');
            return ruleFolder.StartsWith(normalizedFolder + "/", System.StringComparison.Ordinal);
        }

        private void SwitchRuleMode(HideRuleMode newMode)
        {
            if (_ruleMode == newMode) return;

            _rules = new List<string>();
            _manualRules = GetRulesFromCurrentSelection(newMode);
            _ruleMode = newMode;
            _newRule = "";
            SyncRulesString();
            ApplyPatternMarkings();
            if (Event.current != null) GUI.FocusControl(null);
        }

        private void RefreshRulesFromTreeSelection()
        {
            _manualRules = GetManualRulesFromCurrentSelection();
            _newRule = "";
            SyncRulesString();
            ApplyPatternMarkings();
            Repaint();
        }

        private List<string> BuildRulesForApply()
        {
            List<string> result = HideRuleSet.NormalizeRules(_rules);
            _manualRules = _pathToElementMap == null ? HideRuleSet.NormalizeRules(_manualRules) : GetManualRulesFromCurrentSelection();

            foreach (string rule in _manualRules)
            {
                if (!result.Contains(rule)) result.Add(rule);
            }
            return HideRuleSet.NormalizeRules(result);
        }

        private void ReactivateAfterRuleChange()
        {
            if (_pathToElementMap == null) return;

            // Update files that were previously controlled by rules and no longer match.
            foreach (KeyValuePair<string, FileTreeElement> kvp in _pathToElementMap)
            {
                if (kvp.Value.IsFolder) continue;
                if (_ruleMode == HideRuleMode.Include)
                {
                    if (kvp.Value.IsAutoIncluded && !IsMatchedByAnyRule(kvp.Key))
                    {
                        kvp.Value.IsSelected = false;
                    }
                }
                else if (kvp.Value.IsAutoExcluded && !IsMatchedByAnyRule(kvp.Key))
                {
                    kvp.Value.IsSelected = true;
                }
            }
            ApplyPatternMarkings();
            _manualRules = GetManualRulesFromCurrentSelection();
        }

        private bool MatchesPattern(string path, string pattern)
        {
            return HideRuleSet.MatchesPattern(path, pattern);
        }

        public override void OnGUI()
        {
            base.OnGUI();

            if (_info == null)
            {
                EditorGUILayout.HelpBox("No package selected.", MessageType.Info);
                return;
            }

            if (!_initialized)
            {
                EditorGUILayout.HelpBox("Loading...", MessageType.Info);
                return;
            }

            if (_treeView == null)
            {
                EditorGUILayout.HelpBox("No indexed files found for this package.", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField($"Package: {_displayName}", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            int selectedMode = GUILayout.Toolbar((int)_ruleMode, RuleModeOptions);
            if (EditorGUI.EndChangeCheck())
            {
                SwitchRuleMode((HideRuleMode)selectedMode);
            }

            string helpText = _ruleMode == HideRuleMode.Include
                ? "Checked files remain visible in search. Include rules on the right will automatically keep matching files visible and hide all other files."
                : "Checked files remain visible in search. Hide rules on the right will automatically hide matching files.";
            EditorGUILayout.HelpBox(helpText, MessageType.Info);

            EditorGUILayout.Space();

            float leftWidth = Mathf.Floor(position.width * _splitterPos);

            // Horizontal split
            GUILayout.BeginHorizontal();

            // Left pane: file tree
            GUILayout.BeginVertical(GUILayout.Width(leftWidth));
            EditorGUILayout.LabelField("Files", EditorStyles.boldLabel);
            Rect treeRect = GUILayoutUtility.GetRect(0, 10000, 0, 10000);
            _treeView.OnGUI(treeRect);
            GUILayout.EndVertical();

            // Splitter
            int splitterControlId = GUIUtility.GetControlID(FocusType.Passive);
            Rect splitterRect = GUILayoutUtility.GetRect(4, 4, 0, 10000, GUILayout.Width(4));
            EditorGUIUtility.AddCursorRect(splitterRect, MouseCursor.ResizeHorizontal);
            if (Event.current.type == EventType.MouseDown && splitterRect.Contains(Event.current.mousePosition))
            {
                GUIUtility.hotControl = splitterControlId;
                Event.current.Use();
            }
            if (GUIUtility.hotControl == splitterControlId)
            {
                if (Event.current.type == EventType.MouseDrag)
                {
                    _splitterPos = Mathf.Clamp(Event.current.mousePosition.x / position.width, 0.2f, 0.8f);
                    Event.current.Use();
                    Repaint();
                }
                else if (Event.current.type == EventType.MouseUp)
                {
                    GUIUtility.hotControl = 0;
                    Event.current.Use();
                }
            }

            // Right pane: exclusion rules + manually hidden
            GUILayout.BeginVertical();

            string ruleTitle = _ruleMode == HideRuleMode.Include ? "Include Rules" : "Hide Rules";
            EditorGUILayout.LabelField(ruleTitle, EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Patterns: *.ext, folder, path/segment", EditorStyles.miniLabel);

            _rulesScrollPos = EditorGUILayout.BeginScrollView(_rulesScrollPos);

            int deleteIdx = -1;
            for (int i = 0; i < _rules.Count; i++)
            {
                GUILayout.BeginHorizontal();
                EditorGUI.BeginChangeCheck();
                _rules[i] = EditorGUILayout.TextField(_rules[i]);
                if (EditorGUI.EndChangeCheck())
                {
                    SyncRulesString();
                    ReactivateAfterRuleChange();
                }
                if (GUILayout.Button(EditorGUIUtility.IconContent("TreeEditor.Trash", "|Remove rule"), GUILayout.Width(28), GUILayout.Height(18)))
                {
                    deleteIdx = i;
                }
                GUILayout.EndHorizontal();
            }

            if (deleteIdx >= 0)
            {
                _rules.RemoveAt(deleteIdx);
                SyncRulesString();
                ReactivateAfterRuleChange();
            }

            // Add new rule
            bool addRule = false;

            // Check Return key BEFORE TextField consumes the event
            if (Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                && GUI.GetNameOfFocusedControl() == "NewRuleField"
                && !string.IsNullOrWhiteSpace(_newRule))
            {
                addRule = true;
                Event.current.Use();
            }

            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("NewRuleField");
            _newRule = EditorGUILayout.TextField(_newRule);
            if (GUILayout.Button("Add", GUILayout.Width(40)) && !string.IsNullOrWhiteSpace(_newRule))
            {
                addRule = true;
            }
            GUILayout.EndHorizontal();

            if (addRule)
            {
                _rules.Add(_newRule.Trim());
                _newRule = "";
                SyncRulesString();
                ReactivateAfterRuleChange();
                GUI.FocusControl(null);
            }

            // Manual tree selection section
            List<string> manualPaths = _manualRules;
            if (manualPaths.Count > 0)
            {
                EditorGUILayout.Space();
                string manualTitle = _ruleMode == HideRuleMode.Include ? "Manually Included" : "Manually Hidden";
                EditorGUILayout.LabelField($"{manualTitle} ({manualPaths.Count})", EditorStyles.boldLabel);
                EditorGUI.BeginDisabledGroup(true);
                foreach (string path in manualPaths)
                {
                    EditorGUILayout.LabelField(path, EditorStyles.miniLabel);
                }
                EditorGUI.EndDisabledGroup();
            }

            EditorGUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();

            // Bottom buttons
            EditorGUILayout.Space();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Apply", CommonUIStyles.mainButton, GUILayout.Width(120), GUILayout.Height(30)))
            {
                ApplyChanges();
            }
            if (GUILayout.Button("Cancel", GUILayout.Width(80), GUILayout.Height(30)))
            {
                Close();
            }
            GUILayout.EndHorizontal();
            EditorGUILayout.Space();
        }

        private void ApplyChanges()
        {
            List<string> rulesForApply = BuildRulesForApply();
            if (_ruleMode == HideRuleMode.Include && rulesForApply.Count == 0)
            {
                bool applyEmptyInclude = EditorUtility.DisplayDialog(
                    "Include Rules Hide All Files",
                    "Include mode has no rules. Applying will hide every indexed file in this package.",
                    "Apply",
                    "Cancel");
                if (!applyEmptyInclude) return;
            }

            SyncRulesString();

            SaveExclusionRules(rulesForApply);
            Assets.ApplyHidePatternsFromScratch(_info.AssetId);

            Close();
        }

        private void SaveExclusionRules()
        {
            SaveExclusionRules(BuildRulesForApply());
        }

        private void SaveExclusionRules(List<string> rules)
        {
            string serializedRules = HideRuleSet.Serialize(_ruleMode, rules);

            // Find or create the Hide metadata assignment
            List<MetadataInfo> metadata = Metadata.GetPackageMetadata(_info.AssetId);
            MetadataInfo hideMetadata = metadata?.FirstOrDefault(m => m.Name == MetadataDefinition.FIELD_HIDE);

            if (_ruleMode == HideRuleMode.Hide && string.IsNullOrEmpty(serializedRules))
            {
                if (hideMetadata != null)
                {
                    Metadata.RemoveAssignment(_info, hideMetadata);
                }
            }
            else
            {
                if (hideMetadata != null)
                {
                    hideMetadata.StringValue = serializedRules;
                    DBAdapter.DB.Update(hideMetadata.ToAssignment());
                }
                else
                {
                    // Find the Hide definition
                    List<MetadataDefinition> defs = Metadata.LoadDefinitions();
                    MetadataDefinition hideDef = defs.FirstOrDefault(d => d.Name == MetadataDefinition.FIELD_HIDE);
                    if (hideDef != null)
                    {
                        Metadata.AddAssignment(_info, hideDef.Id, MetadataAssignment.Target.Package);
                        // Reload and set value
                        metadata = Metadata.GetPackageMetadata(_info.AssetId);
                        hideMetadata = metadata?.FirstOrDefault(m => m.Name == MetadataDefinition.FIELD_HIDE);
                        if (hideMetadata != null)
                        {
                            hideMetadata.StringValue = serializedRules;
                            DBAdapter.DB.Update(hideMetadata.ToAssignment());
                        }
                    }
                }
            }
        }
    }
}
