#if ASSET_INVENTORY_MCP
using System;
using System.Collections.Generic;
using System.Linq;
using Automator;
using Newtonsoft.Json;
using Unity.AI.MCP.Editor.Helpers;
using Unity.AI.MCP.Editor.ToolRegistry;

namespace AssetInventory
{
    public static class ActionStepTools
    {
        #region List Action Step Types

        public class ListActionStepTypesParams
        {
            [McpDescription("Category filter: FilesAndFolders, Importing, Actions, Settings, or Misc.")]
            public string Category { get; set; }
        }

        [McpTool("AssetInventory_listActionStepTypes",
            "List available step types and their parameters for building actions. Use the step key with addActionStep.",
            Groups = new[] {"Asset Inventory/Actions"})]
        public static object ListActionStepTypes(ListActionStepTypesParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            List<ActionStep> steps = ActionStepRegistry.Steps;

            if (!string.IsNullOrEmpty(parameters.Category))
            {
                if (Enum.TryParse(parameters.Category, true, out ActionStep.ActionCategory category))
                {
                    steps = steps.Where(s => s.Category == category).ToList();
                }
                else
                {
                    return Response.Error($"Invalid category '{parameters.Category}'. Use: FilesAndFolders, Importing, Actions, Settings, Misc.");
                }
            }

            return Response.Success($"Found {steps.Count} action step types.", new
            {
                stepTypes = steps.Select(s => new
                {
                    key = s.Key,
                    name = s.Name,
                    description = s.Description,
                    category = s.Category.ToString(),
                    parameters = s.Parameters.Select(p => new
                    {
                        name = p.Name,
                        description = p.Description,
                        type = p.Type.ToString(),
                        optional = p.Optional,
                        defaultStringValue = p.DefaultValue?.stringValue,
                        defaultIntValue = p.DefaultValue?.intValue ?? 0,
                        defaultBoolValue = p.DefaultValue?.boolValue ?? false
                    }).ToArray()
                }).ToArray()
            });
        }

        #endregion

        #region Add Action Step

        public class AddActionStepParams
        {
            [McpDescription("Action ID.", Required = true)]
            public int ActionId { get; set; }

            [McpDescription("Step type key from listActionStepTypes.", Required = true)]
            public string StepKey { get; set; }

            [McpDescription("Position (0-based). -1 appends to end.", Default = -1)]
            public int OrderIndex { get; set; } = -1;

            [McpDescription("JSON object mapping parameter names to values, e.g. {\"Target File\": \"/path/file.csv\"}. String/int/bool values supported.")]
            public string ParameterValues { get; set; }
        }

        [McpTool("AssetInventory_addActionStep",
            "Add a step to an action. Steps execute in order.",
            Groups = new[] {"Asset Inventory/Actions"})]
        public static object AddActionStep(AddActionStepParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            if (string.IsNullOrEmpty(parameters.StepKey))
            {
                return Response.Error("StepKey is required.");
            }

            SqliteActionRepository repo = new SqliteActionRepository();
            ActionDefinition action = repo.GetAction(parameters.ActionId);
            if (action == null)
            {
                return Response.Error($"Action with ID {parameters.ActionId} not found.");
            }

            ActionStep stepType = ActionStepRegistry.GetStep(parameters.StepKey);
            if (stepType == null)
            {
                return Response.Error($"Step type '{parameters.StepKey}' not found. Use AssetInventory_listActionStepTypes to see available types.");
            }

            List<ActionStepDefinition> existingSteps = repo.GetSteps(parameters.ActionId);
            int orderIndex = parameters.OrderIndex >= 0 ? parameters.OrderIndex : existingSteps.Count;

            // Build parameter values from defaults, then apply overrides
            List<ParameterValue> values = stepType.Parameters.Select(p => new ParameterValue(p.DefaultValue ?? new ParameterValue())).ToList();

            if (!string.IsNullOrEmpty(parameters.ParameterValues))
            {
                try
                {
                    Dictionary<string, object> overrides = JsonConvert.DeserializeObject<Dictionary<string, object>>(parameters.ParameterValues);
                    if (overrides != null)
                    {
                        foreach (KeyValuePair<string, object> kvp in overrides)
                        {
                            int paramIdx = stepType.Parameters.FindIndex(p => p.Name.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase));
                            if (paramIdx < 0)
                            {
                                return Response.Error($"Unknown parameter '{kvp.Key}' for step type '{parameters.StepKey}'.");
                            }

                            while (values.Count <= paramIdx) values.Add(new ParameterValue());

                            if (kvp.Value is bool boolVal)
                            {
                                values[paramIdx].boolValue = boolVal;
                            }
                            else if (kvp.Value is long longVal)
                            {
                                values[paramIdx].intValue = (int)longVal;
                            }
                            else if (kvp.Value is int intVal)
                            {
                                values[paramIdx].intValue = intVal;
                            }
                            else
                            {
                                values[paramIdx].stringValue = kvp.Value?.ToString();
                            }
                        }
                    }
                }
                catch (JsonException e)
                {
                    return Response.Error($"Invalid ParameterValues JSON: {e.Message}");
                }
            }

