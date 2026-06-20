using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace AssetInventory
{
    internal sealed class ReorderableMultiColumnHeader : MultiColumnHeader
    {
        public event Action<int[]> columnOrderChanged;

        public ReorderableMultiColumnHeader(MultiColumnHeaderState state) : base(state)
        {
        }

        protected override void AddColumnHeaderContextMenuItems(GenericMenu menu)
        {
            // Find which column was right-clicked based on mouse position
            int clickedColumnIndex = GetColumnIndexAtMousePosition();

            if (clickedColumnIndex >= 0)
            {
                int visibleIndex = GetVisibleColumnIndexFor(clickedColumnIndex);

                // Don't allow moving the first column (Name) - it should stay locked
                bool isFirstColumn = visibleIndex == 0;
                bool isLastColumn = visibleIndex == state.visibleColumns.Length - 1;

                if (!isFirstColumn && visibleIndex > 1) // Can't move to position 0 (Name's spot)
                {
                    menu.AddItem(new GUIContent("Move Left"), false, () => MoveColumn(clickedColumnIndex, -1));
                }
                else
                {
                    menu.AddDisabledItem(new GUIContent("Move Left"));
                }

                if (!isFirstColumn && !isLastColumn)
                {
                    menu.AddItem(new GUIContent("Move Right"), false, () => MoveColumn(clickedColumnIndex, 1));
                }
                else
                {
                    menu.AddDisabledItem(new GUIContent("Move Right"));
                }
            }

            // Add toggle visibility items for each column
            menu.AddSeparator("");
            for (int i = 0; i < state.columns.Length; i++)
            {
                MultiColumnHeaderState.Column column = state.columns[i];
                if (!column.allowToggleVisibility) continue;

                string menuText = !string.IsNullOrEmpty(column.contextMenuText) ? column.contextMenuText : column.headerContent.text;
                int columnIndex = i;
                bool isVisible = IsColumnVisible(columnIndex);
                menu.AddItem(new GUIContent(menuText), isVisible, () => ToggleVisibility(columnIndex));
            }

            // Add custom Resize to Fit
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Resize to Fit"), false, SmartResizeToFit);
        }

        public void SmartResizeToFit()
        {
            float totalAvailable = 0;
            for (int i = 0; i < state.visibleColumns.Length; i++)
            {
                totalAvailable += state.columns[state.visibleColumns[i]].width;
            }
            if (totalAvailable <= 0) totalAvailable = 800;

            float usedByOthers = 0;
            int primaryColumnVisibleIndex = -1;

            for (int i = 0; i < state.visibleColumns.Length; i++)
            {
                int colIdx = state.visibleColumns[i];
                MultiColumnHeaderState.Column col = state.columns[colIdx];

                if (i == 0)
                {
                    primaryColumnVisibleIndex = i;
                    continue;
                }

                float targetWidth;
                if (col.maxWidth > 0 && col.maxWidth <= 80)
                {
                    // Checkmark-style column: use defined width
                    targetWidth = col.width > 0 ? Mathf.Min(col.width, col.maxWidth) : 60;
                }
                else
                {
                    // Text column: size to header text + padding
                    float headerWidth = EditorStyles.miniLabel.CalcSize(col.headerContent).x + 25;
                    targetWidth = Mathf.Max(headerWidth, col.minWidth);
                    if (col.maxWidth > 0) targetWidth = Mathf.Min(targetWidth, col.maxWidth);
                }

                state.columns[colIdx].width = targetWidth;
                usedByOthers += targetWidth;
            }

            // Give remaining space to the primary column (index 0)
            if (primaryColumnVisibleIndex >= 0)
            {
                int primaryColIdx = state.visibleColumns[primaryColumnVisibleIndex];
                MultiColumnHeaderState.Column primaryCol = state.columns[primaryColIdx];
                float remaining = totalAvailable - usedByOthers;
                float primaryWidth = Mathf.Max(remaining, primaryCol.minWidth);
                state.columns[primaryColIdx].width = primaryWidth;
            }

            Repaint();
        }

        private int GetColumnIndexAtMousePosition()
        {
            Vector2 mousePos = Event.current.mousePosition;

            for (int i = 0; i < state.visibleColumns.Length; i++)
            {
                Rect columnRect = GetColumnRect(i);
                if (columnRect.Contains(mousePos))
                {
                    return state.visibleColumns[i];
                }
            }
            return -1;
        }

        private int GetVisibleColumnIndexFor(int columnIndex)
        {
            for (int i = 0; i < state.visibleColumns.Length; i++)
            {
                if (state.visibleColumns[i] == columnIndex) return i;
            }
            return -1;
        }

        private void MoveColumn(int columnIndex, int direction)
        {
            int currentVisibleIndex = GetVisibleColumnIndexFor(columnIndex);
            if (currentVisibleIndex < 0) return;

            int newVisibleIndex = currentVisibleIndex + direction;

            // Don't allow moving to position 0 (reserved for Name column)
            if (newVisibleIndex < 1) return;
            if (newVisibleIndex >= state.visibleColumns.Length) return;

            // Swap columns in visibleColumns array
            List<int> cols = new List<int>(state.visibleColumns);
            int temp = cols[currentVisibleIndex];
            cols[currentVisibleIndex] = cols[newVisibleIndex];
            cols[newVisibleIndex] = temp;

            state.visibleColumns = cols.ToArray();

            // Notify listeners
            columnOrderChanged?.Invoke(state.visibleColumns);

            Repaint();
        }
    }
}
