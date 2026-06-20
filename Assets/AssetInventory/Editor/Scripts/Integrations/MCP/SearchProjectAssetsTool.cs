#if ASSET_INVENTORY_MCP
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.AI.MCP.Editor.Helpers;
using Unity.AI.MCP.Editor.ToolRegistry;
using UnityEngine;

namespace AssetInventory
{
    public static class SearchProjectAssetsTool
    {
        public class SearchProjectAssetsParams
        {
            [McpDescription("Search query. " +
                "BASICS: Space-separated words are ANDed (all must match). Searches file names and paths. " +
                "EXCLUDE: Prefix a word with '-' to exclude matches containing it, e.g. 'barrel -turret' finds barrels but not turrets. " +
                "NOTE: Expert SQL syntax (=) and tag filters (pt:, ft:) are NOT supported for project search. " +
                "Use specific terms and exclusions to narrow results.", Required = true)]
            public string SearchPhrase { get; set; }

            [McpDescription("File extension without dot, e.g. 'prefab', 'png', 'fbx'.")]
            public string Type { get; set; }

            [McpDescription("Asset category filter.", EnumType = typeof(AI.AssetGroup))]
            public string AssetGroup { get; set; }

            [McpDescription("Filter by texture type, e.g. 'albedo', 'normal', 'specular', 'metal', 'occlusion', 'emission'.")]
            public string ImageType { get; set; }

            [McpDescription("Min image width in pixels.")]
            public int MinWidth { get; set; }

            [McpDescription("Max image width in pixels.")]
            public int MaxWidth { get; set; }

            [McpDescription("Min image height in pixels.")]
            public int MinHeight { get; set; }

            [McpDescription("Max image height in pixels.")]
            public int MaxHeight { get; set; }

            [McpDescription("Min file size in bytes.")]
            public long MinSize { get; set; }

            [McpDescription("Max file size in bytes.")]
            public long MaxSize { get; set; }

            [McpDescription("Min vertex count for 3D models.")]
            public int MinVertexCount { get; set; }

            [McpDescription("Max vertex count for 3D models.")]
            public int MaxVertexCount { get; set; }

            [McpDescription("Results per page (1-100).", Default = 25)]
            public int MaxResults { get; set; } = 25;

            [McpDescription("Page number (1-based).", Default = 1)]
            public int Page { get; set; } = 1;
        }

        [McpTool("AssetInventory_searchProjectAssets",
            "Search files directly in the current Unity project's Assets folder using AssetDatabase. " +
            "Unlike searchAssets (which searches indexed packages), this searches live project files that may not be indexed. " +
            "Useful for finding files already in the project. Supports basic text search with exclusion (-word) but not expert SQL or tag filters. " +
            "Metadata filters (dimensions, size, vertex count) require on-demand extraction and may be slower for large result sets.",
            Groups = new[] {"Asset Inventory/Search"})]
        public static object SearchProjectAssets(SearchProjectAssetsParams parameters)
        {
            ProjectAssetSearch.Options options = ProjectAssetSearch.Options.CreateDefault();
            options.SearchPhrase = parameters.SearchPhrase ?? string.Empty;
            options.MaxResults = Mathf.Clamp(parameters.MaxResults, 1, 100);
            options.CurrentPage = Mathf.Max(1, parameters.Page);

            // Type/extension filter
            if (!string.IsNullOrEmpty(parameters.Type))
            {
                string ext = parameters.Type.TrimStart('.').ToLowerInvariant();
                options.RawSearchType = ext;
            }

            // Asset group filter
            if (!string.IsNullOrEmpty(parameters.AssetGroup) && string.IsNullOrEmpty(parameters.Type))
            {
                if (Enum.TryParse(parameters.AssetGroup, true, out AI.AssetGroup group))
                {
                    options.RawSearchType = group.ToString();
                }
            }

            // Image type filter
            if (!string.IsNullOrEmpty(parameters.ImageType) && options.ImageTypeOptions != null)
            {
                int imgIdx = Array.FindIndex(options.ImageTypeOptions, o =>
                    string.Equals(o, parameters.ImageType, StringComparison.OrdinalIgnoreCase));
                if (imgIdx > 0) options.SelectedImageType = imgIdx;
            }

            // Dimension filters
            if (parameters.MinWidth > 0)
            {
                options.SearchWidth = parameters.MinWidth.ToString();
                options.CheckMaxWidth = false;
            }
            if (parameters.MaxWidth > 0)
            {
                options.SearchWidth = parameters.MaxWidth.ToString();
                options.CheckMaxWidth = true;
            }
            if (parameters.MinHeight > 0)
            {
                options.SearchHeight = parameters.MinHeight.ToString();
                options.CheckMaxHeight = false;
            }
            if (parameters.MaxHeight > 0)
            {
                options.SearchHeight = parameters.MaxHeight.ToString();
                options.CheckMaxHeight = true;
            }

            // Size filters
            if (parameters.MinSize > 0)
            {
                options.SearchSize = parameters.MinSize.ToString();
                options.CheckMaxSize = false;
            }
            if (parameters.MaxSize > 0)
            {
                options.SearchSize = parameters.MaxSize.ToString();
                options.CheckMaxSize = true;
            }

            // Vertex count filters
            if (parameters.MinVertexCount > 0)
            {
                options.SearchVertexCount = parameters.MinVertexCount.ToString();
                options.CheckMaxVertexCount = false;
            }
            if (parameters.MaxVertexCount > 0)
            {
                options.SearchVertexCount = parameters.MaxVertexCount.ToString();
                options.CheckMaxVertexCount = true;
            }

            ProjectAssetSearch.Result result = ProjectAssetSearch.Execute(options);

            List<object> items = result.Files.Select(McpResultHelper.ToAssetFileResult).ToList();

            return Response.Success($"Found {result.ResultCount} project assets (showing page {parameters.Page}).", new
            {
                results = items,
                totalCount = result.ResultCount,
                page = parameters.Page,
                pageSize = parameters.MaxResults,
                totalPages = parameters.MaxResults > 0 ? (result.ResultCount + parameters.MaxResults - 1) / parameters.MaxResults : 1
            });
        }
    }
}
#endif
