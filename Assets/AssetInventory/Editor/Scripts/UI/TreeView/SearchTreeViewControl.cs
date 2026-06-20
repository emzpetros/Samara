using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

#pragma warning disable CS0618 // Type or member is obsolete
#if UNITY_6000_2_OR_NEWER
using BaseTreeViewItem = UnityEditor.IMGUI.Controls.TreeViewItem<int>;
using BaseTreeViewState = UnityEditor.IMGUI.Controls.TreeViewState<int>;
#else
using BaseTreeViewItem = UnityEditor.IMGUI.Controls.TreeViewItem;
using BaseTreeViewState = UnityEditor.IMGUI.Controls.TreeViewState;
#endif

namespace AssetInventory
{
    internal sealed class SearchTreeViewControl : TreeViewWithTreeModel<AssetInfo>
    {
        private readonly IndexUI _indexUI;

        public enum Columns
        {
            FileName,
            Path,
            Type,
            Size,
            Package
        }

        private readonly List<int> _previousSelection = new List<int>();

        public SearchTreeViewControl(BaseTreeViewState state, MultiColumnHeader multiColumnHeader, TreeModel<AssetInfo> model, IndexUI indexUI) : base(state, multiColumnHeader, model)
        {
            _indexUI = indexUI;
            showAlternatingRowBackgrounds = true;
            showBorder = true;
            rowHeight = AI.Config.searchListRowHeight;
            extraSpaceBeforeIconAndLabel = rowHeight;

            Reload();
        }

        public override void OnGUI(Rect rect)
        {
            _previousSelection.Clear();
            _previousSelection.AddRange(state.selectedIDs);

            base.OnGUI(rect);
        }

        protected override void SingleClickedItem(int id)
        {
            if (Event.current.modifiers != EventModifiers.Control) return;

            if (_previousSelection.Contains(id))
            {
                state.selectedIDs.Remove(id);
                SetSelection(state.selectedIDs, TreeViewSelectionOptions.FireSelectionChanged);
            }
        }

        protected override IList<BaseTreeViewItem> BuildRows(BaseTreeViewItem root)
        {
            IList<BaseTreeViewItem> rows = base.BuildRows(root);
            return rows;
        }

        protected override bool CanStartDrag(CanStartDragArgs args)
        {
            return false;
        }

        protected override void RowGUI(RowGUIArgs args)
        {
            TreeViewItem<AssetInfo> item = (TreeViewItem<AssetInfo>)args.item;

            for (int i = 0; i < args.GetNumVisibleColumns(); ++i)
            {
                CellGUI(args.GetCellRect(i), item, (Columns)args.GetColumn(i), ref args);
            }
        }

        private void CellGUI(Rect cellRect, TreeViewItem<AssetInfo> item, Columns column, ref RowGUIArgs args)
        {
            CenterRectUsingSingleLineHeight(ref cellRect);

            if (item.Data == null) return;

            switch (column)
            {
                case Columns.FileName:
                    Rect rect = cellRect;
                    float iconSize = rowHeight - 4;
                    rect.width = iconSize;
                    rect.height = iconSize;
                    rect.y = cellRect.y + (cellRect.height - iconSize) / 2;

                    Texture2D filePreview = _indexUI?.GetFilePreview(item.Data.Id);
                    if (filePreview != null)
                    {
                        GUI.DrawTexture(rect, filePreview, ScaleMode.ScaleToFit);
                    }
                    else
                    {
                        Texture icon = item.Data.GetFallbackIcon();
                        if (icon != null) GUI.DrawTexture(rect, icon, ScaleMode.ScaleToFit);
                    }

                    args.rowRect = cellRect;
                    base.RowGUI(args);
                    break;

                case Columns.Path:
                    EditorGUI.LabelField(cellRect, item.Data.ShortPath);
                    break;

                case Columns.Type:
                    EditorGUI.LabelField(cellRect, item.Data.Type);
                    break;

                case Columns.Size:
                    if (item.Data.Size > 0)
                    {
                        EditorGUI.LabelField(cellRect, EditorUtility.FormatBytes(item.Data.Size));
                    }
                    break;

                case Columns.Package:
                    EditorGUI.LabelField(cellRect, item.Data.SafeName);
                    break;
            }
        }

        protected override bool CanMultiSelect(BaseTreeViewItem item)
        {
            return true;
        }

        public static MultiColumnHeaderState CreateDefaultMultiColumnHeaderState()
        {
            int[] defaultVisibleColumns = new[]
            {
                (int)Columns.FileName,
                (int)Columns.Path,
                (int)Columns.Type,
                (int)Columns.Size,
                (int)Columns.Package
            };

            MultiColumnHeaderState.Column[] columns =
            {
                new MultiColumnHeaderState.Column
                {
                    headerContent = new GUIContent("File Name"),
                    headerTextAlignment = TextAlignment.Left,
                    sortedAscending = true,
                    sortingArrowAlignment = TextAlignment.Center,
                    width = 250,
                    minWidth = 100,
                    autoResize = true,
                    allowToggleVisibility = false
                },
                new MultiColumnHeaderState.Column
                {
                    headerContent = new GUIContent("Path"),
                    contextMenuText = "Path",
                    headerTextAlignment = TextAlignment.Left,
                    canSort = true,
                    sortedAscending = true,
                    sortingArrowAlignment = TextAlignment.Right,
                    width = 200,
                    minWidth = 60,
                    autoResize = true,
                    allowToggleVisibility = true
                },
                new MultiColumnHeaderState.Column
                {
                    headerContent = new GUIContent("Type"),
                    contextMenuText = "Type",
                    headerTextAlignment = TextAlignment.Left,
                    canSort = true,
                    sortedAscending = true,
                    sortingArrowAlignment = TextAlignment.Right,
                    width = 80,
                    minWidth = 40,
                    autoResize = false,
                    allowToggleVisibility = true
                },
                new MultiColumnHeaderState.Column
                {
                    headerContent = new GUIContent("Size"),
                    contextMenuText = "Size",
                    headerTextAlignment = TextAlignment.Left,
                    canSort = true,
                    sortedAscending = true,
                    sortingArrowAlignment = TextAlignment.Right,
                    width = 70,
                    minWidth = 40,
                    autoResize = false,
                    allowToggleVisibility = true
                },
                new MultiColumnHeaderState.Column
                {
                    headerContent = new GUIContent("Package"),
                    contextMenuText = "Package",
                    headerTextAlignment = TextAlignment.Left,
                    canSort = true,
                    sortedAscending = true,
                    sortingArrowAlignment = TextAlignment.Right,
                    width = 150,
                    minWidth = 60,
                    autoResize = true,
                    allowToggleVisibility = true
                }
            };

            MultiColumnHeaderState state = new MultiColumnHeaderState(columns);
            state.visibleColumns = defaultVisibleColumns;
            return state;
        }
    }
}
