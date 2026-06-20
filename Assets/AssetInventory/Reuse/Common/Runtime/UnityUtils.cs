using UnityEngine;

namespace ImpossibleRobert.Common
{
    /// <summary>
    /// Unity-version-safe wrappers for Object.Find* APIs.
    /// Centralises all #if version guards so call sites stay clean.
    /// Supports Unity 2021.3 through 6.x on all graphics backends.
    /// </summary>
    public static class UnityUtils
    {
        /// <summary>
        /// Returns any single active object of type T in the loaded scenes.
        /// Uses FindAnyObjectByType on 2023.1+ (no ordering dependency) and
        /// falls back to FindObjectOfType on older versions.
        /// </summary>
        public static T FindAny<T>() where T : Object
        {
#if UNITY_2023_1_OR_NEWER
            return Object.FindAnyObjectByType<T>();
#else
            return Object.FindObjectOfType<T>();
#endif
        }

        /// <summary>
        /// Returns all active objects of type T in the loaded scenes.
        /// Uses the lowest-overhead, non-deprecated API for each Unity version:
        ///   2021.3        → FindObjectsOfType (legacy)
        ///   2023.1+       → FindObjectsByType with FindObjectsSortMode.None
        /// </summary>
        public static T[] FindAll<T>() where T : Object
        {
#if UNITY_2023_1_OR_NEWER
#pragma warning disable CS0618
            return Object.FindObjectsByType<T>(FindObjectsSortMode.None);
#pragma warning restore CS0618
#else
            return Object.FindObjectsOfType<T>();
#endif
        }

        /// <summary>
        /// Returns all active and inactive objects of type T in the loaded scenes.
        /// </summary>
        public static T[] FindAllIncludingInactive<T>() where T : Object
        {
#if UNITY_2023_1_OR_NEWER
#pragma warning disable CS0618
            return Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#pragma warning restore CS0618
#else
            return Object.FindObjectsOfType<T>(true);
#endif
        }

        /// <summary>
        /// Returns a stable integer identifier for the object.
        /// Uses GetEntityId on Unity 6.2+ and GetInstanceID on older versions.
        /// </summary>
        public static int GetStableId(this Object obj)
        {
#if UNITY_6000_2_OR_NEWER
            return obj.GetEntityId().GetHashCode();
#else
            return obj.GetInstanceID();
#endif
        }
    }
}
