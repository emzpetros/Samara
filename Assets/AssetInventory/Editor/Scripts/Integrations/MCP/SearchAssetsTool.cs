#if ASSET_INVENTORY_MCP
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.AI.MCP.Editor.Helpers;
using Unity.AI.MCP.Editor.ToolRegistry;
using UnityEngine;

namespace AssetInventory
{
    public static class SearchAssetsTool
    {
        public enum SrpFilter
        {
            All,
            Auto,
            BIRP,
            URP,
            HDRP
        }

        public enum PriceFilter
        {
            All,
            Free,
            Paid
        }

        public class SearchAssetsParams
        {
            [McpDescription("Search query with advanced syntax support. " +
                "BASICS: Space-separated words are ANDed (all must match). Searches file names, paths, and AI descriptions. " +
                "EXCLUDE: Prefix a word with '-' to exclude matches containing it, e.g. 'barrel -turret' finds barrels but not turrets. " +
                "EXACT PHRASE: Prefix with '~' to match the entire phrase exactly instead of splitting into words, e.g. '~wooden barrel' matches only 'wooden barrel' as a phrase. " +
                "EXPERT SQL: Prefix with '=' to use raw SQL WHERE clause, e.g. '=AssetFile.FileName like \'%barrel%\' AND AssetFile.Size > 1000'. " +
                "INLINE TAG FILTERS: Use 'pt:tagname' for package tags, 'ft:tagname' for file tags (supports comma-separated values and quoted strings with spaces, e.g. pt:'my tag'). " +
                "Use 'withallpt:tag1,tag2' to require ALL package tags, 'withnonept:tag' to exclude package tags. Same for file tags: 'withallft:', 'withnoneft:'. " +
                "TIPS: Combine operators freely, e.g. 'barrel wood -broken -old' finds items matching barrel AND wood but NOT broken and NOT old. " +
                "Use specific terms and exclusions to narrow results when searches return too many items.", Required = true)]
            public string SearchPhrase { get; set; }

            [McpDescription("File extension without dot, e.g. 'prefab', 'png', 'fbx'.")]
            public string Type { get; set; }

            [McpDescription("Asset category filter.", EnumType = typeof(AI.AssetGroup))]
            public string AssetGroup { get; set; }

            [McpDescription("Filter by package tag.")]
            public string PackageTag { get; set; }

            [McpDescription("Filter by file tag.")]
            public string FileTag { get; set; }

            [McpDescription("Filter by publisher name.")]
            public string Publisher { get; set; }

            [McpDescription("Filter by category.")]
            public string Category { get; set; }

            [McpDescription("Render pipeline compatibility filter.", EnumType = typeof(SrpFilter))]
            public string SrpCompatibility { get; set; }

            [McpDescription("Price filter.", EnumType = typeof(PriceFilter))]
            public string PriceOption { get; set; }

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

            [McpDescription("Results per page (1-100).", Default = 25)]
            public int MaxResults { get; set; } = 25;

            [McpDescription("Page number (1-based).", Default = 1)]
            public int Page { get; set; } = 1;
        }

