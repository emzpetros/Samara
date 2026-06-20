using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    /// <summary>
    /// Project-view context menu integration that runs Asset Inventory's custom
    /// pipeline converter on the current selection. Supports individual materials,
    /// prefabs/models (converts their referenced material assets), and folders
    /// (recurses into all subfolders).
    /// </summary>
    public static class PipelineConverterMenu
    {
        private const string MENU_PATH = "Assets/Convert to Current Render Pipeline";

#if !ASSET_INVENTORY_HIDE_AI
        [MenuItem(MENU_PATH, priority = 9002)]
#endif
        public static void Convert()
        {
            if (!AssetUtils.IsOnURP() && !AssetUtils.IsOnHDRP())
            {
                EditorUtility.DisplayDialog("Asset Inventory",
                    "The current project is not using URP or HDRP. No conversion is needed.", "OK");
                return;
            }

            HashSet<string> materialPaths;
            try
            {
                EditorUtility.DisplayProgressBar("Asset Inventory", "Collecting materials...", 0f);
                materialPaths = CollectMaterialPaths(Selection.assetGUIDs);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (materialPaths.Count == 0)
            {
                EditorUtility.DisplayDialog("Asset Inventory",
                    "No materials found in the current selection.", "OK");
                return;
            }

            string pipeline = AssetUtils.IsOnURP() ? "URP" : "HDRP";
            if (!EditorUtility.DisplayDialog("Asset Inventory",
                    $"Convert {materialPaths.Count} material(s) to {pipeline}?\n\n" +
                    "Only Built-in Render Pipeline materials will be touched. " +
                    "Materials already on the current pipeline (or unknown shaders) are left untouched.",
                    "Convert", "Cancel"))
            {
                return;
            }

            try
            {
                EditorUtility.DisplayProgressBar("Asset Inventory",
                    $"Converting {materialPaths.Count} material(s) to {pipeline}...", 0.5f);
                PipelineConverter.ConvertImportedMaterials(materialPaths);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            Debug.Log($"[Asset Inventory] Pipeline conversion finished. Inspected {materialPaths.Count} material(s) for {pipeline}.");
        }

#if !ASSET_INVENTORY_HIDE_AI
        [MenuItem(MENU_PATH, true)]
#endif
        public static bool ConvertValidate()
        {
            if (!AssetUtils.IsOnURP() && !AssetUtils.IsOnHDRP()) return false;
            string[] guids = Selection.assetGUIDs;
            return guids != null && guids.Length > 0;
        }

        private static HashSet<string> CollectMaterialPaths(string[] guids)
        {
            HashSet<string> result = new HashSet<string>();
            if (guids == null) return result;

            List<string> folders = new List<string>();
            List<string> nonFolderAssets = new List<string>();

            foreach (string guid in guids)
            {
                if (string.IsNullOrEmpty(guid)) continue;
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/")) continue;

                if (AssetDatabase.IsValidFolder(path))
                {
                    folders.Add(path);
                }
                else
                {
                    nonFolderAssets.Add(path);
                }
            }

            // Folders: find all materials within (recursive by default)
            if (folders.Count > 0)
            {
                string[] matGuids = AssetDatabase.FindAssets("t:Material", folders.ToArray());
                foreach (string g in matGuids)
                {
                    string p = AssetDatabase.GUIDToAssetPath(g);
                    if (!string.IsNullOrEmpty(p) && p.StartsWith("Assets/")) result.Add(p);
                }
            }

            // Individual assets
            foreach (string path in nonFolderAssets)
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".mat")
                {
                    result.Add(path);
                    continue;
                }

                // Prefab / model / scene / other: pull referenced materials from dependencies
                string[] deps = AssetDatabase.GetDependencies(path, true);
                foreach (string dep in deps)
                {
                    if (string.IsNullOrEmpty(dep)) continue;
                    if (!dep.StartsWith("Assets/")) continue;
                    if (dep.EndsWith(".mat", System.StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(dep);
                    }
                }
            }

            return result;
        }
    }
}