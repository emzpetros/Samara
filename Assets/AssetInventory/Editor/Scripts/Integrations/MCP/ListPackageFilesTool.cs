#if ASSET_INVENTORY_MCP
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.AI.MCP.Editor.Helpers;
using Unity.AI.MCP.Editor.ToolRegistry;
using UnityEngine;

namespace AssetInventory
{
    public static class ListPackageFilesTool
    {
        public class ListPackageFilesParams
        {
            [McpDescription("Package ID to list files for (the 'id' field from searchPackages results).", Required = true)]
            public int PackageId { get; set; }

            [McpDescription("File extension without dot, e.g. 'prefab', 'png'.")]
            public string Type { get; set; }

            [McpDescription("Asset category filter.", EnumType = typeof(AI.AssetGroup))]
            public string AssetGroup { get; set; }

            [McpDescription("Search within file names and paths. Supports advanced syntax: " +
                "space-separated words are ANDed, '-word' excludes matches (e.g. 'barrel -turret'), " +
                "'~phrase' for exact phrase matching, '=' prefix for raw SQL WHERE clause.")]
            public string SearchPhrase { get; set; }

            [McpDescription("Results per page (1-200).", Default = 50)]
            public int MaxResults { get; set; } = 50;

            [McpDescription("Page number (1-based).", Default = 1)]
            public int Page { get; set; } = 1;
        }

        [McpTool("AssetInventory_listPackageFiles",
            "List files in a specific package with optional type/search filters. Use after searchPackages to browse contents before importing individual files.",
            Groups = new[] {"Asset Inventory/Search"})]
        public static object ListPackageFiles(ListPackageFilesParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            AssetSearch.Options options = AssetSearch.Options.CreateDefault();
            options.SelectedAssetId = parameters.PackageId;
            options.SearchPhrase = parameters.SearchPhrase ?? string.Empty;
            options.MaxResults = Mathf.Clamp(parameters.MaxResults, 1, 200);
            options.CurrentPage = Mathf.Max(1, parameters.Page);

            if (!string.IsNullOrEmpty(parameters.Type))
            {
                string ext = parameters.Type.TrimStart('.').ToLowerInvariant();
                string[] allTypes = Assets.LoadTypes();
                int typeIdx = Array.FindIndex(allTypes, t => t.Split('/').LastOrDefault()?.ToLowerInvariant() == ext);
                if (typeIdx >= 0) options.RawSearchType = allTypes[typeIdx];
            }
            else if (!string.IsNullOrEmpty(parameters.AssetGroup) && Enum.TryParse(parameters.AssetGroup, true, out AI.AssetGroup group))
            {
                options.RawSearchType = group.ToString();
            }

            AssetSearch.Result result = AssetSearch.Execute(options);

            List<object> items = result.Files.Select(McpResultHelper.ToAssetFileResult).ToList();

            return Response.Success($"Found {result.ResultCount} files in package (showing page {options.CurrentPage}).", new
            {
                packageId = parameters.PackageId,
                results = items,
                totalCount = result.ResultCount,
                page = options.CurrentPage,
                pageSize = options.MaxResults,
                totalPages = options.MaxResults > 0 ? (result.ResultCount + options.MaxResults - 1) / options.MaxResults : 1
            });
        }
    }
}
#endif
