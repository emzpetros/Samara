using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ImpossibleRobert.Common
{
    [Serializable]
    public sealed class ToolInfo
    {
        public string id;
        public string name;
        public string logoTextureName;
        public string description;
        public string assetStoreLink;
        public string webLink;
        public string packageName;
        public bool isAddon;
        public bool isPublic;
    }

    [Serializable]
    internal sealed class PackageJson
    {
        public string version;
    }

    [Serializable]
    public sealed class ToolCatalog
    {
        public string publisher;
        public string discordLink;
        public List<ToolInfo> tools = new List<ToolInfo>();

        private static ToolCatalog _instance;

        public static ToolCatalog Load(bool forceReload = false)
        {
            if (_instance != null && !forceReload) return _instance;

            string[] guids = AssetDatabase.FindAssets("tools t:TextAsset");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith("tools.json")) continue;

                TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                if (asset == null) continue;

                _instance = JsonUtility.FromJson<ToolCatalog>(asset.text);
                return _instance;
            }

            Debug.LogWarning("ToolCatalog: tools.json not found.");
            _instance = new ToolCatalog();
            return _instance;
        }

        public ToolInfo GetTool(string toolId)
        {
            return tools?.FirstOrDefault(t => string.Equals(t.id, toolId, StringComparison.OrdinalIgnoreCase));
        }

        public List<ToolInfo> GetOtherTools(string excludeToolId)
        {
            return tools?.Where(t => !string.Equals(t.id, excludeToolId, StringComparison.OrdinalIgnoreCase) && t.isPublic && !t.isAddon).ToList()
                ?? new List<ToolInfo>();
        }

        /// <summary>
        /// Resolves the version for a tool. If an explicit version is provided, it is returned as-is.
        /// Otherwise reads the version from the tool's package.json using the packageName field.
        /// </summary>
        public static string ResolveVersion(string toolId, string explicitVersion = null)
        {
            if (!string.IsNullOrEmpty(explicitVersion)) return explicitVersion;

            ToolCatalog catalog = Load();
            ToolInfo info = catalog.GetTool(toolId);
            if (info == null || string.IsNullOrEmpty(info.packageName)) return "Unknown";

            string[] guids = AssetDatabase.FindAssets("package t:TextAsset");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith("package.json")) continue;

                TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                if (asset == null) continue;

                // Quick check before full deserialization
                if (!asset.text.Contains(info.packageName)) continue;

                try
                {
                    PackageJson pkg = JsonUtility.FromJson<PackageJson>(asset.text);
                    if (pkg != null && !string.IsNullOrEmpty(pkg.version))
                    {
                        // Verify this is actually the right package by checking the name field
                        if (asset.text.Contains($"\"name\": \"{info.packageName}\"") ||
                            asset.text.Contains($"\"name\":\"{info.packageName}\""))
                        {
                            return pkg.version;
                        }
                    }
                }
                catch
                {
                    // ignore malformed package.json
                }
            }

            return "Unknown";
        }
    }
}