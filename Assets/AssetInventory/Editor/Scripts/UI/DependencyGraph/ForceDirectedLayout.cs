using System.Collections.Generic;
using UnityEngine;

namespace AssetInventory
{
    public class ForceDirectedLayout
    {
        // Force parameters
        public float RepulsionStrength { get; set; } = 15000f;
        public float AttractionStrength { get; set; } = 0.05f;
        public float CenterGravity { get; set; } = 0.02f;
        public float Damping { get; set; } = 0.85f;
        public float MinDistance { get; set; } = 150f;
        public float IdealEdgeLength { get; set; } = 180f;
        public float TimeStep { get; set; } = 0.5f;

        // Spatial hashing for optimization
        private Dictionary<Vector2Int, List<DependencyGraphNode>> _spatialGrid;
        private float _cellSize = 200f;
        private bool _useSpatialHashing = true;

        // Convergence tracking
        private float _totalEnergy;
        private float _energyThreshold = 0.1f;
        public bool IsStable => _totalEnergy < _energyThreshold;

        public ForceDirectedLayout()
        {
            _spatialGrid = new Dictionary<Vector2Int, List<DependencyGraphNode>>();
        }

        /// <summary>
        /// Initialize node and package positions (combined - for backward compatibility)
        /// </summary>
        public void InitializePositions(DependencyGraphData graphData)
        {
            InitializePackagePositions(graphData);
            InitializeFilePositions(graphData);
        }

        /// <summary>
        /// Initialize package positions only
        /// </summary>
        public void InitializePackagePositions(DependencyGraphData graphData)
        {
            if (graphData.Packages.Count == 0) return;

            float packageRadius = Mathf.Max(600f, graphData.Packages.Count * 150f);
            float angleStep = 360f / Mathf.Max(1, graphData.Packages.Count);

            for (int i = 0; i < graphData.Packages.Count; i++)
            {
                PackageNode package = graphData.Packages[i];
                float angle = i * angleStep * Mathf.Deg2Rad;
                package.Position = new Vector2(
                    Mathf.Cos(angle) * packageRadius,
                    Mathf.Sin(angle) * packageRadius
                );

                // Add small random offset to break symmetry
                package.Position += new Vector2(
                    Random.Range(-50f, 50f),
                    Random.Range(-50f, 50f)
                );

                // Reset velocity and force
                package.Velocity = Vector2.zero;
                package.Force = Vector2.zero;
            }
        }

        /// <summary>
        /// Initialize file positions within their packages
        /// </summary>
        public void InitializeFilePositions(DependencyGraphData graphData)
        {
            foreach (PackageNode package in graphData.Packages)
            {
                if (package.Files.Count == 0) continue;

                // Arrange ALL files (including root) in a circle around package center
                // Use same logic as reexpansion for consistent appearance
                float radius = 80f + package.Files.Count * 10f;
                float fileAngleStep = 360f / Mathf.Max(1, package.Files.Count);

                for (int i = 0; i < package.Files.Count; i++)
                {
                    DependencyGraphNode file = package.Files[i];
                    float angle = i * fileAngleStep * Mathf.Deg2Rad;

                    // Position file in circle around package center (overrides any previous position)
                    file.Position = package.Position + new Vector2(
                        Mathf.Cos(angle) * radius,
                        Mathf.Sin(angle) * radius
                    );

                    // Reset velocity and force for clean start
                    file.Velocity = Vector2.zero;
                    file.Force = Vector2.zero;
                }
            }
        }

        /// <summary>
        /// Run iterations for package layout only (no file movement)
        /// </summary>
        public void RunPackageLayoutIterations(DependencyGraphData graphData, int iterations)
        {
            for (int i = 0; i < iterations; i++)
            {
                UpdatePackageLayout(graphData, 1f);
                // Hard constraint in UpdatePackageLayout() ensures no overlaps
            }
        }

