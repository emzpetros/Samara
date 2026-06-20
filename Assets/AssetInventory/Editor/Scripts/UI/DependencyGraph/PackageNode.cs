using System.Collections.Generic;
using UnityEngine;

namespace AssetInventory
{
    public class PackageNode
    {
        public int AssetId { get; private set; }
        public string Name { get; set; }
        public Vector2 Position { get; set; }
        public Vector2 Velocity { get; set; }
        public Vector2 Force { get; set; }
        public Color Color { get; set; }
        public bool IsExpanded { get; set; }
        public bool IsVisible { get; set; }
        public Rect Bounds { get; set; }

        public AssetInfo AssetInfo { get; set; }
        public List<DependencyGraphNode> Files { get; private set; }

        // For rendering
        public float HeaderHeight { get; set; } = 25f;
        public float Padding { get; set; } = 40f; // Generous padding for visibility
        public float MinWidth { get; set; } = 120f;
        public float MinHeight { get; set; } = 100f;

        public PackageNode(int assetId)
        {
            AssetId = assetId;
            Files = new List<DependencyGraphNode>();
            Position = Vector2.zero;
            Velocity = Vector2.zero;
            Force = Vector2.zero;
            IsExpanded = false; // Start collapsed - will auto-expand for consistent layout
            IsVisible = true;
            Color = Color.white;
            Name = $"Package {assetId}";
        }

        public void AddFile(DependencyGraphNode node)
        {
            if (!Files.Contains(node))
            {
                Files.Add(node);
            }
        }

        public void RemoveFile(DependencyGraphNode node)
        {
            Files.Remove(node);
        }

        public void ResetForces()
        {
            Force = Vector2.zero;
        }

        public void ApplyForce(Vector2 force)
        {
            Force += force;
        }

        public void UpdateVelocity(float damping)
        {
            Velocity = (Velocity + Force) * damping;
        }

        public void UpdatePosition(float deltaTime)
        {
            Position += Velocity * deltaTime;
        }

        public Rect CalculateBounds()
        {
            if (!IsExpanded || Files.Count == 0)
            {
                // Collapsed - fixed size
                return new Rect(Position.x - MinWidth / 2f, Position.y - MinHeight / 2f, MinWidth, MinHeight);
            }

            // Expanded - calculate based on file positions with generous padding
            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;

            int visibleCount = 0;
            foreach (DependencyGraphNode file in Files)
            {
                if (!file.IsVisible) continue;
                visibleCount++;

                // Include full bounds of file including label
                float fileWidth = Mathf.Max(file.Size, file.FullBounds.width);
                float fileHeight = file.FullBounds.height > 0 ? file.FullBounds.height : file.Size;

                minX = Mathf.Min(minX, file.Position.x - fileWidth / 2f);
                minY = Mathf.Min(minY, file.Position.y - file.Size / 2f);
                maxX = Mathf.Max(maxX, file.Position.x + fileWidth / 2f);
                maxY = Mathf.Max(maxY, file.Position.y + fileHeight);
            }

            if (minX == float.MaxValue || visibleCount == 0)
            {
                // No visible files
                return new Rect(Position.x - MinWidth / 2f, Position.y - MinHeight / 2f, MinWidth, MinHeight);
            }

            // Add generous padding around files
            float width = Mathf.Max(MinWidth, maxX - minX + Padding * 2);
            float height = Mathf.Max(MinHeight, maxY - minY + Padding * 2 + HeaderHeight);

            return new Rect(minX - Padding, minY - Padding - HeaderHeight, width, height);
        }

        public bool ContainsPoint(Vector2 point)
        {
            return Bounds.Contains(point);
        }

        public bool HeaderContainsPoint(Vector2 point, float scaledHeaderHeight)
        {
            // Check if point is within the header area (top portion of bounds)
            // Uses scaledHeaderHeight because Bounds is in screen space (scaled)
            Rect headerRect = new Rect(Bounds.x, Bounds.y, Bounds.width, scaledHeaderHeight);
            return headerRect.Contains(point);
        }

        public void ToggleExpanded()
        {
            IsExpanded = !IsExpanded;

            // Update file visibility based on expanded state
            foreach (DependencyGraphNode file in Files)
            {
                if (IsExpanded)
                {
                    file.IsVisible = true; // Show all files when expanding
                }
                else
                {
                    file.IsVisible = false; // Hide all files when collapsing
                }
            }
        }

        public int GetCrossPackageEdgeCount(List<DependencyGraphEdge> allEdges)
        {
            int count = 0;
            foreach (DependencyGraphEdge edge in allEdges)
            {
                if (edge.IsCrossDependency &&
                    (Files.Contains(edge.Source) || Files.Contains(edge.Target)))
                {
                    count++;
                }
            }
            return count;
        }

        public override string ToString()
        {
            return $"Package: {Name} ({Files.Count} files)";
        }
    }
}
