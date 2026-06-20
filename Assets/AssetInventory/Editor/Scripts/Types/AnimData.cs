using System;

namespace AssetInventory
{
    /// <summary>
    /// Metadata for standalone .anim (AnimationClip) files.
    /// Similar to FBXData but for animation-only assets.
    /// </summary>
    [Serializable]
    public sealed class AnimData
    {
        /// <summary>
        /// Name of the animation clip.
        /// </summary>
        public string name;

        /// <summary>
        /// Duration of the animation in seconds.
        /// </summary>
        public float length;

        /// <summary>
        /// True if this is a humanoid animation (uses Avatar muscle space).
        /// False for generic animations (uses Transform paths).
        /// </summary>
        public bool isHumanMotion;

        /// <summary>
        /// True if the clip is marked as looping.
        /// </summary>
        public bool isLooping;

        /// <summary>
        /// Frame rate of the animation.
        /// </summary>
        public float frameRate;

        /// <summary>
        /// GUID of the referenced FBX model dependency (if any).
        /// </summary>
        public string referencedModelGuid;

        public AnimData()
        {
        }

        public override string ToString()
        {
            string type = isHumanMotion ? "Humanoid" : "Generic";
            return $"Anim Data: {name}, {length:F2}s, {type}";
        }
    }
}
