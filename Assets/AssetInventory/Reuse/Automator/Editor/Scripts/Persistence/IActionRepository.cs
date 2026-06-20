using System.Collections.Generic;

namespace Automator
{
    /// <summary>
    /// Interface for persisting and retrieving action definitions.
    /// Implement this interface to support different storage backends (JSON, SQLite, etc.).
    /// </summary>
    public interface IActionRepository
    {
        /// <summary>
        /// Gets all action definitions.
        /// </summary>
        /// <returns>List of all actions</returns>
        List<ActionDefinition> GetAllActions();

        /// <summary>
        /// Gets a single action by ID.
        /// </summary>
        /// <param name="id">The action ID</param>
        /// <returns>The action, or null if not found</returns>
        ActionDefinition GetAction(int id);

        /// <summary>
        /// Gets a single action by name.
        /// </summary>
        /// <param name="name">The action name</param>
        /// <returns>The action, or null if not found</returns>
        ActionDefinition GetActionByName(string name);

        /// <summary>
        /// Saves an action (insert or update).
        /// </summary>
        /// <param name="action">The action to save</param>
        /// <returns>The saved action with updated ID if inserted</returns>
        ActionDefinition SaveAction(ActionDefinition action);

        /// <summary>
        /// Deletes an action and its steps.
        /// </summary>
        /// <param name="id">The action ID to delete</param>
        void DeleteAction(int id);

        /// <summary>
        /// Gets all steps for an action.
        /// </summary>
        /// <param name="actionId">The action ID</param>
        /// <returns>List of steps ordered by OrderIndex</returns>
        List<ActionStepDefinition> GetSteps(int actionId);

        /// <summary>
        /// Saves a step (insert or update).
        /// </summary>
        /// <param name="step">The step to save</param>
        /// <returns>The saved step with updated ID if inserted</returns>
        ActionStepDefinition SaveStep(ActionStepDefinition step);

        /// <summary>
        /// Deletes a step.
        /// </summary>
        /// <param name="id">The step ID to delete</param>
        void DeleteStep(int id);

        /// <summary>
        /// Deletes all steps for an action that are not in the given list of IDs.
        /// </summary>
        /// <param name="actionId">The action ID</param>
        /// <param name="keepStepIds">Step IDs to keep</param>
        void DeleteStepsExcept(int actionId, List<int> keepStepIds);

        /// <summary>
        /// Persists any pending changes (for file-based repositories).
        /// </summary>
        void Save();
    }
}