            ActionStepDefinition stepDef = new ActionStepDefinition
            {
                ActionId = parameters.ActionId,
                Key = parameters.StepKey,
                OrderIndex = orderIndex,
                Values = values
            };

            stepDef = repo.SaveStep(stepDef);

            return Response.Success($"Step '{stepType.Name}' added to action '{action.Name}' at position {orderIndex}.", new { stepId = stepDef.Id });
        }

        #endregion

        #region Remove Action Step

        public class RemoveActionStepParams
        {
            [McpDescription("Step ID from getActionDetails.", Required = true)]
            public int StepId { get; set; }
        }

        [McpTool("AssetInventory_removeActionStep",
            "Remove a step from an action.",
            Groups = new[] {"Asset Inventory/Actions"})]
        public static object RemoveActionStep(RemoveActionStepParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            SqliteActionRepository repo = new SqliteActionRepository();
            repo.DeleteStep(parameters.StepId);

            return Response.Success($"Step {parameters.StepId} removed.");
        }

        #endregion

        #region Update Action Step

        public class UpdateActionStepParams
        {
            [McpDescription("Action ID.", Required = true)]
            public int ActionId { get; set; }

            [McpDescription("Step ID.", Required = true)]
            public int StepId { get; set; }

            [McpDescription("New position (0-based). -1 keeps current.", Default = -1)]
            public int OrderIndex { get; set; } = -1;

            [McpDescription("JSON object of parameter names to new values. Only specified params update.")]
            public string ParameterValues { get; set; }
        }

        [McpTool("AssetInventory_updateActionStep",
            "Update a step's parameters or position within an action.",
            Groups = new[] {"Asset Inventory/Actions"})]
        public static object UpdateActionStep(UpdateActionStepParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            SqliteActionRepository repo = new SqliteActionRepository();
            List<ActionStepDefinition> steps = repo.GetSteps(parameters.ActionId);
            ActionStepDefinition step = steps.FirstOrDefault(s => s.Id == parameters.StepId);
            if (step == null)
            {
                return Response.Error($"Step with ID {parameters.StepId} not found in action {parameters.ActionId}.");
            }

            ActionStep stepType = ActionStepRegistry.GetStep(step.Key);

            if (parameters.OrderIndex >= 0)
            {
                step.OrderIndex = parameters.OrderIndex;
            }

            if (!string.IsNullOrEmpty(parameters.ParameterValues) && stepType != null)
            {
                try
                {
                    Dictionary<string, object> overrides = JsonConvert.DeserializeObject<Dictionary<string, object>>(parameters.ParameterValues);
                    if (overrides != null)
                    {
                        foreach (KeyValuePair<string, object> kvp in overrides)
                        {
                            int paramIdx = stepType.Parameters.FindIndex(p => p.Name.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase));
                            if (paramIdx < 0)
                            {
                                return Response.Error($"Unknown parameter '{kvp.Key}' for step type '{step.Key}'.");
                            }

                            while (step.Values.Count <= paramIdx) step.Values.Add(new ParameterValue());

                            if (kvp.Value is bool boolVal)
                            {
                                step.Values[paramIdx].boolValue = boolVal;
                            }
                            else if (kvp.Value is long longVal)
                            {
                                step.Values[paramIdx].intValue = (int)longVal;
                            }
                            else if (kvp.Value is int intVal)
                            {
                                step.Values[paramIdx].intValue = intVal;
                            }
                            else
                            {
                                step.Values[paramIdx].stringValue = kvp.Value?.ToString();
                            }
                        }
                    }
                }
                catch (JsonException e)
                {
                    return Response.Error($"Invalid ParameterValues JSON: {e.Message}");
                }
            }

            repo.SaveStep(step);

            return Response.Success($"Step {parameters.StepId} updated.");
        }

        #endregion
    }
}
#endif
