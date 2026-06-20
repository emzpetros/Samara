using System;

namespace ImpossibleRobert.Common
{
    public class TimedCache<T>
    {
        private T _cachedValue;
        private DateTime? _expiryTime;

        public void SetValue(T value, TimeSpan ttl)
        {
            _cachedValue = value;
            _expiryTime = DateTime.UtcNow.Add(ttl);
        }

        public bool TryGetValue(out T value)
        {
            if (_expiryTime.HasValue && DateTime.UtcNow <= _expiryTime.Value)
            {
                value = _cachedValue;
                return true;
            }

            value = default(T);
            _expiryTime = null;
            return false;
        }

        public void Clear()
        {
            _cachedValue = default(T);
            _expiryTime = null;
        }
    }
}
