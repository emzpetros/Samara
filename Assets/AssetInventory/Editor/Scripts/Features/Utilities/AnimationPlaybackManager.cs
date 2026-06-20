using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace AssetInventory
{
    /// <summary>
    /// Generic manager for handling multiple concurrent animation playbacks.
    /// Used by both Search grid and Project View overlay for animated preview playback.
    /// </summary>
    /// <typeparam name="TKey">The type used to identify animations (e.g., int for tile index, string for GUID)</typeparam>
    public class AnimationPlaybackManager<TKey>
    {
        private readonly Dictionary<TKey, AnimationPlayer> _players = new Dictionary<TKey, AnimationPlayer>();
        private readonly HashSet<TKey> _activelyLoading = new HashSet<TKey>();
        private readonly Queue<(TKey key, AssetInfo info)> _loadQueue = new Queue<(TKey, AssetInfo)>();
        
        private readonly int _maxConcurrentLoads;
        private readonly Func<bool> _isEnabledCheck;
        private readonly Func<int> _maxLoadedCheck; // Optional: returns max allowed loaded animations
        
        /// <summary>
        /// Event fired when an animation finishes loading (success or failure).
        /// Parameters: key, success
        /// </summary>
        public event Action<TKey, bool> OnAnimationLoaded;
        
        /// <summary>
        /// Event fired when an animation is disposed.
        /// Parameter: key
        /// </summary>
        public event Action<TKey> OnAnimationDisposed;

        /// <summary>
        /// Number of currently loaded and playing animations.
        /// </summary>
        public int LoadedCount => _players.Count;
        
        /// <summary>
        /// Number of animations currently being loaded.
        /// </summary>
        public int LoadingCount => _activelyLoading.Count;
        
        /// <summary>
        /// Total count of loaded + loading animations.
        /// </summary>
        public int TotalActiveCount => _players.Count + _activelyLoading.Count;
        
        /// <summary>
        /// Access to loaded animation player keys for iteration.
        /// </summary>
        public IEnumerable<TKey> LoadedKeys => _players.Keys;

        /// <summary>
        /// Creates a new AnimationPlaybackManager.
        /// </summary>
        /// <param name="maxConcurrentLoads">Maximum number of animations to load simultaneously.</param>
        /// <param name="isEnabledCheck">Optional function to check if animation playback is still enabled. 
        /// If provided and returns false, loading animations will be discarded.</param>
        /// <param name="maxLoadedCheck">Optional function that returns the maximum number of loaded animations allowed.
        /// If provided, QueueAnimation will return false when at the limit.</param>
        public AnimationPlaybackManager(int maxConcurrentLoads = 3, Func<bool> isEnabledCheck = null, Func<int> maxLoadedCheck = null)
        {
            _maxConcurrentLoads = maxConcurrentLoads;
            _isEnabledCheck = isEnabledCheck;
            _maxLoadedCheck = maxLoadedCheck;
        }

        /// <summary>
        /// Checks if an animation is loaded for the given key.
        /// </summary>
        public bool IsLoaded(TKey key)
        {
            return _players.ContainsKey(key);
        }
        
        /// <summary>
        /// Checks if an animation is currently being loaded for the given key.
        /// </summary>
        public bool IsLoading(TKey key)
        {
            return _activelyLoading.Contains(key);
        }
        
        /// <summary>
        /// Checks if an animation is loaded or loading for the given key.
        /// </summary>
        public bool IsActive(TKey key)
        {
            return _players.ContainsKey(key) || _activelyLoading.Contains(key);
        }

        /// <summary>
        /// Gets the animation player for a key if loaded.
        /// </summary>
        public bool TryGetPlayer(TKey key, out AnimationPlayer player)
        {
            return _players.TryGetValue(key, out player);
        }
        
        /// <summary>
        /// Gets the current frame texture for an animation if loaded.
        /// </summary>
        public UnityEngine.Texture2D GetCurrentFrame(TKey key)
        {
            if (_players.TryGetValue(key, out AnimationPlayer player) && player.IsLoaded)
            {
                return player.GetCurrentFrame();
            }
            return null;
        }

        /// <summary>
        /// Queues an animation for loading if not already loaded or loading.
        /// </summary>
        /// <param name="key">The key to identify this animation.</param>
        /// <param name="info">The AssetInfo for the animation.</param>
        /// <returns>True if queued, false if already loaded/loading or at max capacity.</returns>
        public bool QueueAnimation(TKey key, AssetInfo info)
        {
            if (_players.ContainsKey(key)) return false;
            if (_activelyLoading.Contains(key)) return false;
            
            // Check max loaded limit if configured
            if (_maxLoadedCheck != null)
            {
                int maxLoaded = _maxLoadedCheck();
                if (_players.Count >= maxLoaded) return false;
            }
            
            _loadQueue.Enqueue((key, info));
            return true;
        }

        /// <summary>
        /// Clears the load queue. Useful when scroll position changes significantly.
        /// Does not affect currently loading or loaded animations.
        /// </summary>
        public void ClearQueue()
        {
            _loadQueue.Clear();
        }
        
        /// <summary>
        /// Marks a key as no longer needed for loading.
        /// If the animation is currently loading, it will be discarded when complete.
        /// </summary>
        public void CancelLoading(TKey key)
        {
            _activelyLoading.Remove(key);
        }

        /// <summary>
        /// Processes the load queue, starting new loads up to the concurrent limit.
        /// Should be called regularly (e.g., from Update loop).
        /// </summary>
        public void ProcessQueue()
        {
            while (_activelyLoading.Count < _maxConcurrentLoads && _loadQueue.Count > 0)
            {
                (TKey key, AssetInfo info) = _loadQueue.Dequeue();
                
                // Skip if already loaded or loading
                if (_players.ContainsKey(key)) continue;
                if (_activelyLoading.Contains(key)) continue;
                
                _activelyLoading.Add(key);
                _ = LoadAnimationAsync(key, info);
            }
        }

        private async Task LoadAnimationAsync(TKey key, AssetInfo info)
        {
            AnimationPlayer player = null;
            bool success = false;
            
            try
            {
                player = new AnimationPlayer(info.Guid);
                success = await player.LoadAnimation(info, Paths.GetPreviewFolder());

                // Check if still needed after async load
                bool stillNeeded = _activelyLoading.Contains(key);
                bool stillEnabled = _isEnabledCheck?.Invoke() ?? true;
                
                if (!stillNeeded || !stillEnabled)
                {
                    player.Dispose();
                    return;
                }

                if (success)
                {
                    _players[key] = player;
                }
                else
                {
                    player.Dispose();
                }
            }
            catch
            {
                player?.Dispose();
                success = false;
            }
            finally
            {
                _activelyLoading.Remove(key);
                OnAnimationLoaded?.Invoke(key, success);
                
                // Process next queued animation
                ProcessQueue();
            }
        }

        /// <summary>
        /// Disposes a single animation by key.
        /// </summary>
        public void DisposeAnimation(TKey key)
        {
            if (_players.TryGetValue(key, out AnimationPlayer player))
            {
                player.Dispose();
                _players.Remove(key);
            }
            _activelyLoading.Remove(key);
            OnAnimationDisposed?.Invoke(key);
        }

        /// <summary>
        /// Disposes all animations and clears the queue.
        /// </summary>
        /// <returns>List of keys that were disposed (useful for restoring static previews).</returns>
        public List<TKey> DisposeAll()
        {
            List<TKey> disposedKeys = new List<TKey>(_players.Keys);
            
            foreach (AnimationPlayer player in _players.Values)
            {
                player.Dispose();
            }
            _players.Clear();
            _activelyLoading.Clear();
            _loadQueue.Clear();
            
            return disposedKeys;
        }

        /// <summary>
        /// Updates which animations should be active based on a visibility check.
        /// Disposes animations that are no longer visible and queues new ones.
        /// </summary>
        /// <param name="isVisible">Function to check if a key should still be visible/active.</param>
        /// <param name="onDispose">Optional callback when an animation is disposed for visibility reasons.</param>
        public void UpdateVisibility(Func<TKey, bool> isVisible, Action<TKey> onDispose = null)
        {
            // Find and dispose animations that are no longer visible
            List<TKey> toRemove = new List<TKey>();
            foreach (TKey key in _players.Keys)
            {
                if (!isVisible(key))
                {
                    toRemove.Add(key);
                }
            }
            
            foreach (TKey key in toRemove)
            {
                if (_players.TryGetValue(key, out AnimationPlayer player))
                {
                    player.Dispose();
                    _players.Remove(key);
                    onDispose?.Invoke(key);
                }
            }
            
            // Also mark loading animations that are no longer visible
            List<TKey> loadingToRemove = new List<TKey>();
            foreach (TKey key in _activelyLoading)
            {
                if (!isVisible(key))
                {
                    loadingToRemove.Add(key);
                }
            }
            foreach (TKey key in loadingToRemove)
            {
                _activelyLoading.Remove(key);
            }
        }

        /// <summary>
        /// Checks if a preview file exists for the given AssetInfo.
        /// </summary>
        public static bool HasAnimatedPreview(AssetInfo info)
        {
            if (info == null) return false;
            string animPreviewFile = info.GetPreviewFile(Paths.GetPreviewFolder(), true);
            return File.Exists(animPreviewFile);
        }
    }
}
