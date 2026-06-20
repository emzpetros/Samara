using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AssetInventory
{
    /// <summary>
    /// Hierarchical radial layout algorithm for dependency graphs.
    /// Positions the root at center with dependencies arranged in expanding rings,
    /// clustered angularly near their parents to show the hierarchical structure clearly.
    /// 
    /// Key Features:
    /// - Radial depth-based positioning (root at center, dependencies in expanding rings)
    /// - Dynamic radius calculation (expands crowded rings automatically to prevent overlap)
    /// - Angular clustering (children positioned near their parents)
    /// - Within-package edge optimization (eliminates edge crossings inside packages)
    /// - Collision avoidance (nodes pushed to empty spaces while preserving hierarchy)
    /// - Cross-package edge optimization (minimizes overlaps and edge crossings)
    /// - Package-aware spacing (ensures package boundaries don't overlap)
    /// - Auto-tuning (adapts parameters based on graph size and complexity)
    /// </summary>
    public class HierarchicalRadialLayout
    {
        // Layout parameters
        public float BaseRadiusPerLevel { get; set; } = 280f;
        public float MinNodeSeparation { get; set; } = 120f;
        public float AngularClusteringStrength { get; set; } = 0.75f;
        public float CollisionAvoidanceStrength { get; set; } = 0.4f;
        public int CollisionIterations { get; set; } = 25;

        // Cross-package edge optimization
        public float CrossPackageEdgeWeight { get; set; } = 1.5f;

        // Convergence tracking
        private float _totalDisplacement;
        public bool IsStable => _totalDisplacement < 1f;

        // Angular position tracking per depth level
        private Dictionary<int, List<AngularSlot>> _angularSlotsByDepth;

        private class AngularSlot
        {
            public float Angle;
            public float AngularWidth;
            public DependencyGraphNode Node;
            public PackageNode Package;
        }

        /// <summary>
        /// Initialize hierarchical positions for all nodes based on their depth and parent relationships
        /// </summary>
        public void InitializeHierarchicalPositions(DependencyGraphData graphData)
        {
            if (graphData.RootNode == null) return;

            // Check if there are any visible nodes besides root
            int visibleCount = graphData.Nodes.Count(n => n.IsVisible);
            if (visibleCount <= 1)
            {
                // Only root node - center it
                graphData.RootNode.Position = Vector2.zero;
                graphData.RootNode.Velocity = Vector2.zero;
                graphData.RootNode.Force = Vector2.zero;
                return;
            }

            _angularSlotsByDepth = new Dictionary<int, List<AngularSlot>>();

            // Position root at origin
            graphData.RootNode.Position = Vector2.zero;
            graphData.RootNode.Velocity = Vector2.zero;
            graphData.RootNode.Force = Vector2.zero;

            // Calculate initial hierarchical positions
            CalculateNodePositions(graphData);

            // Optimize angular positions to minimize cross-package edge overlaps
            OptimizeCrossPackageEdges(graphData);

            // Apply collision avoidance to push overlapping nodes apart
            ApplyCollisionAvoidance(graphData);
        }

        // Cache for dynamically calculated radii per depth
        private Dictionary<int, float> _radiusByDepth;

        /// <summary>
        /// Calculate positions for all nodes recursively based on parent-child relationships
        /// </summary>
        private void CalculateNodePositions(DependencyGraphData graphData)
        {
            // Collect nodes by depth
            Dictionary<int, List<DependencyGraphNode>> nodesByDepth = new Dictionary<int, List<DependencyGraphNode>>();
            int maxDepth = 0;

            foreach (DependencyGraphNode node in graphData.Nodes)
            {
                if (!node.IsVisible) continue;

                int depth = node.Depth;
                if (!nodesByDepth.ContainsKey(depth))
                {
                    nodesByDepth[depth] = new List<DependencyGraphNode>();
                    _angularSlotsByDepth[depth] = new List<AngularSlot>();
                }
                nodesByDepth[depth].Add(node);
                maxDepth = Mathf.Max(maxDepth, depth);
            }

            // Calculate optimal radius for each depth level based on node count
            _radiusByDepth = CalculateOptimalRadiiPerDepth(nodesByDepth, maxDepth);

            // Position nodes level by level, starting from root's children
            for (int depth = 1; depth <= maxDepth; depth++)
            {
                if (!nodesByDepth.ContainsKey(depth)) continue;

                List<DependencyGraphNode> nodesAtDepth = nodesByDepth[depth];
                PositionNodesAtDepth(nodesAtDepth, depth, graphData);
            }
        }

        /// <summary>
        /// Calculate optimal radius for each depth level to prevent overcrowding
        /// Dynamically expands rings with many nodes to ensure proper spacing
        /// </summary>
        private Dictionary<int, float> CalculateOptimalRadiiPerDepth(Dictionary<int, List<DependencyGraphNode>> nodesByDepth, int maxDepth)
        {
            Dictionary<int, float> radii = new Dictionary<int, float>();
            float currentRadius = 0f;

            for (int depth = 1; depth <= maxDepth; depth++)
            {
                if (!nodesByDepth.ContainsKey(depth))
                {
                    // No nodes at this depth - use standard spacing
                    currentRadius += BaseRadiusPerLevel;
                    radii[depth] = currentRadius;
                    continue;
                }

                int nodeCount = nodesByDepth[depth].Count;

                // Calculate required circumference to prevent overlap even with many nodes at this depth
                float requiredCircumference = nodeCount * MinNodeSeparation;

                // Add extra spacing factor for better readability (10% padding)
                requiredCircumference *= 1.1f;

                // Calculate minimum radius needed for this circumference
                // Circumference = 2π * radius, so radius = circumference / (2π)
                float minRequiredRadius = requiredCircumference / (2f * Mathf.PI);

                // Use the larger of: standard radius increment or required radius
                float standardRadius = currentRadius + BaseRadiusPerLevel;
                float optimalRadius = Mathf.Max(standardRadius, minRequiredRadius);

                // Also ensure minimum spacing from previous ring
                if (currentRadius > 0)
                {
                    float minSpacing = BaseRadiusPerLevel * 0.8f; // At least 80% of standard spacing
                    optimalRadius = Mathf.Max(optimalRadius, currentRadius + minSpacing);
                }

                currentRadius = optimalRadius;
                radii[depth] = currentRadius;
            }

            return radii;
        }

        /// <summary>
        /// Position all nodes at a specific depth level
        /// </summary>
        private void PositionNodesAtDepth(List<DependencyGraphNode> nodes, int depth, DependencyGraphData graphData)
        {
            // Use dynamically calculated radius for this depth to prevent overcrowding
            float radius = _radiusByDepth.ContainsKey(depth) ? _radiusByDepth[depth] : BaseRadiusPerLevel * depth;

            // Group nodes by package first for within-package edge optimization
            Dictionary<int, List<DependencyGraphNode>> nodesByPackage = new Dictionary<int, List<DependencyGraphNode>>();

            foreach (DependencyGraphNode node in nodes)
            {
                int packageId = node.AssetFile.AssetId;
                if (!nodesByPackage.ContainsKey(packageId))
                {
                    nodesByPackage[packageId] = new List<DependencyGraphNode>();
                }
                nodesByPackage[packageId].Add(node);
            }

            // Track used angular space to avoid overlaps
            List<AngularRange> usedRanges = new List<AngularRange>();

            // Process each package separately to minimize within-package edge crossings
            foreach (KeyValuePair<int, List<DependencyGraphNode>> packageGroup in nodesByPackage)
            {
                int packageId = packageGroup.Key;
                List<DependencyGraphNode> packageNodes = packageGroup.Value;

                // Group nodes by parent within this package
                Dictionary<DependencyGraphNode, List<DependencyGraphNode>> nodesByParent = new Dictionary<DependencyGraphNode, List<DependencyGraphNode>>();
                List<DependencyGraphNode> nodesWithoutParent = new List<DependencyGraphNode>();

                foreach (DependencyGraphNode node in packageNodes)
                {
                    // Find primary parent (closest depth, preferring same package)
                    DependencyGraphNode parent = node.IncomingNodes
                        .Where(n => n.IsVisible && n.Depth < depth)
                        .OrderByDescending(n => n.Depth)
                        .ThenByDescending(n => n.AssetFile.AssetId == node.AssetFile.AssetId ? 1 : 0)
                        .FirstOrDefault();

                    if (parent != null)
                    {
                        if (!nodesByParent.ContainsKey(parent))
                        {
                            nodesByParent[parent] = new List<DependencyGraphNode>();
                        }
                        nodesByParent[parent].Add(node);
                    }
                    else
                    {
                        nodesWithoutParent.Add(node);
                    }
                }

                // Calculate average parent angle for this package
                float avgParentAngle = CalculateAverageParentAngle(packageNodes);

                // Calculate total angular space needed for this package
                float totalAngularSpace = CalculateAngularSpaceNeeded(packageNodes.Count, radius);

                // Position children in a cone emanating from their parent's angular position
                // This creates natural visual flow instead of searching for "best" position
                float packageCenterAngle = avgParentAngle;

                // Check if this position conflicts with already placed packages
                // If so, find the nearest available position that maintains the cone structure
                if (RangeOverlaps(packageCenterAngle - totalAngularSpace / 2f,
                        packageCenterAngle + totalAngularSpace / 2f, usedRanges))
                {
                    // Find nearest non-overlapping position close to parent angle
                    packageCenterAngle = FindNearestAvailableAngle(avgParentAngle, totalAngularSpace, usedRanges);
                }

                // Order parents by angle relative to package center so children flow naturally from parents
                List<KeyValuePair<DependencyGraphNode, List<DependencyGraphNode>>> orderedParentGroups = nodesByParent
                    .OrderBy(kvp =>
                    {
                        float parentAngle = Mathf.Atan2(kvp.Key.Position.y, kvp.Key.Position.x);
                        // Order by angular distance from package center (maintains cone shape)
                        return parentAngle;
                    })
                    .ToList();

                // Position each parent's children consecutively to avoid edge crossings
                float currentAngle = packageCenterAngle - totalAngularSpace / 2f;
                float anglePerNode = totalAngularSpace / Mathf.Max(1, packageNodes.Count);

                List<DependencyGraphNode> orderedNodes = new List<DependencyGraphNode>();

                // Add nodes grouped by parent in order
                foreach (KeyValuePair<DependencyGraphNode, List<DependencyGraphNode>> parentGroup in orderedParentGroups)
                {
                    List<DependencyGraphNode> children = parentGroup.Value;

                    // Sort children to minimize crossings with their siblings
                    // Children with more descendants should be in the middle
                    children.Sort((a, b) =>
                    {
                        int aDescendants = CountDescendants(a);
                        int bDescendants = CountDescendants(b);
                        return bDescendants.CompareTo(aDescendants);
                    });

                    orderedNodes.AddRange(children);
                }

                // Add nodes without parents at the end
                orderedNodes.AddRange(nodesWithoutParent);

                // Position all nodes in order
                for (int i = 0; i < orderedNodes.Count; i++)
                {
                    DependencyGraphNode node = orderedNodes[i];
                    float angle = currentAngle + i * anglePerNode;

                    node.Position = new Vector2(
                        Mathf.Cos(angle) * radius,
                        Mathf.Sin(angle) * radius
                    );

                    node.Velocity = Vector2.zero;
                    node.Force = Vector2.zero;

                    // Record angular slot
                    _angularSlotsByDepth[depth].Add(new AngularSlot
                    {
                        Angle = angle,
                        AngularWidth = anglePerNode,
                        Node = node,
                        Package = node.PackageNode
                    });
                }

                // Mark angular range as used
                usedRanges.Add(new AngularRange
                {
                    StartAngle = currentAngle,
                    EndAngle = currentAngle + totalAngularSpace
                });
            }
        }

        /// <summary>
        /// Calculate average angle of all parents for a group of nodes
        /// </summary>
        private float CalculateAverageParentAngle(List<DependencyGraphNode> nodes)
        {
            Vector2 avgDirection = Vector2.zero;
            int count = 0;

            foreach (DependencyGraphNode node in nodes)
            {
                foreach (DependencyGraphNode parent in node.IncomingNodes)
                {
                    if (parent.IsVisible)
                    {
                        Vector2 dir = parent.Position.normalized;
                        if (parent.Position.magnitude > 0.1f)
                        {
                            avgDirection += dir;
                            count++;
                        }
                    }
                }
            }

            if (count == 0)
            {
                return 0f;
            }

            avgDirection /= count;
            return Mathf.Atan2(avgDirection.y, avgDirection.x);
        }

        /// <summary>
        /// Count total descendants of a node (for ordering to minimize crossings)
        /// </summary>
        private int CountDescendants(DependencyGraphNode node)
        {
            HashSet<DependencyGraphNode> visited = new HashSet<DependencyGraphNode>();
            CountDescendantsRecursive(node, visited);
            return visited.Count;
        }

        private void CountDescendantsRecursive(DependencyGraphNode node, HashSet<DependencyGraphNode> visited)
        {
            foreach (DependencyGraphNode child in node.OutgoingNodes)
            {
                if (!visited.Contains(child) && child.IsVisible)
                {
                    visited.Add(child);
                    CountDescendantsRecursive(child, visited);
                }
            }
        }

        private class AngularRange
        {
            public float StartAngle;
            public float EndAngle;

            public bool Overlaps(float start, float end)
            {
                // Normalize angles to 0-2π
                start = NormalizeAngle(start);
                end = NormalizeAngle(end);
                float rangeStart = NormalizeAngle(StartAngle);
                float rangeEnd = NormalizeAngle(EndAngle);

                // Handle wrap-around
                if (rangeStart > rangeEnd)
                {
                    return start <= rangeEnd || start >= rangeStart || end <= rangeEnd || end >= rangeStart;
                }

                return (start >= rangeStart && start <= rangeEnd) || (end >= rangeStart && end <= rangeEnd);
            }
        }

        /// <summary>
        /// Calculate angular space needed for a cluster of nodes
        /// </summary>
        private float CalculateAngularSpaceNeeded(int nodeCount, float radius)
        {
            if (nodeCount == 0) return 0f;

            // Calculate based on minimum separation distance
            float arcLength = nodeCount * MinNodeSeparation;
            float angle = arcLength / radius;

            // Add padding proportional to node count
            // More nodes = less relative padding to fit them better
            float paddingFactor = Mathf.Lerp(1.3f, 1.1f, Mathf.Min(nodeCount / 20f, 1f));

            // Ensure we don't exceed full circle
            return Mathf.Min(angle * paddingFactor, Mathf.PI * 2f * 0.95f);
        }

        /// <summary>
        /// Find the nearest available angular position to maintain cone structure
        /// Searches outward from preferred angle in both directions
        /// </summary>
        private float FindNearestAvailableAngle(float preferredAngle, float angularSpace, List<AngularRange> usedRanges)
        {
            // Try preferred angle first
            if (!RangeOverlaps(preferredAngle - angularSpace / 2f, preferredAngle + angularSpace / 2f, usedRanges))
            {
                return preferredAngle;
            }

            // Search in expanding increments around the preferred angle
            // This maintains the cone structure by staying as close as possible to parent
            float searchIncrement = angularSpace * 0.25f; // Search in quarter-width steps
            float maxSearchRange = Mathf.PI; // Don't search more than 180 degrees away

            for (float offset = searchIncrement; offset < maxSearchRange; offset += searchIncrement)
            {
                // Try positive offset
                float testAngle = preferredAngle + offset;
                if (!RangeOverlaps(testAngle - angularSpace / 2f, testAngle + angularSpace / 2f, usedRanges))
                {
                    return testAngle;
                }

                // Try negative offset
                testAngle = preferredAngle - offset;
                if (!RangeOverlaps(testAngle - angularSpace / 2f, testAngle + angularSpace / 2f, usedRanges))
                {
                    return testAngle;
                }
            }

            // Fallback: return preferred angle (will overlap, but collision avoidance will handle it)
            return preferredAngle;
        }

        /// <summary>
        /// Check if an angular range overlaps with any used ranges
        /// </summary>
        private bool RangeOverlaps(float start, float end, List<AngularRange> usedRanges)
        {
            foreach (AngularRange range in usedRanges)
            {
                if (range.Overlaps(start, end))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Optimize angular positions to minimize cross-package edge overlaps
        /// Only swaps nodes from different packages to avoid breaking within-package structure
        /// </summary>
        private void OptimizeCrossPackageEdges(DependencyGraphData graphData)
        {
            // Identify cross-package edges
            List<DependencyGraphEdge> crossPackageEdges = graphData.Edges
                .Where(e => e.IsVisible && e.IsCrossDependency)
                .ToList();

            if (crossPackageEdges.Count == 0) return;

            // Perform limited optimization passes
            for (int iter = 0; iter < 3; iter++)
            {
                bool improved = false;

                // For each depth level, try swapping nodes from different packages to reduce edge crossings
                foreach (int depth in _angularSlotsByDepth.Keys.OrderBy(d => d))
                {
                    List<AngularSlot> slots = _angularSlotsByDepth[depth];
                    if (slots.Count < 2) continue;

                    // Try swapping adjacent nodes ONLY if they're from different packages
                    for (int i = 0; i < slots.Count - 1; i++)
                    {
                        // Skip if same package - we want to preserve within-package ordering
                        if (slots[i].Node.AssetFile.AssetId == slots[i + 1].Node.AssetFile.AssetId)
                            continue;

                        float scoreBefore = CalculateCrossPackageEdgeScore(slots[i].Node, slots[i].Angle, graphData) +
                            CalculateCrossPackageEdgeScore(slots[i + 1].Node, slots[i + 1].Angle, graphData);

                        float scoreAfter = CalculateCrossPackageEdgeScore(slots[i].Node, slots[i + 1].Angle, graphData) +
                            CalculateCrossPackageEdgeScore(slots[i + 1].Node, slots[i].Angle, graphData);

                        if (scoreAfter > scoreBefore + 0.1f) // Small threshold to avoid unnecessary swaps
                        {
                            // Swap positions
                            Vector2 tempPos = slots[i].Node.Position;
                            slots[i].Node.Position = slots[i + 1].Node.Position;
                            slots[i + 1].Node.Position = tempPos;

                            float tempAngle = slots[i].Angle;
                            slots[i].Angle = slots[i + 1].Angle;
                            slots[i + 1].Angle = tempAngle;

                            improved = true;
                        }
                    }
                }

                if (!improved) break;
            }
        }

        /// <summary>
        /// Calculate cross-package edge quality score for a node at a given angle
        /// Only considers edges to/from different packages
        /// Higher score = better placement (shorter cross-package edges)
        /// </summary>
        private float CalculateCrossPackageEdgeScore(DependencyGraphNode node, float angle, DependencyGraphData graphData)
        {
            float score = 0f;

            // Check incoming edges (from parents) - only cross-package
            foreach (DependencyGraphNode parent in node.IncomingNodes)
            {
                if (!parent.IsVisible) continue;

                // Only consider cross-package edges
                if (node.AssetFile.AssetId == parent.AssetFile.AssetId) continue;

                float parentAngle = Mathf.Atan2(parent.Position.y, parent.Position.x);
                float angleDiff = Mathf.Abs(NormalizeAngleDifference(angle - parentAngle));

                // Prefer smaller angle differences for cross-package edges
                score += 1f - (angleDiff / Mathf.PI);
            }

            // Check outgoing edges (to children) - only cross-package
            foreach (DependencyGraphNode child in node.OutgoingNodes)
            {
                if (!child.IsVisible) continue;

                // Only consider cross-package edges
                if (node.AssetFile.AssetId == child.AssetFile.AssetId) continue;

                float childAngle = Mathf.Atan2(child.Position.y, child.Position.x);
                float angleDiff = Mathf.Abs(NormalizeAngleDifference(angle - childAngle));

                // Prefer smaller angle differences for cross-package edges
                score += 1f - (angleDiff / Mathf.PI);
            }

            return score;
        }

        /// <summary>
        /// Apply minimal collision avoidance to push overlapping nodes apart while preserving hierarchy
        /// Since we've already ordered nodes carefully, this should rarely trigger
        /// </summary>
        private void ApplyCollisionAvoidance(DependencyGraphData graphData)
        {
            // Reduce iterations since we've pre-ordered nodes to minimize overlaps
            int reducedIterations = Mathf.Max(5, CollisionIterations / 3);

            for (int iteration = 0; iteration < reducedIterations; iteration++)
            {
                _totalDisplacement = 0f;

                // Reset forces
                foreach (DependencyGraphNode node in graphData.Nodes)
                {
                    if (!node.IsVisible || node.IsRoot) continue;
                    node.ResetForces();
                }

                // Apply repulsion forces only between nodes at same depth (more likely to overlap)
                // This is much faster and sufficient since cross-depth nodes are naturally separated
                Dictionary<int, List<DependencyGraphNode>> nodesByDepth = new Dictionary<int, List<DependencyGraphNode>>();

                foreach (DependencyGraphNode node in graphData.Nodes)
                {
                    if (!node.IsVisible || node.IsRoot) continue;

                    if (!nodesByDepth.ContainsKey(node.Depth))
                    {
                        nodesByDepth[node.Depth] = new List<DependencyGraphNode>();
                    }
                    nodesByDepth[node.Depth].Add(node);
                }

                // Process each depth level separately
                foreach (KeyValuePair<int, List<DependencyGraphNode>> depthGroup in nodesByDepth)
                {
                    List<DependencyGraphNode> nodesAtDepth = depthGroup.Value;

                    for (int i = 0; i < nodesAtDepth.Count; i++)
                    {
                        for (int j = i + 1; j < nodesAtDepth.Count; j++)
                        {
                            DependencyGraphNode node1 = nodesAtDepth[i];
                            DependencyGraphNode node2 = nodesAtDepth[j];

                            Vector2 delta = node1.Position - node2.Position;
                            float distance = delta.magnitude;

                            if (distance < 0.1f) distance = 0.1f;

                            // Calculate minimum safe distance
                            float minDistance = MinNodeSeparation * 0.8f; // Slightly tighter since ordering is good

                            if (distance < minDistance)
                            {
                                // Nodes are too close - apply gentle repulsion
                                float overlap = minDistance - distance;
                                Vector2 direction = delta.normalized;

                                // Reduced force since structure is already good
                                float forceMagnitude = overlap * CollisionAvoidanceStrength * 0.5f;

                                node1.ApplyForce(direction * forceMagnitude);
                                node2.ApplyForce(-direction * forceMagnitude);
                            }
                        }
                    }
                }

                // Update positions while preserving radial distance (constrain to depth ring)
                foreach (KeyValuePair<int, List<DependencyGraphNode>> depthGroup in nodesByDepth)
                {
                    foreach (DependencyGraphNode node in depthGroup.Value)
                    {
                        if (node.Force.magnitude < 0.01f) continue;

                        // Calculate target radius for this node's depth (use dynamic radius)
                        float targetRadius = _radiusByDepth.ContainsKey(node.Depth) ?
                            _radiusByDepth[node.Depth] : BaseRadiusPerLevel * node.Depth;

                        // Apply force tangentially (perpendicular to radial direction)
                        Vector2 radialDir = node.Position.normalized;
                        Vector2 tangentialDir = new Vector2(-radialDir.y, radialDir.x);

                        // Project force onto tangential direction to preserve radius
                        float tangentialForce = Vector2.Dot(node.Force, tangentialDir);
                        Vector2 displacement = tangentialDir * tangentialForce * 0.3f; // Reduced displacement

                        node.Position += displacement;
                        _totalDisplacement += displacement.magnitude;

                        // Snap back to correct radius
                        float currentRadius = node.Position.magnitude;
                        if (currentRadius > 0.1f)
                        {
                            node.Position = node.Position.normalized * targetRadius;
                        }
                    }
                }

                // Early exit if stable
                if (IsStable) break;
            }
        }

        /// <summary>
        /// Update package positions and bounds after node layout
        /// </summary>
        public void UpdatePackagePositions(DependencyGraphData graphData)
        {
            foreach (PackageNode package in graphData.Packages)
            {
                if (!package.IsVisible) continue;

                // Calculate centroid of all files in package
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

                // Update bounds
                package.Bounds = package.CalculateBounds();
            }

            // Ensure package boundaries don't overlap
            ResolvePackageBoundaryOverlaps(graphData);
        }

        /// <summary>
        /// Resolve overlapping package boundaries by adjusting node positions
        /// Preserves the radial hierarchical structure as much as possible
        /// Uses aggressive separation to ensure no overlaps
        /// </summary>
        private void ResolvePackageBoundaryOverlaps(DependencyGraphData graphData)
        {
            const int maxIterations = 30; // More iterations for thorough separation
            const float minSeparation = 60f; // Generous minimum gap between packages

            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                bool hadOverlap = false;
                float maxOverlapFound = 0f;

                // Recalculate ALL package positions and bounds at start of each iteration
                foreach (PackageNode pkg in graphData.Packages)
                {
                    if (pkg.IsVisible && pkg.IsExpanded)
                    {
                        // Recalculate position as centroid
                        Vector2 centroid = Vector2.zero;
                        int visibleCount = 0;

                        foreach (DependencyGraphNode file in pkg.Files)
                        {
                            if (file.IsVisible)
                            {
                                centroid += file.Position;
                                visibleCount++;
                            }
                        }

                        if (visibleCount > 0)
                        {
                            pkg.Position = centroid / visibleCount;
                        }

                        // Recalculate bounds
                        pkg.Bounds = pkg.CalculateBounds();
                    }
                }

                for (int i = 0; i < graphData.Packages.Count; i++)
                {
                    PackageNode pkg1 = graphData.Packages[i];
                    if (!pkg1.IsVisible || !pkg1.IsExpanded) continue;

                    for (int j = i + 1; j < graphData.Packages.Count; j++)
                    {
                        PackageNode pkg2 = graphData.Packages[j];
                        if (!pkg2.IsVisible || !pkg2.IsExpanded) continue;

                        // Use bounds centers for more accurate overlap detection
                        Rect bounds1 = pkg1.Bounds;
                        Rect bounds2 = pkg2.Bounds;

                        // Check for actual rectangular overlap with padding
                        float paddedMinSep = minSeparation / 2f; // Padding around each package
                        Rect paddedBounds1 = new Rect(
                            bounds1.x - paddedMinSep,
                            bounds1.y - paddedMinSep,
                            bounds1.width + paddedMinSep * 2,
                            bounds1.height + paddedMinSep * 2
                        );

                        if (paddedBounds1.Overlaps(bounds2))
                        {
                            hadOverlap = true;

                            // Calculate separation vector between bounds centers
                            Vector2 center1 = bounds1.center;
                            Vector2 center2 = bounds2.center;
                            Vector2 delta = center1 - center2;
                            float centerDistance = delta.magnitude;

                            if (centerDistance < 0.1f)
                            {
                                // Same position - push in opposite tangential directions
                                float angle1 = Mathf.Atan2(center1.y, center1.x);
                                float angle2 = angle1 + Mathf.PI / 3f; // 60 degrees apart
                                delta = new Vector2(Mathf.Cos(angle2), Mathf.Sin(angle2)) * 100f;
                                centerDistance = 100f;
                            }

                            Vector2 direction = delta.normalized;

                            // Calculate actual overlap using rectangle geometry
                            float xOverlap = (bounds1.width + bounds2.width) / 2f + minSeparation - Mathf.Abs(center1.x - center2.x);
                            float yOverlap = (bounds1.height + bounds2.height) / 2f + minSeparation - Mathf.Abs(center1.y - center2.y);

                            // Both must be positive for overlap
                            if (xOverlap > 0 && yOverlap > 0)
                            {
                                // Use the direction of least resistance (smaller overlap)
                                float overlapAmount;
                                if (xOverlap < yOverlap)
                                {
                                    // Separate horizontally
                                    overlapAmount = xOverlap;
                                    direction = new Vector2(Mathf.Sign(delta.x), 0).normalized;
                                }
                                else
                                {
                                    // Separate vertically
                                    overlapAmount = yOverlap;
                                    direction = new Vector2(0, Mathf.Sign(delta.y)).normalized;
                                }

                                maxOverlapFound = Mathf.Max(maxOverlapFound, overlapAmount);

                                // Apply separation - each package moves full amount in its direction
                                Vector2 correction = direction * overlapAmount * 0.6f;

                                // Move packages with tangential preference
                                Vector2 pkg1Movement = GetTangentialMovement(pkg1, correction);
                                Vector2 pkg2Movement = GetTangentialMovement(pkg2, -correction);

                                // Move package 1 and its files (and all descendants)
                                MovePackageWithFiles(pkg1, pkg1Movement);

                                // Move package 2 and its files (and all descendants)
                                MovePackageWithFiles(pkg2, pkg2Movement);

                                // Note: Descendants may be in other packages
                                // Their packages will be updated in the next iteration
                            }
                        }
                    }
                }

                // After each iteration, ensure all package bounds reflect any descendant movements
                // This is critical because moving nodes cascades to children in other packages

                // Continue iterating until no overlaps found or overlap is negligible
                if (!hadOverlap || maxOverlapFound < 1f)
                {
                    break;
                }
            }

            // Final complete recalculation of all package positions and bounds
            foreach (PackageNode pkg in graphData.Packages)
            {
                if (pkg.IsVisible && pkg.IsExpanded)
                {
                    // Recalculate position as centroid
                    Vector2 centroid = Vector2.zero;
                    int visibleCount = 0;

                    foreach (DependencyGraphNode file in pkg.Files)
                    {
                        if (file.IsVisible)
                        {
                            centroid += file.Position;
                            visibleCount++;
                        }
                    }

                    if (visibleCount > 0)
                    {
                        pkg.Position = centroid / visibleCount;
                    }

                    // Final bounds calculation
                    pkg.Bounds = pkg.CalculateBounds();
                }
            }
        }

        /// <summary>
        /// Convert a movement vector into tangential movement to preserve radial structure
        /// </summary>
        private Vector2 GetTangentialMovement(PackageNode package, Vector2 desiredMovement)
        {
            // Calculate package's radial direction
            Vector2 packageRadial = package.Position.normalized;
            if (packageRadial.magnitude < 0.01f) return desiredMovement; // Package at origin

            // Calculate tangential direction (perpendicular to radial)
            Vector2 tangential = new Vector2(-packageRadial.y, packageRadial.x);

            // Project desired movement onto tangential direction
            float tangentialAmount = Vector2.Dot(desiredMovement, tangential);

            // Also allow small radial movement to help separation
            float radialAmount = Vector2.Dot(desiredMovement, packageRadial) * 0.3f; // 30% radial movement allowed

            return tangential * tangentialAmount + packageRadial * radialAmount;
        }

        /// <summary>
        /// Move a package and all its files by an offset
        /// Also cascades movement to all descendants to maintain cone structure
        /// </summary>
        private void MovePackageWithFiles(PackageNode package, Vector2 offset)
        {
            // Move all files in this package and cascade to descendants
            foreach (DependencyGraphNode file in package.Files)
            {
                if (file.IsVisible)
                {
                    MoveNodeAndDescendants(file, offset);
                }
            }

            // Recalculate package position as centroid of files
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

            // Recalculate bounds after movement based on new file positions
            package.Bounds = package.CalculateBounds();
        }

        /// <summary>
        /// Move a node and all its descendants to maintain cone structure
        /// </summary>
        private void MoveNodeAndDescendants(DependencyGraphNode node, Vector2 offset)
        {
            // Move this node
            node.Position += offset;

            // Recursively move all children (outgoing nodes)
            foreach (DependencyGraphNode child in node.OutgoingNodes)
            {
                if (child.IsVisible)
                {
                    MoveNodeAndDescendants(child, offset);
                }
            }
        }

        /// <summary>
        /// Auto-adjust layout parameters based on graph characteristics
        /// </summary>
        public void AutoAdjustParameters(DependencyGraphData graphData)
        {
            int nodeCount = graphData.Nodes.Count(n => n.IsVisible);
            int packageCount = graphData.Packages.Count(p => p.IsVisible);

            // Calculate max depth in graph
            int maxDepth = graphData.Nodes.Where(n => n.IsVisible).Max(n => n.Depth);
            if (maxDepth == 0) maxDepth = 1;

            // Adjust radius based on node count and depth
            if (nodeCount > 100)
            {
                BaseRadiusPerLevel = 320f;
                MinNodeSeparation = 140f;
                AngularClusteringStrength = 0.7f;
                CollisionIterations = 30;
            }
            else if (nodeCount > 50)
            {
                BaseRadiusPerLevel = 300f;
                MinNodeSeparation = 130f;
                AngularClusteringStrength = 0.72f;
                CollisionIterations = 25;
            }
            else if (nodeCount > 20)
            {
                BaseRadiusPerLevel = 280f;
                MinNodeSeparation = 120f;
                AngularClusteringStrength = 0.75f;
                CollisionIterations = 25;
            }
            else
            {
                BaseRadiusPerLevel = 250f;
                MinNodeSeparation = 110f;
                AngularClusteringStrength = 0.78f;
                CollisionIterations = 20;
            }

            // Adjust for deep graphs - need more radial spacing
            if (maxDepth > 5)
            {
                BaseRadiusPerLevel *= 1.2f;
            }

            // Adjust for many packages - need more angular spacing
            if (packageCount > 5)
            {
                AngularClusteringStrength *= 0.9f; // Reduce clustering to spread out more
                MinNodeSeparation *= 1.1f;
            }

            // Adjust collision avoidance strength inversely to node count
            // More nodes = weaker collision avoidance to keep layout compact
            CollisionAvoidanceStrength = Mathf.Lerp(0.5f, 0.3f, nodeCount / 100f);
        }

        /// <summary>
        /// Normalize angle to 0-2π range
        /// </summary>
        private static float NormalizeAngle(float angle)
        {
            while (angle < 0) angle += Mathf.PI * 2f;
            while (angle >= Mathf.PI * 2f) angle -= Mathf.PI * 2f;
            return angle;
        }

        /// <summary>
        /// Calculate shortest angular difference between two angles
        /// </summary>
        private static float NormalizeAngleDifference(float diff)
        {
            while (diff < -Mathf.PI) diff += Mathf.PI * 2f;
            while (diff > Mathf.PI) diff -= Mathf.PI * 2f;
            return diff;
        }
    }
}
