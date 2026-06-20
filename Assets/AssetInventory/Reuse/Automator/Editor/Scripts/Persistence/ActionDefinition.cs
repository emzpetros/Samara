using System;
using System.Collections.Generic;

namespace Automator
{
    /// <summary>
    /// A user-defined action containing a sequence of steps.
    /// </summary>
    [Serializable]
    public class ActionDefinition
    {
        public enum RunMode
        {
            Manual = 0,
            AtInstallation = 1
        }

        /// <summary>
        /// Unique identifier for this action.
        /// </summary>
        public int Id;

        /// <summary>
        /// Display name of the action.
        /// </summary>
        public string Name;

        /// <summary>
        /// Optional description of what the action does.
        /// </summary>
        public string Description;

        /// <summary>
        /// If true, stop execution when a step fails. If false, continue with next step.
        /// </summary>
        public bool StopOnFailure = true;

        /// <summary>
        /// When this action should run.
        /// </summary>
        public RunMode Mode;

        /// <summary>
        /// Steps to execute (populated when loading from repository).
        /// </summary>
        public List<ActionStepDefinition> Steps = new List<ActionStepDefinition>();

        public ActionDefinition()
        {
        }

        public ActionDefinition(string name) : this()
        {
            Name = name;
        }

        public override string ToString()
        {
            return $"Action '{Name}'";
        }
    }

    /// <summary>
    /// A single step within an action definition.
    /// </summary>
    [Serializable]
    public class ActionStepDefinition
    {
        /// <summary>
        /// Unique identifier for this step.
        /// </summary>
        public int Id;

        /// <summary>
        /// ID of the parent action.
        /// </summary>
        public int ActionId;

        /// <summary>
        /// Key of the ActionStep type (e.g., "CopyFile", "DebugLog").
        /// </summary>
        public string Key;

        /// <summary>
        /// Order index within the action (0-based).
        /// </summary>
        public int OrderIndex;

        /// <summary>
        /// Parameter values for this step.
        /// </summary>
        public List<ParameterValue> Values = new List<ParameterValue>();

        public override string ToString()
        {
            return $"Step '{Key}' (Order: {OrderIndex})";
        }
    }
}
