using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace ImpossibleRobert.Common
{
    /// <summary>
    /// A reusable EditorWindow for displaying large amounts of line-based content
    /// with search filtering and virtualized scrolling.
    /// </summary>
    public sealed class LineListWindow : EditorWindow
    {
        private string[] _lines;
        private Action<int, string> _onLineClick;
        private Vector2 _scrollPos;
        private string _searchText = string.Empty;
        private List<int> _filteredIndices;
        private bool _filterCacheDirty = true;
        private bool _focusSearchField;

        private SearchField SearchField => _searchField ??= new SearchField();
        private SearchField _searchField;

        private const float ITEM_HEIGHT = 18f;
        private const float SEARCH_HEIGHT = 22f;
        private const float PADDING = 4f;
        private const float STATUS_HEIGHT = 20f;

        /// <summary>
        /// Shows the LineListWindow with the specified content.
        /// </summary>
        /// <param name="title">Window title</param>
        /// <param name="lines">Array of lines to display</param>
        /// <param name="onLineClick">Optional callback when a line is clicked (receives original index and line text)</param>
        /// <param name="width">Initial window width</param>
        /// <param name="height">Initial window height</param>
        /// <returns>The window instance</returns>
        public static LineListWindow Show(string title, string[] lines, Action<int, string> onLineClick = null, float width = 500f, float height = 400f)
        {
            LineListWindow window = CreateInstance<LineListWindow>();
            window.titleContent = new GUIContent(title);
            window._lines = lines ?? Array.Empty<string>();
            window._onLineClick = onLineClick;
            window._filterCacheDirty = true;
            window._focusSearchField = true;
            window._searchText = string.Empty;
            window._scrollPos = Vector2.zero;

            // Center window on screen
            Vector2 screenCenter = new Vector2(Screen.currentResolution.width / 2f, Screen.currentResolution.height / 2f);
            window.position = new Rect(screenCenter.x - width / 2f, screenCenter.y - height / 2f, width, height);
            window.minSize = new Vector2(300, 200);

            window.ShowUtility();
            return window;
        }

        /// <summary>
        /// Shows the LineListWindow with IEnumerable content.
        /// </summary>
        public static LineListWindow Show(string title, IEnumerable<string> lines, Action<int, string> onLineClick = null, float width = 500f, float height = 400f)
        {
            return Show(title, lines != null ? new List<string>(lines).ToArray() : Array.Empty<string>(), onLineClick, width, height);
        }

        private void OnGUI()
        {
            if (_lines == null)
            {
                EditorGUILayout.HelpBox("No content to display.", MessageType.Info);
                return;
            }

            // Handle ESC key to close
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            {
                Close();
                Event.current.Use();
                return;
            }

            Rect contentRect = new Rect(PADDING, PADDING, position.width - PADDING * 2, position.height - PADDING * 2);

            // Search field
            Rect searchRect = new Rect(contentRect.x, contentRect.y, contentRect.width, SEARCH_HEIGHT);
            GUI.SetNextControlName("LineListSearchField");
            string newSearchText = SearchField.OnGUI(searchRect, _searchText);
            if (newSearchText != _searchText)
            {
                _searchText = newSearchText;
                _filterCacheDirty = true;
                _scrollPos = Vector2.zero; // Reset scroll on search change
            }

            // Focus search field on first frame
            if (_focusSearchField)
            {
                SearchField.SetFocus();
                _focusSearchField = false;
            }

            // Update filter cache
            if (_filterCacheDirty)
            {
                UpdateFilteredIndices();
                _filterCacheDirty = false;
            }

            // Status line showing count
            float contentY = searchRect.yMax + PADDING;
            Rect statusRect = new Rect(contentRect.x, contentY, contentRect.width, STATUS_HEIGHT);
            string statusText = string.IsNullOrWhiteSpace(_searchText)
                ? $"{_lines.Length:N0} items"
                : $"Showing {_filteredIndices.Count:N0} of {_lines.Length:N0} items";
            EditorGUI.LabelField(statusRect, statusText, EditorStyles.miniLabel);

            // List area
            float listY = statusRect.yMax + PADDING;
            float listHeight = contentRect.yMax - listY;

            if (_filteredIndices.Count == 0)
            {
                Rect emptyRect = new Rect(contentRect.x, listY, contentRect.width, 40);
                EditorGUI.HelpBox(emptyRect, string.IsNullOrWhiteSpace(_searchText) ? "No items to display" : "No items match the search", MessageType.Info);
                return;
            }

            // Calculate visible range for virtualized rendering
            int visibleStart = Mathf.Max(0, Mathf.FloorToInt(_scrollPos.y / ITEM_HEIGHT) - 2);
            int visibleEnd = Mathf.Min(_filteredIndices.Count, Mathf.CeilToInt((_scrollPos.y + listHeight) / ITEM_HEIGHT) + 2);

            // Scrollable list
            Rect scrollRect = new Rect(contentRect.x, listY, contentRect.width, listHeight);
            float scrollbarWidth = GUI.skin.verticalScrollbar.fixedWidth;
            Rect viewRect = new Rect(0, 0, scrollRect.width - scrollbarWidth, _filteredIndices.Count * ITEM_HEIGHT);

            _scrollPos = GUI.BeginScrollView(scrollRect, _scrollPos, viewRect, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);

            // Repaint on mouse move to update hover state
            if (Event.current.type == EventType.MouseMove)
            {
                Repaint();
            }

            // Only render visible items for performance
            for (int i = visibleStart; i < visibleEnd; i++)
            {
                if (i < 0 || i >= _filteredIndices.Count) continue;

                int originalIndex = _filteredIndices[i];
                string lineText = _lines[originalIndex];

                Rect itemRect = new Rect(0, i * ITEM_HEIGHT, viewRect.width, ITEM_HEIGHT);
                bool isHovered = itemRect.Contains(Event.current.mousePosition);

                // Draw hover background
                if (isHovered && Event.current.type == EventType.Repaint)
                {
                    Color hoverColor = EditorGUIUtility.isProSkin
                        ? new Color(1f, 1f, 1f, 0.1f)
                        : new Color(0f, 0f, 0f, 0.1f);
                    EditorGUI.DrawRect(itemRect, hoverColor);
                }

                // Draw text
                Rect textRect = new Rect(itemRect.x + 4, itemRect.y, itemRect.width - 8, itemRect.height);
                
                // Show tooltip for long text
                GUIContent content = new GUIContent(lineText);
                float textWidth = EditorStyles.label.CalcSize(content).x;
                if (textWidth > textRect.width)
                {
                    content.tooltip = lineText;
                }

                EditorGUI.LabelField(textRect, content);

                // Handle click
                if (_onLineClick != null && isHovered && Event.current.type == EventType.MouseDown && Event.current.button == 0)
                {
                    _onLineClick.Invoke(originalIndex, lineText);
                    Event.current.Use();
                }
            }

            GUI.EndScrollView();
        }

        private void UpdateFilteredIndices()
        {
            _filteredIndices ??= new List<int>();
            _filteredIndices.Clear();

            if (_lines == null || _lines.Length == 0) return;

            string searchLower = _searchText?.ToLowerInvariant() ?? string.Empty;

            for (int i = 0; i < _lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(searchLower) ||
                    (_lines[i] != null && _lines[i].ToLowerInvariant().Contains(searchLower)))
                {
                    _filteredIndices.Add(i);
                }
            }
        }
    }
}