        [McpTool("AssetInventory_searchAssets",
            "Search individual files (prefabs, models, textures, materials, audio, etc.) across all indexed packages. " +
            "Primary tool for finding assets. Workflow: searchAssets → importAsset → addToScene. " +
            "For whole packages (scripting libraries, editor tools), use searchPackages instead. " +
            "The SearchPhrase supports powerful query syntax: use '-word' to exclude unwanted results (e.g. 'barrel -turret'), " +
            "'~phrase' for exact phrase matching, multiple words are ANDed. " +
            "Use the structured filter parameters (Type, AssetGroup, Publisher, etc.) in combination with search phrase operators " +
            "to precisely narrow down results. If too many results are returned, add exclusion terms or more specific filters.",
            Groups = new[] {"Asset Inventory/Search"})]
        public static object SearchAssets(SearchAssetsParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            AssetSearch.Options options = AssetSearch.Options.CreateDefault();
            options.SearchPhrase = parameters.SearchPhrase ?? string.Empty;
            options.MaxResults = Mathf.Clamp(parameters.MaxResults, 1, 100);
            options.CurrentPage = Mathf.Max(1, parameters.Page);

            // Type/extension filter
            if (!string.IsNullOrEmpty(parameters.Type))
            {
                string ext = parameters.Type.TrimStart('.').ToLowerInvariant();
                string[] allTypes = Assets.LoadTypes();
                int typeIdx = Array.FindIndex(allTypes, t => t.Split('/').LastOrDefault()?.ToLowerInvariant() == ext);
                if (typeIdx >= 0) options.RawSearchType = allTypes[typeIdx];
            }

            // Asset group filter — translate to RawSearchType so the query filters by file type
            if (!string.IsNullOrEmpty(parameters.AssetGroup) && string.IsNullOrEmpty(parameters.Type))
            {
                if (Enum.TryParse(parameters.AssetGroup, true, out AI.AssetGroup group))
                {
                    options.RawSearchType = group.ToString();
                }
            }

            // Tag filters
            if (!string.IsNullOrEmpty(parameters.PackageTag))
            {
                int tagIdx = Array.FindIndex(options.TagNames, t => t.IndexOf(parameters.PackageTag, StringComparison.OrdinalIgnoreCase) >= 0);
                if (tagIdx > 0) options.SelectedPackageTag = tagIdx;
            }
            if (!string.IsNullOrEmpty(parameters.FileTag))
            {
                int tagIdx = Array.FindIndex(options.TagNames, t => t.IndexOf(parameters.FileTag, StringComparison.OrdinalIgnoreCase) >= 0);
                if (tagIdx > 0) options.SelectedFileTag = tagIdx;
            }

            // Publisher filter
            if (!string.IsNullOrEmpty(parameters.Publisher))
            {
                int pubIdx = Array.FindIndex(options.PublisherNames, p =>
                {
                    string name = p.Split('/').LastOrDefault() ?? p;
                    return name.IndexOf(parameters.Publisher, StringComparison.OrdinalIgnoreCase) >= 0;
                });
                if (pubIdx > 0) options.SelectedPublisher = pubIdx;
            }

            // Category filter
            if (!string.IsNullOrEmpty(parameters.Category))
            {
                int catIdx = Array.FindIndex(options.CategoryNames, c => c.IndexOf(parameters.Category, StringComparison.OrdinalIgnoreCase) >= 0);
                if (catIdx > 0) options.SelectedCategory = catIdx;
            }

            // SRP filter
            if (!string.IsNullOrEmpty(parameters.SrpCompatibility) && Enum.TryParse(parameters.SrpCompatibility, true, out SrpFilter srp))
            {
                switch (srp)
                {
                    case SrpFilter.Auto: options.SelectedPackageSRPs = 1; break;
                    case SrpFilter.BIRP: options.SelectedPackageSRPs = 3; break;
                    case SrpFilter.URP: options.SelectedPackageSRPs = 4; break;
                    case SrpFilter.HDRP: options.SelectedPackageSRPs = 5; break;
                }
            }

            // Price filter
            if (!string.IsNullOrEmpty(parameters.PriceOption) && Enum.TryParse(parameters.PriceOption, true, out PriceFilter price))
            {
                switch (price)
                {
                    case PriceFilter.Free: options.SelectedPriceOption = 1; break;
                    case PriceFilter.Paid: options.SelectedPriceOption = 2; break;
                }
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

            AssetSearch.Result result = AssetSearch.Execute(options);

            if (!string.IsNullOrEmpty(result.Error))
            {
                return Response.Error(result.Error);
            }

            List<object> items = result.Files.Select(McpResultHelper.ToAssetFileResult).ToList();

            return Response.Success($"Found {result.ResultCount} assets (showing page {parameters.Page}).", new
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
