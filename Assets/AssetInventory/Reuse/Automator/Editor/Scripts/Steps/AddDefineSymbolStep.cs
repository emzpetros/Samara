using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ImpossibleRobert.Common;

namespace Automator
{
    [Serializable]
    public sealed class AddDefineSymbolStep : ActionStep
    {
        public AddDefineSymbolStep()
        {
            Key = "AddDefineSymbol";
            Name = "Add Define Symbol";
            Description = "Add a compiler define symbol.";
            Category = ActionCategory.Settings;
            Parameters.Add(new StepParameter
            {
                Name = "Symbol"
            });
        }

        public override async Task Run(List<ParameterValue> parameters)
        {
            if (!EditorUtils.HasDefine(parameters[0].stringValue)) EditorUtils.AddDefine(parameters[0].stringValue);

            await Task.Yield();
        }
    }
}