using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Automator
{
    [Serializable]
    public sealed class RevealFolderStep : ActionStep
    {
        public RevealFolderStep()
        {
            Key = "RevealFolder";
            Name = "Reveal Folder";
            Description = "Open a folder in the system file explorer.";
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
            Application.OpenURL("file://" + path);
            await Task.Yield();
        }
    }
}
