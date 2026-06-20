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
    internal sealed class HierarchyTreeViewControl : TreeViewWithTreeModel<HierarchyTreeElement>
    {
        public HierarchyTreeViewControl(BaseTreeViewState state, TreeModel<HierarchyTreeElement> model) : base(state, model)
        {
            showAlternatingRowBackgrounds = true;
            showBorder = true;
            rowHeight = 20f;
            Reload();
        }

        protected override void RowGUI(RowGUIArgs args)
        {
            TreeViewItem<HierarchyTreeElement> item = args.item as TreeViewItem<HierarchyTreeElement>;
            if (item == null)
            {
                base.RowGUI(args);
                return;
            }

            HierarchyTreeElement data = item.Data;

            Rect rowRect = args.rowRect;
            float indent = GetContentIndent(args.item);

            Rect labelRect = new Rect(rowRect.x + indent, rowRect.y, rowRect.width - indent - 50, rowRect.height);
            Rect countRect = new Rect(rowRect.xMax - 45, rowRect.y, 40, rowRect.height);

            if (args.selected)
            {
                EditorGUI.DrawRect(rowRect, new Color(0.24f, 0.37f, 0.59f));
            }

            EditorGUI.LabelField(labelRect, data.TreeName);

            if (data.MatchCount > 0)
            {
                GUIStyle countStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleRight,
                    normal = { textColor = Color.gray }
                };
                EditorGUI.LabelField(countRect, $"({data.MatchCount})", countStyle);
            }
        }

        protected override bool CanMultiSelect(BaseTreeViewItem item)
        {
            return false;
        }

        protected override bool CanStartDrag(CanStartDragArgs args)
        {
            return false;
        }
    }
}
