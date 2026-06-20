using UnityEditor;
using UnityEngine;

namespace ImpossibleRobert.Common
{
    /// <summary>
    /// Version-safe wrappers for Object identity APIs deprecated in Unity 6.4+.
    /// Companion to <see cref="UnityUtils"/> (runtime) for editor code.
    /// </summary>
    public static class UnityEditorCompat
    {
        /// <summary>
        /// Version-safe wrapper for AssetPreview.IsLoadingAssetPreview.
        /// Uses the EntityId overload on Unity 6.3+ and the int overload on older versions.
        /// </summary>
        public static bool IsLoadingPreview(Object obj)
        {
#if UNITY_6000_3_OR_NEWER
            return AssetPreview.IsLoadingAssetPreview(obj.GetEntityId());
#else
            return AssetPreview.IsLoadingAssetPreview(obj.GetInstanceID());
#endif
        }
    }
}