        /// <summary>
        /// Run one iteration of the two-phase force-directed layout algorithm
        /// </summary>
        public void Update(DependencyGraphData graphData, float deltaTime)
        {
            if (graphData.Packages.Count == 0 && graphData.Nodes.Count == 0) return;

            _totalEnergy = 0f;

            // Phase 1: Layout packages
            UpdatePackageLayout(graphData, deltaTime);

            // Phase 2: Layout files within packages
            UpdateFileLayout(graphData, deltaTime);
        }

        private void UpdatePackageLayout(DependencyGraphData graphData, float deltaTime)
        {
            // Reset package forces
            foreach (PackageNode package in graphData.Packages)
            {
                package.ResetForces();
            }

            // Apply ONLY repulsion between packages (no attraction)
            // Packages should naturally space apart, edges can be any length
            for (int i = 0; i < graphData.Packages.Count; i++)
            {
                PackageNode pkg1 = graphData.Packages[i];
                if (!pkg1.IsVisible) continue;

                for (int j = i + 1; j < graphData.Packages.Count; j++)
                {
                    PackageNode pkg2 = graphData.Packages[j];
                    if (!pkg2.IsVisible) continue;

                    ApplyPackageRepulsion(pkg1, pkg2);
                }
            }

            // Optional: Very weak center gravity to prevent packages drifting too far
            // But much weaker than before
            foreach (PackageNode package in graphData.Packages)
            {
                if (!package.IsVisible) continue;

                Vector2 delta = Vector2.zero - package.Position;
                float distance = delta.magnitude;

                // Only apply if very far from center
                if (distance > 2000f)
                {
                    Vector2 force = delta.normalized * CenterGravity * 0.1f * (distance - 2000f);
                    package.ApplyForce(force);
                }
            }

            // Update package positions
            foreach (PackageNode package in graphData.Packages)
            {
                if (!package.IsVisible) continue;

                package.UpdateVelocity(Damping);
                package.UpdatePosition(TimeStep * deltaTime);
                _totalEnergy += package.Velocity.sqrMagnitude;
            }

            // HARD CONSTRAINT: Resolve any package overlaps after position update
            ResolvePackageBoundaryCollisions(graphData);
        }

        private void ResolvePackageBoundaryCollisions(DependencyGraphData graphData)
        {
            // Hard constraint: directly move packages apart if their boundaries overlap
            // This is NOT force-based, it's a direct position correction
            const float minSeparation = 50f;

            // Multiple passes to handle chain collisions
            for (int pass = 0; pass < 5; pass++)
            {
                bool hadCollision = false;

                for (int i = 0; i < graphData.Packages.Count; i++)
                {
                    PackageNode pkg1 = graphData.Packages[i];
                    if (!pkg1.IsVisible || !pkg1.IsExpanded) continue;

                    // Recalculate bounds before checking
                    pkg1.Bounds = pkg1.CalculateBounds();

                    for (int j = i + 1; j < graphData.Packages.Count; j++)
                    {
                        PackageNode pkg2 = graphData.Packages[j];
                        if (!pkg2.IsVisible || !pkg2.IsExpanded) continue;

                        // Recalculate bounds before checking
                        pkg2.Bounds = pkg2.CalculateBounds();

                        // Check if boundaries overlap
                        if (pkg1.Bounds.Overlaps(pkg2.Bounds))
                        {
                            hadCollision = true;

                            // Calculate separation vector
                            Vector2 delta = pkg1.Position - pkg2.Position;
                            float distance = delta.magnitude;

                            if (distance < 0.1f)
                            {
                                // Same position - push in random directions
                                delta = new Vector2(UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f)).normalized;
                                distance = 0.1f;
                            }

                            Vector2 direction = delta.normalized;

                            // Calculate minimum required distance between centers based on bounds
                            float halfWidth1 = pkg1.Bounds.width / 2f;
                            float halfWidth2 = pkg2.Bounds.width / 2f;
                            float halfHeight1 = pkg1.Bounds.height / 2f;
                            float halfHeight2 = pkg2.Bounds.height / 2f;

                            // Calculate required separation in the direction of separation
                            float requiredDistance = Mathf.Max(halfWidth1 + halfWidth2, halfHeight1 + halfHeight2) + minSeparation;

                            // Calculate how much we need to push them apart
                            float overlap = requiredDistance - distance;

                            if (overlap > 0)
                            {
                                // Push both packages apart equally (hard constraint, not force)
                                Vector2 correction = direction * (overlap / 2f);

                                // Move package 1 and all its files
                                pkg1.Position += correction;
                                foreach (DependencyGraphNode file in pkg1.Files)
                                {
                                    if (file.IsVisible)
                                    {
                                        file.Position += correction;
                                    }
                                }

                                // Move package 2 and all its files
                                pkg2.Position -= correction;
                                foreach (DependencyGraphNode file in pkg2.Files)
                                {
                                    if (file.IsVisible)
                                    {
                                        file.Position -= correction;
                                    }
                                }

                                // Zero out velocities to prevent oscillation
                                pkg1.Velocity *= 0.8f;
                                pkg2.Velocity *= 0.8f;
                            }
                        }
                    }
                }

                // If no collisions in this pass, we're done
                if (!hadCollision) break;
            }
        }

