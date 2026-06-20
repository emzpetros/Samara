using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Automator
{
    [Serializable]
    public sealed class MoveFolderStep : ActionStep
    {
        public MoveFolderStep()
        {
            Key = "MoveFolder";
            Name = "Move Folder";
            Description = "Move the folder under the specified path to the target location.";
            Category = ActionCategory.FilesAndFolders;
            Parameters.Add(new StepParameter
            {
                Name = "Source",
                Description = "Path of a folder relative to the current project root.",
                DefaultValue = new ParameterValue("Assets"),
                ValueList = StepParameter.ValueType.Folder
            });
            Parameters.Add(new StepParameter
            {
                Name = "Target",
                Description = "Path of a folder relative to the current project root.",
                DefaultValue = new ParameterValue("Assets"),
                ValueList = StepParameter.ValueType.Folder
            });
        }

        public override async Task Run(List<ParameterValue> parameters)
        {
            string source = parameters[0].stringValue;
            string target = parameters[1].stringValue;

            if (Directory.Exists(target))
            {
                Directory.Delete(target, true);
            }
            Directory.Move(source, target);
            await Task.Yield();
        }
    }
}
