#if ASSET_INVENTORY_MCP
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.AI.MCP.Editor.Helpers;
using Unity.AI.MCP.Editor.ToolRegistry;
using UnityEngine;

namespace AssetInventory
{
    public static class SearchPackagesTool
    {
        public enum SourceFilter
        {
            All,
            AssetStore,
            CustomPackage,
            Directory,
            Registry,
            Archive,
            AssetManager
        }

        public enum DeprecationFilter
        {
            All,
            NotDeprecated,
            Deprecated
        }

        public class SearchPackagesParams
        {
            [McpDescription("Search query with advanced syntax support. " +
                "BASICS: Space-separated words are ANDed (all must match). " +
                "EXCLUDE: Prefix a word with '-' to exclude matches, e.g. 'shader -legacy' finds shaders but excludes legacy ones. " +
                "EXACT PHRASE: Prefix with '~' to match the entire phrase exactly, e.g. '~particle system'. " +
                "TIPS: Combine operators freely, e.g. 'terrain tool -demo -sample' narrows results effectively. " +
                "Use exclusions to filter out unwanted results when searches return too many items.")]
            public string SearchPhrase { get; set; }

            [McpDescription("Filter by package source.", EnumType = typeof(SourceFilter))]
            public string Source { get; set; }

            [McpDescription("Render pipeline compatibility filter.", EnumType = typeof(SearchAssetsTool.SrpFilter))]
            public string SrpCompatibility { get; set; }

            [McpDescription("Filter by status/maintenance condition.", EnumType = typeof(PackageSearch.MaintenanceOption))]
            public string Maintenance { get; set; }

            [McpDescription("Filter by deprecation status.", EnumType = typeof(DeprecationFilter))]
            public string Deprecation { get; set; }

            [McpDescription("Price filter.", EnumType = typeof(SearchAssetsTool.PriceFilter))]
            public string PriceOption { get; set; }

            [McpDescription("Filter by package tag.")]
            public string Tag { get; set; }

            [McpDescription("Filter by publisher name.")]
            public string Publisher { get; set; }

            [McpDescription("Filter by category.")]
            public string Category { get; set; }

            [McpDescription("Only packages with files used in current project.")]
            public bool OnlyInProject { get; set; }

            [McpDescription("Also search in package descriptions.")]
            public bool SearchDescription { get; set; }

            [McpDescription("Min package size in MB.")]
            public float MinSizeMB { get; set; }

            [McpDescription("Max package size in MB.")]
            public float MaxSizeMB { get; set; }

            [McpDescription("Packages updated before this date (ISO 8601).")]
            public string UpdatedBefore { get; set; }

            [McpDescription("Packages updated after this date (ISO 8601).")]
            public string UpdatedAfter { get; set; }

            [McpDescription("Packages purchased before this date (ISO 8601).")]
            public string PurchasedBefore { get; set; }

            [McpDescription("Packages purchased after this date (ISO 8601).")]
            public string PurchasedAfter { get; set; }

            [McpDescription("Results per page (1-100).", Default = 25)]
            public int MaxResults { get; set; } = 25;

            [McpDescription("Page number (1-based).", Default = 1)]
            public int Page { get; set; } = 1;
        }