        private void UpdateFileLayout(DependencyGraphData graphData, float deltaTime)
        {
            // Build spatial grid for file-level optimization
            if (_useSpatialHashing)
            {
                BuildSpatialGrid(graphData);
            }

            // Reset file forces
            foreach (DependencyGraphNode node in graphData.Nodes)
            {
                if (!node.IsVisible) continue; // Skip invisible files completely
                node.ResetForces();
            }

            // Apply repulsion between files (within and across packages)
            ApplyRepulsionForces(graphData);

            // Apply attraction along edges
            ApplyAttractionForces(graphData);

            // Apply package boundary constraints
            ApplyPackageBoundaryForces(graphData);

            // Update file positions
            foreach (DependencyGraphNode node in graphData.Nodes)
            {
                if (!node.IsVisible) continue; // Skip invisible files completely
                if (node.IsRoot) continue; // Skip root node

                if (node.PackageNode != null)
                {
                    // Normal physics update when file is visible
                    node.UpdateVelocity(Damping);
                    node.UpdatePosition(TimeStep * deltaTime);
                    _totalEnergy += node.Velocity.sqrMagnitude;
                }
                else
                {
                    // No package - update normally
                    node.UpdateVelocity(Damping);
                    node.UpdatePosition(TimeStep * deltaTime);
                    _totalEnergy += node.Velocity.sqrMagnitude;
                }
            }
        }

        private void ApplyPackageRepulsion(PackageNode pkg1, PackageNode pkg2)
        {
            Vector2 delta = pkg1.Position - pkg2.Position;
            float centerDistance = delta.magnitude;

            if (centerDistance < 0.1f) centerDistance = 0.1f;

            // Calculate actual boundary-to-boundary distance (gap between package edges)
            // This way collapsed packages (smaller bounds) naturally settle closer together
            float pkg1Radius = Mathf.Max(pkg1.Bounds.width, pkg1.Bounds.height) / 2f;
            float pkg2Radius = Mathf.Max(pkg2.Bounds.width, pkg2.Bounds.height) / 2f;
            float boundaryDistance = centerDistance - pkg1Radius - pkg2Radius;

            // Desired minimum gap between package boundaries
            float desiredGap = pkg1.IsExpanded && pkg2.IsExpanded ? 300f : 150f; // Smaller gap for collapsed

            // Only apply repulsion if they're too close
            if (boundaryDistance < desiredGap)
            {
                // Stronger repulsion when boundaries are very close
                float overlap = desiredGap - boundaryDistance;
                float force = RepulsionStrength * 3f * overlap / desiredGap;
                Vector2 forceVector = delta.normalized * force;

                pkg1.ApplyForce(forceVector);
                pkg2.ApplyForce(-forceVector);
            }
            else
            {
                // Very weak repulsion at proper distance to maintain spacing
                float force = (RepulsionStrength * 0.5f) / (boundaryDistance * boundaryDistance);
                Vector2 forceVector = delta.normalized * force;

                pkg1.ApplyForce(forceVector);
                pkg2.ApplyForce(-forceVector);
            }
        }

