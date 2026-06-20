using Automator;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AssetInventory
{
    [Serializable]
    public sealed class LoadAssetsStep : ActionStep
    {
        public enum AssetOperation
        {
            Replace = 1,
            Additive = 2,
            Subtractive = 3
        }

        public LoadAssetsStep()
        {
            Key = "LoadAssets";
            Name = "Load Assets";
            Description = "Define the assets to work on in subsequent steps. Supports package type filters and saved searches.";
            Category = ActionCategory.Misc;

            // Filter parameter with lazy loading
            Parameters.Add(new StepParameter
            {
                Name = "Filter",
                Description = "Select package type filter or a saved package search to define which assets to load.",
                ValueList = StepParameter.ValueType.Custom,
                LazyLoadOptions = true,
                DefaultValue = new ParameterValue("packagetype:0")
            });

            // Operation parameter
            List<Tuple<string, ParameterValue>> operationOptions = new List<Tuple<string, ParameterValue>>
            {
                new Tuple<string, ParameterValue>("Replace", new ParameterValue((int)AssetOperation.Replace)),
                new Tuple<string, ParameterValue>("Additive", new ParameterValue((int)AssetOperation.Additive)),
                new Tuple<string, ParameterValue>("Subtractive", new ParameterValue((int)AssetOperation.Subtractive))
            };

            Parameters.Add(new StepParameter
            {
                Name = "Operation",
                Description = "How to merge with previously loaded assets: Replace (set new list), Additive (add to existing), Subtractive (remove from existing).",
                Type = StepParameter.ParamType.Int,
                ValueList = StepParameter.ValueType.Custom,
                Options = operationOptions,
                DefaultValue = operationOptions[0].Item2
            });
        }

        public override async Task Run(List<ParameterValue> parameters)
        {
            string filterValue = parameters[0].stringValue;
            int operationValue = parameters[1].intValue;
            AssetOperation operation = (AssetOperation)operationValue;

            List<AssetInfo> assets;

            // Parse filter value
            if (string.IsNullOrEmpty(filterValue))
            {
                throw new Exception("Filter value is empty. Please select a valid filter option.");
            }

            if (filterValue.StartsWith("packagetype:"))
            {
                // Package type filter
                int packageType = int.Parse(filterValue.Substring("packagetype:".Length));
                assets = await LoadAssetsByPackageType(packageType);
            }
            else if (filterValue.StartsWith("savedsearch:"))
            {
                // Saved search filter
                int searchId = int.Parse(filterValue.Substring("savedsearch:".Length));
                assets = await LoadAssetsBySavedSearch(searchId);
            }
            else
            {
                throw new Exception($"Unknown filter format: {filterValue}");
            }

            // Store the loaded assets with the specified operation
            AssetInventoryActionContext.SetLoadedAssets(assets, operation);

            await Task.Yield();
        }

        private async Task<List<AssetInfo>> LoadAssetsByPackageType(int packageType)
        {
            await Task.Yield();

            IEnumerable<AssetInfo> query = Assets.Load();

            // Apply package type filters based on IndexUI logic
            switch (packageType)
            {
                case 0: // -all-
                    // No additional filters
                    break;

                case 1: // -all except registry packages-
                    query = query.Where(a => a.AssetSource != Asset.Source.RegistryPackage);
                    break;

                case 2: // Only Asset Store Packages
                    query = query.Where(a => a.AssetSource == Asset.Source.AssetStorePackage ||
                        (a.AssetSource == Asset.Source.RegistryPackage && a.ForeignId > 0));
                    break;

                case 3: // Only Registry Packages
                    query = query.Where(a => a.AssetSource == Asset.Source.RegistryPackage);
                    break;

                case 4: // Only Custom Packages
                    query = query.Where(a => a.AssetSource == Asset.Source.CustomPackage);
                    break;

                case 5: // Only Media Folders
                    query = query.Where(a => a.AssetSource == Asset.Source.Directory);
                    break;

                case 6: // Only Archives
                    query = query.Where(a => a.AssetSource == Asset.Source.Archive);
                    break;

                case 7: // Only Asset Manager
                    query = query.Where(a => a.AssetSource == Asset.Source.AssetManager);
                    break;

                default:
                    throw new Exception($"Unknown package type: {packageType}");
            }

            return query.ToList();
        }

        private async Task<List<AssetInfo>> LoadAssetsBySavedSearch(int searchId)
        {
            await Task.Yield();

            SavedPackageSearch savedSearch = DBAdapter.DB.Find<SavedPackageSearch>(searchId);
            if (savedSearch == null)
            {
                throw new Exception($"Saved package search with ID {searchId} not found.");
            }

            PackageSearch.Options options = PackageSearch.Options.FromSavedSearch(savedSearch);
            PackageSearch.Result result = PackageSearch.Execute(options);
            return result.Packages;
        }

        public override List<Tuple<string, ParameterValue>> GetParamOptions(StepParameter param, List<ParameterValue> parameters)
        {
            if (param.Name == "Filter")
            {
                // Build filter options: package types + separator + saved searches
                List<Tuple<string, ParameterValue>> filterOptions = new List<Tuple<string, ParameterValue>>();

                // Add package type options
                filterOptions.Add(new Tuple<string, ParameterValue>("-all-", new ParameterValue("packagetype:0")));
                filterOptions.Add(new Tuple<string, ParameterValue>("-all except registry packages-", new ParameterValue("packagetype:1")));
                filterOptions.Add(new Tuple<string, ParameterValue>("Asset Store Packages", new ParameterValue("packagetype:2")));
                filterOptions.Add(new Tuple<string, ParameterValue>("Registry Packages", new ParameterValue("packagetype:3")));
                filterOptions.Add(new Tuple<string, ParameterValue>("Custom Packages", new ParameterValue("packagetype:4")));
                filterOptions.Add(new Tuple<string, ParameterValue>("Media Folders", new ParameterValue("packagetype:5")));
                filterOptions.Add(new Tuple<string, ParameterValue>("Archives", new ParameterValue("packagetype:6")));
                filterOptions.Add(new Tuple<string, ParameterValue>("Asset Manager", new ParameterValue("packagetype:7")));

                // Add saved searches if any exist
                List<SavedPackageSearch> savedSearches = DBAdapter.DB.Table<SavedPackageSearch>().ToList();
                if (savedSearches.Count > 0)
                {
                    filterOptions.Add(new Tuple<string, ParameterValue>(string.Empty, new ParameterValue("")));
                    foreach (SavedPackageSearch search in savedSearches)
                    {
                        filterOptions.Add(new Tuple<string, ParameterValue>(search.Name, new ParameterValue($"savedsearch:{search.Id}")));
                    }
                }

                return filterOptions;
            }
            return base.GetParamOptions(param, parameters);
        }
    }
}
