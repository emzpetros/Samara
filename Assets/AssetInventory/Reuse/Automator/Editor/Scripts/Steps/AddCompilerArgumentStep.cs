using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ImpossibleRobert.Common;

namespace Automator
{
    [Serializable]
    public sealed class AddCompilerArgumentStep : ActionStep
    {
        public AddCompilerArgumentStep()
        {
            Key = "AddCompilerArgument";
            Name = "Add Compiler Arg";
            Description = "Add a compiler argument for the build.";
            Category = ActionCategory.Settings;
            Parameters.Add(new StepParameter
            {
                Name = "Argument"
            });
        }

        public override async Task Run(List<ParameterValue> parameters)
        {
            if (!EditorUtils.HasCompilerArgument(parameters[0].stringValue)) EditorUtils.AddCompilerArgument(parameters[0].stringValue);
            await Task.Yield();
        }
    }
}