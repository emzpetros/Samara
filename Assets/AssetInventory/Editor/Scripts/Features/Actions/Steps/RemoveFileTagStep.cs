using Automator;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AssetInventory
{
    [Serializable]
    public sealed class RemoveFileTagStep : TagActionStepBase
    {
        public RemoveFileTagStep()
        {
            Key = "RemoveFileTag";
            Name = "Remove File Tag";
            Description = "Remove file tags from files. Requires LoadFilesStep to be executed first.";
            Category = ActionCategory.Actions;
        }

        protected override List<AssetInfo> GetTargetItems(List<ParameterValue> parameters)
        {
            return AssetInventoryActionContext.GetFilesForStep();
        }

        protected override void ApplyTags(List<AssetInfo> items, List<string> tags)
        {
            foreach (string tag in tags)
            {
                Tagging.RemoveAssetAssignments(items, tag, byUser: false);
            }
        }
    }
}
