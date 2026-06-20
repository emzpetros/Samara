#if ASSET_INVENTORY_MCP
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unity.AI.MCP.Editor.Helpers;
using Unity.AI.MCP.Editor.ToolRegistry;

namespace AssetInventory
{
    public static class ImportAssetTool
    {
        public class ImportAssetParams
        {
            [McpDescription("File ID to import (the 'id' field from searchAssets or listPackageFiles results).", Required = true)]
            public int FileId { get; set; }

            [McpDescription("Target project folder, e.g. 'Assets/ThirdParty'. Uses the user's configured default import folder if empty.")]
            public string TargetFolder { get; set; }

            [McpDescription("Import dependencies automatically (e.g. textures and materials referenced by a prefab or model).", Default = true)]
            public bool WithDependencies { get; set; } = true;

            [McpDescription("Script dependency import mode: 0=no scripts (only media dependencies like textures/materials), " +
                "2=direct script dependencies only (scripts directly referenced via GUID), " +
                "3=extended analysis (direct + indirectly referenced scripts), " +
                "4=all scripts from the same package. Use 0 for visual assets, 2-4 when scripts are needed.", Default = 0)]
            public int ScriptMode { get; set; }
        }

        [McpTool("AssetInventory_importAsset",
            "Import a file into the Unity project with automatic dependency resolution. Returns importedPath for use with addToScene.",
            Groups = new[] {"Asset Inventory/Import"})]        public static async Task<object> ImportAsset(ImportAssetParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            AssetFile file = DBAdapter.DB.Find<AssetFile>(parameters.FileId);
            if (file == null)
            {
                return Response.Error($"Asset file with ID {parameters.FileId} not found.");
            }

            Asset asset = DBAdapter.DB.Find<Asset>(file.AssetId);
            if (asset == null)
            {
                return Response.Error($"Parent package for file ID {parameters.FileId} not found.");
            }

            // Check if already in project
            file.CheckIfInProject();
            if (file.InProject)
            {
                bool isPrefabExisting = AssetUtils.IsPrefab(file.ProjectPath);
                return Response.Success($"File '{file.FileName}' is already in the project.", new
                {
                    importedPath = file.ProjectPath,
                    isPrefab = isPrefabExisting,
                    alreadyInProject = true
                });
            }

            string targetFolder = !string.IsNullOrEmpty(parameters.TargetFolder) ? parameters.TargetFolder : AI.GetImportFolder();

            AssetInfo info = new AssetInfo().CopyFrom(asset, file);

            string importedPath = await Assets.CopyTo(info, targetFolder, parameters.WithDependencies, parameters.ScriptMode);

            if (string.IsNullOrEmpty(importedPath))
            {
                return Response.Error($"Failed to import file '{file.FileName}'. The file might not be downloadable or extractable.");
            }

            bool isPrefab = AssetUtils.IsPrefab(importedPath);
            return Response.Success($"File '{file.FileName}' imported to '{importedPath}'.", new
            {
                importedPath,
                isPrefab,
                alreadyInProject = false
            });
        }
    }
}
#endif
