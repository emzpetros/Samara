#if ASSET_INVENTORY_MCP
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.AI.MCP.Editor.Helpers;
using Unity.AI.MCP.Editor.ToolRegistry;
using UnityEditor;

namespace AssetInventory
{
    public static class UtilityTools
    {
        #region Inventory Stats

        [McpTool("AssetInventory_getInventoryStats",
            "Get database overview: total packages, indexed count, file count, and source breakdown.",
            Groups = new[] {"Asset Inventory"})]
        public static object GetInventoryStats()
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            InventoryStats stats = Assets.GetInventoryStats();
            stats.DatabaseSize = DBAdapter.GetDBSize();

            return Response.Success($"Inventory contains {stats.TotalPackages} packages ({stats.IndexedPackages} indexed) with {stats.TotalFiles} files.", stats);
        }

        #endregion

        #region Open Window

        [McpTool("AssetInventory_openWindow",
            "Open the Asset Inventory editor window.",
            Groups = new[] {"Asset Inventory"})]
        public static object OpenAssetInventory()
        {
            EditorWindow.GetWindow<IndexUI>("Asset Inventory");
            return Response.Success("Asset Inventory window opened.");
        }

        #endregion

        #region Close Window

        [McpTool("AssetInventory_closeWindow",
            "Close the Asset Inventory editor window.",
            Groups = new[] {"Asset Inventory"})]
        public static object CloseAssetInventory()
        {
            IndexUI[] windows = UnityEngine.Resources.FindObjectsOfTypeAll<IndexUI>();
            if (windows.Length == 0)
            {
                return Response.Success("Asset Inventory window was not open.");
            }

            foreach (IndexUI window in windows)
            {
                window.Close();
            }
            return Response.Success("Asset Inventory window closed.");
        }

        #endregion

        #region Check Asset In Project

        public class CheckAssetParams
        {
            [McpDescription("Asset GUID (the 'guid' field from searchAssets or listPackageFiles results).", Required = true)]
            public string Guid { get; set; }
        }

        [McpTool("AssetInventory_checkAssetInProject",
            "Check if an asset (by GUID) already exists in the current project. Returns project path if found.",
            Groups = new[] {"Asset Inventory/Search"})]
        public static object CheckAssetInProject(CheckAssetParams parameters)
        {
            if (string.IsNullOrEmpty(parameters.Guid))
            {
                return Response.Error("GUID is required.");
            }

            string projectPath = AssetDatabase.GUIDToAssetPath(parameters.Guid);
            bool exists = !string.IsNullOrEmpty(projectPath) && File.Exists(projectPath);

            return Response.Success(exists ? $"Asset found at '{projectPath}'." : "Asset not found in project.", new
            {
                exists,
                projectPath = exists ? projectPath : null
            });
        }

        #endregion

        #region Asset Group Types

        [McpTool("AssetInventory_getAssetGroupTypes",
            "Get asset group classifications and their file extensions (e.g. Prefabs=[prefab], Models=[fbx,obj,...], Audio=[wav,mp3,...]).",
            Groups = new[] {"Asset Inventory/Search"})]
        public static object GetAssetGroupTypes()
        {
            Dictionary<string, string[]> groups = AI.TypeGroups.ToDictionary(
                kvp => kvp.Key.ToString(),
                kvp => kvp.Value
            );

            return Response.Success($"Found {groups.Count} asset group types.", new { groups });
        }

        #endregion
    }
}
#endif
