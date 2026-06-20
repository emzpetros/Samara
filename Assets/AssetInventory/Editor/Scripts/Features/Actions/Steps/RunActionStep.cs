using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Automator;

namespace AssetInventory
{
    [Serializable]
    public sealed class RunActionStep : ActionStep
    {
        public RunActionStep()
        {
            Key = "RunAction";
            Name = "Run Action";
            Description = "Run another custom or predefined action.";
            Category = ActionCategory.Actions;
            Parameters.Add(new StepParameter
            {
                Name = "Action",
                Description = "Action to run.",
                ValueList = StepParameter.ValueType.Custom,
                LazyLoadOptions = true
            });
            Parameters.Add(new StepParameter
            {
                Name = "Force",
                Description = "Force re-run the action even if recently executed.",
                Type = StepParameter.ParamType.Bool,
                DefaultValue = new ParameterValue(false)
            });
        }

        public override List<Tuple<string, ParameterValue>> GetParamOptions(StepParameter param, List<ParameterValue> parameters)
        {
            if (param.Name == "Action")
            {
                List<Tuple<string, ParameterValue>> options = new List<Tuple<string, ParameterValue>>();
                foreach (UpdateAction action in AI.Actions.Actions.Where(a => !a.hidden))
                {
                    options.Add(new Tuple<string, ParameterValue>(action.name, new ParameterValue(action.key)));
                }
                return options;
            }
            return base.GetParamOptions(param, parameters);
        }

        public override bool GetParamVisibility(StepParameter param, List<ParameterValue> parameters)
        {
            if (param.Name == "Force")
            {
                // Only show Force for internal actions (not user actions)
                if (parameters.Count > 0 && !string.IsNullOrEmpty(parameters[0].stringValue))
                {
                    return !parameters[0].stringValue.StartsWith(ActionHandler.ACTION_USER);
                }
                return false; // Hide until action is selected
            }
            return base.GetParamVisibility(param, parameters);
        }

        public override async Task Run(List<ParameterValue> parameters)
        {
            string key = parameters[0].stringValue;
            bool force = parameters.Count > 1 && parameters[1].boolValue;

            if (key.StartsWith(ActionHandler.ACTION_USER))
            {
                // user actions are more complex and need to be triggered differently
                int idx = int.Parse(key.Substring(ActionHandler.ACTION_USER.Length));
                CustomAction action = DBAdapter.DB.Find<CustomAction>(idx);
                await AI.Actions.RunUserAction(action);
            }
            else
            {
                // internal action
                await AI.Actions.RunAction(key, force);
            }
        }
    }
}
