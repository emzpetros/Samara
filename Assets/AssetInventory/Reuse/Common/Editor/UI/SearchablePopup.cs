using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace ImpossibleRobert.Common
{
    public sealed class SearchablePopup : PopupWindowContent
    {
        public struct PopupItem
        {
            public string Text;
            public Color BackgroundColor;
            public bool TintBackground;

            public PopupItem(string text)
            {
                Text = text;
                BackgroundColor = default;
                TintBackground = false;
            }

            public PopupItem(string text, Color backgroundColor, bool tintBackground = true)
            {
                Text = text;
                BackgroundColor = backgroundColor;
                TintBackground = tintBackground;
            }
        }

        private string[] _items;
        private PopupItem[] _popupItems;
        private int _selectedIndex;
        private Action<int> _callback;
        private Vector2 _scrollPos;
        private string _searchText = string.Empty;
        private bool _firstRunDone;
        private float _width;
        private float _maxHeight;
        private List<int> _filteredIndices;
        private bool _filterCacheDirty = true;
        private string _currentHierarchyPath = ""; // Track current hierarchy level
        private bool _showBracketedValues; // Default: hide bracketed values
        private bool _treatSlashLiterally; // When true, "/" in items is not treated as hierarchy separator
        private bool _tintSelectedField;
        private int _highlightedIndex = -1; // Track keyboard-highlighted item index in filtered list
        private bool _keyboardFocusActive; // Track if keyboard focus is on list (not search field)
        private const float ITEM_HEIGHT = 18f;
        private const float SEARCH_HEIGHT = 22f;
        private const float PADDING = 4f;

        private SearchField SearchField => _searchField = _searchField ?? new SearchField();
        private SearchField _searchField;

        public void Init(string[] items, int selectedIndex, Action<int> callback, float width = 300f, float maxHeight = 400f, bool showBracketedValues = false, bool treatSlashLiterally = false)
        {
            Init(CreatePopupItems(items), selectedIndex, callback, width, maxHeight, false, showBracketedValues, treatSlashLiterally);
        }

        public void Init(PopupItem[] items, int selectedIndex, Action<int> callback, float width = 300f, float maxHeight = 400f, bool tintSelectedField = false, bool showBracketedValues = false, bool treatSlashLiterally = false)
        {
            _popupItems = items ?? Array.Empty<PopupItem>();
            _items = new string[_popupItems.Length];
            for (int i = 0; i < _popupItems.Length; i++)
            {
                _items[i] = _popupItems[i].Text ?? string.Empty;
            }
            _selectedIndex = selectedIndex;
            _callback = callback;
            _width = width;
            _maxHeight = maxHeight;
            _searchText = string.Empty; // Reset search on init
            _firstRunDone = false;
            _filterCacheDirty = true;
            _currentHierarchyPath = ""; // Reset to root level
            _showBracketedValues = showBracketedValues;
            _treatSlashLiterally = treatSlashLiterally;
            _tintSelectedField = tintSelectedField;
            _highlightedIndex = -1;
            _keyboardFocusActive = false;
        }

        public override Vector2 GetWindowSize()
        {
            return new Vector2(_width, _maxHeight);
        }

        public override void OnGUI(Rect rect)
        {
            // Apply padding
            Rect contentRect = new Rect(rect.x + PADDING, rect.y + PADDING, rect.width - PADDING * 2, rect.height - PADDING * 2);

            if (_items == null || _items.Length == 0)
            {
                EditorGUI.HelpBox(new Rect(contentRect.x, contentRect.y, contentRect.width, 40), "No items available", MessageType.Info);
                return;
            }

            // Handle ESC key
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            {
                if (!_treatSlashLiterally && !string.IsNullOrEmpty(_currentHierarchyPath))
                {
                    // Navigate back one level
                    int lastSlash = _currentHierarchyPath.LastIndexOf('/');
                    if (lastSlash >= 0)
                    {
                        _currentHierarchyPath = _currentHierarchyPath.Substring(0, lastSlash);
                    }
                    else
                    {
                        _currentHierarchyPath = "";
                    }
                    _filterCacheDirty = true;
                    _scrollPos = Vector2.zero;
                }
                else
                {
                    // At root level or slash literal mode - close popup
                    editorWindow.Close();
                }
                Event.current.Use();
            }

            // Search field
            Rect searchRect = new Rect(contentRect.x, contentRect.y, contentRect.width, SEARCH_HEIGHT);

            // Handle Tab key in search field to jump to first item
            bool searchFieldHadFocus = GUI.GetNameOfFocusedControl() == "SearchablePopupSearchField";
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab && searchFieldHadFocus)
            {
                if (_filteredIndices != null && _filteredIndices.Count > 0)
                {
                    _highlightedIndex = 0;
                    _keyboardFocusActive = true;
                    GUIUtility.keyboardControl = 0; // Remove focus from search field
                    ScrollToHighlightedItem();
                    Event.current.Use();
                }
            }

            GUI.SetNextControlName("SearchablePopupSearchField");
            string newSearchText = SearchField.OnGUI(searchRect, _searchText);
            if (newSearchText != _searchText)
            {
                _searchText = newSearchText;
                _filterCacheDirty = true;
                // Reset hierarchy when searching
                if (!string.IsNullOrWhiteSpace(newSearchText))
                {
                    _currentHierarchyPath = "";
                }
                // Reset highlight when search changes
                _highlightedIndex = -1;
                _keyboardFocusActive = false;
            }

            // Check if search field lost focus (user clicked outside or tabbed away)
            if (searchFieldHadFocus && GUI.GetNameOfFocusedControl() != "SearchablePopupSearchField")
            {
                if (!_keyboardFocusActive && _filteredIndices != null && _filteredIndices.Count > 0)
                {
                    _highlightedIndex = 0;
                    _keyboardFocusActive = true;
                }
            }

            // Filter items based on search text (cache result)
            bool filterChanged = false;
            if (_filterCacheDirty)
            {
                int oldFilterCount = _filteredIndices?.Count ?? 0;
                _filteredIndices = GetFilteredIndices();
                filterChanged = (oldFilterCount != _filteredIndices.Count);
                _filterCacheDirty = false;

                // Force repaint when filter results change
                if (filterChanged)
                {
                    editorWindow.Repaint();
                }
            }

            float contentY = searchRect.yMax + 4;
            float contentHeight = contentRect.height - SEARCH_HEIGHT - 4;

            // Show breadcrumb navigation when in a hierarchy and not searching
            if (!string.IsNullOrEmpty(_currentHierarchyPath) && string.IsNullOrWhiteSpace(_searchText))
            {
                Rect breadcrumbRect = new Rect(contentRect.x, contentY, contentRect.width, ITEM_HEIGHT);
                contentY += ITEM_HEIGHT + 2;
                contentHeight -= ITEM_HEIGHT + 2;

                bool isHovered = breadcrumbRect.Contains(Event.current.mousePosition);

                // Draw hover background
                if (Event.current.type == EventType.Repaint && isHovered)
                {
                    Color hoverColor = EditorGUIUtility.isProSkin
                        ? new Color(1f, 1f, 1f, 0.1f)
                        : new Color(0f, 0f, 0f, 0.1f);
                    EditorGUI.DrawRect(breadcrumbRect, hoverColor);
                }

                // Draw back button/indicator
                if (Event.current.type == EventType.Repaint)
                {
                    EditorStyles.miniLabel.Draw(breadcrumbRect, "← " + _currentHierarchyPath, false, false, false, false);
                }

                // Handle back navigation
                if (Event.current.type == EventType.MouseDown && breadcrumbRect.Contains(Event.current.mousePosition))
                {
                    // Navigate back one level
                    int lastSlash = _currentHierarchyPath.LastIndexOf('/');
                    if (lastSlash >= 0)
                    {
                        _currentHierarchyPath = _currentHierarchyPath.Substring(0, lastSlash);
                    }
                    else
                    {
                        _currentHierarchyPath = "";
                    }
                    _filterCacheDirty = true;
                    _scrollPos = Vector2.zero;
                    Event.current.Use();
                }

                // Repaint on mouse move to update hover state
                if (Event.current.type == EventType.MouseMove)
                {
                    editorWindow.Repaint();
                }
            }

            // Handle keyboard navigation - enable it if arrow keys are pressed even if search field has focus
            if (_filteredIndices != null && _filteredIndices.Count > 0)
            {
                if (Event.current.type == EventType.KeyDown &&
                    (Event.current.keyCode == KeyCode.UpArrow || Event.current.keyCode == KeyCode.DownArrow))
                {
                    if (!_keyboardFocusActive)
                    {
                        // Switch to keyboard navigation mode
                        _keyboardFocusActive = true;
                        GUIUtility.keyboardControl = 0; // Remove focus from search field
                    }
                }

                if (_keyboardFocusActive)
                {
                    HandleKeyboardNavigation();
                }
            }

            // Handle mouse clicks - disable keyboard focus when user clicks on list
            if (Event.current.type == EventType.MouseDown && contentRect.Contains(Event.current.mousePosition))
            {
                // Check if click is in the list area (not search field)
                if (Event.current.mousePosition.y > searchRect.yMax + 4)
                {
                    _keyboardFocusActive = false;
                    _highlightedIndex = -1;
                }
            }

            if (_filteredIndices.Count == 0)
            {
                EditorGUI.HelpBox(new Rect(contentRect.x, contentY, contentRect.width, 40), "No items match the search", MessageType.Info);
            }
            else
            {
                // Reset scroll position if it's beyond the new content bounds (do this after contentHeight is calculated)
                if (filterChanged)
                {
                    float maxScrollY = Mathf.Max(0, (_filteredIndices.Count * ITEM_HEIGHT) - contentHeight);
                    if (_scrollPos.y > maxScrollY) _scrollPos.y = 0;
                }

                // Calculate visible range for performance optimization
                // Ensure visibleStart is never beyond the filtered count
                int visibleStart = Mathf.Max(0, Mathf.Min(_filteredIndices.Count - 1, Mathf.FloorToInt(_scrollPos.y / ITEM_HEIGHT) - 2));
                int visibleEnd = Mathf.Min(_filteredIndices.Count, Mathf.CeilToInt((_scrollPos.y + contentHeight) / ITEM_HEIGHT) + 2);

                // Scrollable list of items - scrollbar extends to right edge (no padding on right)
                Rect scrollRect = new Rect(contentRect.x, contentY, rect.width - PADDING, contentHeight);
                float scrollbarWidth = GUI.skin.verticalScrollbar.fixedWidth;
                Rect viewRect = new Rect(0, 0, scrollRect.width - scrollbarWidth, _filteredIndices.Count * ITEM_HEIGHT);

                _scrollPos = GUI.BeginScrollView(scrollRect, _scrollPos, viewRect, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);

                // Repaint on mouse move to update hover state
                if (Event.current.type == EventType.MouseMove)
                {
                    editorWindow.Repaint();
                }

                // Only render visible items for performance
                for (int i = visibleStart; i < visibleEnd; i++)
                {
                    if (i >= _filteredIndices.Count) break;

                    int index = _filteredIndices[i];
                    string itemText = _items[index];
                    PopupItem popupItem = GetPopupItem(index);
                    bool isEmpty = string.IsNullOrEmpty(itemText);
                    string displayText = isEmpty ? "" : GetDisplayText(itemText);
                    bool isSelected = index == _selectedIndex;

                    Rect itemRect = new Rect(0, i * ITEM_HEIGHT, viewRect.width, ITEM_HEIGHT);

                    if (isEmpty)
                    {
                        // Skip rendering if this is the last item in the filtered list
                        if (i == _filteredIndices.Count - 1)
                        {
                            continue;
                        }
                        
                        // Draw horizontal divider for empty string entries
                        if (Event.current.type == EventType.Repaint)
                        {
                            float dividerY = itemRect.y + itemRect.height * 0.5f;
                            EditorGUI.DrawRect(new Rect(itemRect.x + 2, dividerY, itemRect.width - 4, 1), EditorGUIUtility.isProSkin ? new Color(0.5f, 0.5f, 0.5f, 0.5f) : new Color(0.3f, 0.3f, 0.3f, 0.5f));
                        }
                    }
                    else
                    {
                        bool isHovered = itemRect.Contains(Event.current.mousePosition);
                        bool isHighlighted = _keyboardFocusActive && i == _highlightedIndex;

                        // Check if this is a parent item (has children in hierarchy)
                        bool isParentItem = IsParentItem(itemText);

                        // Draw hover/selection/keyboard highlight background
                        if (Event.current.type == EventType.Repaint)
                        {
                            if (popupItem.TintBackground)
                            {
                                DrawTintedItemBackground(itemRect, popupItem.BackgroundColor, isSelected, isHighlighted, isHovered);
                            }
                            else if (isSelected)
                            {
                                EditorStyles.selectionRect.Draw(itemRect, false, false, true, true);
                            }
                            else if (isHighlighted)
                            {
                                // Draw keyboard highlight (slightly more prominent than hover)
                                Color highlightColor = EditorGUIUtility.isProSkin
                                    ? new Color(1f, 1f, 1f, 0.15f)
                                    : new Color(0f, 0f, 0f, 0.15f);
                                EditorGUI.DrawRect(itemRect, highlightColor);
                            }
                            else if (isHovered)
                            {
                                // Draw hover highlight
                                Color hoverColor = EditorGUIUtility.isProSkin
                                    ? new Color(1f, 1f, 1f, 0.1f)
                                    : new Color(0f, 0f, 0f, 0.1f);
                                EditorGUI.DrawRect(itemRect, hoverColor);
                            }
                        }

                        // Draw text with appropriate style
                        GUIStyle labelStyle = CreateLabelStyle(popupItem, isSelected);
                        Rect textRect = itemRect;

                        // Reserve space for arrow on the right if it's a parent item
                        if (isParentItem)
                        {
                            textRect.width -= 16; // Reserve space for arrow
                        }

                        // Process display text (remove brackets if setting is off, handle elipsis)
                        string processedText = ProcessDisplayText(displayText, textRect.width, labelStyle);

                        // Determine if tooltip is needed (text was ellipsed or modified)
                        bool textWasModified = processedText != displayText || processedText.EndsWith("...");
                        string tooltipText = textWasModified ? displayText : null;

                        // Draw text with bracket highlighting and tooltip
                        DrawTextWithBrackets(textRect, processedText, labelStyle, tooltipText);

                        // Draw arrow indicator for parent items
                        if (isParentItem && Event.current.type == EventType.Repaint)
                        {
                            Rect arrowRect = new Rect(itemRect.xMax - 14, itemRect.y, 12, itemRect.height);
                            GUIStyle arrowStyle = new GUIStyle(EditorStyles.miniLabel);
                            arrowStyle.alignment = TextAnchor.MiddleRight;
                            arrowStyle.normal.textColor = popupItem.TintBackground
                                ? CommonUIStyles.GetHSPColor(popupItem.BackgroundColor)
                                : (EditorGUIUtility.isProSkin
                                    ? new Color(0.7f, 0.7f, 0.7f, 1f)
                                    : new Color(0.4f, 0.4f, 0.4f, 1f));
                            GUI.Label(arrowRect, "►", arrowStyle);
                        }

                        // Handle mouse click
                        if (Event.current.type == EventType.MouseDown && itemRect.Contains(Event.current.mousePosition))
                        {
                            if (_treatSlashLiterally || !string.IsNullOrWhiteSpace(_searchText))
                            {
                                // Slash literal mode or search active - select directly
                                _callback?.Invoke(index);
                                editorWindow.Close();
                                Event.current.Use();
                            }
                            else
                            {
                                // Check if this is a hierarchy navigation
                                string clickedItemText = _items[index];
                                int slashIndex = clickedItemText.IndexOf('/');

                                if (slashIndex >= 0)
                                {
                                    // Check if we're at root and this starts a hierarchy, or if we're in a hierarchy
                                    if (string.IsNullOrEmpty(_currentHierarchyPath))
                                    {
                                        // Root level - navigate into hierarchy
                                        string prefix = clickedItemText.Substring(0, slashIndex);
                                        _currentHierarchyPath = prefix;
                                        _filterCacheDirty = true;
                                        _scrollPos = Vector2.zero;
                                        Event.current.Use();
                                    }
                                    else if (clickedItemText.StartsWith(_currentHierarchyPath + "/"))
                                    {
                                        // Check if this is a leaf item (no more hierarchy) or has deeper levels
                                        string remaining = clickedItemText.Substring(_currentHierarchyPath.Length + 1);
                                        int nextSlash = remaining.IndexOf('/');

                                        if (nextSlash >= 0)
                                        {
                                            // Has deeper hierarchy - navigate deeper
                                            string nextLevel = remaining.Substring(0, nextSlash);
                                            _currentHierarchyPath = _currentHierarchyPath + "/" + nextLevel;
                                            _filterCacheDirty = true;
                                            _scrollPos = Vector2.zero;
                                        }
                                        else
                                        {
                                            // Leaf item - select it
                                            _callback?.Invoke(index);
                                            editorWindow.Close();
                                        }
                                        Event.current.Use();
                                    }
                                }
                                else
                                {
                                    // No hierarchy - select directly
                                    _callback?.Invoke(index);
                                    editorWindow.Close();
                                    Event.current.Use();
                                }
                            }
                        }
                    }
                }

                GUI.EndScrollView();
            }

            // Focus search field on first run
            if (!_firstRunDone)
            {
                SearchField.SetFocus();
                _firstRunDone = true;
            }
        }

        private List<int> GetFilteredIndices()
        {
            List<int> result = new List<int>();

            // When treating slash literally, show all items without hierarchy navigation
            if (_treatSlashLiterally)
            {
                for (int i = 0; i < _items.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(_searchText) ||
                        _items[i].ToLowerInvariant().Contains(_searchText.ToLowerInvariant()))
                    {
                        result.Add(i);
                    }
                }
                return result;
            }

            if (string.IsNullOrWhiteSpace(_searchText))
            {
                // No search - show hierarchical navigation
                if (string.IsNullOrEmpty(_currentHierarchyPath))
                {
                    // Root level: show all top-level items and hierarchy prefixes
                    HashSet<string> seenPrefixes = new HashSet<string>();

                    for (int i = 0; i < _items.Length; i++)
                    {
                        string item = _items[i];
                        if (string.IsNullOrEmpty(item))
                        {
                            result.Add(i); // Keep separators
                            continue;
                        }

                        int slashIndex = item.IndexOf('/');
                        if (slashIndex >= 0)
                        {
                            // Has hierarchy - show prefix if not seen yet
                            string prefix = item.Substring(0, slashIndex);
                            if (seenPrefixes.Add(prefix))
                            {
                                result.Add(i); // Add as hierarchy entry
                            }
                        }
                        else
                        {
                            // No hierarchy - show directly
                            result.Add(i);
                        }
                    }
                }
                else
                {
                    // Inside a hierarchy level - show items matching current path
                    string pathPrefix = _currentHierarchyPath + "/";
                    HashSet<string> seenPrefixes = new HashSet<string>();

                    for (int i = 0; i < _items.Length; i++)
                    {
                        string item = _items[i];
                        if (string.IsNullOrEmpty(item))
                        {
                            result.Add(i); // Keep separators
                            continue;
                        }

                        if (item.StartsWith(pathPrefix))
                        {
                            // Remove the current path prefix to get remaining part
                            string remaining = item.Substring(pathPrefix.Length);

                            // Check if this is a parent item (has more hierarchy levels)
                            int nextSlashIndex = remaining.IndexOf('/');
                            if (nextSlashIndex >= 0)
                            {
                                // Has deeper hierarchy - extract next level prefix
                                string nextLevelPrefix = remaining.Substring(0, nextSlashIndex);
                                if (seenPrefixes.Add(nextLevelPrefix))
                                {
                                    result.Add(i); // Add as hierarchy entry (only once per prefix)
                                }
                            }
                            else
                            {
                                // Leaf item - show directly
                                result.Add(i);
                            }
                        }
                    }
                }
            }
            else
            {
                // Search active - show all matching items regardless of hierarchy
                string searchLower = _searchText.ToLowerInvariant();

                for (int i = 0; i < _items.Length; i++)
                {
                    string item = _items[i];
                    string itemLower = item.ToLowerInvariant();

                    // Check if search matches full string or just item name (after "/")
                    if (itemLower.Contains(searchLower))
                    {
                        result.Add(i);
                    }
                }
            }

            return result;
        }

        private string GetDisplayText(string item)
        {
            // When treating slash literally, show full item text
            if (_treatSlashLiterally) return item;

            if (!string.IsNullOrWhiteSpace(_searchText))
            {
                // Search active - show only item name after "/"
                int lastSlashIndex = item.LastIndexOf('/');
                if (lastSlashIndex >= 0 && lastSlashIndex < item.Length - 1)
                {
                    return item.Substring(lastSlashIndex + 1);
                }
                return item;
            }

            // Hierarchical navigation mode
            if (string.IsNullOrEmpty(_currentHierarchyPath))
            {
                // Root level - show prefix for hierarchical items (without the slash delimiter)
                int slashIndex = item.IndexOf('/');
                if (slashIndex >= 0)
                {
                    return item.Substring(0, slashIndex);
                }
                return item;
            }

            // Inside hierarchy - show relative path from current level
            string pathPrefix = _currentHierarchyPath + "/";
            if (item.StartsWith(pathPrefix))
            {
                string remaining = item.Substring(pathPrefix.Length);
                int nextSlash = remaining.IndexOf('/');
                if (nextSlash >= 0)
                {
                    // Has deeper level - show next level name (without the slash delimiter)
                    return remaining.Substring(0, nextSlash);
                }

                // Leaf item - show name
                return remaining;
            }

            return item;
        }

        private bool IsParentItem(string itemText)
        {
            if (string.IsNullOrEmpty(itemText)) return false;

            // When treating slash literally, items are never parents
            if (_treatSlashLiterally) return false;

            if (string.IsNullOrWhiteSpace(_searchText))
            {
                // Hierarchical navigation mode
                if (string.IsNullOrEmpty(_currentHierarchyPath))
                {
                    // Root level - check if item has hierarchy
                    return itemText.IndexOf('/') >= 0;
                }
                else
                {
                    // Inside hierarchy - check if item has deeper levels
                    string pathPrefix = _currentHierarchyPath + "/";
                    if (itemText.StartsWith(pathPrefix))
                    {
                        string remaining = itemText.Substring(pathPrefix.Length);
                        return remaining.IndexOf('/') >= 0;
                    }
                }
            }

            return false;
        }

        private string ProcessDisplayText(string text, float availableWidth, GUIStyle style)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Remove bracketed values if setting is off
            if (!_showBracketedValues)
            {
                text = RemoveBracketedValues(text);
            }

            // Apply ellipsis if text is too long
            GUIContent content = new GUIContent(text);
            float textWidth = style.CalcSize(content).x;
            if (textWidth > availableWidth)
            {
                // Binary search for optimal truncation point
                int min = 0;
                int max = text.Length;
                string result = text;

                while (min < max)
                {
                    int mid = (min + max + 1) / 2;
                    string testText = text.Substring(0, mid) + "...";
                    float testWidth = style.CalcSize(new GUIContent(testText)).x;

                    if (testWidth <= availableWidth)
                    {
                        min = mid;
                        result = testText;
                    }
                    else
                    {
                        max = mid - 1;
                    }
                }

                if (min > 0 && min < text.Length)
                {
                    text = text.Substring(0, min) + "...";
                }
                else if (text.Length > 0)
                {
                    // Fallback: just show ellipsis
                    text = "...";
                }
            }

            return text;
        }

        private string RemoveBracketedValues(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            StringBuilder result = new StringBuilder();
            int i = 0;

            while (i < text.Length)
            {
                if (text[i] == '[')
                {
                    // Skip until closing bracket
                    int endIndex = text.IndexOf(']', i);
                    if (endIndex >= 0)
                    {
                        i = endIndex + 1;
                        continue;
                    }

                    // No closing bracket, include the rest
                    result.Append(text.Substring(i));
                    break;
                }

                result.Append(text[i]);
                i++;
            }

            return result.ToString();
        }

        private void DrawTextWithBrackets(Rect rect, string text, GUIStyle style, string tooltip = null)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            // Set tooltip if provided
            GUIContent content = string.IsNullOrEmpty(tooltip) ? new GUIContent(text) : new GUIContent(text, tooltip);

            // Check if text has brackets, if not use simple label
            if (!text.Contains("[") || !text.Contains("]"))
            {
                GUI.Label(rect, content, style);
                return;
            }

            if (Event.current.type == EventType.Repaint)
            {
                // Find all bracketed sections [content]
                List<TextSegment> segments = ParseBracketedText(text);

                float x = rect.x;
                Color originalColor = GUI.color;

                // Get the actual text color from the style (preserves whiteLabel for selected items)
                // We don't modify GUI.color for non-bracketed text - let the style handle it
                Color baseTextColor = style.normal.textColor.a > 0f
                    ? style.normal.textColor
                    : (EditorGUIUtility.isProSkin
                        ? new Color(0.8f, 0.8f, 0.8f, 1f)
                        : new Color(0.1f, 0.1f, 0.1f, 1f));
                Color bracketColor = new Color(baseTextColor.r, baseTextColor.g, baseTextColor.b, Mathf.Clamp01(baseTextColor.a * 0.75f));

                foreach (TextSegment segment in segments)
                {
                    Vector2 size = style.CalcSize(new GUIContent(segment.Text));
                    Rect segmentRect = new Rect(x, rect.y, size.x, rect.height);

                    GUIContent segmentContent = string.IsNullOrEmpty(tooltip)
                        ? new GUIContent(segment.Text)
                        : new GUIContent(segment.Text, tooltip);

                    if (segment.IsBracketed)
                    {
                        // Only set color for bracketed segments
                        GUI.color = bracketColor;
                        style.Draw(segmentRect, segmentContent, false, false, false, false);
                        GUI.color = originalColor; // Reset immediately
                    }
                    else
                    {
                        // For normal text, don't modify color - use style's default
                        style.Draw(segmentRect, segmentContent, false, false, false, false);
                    }
                    x += size.x;
                }
            }
            else
            {
                // For layout and other events, just use regular label
                GUI.Label(rect, content, style);
            }
        }

        private List<TextSegment> ParseBracketedText(string text)
        {
            List<TextSegment> segments = new List<TextSegment>();
            int startIndex = 0;

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '[')
                {
                    // Add text before bracket
                    if (i > startIndex)
                    {
                        segments.Add(new TextSegment {Text = text.Substring(startIndex, i - startIndex), IsBracketed = false});
                    }

                    // Find closing bracket
                    int endIndex = text.IndexOf(']', i);
                    if (endIndex >= 0)
                    {
                        // Include brackets in the segment
                        segments.Add(new TextSegment {Text = text.Substring(i, endIndex - i + 1), IsBracketed = true});
                        startIndex = endIndex + 1;
                        i = endIndex;
                    }
                    else
                    {
                        // No closing bracket found, treat rest as normal text
                        break;
                    }
                }
            }

            // Add remaining text after last bracket
            if (startIndex < text.Length)
            {
                segments.Add(new TextSegment {Text = text.Substring(startIndex), IsBracketed = false});
            }

            return segments;
        }

        private void HandleKeyboardNavigation()
        {
            if (Event.current.type != EventType.KeyDown || _filteredIndices == null || _filteredIndices.Count == 0)
                return;

            bool handled = false;

            switch (Event.current.keyCode)
            {
                case KeyCode.UpArrow:
                    // Initialize to last item if not set
                    if (_highlightedIndex < 0)
                    {
                        _highlightedIndex = _filteredIndices.Count - 1;
                    }
                    else if (_highlightedIndex > 0)
                    {
                        _highlightedIndex--;
                    }
                    ScrollToHighlightedItem();
                    handled = true;
                    break;

                case KeyCode.DownArrow:
                    // Initialize to first item if not set
                    if (_highlightedIndex < 0)
                    {
                        _highlightedIndex = 0;
                    }
                    else if (_highlightedIndex < _filteredIndices.Count - 1)
                    {
                        _highlightedIndex++;
                    }
                    ScrollToHighlightedItem();
                    handled = true;
                    break;

                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                case KeyCode.RightArrow:
                    if (_highlightedIndex >= 0 && _highlightedIndex < _filteredIndices.Count)
                    {
                        int index = _filteredIndices[_highlightedIndex];
                        string itemText = _items[index];
                        HandleItemSelection(index, itemText);
                        handled = true;
                    }
                    break;

                case KeyCode.LeftArrow:
                    if (!string.IsNullOrEmpty(_currentHierarchyPath))
                    {
                        // Navigate back one level
                        int lastSlash = _currentHierarchyPath.LastIndexOf('/');
                        if (lastSlash >= 0)
                        {
                            _currentHierarchyPath = _currentHierarchyPath.Substring(0, lastSlash);
                        }
                        else
                        {
                            _currentHierarchyPath = "";
                        }
                        _filterCacheDirty = true;
                        _scrollPos = Vector2.zero;
                        _highlightedIndex = -1;
                        handled = true;
                    }
                    break;

                case KeyCode.Tab:
                    // Tab should not be handled here - it's handled in search field
                    break;
            }

            if (handled)
            {
                Event.current.Use();
                editorWindow.Repaint();
            }
        }

        private void HandleItemSelection(int index, string itemText)
        {
            if (string.IsNullOrWhiteSpace(_searchText))
            {
                // Hierarchical navigation mode
                int slashIndex = itemText.IndexOf('/');
                if (slashIndex >= 0)
                {
                    // Check if we're at root and this starts a hierarchy, or if we're in a hierarchy
                    if (string.IsNullOrEmpty(_currentHierarchyPath))
                    {
                        // Root level - navigate into hierarchy
                        string prefix = itemText.Substring(0, slashIndex);
                        _currentHierarchyPath = prefix;
                        _filterCacheDirty = true;
                        _scrollPos = Vector2.zero;
                        _highlightedIndex = -1;
                    }
                    else if (itemText.StartsWith(_currentHierarchyPath + "/"))
                    {
                        // Check if this is a leaf item (no more hierarchy) or has deeper levels
                        string remaining = itemText.Substring(_currentHierarchyPath.Length + 1);
                        int nextSlash = remaining.IndexOf('/');

                        if (nextSlash >= 0)
                        {
                            // Has deeper hierarchy - navigate deeper
                            string nextLevel = remaining.Substring(0, nextSlash);
                            _currentHierarchyPath = _currentHierarchyPath + "/" + nextLevel;
                            _filterCacheDirty = true;
                            _scrollPos = Vector2.zero;
                            _highlightedIndex = -1;
                        }
                        else
                        {
                            // Leaf item - select it
                            _callback?.Invoke(index);
                            editorWindow.Close();
                        }
                    }
                }
                else
                {
                    // No hierarchy - select directly
                    _callback?.Invoke(index);
                    editorWindow.Close();
                }
            }
            else
            {
                // Search active - select directly
                _callback?.Invoke(index);
                editorWindow.Close();
            }
        }

        private void ScrollToHighlightedItem()
        {
            if (_highlightedIndex < 0 || _filteredIndices == null || _highlightedIndex >= _filteredIndices.Count) return;

            float itemY = _highlightedIndex * ITEM_HEIGHT;
            float contentHeight = editorWindow.position.height - SEARCH_HEIGHT - PADDING * 2;

            // Adjust for breadcrumb if visible
            if (!string.IsNullOrEmpty(_currentHierarchyPath) && string.IsNullOrWhiteSpace(_searchText))
            {
                contentHeight -= ITEM_HEIGHT + 2;
            }

            // Scroll to keep highlighted item visible
            if (itemY < _scrollPos.y)
            {
                _scrollPos.y = itemY;
            }
            else if (itemY + ITEM_HEIGHT > _scrollPos.y + contentHeight)
            {
                _scrollPos.y = itemY + ITEM_HEIGHT - contentHeight;
            }
        }

        private PopupItem GetPopupItem(int index)
        {
            if (_popupItems != null && index >= 0 && index < _popupItems.Length)
            {
                return _popupItems[index];
            }

            return new PopupItem(index >= 0 && index < _items.Length ? _items[index] : string.Empty);
        }

        private static PopupItem[] CreatePopupItems(string[] items)
        {
            string[] source = items ?? Array.Empty<string>();
            PopupItem[] result = new PopupItem[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                result[i] = new PopupItem(source[i]);
            }

            return result;
        }

        private static string GetSelectedDisplayText(int selectedIndex, string[] items, bool treatSlashLiterally)
        {
            string displayText = selectedIndex >= 0 && selectedIndex < items.Length ? items[selectedIndex] : string.Empty;
            if (!treatSlashLiterally)
            {
                int lastSlashIndex = displayText.LastIndexOf('/');
                if (lastSlashIndex >= 0 && lastSlashIndex < displayText.Length - 1)
                {
                    displayText = displayText.Substring(lastSlashIndex + 1);
                }
            }

            return displayText;
        }

        private static bool ShouldTintSelectedField(PopupItem[] items, int selectedIndex, bool tintSelectedField, out PopupItem popupItem)
        {
            if (items != null && tintSelectedField && selectedIndex >= 0 && selectedIndex < items.Length)
            {
                popupItem = items[selectedIndex];
                if (popupItem.TintBackground)
                {
                    return true;
                }
            }

            popupItem = default;
            return false;
        }

        private static GUIStyle CreateLabelStyle(PopupItem popupItem, bool isSelected)
        {
            GUIStyle labelStyle = new GUIStyle(isSelected ? EditorStyles.whiteLabel : EditorStyles.label);
            if (popupItem.TintBackground)
            {
                Color textColor = CommonUIStyles.GetHSPColor(popupItem.BackgroundColor);
                labelStyle.normal.textColor = textColor;
                labelStyle.hover.textColor = textColor;
                labelStyle.focused.textColor = textColor;
                labelStyle.active.textColor = textColor;
            }

            return labelStyle;
        }

        private static void DrawTintedItemBackground(Rect rect, Color color, bool isSelected, bool isHighlighted, bool isHovered)
        {
            Rect tintRect = new Rect(rect.x + 1f, rect.y + 1f, rect.width - 2f, rect.height - 2f);
            EditorGUI.DrawRect(tintRect, color);

            if (isSelected)
            {
                Color overlayColor = EditorGUIUtility.isProSkin
                    ? new Color(1f, 1f, 1f, 0.10f)
                    : new Color(0f, 0f, 0f, 0.10f);
                EditorGUI.DrawRect(tintRect, overlayColor);
                DrawRectOutline(tintRect, CommonUIStyles.GetHSPColor(color));
            }
            else if (isHighlighted)
            {
                Color highlightColor = EditorGUIUtility.isProSkin
                    ? new Color(1f, 1f, 1f, 0.15f)
                    : new Color(0f, 0f, 0f, 0.15f);
                EditorGUI.DrawRect(tintRect, highlightColor);
            }
            else if (isHovered)
            {
                Color hoverColor = EditorGUIUtility.isProSkin
                    ? new Color(1f, 1f, 1f, 0.08f)
                    : new Color(0f, 0f, 0f, 0.08f);
                EditorGUI.DrawRect(tintRect, hoverColor);
            }
        }

        private static void DrawRectOutline(Rect rect, Color color)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1f), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1f, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), color);
        }

        private class TextSegment
        {
            public string Text;
            public bool IsBracketed;
        }

        private class PopupState
        {
            public int lastKnownValue;
            public bool hasChanged;
            public bool valueChangedThisFrame; // Track if value changed for change detection
            public Rect cachedRect;
        }

        // Static dictionary for popup states, keyed by stable string key or control ID
        // Uses same state across Layout/Repaint and across frames
        // Each dropdown control gets its own state even when sharing the same items array
        private static readonly Dictionary<int, PopupState> _popupStates = new Dictionary<int, PopupState>();
        
        // Hint for GetControlID to ensure stable IDs
        private static readonly int _popupControlHint = "SearchablePopup".GetHashCode();

        private static PopupState GetOrCreatePopupState(int stateKey)
        {
            if (!_popupStates.TryGetValue(stateKey, out PopupState state))
            {
                state = new PopupState();
                _popupStates[stateKey] = state;
            }
            return state;
        }

        /// <summary>
        /// Creates a searchable popup field similar to EditorGUILayout.Popup but with search functionality.
        /// </summary>
        /// <param name="selectedIndex">The currently selected index</param>
        /// <param name="items">Array of items to display</param>
        /// <param name="showBracketedValues">Whether to show values in square brackets (default: false)</param>
        /// <param name="treatSlashLiterally">When true, "/" in items is not treated as hierarchy separator (default: false)</param>
        /// <param name="options">Layout options for the field</param>
        /// <returns>The new selected index if changed, otherwise the original index</returns>
        public static int PopupField(int selectedIndex, string[] items, bool showBracketedValues = false, bool treatSlashLiterally = false, params GUILayoutOption[] options)
        {
            // Use auto-generated control ID when no stable key provided
            int controlId = GUIUtility.GetControlID(_popupControlHint, FocusType.Keyboard);
            return PopupFieldInternal(selectedIndex, items, null, false, showBracketedValues, treatSlashLiterally, controlId, options);
        }

        public static int PopupField(int selectedIndex, PopupItem[] items, bool tintSelectedField = false, bool showBracketedValues = false, bool treatSlashLiterally = false, params GUILayoutOption[] options)
        {
            int controlId = GUIUtility.GetControlID(_popupControlHint, FocusType.Keyboard);
            return PopupFieldInternal(selectedIndex, null, items, tintSelectedField, showBracketedValues, treatSlashLiterally, controlId, options);
        }

        /// <summary>
        /// Creates a searchable popup field with a stable state key for cross-tab/layout persistence.
        /// Use this overload when the popup might not be rendered continuously (e.g., hidden by tab switches).
        /// </summary>
        /// <param name="selectedIndex">The currently selected index</param>
        /// <param name="items">Array of items to display</param>
        /// <param name="stateKey">A unique stable key for state persistence across layout changes</param>
        /// <param name="showBracketedValues">Whether to show values in square brackets (default: false)</param>
        /// <param name="treatSlashLiterally">When true, "/" in items is not treated as hierarchy separator (default: false)</param>
        /// <param name="options">Layout options for the field</param>
        /// <returns>The new selected index if changed, otherwise the original index</returns>
        public static int PopupField(int selectedIndex, string[] items, string stateKey, bool showBracketedValues = false, bool treatSlashLiterally = false, params GUILayoutOption[] options)
        {
            // Still need to call GetControlID to maintain consistent control order for Unity's IMGUI
            GUIUtility.GetControlID(_popupControlHint, FocusType.Keyboard);
            // Use stable string key's hash code for state lookup
            int stableKey = stateKey?.GetHashCode() ?? 0;
            return PopupFieldInternal(selectedIndex, items, null, false, showBracketedValues, treatSlashLiterally, stableKey, options);
        }

        public static int PopupField(int selectedIndex, PopupItem[] items, string stateKey, bool tintSelectedField = false, bool showBracketedValues = false, bool treatSlashLiterally = false, params GUILayoutOption[] options)
        {
            GUIUtility.GetControlID(_popupControlHint, FocusType.Keyboard);
            int stableKey = stateKey?.GetHashCode() ?? 0;
            return PopupFieldInternal(selectedIndex, null, items, tintSelectedField, showBracketedValues, treatSlashLiterally, stableKey, options);
        }

        private static int PopupFieldInternal(int selectedIndex, string[] items, PopupItem[] popupItems, bool tintSelectedField, bool showBracketedValues, bool treatSlashLiterally, int stateKey, GUILayoutOption[] options)
        {
            popupItems = popupItems ?? CreatePopupItems(items);
            items = items ?? Array.Empty<string>();
            if (items.Length == 0 && popupItems.Length > 0)
            {
                items = new string[popupItems.Length];
                for (int i = 0; i < popupItems.Length; i++)
                {
                    items[i] = popupItems[i].Text ?? string.Empty;
                }
            }

            // Track original value for change detection (like EditorGUILayout.Popup does internally)
            int originalValue = selectedIndex;

            // Use EditorGUI's change tracking system
            EditorGUI.BeginChangeCheck();

            // Get current display text
            string displayText = GetSelectedDisplayText(selectedIndex, items, treatSlashLiterally);

            // Create a popup-style field that matches EditorGUILayout.Popup appearance
            // Always call GetControlRect - it handles Layout/Repaint phases properly internally
            // But we must ensure we always call it in the same order and with same parameters
            Rect popupRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight, options);

            // Use state key for state tracking - each popup field gets its own state
            PopupState state = GetOrCreatePopupState(stateKey);

            // ONLY process state changes during Layout to ensure consistent control counts
            // between Layout and Repaint events
            if (Event.current.type == EventType.Layout)
            {
                state.cachedRect = popupRect;
                state.valueChangedThisFrame = false;

                // Check if value changed via async callback from previous frame
                if (state.hasChanged)
                {
                    selectedIndex = state.lastKnownValue;
                    state.hasChanged = false;
                    state.valueChangedThisFrame = true;
                    // Update display text to reflect new selection
                    displayText = GetSelectedDisplayText(selectedIndex, items, treatSlashLiterally);
                }
                else
                {
                    // Update state with current value if not changed
                    state.lastKnownValue = selectedIndex;
                }
            }
            else if (Event.current.type == EventType.Repaint)
            {
                // During Repaint, use the value from Layout for consistent rendering
                selectedIndex = state.lastKnownValue;
                // Update display text to match
                displayText = GetSelectedDisplayText(selectedIndex, items, treatSlashLiterally);
            }
            // For other events (mouse, keyboard), don't override selectedIndex - 
            // this allows external value changes (like saved search restoration) to take effect

            // Handle button interaction
            bool tintField = ShouldTintSelectedField(popupItems, selectedIndex, tintSelectedField, out PopupItem selectedItem);
            Color oldBackgroundColor = GUI.backgroundColor;
            Color oldContentColor = GUI.contentColor;
            if (tintField)
            {
                GUI.backgroundColor = selectedItem.BackgroundColor;
                GUI.contentColor = CommonUIStyles.GetHSPColor(selectedItem.BackgroundColor);
            }

            if (GUI.Button(popupRect, new GUIContent(displayText), EditorStyles.popup))
            {
                SearchablePopup popup = new SearchablePopup();

                // Use the popup rect width, ensuring minimum width
                float width = Mathf.Max(popupRect.width, 200f);

                // Capture the state key and current selectedIndex for the callback
                int capturedStateKey = stateKey;
                int capturedSelectedIndex = selectedIndex;
                popup.Init(popupItems, selectedIndex, (index) =>
                {
                    if (capturedSelectedIndex != index)
                    {
                        // Store the change in the state object using the state key
                        PopupState callbackState = GetOrCreatePopupState(capturedStateKey);
                        callbackState.lastKnownValue = index;
                        callbackState.hasChanged = true;
                    }
                }, width, 400f, tintSelectedField, showBracketedValues, treatSlashLiterally);

                PopupWindow.Show(popupRect, popup);
            }

            if (tintField)
            {
                GUI.backgroundColor = oldBackgroundColor;
                GUI.contentColor = oldContentColor;
            }

            // Use EditorGUI's change detection to see if the value differs from original
            EditorGUI.EndChangeCheck();
            if (state.valueChangedThisFrame || selectedIndex != originalValue)
            {
                GUI.changed = true;
            }

            return selectedIndex;
        }
    }
}
