using System;
using System.Collections.Generic;
using System.Linq;
using Automator;
using Newtonsoft.Json;

namespace AssetInventory
{
    /// <summary>
    /// Implementation of IActionRepository that uses AssetInventory's SQLite database
    /// via CustomAction and CustomActionStep tables.
    /// </summary>
    [Serializable]
    public sealed class SqliteActionRepository : IActionRepository
    {
        public List<ActionDefinition> GetAllActions()
        {
            List<CustomAction> actions = DBAdapter.DB.Table<CustomAction>().ToList();
            return actions.Select(ToActionDefinition).ToList();
        }

        public ActionDefinition GetAction(int id)
        {
            CustomAction action = DBAdapter.DB.Find<CustomAction>(id);
            return action != null ? ToActionDefinition(action) : null;
        }

        public ActionDefinition GetActionByName(string name)
        {
            CustomAction action = DBAdapter.DB.Table<CustomAction>()
                .FirstOrDefault(a => a.Name == name);
            return action != null ? ToActionDefinition(action) : null;
        }

        public ActionDefinition SaveAction(ActionDefinition action)
        {
            CustomAction ca = ToCustomAction(action);
            
            if (ca.Id > 0)
            {
                DBAdapter.DB.Update(ca);
            }
            else
            {
                DBAdapter.DB.Insert(ca);
            }
            
            action.Id = ca.Id;
            return action;
        }

        public void DeleteAction(int id)
        {
            // Delete all steps first
            DBAdapter.DB.Execute("DELETE FROM CustomActionStep WHERE ActionId = ?", id);
            
            // Delete the action
            DBAdapter.DB.Delete<CustomAction>(id);
        }

        public List<ActionStepDefinition> GetSteps(int actionId)
        {
            List<CustomActionStep> steps = DBAdapter.DB.Query<CustomActionStep>(
                "SELECT * FROM CustomActionStep WHERE ActionId = ? ORDER BY OrderIdx", actionId);
            
            return steps.Select(ToActionStepDefinition).ToList();
        }

        public ActionStepDefinition SaveStep(ActionStepDefinition step)
        {
            CustomActionStep cas = ToCustomActionStep(step);
            
            if (cas.Id > 0)
            {
                DBAdapter.DB.Update(cas);
            }
            else
            {
                DBAdapter.DB.Insert(cas);
            }
            
            step.Id = cas.Id;
            return step;
        }

        public void DeleteStep(int id)
        {
            DBAdapter.DB.Delete<CustomActionStep>(id);
        }

        public void DeleteStepsExcept(int actionId, List<int> keepStepIds)
        {
            if (keepStepIds == null || keepStepIds.Count == 0)
            {
                DBAdapter.DB.Execute("DELETE FROM CustomActionStep WHERE ActionId = ?", actionId);
            }
            else
            {
                string ids = string.Join(",", keepStepIds);
                DBAdapter.DB.Execute($"DELETE FROM CustomActionStep WHERE ActionId = ? AND Id NOT IN ({ids})", actionId);
            }
        }

        public void Save()
        {
            // SQLite operations are auto-committed, no explicit save needed
        }

        #region Conversion Methods

        private static ActionDefinition ToActionDefinition(CustomAction ca)
        {
            return new ActionDefinition
            {
                Id = ca.Id,
                Name = ca.Name,
                Description = ca.Description,
                StopOnFailure = ca.StopOnFailure,
                Mode = (ActionDefinition.RunMode)(int)ca.RunMode
            };
        }

        private static CustomAction ToCustomAction(ActionDefinition action)
        {
            return new CustomAction
            {
                Id = action.Id,
                Name = action.Name,
                Description = action.Description,
                StopOnFailure = action.StopOnFailure,
                RunMode = (CustomAction.Mode)(int)action.Mode
            };
        }

        private static ActionStepDefinition ToActionStepDefinition(CustomActionStep cas)
        {
            List<ParameterValue> values = new List<ParameterValue>();
            if (!string.IsNullOrEmpty(cas.Params))
            {
                values = JsonConvert.DeserializeObject<List<ParameterValue>>(cas.Params) ?? new List<ParameterValue>();
            }

            return new ActionStepDefinition
            {
                Id = cas.Id,
                ActionId = cas.ActionId,
                Key = cas.Key,
                OrderIndex = cas.OrderIdx,
                Values = values
            };
        }

        private static CustomActionStep ToCustomActionStep(ActionStepDefinition step)
        {
            JsonSerializerSettings settings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Ignore,
                Formatting = Formatting.None
            };

            return new CustomActionStep
            {
                Id = step.Id,
                ActionId = step.ActionId,
                Key = step.Key,
                OrderIdx = step.OrderIndex,
                Params = JsonConvert.SerializeObject(step.Values, settings)
            };
        }

        #endregion
    }
}
