#if ASSET_INVENTORY_MCP
using Unity.AI.MCP.Editor.Helpers;
using Unity.AI.MCP.Editor.ToolRegistry;

namespace AssetInventory
{
    public static class PackageDetailsTool
    {
        public class PackageDetailsParams
        {
            [McpDescription("Package ID (the 'id' field from searchPackages or searchAssets results).", Required = true)]
            public int PackageId { get; set; }
        }

        [McpTool("AssetInventory_getPackageDetails",
            "Get full package metadata: description, features, keywords, compatibility, tags, and media screenshots.",
            Groups = new[] {"Asset Inventory/Search"})]
        public static object GetPackageDetails(PackageDetailsParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            AssetInfo info = Assets.GetPackage(parameters.PackageId);
            if (info == null)
            {
                return Response.Error($"Package with ID {parameters.PackageId} not found.");
            }

            return Response.Success($"Details for package '{info.GetDisplayName()}'.", new
            {
                package = McpResultHelper.ToPackageDetailResult(info)
            });
        }
    }
}
#endif
