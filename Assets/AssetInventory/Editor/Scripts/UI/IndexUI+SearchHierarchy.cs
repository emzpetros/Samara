using System;
using System.Collections.Generic;
using ImpossibleRobert.Common;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
#pragma warning disable CS0618 // Type or member is obsolete
#if UNITY_6000_2_OR_NEWER
using BaseTreeViewState = UnityEditor.IMGUI.Controls.TreeViewState<int>;
#else
using BaseTreeViewState = UnityEditor.IMGUI.Controls.TreeViewState;
#endif

namespace AssetInventory
{
    public partial class IndexUI
    {
        private Vector2 _leftSidebarScrollPos;

        private static readonly string[] _hierarchyTypes = {"File Path", "Category", "Publisher", "Package", "File Type"};
        private BaseTreeViewState _hierarchyTreeState;
        private HierarchyTreeViewControl _hierarchyTreeView;
        private TreeModel<HierarchyTreeElement> _hierarchyTreeModel;
        private bool _requireHierarchyRebuild;
        private string _activeHierarchyFilter;
        private string _activeHierarchyFilterValue;

        private void DrawLeftHierarchySidebar()
        {
            GUILayout.BeginVertical("box", GUILayout.Width(UIStyles.INSPECTOR_WIDTH));

            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUI.BeginChangeCheck();
            AI.Config.searchLeftSideBarHierarchy = EditorGUILayout.Popup(AI.Config.searchLeftSideBarHierarchy, _hierarchyTypes, EditorStyles.toolbarPopup);
            if (EditorGUI.EndChangeCheck())
            {
                AI.SaveConfig();
                _requireHierarchyRebuild = true;
            }

            if (!string.IsNullOrEmpty(_activeHierarchyFilter))
            {
                if (GUILayout.Button(CommonUIStyles.Content("✕", "Clear hierarchy filter"), EditorStyles.toolbarButton, GUILayout.Width(22)))
                {
                    ClearHierarchyFilter();
                }
            }
            GUILayout.EndHorizontal();

            if (_hierarchyTreeView == null || _requireHierarchyRebuild)
            {
                InitHierarchyTree();
                _requireHierarchyRebuild = false;
            }

            _leftSidebarScrollPos = GUILayout.BeginScrollView(_leftSidebarScrollPos, false, false);
            if (_hierarchyTreeView != null && _hierarchyTreeModel != null && _hierarchyTreeModel.Root != null)
            {
                Rect treeRect = GUILayoutUtility.GetRect(0, 10000, 0, 10000);
                _hierarchyTreeView.OnGUI(treeRect);
            }
            else
            {
                EditorGUILayout.HelpBox("No hierarchy data available. Perform a search first.", MessageType.Info);
            }
            GUILayout.EndScrollView();

            GUILayout.EndVertical();
        }

        private void InitHierarchyTree()
        {
            if (_hierarchyTreeState == null)
            {
                _hierarchyTreeState = new BaseTreeViewState();
            }

            List<HierarchyTreeElement> elements = BuildHierarchyElements();

            _hierarchyTreeModel = new TreeModel<HierarchyTreeElement>(elements);
            _hierarchyTreeView = new HierarchyTreeViewControl(_hierarchyTreeState, _hierarchyTreeModel);
            _hierarchyTreeView.OnSelectionChanged += OnHierarchySelectionChanged;
        }

        private List<HierarchyTreeElement> BuildHierarchyElements()
        {
            return HierarchyBuilder.Build(_files, AI.Config.searchLeftSideBarHierarchy);
        }

        private void OnHierarchySelectionChanged(IList<int> selectedIds)
        {
            if (selectedIds == null || selectedIds.Count == 0) return;

            HierarchyTreeElement element = _hierarchyTreeModel.Find(selectedIds[0]);
            if (element == null || string.IsNullOrEmpty(element.FilterKey)) return;

            ApplyHierarchyFilter(element);
        }

        private void ApplyHierarchyFilter(HierarchyTreeElement element)
        {
            _activeHierarchyFilter = element.FilterKey;
            _activeHierarchyFilterValue = element.FilterValue;

            switch (element.FilterKey)
            {
                case "Path":
                    _searchPhrase = $"=Path like '{element.FilterValue}%'";
                    _previousSearchPhrase = _searchPhrase;
                    break;

                case "Category":
                    _selectedCategory = FindIndexByValue(_categoryNames, element.FilterValue, splitPath: false);
                    break;

                case "Publisher":
                    _selectedPublisher = FindIndexByValue(_publisherNames, element.FilterValue, splitPath: true);
                    break;

                case "Package":
                    _selectedAsset = FindIndexByValue(_assetNames, element.FilterValue, splitPath: true);
                    break;

                case "Type":
                    int typeIdx = Array.FindIndex(_types, t => t.Equals(element.FilterValue, StringComparison.OrdinalIgnoreCase) ||
                        t.EndsWith("/" + element.FilterValue, StringComparison.OrdinalIgnoreCase));
                    if (typeIdx >= 0) AI.Config.searchType = typeIdx;
                    break;
            }

            _requireSearchUpdate = true;
            _curPage = 1;
        }

        private void ClearHierarchyFilter()
        {
            _activeHierarchyFilter = null;
            _activeHierarchyFilterValue = null;

            ResetSearch(true, false);
            _requireSearchUpdate = true;
        }
    }
}
