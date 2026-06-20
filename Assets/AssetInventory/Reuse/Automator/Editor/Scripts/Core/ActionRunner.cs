using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ImpossibleRobert.Common;
using UnityEditor;
using UnityEngine;

namespace Automator
{
    /// <summary>
    /// Configuration interface for ActionRunner logging and behavior.
    /// </summary>
    public interface IActionRunnerConfig
    {
        /// <summary>
        /// If true, detailed execution logs are printed to the console.
        /// </summary>
        bool LogActions { get; }
    }

    /// <summary>
    /// Default configuration that enables logging.
    /// </summary>
    public class DefaultActionRunnerConfig : IActionRunnerConfig
    {
        public bool LogActions => true;
    }

    /// <summary>
    /// Executes action sequences with variable resolution and interruption recovery.
    /// </summary>
    public sealed class ActionRunner : ActionProgress
    {
        private static readonly string PREF_PREFIX = "Automator_";
        private static readonly string PREF_ACTION_ACTIVE = PREF_PREFIX + "ActionActive_";
        private static readonly string PREF_CURRENT_STEP = PREF_PREFIX + "CurrentStep_";

        private readonly IActionRepository _repository;
        private static IActionRunnerConfig _config;

        // Dictionary to store variables defined during action execution
        private Dictionary<string, string> _variables = new Dictionary<string, string>();
        private bool _hadFailures;

        // Static reference to current runner instance for steps to access
        private static ActionRunner _currentRunner;

        // Context factory for creating action-scoped context instances
        private static ActionRunnerContextFactory _contextFactory;

        // Current context instance for the running action
        private static IActionRunnerContext _context;

        public ActionRunner(IActionRepository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof (repository));
        }

