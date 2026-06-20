using Automator;
using ImpossibleRobert.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AssetInventory
{
    [Serializable]
    public abstract class TagActionStepBase : ActionStep
    {
        protected TagActionStepBase()
        {
            // Tags parameter
            Parameters.Add(new StepParameter
            {
                Name = "Tags",
                Description = "Comma-separated list of tags to add or remove.",
                Type = StepParameter.ParamType.String
            });
        }

        public override async Task Run(List<ParameterValue> parameters)
        {
            string tagsValue = parameters[0].stringValue;

            // Parse tags
            List<string> tags = StringUtils.FlattenCommaSeparated(new[] {tagsValue});
            
            // Validate that tags list is not empty
            if (tags.Count == 0 || tags.All(string.IsNullOrWhiteSpace))
            {
                throw new Exception("Tags list is empty. Please provide at least one tag.");
            }

            // Remove empty tags and trim
            tags = tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();

            if (tags.Count == 0)
            {
                throw new Exception("No valid tags found after parsing. Please provide at least one non-empty tag.");
            }

            // Get target items
            List<AssetInfo> items = GetTargetItems(parameters);

            if (items == null || items.Count == 0)
            {
                throw new Exception("No items found to tag. Please ensure the appropriate Load step was executed first.");
            }

            // Apply tags
            ApplyTags(items, tags);

            await Task.Yield();
        }

        /// <summary>
        /// Gets the list of items to tag (packages or files).
        /// </summary>
        protected abstract List<AssetInfo> GetTargetItems(List<ParameterValue> parameters);

        /// <summary>
        /// Applies tags to the items (add or remove).
        /// </summary>
        protected abstract void ApplyTags(List<AssetInfo> items, List<string> tags);
    }
}
