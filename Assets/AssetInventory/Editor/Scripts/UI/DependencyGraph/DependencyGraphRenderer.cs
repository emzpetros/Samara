using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    public class DependencyGraphRenderer
    {
        // View transform
        private Vector2 _panOffset = Vector2.zero;
        private float _zoom = 1f;
        private float _minZoom = 0.1f;
        private float _maxZoom = 2f;

        // Interaction state
        private DependencyGraphNode _hoveredNode;
        private PackageNode _selectedPackage;
        private PackageNode _hoveredPackage;
        private bool _isDragging;
        private Vector2 _dragStartPos;
        private Vector2 _lastMousePos;
        private double _lastClickTime;
        private PackageNode _lastClickedPackage;

        // Rendering settings
        private float _edgeWidth = 2f;
        private float _arrowSize = 8f;
        private bool _showLabels = true;
        private bool _showIcons = true;
        private bool _showArrows = true;

        // LOD settings
        private float _labelLodThreshold = 0.3f;
        private float _iconLodThreshold = 0.2f;

        // Colors
        private readonly Color _selectionColor = new Color(1f, 1f, 0f, 0.8f);
        private readonly Color _hoverColor = new Color(1f, 1f, 1f, 0.5f);
        private readonly Color _backgroundColor = new Color(0.2f, 0.2f, 0.2f, 1f);
        private readonly Color _gridColor = new Color(0.25f, 0.25f, 0.25f, 0.3f); // More subtle grid

        // Cached styles
        private GUIStyle _nodeLabelStyle;
        private GUIStyle _badgeStyle;

        public DependencyGraphNode HoveredNode => _hoveredNode;

        // Events
        public event Action<DependencyGraphNode> OnNodeDoubleClicked;
        public event Action<DependencyGraphNode> OnNodeRightClicked;
        public event Action<PackageNode> OnPackageClicked;
        public event Action<PackageNode> OnPackageDoubleClicked;
        public event Action<PackageNode> OnPackageRightClicked;

        public DependencyGraphRenderer()
        {
            InitializeStyles();
        }

        private void InitializeStyles()
        {
            _nodeLabelStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.UpperCenter,
                wordWrap = false,
                fontSize = 8,
                normal = {textColor = Color.white}
            };

            _badgeStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 8,
                fontStyle = FontStyle.Bold,
                normal = {textColor = Color.white}
            };
        }

        public void Render(Rect viewRect, DependencyGraphData graphData, ForceDirectedLayout layout)
        {
            if (graphData.Nodes.Count == 0)
            {
                DrawEmptyState(viewRect);
                return;
            }

            // Calculate view center in screen space (absolute coordinates for bounds calculation)
            Vector2 viewCenter = new Vector2(viewRect.x + viewRect.width / 2f, viewRect.y + viewRect.height / 2f);

            // Calculate offset: graph space (0,0) should appear at viewCenter + pan
            Vector2 graphOriginScreen = viewCenter + _panOffset * _zoom;

            // Update bounds for hit testing (screen space coordinates) - BEFORE input handling
            UpdateNodeBounds(graphData, graphOriginScreen, _zoom);
            UpdatePackageBounds(graphData, graphOriginScreen, _zoom);

            // Handle input with updated bounds
            HandleInput(viewRect, graphData);

            // Draw background
            EditorGUI.DrawRect(viewRect, _backgroundColor);

            // Begin clipping group - shifts to local coordinates and clips drawing
            GUI.BeginGroup(viewRect);

            // Create local rect
            Rect localRect = new Rect(0, 0, viewRect.width, viewRect.height);

            // Convert graphOriginScreen to local space (subtract viewRect.position)
            Vector2 graphOriginLocal = graphOriginScreen - viewRect.position;

            // Draw grid in local space
            DrawGrid(localRect);

            // Draw graph content in local space (BeginGroup automatically clips)
            Rect localViewRect = new Rect(0, 0, viewRect.width, viewRect.height);
            DrawPackages(graphData, graphOriginLocal, _zoom, localViewRect);
            DrawEdges(graphData, graphOriginLocal, _zoom, localViewRect);
            DrawNodes(graphData, graphOriginLocal, _zoom, localViewRect);

            // Draw UI overlays in local space
            DrawOverlays(localRect, graphData);

            // End clipping group
            GUI.EndGroup();

            // Update physics if not stable
            if (!layout.IsStable)
            {
                layout.Update(graphData, 0.016f); // Assume ~60 FPS
            }
        }

        private void HandleInput(Rect viewRect, DependencyGraphData graphData)
        {
            Event e = Event.current;
            Vector2 mousePos = e.mousePosition;

            if (!viewRect.Contains(mousePos))
            {
                return;
            }

            // Only update hover state for interactive events, not during Layout/Repaint
            if (e.type == EventType.MouseMove || e.type == EventType.MouseDown ||
                e.type == EventType.MouseDrag || e.type == EventType.MouseUp ||
                e.type == EventType.ScrollWheel)
            {
                // Bounds are now in screen space, so use mousePos directly for hit testing
                _hoveredPackage = GetPackageAtPosition(graphData, mousePos);
                _hoveredNode = _hoveredPackage == null ? GetNodeAtPosition(graphData, mousePos) : null;
            }

            switch (e.type)
            {
                case EventType.MouseDown:
                    if (e.button == 0) // Left click
                    {
                        if (_hoveredPackage != null)
                        {
                            // Only trigger expand/collapse if clicking on the header
                            if (_hoveredPackage.HeaderContainsPoint(mousePos, _hoveredPackage.HeaderHeight * _zoom))
                            {
                                // Check for double-click on package header
                                double timeSinceLastClick = EditorApplication.timeSinceStartup - _lastClickTime;
                                if (_hoveredPackage == _lastClickedPackage && timeSinceLastClick < 0.3)
                                {
                                    OnPackageDoubleClicked?.Invoke(_hoveredPackage);
                                    e.Use();
                                }
                                else
                                {
                                    _selectedPackage = _hoveredPackage;
                                    OnPackageClicked?.Invoke(_hoveredPackage);
                                    _lastClickTime = EditorApplication.timeSinceStartup;
                                    _lastClickedPackage = _hoveredPackage;
                                    e.Use();
                                }
                            }
                            // If clicking on body (not header), pass through to node selection
                            else
                            {
                                // Check if we clicked on a node within the package
                                _hoveredNode = GetNodeAtPosition(graphData, mousePos);
                                if (_hoveredNode != null)
                                {
                                    // Check for double-click on node
                                    if (e.clickCount == 2)
                                    {
                                        OnNodeDoubleClicked?.Invoke(_hoveredNode);
                                        e.Use();
                                    }
                                    else
                                    {
                                        // Single click on node - just consume the event
                                        e.Use();
                                    }
                                }
                                else
                                {
                                    // Clicked on empty space in package, start panning
                                    _isDragging = true;
                                    _dragStartPos = mousePos;
                                    _lastMousePos = mousePos;
                                    e.Use();
                                }
                            }
                        }
                        else if (_hoveredNode != null)
                        {
                            // Check for double-click on node
                            if (e.clickCount == 2)
                            {
                                OnNodeDoubleClicked?.Invoke(_hoveredNode);
                                e.Use();
                            }
                            else
                            {
                                // Single click on node - just consume the event
                                e.Use();
                            }
                        }
                        else
                        {
                            // Start panning
                            _isDragging = true;
                            _dragStartPos = mousePos;
                            _lastMousePos = mousePos;
                            e.Use();
                        }
                    }
                    else if (e.button == 1) // Right click
                    {
                        if (_hoveredPackage != null)
                        {
                            OnPackageRightClicked?.Invoke(_hoveredPackage);
                            e.Use();
                        }
                        else if (_hoveredNode != null)
                        {
                            OnNodeRightClicked?.Invoke(_hoveredNode);
                            e.Use();
                        }
                    }
                    break;

                case EventType.MouseDrag:
                    if (_isDragging && e.button == 0)
                    {
                        Vector2 delta = mousePos - _lastMousePos;
                        _panOffset += delta / _zoom;
                        _lastMousePos = mousePos;
                        e.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (e.button == 0)
                    {
                        _isDragging = false;
                        e.Use();
                    }
                    break;

                case EventType.ScrollWheel:
                    // Zoom centered on mouse position
                    float zoomDelta = -e.delta.y * 0.05f;
                    float oldZoom = _zoom;
                    _zoom = Mathf.Clamp(_zoom + zoomDelta, _minZoom, _maxZoom);

                    // Adjust pan to keep the point under the mouse cursor at the same screen position
                    // Calculate mouse offset from view center in screen space
                    Vector2 viewCenter = new Vector2(viewRect.width / 2f, viewRect.height / 2f);
                    Vector2 mouseOffsetFromCenter = mousePos - viewRect.position - viewCenter;

                    // Apply zoom adjustment to keep mouse position stable
                    _panOffset += mouseOffsetFromCenter * (1f / _zoom - 1f / oldZoom);

                    // Clear hover state during zoom to prevent stale hover detection
                    // Bounds will be recalculated next frame with new zoom
                    _hoveredPackage = null;
                    _hoveredNode = null;

                    e.Use();
                    break;

                case EventType.KeyDown:
                    if (e.keyCode == KeyCode.F) // Frame all
                    {
                        FrameAll(viewRect, graphData);
                        e.Use();
                    }
                    break;
            }
        }

        private void DrawGrid(Rect localRect)
        {
            float gridSpacing = 100f * _zoom;

            if (gridSpacing < 20f) return; // Don't draw grid when too zoomed out

            Vector2 offset = _panOffset * _zoom;

            // Vertical lines - draw in local space (BeginGroup handles clipping)
            float startX = (offset.x % gridSpacing);
            for (float x = startX; x < localRect.width; x += gridSpacing)
            {
                EditorGUI.DrawRect(new Rect(x, 0, 1, localRect.height), _gridColor);
            }

            // Horizontal lines - draw in local space
            float startY = (offset.y % gridSpacing);
            for (float y = startY; y < localRect.height; y += gridSpacing)
            {
                EditorGUI.DrawRect(new Rect(0, y, localRect.width, 1), _gridColor);
            }
        }

        private void DrawEdges(DependencyGraphData graphData, Vector2 graphOriginLocal, float zoom, Rect localViewRect)
        {
            Handles.BeginGUI();

            // View frustum culling for edges in local space
            // Scale margin inversely with zoom to maintain consistent culling distance in graph space
            float margin = 100f / Mathf.Max(zoom, 0.01f);
            Rect cullRect = new Rect(
                -margin,
                -margin,
                localViewRect.width + margin * 2,
                localViewRect.height + margin * 2
            );

            foreach (DependencyGraphEdge edge in graphData.Edges)
            {
                if (!edge.ShouldRender()) continue;

                // Transform graph positions to local positions
                Vector2 start = graphOriginLocal + edge.Source.Position * zoom;
                Vector2 end = graphOriginLocal + edge.Target.Position * zoom;

                // Cull edges that are completely outside view
                if (!IsEdgeInView(start, end, cullRect))
                {
                    continue;
                }

                // Determine edge type and style
                bool isInternal = edge.Source.PackageNode == edge.Target.PackageNode &&
                    edge.Source.PackageNode != null;
                bool isCrossPackage = edge.Source.PackageNode != edge.Target.PackageNode &&
                    edge.Source.PackageNode != null &&
                    edge.Target.PackageNode != null;

                Color edgeColor;
                float edgeWidth;

                if (edge.IsPartOfCycle)
                {
                    // Cycles - red and thick
                    edgeColor = new Color(1f, 0.3f, 0.3f, 1f);
                    edgeWidth = 3.5f;
                }
                else if (isCrossPackage)
                {
                    // Cross-package - bright purple/magenta and thick
                    edgeColor = new Color(0.9f, 0.5f, 0.9f, 0.95f);
                    edgeWidth = 3.5f;
                }
                else if (isInternal)
                {
                    // Internal package - brighter gray/cyan tint for better visibility
                    edgeColor = new Color(0.6f, 0.7f, 0.75f, 0.85f);
                    edgeWidth = 2f;
                }
                else
                {
                    // Default - brighter
                    edgeColor = new Color(0.7f, 0.7f, 0.8f, 0.8f);
                    edgeWidth = _edgeWidth;
                }

                Handles.color = edgeColor;

                if (isCrossPackage || edge.IsCrossDependency)
                {
                    // Draw bezier curve for cross-package dependencies
                    Vector2 mid = (start + end) / 2f;
                    Vector2 perpendicular = new Vector2(-(end.y - start.y), end.x - start.x).normalized;
                    Vector2 controlPoint = mid + perpendicular * 50f;

                    Handles.DrawBezier(start, end, controlPoint, controlPoint, edgeColor, null, edgeWidth);
                }
                else
                {
                    // Draw straight line for internal dependencies
                    Handles.DrawAAPolyLine(edgeWidth, start, end);
                }

                // Draw arrow (larger for cross-package edges)
                if (_showArrows && _zoom > 0.3f)
                {
                    float arrowSize = isCrossPackage ? _arrowSize * 1.5f : _arrowSize;
                    DrawArrow(end, (end - start).normalized, arrowSize, edgeColor);
                }
            }

            Handles.EndGUI();
        }

        private void DrawArrow(Vector2 position, Vector2 direction, float size, Color color)
        {
            Handles.color = color;

            Vector2 right = new Vector2(-direction.y, direction.x);
            Vector2 p1 = position - direction * size + right * size * 0.5f;
            Vector2 p2 = position - direction * size - right * size * 0.5f;

            Handles.DrawAAConvexPolygon(position, p1, p2);
        }

        private void DrawNodes(DependencyGraphData graphData, Vector2 graphOriginLocal, float zoom, Rect localViewRect)
        {
            // View frustum culling for nodes in local space
            // Scale margin inversely with zoom to maintain consistent culling distance in graph space
            float margin = 100f / Mathf.Max(zoom, 0.01f);
            Rect cullRect = new Rect(
                -margin,
                -margin,
                localViewRect.width + margin * 2,
                localViewRect.height + margin * 2
            );

            foreach (DependencyGraphNode node in graphData.Nodes)
            {
                if (!node.IsVisible) continue;

                // Transform graph position to local position
                Vector2 localPos = graphOriginLocal + node.Position * zoom;
                float size = node.Size * zoom;
                Rect localNodeBounds = new Rect(localPos.x - size / 2f, localPos.y - size / 2f, size, size);

                // Cull if outside visible area
                if (!localNodeBounds.Overlaps(cullRect))
                {
                    continue;
                }

                DrawNode(node, localPos, zoom);
            }
        }

        private void DrawNode(DependencyGraphNode node, Vector2 screenPos, float zoom)
        {
            // Scale node size with zoom
            float size = node.Size * zoom;
            Rect nodeRect = new Rect(screenPos.x - size / 2f, screenPos.y - size / 2f, size, size);

            // Draw node background
            Color bgColor = node.Color;
            if (node.IsPartOfCycle)
            {
                // Red outline for cycles (scaled)
                float outlineWidth = 3 * zoom;
                EditorGUI.DrawRect(new Rect(nodeRect.x - outlineWidth, nodeRect.y - outlineWidth,
                        nodeRect.width + outlineWidth * 2, nodeRect.height + outlineWidth * 2),
                    new Color(1f, 0.2f, 0.2f, 1f));
            }

            if (node == _hoveredNode)
            {
                // Hover highlight (scaled)
                float hoverWidth = 2 * zoom;
                EditorGUI.DrawRect(new Rect(nodeRect.x - hoverWidth, nodeRect.y - hoverWidth,
                        nodeRect.width + hoverWidth * 2, nodeRect.height + hoverWidth * 2),
                    _hoverColor);
            }

            // Draw node body
            EditorGUI.DrawRect(nodeRect, bgColor);

            // Draw border (scaled)
            Color borderColor = EditorGUIUtility.isProSkin ? Color.black : Color.gray;
            DrawRectOutline(nodeRect, borderColor, 2f * zoom);

            // Draw icon (LOD based on zoom)
            if (_showIcons && zoom > _iconLodThreshold && node.Icon != null)
            {
                float iconSize = size * 0.5f;
                Rect iconRect = new Rect(
                    screenPos.x - iconSize / 2f,
                    screenPos.y - iconSize / 2f,
                    iconSize,
                    iconSize
                );
                GUI.DrawTexture(iconRect, node.Icon);
            }

            // Draw label below node (LOD based on zoom)
            if (_showLabels && zoom > _labelLodThreshold)
            {
                string label = node.GetDisplayName();
                GUIStyle scaledLabelStyle = new GUIStyle(_nodeLabelStyle)
                {
                    fontSize = Mathf.Max(8, Mathf.RoundToInt(_nodeLabelStyle.fontSize * zoom))
                };
                Vector2 labelSize = scaledLabelStyle.CalcSize(new GUIContent(label));
                Rect labelRect = new Rect(
                    screenPos.x - labelSize.x / 2f,
                    screenPos.y + size / 2f + 4f * zoom,
                    labelSize.x,
                    labelSize.y
                );

                // Draw label background (scaled padding)
                float labelPadding = 2 * zoom;
                EditorGUI.DrawRect(new Rect(labelRect.x - labelPadding, labelRect.y, labelRect.width + labelPadding * 2, labelRect.height),
                    new Color(0, 0, 0, 0.7f));
                GUI.Label(labelRect, label, scaledLabelStyle);
            }

            // Draw badge for hidden dependencies
            if (node.HasHiddenDependencies)
            {
                float badgeSize = 16f;
                Rect badgeRect = new Rect(
                    screenPos.x + size / 2f - badgeSize / 2f,
                    screenPos.y - size / 2f - badgeSize / 2f,
                    badgeSize,
                    badgeSize
                );

                EditorGUI.DrawRect(badgeRect, new Color(1f, 0.5f, 0f, 0.9f));
                DrawRectOutline(badgeRect, Color.white, 1f);
                GUI.Label(badgeRect, node.HiddenDependencyCount.ToString(), _badgeStyle);
            }

            // Draw root indicator
            if (node.IsRoot)
            {
                float indicatorSize = 6f;
                Rect indicatorRect = new Rect(
                    screenPos.x - indicatorSize / 2f,
                    screenPos.y - size / 2f - indicatorSize - 3f,
                    indicatorSize,
                    indicatorSize
                );
                EditorGUI.DrawRect(indicatorRect, new Color(1f, 1f, 0f, 1f));
            }
        }

        private void DrawRectOutline(Rect rect, Color color, float thickness)
        {
            // Top
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, thickness), color);
            // Bottom
            EditorGUI.DrawRect(new Rect(rect.x, rect.y + rect.height - thickness, rect.width, thickness), color);
            // Left
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, thickness, rect.height), color);
            // Right
            EditorGUI.DrawRect(new Rect(rect.x + rect.width - thickness, rect.y, thickness, rect.height), color);
        }

        private void DrawOverlays(Rect viewRect, DependencyGraphData graphData)
        {
            // Legend removed per user request
            // DrawLegend(viewRect);

            // Draw zoom level (anchored to bottom-left, in group space)
            string zoomText = $"Zoom: {(_zoom * 100f):F0}%";
            GUI.Label(new Rect(10, viewRect.height - 25, 100, 20), zoomText, EditorStyles.miniLabel);

            // Draw node count (anchored to bottom-left, in group space)
            int visibleCount = graphData.Nodes.Count(n => n.IsVisible);
            string countText = $"Nodes: {visibleCount}/{graphData.Nodes.Count}";
            GUI.Label(new Rect(10, viewRect.height - 45, 150, 20), countText, EditorStyles.miniLabel);
        }

        private void DrawLegend(Rect viewRect)
        {
            float legendWidth = 200f;
            float legendHeight = 130f;
            float padding = 10f;
            Rect legendRect = new Rect(
                viewRect.width - legendWidth - padding,
                viewRect.height - legendHeight - padding,
                legendWidth,
                legendHeight
            );

            GUI.Box(legendRect, "", EditorStyles.helpBox);

            GUILayout.BeginArea(new Rect(legendRect.x + 5, legendRect.y + 5, legendRect.width - 10, legendRect.height - 10));

            GUIStyle boldLabel = new GUIStyle(EditorStyles.miniLabel) {fontStyle = FontStyle.Bold};
            GUIStyle smallLabel = new GUIStyle(EditorStyles.miniLabel) {fontSize = 9};

            EditorGUILayout.LabelField("Legend", boldLabel);
            EditorGUILayout.Space(2);

            // Node colors
            GUILayout.BeginHorizontal();
            EditorGUI.DrawRect(new Rect(legendRect.x + 8, legendRect.y + 25, 12, 12), new Color(0.3f, 0.6f, 1f));
            GUILayout.Space(18);
            EditorGUILayout.LabelField("Root Asset", smallLabel);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            EditorGUI.DrawRect(new Rect(legendRect.x + 8, legendRect.y + 40, 12, 12), new Color(0.3f, 0.8f, 0.3f));
            GUILayout.Space(18);
            EditorGUILayout.LabelField("In Project", smallLabel);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            EditorGUI.DrawRect(new Rect(legendRect.x + 8, legendRect.y + 55, 12, 12), new Color(1f, 0.7f, 0.2f));
            GUILayout.Space(18);
            EditorGUILayout.LabelField("Needs Import", smallLabel);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            EditorGUI.DrawRect(new Rect(legendRect.x + 8, legendRect.y + 70, 12, 12), new Color(0.5f, 0.5f, 1f));
            GUILayout.Space(18);
            EditorGUILayout.LabelField("SRP Support", smallLabel);
            GUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            // Edge colors
            Handles.BeginGUI();
            Handles.color = new Color(0.5f, 0.5f, 0.5f, 0.8f);
            Handles.DrawAAPolyLine(2f, new Vector3(legendRect.x + 8, legendRect.y + 93), new Vector3(legendRect.x + 20, legendRect.y + 93));
            Handles.EndGUI();
            GUILayout.BeginHorizontal();
            GUILayout.Space(26);
            EditorGUILayout.LabelField("Direct Dependency", smallLabel);
            GUILayout.EndHorizontal();

            Handles.BeginGUI();
            Handles.color = new Color(0.8f, 0.4f, 0.8f, 0.8f);
            Handles.DrawAAPolyLine(3f, new Vector3(legendRect.x + 8, legendRect.y + 108), new Vector3(legendRect.x + 20, legendRect.y + 108));
            Handles.EndGUI();
            GUILayout.BeginHorizontal();
            GUILayout.Space(26);
            EditorGUILayout.LabelField("Cross-Package (Thick)", smallLabel);
            GUILayout.EndHorizontal();

            GUILayout.EndArea();
        }

        private void DrawEmptyState(Rect viewRect)
        {
            EditorGUI.DrawRect(viewRect, _backgroundColor);

            GUIStyle centerStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14,
                normal = {textColor = Color.gray}
            };

            GUI.Label(viewRect, "No dependencies to display", centerStyle);
        }

        private void UpdateNodeBounds(DependencyGraphData graphData, Vector2 graphOriginScreen, float zoom)
        {
            foreach (DependencyGraphNode node in graphData.Nodes)
            {
                if (!node.IsVisible) continue;

                // Calculate bounds in screen space for hit testing
                Vector2 screenPos = graphOriginScreen + node.Position * zoom;
                float size = node.Size * zoom;
                node.Bounds = new Rect(
                    screenPos.x - size / 2f,
                    screenPos.y - size / 2f,
                    size,
                    size
                );

                // Calculate full bounds including label for collision detection (also graph space)
                string label = node.GetDisplayName();
                Vector2 labelSize = _nodeLabelStyle.CalcSize(new GUIContent(label));

                float fullWidth = Mathf.Max(size, labelSize.x);
                float fullHeight = size + labelSize.y + 8f; // 4px spacing + label height

                node.FullBounds = new Rect(
                    node.Position.x - fullWidth / 2f,
                    node.Position.y - size / 2f,
                    fullWidth,
                    fullHeight
                );
            }
        }

        private DependencyGraphNode GetNodeAtPosition(DependencyGraphData graphData, Vector2 graphPos)
        {
            // Check in reverse order so top nodes are hit first
            for (int i = graphData.Nodes.Count - 1; i >= 0; i--)
            {
                DependencyGraphNode node = graphData.Nodes[i];
                if (node.IsVisible && node.Bounds.Contains(graphPos))
                {
                    return node;
                }
            }
            return null;
        }

        public void FrameAll(Rect viewRect, DependencyGraphData graphData)
        {
            if (graphData.Nodes.Count == 0) return;

            Rect bounds = graphData.GetBoundingBox();

            // Calculate zoom to fit
            float xZoom = viewRect.width / (bounds.width + 100f);
            float yZoom = viewRect.height / (bounds.height + 100f);
            _zoom = Mathf.Clamp(Mathf.Min(xZoom, yZoom), _minZoom, _maxZoom);

            // Center on bounds
            _panOffset = -new Vector2(bounds.center.x, bounds.center.y);
        }

        public void FocusOnNode(DependencyGraphNode node)
        {
            if (node == null) return;
            _panOffset = -node.Position;
        }

        public void ResetView(DependencyGraphData graphData = null)
        {
            _zoom = 1f;

            // Center on graph content if available
            if (graphData != null && graphData.Nodes.Count > 0)
            {
                Rect bounds = graphData.GetBoundingBox();
                _panOffset = -new Vector2(bounds.center.x, bounds.center.y);
            }
            else
            {
                _panOffset = Vector2.zero;
            }
        }

        public void SetShowLabels(bool show)
        {
            _showLabels = show;
        }

        public void SetShowIcons(bool show)
        {
            _showIcons = show;
        }

        // Culling helpers
        private bool IsNodeInView(Vector2 nodePos, float nodeSize, Rect viewBounds)
        {
            float halfSize = nodeSize / 2f;
            Rect nodeBounds = new Rect(nodePos.x - halfSize, nodePos.y - halfSize, nodeSize, nodeSize);
            return viewBounds.Overlaps(nodeBounds);
        }

        private bool IsEdgeInView(Vector2 start, Vector2 end, Rect viewBounds)
        {
            // Check if either endpoint is in view
            if (viewBounds.Contains(start) || viewBounds.Contains(end))
            {
                return true;
            }

            // Check if edge crosses view bounds
            return LineIntersectsRect(start, end, viewBounds);
        }

        private bool LineIntersectsRect(Vector2 p1, Vector2 p2, Rect rect)
        {
            // Simple AABB line intersection test
            float minX = Mathf.Min(p1.x, p2.x);
            float maxX = Mathf.Max(p1.x, p2.x);
            float minY = Mathf.Min(p1.y, p2.y);
            float maxY = Mathf.Max(p1.y, p2.y);

            return !(maxX < rect.xMin || minX > rect.xMax || maxY < rect.yMin || minY > rect.yMax);
        }

        // Package rendering methods
        private void UpdatePackageBounds(DependencyGraphData graphData, Vector2 graphOriginScreen, float zoom)
        {
            foreach (PackageNode package in graphData.Packages)
            {
                if (!package.IsVisible) continue;

                // Calculate bounds in graph space first
                Rect graphBounds = package.CalculateBounds();

                // Transform to screen space for hit testing
                Vector2 screenPos = graphOriginScreen + graphBounds.position * zoom;
                package.Bounds = new Rect(
                    screenPos.x,
                    screenPos.y,
                    graphBounds.width * zoom,
                    graphBounds.height * zoom
                );
            }
        }

        private PackageNode GetPackageAtPosition(DependencyGraphData graphData, Vector2 position)
        {
            // Check in reverse order so top packages are hit first
            for (int i = graphData.Packages.Count - 1; i >= 0; i--)
            {
                PackageNode package = graphData.Packages[i];
                if (package.IsVisible && package.ContainsPoint(position))
                {
                    return package;
                }
            }
            return null;
        }

        private void DrawPackages(DependencyGraphData graphData, Vector2 graphOriginLocal, float zoom, Rect localViewRect)
        {
            // View frustum culling - only draw packages visible in local view
            // Scale margin inversely with zoom to maintain consistent culling distance in graph space
            float margin = 100f / Mathf.Max(zoom, 0.01f);
            Rect cullRect = new Rect(
                -margin,
                -margin,
                localViewRect.width + margin * 2,
                localViewRect.height + margin * 2
            );

            foreach (PackageNode package in graphData.Packages)
            {
                if (!package.IsVisible) continue;

                // Calculate package position in local space for culling
                Rect graphBounds = package.CalculateBounds();
                Vector2 localPos = graphOriginLocal + graphBounds.position * zoom;
                Rect localBounds = new Rect(localPos.x, localPos.y, graphBounds.width * zoom, graphBounds.height * zoom);

                // Cull if outside visible area
                if (!localBounds.Overlaps(cullRect))
                {
                    continue;
                }

                DrawPackage(package, graphOriginLocal, zoom);
            }
        }

        private void DrawPackage(PackageNode package, Vector2 graphOriginLocal, float zoom)
        {
            // Calculate bounds in local space
            Rect graphBounds = package.CalculateBounds();
            Vector2 localPos = graphOriginLocal + graphBounds.position * zoom;
            Rect bounds = new Rect(localPos.x, localPos.y, graphBounds.width * zoom, graphBounds.height * zoom);

            // Draw package background
            Color bgColor = package.Color;
            EditorGUI.DrawRect(bounds, bgColor);

            // Draw border (scaled)
            Color borderColor = package == _selectedPackage ? Color.yellow :
                package == _hoveredPackage ? Color.white :
                new Color(bgColor.r * 1.5f, bgColor.g * 1.5f, bgColor.b * 1.5f, 1f);
            float borderWidth = (package == _selectedPackage ? 3f : 2f) * zoom;
            DrawRectOutline(bounds, borderColor, borderWidth);

            // Draw header (scaled height)
            Rect headerRect = new Rect(bounds.x, bounds.y, bounds.width, package.HeaderHeight * zoom);
            EditorGUI.DrawRect(headerRect, new Color(bgColor.r * 0.7f, bgColor.g * 0.7f, bgColor.b * 0.7f, 0.8f));

            // Draw package name (scaled font)
            GUIStyle packageNameStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = Mathf.Max(8, Mathf.RoundToInt(10 * zoom)),
                normal = {textColor = Color.white}
            };

            Rect nameRect = new Rect(headerRect.x + 5 * zoom, headerRect.y, headerRect.width - 30 * zoom, headerRect.height);
            GUI.Label(nameRect, package.Name, packageNameStyle);

            // Draw file count badge (scaled)
            string countText = package.Files.Count.ToString();
            GUIStyle countStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = Mathf.Max(7, Mathf.RoundToInt(9 * zoom)),
                fontStyle = FontStyle.Bold,
                normal = {textColor = Color.white}
            };

            Rect badgeRect = new Rect(headerRect.x + headerRect.width - 25 * zoom, headerRect.y + 5 * zoom, 20 * zoom, 15 * zoom);
            EditorGUI.DrawRect(badgeRect, new Color(0.2f, 0.2f, 0.2f, 0.8f));
            GUI.Label(badgeRect, countText, countStyle);

            // Draw expand/collapse indicator (scaled)
            string indicator = package.IsExpanded ? "▼" : "▶";
            Rect indicatorRect = new Rect(headerRect.x + headerRect.width - 45 * zoom, headerRect.y, 15 * zoom, headerRect.height);
            GUI.Label(indicatorRect, indicator, packageNameStyle);

            // If collapsed and contains root node, draw the blue root icon inside
            if (!package.IsExpanded)
            {
                DependencyGraphNode rootNode = package.Files.FirstOrDefault(f => f.IsRoot);
                if (rootNode != null)
                {
                    // Calculate center of package body (below header) - scaled
                    float bodyHeight = bounds.height - package.HeaderHeight * zoom;
                    float iconSize = Mathf.Min(32f * zoom, bodyHeight * 0.6f); // Limit icon size, scale with zoom
                    Vector2 center = new Vector2(
                        bounds.x + bounds.width / 2f,
                        bounds.y + package.HeaderHeight * zoom + bodyHeight / 2f
                    );

                    Rect iconRect = new Rect(
                        center.x - iconSize / 2f,
                        center.y - iconSize / 2f,
                        iconSize,
                        iconSize
                    );

                    // Draw blue background circle for root node
                    Color rootColor = new Color(0.3f, 0.6f, 1f, 0.9f);
                    DrawCircle(iconRect.center, iconSize / 2f, rootColor);

                    // Draw icon if available
                    if (rootNode.Icon != null)
                    {
                        GUI.DrawTexture(iconRect, rootNode.Icon, ScaleMode.ScaleToFit);
                    }

                    // Draw border around icon
                    DrawCircleOutline(iconRect.center, iconSize / 2f, Color.white, 2f * zoom);
                }
            }
        }

        private void DrawCircle(Vector2 center, float radius, Color color)
        {
            Handles.BeginGUI();
            Handles.color = color;
            Handles.DrawSolidDisc(center, Vector3.forward, radius);
            Handles.EndGUI();
        }

        private void DrawCircleOutline(Vector2 center, float radius, Color color, float thickness)
        {
            Handles.BeginGUI();
            Handles.color = color;
            Handles.DrawWireDisc(center, Vector3.forward, radius, thickness);
            Handles.EndGUI();
        }
    }
}