        /// <summary>
        /// Sets the configuration for all ActionRunner instances.
        /// </summary>
        public static void SetConfig(IActionRunnerConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// Sets the context factory for creating action-scoped context instances.
        /// </summary>
        public static void SetContextFactory(ActionRunnerContextFactory factory)
        {
            _contextFactory = factory;
        }

        /// <summary>
        /// Gets the current action context. Returns null if no action is running.
        /// </summary>
        public static IActionRunnerContext Context => _context;

        private static bool LogActions => _config?.LogActions ?? true;

        /// <summary>
        /// Runs an action by ID.
        /// </summary>
        public async Task<ActionRunResult> RunAction(int actionId)
        {
            ActionDefinition action = _repository.GetAction(actionId);
            if (action == null)
            {
                throw new Exception($"Action with ID {actionId} not found");
            }

            return await RunAction(action);
        }

        /// <summary>
        /// Runs an action by name.
        /// </summary>
        public async Task<ActionRunResult> RunActionByName(string actionName)
        {
            ActionDefinition action = _repository.GetActionByName(actionName);
            if (action == null)
            {
                throw new Exception($"Action with name '{actionName}' not found");
            }

            return await RunAction(action);
        }

        /// <summary>
        /// Runs an action definition.
        /// </summary>
        public async Task<ActionRunResult> RunAction(ActionDefinition action)
        {
            int stepsExecuted = 0;
            int stepsFailed = 0;
            // Get steps for this action
            List<ActionStepDefinition> steps = _repository.GetSteps(action.Id);

            // Initialize variables dictionary
            _variables = new Dictionary<string, string>();
            _hadFailures = false;

            // Set as current runner
            _currentRunner = this;

            // Initialize context using factory or default
            _context = _contextFactory?.Invoke() ?? new DictionaryActionContext();

            // Check if we're resuming after a recompilation
            int lastExecutedStepIndex = EditorPrefs.GetInt(PREF_CURRENT_STEP + action.Id, -1);
            bool isResuming = EditorPrefs.GetBool(PREF_ACTION_ACTIVE + action.Id, false);

            if (isResuming)
            {
                if (LogActions) Debug.Log($"Resuming action '{action.Name}' after recompilation from step {lastExecutedStepIndex + 1}");

                // Restore variables from previous execution
                RestoreVariables(lastExecutedStepIndex, steps);
            }
            else
            {
                // Mark that we're starting execution of this action
                EditorPrefs.SetBool(PREF_ACTION_ACTIVE + action.Id, true);
                EditorPrefs.SetInt(PREF_CURRENT_STEP + action.Id, -1);
                if (LogActions) Debug.Log($"Starting execution of action '{action.Name}'");
            }

            MainCount = steps.Count;
            for (int i = 0; i < steps.Count; i++)
            {
                ActionStepDefinition stepDef = steps[i];
                ActionStep step = ActionStepRegistry.GetStep(stepDef.Key);

                if (step == null)
                {
                    Debug.LogError($"Invalid action step definition. Step '{stepDef.Key}' not found. Skipping.");
                    continue;
                }

                // Skip steps that were already executed before recompilation
                if (isResuming && i <= lastExecutedStepIndex) continue;

                SetProgress(step.Name, i + 1);
                if (LogActions) Debug.Log($"Executing step {i + 1}/{steps.Count}: {step.Name}");

                // Validate parameters
                bool passed = ValidateParameters(step, stepDef.Values);
                if (!passed)
                {
                    _hadFailures = true;
                    stepsFailed++;
                    if (action.StopOnFailure)
                    {
                        Debug.LogError($"Action step '{step.Name}' failed validation. Action execution stopped.");
                        break;
                    }
                    Debug.LogWarning($"Action step '{step.Name}' failed validation. Continuing with next step.");
                    continue;
                }

                // Execute
                try
                {
                    // Mark this step as executed
                    EditorPrefs.SetInt(PREF_CURRENT_STEP + action.Id, i);

                    // Handle variable definition if this is a SetTextVariableStep
                    if (step is SetTextVariableStep)
                    {
                        if (SetTextVariableStep.TryExtractVariable(stepDef.Values, out string varName, out string varValue))
                        {
                            // Resolve any variables in the value itself
                            varValue = VariableResolver.ReplaceVariables(varValue, _variables);

                            // Store the variable
                            _variables[varName] = varValue;

                            if (LogActions)
                            {
                                Debug.Log($"Variable defined: ${varName} = \"{varValue}\"");
                            }
                        }
                    }

                    // Create a copy of values with variables resolved
                    List<ParameterValue> resolvedValues = ResolveParameterVariables(stepDef.Values, step.Parameters, step, _variables);

                    await step.Run(resolvedValues);

                    AssetDatabase.Refresh();

                    // Wait for all processes to finish
                    while (EditorApplication.isCompiling || EditorApplication.isUpdating)
                    {
                        await Task.Delay(25);
                    }

                    stepsExecuted++;
                    if (LogActions) Debug.Log($"Step {i + 1}/{steps.Count} completed successfully");
                    if (step.InterruptsExecution)
                    {
                        return new ActionRunResult {Success = !_hadFailures, StepsExecuted = stepsExecuted, StepsFailed = stepsFailed};
                    }
                }
                catch (Exception e)
                {
                    _hadFailures = true;
                    stepsFailed++;
                    if (action.StopOnFailure)
                    {
                        Debug.LogError($"Error executing step '{step.Name}': {e.Message}. Action execution stopped.");
                        Debug.LogException(e);
                        break;
                    }

                    Debug.LogWarning($"Error executing step '{step.Name}': {e.Message}. Continuing with next step.");
                    Debug.LogException(e);
                }
            }

            // Log completion status
            if (_hadFailures)
            {
                if (action.StopOnFailure)
                {
                    if (LogActions) Debug.Log($"Action '{action.Name}' stopped due to step failure");
                }
                else
                {
                    if (LogActions) Debug.LogWarning($"Action '{action.Name}' completed with failures");
                }
            }
            else
            {
                if (LogActions) Debug.Log($"Action '{action.Name}' completed successfully");
            }

            // Clear execution state when done (either completed or failed)
            EditorPrefs.DeleteKey(PREF_ACTION_ACTIVE + action.Id);
            EditorPrefs.DeleteKey(PREF_CURRENT_STEP + action.Id);

            // Clear current runner reference and context
            _currentRunner = null;
            _context?.Clear();
            _context = null;

            string error = null;
            if (_hadFailures)
            {
                error = action.StopOnFailure
                    ? $"Action '{action.Name}' stopped due to step failure"
                    : $"Action '{action.Name}' completed with {stepsFailed} failure(s)";
            }

            return new ActionRunResult
            {
                Success = !_hadFailures,
                Error = error,
                StepsExecuted = stepsExecuted,
                StepsFailed = stepsFailed
            };
        }

        private bool ValidateParameters(ActionStep step, List<ParameterValue> values)
        {
            bool passed = true;
            for (int j = 0; j < step.Parameters.Count; j++)
            {
                StepParameter param = step.Parameters[j];
                if (param.Optional) continue;

                // Skip hidden parameters
                if (!step.GetParamVisibility(param, values)) continue;

                ParameterValue value = j < values.Count ? values[j] : null;
                if (value == null)
                {
                    Debug.LogError($"Action step '{step.Name}' is missing parameter '{param.Name}'.");
                    passed = false;
                    continue;
                }

                StepParameter.ParamType paramType = step.GetParamType(param, values);

                if ((paramType == StepParameter.ParamType.String || paramType == StepParameter.ParamType.MultilineString) && string.IsNullOrWhiteSpace(value.stringValue))
                {
                    Debug.LogError($"Action step '{step.Name}' is missing parameter '{param.Name}'.");
                    passed = false;
                }
                else if (paramType == StepParameter.ParamType.Int && value.intValue == 0 && !param.Optional)
                {
                    // Check if 0 is explicitly invalid (might be valid for some parameters)
                    // For now, don't treat 0 as missing
                }
            }
            return passed;
        }

        /// <summary>
        /// Resolves variables in parameter values.
        /// </summary>
        public static List<ParameterValue> ResolveParameterVariables(List<ParameterValue> values, List<StepParameter> parameters, ActionStep stepDef, Dictionary<string, string> variables)
        {
            List<ParameterValue> resolved = new List<ParameterValue>();

            for (int i = 0; i < values.Count; i++)
            {
                ParameterValue newValue = new ParameterValue(values[i]);

                // Only resolve variables in string parameters
                if (i < parameters.Count)
                {
                    StepParameter param = parameters[i];

                    // Skip hidden parameters
                    if (!stepDef.GetParamVisibility(param, values))
                    {
                        resolved.Add(newValue);
                        continue;
                    }

                    StepParameter.ParamType paramType = param.Type;

                    // Handle dynamic types by getting the actual type
                    if (paramType == StepParameter.ParamType.Dynamic)
                    {
                        paramType = stepDef.GetParamType(param, values);
                    }

                    if (paramType == StepParameter.ParamType.String || paramType == StepParameter.ParamType.MultilineString)
                    {
                        if (!string.IsNullOrEmpty(newValue.stringValue))
                        {
                            string resolvedValue = VariableResolver.ReplaceVariables(newValue.stringValue, variables);

                            // Check if there are any unresolved variables left after replacement
                            if (VariableResolver.ContainsVariables(resolvedValue))
                            {
                                List<string> unresolvedVars = VariableResolver.FindVariableReferences(resolvedValue);
                                throw new Exception($"Parameter '{parameters[i].Name}' contains unresolved variables: {string.Join(", ", unresolvedVars.Select(v => "$" + v))}");
                            }

                            newValue.stringValue = resolvedValue;
                        }
                    }
                }

                resolved.Add(newValue);
            }

            return resolved;
        }

        /// <summary>
        /// Restores variables by re-executing variable definitions from completed steps.
        /// This is called after recompilation to rebuild the variable state.
        /// </summary>
        private void RestoreVariables(int lastExecutedStepIndex, List<ActionStepDefinition> steps)
        {
            // Re-execute variable definitions from steps that were completed
            for (int i = 0; i <= lastExecutedStepIndex && i < steps.Count; i++)
            {
                ActionStepDefinition stepDef = steps[i];
                ActionStep step = ActionStepRegistry.GetStep(stepDef.Key);

                if (step is SetTextVariableStep)
                {
                    if (SetTextVariableStep.TryExtractVariable(stepDef.Values, out string varName, out string varValue))
                    {
                        // Resolve variables in the value (in case it references other variables)
                        varValue = VariableResolver.ReplaceVariables(varValue, _variables);
                        _variables[varName] = varValue;

                        if (LogActions)
                        {
                            Debug.Log($"Variable restored: ${varName} = \"{varValue}\"");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Gets the current runner's variables dictionary.
        /// </summary>
        public static Dictionary<string, string> GetCurrentVariables()
        {
            return _currentRunner?._variables ?? new Dictionary<string, string>();
        }
    }
}