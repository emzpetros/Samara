using Automator;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AssetInventory
{
    [Serializable]
    public sealed class AddPackageTagStep : TagActionStepBase
    {
        public AddPackageTagStep()
        {
            Key = "AddPackageTag";
            Name = "Add Package Tag";
            Description = "Add package tags to packages. Requires LoadAssetsStep to be executed first.";
            Category = ActionCategory.Actions;
        }

        protected override List<AssetInfo> GetTargetItems(List<ParameterValue> parameters)
        {
            return AssetInventoryActionContext.GetAssetsForStep();
        }

        protected override void ApplyTags(List<AssetInfo> items, List<string> tags)
        {
            foreach (string tag in tags)
            {
                Tagging.AddAssignments(items, tag, TagAssignment.Target.Package, byUser: false);
            }
        }
    }
}