        // REMOVED: ApplyPackageAttraction
        // Packages should NOT be attracted to each other based on edges
        // They should only repel to create natural spacing
        // Cross-package dependency arrows can be any length

        private void ApplyPackageBoundaryForces(DependencyGraphData graphData)
        {
            // Keep files within their package boundaries (soft constraint)
            foreach (PackageNode package in graphData.Packages)
            {
                if (!package.IsExpanded) continue;

                foreach (DependencyGraphNode file in package.Files)
                {
                    if (!file.IsVisible) continue;

                    // Calculate distance from package center
                    Vector2 delta = file.Position - package.Position;
                    float distance = delta.magnitude;

                    // Soft constraint: pull files toward package center if too far
                    // Dynamic based on file count - more files = larger radius
                    float baseRadius = 250f;
                    float maxRadius = baseRadius + (package.Files.Count * 5f);

                    if (distance > maxRadius)
                    {
                        Vector2 force = -delta.normalized * (distance - maxRadius) * 0.3f;
                        file.ApplyForce(force);
                    }
                }
            }
        }

        /// <summary>
        /// Apply repulsion forces between all node pairs
        /// </summary>
        private void ApplyRepulsionForces(DependencyGraphData graphData)
        {
            if (_useSpatialHashing)
            {
                // Use spatial hashing for O(n) instead of O(n²)
                foreach (DependencyGraphNode node in graphData.Nodes)
                {
                    if (!node.IsVisible) continue;

                    // Check only nearby cells
                    List<DependencyGraphNode> nearbyNodes = GetNearbyNodes(node);
                    foreach (DependencyGraphNode other in nearbyNodes)
                    {
                        if (node == other || !other.IsVisible) continue;

                        ApplyRepulsionBetween(node, other);
                    }
                }
            }
            else
            {
                // Brute force O(n²) - use for small graphs
                for (int i = 0; i < graphData.Nodes.Count; i++)
                {
                    DependencyGraphNode node1 = graphData.Nodes[i];
                    if (!node1.IsVisible) continue;

                    for (int j = i + 1; j < graphData.Nodes.Count; j++)
                    {
                        DependencyGraphNode node2 = graphData.Nodes[j];
                        if (!node2.IsVisible) continue;

                        ApplyRepulsionBetween(node1, node2);
                    }
                }
            }
        }

        private void ApplyRepulsionBetween(DependencyGraphNode node1, DependencyGraphNode node2)
        {
            Vector2 delta = node1.Position - node2.Position;
            float distance = delta.magnitude;

            // Avoid division by zero
            if (distance < 0.1f) distance = 0.1f;

            // Use full bounds for collision detection (including labels)
            float effectiveMinDistance = MinDistance;
            if (node1.FullBounds.width > 0 && node2.FullBounds.width > 0)
            {
                // Calculate combined radius from full bounds
                float radius1 = Mathf.Max(node1.FullBounds.width, node1.FullBounds.height) / 2f;
                float radius2 = Mathf.Max(node2.FullBounds.width, node2.FullBounds.height) / 2f;
                effectiveMinDistance = radius1 + radius2 + 20f; // Add padding
            }

            // Stronger repulsion when too close
            float repulsionMultiplier = 1f;
            if (distance < effectiveMinDistance)
            {
                repulsionMultiplier = 3f; // Much stronger push when overlapping
            }

            // Coulomb's law: F = k * 1/d²
            float force = (RepulsionStrength * repulsionMultiplier) / (distance * distance);
            Vector2 forceVector = delta.normalized * force;

            node1.ApplyForce(forceVector);
            node2.ApplyForce(-forceVector);
        }

