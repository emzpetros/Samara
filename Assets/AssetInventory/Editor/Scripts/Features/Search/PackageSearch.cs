using ImpossibleRobert.Common;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AssetInventory
{
    public static class PackageSearch
    {
        public enum MaintenanceOption
        {
            All = 0,
            UpdateAvailable = 2,
            OutdatedInUnityCache = 3,
            DisabledByUnity = 4,
            CustomAssetStoreLink = 5,
            Indexed = 6,
            NotIndexed = 7,
            CustomRegistry = 8,
            Downloaded = 9,
            Downloading = 10,
            NotDownloaded = 11,
            Duplicate = 12,
            MarkedForBackup = 13,
            NotMarkedForBackup = 14,
            MarkedForAI = 15,
            NotMarkedforAI = 16,
            Deleted = 17,
            Excluded = 18,
            WithSubPackages = 19,
            IncompatiblePackages = 20,
            FixableIncompatibilities = 21,
            UnfixableIncompatibilities = 22
        }

        public class Options
        {
            public string SearchPhrase = string.Empty;
            public int SelectedPackageListing;
            public int SelectedSRPs;
            public int SelectedDeprecation;
            public MaintenanceOption SelectedMaintenance = MaintenanceOption.All;
            public bool OnlyInProject = false;
            public bool SearchDescription = false;
            public bool SearchGroupNames = false;
            public int CurrentGrouping = 0;
            public int SelectedPriceOption;
            public float SearchPrice;
            public int SelectedPackageTag;
            public int SelectedPublisher;
            public int SelectedCategory;
            public int SelectedUpdateDateOption;
            public DateTime? UpdateBeforeDate;
            public DateTime? UpdateAfterDate;
            public int SelectedPurchaseDateOption;
            public DateTime? PurchaseBeforeDate;
            public DateTime? PurchaseAfterDate;
            public int SelectedPackageSizeOption;
            public float PackageSizeMB;
            public int SelectedUnityVersionOption;
            public int CurrentPage = 1;
            public int MaxResults = 0;

            // data references as input
            public Dictionary<int, AssetInfo> UsedPackages = null; // for OnlyInProject filtering
            public List<AssetInfo> AllAssets = new List<AssetInfo>(); // full asset list for reference
            public string[] TagNames = Array.Empty<string>();
            public List<Tag> Tags = new List<Tag>();
            public string[] PublisherNames = Array.Empty<string>();
            public string[] CategoryNames = Array.Empty<string>();

            /// <summary>
            /// Creates a default Options object with all data loaded from the database.
            /// </summary>
            public static Options CreateDefault()
            {
                string initError = EnsureInitialized();
                if (!string.IsNullOrEmpty(initError)) return new Options();

                List<AssetInfo> allAssets = Assets.Load().ToList();
                List<Tag> tags = DBAdapter.DB.Table<Tag>().ToList();
                string[] tagNames = Assets.ExtractTagNames(tags);
                string[] publisherNames = Assets.ExtractPublisherNames(allAssets);
                string[] categoryNames = Assets.ExtractCategoryNames(allAssets);

                Options options = new Options
                {
                    AllAssets = allAssets,
                    Tags = tags,
                    TagNames = tagNames,
                    PublisherNames = publisherNames,
                    CategoryNames = categoryNames
                };

                return options;
            }

            /// <summary>
            /// Creates an Options object from a SavedPackageSearch.
            /// </summary>
            public static Options FromSavedSearch(SavedPackageSearch savedSearch)
            {
                Options options = CreateDefault();

                // Map saved search properties to options
                options.SearchPhrase = savedSearch.SearchPhrase ?? string.Empty;
                options.SelectedPackageListing = savedSearch.PackagesListing;
                options.SelectedSRPs = savedSearch.SRPs;
                options.SelectedDeprecation = savedSearch.Deprecation;
                options.SelectedMaintenance = (MaintenanceOption)savedSearch.Maintenance;
                options.SelectedPriceOption = savedSearch.PriceOption;
                options.SearchPrice = savedSearch.Price;
                options.SelectedPackageSizeOption = savedSearch.PackageSizeOption;
                options.PackageSizeMB = savedSearch.PackageSizeMB;
                options.SelectedUpdateDateOption = savedSearch.UpdateDateOption;
                options.SelectedPurchaseDateOption = savedSearch.PurchaseDateOption;
                options.SelectedUnityVersionOption = savedSearch.UnityVersionOption;

                // Parse dates
                if (!string.IsNullOrEmpty(savedSearch.UpdateBeforeDate) &&
                    DateTime.TryParse(savedSearch.UpdateBeforeDate, out DateTime beforeDate))
                {
                    options.UpdateBeforeDate = beforeDate;
                }

                if (!string.IsNullOrEmpty(savedSearch.UpdateAfterDate) &&
                    DateTime.TryParse(savedSearch.UpdateAfterDate, out DateTime afterDate))
                {
                    options.UpdateAfterDate = afterDate;
                }

                if (!string.IsNullOrEmpty(savedSearch.PurchaseBeforeDate) &&
                    DateTime.TryParse(savedSearch.PurchaseBeforeDate, out DateTime purchaseBeforeDate))
                {
                    options.PurchaseBeforeDate = purchaseBeforeDate;
                }

                if (!string.IsNullOrEmpty(savedSearch.PurchaseAfterDate) &&
                    DateTime.TryParse(savedSearch.PurchaseAfterDate, out DateTime purchaseAfterDate))
                {
                    options.PurchaseAfterDate = purchaseAfterDate;
                }

                // Map saved search values to indices
                options.SelectedPackageTag = FindTagIndex(options.TagNames, savedSearch.PackageTag);
                options.SelectedPublisher = FindPublisherIndex(options.PublisherNames, savedSearch.Publisher);
                options.SelectedCategory = FindCategoryIndex(options.CategoryNames, savedSearch.Category);

                return options;
            }

            private static int FindTagIndex(string[] tagNames, string tagValue)
            {
                if (string.IsNullOrEmpty(tagValue)) return 0;

                for (int i = 0; i < tagNames.Length; i++)
                {
                    if (tagNames[i].Contains($"({tagValue})") || tagNames[i] == tagValue)
                    {
                        return i;
                    }
                }
                return 0;
            }

            private static int FindPublisherIndex(string[] publisherNames, string publisherValue)
            {
                if (string.IsNullOrEmpty(publisherValue)) return 0;

                for (int i = 0; i < publisherNames.Length; i++)
                {
                    if (publisherNames[i].Contains($"({publisherValue})") || publisherNames[i] == publisherValue)
                    {
                        return i;
                    }
                }
                return 0;
            }

            private static int FindCategoryIndex(string[] categoryNames, string categoryValue)
            {
                if (string.IsNullOrEmpty(categoryValue)) return 0;

                for (int i = 0; i < categoryNames.Length; i++)
                {
                    if (categoryNames[i] == categoryValue)
                    {
                        return i;
                    }
                }
                return 0;
            }
        }

        public class Result
        {
            public List<AssetInfo> Packages = new List<AssetInfo>();
            public int ResultCount;
            public string Error;
        }

        public static Result Execute(Options opt)
        {
            Result result = new Result();

            if (opt == null)
            {
                result.Error = "Search options are missing.";
                return result;
            }

            // Start with all assets
            IEnumerable<AssetInfo> filteredAssets = opt.AllAssets;

            // 1. Parent/sub-package filtering (based on maintenance option)
            if (opt.SelectedMaintenance != MaintenanceOption.Excluded)
            {
                filteredAssets = filteredAssets.Where(a => a.ParentId == 0);
            }

            // 2. SRP filtering (BIRP/URP/HDRP compatibility)
            switch (opt.SelectedSRPs)
            {
                case 2:
                    filteredAssets = filteredAssets.Where(a => a.BIRPCompatible);
                    break;
                case 3:
                    filteredAssets = filteredAssets.Where(a => a.URPCompatible);
                    break;
                case 4:
                    filteredAssets = filteredAssets.Where(a => a.HDRPCompatible);
                    break;
            }

            // 3. Deprecation and China Store filtering
            switch (opt.SelectedDeprecation)
            {
                case 2:
                    filteredAssets = filteredAssets.Where(a => !a.IsDeprecated && !a.IsAbandoned);
                    break;
                case 3:
                    filteredAssets = filteredAssets.Where(a => a.IsDeprecated || a.IsAbandoned);
                    break;
                case 5:
                    filteredAssets = filteredAssets.Where(a => !ChinaBasedAssets.IsAffected(a));
                    break;
                case 6:
                    filteredAssets = filteredAssets.Where(a => ChinaBasedAssets.IsAffected(a));
                    break;
            }

            // 4. Maintenance options
            switch (opt.SelectedMaintenance)
            {
                case MaintenanceOption.UpdateAvailable:
                    filteredAssets = filteredAssets.Where(a => a.IsUpdateAvailable(opt.AllAssets, false) || a.WasOutdated);
                    break;

                case MaintenanceOption.OutdatedInUnityCache:
                    filteredAssets = filteredAssets.Where(a => a.CurrentSubState == Asset.SubState.Outdated);
                    break;

                case MaintenanceOption.DisabledByUnity:
                    filteredAssets = filteredAssets.Where(a => (a.AssetSource == Asset.Source.AssetStorePackage || (a.AssetSource == Asset.Source.RegistryPackage && a.ForeignId > 0)) && a.OfficialState == Asset.OfficialStateType.Disabled);
                    break;

                case MaintenanceOption.CustomAssetStoreLink:
                    filteredAssets = filteredAssets.Where(a => a.AssetSource == Asset.Source.CustomPackage && a.ForeignId > 0);
                    break;

                case MaintenanceOption.Indexed:
                    filteredAssets = filteredAssets.Where(a => a.FileCount > 0);
                    break;

                case MaintenanceOption.NotIndexed:
                    filteredAssets = filteredAssets.Where(a => a.FileCount == 0);
                    break;

                case MaintenanceOption.CustomRegistry:
                    filteredAssets = filteredAssets.Where(a => !string.IsNullOrEmpty(a.Registry) && a.Registry != "Unity");
                    break;

                case MaintenanceOption.Downloaded:
                    filteredAssets = filteredAssets.Where(a => a.AssetSource == Asset.Source.AssetStorePackage && a.IsDownloaded);
                    break;

                case MaintenanceOption.Downloading:
                    filteredAssets = filteredAssets.Where(a => a.IsDownloading());
                    break;

                case MaintenanceOption.NotDownloaded:
                    filteredAssets = filteredAssets.Where(a => a.AssetSource == Asset.Source.AssetStorePackage && !a.IsDownloaded);
                    break;

                case MaintenanceOption.Duplicate:
                    // Find duplicates by ForeignId
                    HashSet<int> duplicates = filteredAssets
                        .Where(a => a.ForeignId > 0)
                        .GroupBy(a => a.ForeignId)
                        .Where(g => g.Count() > 1)
                        .Select(g => g.Key)
                        .ToHashSet();

                    // Find duplicates by Location
                    HashSet<string> locationDuplicates = filteredAssets
                        .Where(a => !string.IsNullOrEmpty(a.Location))
                        .GroupBy(a => a.Location)
                        .Where(g => g.Count() > 1)
                        .Select(g => g.Key)
                        .ToHashSet();

                    // Filter to include assets with duplicate ForeignId OR duplicate Location
                    filteredAssets = filteredAssets.Where(a =>
                        duplicates.Contains(a.ForeignId) ||
                        locationDuplicates.Contains(a.Location));
                    break;

                case MaintenanceOption.MarkedForBackup:
                    filteredAssets = filteredAssets.Where(a => a.Backup);
                    break;

                case MaintenanceOption.NotMarkedForBackup:
                    filteredAssets = filteredAssets.Where(a => !a.Backup);
                    break;

                case MaintenanceOption.MarkedForAI:
                    filteredAssets = filteredAssets.Where(a => a.UseAI);
                    break;

                case MaintenanceOption.NotMarkedforAI:
                    filteredAssets = filteredAssets.Where(a => !a.UseAI);
                    break;

                case MaintenanceOption.Deleted:
                    filteredAssets = filteredAssets.Where(a => a.AssetSource != Asset.Source.AssetStorePackage && a.AssetSource != Asset.Source.RegistryPackage && !a.IsDownloaded);
                    break;

                case MaintenanceOption.Excluded:
                    filteredAssets = filteredAssets.Where(a => a.Exclude);
                    break;

                case MaintenanceOption.WithSubPackages:
                    filteredAssets = opt.AllAssets.Where(a => a.ParentId > 0);
                    break;

                case MaintenanceOption.IncompatiblePackages:
                    filteredAssets = filteredAssets.Where(a => (a.AssetSource == Asset.Source.AssetStorePackage || a.AssetSource == Asset.Source.CustomPackage) && !a.IsDownloadedCompatible);
                    break;

                case MaintenanceOption.FixableIncompatibilities:
                    filteredAssets = filteredAssets.Where(a => (a.AssetSource == Asset.Source.AssetStorePackage || a.AssetSource == Asset.Source.CustomPackage) && !a.IsDownloadedCompatible && a.IsCurrentUnitySupported());
                    break;

                case MaintenanceOption.UnfixableIncompatibilities:
                    filteredAssets = filteredAssets.Where(a => (a.AssetSource == Asset.Source.AssetStorePackage || a.AssetSource == Asset.Source.CustomPackage) && !a.IsDownloadedCompatible && !a.IsCurrentUnitySupported());
                    break;
            }

            if (opt.SelectedMaintenance != MaintenanceOption.Excluded)
            {
                filteredAssets = filteredAssets.Where(a => !a.Exclude);
            }

            // 5. Package listing type (Asset Store, Registry, Custom, etc.)
            switch (opt.SelectedPackageListing)
            {
                case 1:
                    filteredAssets = filteredAssets.Where(a => a.AssetSource != Asset.Source.RegistryPackage);
                    break;

                case 2:
                    filteredAssets = filteredAssets.Where(a => a.AssetSource == Asset.Source.AssetStorePackage || (a.AssetSource == Asset.Source.RegistryPackage && a.ForeignId > 0));
                    break;

                case 3:
                    filteredAssets = filteredAssets.Where(a => a.AssetSource == Asset.Source.RegistryPackage);
                    break;

                case 4:
                    filteredAssets = filteredAssets.Where(a => a.AssetSource == Asset.Source.CustomPackage);
                    break;

                case 5:
                    filteredAssets = filteredAssets.Where(a => a.AssetSource == Asset.Source.Directory);
                    break;

                case 6:
                    filteredAssets = filteredAssets.Where(a => a.AssetSource == Asset.Source.Archive);
                    break;

                case 7:
                    filteredAssets = filteredAssets.Where(a => a.AssetSource == Asset.Source.AssetManager);
                    break;
            }

            // 6. Price filtering
            if (opt.SelectedPriceOption > 0)
            {
                switch (opt.SelectedPriceOption)
                {
                    case 1: // -Free-
                        filteredAssets = filteredAssets.Where(a => GetPrice(a, AI.Config.currency) == 0);
                        break;

                    case 2: // -Paid-
                        filteredAssets = filteredAssets.Where(a => GetPrice(a, AI.Config.currency) > 0);
                        break;

                    case 4: // < smaller than
                        if (opt.SearchPrice > 0)
                        {
                            filteredAssets = filteredAssets.Where(a => GetPrice(a, AI.Config.currency) < opt.SearchPrice);
                        }
                        break;

                    case 5: // > greater than
                        if (opt.SearchPrice > 0)
                        {
                            filteredAssets = filteredAssets.Where(a => GetPrice(a, AI.Config.currency) > opt.SearchPrice);
                        }
                        break;
                }
            }

            // 7. Package tag filtering
            if (opt.SelectedPackageTag > 0 && opt.TagNames.Length > opt.SelectedPackageTag)
            {
                if (opt.SelectedPackageTag == 1) // -untagged-
                {
                    filteredAssets = filteredAssets.Where(a => Tagging.GetPackageTags(a.AssetId).Count == 0);
                }
                else
                {
                    string tagName = opt.TagNames[opt.SelectedPackageTag];
                    Tag selectedTag = opt.Tags.FirstOrDefault(t => t.Name == tagName);
                    if (selectedTag != null)
                    {
                        HashSet<int> descendantIds = Tagging.GetDescendantTagIds(selectedTag.Id);
                        filteredAssets = filteredAssets.Where(a => Tagging.GetPackageTags(a.AssetId).Any(t => descendantIds.Contains(t.TagId)));
                    }
                }
            }

            // 8. Publisher filtering
            if (opt.SelectedPublisher > 0 && opt.PublisherNames.Length > opt.SelectedPublisher)
            {
                string[] arr = opt.PublisherNames[opt.SelectedPublisher].Split('/');
                string publisher = arr[arr.Length - 1];
                filteredAssets = filteredAssets.Where(a => a.SafePublisher == publisher);
            }

            // 9. Category filtering
            if (opt.SelectedCategory > 0 && opt.CategoryNames.Length > opt.SelectedCategory)
            {
                filteredAssets = filteredAssets.Where(a => a.DisplayCategory == opt.CategoryNames[opt.SelectedCategory]);
            }

            // 10. Update date filtering
            if (opt.SelectedUpdateDateOption > 0)
            {
                DateTime now = DateTime.Now;

                switch (opt.SelectedUpdateDateOption)
                {
                    case 2: // Last Week
                        DateTime weekAgo = now.AddDays(-7);
                        filteredAssets = filteredAssets.Where(a => a.LastRelease >= weekAgo);
                        break;

                    case 3: // Last Month
                        DateTime monthAgo = now.AddDays(-30);
                        filteredAssets = filteredAssets.Where(a => a.LastRelease >= monthAgo);
                        break;

                    case 4: // Last Year
                        DateTime yearAgo = now.AddDays(-365);
                        filteredAssets = filteredAssets.Where(a => a.LastRelease >= yearAgo);
                        break;

                    case 6: // Before Date
                        if (opt.UpdateBeforeDate.HasValue)
                        {
                            filteredAssets = filteredAssets.Where(a => a.LastRelease <= opt.UpdateBeforeDate.Value);
                        }
                        break;

                    case 7: // After Date
                        if (opt.UpdateAfterDate.HasValue)
                        {
                            filteredAssets = filteredAssets.Where(a => a.LastRelease >= opt.UpdateAfterDate.Value);
                        }
                        break;
                }
            }

            // 11. Purchase date filtering
            if (opt.SelectedPurchaseDateOption > 0)
            {
                DateTime now = DateTime.Now;

                switch (opt.SelectedPurchaseDateOption)
                {
                    case 2: // Last Week
                        DateTime purchaseWeekAgo = now.AddDays(-7);
                        filteredAssets = filteredAssets.Where(a => a.GetPurchaseDate() >= purchaseWeekAgo);
                        break;

                    case 3: // Last Month
                        DateTime purchaseMonthAgo = now.AddDays(-30);
                        filteredAssets = filteredAssets.Where(a => a.GetPurchaseDate() >= purchaseMonthAgo);
                        break;

                    case 4: // Last Year
                        DateTime purchaseYearAgo = now.AddDays(-365);
                        filteredAssets = filteredAssets.Where(a => a.GetPurchaseDate() >= purchaseYearAgo);
                        break;

                    case 6: // Before Date
                        if (opt.PurchaseBeforeDate.HasValue)
                        {
                            filteredAssets = filteredAssets.Where(a => a.GetPurchaseDate() <= opt.PurchaseBeforeDate.Value);
                        }
                        break;

                    case 7: // After Date
                        if (opt.PurchaseAfterDate.HasValue)
                        {
                            filteredAssets = filteredAssets.Where(a => a.GetPurchaseDate() >= opt.PurchaseAfterDate.Value);
                        }
                        break;
                }
            }

            // 12. Package size filtering
            if (opt.SelectedPackageSizeOption > 0 && opt.PackageSizeMB > 0)
            {
                long sizeInBytes = (long)(opt.PackageSizeMB * 1024 * 1024);

                switch (opt.SelectedPackageSizeOption)
                {
                    case 2: // Smaller Than
                        filteredAssets = filteredAssets.Where(a => a.PackageSize > 0 && a.PackageSize < sizeInBytes);
                        break;

                    case 3: // Greater Than
                        filteredAssets = filteredAssets.Where(a => a.PackageSize > sizeInBytes);
                        break;
                }
            }

            // 12. Unity version filtering (simple string comparison for major versions)
            if (opt.SelectedUnityVersionOption > 0)
            {
                int targetYear = 0;
                switch (opt.SelectedUnityVersionOption)
                {
                    case 2: targetYear = 2019; break;
                    case 3: targetYear = 2020; break;
                    case 4: targetYear = 2021; break;
                    case 5: targetYear = 2022; break;
                    case 6: targetYear = 2023; break;
                    case 7: targetYear = 6000; break;
                }

                if (targetYear > 0)
                {
                    filteredAssets = filteredAssets.Where(a => SupportsUnityVersionOrOlder(a.SupportedUnityVersions, targetYear));
                }
            }

            // 13. Package tag token filtering (PT)
            string searchPhrase = opt.SearchPhrase;

            List<string> withAllPT = new List<string>();
            searchPhrase = StringUtils.ExtractTokens(searchPhrase, "withallpt", withAllPT);
            List<string> withAnyPT = new List<string>();
            searchPhrase = StringUtils.ExtractTokens(searchPhrase, new[] {"withanypt", "pt"}, withAnyPT);
            List<string> withNonePT = new List<string>();
            searchPhrase = StringUtils.ExtractTokens(searchPhrase, new[] {"withnonept", "withnopt"}, withNonePT);

            List<string> withAllPTTags = StringUtils.FlattenCommaSeparated(withAllPT);
            if (withAllPTTags.Count > 0)
            {
                filteredAssets = filteredAssets.Where(a =>
                {
                    List<TagInfo> packageTags = Tagging.GetPackageTags(a.AssetId);
                    return withAllPTTags.All(tagName =>
                    {
                        Tag tag = opt.Tags.FirstOrDefault(t => t.Name.ToLowerInvariant() == tagName.ToLowerInvariant());
                        return tag != null && packageTags.Any(pt => pt.TagId == tag.Id);
                    });
                });
            }

            List<string> withAnyPTTags = StringUtils.FlattenCommaSeparated(withAnyPT);
            if (withAnyPTTags.Count > 0)
            {
                filteredAssets = filteredAssets.Where(a =>
                {
                    List<TagInfo> packageTags = Tagging.GetPackageTags(a.AssetId);
                    return withAnyPTTags.Any(tagName =>
                    {
                        Tag tag = opt.Tags.FirstOrDefault(t => t.Name.ToLowerInvariant() == tagName.ToLowerInvariant());
                        return tag != null && packageTags.Any(pt => pt.TagId == tag.Id);
                    });
                });
            }

            List<string> withNonePTTags = StringUtils.FlattenCommaSeparated(withNonePT);
            if (withNonePTTags.Count > 0)
            {
                filteredAssets = filteredAssets.Where(a =>
                {
                    List<TagInfo> packageTags = Tagging.GetPackageTags(a.AssetId);
                    return !withNonePTTags.Any(tagName =>
                    {
                        Tag tag = opt.Tags.FirstOrDefault(t => t.Name.ToLowerInvariant() == tagName.ToLowerInvariant());
                        return tag != null && packageTags.Any(pt => pt.TagId == tag.Id);
                    });
                });
            }

            // 14. Text search (existing fuzzy/exact logic)
            if (!string.IsNullOrWhiteSpace(searchPhrase))
            {
                bool searchDescription = opt.SearchDescription;
                bool searchGroupNames = opt.SearchGroupNames;
                int currentGrouping = opt.CurrentGrouping;

                if (searchPhrase.StartsWith("~")) // exact mode
                {
                    string term = searchPhrase.Substring(1);
                    filteredAssets = filteredAssets.Where(a => MatchesSearchText(a, term, searchDescription, searchGroupNames, currentGrouping));
                }
                else
                {
                    string[] fuzzyWords = searchPhrase.Split(' ');
                    foreach (string fuzzyWord in fuzzyWords.Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)))
                    {
                        if (fuzzyWord.StartsWith("+"))
                        {
                            filteredAssets = filteredAssets.Where(a =>
                            {
                                string phrase = fuzzyWord.Substring(1);
                                return MatchesSearchText(a, phrase, searchDescription, searchGroupNames, currentGrouping);
                            });
                        }
                        else if (fuzzyWord.StartsWith("-"))
                        {
                            filteredAssets = filteredAssets.Where(a =>
                            {
                                string phrase = fuzzyWord.Substring(1);
                                return !MatchesSearchText(a, phrase, searchDescription, searchGroupNames, currentGrouping);
                            });
                        }
                        else
                        {
                            filteredAssets = filteredAssets.Where(a => MatchesSearchText(a, fuzzyWord, searchDescription, searchGroupNames, currentGrouping));
                        }
                    }
                }
            }

            // 15. OnlyInProject filtering
            if (opt.OnlyInProject && opt.UsedPackages != null)
            {
                filteredAssets = filteredAssets.Where(a => opt.UsedPackages.ContainsKey(a.AssetId));
            }

            result.Packages = filteredAssets.ToList();
            result.ResultCount = result.Packages.Count;

            if (opt.MaxResults > 0)
            {
                int skip = (Math.Max(1, opt.CurrentPage) - 1) * opt.MaxResults;
                result.Packages = result.Packages.Skip(skip).Take(opt.MaxResults).ToList();
            }

            return result;
        }

        private static string EnsureInitialized()
        {
            AI.Init();
            if (!AI.IsInitialized)
            {
                return !string.IsNullOrEmpty(DBAdapter.DBError) ? DBAdapter.DBError : "Asset Inventory is not initialized.";
            }
            if (!DBAdapter.IsDBOpen())
            {
                return !string.IsNullOrEmpty(DBAdapter.DBError) ? DBAdapter.DBError : "Asset Inventory database is not open.";
            }
            return null;
        }

        private static float GetPrice(AssetInfo asset, int currency)
        {
            switch (currency)
            {
                case 0: return asset.PriceEur;
                case 1: return asset.PriceUsd;
                case 2: return asset.PriceCny;
                default: return asset.PriceEur;
            }
        }

        private static bool SupportsUnityVersionOrOlder(string supportedVersions, int targetYear)
        {
            if (string.IsNullOrEmpty(supportedVersions)) return false;

            // Parse the major version from strings like "2019.4", "2020.3.48f1", "6000.0.0f1"
            string[] parts = supportedVersions.Split('.');
            if (parts.Length > 0 && int.TryParse(parts[0], out int majorVersion))
            {
                return majorVersion <= targetYear;
            }

            return false;
        }

        private static bool MatchesSearchText(AssetInfo asset, string searchTerm, bool searchDescription, bool searchGroupNames, int grouping)
        {
            string displayName = asset.GetDisplayName();
            return (!string.IsNullOrEmpty(displayName) && displayName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                || (searchDescription && asset.Description != null && asset.Description.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                || (searchGroupNames && MatchesGroupName(asset, searchTerm, grouping));
        }

        private static bool MatchesGroupName(AssetInfo asset, string searchTerm, int grouping)
        {
            string groupName = GetGroupName(asset, grouping);
            return !string.IsNullOrEmpty(groupName) && groupName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetGroupName(AssetInfo asset, int grouping)
        {
            switch (grouping)
            {
                case 1: return asset.DisplayPublisher;
                case 2: return asset.DisplayCategory;
                case 3: return asset.GetLocation(false);
                case 4: return asset.Registry;
                default: return null;
            }
        }
    }
}
