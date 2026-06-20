using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Automator
{
    [Serializable]
    public sealed class DeleteFileStep : ActionStep
    {
        public DeleteFileStep()
        {
            Key = "DeleteFile";
            Name = "Delete File";
            Description = "Delete the file under the specified path.";
            Category = ActionCategory.FilesAndFolders;
            Parameters.Add(new StepParameter
            {
                Name = "Path",
                Description = "Path to a file relative to the current project root.",
                DefaultValue = new ParameterValue("Assets/Readme.md")
            });
        }

        public override async Task Run(List<ParameterValue> parameters)
        {
            string path = parameters[0].stringValue;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            await Task.Yield();
        }
    }
}
