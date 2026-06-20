using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace ImpossibleRobert.Common
{
    /// <summary>
    /// Resolves $variable references in text. Supports user-defined and internal variables (e.g., $Application.unityVersion).
    /// </summary>
    public static class VariableResolver
    {
        // Pattern matches $varname where varname starts with letter or underscore
        // and can contain letters, numbers, underscores, and dots (for internal variables)
        private static readonly Regex VariablePattern = new Regex(@"\$([a-zA-Z_][a-zA-Z0-9_.]*)", RegexOptions.Compiled);

        /// <summary>
        /// Finds all variable references in the given text.
        /// </summary>
        public static List<string> FindVariableReferences(string text)
        {
            if (string.IsNullOrEmpty(text)) return new List<string>();

            MatchCollection matches = VariablePattern.Matches(text);

            // Use HashSet for deduplication, avoiding LINQ overhead
            HashSet<string> uniqueVars = new HashSet<string>();
            for (int i = 0; i < matches.Count; i++)
            {
                uniqueVars.Add(matches[i].Groups[1].Value);
            }

            return new List<string>(uniqueVars);
        }

        /// <summary>
        /// Replaces all variable references with their values. Throws if a variable cannot be resolved.
        /// </summary>
        public static string ReplaceVariables(string text, Dictionary<string, string> variables)
        {
            if (string.IsNullOrEmpty(text)) return text;

            return VariablePattern.Replace(text, match =>
            {
                string varName = match.Groups[1].Value;

                // Check if this is an internal variable (contains a dot)
                if (IsInternalVariable(varName))
                {
                    // Resolve as internal variable using reflection
                    return ResolveInternalVariable(varName);
                }
                else
                {
                    // Resolve as user-defined variable
                    if (variables != null && variables.TryGetValue(varName, out string value))
                    {
                        return value;
                    }

                    // Variable not found - throw error
                    throw new Exception($"Variable '${varName}' is not defined");
                }
            });
        }

        /// <summary>
        /// Checks if the text contains any variable references.
        /// </summary>
        public static bool ContainsVariables(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            return VariablePattern.IsMatch(text);
        }

        /// <summary>
        /// Validates that all variable references can be resolved. Returns error messages for undefined variables.
        /// </summary>
        public static List<string> ValidateVariables(string text, Dictionary<string, string> variables)
        {
            List<string> referencedVars = FindVariableReferences(text);
            if (referencedVars.Count == 0) return new List<string>();

            List<string> errors = new List<string>();
            foreach (string varName in referencedVars)
            {
                if (IsInternalVariable(varName))
                {
                    // Validate internal variable by attempting to resolve it
                    try
                    {
                        ResolveInternalVariable(varName);
                    }
                    catch (Exception e)
                    {
                        errors.Add($"${varName}: {e.Message}");
                    }
                }
                else
                {
                    // Validate user-defined variable
                    if (variables == null || !variables.ContainsKey(varName))
                    {
                        errors.Add($"${varName}: Variable is not defined");
                    }
                }
            }
            return errors;
        }

        /// <summary>
        /// Validates a user-defined variable name. Dots are reserved for internal variables.
        /// </summary>
        public static bool IsValidVariableName(string variableName)
        {
            if (string.IsNullOrWhiteSpace(variableName)) return false;

            // Must start with letter or underscore
            if (!char.IsLetter(variableName[0]) && variableName[0] != '_') return false;

            // Can only contain letters, numbers, underscores (NO dots for user-defined variables)
            foreach (char c in variableName)
            {
                if (!char.IsLetterOrDigit(c) && c != '_') return false;
            }

            return true;
        }

        /// <summary>
        /// Returns true if the variable name contains a dot (internal variable).
        /// </summary>
        public static bool IsInternalVariable(string variableName)
        {
            return !string.IsNullOrEmpty(variableName) && variableName.Contains(".");
        }

        /// <summary>
        /// Gets all available variable groups (built-in and registered).
        /// </summary>
        public static List<string> GetAvailableGroups()
        {
            List<string> groups = new List<string>
            {
                "Application",
                "SystemInfo",
                "Environment",
                "DateTime",
                "PlayerSettings",
                "EditorApplication",
                "BuildTarget",
                "QualitySettings",
                "Screen"
            };

            // Add registered custom groups
            foreach (string customGroup in VariableGroupRegistry.GetRegisteredGroups())
            {
                if (!groups.Contains(customGroup))
                {
                    groups.Add(customGroup);
                }
            }

            return groups;
        }

        /// <summary>
        /// Resolves an internal variable (e.g., "Application.version") using reflection.
        /// </summary>
        public static string ResolveInternalVariable(string variableName)
        {
            if (string.IsNullOrEmpty(variableName))
            {
                throw new Exception("Variable name cannot be empty");
            }

            string[] parts = variableName.Split('.');
            if (parts.Length < 2)
            {
                throw new Exception($"Internal variable '{variableName}' must have at least two components (Group.Property)");
            }

            string groupName = parts[0];
            Type targetType;
            object targetInstance = null;

            // First check if there's a registered custom group
            if (VariableGroupRegistry.TryGetInstance(groupName, out object registeredInstance, out Type registeredType))
            {
                targetType = registeredType;
                targetInstance = registeredInstance;
            }
            else
            {
                // Map group name to built-in type
                switch (groupName)
                {
                    case "Application":
                        targetType = typeof (Application);
                        break;
                    case "SystemInfo":
                        targetType = typeof (SystemInfo);
                        break;
                    case "Environment":
                        targetType = typeof (Environment);
                        break;
                    case "DateTime":
                        targetType = typeof (DateTime);
                        break;
                    case "PlayerSettings":
                        targetType = typeof (PlayerSettings);
                        break;
                    case "EditorApplication":
                        targetType = typeof (EditorApplication);
                        break;
                    case "BuildTarget":
                        targetType = typeof (EditorUserBuildSettings);
                        break;
                    case "QualitySettings":
                        targetType = typeof (QualitySettings);
                        break;
                    case "Screen":
                        targetType = typeof (Screen);
                        break;
                    default:
                        StringBuilder availableGroups = new StringBuilder();
                        availableGroups.Append("Application, SystemInfo, Environment, DateTime, PlayerSettings, EditorApplication, BuildTarget, QualitySettings, Screen");
                        foreach (string customGroup in VariableGroupRegistry.GetRegisteredGroups())
                        {
                            availableGroups.Append(", ").Append(customGroup);
                        }
                        throw new Exception($"Unknown internal variable group: '{groupName}'. Valid groups are: {availableGroups}");
                }
            }

            // Navigate through the property/field path
            object currentValue = targetInstance;
            Type currentType = targetType;

            for (int i = 1; i < parts.Length; i++)
            {
                string memberName = parts[i];

                // Try to get as property first
                PropertyInfo property = currentType.GetProperty(memberName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);

                if (property != null)
                {
                    try
                    {
                        currentValue = property.GetValue(currentValue, null);

                        if (currentValue == null)
                        {
                            // Property exists but returned null
                            return string.Empty;
                        }

                        currentType = currentValue.GetType();
                    }
                    catch (Exception e)
                    {
                        string memberPath = string.Join(".", parts, 0, i + 1);
                        throw new Exception($"Cannot access property '{memberPath}': {e.Message}", e);
                    }
                }
                else
                {
                    // Try to get as field if property not found
                    FieldInfo field = currentType.GetField(memberName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);

                    if (field == null)
                    {
                        throw new Exception($"Property or field '{memberName}' not found on type '{currentType.Name}'");
                    }

                    try
                    {
                        currentValue = field.GetValue(currentValue);

                        if (currentValue == null)
                        {
                            // Field exists but returned null
                            return string.Empty;
                        }

                        currentType = currentValue.GetType();
                    }
                    catch (Exception e)
                    {
                        string memberPath = string.Join(".", parts, 0, i + 1);
                        throw new Exception($"Cannot access field '{memberPath}': {e.Message}", e);
                    }
                }
            }

            return currentValue?.ToString() ?? string.Empty;
        }
    }
}