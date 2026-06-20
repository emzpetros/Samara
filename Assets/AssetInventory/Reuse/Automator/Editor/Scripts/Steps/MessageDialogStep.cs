using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;

namespace Automator
{
    [Serializable]
    public sealed class MessageDialogStep : ActionStep
    {
        public MessageDialogStep()
        {
            Key = "MessageDialog";
            Name = "Message Dialog";
            Description = "Show a message dialog to the user.";
            Category = ActionCategory.Misc;
            Parameters.Add(new StepParameter
            {
                Name = "Title",
                Description = "Title of the dialog window.",
                DefaultValue = new ParameterValue("Message")
            });
            Parameters.Add(new StepParameter
            {
                Name = "Text",
                Description = "Message text to display.",
                Type = StepParameter.ParamType.MultilineString
            });
        }

        public override async Task Run(List<ParameterValue> parameters)
        {
            EditorUtility.DisplayDialog(parameters[0].stringValue, parameters[1].stringValue, "OK");
            await Task.Yield();
        }
    }
}
