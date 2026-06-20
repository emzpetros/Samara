using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Automator
{
    [Serializable]
    public sealed class DeleteFolderStep : ActionStep
    {
        public DeleteFolderStep()
        {
            Key = "DeleteFolder";
            Name = "Delete Folder";
            Description = "Delete the folder under the specified path.";
            Category = ActionCategory.FilesAndFolders;
            Parameters.Add(new StepParameter
            {
                Name = "Path",
                Description = "Path of a folder relative to the current project root.",
                DefaultValue = new ParameterValue("Assets"),
                ValueList = StepParameter.ValueType.Folder
            });
        }

        public override async Task Run(List<ParameterValue> parameters)
        {
            string path = parameters[0].stringValue;
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
            await Task.Yield();
        }
    }
}
