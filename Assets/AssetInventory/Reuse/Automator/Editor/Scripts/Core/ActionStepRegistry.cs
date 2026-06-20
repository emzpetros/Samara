using System;
using System.Collections.Generic;
using System.Linq;

namespace Automator
{
    /// <summary>
    /// Registry for action steps. Discovers implementations via reflection.
    /// </summary>
    public static class ActionStepRegistry
    {
        private static List<ActionStep> _steps;
        private static readonly object _lock = new object();

        /// <summary>
        /// Gets all registered action steps.
        /// </summary>
        public static List<ActionStep> Steps
        {
            get
            {
                if (_steps == null)
                {
                    lock (_lock)
                    {
                        if (_steps == null)
                        {
                            DiscoverSteps();
                        }
                    }
                }
                return _steps;
            }
        }

        /// <summary>
        /// Finds a step by its key.
        /// </summary>
        public static ActionStep GetStep(string key)
        {
            return Steps.FirstOrDefault(s => s.Key == key);
        }

        /// <summary>
        /// Forces re-discovery of all steps.
        /// </summary>
        public static void Refresh()
        {
            lock (_lock)
            {
                DiscoverSteps();
            }
        }

        private static void DiscoverSteps()
        {
            _steps = new List<ActionStep>();

            Type baseType = typeof(ActionStep);

            // Search all loaded assemblies for ActionStep implementations
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (Type type in assembly.GetTypes())
                    {
                        if (type.IsClass && !type.IsAbstract && baseType.IsAssignableFrom(type))
                        {
                            try
                            {
                                ActionStep instance = (ActionStep)Activator.CreateInstance(type);
                                if (!string.IsNullOrEmpty(instance.Key))
                                {
                                    _steps.Add(instance);
                                }
                            }
                            catch
                            {
                                // Skip types that fail to instantiate
                            }
                        }
                    }
                }
                catch
                {
                    // Skip assemblies that can't be reflected
                }
            }

            // Sort by category then name for consistent ordering
            _steps = _steps.OrderBy(s => s.Category).ThenBy(s => s.Name).ToList();
        }
    }
}
