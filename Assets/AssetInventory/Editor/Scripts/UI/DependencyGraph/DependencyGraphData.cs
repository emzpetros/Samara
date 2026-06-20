using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    public class DependencyGraphData
    {
        public DependencyGraphNode RootNode { get; private set; }
        public List<DependencyGraphNode> Nodes { get; private set; }
        public List<DependencyGraphEdge> Edges { get; private set; }
        public List<PackageNode> Packages { get; private set; }

        private Dictionary<string, DependencyGraphNode> _nodesByGuid;
        private Dictionary<int, PackageNode> _packagesByAssetId;
        private AssetInfo _assetInfo;

        public DependencyGraphData()
        {
            Nodes = new List<DependencyGraphNode>();
            Edges = new List<DependencyGraphEdge>();
            Packages = new List<PackageNode>();
            _nodesByGuid = new Dictionary<string, DependencyGraphNode>();
            _packagesByAssetId = new Dictionary<int, PackageNode>();
        }

        public void BuildFromAssetInfo(AssetInfo assetInfo)
        {
            _assetInfo = assetInfo;
            Clear();

            if (assetInfo == null || assetInfo.Dependencies == null) return;

            // Create root node
            AssetFile rootFile = new AssetFile
            {
                FileName = assetInfo.FileName,
                Path = assetInfo.FileName,
                Guid = assetInfo.Guid ?? System.Guid.NewGuid().ToString(),
                Type = "asset",
                Size = assetInfo.Size,
                AssetId = assetInfo.AssetId
            };

            RootNode = CreateNode(rootFile, 0);
            RootNode.IsRoot = true;
            RootNode.Color = new Color(0.3f, 0.6f, 1f);

            PackageNode rootPackage = GetOrCreatePackage(assetInfo.AssetId, assetInfo);
            rootPackage.AddFile(RootNode);
            RootNode.PackageNode = rootPackage;

            // Create nodes for all dependencies
            foreach (AssetFile depFile in assetInfo.Dependencies)
            {
                // Generate synthetic GUID for files without GUIDs (e.g., embedded textures from archives)
                string nodeGuid = depFile.Guid;
                if (string.IsNullOrEmpty(nodeGuid))
                {
                    // Use composite key: AssetId_Path as synthetic GUID
                    nodeGuid = $"{depFile.AssetId}_{depFile.Path}";
                }
                
                if (_nodesByGuid.ContainsKey(nodeGuid)) continue;

                DependencyGraphNode node = CreateNode(depFile, 1, nodeGuid);

                if (depFile.InProject)
                    node.Color = new Color(0.3f, 0.8f, 0.3f);
                else
                    node.Color = new Color(1f, 0.7f, 0.2f);

                if (assetInfo.SRPSupportPackage != null &&
                    assetInfo.SRPSupportPackage.Id == depFile.AssetId)
                    node.Color = new Color(0.5f, 0.5f, 1f);

                node.Icon = GetIconForFileType(depFile.Type);

                PackageNode package = GetOrCreatePackage(depFile.AssetId, null);
                package.AddFile(node);
                node.PackageNode = package;
            }

            // Build edges using ParentGuids relationships
            foreach (DependencyGraphNode node in Nodes)
            {
                if (node == RootNode) continue;

                bool hasParent = false;

                if (node.AssetFile.ParentGuids != null && node.AssetFile.ParentGuids.Count > 0)
                {
                    // Create edge from each parent to this node
                    foreach (string parentGuid in node.AssetFile.ParentGuids)
                    {
                        if (_nodesByGuid.TryGetValue(parentGuid, out DependencyGraphNode parentNode))
                        {
                            CreateEdge(parentNode, node);
                            hasParent = true;
                        }
                    }
                }

                // Connect orphan nodes directly to root to keep all nodes in hierarchy
                if (!hasParent && RootNode != null)
                {
                    CreateEdge(RootNode, node);
                }
            }

            // Calculate proper hierarchical depths after edges are built
            CalculateHierarchicalDepths();

            DetectCrossDependencies();
            DetectCycles();
            AdjustColorsBasedOnDependencies();
        }

        private PackageNode GetOrCreatePackage(int assetId, AssetInfo info)
        {
            if (_packagesByAssetId.TryGetValue(assetId, out PackageNode package))
            {
                return package;
            }

            package = new PackageNode(assetId);

            // Set package name from info if available
            if (info != null)
            {
                package.Name = info.GetDisplayName();
                package.AssetInfo = info;
            }
            else if (_assetInfo != null && _assetInfo.CrossPackageDependencies != null)
            {
                // Try to find in cross package dependencies
                Asset crossPackage = _assetInfo.CrossPackageDependencies.FirstOrDefault(a => a.Id == assetId);
                if (crossPackage != null)
                {
                    package.Name = !string.IsNullOrWhiteSpace(crossPackage.DisplayName) ? crossPackage.DisplayName : crossPackage.SafeName;
                }
            }

            // Generate color based on asset ID
            package.Color = GeneratePackageColor(assetId);

            _packagesByAssetId[assetId] = package;
            Packages.Add(package);

            return package;
        }

        private Color GeneratePackageColor(int assetId)
        {
            // Generate consistent color from asset ID using hash
            float hue = ((assetId * 2654435761u) % 360) / 360f;
            Color color = Color.HSVToRGB(hue, 0.6f, 0.9f);
            color.a = 0.3f; // Semi-transparent
            return color;
        }

        private DependencyGraphNode CreateNode(AssetFile assetFile, int depth, string overrideGuid = null)
        {
            // Use override GUID if provided (for files without GUIDs), otherwise use assetFile.Guid
            string guid = overrideGuid ?? assetFile.Guid;
            
            // Generate synthetic GUID for files without GUIDs if still null
            if (string.IsNullOrEmpty(guid))
            {
                guid = $"{assetFile.AssetId}_{assetFile.Path}";
            }

            if (_nodesByGuid.ContainsKey(guid))
            {
                return _nodesByGuid[guid];
            }

            DependencyGraphNode node = new DependencyGraphNode(assetFile);
            node.Depth = depth;
            Nodes.Add(node);
            _nodesByGuid[guid] = node;

            return node;
        }

        private DependencyGraphEdge CreateEdge(DependencyGraphNode source, DependencyGraphNode target)
        {
            // Check if edge already exists
            DependencyGraphEdge existingEdge = Edges.FirstOrDefault(e => e.Source == source && e.Target == target);
            if (existingEdge != null)
            {
                return existingEdge;
            }

            DependencyGraphEdge edge = new DependencyGraphEdge(source, target);
            Edges.Add(edge);
            source.AddOutgoingEdge(target);

            return edge;
        }

        private DependencyGraphNode GetNodeByGuid(string guid)
        {
            return _nodesByGuid.TryGetValue(guid, out DependencyGraphNode node) ? node : null;
        }

        private void DetectCrossDependencies()
        {
            // Mark edges that connect nodes from different packages
            foreach (DependencyGraphEdge edge in Edges)
            {
                if (edge.Source.AssetFile.AssetId != edge.Target.AssetFile.AssetId)
                {
                    edge.IsCrossDependency = true;
                    edge.Color = new Color(0.8f, 0.4f, 0.8f, 0.6f); // Purple for cross-package
                }
            }
        }

        private void DetectCycles()
        {
            HashSet<DependencyGraphNode> visited = new HashSet<DependencyGraphNode>();
            HashSet<DependencyGraphNode> recursionStack = new HashSet<DependencyGraphNode>();
            List<DependencyGraphNode> cycleNodes = new List<DependencyGraphNode>();

            foreach (DependencyGraphNode node in Nodes)
            {
                if (!visited.Contains(node))
                {
                    DetectCyclesRecursive(node, visited, recursionStack, cycleNodes);
                }
            }

            // Mark nodes and edges that are part of cycles
            foreach (DependencyGraphNode node in cycleNodes)
            {
                node.IsPartOfCycle = true;
            }

            foreach (DependencyGraphEdge edge in Edges)
            {
                if (edge.Source.IsPartOfCycle && edge.Target.IsPartOfCycle)
                {
                    edge.IsPartOfCycle = true;
                    edge.Color = new Color(1f, 0.2f, 0.2f, 0.8f); // Red for cycles
                }
            }
        }

        private bool DetectCyclesRecursive(DependencyGraphNode node, HashSet<DependencyGraphNode> visited,
            HashSet<DependencyGraphNode> recursionStack, List<DependencyGraphNode> cycleNodes)
        {
            visited.Add(node);
            recursionStack.Add(node);

            foreach (DependencyGraphNode neighbor in node.OutgoingNodes)
            {
                if (!visited.Contains(neighbor))
                {
                    if (DetectCyclesRecursive(neighbor, visited, recursionStack, cycleNodes))
                    {
                        cycleNodes.Add(node);
                        return true;
                    }
                }
                else if (recursionStack.Contains(neighbor))
                {
                    // Cycle detected
                    cycleNodes.Add(node);
                    cycleNodes.Add(neighbor);
                    return true;
                }
            }

            recursionStack.Remove(node);
            return false;
        }

        /// <summary>
        /// Calculate hierarchical depths for all nodes based on their actual parent-child relationships
        /// Uses BFS from root to ensure shortest path depth is assigned
        /// </summary>
        private void CalculateHierarchicalDepths()
        {
            if (RootNode == null) return;

            // Reset all depths
            foreach (DependencyGraphNode node in Nodes)
            {
                node.Depth = -1; // Mark as unvisited
            }

            // BFS from root to calculate depths
            Queue<DependencyGraphNode> queue = new Queue<DependencyGraphNode>();
            RootNode.Depth = 0;
            queue.Enqueue(RootNode);

            while (queue.Count > 0)
            {
                DependencyGraphNode current = queue.Dequeue();

                // Process all children (outgoing edges)
                foreach (DependencyGraphNode child in current.OutgoingNodes)
                {
                    // Only set depth if not yet visited (BFS ensures shortest path)
                    if (child.Depth == -1)
                    {
                        child.Depth = current.Depth + 1;
                        queue.Enqueue(child);
                    }
                }
            }

            // Handle any unconnected nodes (shouldn't happen, but be safe)
            foreach (DependencyGraphNode node in Nodes)
            {
                if (node.Depth == -1)
                {
                    // Orphan node - assign default depth of 1
                    node.Depth = 1;
                }
            }
        }

        private void AdjustColorsBasedOnDependencies()
        {
            // Darken nodes that have outgoing dependencies (i.e., they depend on other assets)
            const float darkeningFactor = 0.6f; // Multiply color by this to darken

            foreach (DependencyGraphNode node in Nodes)
            {
                if (node.OutgoingNodes.Count > 0)
                {
                    // This node has dependencies - darken its color
                    node.Color = new Color(
                        node.Color.r * darkeningFactor,
                        node.Color.g * darkeningFactor,
                        node.Color.b * darkeningFactor,
                        node.Color.a
                    );
                }
            }
        }

        public void SetSimplifiedMode(bool simplified)
        {
            if (simplified)
            {
                // In simplified mode, limit depth but keep files visible
                foreach (DependencyGraphNode node in Nodes)
                {
                    // Show nodes up to depth 2 by default
                    node.IsVisible = node.Depth <= 2;
                    node.IsExpanded = true;

                    if (node.IsVisible && node.OutgoingNodes.Count > 0)
                    {
                        // Count how many dependencies are hidden
                        node.HiddenDependencyCount = node.OutgoingNodes.Count(n => !n.IsVisible);
                    }
                    else
                    {
                        node.HiddenDependencyCount = 0;
                    }
                }

                // Update package visibility after changing file visibility
                foreach (PackageNode package in Packages)
                {
                    bool hasVisibleFiles = package.Files.Any(f => f.IsVisible);
                    package.IsVisible = hasVisibleFiles;
                }
            }
            else
            {
                // Show all nodes and packages
                foreach (DependencyGraphNode node in Nodes)
                {
                    node.IsVisible = true;
                    node.IsExpanded = true;
                    node.HiddenDependencyCount = 0;
                }

                // Update package visibility after changing file visibility
                foreach (PackageNode package in Packages)
                {
                    bool hasVisibleFiles = package.Files.Any(f => f.IsVisible);
                    package.IsVisible = hasVisibleFiles;
                }
            }

            // Update edge visibility
            UpdateEdgeVisibility();
        }

        public void ExpandNode(DependencyGraphNode node)
        {
            if (node.IsExpanded) return;

            node.IsExpanded = true;
            node.HiddenDependencyCount = 0;

            // Make all direct children visible
            foreach (DependencyGraphNode childNode in node.OutgoingNodes)
            {
                childNode.IsVisible = true;
                // Count their dependencies
                childNode.HiddenDependencyCount = childNode.OutgoingNodes.Count;
            }

            UpdateEdgeVisibility();
        }

        public void CollapseNode(DependencyGraphNode node)
        {
            if (!node.IsExpanded || node.IsRoot) return;

            node.IsExpanded = false;

            // Hide all descendants
            HashSet<DependencyGraphNode> toHide = new HashSet<DependencyGraphNode>();
            CollectDescendants(node, toHide);

            foreach (DependencyGraphNode descendant in toHide)
            {
                descendant.IsVisible = false;
            }

            node.HiddenDependencyCount = node.OutgoingNodes.Count;
            UpdateEdgeVisibility();
        }

        private void CollectDescendants(DependencyGraphNode node, HashSet<DependencyGraphNode> descendants)
        {
            foreach (DependencyGraphNode child in node.OutgoingNodes)
            {
                if (!descendants.Contains(child))
                {
                    descendants.Add(child);
                    CollectDescendants(child, descendants);
                }
            }
        }

        private void UpdateEdgeVisibility()
        {
            foreach (DependencyGraphEdge edge in Edges)
            {
                edge.IsVisible = edge.Source.IsVisible && edge.Target.IsVisible;
            }
        }

        private Texture2D GetIconForFileType(string fileType)
        {
            if (string.IsNullOrEmpty(fileType)) return null;

            // Map file extensions to Unity built-in icons
            string iconName = fileType.ToLower() switch
            {
                "cs" => "cs Script Icon",
                "dll" => "dll Script Icon",
                "shader" => "Shader Icon",
                "mat" => "Material Icon",
                "prefab" => "Prefab Icon",
                "png" or "jpg" or "jpeg" or "tga" => "Texture Icon",
                "fbx" or "obj" => "PrefabModel Icon",
                "wav" or "mp3" or "ogg" => "AudioSource Icon",
                "anim" => "Animation Icon",
                "controller" => "AnimatorController Icon",
                "ttf" or "otf" => "Font Icon",
                _ => "DefaultAsset Icon"
            };

            return EditorGUIUtility.IconContent(iconName)?.image as Texture2D;
        }

        public void Clear()
        {
            Nodes.Clear();
            Edges.Clear();
            Packages.Clear();
            _nodesByGuid.Clear();
            _packagesByAssetId.Clear();
            RootNode = null;
        }

        public Rect GetBoundingBox()
        {
            if (Nodes.Count == 0) return new Rect(0, 0, 100, 100);

            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;

            foreach (DependencyGraphNode node in Nodes.Where(n => n.IsVisible))
            {
                minX = Mathf.Min(minX, node.Position.x - node.Size);
                minY = Mathf.Min(minY, node.Position.y - node.Size);
                maxX = Mathf.Max(maxX, node.Position.x + node.Size);
                maxY = Mathf.Max(maxY, node.Position.y + node.Size);
            }

            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }
    }
}
