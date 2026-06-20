using System;
using System.Collections.Generic;

namespace ImpossibleRobert.Common
{
    /// <summary>
    /// Registry for custom variable groups resolved by VariableResolver (e.g., $Config.xxx).
    /// </summary>
    public static class VariableGroupRegistry
    {
        private static readonly Dictionary<string, Func<object>> _instanceGetters = new Dictionary<string, Func<object>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Registers a variable group. The getter is called on each resolve to ensure fresh values.
        /// </summary>
        public static void Register(string groupName, Func<object> instanceGetter)
        {
            if (string.IsNullOrEmpty(groupName)) throw new ArgumentException("Group name cannot be empty", nameof (groupName));
            if (instanceGetter == null) throw new ArgumentNullException(nameof (instanceGetter));

            _instanceGetters[groupName] = instanceGetter;
        }

        /// <summary>
        /// Unregisters a variable group.
        /// </summary>
        public static void Unregister(string groupName)
        {
            if (!string.IsNullOrEmpty(groupName))
            {
                _instanceGetters.Remove(groupName);
            }
        }

        /// <summary>
        /// Tries to get a registered instance for a group.
        /// </summary>
        public static bool TryGetInstance(string groupName, out object instance, out Type instanceType)
        {
            if (_instanceGetters.TryGetValue(groupName, out Func<object> getter))
            {
                instance = getter();
                instanceType = instance?.GetType();
                return instance != null;
            }

            instance = null;
            instanceType = null;
            return false;
        }

        /// <summary>
        /// Gets all registered group names.
        /// </summary>
        public static IEnumerable<string> GetRegisteredGroups()
        {
            return _instanceGetters.Keys;
        }

        /// <summary>
        /// Clears all registered groups.
        /// </summary>
        public static void Clear()
        {
            _instanceGetters.Clear();
        }
    }
}