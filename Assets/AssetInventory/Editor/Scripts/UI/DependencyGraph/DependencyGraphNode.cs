using System.Collections.Generic;
using UnityEngine;

namespace AssetInventory
{
    public class DependencyGraphNode
    {
        public AssetFile AssetFile { get; private set; }
        public Vector2 Position { get; set; }
        public Vector2 Velocity { get; set; }
        public Vector2 Force { get; set; }

        public bool IsRoot { get; set; }
        public bool IsExpanded { get; set; }
        public bool IsVisible { get; set; }
        public bool IsPartOfCycle { get; set; }

        // Package and depth info
        public PackageNode PackageNode { get; set; }
        public int Depth { get; set; }

        public List<DependencyGraphNode> OutgoingNodes { get; private set; }
        public List<DependencyGraphNode> IncomingNodes { get; private set; }

        // Visual properties
        public float Size { get; set; }
        public Color Color { get; set; }
        public Texture2D Icon { get; set; }

        // For expandable nodes in simplified mode
        public int HiddenDependencyCount { get; set; }
        public bool HasHiddenDependencies => HiddenDependencyCount > 0;

        // Bounds for hit testing
        public Rect Bounds { get; set; }
        public Rect FullBounds { get; set; } // Including label for collision detection

        public DependencyGraphNode(AssetFile assetFile)
        {
            AssetFile = assetFile;
            OutgoingNodes = new List<DependencyGraphNode>();
            IncomingNodes = new List<DependencyGraphNode>();
            Position = Vector2.zero;
            Velocity = Vector2.zero;
            Force = Vector2.zero;
            IsVisible = true;
            Size = 40f; // Default node size - smaller for better visibility
            Color = Color.white;
        }

        public void AddOutgoingEdge(DependencyGraphNode target)
        {
            if (!OutgoingNodes.Contains(target))
            {
                OutgoingNodes.Add(target);
            }
            if (!target.IncomingNodes.Contains(this))
            {
                target.IncomingNodes.Add(this);
            }
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
            if (!IsRoot) // Root node stays fixed
            {
                Position += Velocity * deltaTime;
            }
        }

        public float DistanceTo(DependencyGraphNode other)
        {
            return Vector2.Distance(Position, other.Position);
        }

        public bool ContainsPoint(Vector2 point)
        {
            return Bounds.Contains(point);
        }

        public string GetDisplayName()
        {
            if (AssetFile == null) return "Unknown";
            return string.IsNullOrEmpty(AssetFile.FileName) ? AssetFile.Path : AssetFile.FileName;
        }

        public override string ToString()
        {
            return $"Node: {GetDisplayName()} at {Position}";
        }
    }
}
