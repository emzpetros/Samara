using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ImpossibleRobert.Common;

namespace Automator
{
    [Serializable]
    public sealed class RemoveCompilerArgumentStep : ActionStep
    {
        public RemoveCompilerArgumentStep()
        {
            Key = "RemoveCompilerArgument";
            Name = "Remove Compiler Arg";
            Description = "Remove a compiler argument from the build.";
            Category = ActionCategory.Settings;
            Parameters.Add(new StepParameter
            {
                Name = "Argument"
            });
        }

        public override async Task Run(List<ParameterValue> parameters)
        {
            if (EditorUtils.HasCompilerArgument(parameters[0].stringValue)) EditorUtils.RemoveCompilerArgument(parameters[0].stringValue);
            await Task.Yield();
        }
    }
}