        /// <summary>
        /// Apply attraction forces along edges (Hooke's law)
        /// </summary>
        private void ApplyAttractionForces(DependencyGraphData graphData)
        {
            foreach (DependencyGraphEdge edge in graphData.Edges)
            {
                if (!edge.ShouldRender()) continue;

                Vector2 delta = edge.Target.Position - edge.Source.Position;
                float distance = delta.magnitude;

                // Hooke's law: F = k * (d - ideal)
                float displacement = distance - IdealEdgeLength;
                float force = AttractionStrength * displacement;
                Vector2 forceVector = delta.normalized * force;

                edge.Source.ApplyForce(forceVector);
                edge.Target.ApplyForce(-forceVector);
            }
        }

        /// <summary>
        /// Apply gravity towards center to prevent nodes from drifting away
        /// </summary>
        private void ApplyCenterGravity(DependencyGraphData graphData)
        {
            Vector2 center = Vector2.zero;
            if (graphData.RootNode != null)
            {
                center = graphData.RootNode.Position;
            }

            foreach (DependencyGraphNode node in graphData.Nodes)
            {
                if (!node.IsVisible || node.IsRoot) continue;

                Vector2 delta = center - node.Position;
                float distance = delta.magnitude;

                if (distance > 0.1f)
                {
                    Vector2 force = delta.normalized * CenterGravity * distance;
                    node.ApplyForce(force);
                }
            }
        }

        /// <summary>
        /// Build spatial grid for efficient nearest neighbor search
        /// </summary>
        private void BuildSpatialGrid(DependencyGraphData graphData)
        {
            _spatialGrid.Clear();

            foreach (DependencyGraphNode node in graphData.Nodes)
            {
                if (!node.IsVisible) continue;

                Vector2Int cell = GetCellCoordinates(node.Position);

                if (!_spatialGrid.ContainsKey(cell))
                {
                    _spatialGrid[cell] = new List<DependencyGraphNode>();
                }

                _spatialGrid[cell].Add(node);
            }
        }

        private Vector2Int GetCellCoordinates(Vector2 position)
        {
            return new Vector2Int(
                Mathf.FloorToInt(position.x / _cellSize),
                Mathf.FloorToInt(position.y / _cellSize)
            );
        }

        private List<DependencyGraphNode> GetNearbyNodes(DependencyGraphNode node)
        {
            List<DependencyGraphNode> nearby = new List<DependencyGraphNode>();
            Vector2Int center = GetCellCoordinates(node.Position);

            // Check 3x3 grid around node
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    Vector2Int cell = new Vector2Int(center.x + dx, center.y + dy);
                    if (_spatialGrid.TryGetValue(cell, out List<DependencyGraphNode> cellNodes))
                    {
                        nearby.AddRange(cellNodes);
                    }
                }
            }

            return nearby;
        }

        /// <summary>
        /// Run multiple iterations until stable or max iterations reached
        /// </summary>
        public void RunIterations(DependencyGraphData graphData, int maxIterations = 100)
        {
            for (int i = 0; i < maxIterations; i++)
            {
                Update(graphData, 1f);

                if (IsStable)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Adjust parameters based on graph size
        /// </summary>
        public void AutoAdjustParameters(int nodeCount)
        {
            // Scale forces based on number of nodes
            if (nodeCount > 100)
            {
                RepulsionStrength = 20000f;
                AttractionStrength = 0.04f;
                IdealEdgeLength = 200f;
                MinDistance = 180f;
                _useSpatialHashing = true;
            }
            else if (nodeCount > 50)
            {
                RepulsionStrength = 18000f;
                AttractionStrength = 0.045f;
                IdealEdgeLength = 190f;
                MinDistance = 170f;
                _useSpatialHashing = true;
            }
            else if (nodeCount > 20)
            {
                RepulsionStrength = 16000f;
                AttractionStrength = 0.05f;
                IdealEdgeLength = 180f;
                MinDistance = 160f;
                _useSpatialHashing = false;
            }
            else
            {
                RepulsionStrength = 15000f;
                AttractionStrength = 0.05f;
                IdealEdgeLength = 180f;
                MinDistance = 150f;
                _useSpatialHashing = false;
            }
        }
    }
}
