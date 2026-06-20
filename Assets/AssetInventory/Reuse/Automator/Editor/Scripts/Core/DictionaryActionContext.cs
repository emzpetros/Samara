using System.Collections.Generic;

namespace Automator
{
    /// <summary>
    /// Default implementation of IActionRunnerContext using a Dictionary for storage.
    /// </summary>
    public class DictionaryActionContext : IActionRunnerContext
    {
        private readonly Dictionary<string, object> _data = new Dictionary<string, object>();

        public T Get<T>(string key)
        {
            return Get<T>(key, default);
        }

        public T Get<T>(string key, T defaultValue)
        {
            if (_data.TryGetValue(key, out object value) && value is T typedValue)
            {
                return typedValue;
            }
            return defaultValue;
        }

        public void Set<T>(string key, T value)
        {
            _data[key] = value;
        }

        public bool Has(string key)
        {
            return _data.ContainsKey(key);
        }

        public void Remove(string key)
        {
            _data.Remove(key);
        }

        public void Clear()
        {
            _data.Clear();
        }
    }
}