        [McpTool("AssetInventory_searchPackages",
            "Search packages (asset collections) by name, source, or metadata. " +
            "Use for scripting libraries, editor tools, or when you need a whole package. For individual files (models, textures, prefabs), use searchAssets instead. " +
            "The SearchPhrase supports query operators: '-word' excludes results (e.g. 'shader -legacy'), '~phrase' for exact matching, multiple words are ANDed. " +
            "Combine with structured filters (Source, Publisher, Category, etc.) to narrow results. If too many results, add exclusion terms.",
            Groups = new[] {"Asset Inventory/Search"})]        public static object SearchPackages(SearchPackagesParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            PackageSearch.Options options = PackageSearch.Options.CreateDefault();
            options.SearchPhrase = parameters.SearchPhrase ?? string.Empty;

            // Source filter
            if (!string.IsNullOrEmpty(parameters.Source) && Enum.TryParse(parameters.Source, true, out SourceFilter source))
            {
                switch (source)
                {
                    case SourceFilter.AssetStore: options.SelectedPackageListing = 1; break;
                    case SourceFilter.Registry: options.SelectedPackageListing = 2; break;
                    case SourceFilter.CustomPackage: options.SelectedPackageListing = 3; break;
                    case SourceFilter.Directory: options.SelectedPackageListing = 4; break;
                    case SourceFilter.Archive: options.SelectedPackageListing = 5; break;
                    case SourceFilter.AssetManager: options.SelectedPackageListing = 6; break;
                }
            }

            // SRP filter
            if (!string.IsNullOrEmpty(parameters.SrpCompatibility) && Enum.TryParse(parameters.SrpCompatibility, true, out SearchAssetsTool.SrpFilter srp))
            {
                switch (srp)
                {
                    case SearchAssetsTool.SrpFilter.BIRP: options.SelectedSRPs = 2; break;
                    case SearchAssetsTool.SrpFilter.URP: options.SelectedSRPs = 3; break;
                    case SearchAssetsTool.SrpFilter.HDRP: options.SelectedSRPs = 4; break;
                }
            }

            // Maintenance filter
            if (!string.IsNullOrEmpty(parameters.Maintenance) && Enum.TryParse(parameters.Maintenance, true, out PackageSearch.MaintenanceOption maint))
            {
                options.SelectedMaintenance = maint;
            }

            // Deprecation filter
            if (!string.IsNullOrEmpty(parameters.Deprecation) && Enum.TryParse(parameters.Deprecation, true, out DeprecationFilter dep))
            {
                switch (dep)
                {
                    case DeprecationFilter.NotDeprecated: options.SelectedDeprecation = 1; break;
                    case DeprecationFilter.Deprecated: options.SelectedDeprecation = 2; break;
                }
            }

            // Price filter
            if (!string.IsNullOrEmpty(parameters.PriceOption) && Enum.TryParse(parameters.PriceOption, true, out SearchAssetsTool.PriceFilter price))
            {
                switch (price)
                {
                    case SearchAssetsTool.PriceFilter.Free: options.SelectedPriceOption = 1; break;
                    case SearchAssetsTool.PriceFilter.Paid: options.SelectedPriceOption = 2; break;
                }
            }

            // Tag filter
            if (!string.IsNullOrEmpty(parameters.Tag))
            {
                int tagIdx = Array.FindIndex(options.TagNames, t => t.IndexOf(parameters.Tag, StringComparison.OrdinalIgnoreCase) >= 0);
                if (tagIdx > 0) options.SelectedPackageTag = tagIdx;
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

            options.OnlyInProject = parameters.OnlyInProject;
            options.SearchDescription = parameters.SearchDescription;
            options.MaxResults = Mathf.Clamp(parameters.MaxResults, 1, 100);
            options.CurrentPage = Mathf.Max(1, parameters.Page);

            // Size filter
            if (parameters.MinSizeMB > 0)
            {
                options.SelectedPackageSizeOption = 1; // greater than
                options.PackageSizeMB = parameters.MinSizeMB;
            }
            if (parameters.MaxSizeMB > 0)
            {
                options.SelectedPackageSizeOption = 2; // less than
                options.PackageSizeMB = parameters.MaxSizeMB;
            }

            // Date filters
            if (!string.IsNullOrEmpty(parameters.UpdatedBefore) && DateTime.TryParse(parameters.UpdatedBefore, out DateTime beforeDate))
            {
                options.SelectedUpdateDateOption = 2; // before
                options.UpdateBeforeDate = beforeDate;
            }
            if (!string.IsNullOrEmpty(parameters.UpdatedAfter) && DateTime.TryParse(parameters.UpdatedAfter, out DateTime afterDate))
            {
                options.SelectedUpdateDateOption = 1; // after
                options.UpdateAfterDate = afterDate;
            }

            // Purchase date filters
            if (!string.IsNullOrEmpty(parameters.PurchasedBefore) && DateTime.TryParse(parameters.PurchasedBefore, out DateTime purchaseBeforeDate))
            {
                options.SelectedPurchaseDateOption = 6; // before
                options.PurchaseBeforeDate = purchaseBeforeDate;
            }
            if (!string.IsNullOrEmpty(parameters.PurchasedAfter) && DateTime.TryParse(parameters.PurchasedAfter, out DateTime purchaseAfterDate))
            {
                options.SelectedPurchaseDateOption = 7; // after
                options.PurchaseAfterDate = purchaseAfterDate;
            }

            PackageSearch.Result result = PackageSearch.Execute(options);
            if (!string.IsNullOrEmpty(result.Error))
            {
                return Response.Error(result.Error);
            }

            int maxResults = Mathf.Clamp(parameters.MaxResults, 1, 100);
            int page = Mathf.Max(1, parameters.Page);
            List<object> items = result.Packages.Select(McpResultHelper.ToPackageResult).ToList();

            return Response.Success($"Found {result.ResultCount} packages (showing page {page}).", new
            {
                results = items,
                totalCount = result.ResultCount,
                page,
                pageSize = maxResults,
                totalPages = maxResults > 0 ? (result.ResultCount + maxResults - 1) / maxResults : 1
            });
        }
    }
}
#endif
