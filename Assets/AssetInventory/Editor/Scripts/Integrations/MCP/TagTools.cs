#if ASSET_INVENTORY_MCP
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.AI.MCP.Editor.Helpers;
using Unity.AI.MCP.Editor.ToolRegistry;

namespace AssetInventory
{
    public static class TagTools
    {
        public enum TagAction
        {
            Add,
            Remove
        }

        #region List Tags

        public class ListTagsParams
        {
            [McpDescription("Filter tags by name.")]
            public string SearchPhrase { get; set; }
        }

        [McpTool("AssetInventory_listTags",
            "List all tags. Tags can be used as filters in searchAssets and searchPackages.",
            Groups = new[] {"Asset Inventory/Tags"})]
        public static object ListTags(ListTagsParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            List<Tag> tags = DBAdapter.DB.Table<Tag>().ToList();

            if (!string.IsNullOrEmpty(parameters.SearchPhrase))
            {
                tags = tags.Where(t => t.Name.IndexOf(parameters.SearchPhrase, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }

            return Response.Success($"Found {tags.Count} tags.", new
            {
                tags = tags.Select(t => new
                {
                    id = t.Id,
                    name = t.Name,
                    color = t.Color,
                    parentId = t.ParentId
                }).ToArray()
            });
        }

        #endregion

        #region Tag Package

        public class TagPackageParams
        {
            [McpDescription("Package ID (the 'id' field from searchPackages results).", Required = true)]
            public int PackageId { get; set; }

            [McpDescription("Tag name. Created automatically if new.", Required = true)]
            public string TagName { get; set; }

            [McpDescription("Add or Remove.", Required = true, EnumType = typeof(TagAction))]
            public string Action { get; set; }
        }

        [McpTool("AssetInventory_tagPackage",
            "Add or remove a tag on a package.",
            Groups = new[] {"Asset Inventory/Tags"})]
        public static object TagPackage(TagPackageParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            if (string.IsNullOrEmpty(parameters.TagName))
            {
                return Response.Error("TagName is required.");
            }

            if (!Enum.TryParse(parameters.Action, true, out TagAction action))
            {
                return Response.Error("Action must be 'Add' or 'Remove'.");
            }

            AssetInfo info = Assets.GetPackage(parameters.PackageId);
            if (info == null)
            {
                return Response.Error($"Package with ID {parameters.PackageId} not found.");
            }

            if (action == TagAction.Add)
            {
                bool added = Tagging.AddAssignment(info, parameters.TagName, TagAssignment.Target.Package, true);
                return Response.Success(added ? $"Tag '{parameters.TagName}' added to package '{info.GetDisplayName()}'." : $"Tag '{parameters.TagName}' was already assigned to package '{info.GetDisplayName()}'.");
            }

            // Remove
            List<TagInfo> packageTags = Tagging.GetPackageTags(parameters.PackageId);
            TagInfo tagInfo = packageTags?.FirstOrDefault(t => t.Name.Equals(parameters.TagName, StringComparison.OrdinalIgnoreCase));
            if (tagInfo == null)
            {
                return Response.Error($"Tag '{parameters.TagName}' is not assigned to package '{info.GetDisplayName()}'.");
            }

            Tagging.RemoveAssignment(info, tagInfo, true, true);
            return Response.Success($"Tag '{parameters.TagName}' removed from package '{info.GetDisplayName()}'.");
        }

        #endregion

        #region Tag Asset File

        public class TagAssetFileParams
        {
            [McpDescription("File ID (the 'id' field from searchAssets or listPackageFiles results).", Required = true)]
            public int FileId { get; set; }

            [McpDescription("Tag name. Created automatically if new.", Required = true)]
            public string TagName { get; set; }

            [McpDescription("Add or Remove.", Required = true, EnumType = typeof(TagAction))]
            public string Action { get; set; }
        }

        [McpTool("AssetInventory_tagAssetFile",
            "Add or remove a tag on an individual file.",
            Groups = new[] {"Asset Inventory/Tags"})]
        public static object TagAssetFile(TagAssetFileParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            if (string.IsNullOrEmpty(parameters.TagName))
            {
                return Response.Error("TagName is required.");
            }

            if (!Enum.TryParse(parameters.Action, true, out TagAction action))
            {
                return Response.Error("Action must be 'Add' or 'Remove'.");
            }

            AssetFile file = DBAdapter.DB.Find<AssetFile>(parameters.FileId);
            if (file == null)
            {
                return Response.Error($"Asset file with ID {parameters.FileId} not found.");
            }

            // Build a minimal AssetInfo for the tagging API
            Asset asset = DBAdapter.DB.Find<Asset>(file.AssetId);
            if (asset == null)
            {
                return Response.Error($"Parent package for file ID {parameters.FileId} not found.");
            }

            AssetInfo info = new AssetInfo().CopyFrom(asset);
            info.Id = file.Id;
            info.AssetId = file.AssetId;

            if (action == TagAction.Add)
            {
                bool added = Tagging.AddAssignment(info, parameters.TagName, TagAssignment.Target.Asset, true);
                return Response.Success(added ? $"Tag '{parameters.TagName}' added to file '{file.FileName}'." : $"Tag '{parameters.TagName}' was already assigned to file '{file.FileName}'.");
            }

            // Remove
            List<TagInfo> fileTags = Tagging.GetAssetTags(parameters.FileId);
            TagInfo tagInfo = fileTags?.FirstOrDefault(t => t.Name.Equals(parameters.TagName, StringComparison.OrdinalIgnoreCase));
            if (tagInfo == null)
            {
                return Response.Error($"Tag '{parameters.TagName}' is not assigned to file '{file.FileName}'.");
            }

            Tagging.RemoveAssignment(info, tagInfo, true, true);
            return Response.Success($"Tag '{parameters.TagName}' removed from file '{file.FileName}'.");
        }

        #endregion
    }
}
#endif
