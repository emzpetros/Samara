#if ASSET_INVENTORY_MCP
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Automator;
using Unity.AI.MCP.Editor.Helpers;
using Unity.AI.MCP.Editor.ToolRegistry;

namespace AssetInventory
{
    public static class ActionTools
    {
        #region List Actions

        public class ListActionsParams
        {
            [McpDescription("Filter actions by name.")]
            public string SearchPhrase { get; set; }
        }

        [McpTool("AssetInventory_listActions",
            "List all custom automation actions. Use getActionDetails to see steps.",
            Groups = new[] {"Asset Inventory/Actions"})]
        public static object ListActions(ListActionsParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            SqliteActionRepository repo = new SqliteActionRepository();
            List<ActionDefinition> actions = repo.GetAllActions();

            if (!string.IsNullOrEmpty(parameters.SearchPhrase))
            {
                actions = actions.Where(a => a.Name != null && a.Name.IndexOf(parameters.SearchPhrase, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }

            return Response.Success($"Found {actions.Count} actions.", new
            {
                actions = actions.Select(a =>
                {
                    List<ActionStepDefinition> steps = repo.GetSteps(a.Id);
                    return new
                    {
                        id = a.Id,
                        name = a.Name,
                        description = a.Description,
                        stopOnFailure = a.StopOnFailure,
                        runMode = a.Mode.ToString(),
                        stepCount = steps.Count
                    };
                }).ToArray()
            });
        }

        #endregion

        #region Get Action Details

        public class GetActionDetailsParams
        {
            [McpDescription("Action ID.", Required = true)]
            public int ActionId { get; set; }
        }

        [McpTool("AssetInventory_getActionDetails",
            "Get action details with all steps, types, and parameter values.",
            Groups = new[] {"Asset Inventory/Actions"})]
        public static object GetActionDetails(GetActionDetailsParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            SqliteActionRepository repo = new SqliteActionRepository();
            ActionDefinition action = repo.GetAction(parameters.ActionId);
            if (action == null)
            {
                return Response.Error($"Action with ID {parameters.ActionId} not found.");
            }

            List<ActionStepDefinition> steps = repo.GetSteps(parameters.ActionId);

            return Response.Success($"Action '{action.Name}' details retrieved.", new
            {
                action = new
                {
                    id = action.Id,
                    name = action.Name,
                    description = action.Description,
                    stopOnFailure = action.StopOnFailure,
                    runMode = action.Mode.ToString(),
                    steps = steps.Select(s =>
                    {
                        ActionStep stepType = ActionStepRegistry.GetStep(s.Key);
                        return new
                        {
                            id = s.Id,
                            key = s.Key,
                            orderIndex = s.OrderIndex,
                            stepName = stepType?.Name,
                            stepDescription = stepType?.Description,
                            category = stepType?.Category.ToString(),
                            parameterValues = s.Values?.Select((v, idx) =>
                            {
                                StepParameter param = stepType != null && idx < stepType.Parameters.Count ? stepType.Parameters[idx] : null;
                                return new
                                {
                                    name = param?.Name,
                                    stringValue = v.stringValue,
                                    intValue = v.intValue,
                                    boolValue = v.boolValue
                                };
                            }).ToArray()
                        };
                    }).ToArray()
                }
            });
        }

        #endregion

        #region Create Action

        public class CreateActionParams
        {
            [McpDescription("Action name.", Required = true)]
            public string Name { get; set; }

            [McpDescription("Description of what the action does.")]
            public string Description { get; set; }

            [McpDescription("Stop on step failure.", Default = true)]
            public bool StopOnFailure { get; set; } = true;

            [McpDescription("'Manual' or 'AtInstallation'.", Default = "Manual")]
            public string RunMode { get; set; } = "Manual";
        }

        [McpTool("AssetInventory_createAction",
            "Create a new action. Add steps with addActionStep after creation.",
            Groups = new[] {"Asset Inventory/Actions"})]
        public static object CreateAction(CreateActionParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            if (string.IsNullOrEmpty(parameters.Name))
            {
                return Response.Error("Name is required.");
            }

            ActionDefinition.RunMode mode = ActionDefinition.RunMode.Manual;
            if (!string.IsNullOrEmpty(parameters.RunMode))
            {
                if (!Enum.TryParse(parameters.RunMode, true, out mode))
                {
                    return Response.Error($"Invalid RunMode '{parameters.RunMode}'. Use 'Manual' or 'AtInstallation'.");
                }
            }

            SqliteActionRepository repo = new SqliteActionRepository();
            ActionDefinition action = new ActionDefinition
            {
                Name = parameters.Name,
                Description = parameters.Description,
                StopOnFailure = parameters.StopOnFailure,
                Mode = mode
            };

            action = repo.SaveAction(action);
            AI.Actions.Init(true);

            return Response.Success($"Action '{parameters.Name}' created.", new { actionId = action.Id });
        }

        #endregion

        #region Update Action

        public class UpdateActionParams
        {
            [McpDescription("Action ID.", Required = true)]
            public int ActionId { get; set; }

            [McpDescription("New name. Empty keeps current.")]
            public string Name { get; set; }

            [McpDescription("New description. Null keeps current.")]
            public string Description { get; set; }

            [McpDescription("Stop execution on step failure: 1=stop, 0=continue, -1=keep current value unchanged.", Default = -1)]
            public int StopOnFailure { get; set; } = -1;

            [McpDescription("'Manual' or 'AtInstallation'. Empty keeps current.")]
            public string RunMode { get; set; }
        }

        [McpTool("AssetInventory_updateAction",
            "Update action properties. Only specified fields change.",
            Groups = new[] {"Asset Inventory/Actions"})]
        public static object UpdateAction(UpdateActionParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            SqliteActionRepository repo = new SqliteActionRepository();
            ActionDefinition action = repo.GetAction(parameters.ActionId);
            if (action == null)
            {
                return Response.Error($"Action with ID {parameters.ActionId} not found.");
            }

            if (!string.IsNullOrEmpty(parameters.Name))
            {
                action.Name = parameters.Name;
            }

            if (parameters.Description != null)
            {
                action.Description = parameters.Description;
            }

            if (parameters.StopOnFailure >= 0)
            {
                action.StopOnFailure = parameters.StopOnFailure != 0;
            }

            if (!string.IsNullOrEmpty(parameters.RunMode))
            {
                if (!Enum.TryParse(parameters.RunMode, true, out ActionDefinition.RunMode mode))
                {
                    return Response.Error($"Invalid RunMode '{parameters.RunMode}'. Use 'Manual' or 'AtInstallation'.");
                }
                action.Mode = mode;
            }

            repo.SaveAction(action);
            AI.Actions.Init(true);

            return Response.Success($"Action '{action.Name}' updated.");
        }

        #endregion

        #region Delete Action

        public class DeleteActionParams
        {
            [McpDescription("Action ID.", Required = true)]
            public int ActionId { get; set; }
        }

        [McpTool("AssetInventory_deleteAction",
            "Delete an action and all its steps.",
            Groups = new[] {"Asset Inventory/Actions"})]
        public static object DeleteAction(DeleteActionParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            SqliteActionRepository repo = new SqliteActionRepository();
            ActionDefinition action = repo.GetAction(parameters.ActionId);
            if (action == null)
            {
                return Response.Error($"Action with ID {parameters.ActionId} not found.");
            }

            string name = action.Name;
            repo.DeleteAction(parameters.ActionId);
            AI.Actions.Init(true);

            return Response.Success($"Action '{name}' deleted.");
        }

        #endregion

        #region Run Action

        public class RunActionParams
        {
            [McpDescription("Action ID.", Required = true)]
            public int ActionId { get; set; }
        }

        [McpTool("AssetInventory_runAction",
            "Execute an action. Steps run in order; stops on failure if configured.",
            Groups = new[] {"Asset Inventory/Actions"})]
        public static async Task<object> RunAction(RunActionParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            SqliteActionRepository repo = new SqliteActionRepository();
            ActionDefinition action = repo.GetAction(parameters.ActionId);
            if (action == null)
            {
                return Response.Error($"Action with ID {parameters.ActionId} not found.");
            }

            try
            {
                ActionRunner runner = new ActionRunner(repo);
                ActionRunResult result = await runner.RunAction(parameters.ActionId);

                if (result.Success)
                {
                    return Response.Success($"Action '{action.Name}' completed successfully.", new
                    {
                        stepsExecuted = result.StepsExecuted,
                        stepsFailed = result.StepsFailed
                    });
                }
                return Response.Error(result.Error ?? $"Action '{action.Name}' failed.", new
                {
                    stepsExecuted = result.StepsExecuted,
                    stepsFailed = result.StepsFailed
                });
            }
            catch (Exception e)
            {
                return Response.Error($"Error running action '{action.Name}': {e.Message}");
            }
        }

        #endregion
    }
}
#endif
