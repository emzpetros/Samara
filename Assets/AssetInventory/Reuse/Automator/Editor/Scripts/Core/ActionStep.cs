using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Automator
{
    /// <summary>
    /// Base class for action steps. Derive to create custom steps; discovered via reflection.
    /// </summary>
    [Serializable]
    public abstract class ActionStep
    {
        public enum ActionCategory
        {
            FilesAndFolders,
            Importing,
            Actions,
            Settings,
            Misc
        }

        /// <summary>
        /// Unique identifier for this step type. Used for persistence.
        /// </summary>
        public string Key { get; protected set; }

        /// <summary>
        /// Display name shown in the UI.
        /// </summary>
        public string Name { get; protected set; }

        /// <summary>
        /// Description shown as tooltip.
        /// </summary>
        public string Description { get; protected set; }

        /// <summary>
        /// Category for grouping in the step selection menu.
        /// </summary>
        public ActionCategory Category { get; protected set; } = ActionCategory.Misc;

        /// <summary>
        /// If true, this step will interrupt the action execution (e.g., editor restart).
        /// </summary>
        public bool InterruptsExecution { get; protected set; }

        /// <summary>
        /// List of parameters this step accepts.
        /// </summary>
        public List<StepParameter> Parameters { get; protected set; } = new List<StepParameter>();

        /// <summary>
        /// Executes the step.
        /// </summary>
        public abstract Task Run(List<ParameterValue> parameters);

        /// <summary>
        /// Gets the parameter type for dynamic parameters.
        /// </summary>
        public virtual StepParameter.ParamType GetParamType(StepParameter param, List<ParameterValue> parameters)
        {
            return param.Type;
        }

        /// <summary>
        /// Gets the value list type for dynamic parameters.
        /// </summary>
        public virtual StepParameter.ValueType GetParamValueList(StepParameter param, List<ParameterValue> parameters)
        {
            return param.ValueList;
        }

        /// <summary>
        /// Gets options for a parameter. Override for dynamic options.
        /// </summary>
        public virtual List<Tuple<string, ParameterValue>> GetParamOptions(StepParameter param, List<ParameterValue> parameters)
        {
            return param.Options;
        }

        /// <summary>
        /// Gets whether a parameter is visible. Override for conditional visibility.
        /// </summary>
        public virtual bool GetParamVisibility(StepParameter param, List<ParameterValue> parameters)
        {
            return true;
        }
    }

    /// <summary>
    /// Defines a parameter for an action step.
    /// </summary>
    [Serializable]
    public class StepParameter
    {
        public enum ParamType
        {
            String,
            MultilineString,
            Int,
            Bool,
            Dynamic // determined depending on other parameters
        }

        public enum ValueType
        {
            None,
            Custom,
            Folder,
            Package
        }

        /// <summary>
        /// Parameter name shown in the UI.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Description shown as tooltip.
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Type of the parameter value.
        /// </summary>
        public ParamType Type { get; set; } = ParamType.String;

        /// <summary>
        /// Default value for the parameter.
        /// </summary>
        public ParameterValue DefaultValue { get; set; } = new ParameterValue();

        /// <summary>
        /// Type of value list (None, Custom, Folder, Package).
        /// </summary>
        public ValueType ValueList { get; set; } = ValueType.None;

        /// <summary>
        /// Fixed list of options to choose from.
        /// </summary>
        public List<Tuple<string, ParameterValue>> Options { get; set; }

        /// <summary>
        /// If true, this parameter can be left empty.
        /// </summary>
        public bool Optional { get; set; }

        /// <summary>
        /// Defers option loading until first access. Caches per UI session.
        /// </summary>
        public bool LazyLoadOptions { get; set; }
    }

    /// <summary>
    /// Holds the actual value for a step parameter.
    /// </summary>
    [Serializable]
    public class ParameterValue
    {
        public string stringValue;
        public int intValue;
        public bool boolValue;

        public ParameterValue() { }

        public ParameterValue(ParameterValue copyFrom)
        {
            if (copyFrom == null) return;
            stringValue = copyFrom.stringValue;
            intValue = copyFrom.intValue;
            boolValue = copyFrom.boolValue;
        }

        public ParameterValue(string stringValue)
        {
            this.stringValue = stringValue;
        }

        public ParameterValue(int intValue)
        {
            this.intValue = intValue;
        }

        public ParameterValue(bool boolValue)
        {
            this.boolValue = boolValue;
        }
    }
}
