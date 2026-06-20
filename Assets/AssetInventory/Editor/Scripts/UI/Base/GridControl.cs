using ImpossibleRobert.Common;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    internal sealed class GridControl
    {
        public event Action<AssetInfo> OnDoubleClick;
        public event Action<int> OnKeyboardSelection;
        public event Action<GenericMenu, IReadOnlyList<AssetInfo>, int> OnContextMenuPopulate;

        public List<AssetInfo> packages;
        private GUIContent[] _contents;
        public GUIContent[] contents
        {
            get => _contents;
            set
            {
                if (_contents != null)
                {
                    foreach (GUIContent content in _contents)
                    {
                        if (content != null && content.image != null)
                        {
                            // Skip built-in Unity icons which shouldn't be destroyed
                            if (!AssetDatabase.GetAssetPath(content.image).StartsWith("Library/"))
                            {
                                UnityEngine.Object.DestroyImmediate(content.image);
                            }
                        }
                    }
                }
                _contents = value;
            }
        }
        public List<AssetInfo> selectionItems;
        public int selectionCount;
        public int selectionTile;
        public int selectionPackageCount;
        public long selectionSize;

        public int noTextBelow;
        public bool LastClickAlt;
        public bool enlargeTiles;
        public bool centerTiles;
        public bool onlySingleSelection;

        public bool IsMouseOverGrid;

        // Keyboard navigation properties
        private bool HasKeyboardFocus { get; set; }
        private int _cellsPerRow = 1;
        private int _totalRows = 1;
        private int _currentRow;
        private int _currentCol;
        private bool _keyboardNavigationOccurred;
        private float _actualTileWidth;
        private float _actualTileHeight;
        private float _leftOffset;

        // Public accessors for visibility calculations
        public int CellsPerRow => _cellsPerRow;
        public float ActualTileHeight => _actualTileHeight;

        private GUIContent[] Selection
        {
            get
            {
                if (_selection == null || (contents != null && _selection.Length != contents.Length))
                {
                    _selection = new GUIContent[contents != null ? contents.Length : 0];
                    MarkGridSelection(true);
                }
                return _selection;
            }
        }
        private Func<AssetInfo, string> _textGenerator;
        private GUIContent[] _selection;
        private int _selectionMin;
        private int _selectionMax;
        private int _lastSelectionTile;
        private List<AssetInfo> _allPackages;
        private Dictionary<int, AssetInfo> _allPackagesDict;
        private Action _bulkHandler;
        private Rect _lastRect;
        private readonly HashSet<int> _selectionPackageIds = new HashSet<int>();

        public void Init(List<AssetInfo> allPackages, IEnumerable<AssetInfo> visiblePackages, Action bulkHandler, Func<AssetInfo, string> textGenerator = null)
        {
            packages = NormalizeVisiblePackages(visiblePackages);
            _textGenerator = textGenerator;
            if (!ReferenceEquals(_allPackages, allPackages))
            {
                _allPackages = allPackages;
                _allPackagesDict = BuildAssetLookup(allPackages);
            }
            _bulkHandler = bulkHandler;
            _selection = new GUIContent[contents != null ? contents.Length : 0];
            MarkGridSelection(true);
            CalculateBulkSelection();
        }

        private List<AssetInfo> NormalizeVisiblePackages(IEnumerable<AssetInfo> visiblePackages)
        {
            if (visiblePackages == null) return null;

            List<AssetInfo> visibleList = visiblePackages as List<AssetInfo>;
            if (visibleList != null && !ContainsNull(visibleList) && (contents == null || visibleList.Count == contents.Length))
            {
                return visibleList;
            }

            List<AssetInfo> result = new List<AssetInfo>();
            foreach (AssetInfo item in visiblePackages)
            {
                if (item != null) result.Add(item);
            }
            return result;
        }

        private static bool ContainsNull(List<AssetInfo> items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] == null) return true;
            }
            return false;
        }

        private static Dictionary<int, AssetInfo> BuildAssetLookup(List<AssetInfo> allPackages)
        {
            if (allPackages == null) return null;

            Dictionary<int, AssetInfo> result = new Dictionary<int, AssetInfo>();
            for (int i = 0; i < allPackages.Count; i++)
            {
                AssetInfo asset = allPackages[i];
                if (asset == null || result.ContainsKey(asset.AssetId)) continue;
                result.Add(asset.AssetId, asset);
            }
            return result;
        }

        public void Draw(float availableWidth, int tileSize, float tileAspectRatio, GUIStyle tileStyle, GUIStyle selectedTileStyle)
        {
            int itemCount = EnsureValidGridState();
            if (itemCount == 0) return;

            float actualWidth = availableWidth - UIStyles.BORDER_WIDTH;
            int cells = Mathf.Clamp(Mathf.FloorToInt(actualWidth / (tileSize + AI.Config.tileMargin)), 1, 99);
            if (cells < 2) cells = 2;

            // Store cells per row for keyboard navigation
            _cellsPerRow = cells;

            if (enlargeTiles)
            {
                // enlarge tiles dynamically so they take the full width
                tileSize = Mathf.FloorToInt((actualWidth - cells * AI.Config.tileMargin - 2 * AI.Config.tileMargin) / cells);
            }

            tileStyle.fixedHeight = tileSize / tileAspectRatio;
            tileStyle.fixedWidth = tileSize;
            tileStyle.margin = new RectOffset(AI.Config.tileMargin, AI.Config.tileMargin, AI.Config.tileMargin, AI.Config.tileMargin); // set again due to initial style only being set once so changes would not reflect
            selectedTileStyle.fixedHeight = tileStyle.fixedHeight + tileStyle.margin.top;
            selectedTileStyle.fixedWidth = tileStyle.fixedWidth + tileStyle.margin.left;

            // Save the actual tile width/height for scroll calculations (includes padding and margins)
            _actualTileWidth = tileStyle.fixedWidth + AI.Config.tileMargin;
            _actualTileHeight = tileStyle.fixedHeight + AI.Config.tileMargin;

            if (_textGenerator != null && contents != null && contents.Length > 0)
            {
                // remove text if tiles are too small
                if (tileSize < noTextBelow)
                {
                    if (!string.IsNullOrEmpty(contents[0].text))
                    {
                        contents.ForEach(c => c.text = string.Empty);
                    }
                }

                // create text on-demand if tiles are big enough
                if (tileSize >= noTextBelow)
                {
                    if (string.IsNullOrEmpty(contents[0].text))
                    {
                        for (int i = 0; i < contents.Length; i++)
                        {
                            contents[i].text = _textGenerator(packages[i]);
                        }
                    }
                }
            }

            // Pre-handle right-click before SelectionGrid can consume the event
            _leftOffset = centerTiles ? (actualWidth - tileSize * cells) / 2f : 0f;
            HandleContextMenuPreGrid(cells);

            // Detect double-clicks during the mouse event before SelectionGrid consumes it.
            // The callback must NOT be invoked during the layout phase (or mid-grid) since
            // it can repaint, change selection, or open windows, which corrupts the
            // GUILayout group stack of the surrounding scroll view (causing scroll resets
            // on rapid clicks). Only left-button double-clicks count.
            bool doubleClicked = Event.current.type == EventType.MouseDown
                && Event.current.button == 0
                && Event.current.clickCount > 1
                && IsMouseOverGrid;

            GUILayout.BeginHorizontal();
            if (centerTiles) GUILayout.Space(_leftOffset);
            selectionTile = GUILayout.SelectionGrid(selectionTile, contents, cells, tileStyle);

            _lastRect = CommonUIStyles.GetCurrentVisibleRect(); // GetLastRect would include invisible scroll area as well
            if (Event.current.type == EventType.Repaint)
            {
                IsMouseOverGrid = _lastRect.Contains(Event.current.mousePosition);
            }

            if (selectionCount > 1)
            {
                // draw selection on top if there are more than one selected, otherwise don't for performance
                // use real last rect to support scrolling
                GUI.SelectionGrid(GUILayoutUtility.GetLastRect(), selectionTile, Selection, cells, selectedTileStyle);
            }
            GUILayout.EndHorizontal();

            // Post-grid context menu handling no longer needed; pre-grid handler covers it

            // Update keyboard focus state and grid layout info
            UpdateKeyboardFocus();
            UpdateGridLayoutInfo();

            // Handle keyboard commands for navigation
            HandleKeyboardCommands();

            // Fire double-click callback after all layout operations are complete.
            // Deferred via EditorApplication.delayCall so it runs OUTSIDE the current
            // OnGUI pass entirely. Otherwise the callback (which may open windows,
            // load assets, or change selection state) can corrupt the GUILayout
            // group stack of the surrounding scroll view, producing
            // "Invalid GUILayout state" errors and scroll-position resets on
            // rapid clicks.
            if (doubleClicked && packages != null)
            {
                int count = packages.Count;
                if (selectionTile >= 0 && selectionTile < count)
                {
                    AssetInfo clicked = packages[selectionTile];
                    Action<AssetInfo> handler = OnDoubleClick;
                    if (handler != null)
                    {
                        LastClickAlt = Event.current != null && Event.current.alt;
                        EditorApplication.delayCall += () => handler(clicked);
                    }
                }
            }
        }

        private int EnsureValidGridState()
        {
            if (contents == null || packages == null || packages.Count == 0)
            {
                selectionTile = 0;
                _selectionMin = 0;
                _selectionMax = 0;
                _lastSelectionTile = 0;
                if (selectionItems == null) selectionItems = new List<AssetInfo>();
                else selectionItems.Clear();
                selectionCount = 0;
                selectionPackageCount = 0;
                selectionSize = 0;
                _selection = Array.Empty<GUIContent>();
                return 0;
            }

            int itemCount = Mathf.Min(contents.Length, packages.Count);
            if (itemCount <= 0)
            {
                selectionTile = 0;
                _selection = Array.Empty<GUIContent>();
                return 0;
            }

            if (itemCount != contents.Length)
            {
                Array.Resize(ref _contents, itemCount);
            }

            if (packages.Count != itemCount)
            {
                packages = packages.GetRange(0, itemCount);
            }

            for (int i = 0; i < itemCount; i++)
            {
                if (_contents[i] == null) _contents[i] = new GUIContent();
            }

            if (_selection == null || _selection.Length != itemCount)
            {
                _selection = new GUIContent[itemCount];
                MarkGridSelection(true);
            }

            if (selectionTile < 0) selectionTile = 0;
            if (selectionTile >= itemCount) selectionTile = itemCount - 1;

            return itemCount;
        }

        private void HandleContextMenuPreGrid(int cells)
        {
            Event evt = Event.current;
            if (evt == null) return;
            if (contents == null || contents.Length == 0) return;

            // Intercept right mouse down or ContextClick before SelectionGrid handles it
            if ((evt.type == EventType.MouseDown && evt.button == 1) || evt.type == EventType.ContextClick)
            {
                if (IsMouseOverGrid)
                {
                    int clickedIndex = selectionTile;
                    if (TryGetIndexUnderMouse(cells, out int idx)) clickedIndex = idx;

                    bool clickedIsSelected = clickedIndex >= 0 && clickedIndex < Selection.Length && Selection[clickedIndex] == UIStyles.selectedTileContent;
                    if (!clickedIsSelected && clickedIndex >= 0 && clickedIndex < contents.Length)
                    {
                        selectionTile = clickedIndex;
                        MarkGridSelection(true);
                        _selectionMin = selectionTile;
                        _selectionMax = selectionTile;
                        _lastSelectionTile = selectionTile;
                        CalculateBulkSelection();
                    }

                    if (OnContextMenuPopulate != null)
                    {
                        GenericMenu menu = new GenericMenu();
                        OnContextMenuPopulate.Invoke(menu, selectionItems, clickedIndex);
                        menu.ShowAsContext();
                        evt.Use();
                    }
                }
            }
        }

        private bool TryGetIndexUnderMouse(int cells, out int index)
        {
            index = -1;
            if (_lastRect.width <= 0f || _actualTileHeight <= 0f) return false;

            Vector2 mouse = Event.current.mousePosition;
            float localX = mouse.x - _lastRect.x - _leftOffset;
            float localY = mouse.y - _lastRect.y;

            if (localX < 0f || localY < 0f) return false;

            int col = Mathf.FloorToInt(localX / _actualTileWidth);
            int row = Mathf.FloorToInt(localY / _actualTileHeight);

            if (col < 0 || col >= cells || row < 0) return false;

            int idx = row * cells + col;
            if (idx < 0 || contents == null || idx >= contents.Length) return false;

            index = idx;
            return true;
        }

        private void UpdateKeyboardFocus()
        {
            // Check if the grid has keyboard focus
            bool isTextEditing = EditorGUIUtility.editingTextField;
            HasKeyboardFocus = !isTextEditing;
        }

        private void UpdateGridLayoutInfo()
        {
            // Calculate total rows for navigation
            if (contents != null && contents.Length > 0)
            {
                _totalRows = Mathf.CeilToInt((float)contents.Length / _cellsPerRow);
                _currentRow = selectionTile / _cellsPerRow;
                _currentCol = selectionTile % _cellsPerRow;
            }
        }

        public void LimitSelection(int count)
        {
            if (selectionTile >= count) selectionTile = 0;
        }

        private void MarkGridSelection(bool clearSelection)
        {
            if (clearSelection) Selection.Populate(UIStyles.emptyTileContent);

            if (selectionTile >= Selection.Length) selectionTile = 0;
            if (selectionTile >= 0 && Selection.Length > 0) Selection[selectionTile] = UIStyles.selectedTileContent;
        }

        public void HandleMouseClicks()
        {
            if (selectionTile >= 0)
            {
                // Remove focus from any active text fields to enable keyboard control
                GUI.FocusControl("");

                if (onlySingleSelection || (!Event.current.control && !Event.current.shift))
                {
                    // regular click, no ctrl/shift
                    MarkGridSelection(true);

                    _selectionMin = selectionTile;
                    _selectionMax = selectionTile;
                }
                else if (Event.current.control)
                {
                    // toggle existing selection
                    Selection[selectionTile] = Selection[selectionTile] == UIStyles.selectedTileContent ? UIStyles.emptyTileContent : UIStyles.selectedTileContent;
                }
                else if (Event.current.shift)
                {
                    // shift click - add all between clicks
                    if (_selectionMin != -1 && _selectionMax != -1 && selectionTile >= _selectionMin && selectionTile <= _selectionMax)
                    {
                        Selection.Populate(UIStyles.emptyTileContent);
                        _selectionMin = Mathf.Min(_lastSelectionTile, selectionTile);
                        _selectionMax = Mathf.Max(_lastSelectionTile, selectionTile);
                    }
                    int minI = Mathf.Min(_lastSelectionTile, selectionTile);
                    int maxI = Mathf.Max(_lastSelectionTile, selectionTile);
                    if (minI < 0) minI = 0;
                    if (maxI < 0) maxI = 0;

                    if (Selection.Length > 0)
                    {
                        for (int i = minI; i <= maxI; i++)
                        {
                            Selection[i] = UIStyles.selectedTileContent;
                        }
                    }
                }

                _selectionMin = Mathf.Min(_selectionMin, selectionTile);
                _selectionMax = Mathf.Max(_selectionMax, selectionTile);
                _lastSelectionTile = selectionTile;

                CalculateBulkSelection();
            }
        }

        public void HandleKeyboardCommands()
        {
            if (!HasKeyboardFocus || contents == null || contents.Length == 0) return;

            // Only process KeyDown events, not KeyUp or other event types
            if (Event.current.type != EventType.KeyDown) return;

            // Handle Ctrl+A for select all
            if (!onlySingleSelection && Event.current.modifiers == EventModifiers.Control && Event.current.keyCode == KeyCode.A)
            {
                // select all
                Selection.Populate(UIStyles.selectedTileContent);

                _selectionMin = 0;
                _selectionMax = contents.Length - 1;
                _lastSelectionTile = selectionTile;

                CalculateBulkSelection();
                Event.current.Use();
                return;
            }

            bool handled = false;
            switch (Event.current.keyCode)
            {
                case KeyCode.LeftArrow:
                    if ((Event.current.modifiers & EventModifiers.Alt) == 0)
                    {
                        NavigateLeft();
                        handled = true;
                    }
                    break;

                case KeyCode.RightArrow:
                    if ((Event.current.modifiers & EventModifiers.Alt) == 0)
                    {
                        NavigateRight();
                        handled = true;
                    }
                    break;

                case KeyCode.UpArrow:
                    NavigateUp();
                    handled = true;
                    break;

                case KeyCode.DownArrow:
                    NavigateDown();
                    handled = true;
                    break;

                case KeyCode.Home:
                    NavigateToStart();
                    handled = true;
                    break;

                case KeyCode.End:
                    NavigateToEnd();
                    handled = true;
                    break;

                case KeyCode.PageUp:
                    NavigatePageUp();
                    handled = true;
                    break;

                case KeyCode.PageDown:
                    NavigatePageDown();
                    handled = true;
                    break;
            }

            if (handled)
            {
                // Use the event since we're only processing keyboard events now
                Event.current.Use();

                // Mark that keyboard navigation occurred
                _keyboardNavigationOccurred = true;

                // Ensure row/col values are consistent with selectionTile
                _currentRow = selectionTile / _cellsPerRow;
                _currentCol = selectionTile % _cellsPerRow;

                // Ensure selectionTile is within bounds
                selectionTile = Mathf.Clamp(selectionTile, 0, contents.Length - 1);

                // Handle selection based on modifier keys
                if (onlySingleSelection || !Event.current.shift)
                {
                    // Regular navigation: clear selection and select only current tile
                    MarkGridSelection(true);
                    _selectionMin = selectionTile;
                    _selectionMax = selectionTile;
                    _lastSelectionTile = selectionTile;
                }
                else
                {
                    // Shift+arrow: extend selection from last tile to current tile
                    int minI = Mathf.Min(_lastSelectionTile, selectionTile);
                    int maxI = Mathf.Max(_lastSelectionTile, selectionTile);

                    // Clear and rebuild selection range
                    Selection.Populate(UIStyles.emptyTileContent);
                    for (int i = minI; i <= maxI; i++)
                    {
                        Selection[i] = UIStyles.selectedTileContent;
                    }

                    _selectionMin = minI;
                    _selectionMax = maxI;
                    // Don't update _lastSelectionTile so further shifts extend from the anchor point
                }

                CalculateBulkSelection();

                // Trigger selection change event for keyboard navigation
                if (packages != null && selectionTile >= 0 && selectionTile < packages.Count)
                {
                    OnKeyboardSelection?.Invoke(selectionTile);
                }
            }
        }

        private void NavigateLeft()
        {
            if (selectionTile > 0)
            {
                selectionTile--;
                UpdateGridLayoutInfo();
            }
        }

        private void NavigateRight()
        {
            if (selectionTile < contents.Length - 1)
            {
                selectionTile++;
                UpdateGridLayoutInfo();
            }
        }

        private void NavigateUp()
        {
            if (_currentRow > 0 && selectionTile >= _cellsPerRow)
            {
                selectionTile -= _cellsPerRow;
                _currentRow--;
                if (selectionTile < 0) selectionTile = 0;
            }
        }

        private void NavigateDown()
        {
            if (_currentRow < _totalRows - 1 && selectionTile + _cellsPerRow < contents.Length)
            {
                selectionTile += _cellsPerRow;
                _currentRow++;
                if (selectionTile >= contents.Length) selectionTile = contents.Length - 1;
            }
        }

        private void NavigateToStart()
        {
            selectionTile = 0;
            _currentRow = 0;
            _currentCol = 0;
        }

        private void NavigateToEnd()
        {
            selectionTile = contents.Length - 1;
            _currentRow = _totalRows - 1;
            _currentCol = (contents.Length - 1) % _cellsPerRow;
        }

        private void NavigatePageUp()
        {
            int pageSize = _cellsPerRow * 3; // Navigate 3 rows up
            if (selectionTile >= pageSize)
            {
                selectionTile -= pageSize;
            }
            else
            {
                selectionTile = 0;
            }

            _currentRow = selectionTile / _cellsPerRow;
            _currentCol = selectionTile % _cellsPerRow;
        }

        private void NavigatePageDown()
        {
            int pageSize = _cellsPerRow * 3; // Navigate 3 rows down
            if (selectionTile + pageSize < contents.Length)
            {
                selectionTile += pageSize;
            }
            else
            {
                selectionTile = contents.Length - 1;
            }

            _currentRow = selectionTile / _cellsPerRow;
            _currentCol = selectionTile % _cellsPerRow;
        }

        private void CalculateBulkSelection()
        {
            if (selectionItems == null) selectionItems = new List<AssetInfo>();
            else selectionItems.Clear();

            GUIContent[] selection = Selection;
            if (packages != null)
            {
                int count = Mathf.Min(selection.Length, packages.Count);
                for (int i = 0; i < count; i++)
                {
                    if (selection[i] == UIStyles.selectedTileContent && packages[i] != null)
                    {
                        selectionItems.Add(packages[i]);
                    }
                }
            }

            UpdateBulkSelectionStats();
        }

        public void SetBulkSelection(List<AssetInfo> items)
        {
            selectionItems = items ?? new List<AssetInfo>();
            UpdateBulkSelectionStats();
        }

        public void SetVisualBulkSelection(List<AssetInfo> items)
        {
            List<AssetInfo> selectedItems = items ?? new List<AssetInfo>();

            if (packages == null)
            {
                SetBulkSelection(selectedItems);
                return;
            }

            HashSet<int> selectedIds = new HashSet<int>();
            for (int i = 0; i < selectedItems.Count; i++)
            {
                if (selectedItems[i] != null) selectedIds.Add(selectedItems[i].AssetId);
            }

            Selection.Populate(UIStyles.emptyTileContent);

            int firstIndex = -1;
            int lastIndex = -1;
            for (int i = 0; i < packages.Count; i++)
            {
                if (!selectedIds.Contains(packages[i].AssetId)) continue;

                Selection[i] = UIStyles.selectedTileContent;
                if (firstIndex == -1) firstIndex = i;
                lastIndex = i;
            }

            if (firstIndex == -1)
            {
                selectionTile = 0;
                _selectionMin = 0;
                _selectionMax = 0;
                _lastSelectionTile = 0;
                if (selectionItems == null) selectionItems = new List<AssetInfo>();
                else selectionItems.Clear();
                UpdateBulkSelectionStats();
                return;
            }

            selectionTile = firstIndex;
            _selectionMin = firstIndex;
            _selectionMax = lastIndex;
            _lastSelectionTile = firstIndex;

            CalculateBulkSelection();
        }

        private void UpdateBulkSelectionStats()
        {
            selectionCount = selectionItems.Count;
            selectionSize = 0;
            _selectionPackageIds.Clear();
            for (int i = 0; i < selectionItems.Count; i++)
            {
                AssetInfo info = selectionItems[i];
                if (info == null) continue;

                selectionSize += info.Size;
                _selectionPackageIds.Add(info.AssetId);
                info.CheckIfInProject();
            }
            selectionPackageCount = _selectionPackageIds.Count;
            Assets.ResolveParents(selectionItems, _allPackagesDict);
            _bulkHandler?.Invoke();
        }

        public void DeselectAll()
        {
            selectionTile = 0;
            _selectionMin = selectionTile;
            _selectionMax = selectionTile;
            _lastSelectionTile = selectionTile;
            MarkGridSelection(true);
            CalculateBulkSelection();
        }

        /// <summary>
        /// Ensures the selected tile is visible in the scroll view by updating the scroll position
        /// </summary>
        /// <param name="scrollPosition">Current scroll position (passed by reference)</param>
        /// <param name="viewHeight">Height of the visible view area</param>
        public void EnsureSelectedTileVisible(ref Vector2 scrollPosition, float viewHeight)
        {
            if (contents == null || contents.Length == 0 || selectionTile < 0 || selectionTile >= contents.Length) return;

            // Use the saved tile height from the last draw operation
            if (_actualTileHeight <= 0f) return; // No valid tile height available yet

            // Calculate the row of the selected tile
            int selectedRow = selectionTile / _cellsPerRow;

            // Calculate the Y position of the selected tile using saved tile height
            float tileY = selectedRow * _actualTileHeight;

            // Calculate the visible range
            float visibleMinY = scrollPosition.y;
            float visibleMaxY = scrollPosition.y + viewHeight;

            // Check if the selected tile is above the visible area
            if (tileY < visibleMinY)
            {
                // Scroll up to show the selected tile at the top with some padding
                scrollPosition.y = Mathf.Max(0, tileY - 20);
            }
            // Check if the selected tile is below the visible area
            else if (tileY + _actualTileHeight > visibleMaxY)
            {
                // Scroll down to show the selected tile fully visible
                // Position the tile so it's visible in the middle of the view
                scrollPosition.y = tileY - (viewHeight / 2) + (_actualTileHeight / 2);

                // Ensure we don't scroll past the end of the content
                if (contents != null && contents.Length > 0)
                {
                    float totalContentHeight = Mathf.CeilToInt((float)contents.Length / _cellsPerRow) * _actualTileHeight;
                    scrollPosition.y = Mathf.Min(scrollPosition.y, totalContentHeight - viewHeight);
                }
            }
        }

        /// <summary>
        /// Checks if keyboard navigation occurred and resets the flag
        /// </summary>
        /// <returns>True if keyboard navigation occurred since last check</returns>
        public bool CheckAndResetKeyboardNavigation()
        {
            bool occurred = _keyboardNavigationOccurred;
            _keyboardNavigationOccurred = false;
            return occurred;
        }

        public void Select(AssetInfo info)
        {
            if (packages != null)
            {
                selectionTile = -1;
                for (int i = 0; i < packages.Count; i++)
                {
                    if (packages[i].AssetId != info.AssetId) continue;

                    selectionTile = i;
                    break;
                }
            }
            else
            {
                selectionTile = 0;
            }
            MarkGridSelection(true);

            _selectionMin = selectionTile;
            _selectionMax = selectionTile;

            CalculateBulkSelection();
        }
    }
}
