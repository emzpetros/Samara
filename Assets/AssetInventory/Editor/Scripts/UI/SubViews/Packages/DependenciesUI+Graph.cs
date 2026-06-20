using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    public partial class DependenciesUI
    {
        // Graph visualization fields
        private enum ViewMode { List, Graph }
        private ViewMode _viewMode = ViewMode.Graph;
        private bool _showAllDependencies;
        private int _serializedAssetInfoId = -1;

        private DependencyGraphData _graphData;
        private DependencyGraphRenderer _graphRenderer;
        private ForceDirectedLayout _forceLayout;
        private HierarchicalRadialLayout _hierarchicalLayout;
        private bool _useHierarchicalLayout = true;
        private bool _graphNeedsRebuild = true;
        private bool _needsInitialFrame = true;

        private void InitializeGraph()
        {
            // Always reinitialize the graph objects after domain reload
            if (_graphData == null)
            {
                _graphData = new DependencyGraphData();
                _graphNeedsRebuild = true;
            }

            if (_graphRenderer == null)
            {
                _graphRenderer = new DependencyGraphRenderer();
                _graphRenderer.OnNodeDoubleClicked += OnGraphNodeDoubleClicked;
                _graphRenderer.OnNodeRightClicked += OnGraphNodeRightClicked;
                _graphRenderer.OnPackageClicked += OnGraphPackageClicked;
                _graphRenderer.OnPackageDoubleClicked += OnGraphPackageDoubleClicked;
                _graphRenderer.OnPackageRightClicked += OnGraphPackageRightClicked;
            }

            if (_forceLayout == null)
            {
                _forceLayout = new ForceDirectedLayout();
                _graphNeedsRebuild = true;
            }

            if (_hierarchicalLayout == null)
            {
                _hierarchicalLayout = new HierarchicalRadialLayout();
                _graphNeedsRebuild = true;
            }

            // Rebuild graph if needed and we have valid info
            if (_graphNeedsRebuild && _info != null && _info.Id != 0)
            {
                _graphData.BuildFromAssetInfo(_info);

                // Make all files visible initially
                foreach (DependencyGraphNode node in _graphData.Nodes)
                {
                    node.IsVisible = true;
                }

                // Expand all packages
                foreach (PackageNode package in _graphData.Packages)
                {
                    package.IsExpanded = true;
                }

                // Set initial view mode
                _graphData.SetSimplifiedMode(!_showAllDependencies);

                if (_useHierarchicalLayout)
                {
                    // Use hierarchical radial layout
                    _hierarchicalLayout.AutoAdjustParameters(_graphData);
                    _hierarchicalLayout.InitializeHierarchicalPositions(_graphData);
                    _hierarchicalLayout.UpdatePackagePositions(_graphData);
                }
                else
                {
                    // Use force-directed layout (legacy)
                    _forceLayout.AutoAdjustParameters(_graphData.Nodes.Count);
                    _forceLayout.InitializePackagePositions(_graphData);

                    foreach (PackageNode package in _graphData.Packages)
                    {
                        ReinitializePackageFilePositions(package);
                    }

                    _forceLayout.RunIterations(_graphData, 50);
                    UpdatePackageBoundsAfterLayout(_graphData);
                }

                _graphNeedsRebuild = false;
                _needsInitialFrame = true; // Frame the view after first render
            }
        }

        private void OnGraphNodeDoubleClicked(DependencyGraphNode node)
        {
            if (node == null) return;

            // If node has hidden dependencies, expand it
            if (node.HasHiddenDependencies)
            {
                _graphData.ExpandNode(node);

                if (_useHierarchicalLayout)
                {
                    _hierarchicalLayout.InitializeHierarchicalPositions(_graphData);
                    _hierarchicalLayout.UpdatePackagePositions(_graphData);
                }
                else
                {
                    _forceLayout.RunIterations(_graphData, 30);
                }
            }
        }

        private void OnGraphNodeRightClicked(DependencyGraphNode node)
        {
            if (node == null) return;

            GenericMenu menu = new GenericMenu();

            if (node.HasHiddenDependencies)
            {
                menu.AddItem(new GUIContent("Expand Dependencies"), false, () =>
                {
                    _graphData.ExpandNode(node);

                    if (_useHierarchicalLayout)
                    {
                        _hierarchicalLayout.InitializeHierarchicalPositions(_graphData);
                        _hierarchicalLayout.UpdatePackagePositions(_graphData);
                    }
                    else
                    {
                        _forceLayout.RunIterations(_graphData, 30);
                    }

                    Repaint();
                });
            }

            if (node.IsExpanded && !node.IsRoot)
            {
                menu.AddItem(new GUIContent("Collapse Dependencies"), false, () =>
                {
                    _graphData.CollapseNode(node);
                    Repaint();
                });
            }

            if (!string.IsNullOrEmpty(node.AssetFile.ProjectPath))
            {
                menu.AddItem(new GUIContent("Reveal in Project"), false, () =>
                {
                    EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Object>(node.AssetFile.ProjectPath));
                });
            }

            menu.AddItem(new GUIContent("Copy Path"), false, () =>
            {
                EditorGUIUtility.systemCopyBuffer = node.AssetFile.Path;
            });

            menu.ShowAsContext();
        }

        private void OnGraphPackageClicked(PackageNode package)
        {
            // Toggle expand/collapse on single click
            if (package != null)
            {
                package.ToggleExpanded();

                if (_graphData != null)
                {
                    if (package.IsExpanded)
                    {
                        if (_useHierarchicalLayout)
                        {
                            // Re-run hierarchical layout to incorporate newly visible nodes
                            _hierarchicalLayout.InitializeHierarchicalPositions(_graphData);
                            _hierarchicalLayout.UpdatePackagePositions(_graphData);
                        }
                        else
                        {
                            // Expanding: Reinitialize file positions within the package
                            ReinitializePackageFilePositions(package);
                            _forceLayout.RunIterations(_graphData, 50);

                            // Recenter package on its files and recalculate bounds
                            RecenterPackageOnFiles(package);

                            // Resolve any package overlaps
                            ResolvePackageOverlaps(_graphData);
                        }
                    }
                    else
                    {
                        // Collapsing: Just update bounds
                        package.Velocity = Vector2.zero;
                        package.Force = Vector2.zero;
                        package.Bounds = package.CalculateBounds();

                        // Reset forces on all packages and nodes
                        foreach (PackageNode pkg in _graphData.Packages)
                        {
                            pkg.Velocity = Vector2.zero;
                            pkg.Force = Vector2.zero;
                        }

                        foreach (DependencyGraphNode node in _graphData.Nodes)
                        {
                            node.Velocity = Vector2.zero;
                            node.Force = Vector2.zero;
                        }
                    }
                }

                Repaint();
            }
        }

        private void RecenterPackageOnFiles(PackageNode package)
        {
            if (!package.IsExpanded || package.Files.Count == 0) return;

            Vector2 centroid = Vector2.zero;
            int visibleCount = 0;

            foreach (DependencyGraphNode file in package.Files)
            {
                if (file.IsVisible)
                {
                    centroid += file.Position;
                    visibleCount++;
                }
            }

            if (visibleCount > 0)
            {
                package.Position = centroid / visibleCount;
            }

            package.Bounds = package.CalculateBounds();
        }

        private void ReinitializePackageFilePositions(PackageNode package)
        {
            // Arrange ALL files (including root if present) in a circle around package center
            float radius = 80f + package.Files.Count * 10f;
            float angleStep = 360f / Mathf.Max(1, package.Files.Count);

            for (int i = 0; i < package.Files.Count; i++)
            {
                DependencyGraphNode file = package.Files[i];
                float angle = i * angleStep * Mathf.Deg2Rad;

                // Position file in circle (overrides initial position like Vector2.zero for root)
                file.Position = package.Position + new Vector2(
                    Mathf.Cos(angle) * radius,
                    Mathf.Sin(angle) * radius
                );
                file.Velocity = Vector2.zero;
                file.Force = Vector2.zero;
            }
        }

        private void UpdatePackageBoundsAfterLayout(DependencyGraphData graphData)
        {
            // Recenter all packages on their files and recalculate bounds
            foreach (PackageNode package in graphData.Packages)
            {
                if (!package.IsVisible) continue;
                RecenterPackageOnFiles(package);
            }

            // Resolve any package boundary overlaps
            ResolvePackageOverlaps(graphData);
        }

        private void ResolvePackageOverlaps(DependencyGraphData graphData)
        {
            // Final hard constraint pass - ensures absolutely no overlaps
            // This runs AFTER physics simulation as a cleanup
            const int maxIterations = 10; // Should be quick since physics already separated them
            const float minSeparation = 50f;

            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                bool hadOverlap = false;

                for (int i = 0; i < graphData.Packages.Count; i++)
                {
                    PackageNode pkg1 = graphData.Packages[i];
                    if (!pkg1.IsVisible || !pkg1.IsExpanded) continue;

                    // Recalculate bounds
                    pkg1.Bounds = pkg1.CalculateBounds();

                    for (int j = i + 1; j < graphData.Packages.Count; j++)
                    {
                        PackageNode pkg2 = graphData.Packages[j];
                        if (!pkg2.IsVisible || !pkg2.IsExpanded) continue;

                        // Recalculate bounds
                        pkg2.Bounds = pkg2.CalculateBounds();

                        // Check if package bounds overlap
                        if (pkg1.Bounds.Overlaps(pkg2.Bounds))
                        {
                            hadOverlap = true;

                            // Calculate separation vector
                            Vector2 delta = pkg1.Position - pkg2.Position;
                            float distance = delta.magnitude;

                            if (distance < 0.1f)
                            {
                                delta = new Vector2(UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f)).normalized;
                                distance = 0.1f;
                            }

                            Vector2 direction = delta.normalized;

                            // Calculate minimum required distance between centers
                            float halfWidth1 = pkg1.Bounds.width / 2f;
                            float halfWidth2 = pkg2.Bounds.width / 2f;
                            float halfHeight1 = pkg1.Bounds.height / 2f;
                            float halfHeight2 = pkg2.Bounds.height / 2f;

                            float requiredDistance = Mathf.Max(halfWidth1 + halfWidth2, halfHeight1 + halfHeight2) + minSeparation;
                            float overlap = requiredDistance - distance;

                            if (overlap > 0)
                            {
                                // Hard constraint: directly move packages and files
                                Vector2 correction = direction * (overlap / 2f);

                                pkg1.Position += correction;
                                MovePackageFiles(pkg1, correction);

                                pkg2.Position -= correction;
                                MovePackageFiles(pkg2, -correction);
                            }
                        }
                    }
                }

                // If no overlaps found, we're done
                if (!hadOverlap) break;
            }
        }

        private void MovePackageFiles(PackageNode package, Vector2 offset)
        {
            // Move all files in the package by the offset
            foreach (DependencyGraphNode file in package.Files)
            {
                if (file.IsVisible)
                {
                    file.Position += offset;
                }
            }
        }

        private void OnGraphPackageDoubleClicked(PackageNode package)
        {
            // Expand and focus on double-click
            if (package != null && !package.IsExpanded)
            {
                package.IsExpanded = true;

                // Make files visible
                foreach (DependencyGraphNode file in package.Files)
                {
                    file.IsVisible = true;
                }

                if (_graphData != null)
                {
                    if (_useHierarchicalLayout)
                    {
                        _hierarchicalLayout.InitializeHierarchicalPositions(_graphData);
                        _hierarchicalLayout.UpdatePackagePositions(_graphData);
                    }
                    else
                    {
                        ReinitializePackageFilePositions(package);
                        _forceLayout.RunIterations(_graphData, 50);
                        RecenterPackageOnFiles(package);
                        ResolvePackageOverlaps(_graphData);
                    }
                }

                _graphRenderer.FocusOnNode(package.Files.FirstOrDefault());
                Repaint();
            }
        }

        private void OnGraphPackageRightClicked(PackageNode package)
        {
            if (package == null) return;

            GenericMenu menu = new GenericMenu();

            menu.AddItem(new GUIContent(package.IsExpanded ? "Collapse Package" : "Expand Package"), false, () =>
            {
                package.ToggleExpanded();

                if (_graphData != null)
                {
                    if (_useHierarchicalLayout)
                    {
                        _hierarchicalLayout.InitializeHierarchicalPositions(_graphData);
                        _hierarchicalLayout.UpdatePackagePositions(_graphData);
                    }
                    else
                    {
                        ReinitializePackageFilePositions(package);
                        _forceLayout.RunIterations(_graphData, 50);
                        RecenterPackageOnFiles(package);
                        ResolvePackageOverlaps(_graphData);
                    }
                }

                Repaint();
            });

            menu.AddItem(new GUIContent("Expand All Files"), false, () =>
            {
                package.IsExpanded = true;
                foreach (DependencyGraphNode file in package.Files)
                {
                    file.IsVisible = true;
                    file.IsExpanded = true;
                }

                if (_graphData != null)
                {
                    if (_useHierarchicalLayout)
                    {
                        _hierarchicalLayout.InitializeHierarchicalPositions(_graphData);
                        _hierarchicalLayout.UpdatePackagePositions(_graphData);
                    }
                    else
                    {
                        ReinitializePackageFilePositions(package);
                        _forceLayout.RunIterations(_graphData, 50);
                        RecenterPackageOnFiles(package);
                        ResolvePackageOverlaps(_graphData);
                    }
                }

                Repaint();
            });

            if (package.AssetInfo != null && !string.IsNullOrEmpty(package.AssetInfo.Location))
            {
                menu.AddItem(new GUIContent("Open Package Location"), false, () =>
                {
                    EditorUtility.RevealInFinder(package.AssetInfo.Location);
                });
            }

            menu.ShowAsContext();
        }

        private void RenderGraphView()
        {
            InitializeGraph();

            // Layout controls
            GUILayout.BeginHorizontal();

            // Layout mode toggle
            EditorGUI.BeginChangeCheck();
            _useHierarchicalLayout = GUILayout.Toggle(_useHierarchicalLayout, "Hierarchical Layout", EditorStyles.miniButtonLeft, GUILayout.Width(130));
            if (EditorGUI.EndChangeCheck())
            {
                // Rebuild graph with new layout
                _graphNeedsRebuild = true;
            }

            // Show All / Simplified toggle
            EditorGUI.BeginChangeCheck();
            _showAllDependencies = GUILayout.Toggle(_showAllDependencies, "Show All", EditorStyles.miniButtonMid, GUILayout.Width(80));
            if (EditorGUI.EndChangeCheck())
            {
                _graphData.SetSimplifiedMode(!_showAllDependencies);

                if (_useHierarchicalLayout)
                {
                    _hierarchicalLayout.InitializeHierarchicalPositions(_graphData);
                    _hierarchicalLayout.UpdatePackagePositions(_graphData);
                }
                else
                {
                    _forceLayout.RunIterations(_graphData, 30);
                }
            }

            bool frameAllClicked = GUILayout.Button("Frame All (F)", EditorStyles.miniButtonRight, GUILayout.Width(100));

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            EditorGUILayout.Space();

            // Graph rendering area
            Rect graphRect = GUILayoutUtility.GetRect(100, position.height - 200, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            // Frame all on initial display or when button clicked
            // Use window dimensions for framing to ensure correct viewport size
            if (_needsInitialFrame || frameAllClicked)
            {
                Rect frameRect = new Rect(0, 0, position.width, position.height - 200);
                _graphRenderer.FrameAll(frameRect, _graphData);
                _needsInitialFrame = false;
            }

            _graphRenderer.Render(graphRect, _graphData, _forceLayout);

            // Force repaint if layout is still updating
            if (!_forceLayout.IsStable)
            {
                Repaint();
            }
        }
    }
}
