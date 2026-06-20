using Automator;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AssetInventory
{
    [Serializable]
    public sealed class LoadFilesStep : ActionStep
    {
        public enum FileOperation
        {
            Replace = 1,
            Additive = 2,
            Subtractive = 3
        }

        public LoadFilesStep()
        {
            Key = "LoadFiles";
            Name = "Load Files";
            Description = "Define the files to work on in subsequent steps. Supports saved file searches.";
            Category = ActionCategory.Misc;

            // Filter parameter with lazy loading
            Parameters.Add(new StepParameter
            {
                Name = "Filter",
                Description = "Select a saved file search to define which files to load.",
                ValueList = StepParameter.ValueType.Custom,
                LazyLoadOptions = true
            });

            // Operation parameter
            List<Tuple<string, ParameterValue>> operationOptions = new List<Tuple<string, ParameterValue>>
            {
                new Tuple<string, ParameterValue>("Replace", new ParameterValue((int)FileOperation.Replace)),
                new Tuple<string, ParameterValue>("Additive", new ParameterValue((int)FileOperation.Additive)),
                new Tuple<string, ParameterValue>("Subtractive", new ParameterValue((int)FileOperation.Subtractive))
            };

            Parameters.Add(new StepParameter
            {
                Name = "Operation",
                Description = "How to merge with previously loaded files: Replace (set new list), Additive (add to existing), Subtractive (remove from existing).",
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
            FileOperation operation = (FileOperation)operationValue;

            List<AssetInfo> files;

            // Parse filter value
            if (string.IsNullOrEmpty(filterValue))
            {
                throw new Exception("Filter value is empty. Please select a valid filter option.");
            }

            if (filterValue.StartsWith("savedsearch:"))
            {
                // Saved search filter
                int searchId = int.Parse(filterValue.Substring("savedsearch:".Length));
                files = await LoadFilesBySavedSearch(searchId);
            }
            else
            {
                throw new Exception($"Unknown filter format: {filterValue}");
            }

            // Store the loaded files with the specified operation
            AssetInventoryActionContext.SetLoadedFiles(files, operation);

            await Task.Yield();
        }

        private async Task<List<AssetInfo>> LoadFilesBySavedSearch(int searchId)
        {
            await Task.Yield();

            SavedSearch savedSearch = DBAdapter.DB.Find<SavedSearch>(searchId);
            if (savedSearch == null)
            {
                throw new Exception($"Saved file search with ID {searchId} not found.");
            }

            AssetSearch.Options options = AssetSearch.Options.FromSavedSearch(savedSearch);
            AssetSearch.Result result = AssetSearch.Execute(options);
            if (!string.IsNullOrEmpty(result.Error))
            {
                throw new Exception($"Error executing file search: {result.Error}");
            }

            return result.Files;
        }

        public override List<Tuple<string, ParameterValue>> GetParamOptions(StepParameter param, List<ParameterValue> parameters)
        {
            if (param.Name == "Filter")
            {
                // Build filter options: saved file searches
                List<Tuple<string, ParameterValue>> filterOptions = new List<Tuple<string, ParameterValue>>();

                // Add saved file searches if any exist
                List<SavedSearch> savedSearches = DBAdapter.DB.Table<SavedSearch>().ToList();
                if (savedSearches.Count > 0)
                {
                    foreach (SavedSearch search in savedSearches)
                    {
                        filterOptions.Add(new Tuple<string, ParameterValue>(search.Name, new ParameterValue($"savedsearch:{search.Id}")));
                    }
                }
                else
                {
                    filterOptions.Add(new Tuple<string, ParameterValue>("No saved file searches available", new ParameterValue("")));
                }

                return filterOptions;
            }
            return base.GetParamOptions(param, parameters);
        }
    }
}
