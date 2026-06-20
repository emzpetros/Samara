using System;
using System.Collections.Generic;

namespace AssetInventory
{
    [Serializable]
    public sealed class AnimationInfo
    {
        public string name;
        public float length;

        public AnimationInfo()
        {
        }
    }

    [Serializable]
    public sealed class FBXData
    {
        public List<AnimationInfo> animations;
        public int vertexCount;
        public int triangleCount;
        public int boneCount;
        public int materialCount;
        public int meshCount;

        public FBXData()
        {
            animations = new List<AnimationInfo>();
        }

        public override string ToString()
        {
            return $"FBX Data: {animations?.Count ?? 0} animations, {vertexCount} vertices, {triangleCount} triangles";
        }
    }
}
