namespace Automator
{
    /// <summary>
    /// Interface for action runner context that allows storing and retrieving
    /// data during action execution. Contexts are scoped to a single action run.
    /// </summary>
    public interface IActionRunnerContext
    {
        /// <summary>
        /// Gets a value from the context.
        /// </summary>
        /// <typeparam name="T">The type of value to retrieve.</typeparam>
        /// <param name="key">The key for the value.</param>
        /// <returns>The value if found, default(T) otherwise.</returns>
        T Get<T>(string key);

        /// <summary>
        /// Gets a value from the context with a default fallback.
        /// </summary>
        /// <typeparam name="T">The type of value to retrieve.</typeparam>
        /// <param name="key">The key for the value.</param>
        /// <param name="defaultValue">The default value if key is not found.</param>
        /// <returns>The value if found, defaultValue otherwise.</returns>
        T Get<T>(string key, T defaultValue);

        /// <summary>
        /// Sets a value in the context.
        /// </summary>
        /// <typeparam name="T">The type of value to store.</typeparam>
        /// <param name="key">The key for the value.</param>
        /// <param name="value">The value to store.</param>
        void Set<T>(string key, T value);

        /// <summary>
        /// Checks if a key exists in the context.
        /// </summary>
        /// <param name="key">The key to check.</param>
        /// <returns>True if the key exists, false otherwise.</returns>
        bool Has(string key);

        /// <summary>
        /// Removes a value from the context.
        /// </summary>
        /// <param name="key">The key to remove.</param>
        void Remove(string key);

        /// <summary>
        /// Clears all values from the context.
        /// </summary>
        void Clear();
    }

    /// <summary>
    /// Factory delegate for creating action runner contexts.
    /// </summary>
    public delegate IActionRunnerContext ActionRunnerContextFactory();
}