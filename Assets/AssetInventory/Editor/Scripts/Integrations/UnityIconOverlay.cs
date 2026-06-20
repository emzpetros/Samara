using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AssetInventory
{
    [InitializeOnLoad]
    public static class UnityIconOverlay
    {
        // Icon caches
        private static readonly Dictionary<string, Texture2D> _iconCache = new Dictionary<string, Texture2D>();
        
        // Animation manager using shared infrastructure
        private static readonly AnimationPlaybackManager<string> _animationManager = new AnimationPlaybackManager<string>(
            maxConcurrentLoads: 3,
            isEnabledCheck: () => AI.Config.playProjectWindowAnimations,
            maxLoadedCheck: () => AI.Config.maxAnimatedProjectPreviews
        );
        
        // LRU tracking - stores last access time for cache eviction
        private static readonly Dictionary<string, float> _iconLastAccess = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _animationLastAccess = new Dictionary<string, float>();
        
        // Batched DB query support
        private static readonly HashSet<string> _pendingStaticLookups = new HashSet<string>();
        private static readonly HashSet<string> _pendingAnimatedLookups = new HashSet<string>();
        private static readonly HashSet<string> _failedLookups = new HashSet<string>(); // GUIDs with no preview (avoid re-querying)
        
        // Fast skip cache for non-prefab GUIDs to avoid repeated AssetDatabase.GUIDToAssetPath calls
        private static readonly HashSet<string> _nonPrefabGuids = new HashSet<string>();
        
        // Throttling
        private static float _nextRepaintTime;
        private static float _nextCleanupTime;
        private static float _nextBatchQueryTime;
        private const float CLEANUP_INTERVAL = 30f; // Seconds between cache cleanup checks
        private const float STATIC_ICON_TTL = 300f; // 5 minutes before static icons can be evicted
        private const float ANIMATION_TTL = 60f; // 1 minute before animations can be evicted
        private const float BATCH_QUERY_DELAY = 0.1f; // Collect queries for 100ms before executing

        static UnityIconOverlay()
        {
            // Unsubscribe first to prevent accumulation on domain reload
            EditorApplication.projectWindowItemOnGUI -= OnProjectWindowItemGUI;
            EditorApplication.update -= UpdateAnimations;

            EditorApplication.projectWindowItemOnGUI += OnProjectWindowItemGUI;
            EditorApplication.update += UpdateAnimations;
            
            // Subscribe to animation loaded events to track access time
            _animationManager.OnAnimationLoaded += (guid, success) =>
            {
                if (success)
                {
                    _animationLastAccess[guid] = Time.realtimeSinceStartup;
                }
            };
            
            // Initialize timers
            _nextRepaintTime = 0f;
            _nextCleanupTime = Time.realtimeSinceStartup + CLEANUP_INTERVAL;
            _nextBatchQueryTime = 0f;
        }

        private static void OnProjectWindowItemGUI(string guid, Rect selectionRect)
        {
            if (!CanUseDatabaseBackedPreviews()) return;
            if (!AI.Config.overrideProjectPreviews) return;

            // Fast skip for GUIDs we already know aren't prefabs or have no preview
            if (_nonPrefabGuids.Contains(guid)) return;
            if (_failedLookups.Contains(guid)) return;

            string assetPath = AssetDatabase.GUIDToAssetPath(guid);

            if (!assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                _nonPrefabGuids.Add(guid);
                return;
            }

            Texture2D icon = null;

            // Check if animations are enabled and try to get animated preview
            if (AI.Config.playProjectWindowAnimations)
            {
                icon = GetAnimatedIcon(guid);
            }

            // Fall back to static preview if no animation
            if (icon == null)
            {
                icon = GetStaticIcon(guid);
            }

            // Draw the icon over the prefab thumbnail
            if (icon != null)
            {
                Rect iconRect = GetIconRect(selectionRect);

                // Detect if we're in list view mode
                bool isListView = selectionRect.width > selectionRect.height + 10;

                // In list view, draw a background to prevent transparent icons from overlaying the default icon
                if (isListView)
                {
                    // Get the appropriate background color based on the current editor theme
                    Color backgroundColor = EditorGUIUtility.isProSkin
                        ? new Color(0.22f, 0.22f, 0.22f, 1f) // Dark theme
                        : new Color(0.76f, 0.76f, 0.76f, 1f); // Light theme

                    EditorGUI.DrawRect(iconRect, backgroundColor);
                }

                GUI.DrawTexture(iconRect, icon, ScaleMode.StretchToFill);
            }
        }

        private static Texture2D GetStaticIcon(string guid)
        {
            // Try to get the icon from cache
            if (_iconCache.TryGetValue(guid, out Texture2D icon))
            {
                // Update last access time for LRU
                _iconLastAccess[guid] = Time.realtimeSinceStartup;
                return icon;
            }

            // Queue for batch lookup instead of immediate DB query
            if (!_pendingStaticLookups.Contains(guid))
            {
                _pendingStaticLookups.Add(guid);
                
                // Schedule batch query if not already scheduled
                if (_nextBatchQueryTime <= 0f)
                {
                    _nextBatchQueryTime = Time.realtimeSinceStartup + BATCH_QUERY_DELAY;
                }
            }

            return null;
        }

        private static Texture2D GetAnimatedIcon(string guid)
        {
            // Check if we already have an animation loaded for this GUID
            if (_animationManager.IsLoaded(guid))
            {
                // Update last access time for LRU
                _animationLastAccess[guid] = Time.realtimeSinceStartup;
                return _animationManager.GetCurrentFrame(guid);
            }
            
            // Check if currently loading
            if (_animationManager.IsLoading(guid))
            {
                // Still loading, return null (will fall back to static)
                return null;
            }

            // Check if we're at the animation limit
            if (_animationManager.LoadedCount >= AI.Config.maxAnimatedProjectPreviews)
            {
                // Fall back to static preview
                return null;
            }

            // Queue for batch lookup instead of immediate DB query
            if (!_pendingAnimatedLookups.Contains(guid) && !_pendingStaticLookups.Contains(guid))
            {
                _pendingAnimatedLookups.Add(guid);
                
                // Schedule batch query if not already scheduled
                if (_nextBatchQueryTime <= 0f)
                {
                    _nextBatchQueryTime = Time.realtimeSinceStartup + BATCH_QUERY_DELAY;
                }
            }

            return null;
        }
        
        private static void ProcessBatchQueries()
        {
            if (!CanUseDatabaseBackedPreviews())
            {
                _pendingStaticLookups.Clear();
                _pendingAnimatedLookups.Clear();
                _nextBatchQueryTime = 0f;
                return;
            }
            if (_pendingStaticLookups.Count == 0 && _pendingAnimatedLookups.Count == 0) return;
            
            // Combine all pending GUIDs for a single batch query
            HashSet<string> allPendingGuids = new HashSet<string>(_pendingStaticLookups);
            allPendingGuids.UnionWith(_pendingAnimatedLookups);
            
            if (allPendingGuids.Count == 0) return;
            
            // Build batch query with IN clause
            string guidList = string.Join(",", allPendingGuids.Select(g => $"'{g}'"));
            string query = $"SELECT *, AssetFile.Id as Id FROM AssetFile WHERE Guid IN ({guidList}) AND PreviewState = ?";
            
            List<AssetInfo> results = DBAdapter.DB.Query<AssetInfo>(query, AssetFile.PreviewOptions.Custom);
            
            // Create lookup for quick access (GroupBy to handle duplicate GUIDs across packages)
            Dictionary<string, AssetInfo> resultsByGuid = results.GroupBy(r => r.Guid).ToDictionary(g => g.Key, g => g.First());
            
            string previewFolder = Paths.GetPreviewFolder();
            float now = Time.realtimeSinceStartup;
            
            // Process animated lookups first (they take priority)
            foreach (string guid in _pendingAnimatedLookups)
            {
                if (!resultsByGuid.TryGetValue(guid, out AssetInfo info))
                {
                    // No result for this GUID, mark as failed
                    _failedLookups.Add(guid);
                    continue;
                }
                
                // Check if animated preview exists
                string animPreviewFile = info.GetPreviewFile(previewFolder, true);
                if (!File.Exists(animPreviewFile))
                {
                    // No animated preview, will fall back to static
                    // Move to static pending if not already there
                    if (!_iconCache.ContainsKey(guid))
                    {
                        LoadStaticIcon(guid, info, previewFolder, now);
                    }
                    continue;
                }
                
                // Queue animation load via manager (handles throttling and limits)
                _animationManager.QueueAnimation(guid, info);
            }
            
            // Process static lookups
            foreach (string guid in _pendingStaticLookups)
            {
                // Skip if already processed for animation
                if (_animationManager.IsActive(guid) || _iconCache.ContainsKey(guid)) continue;
                
                if (!resultsByGuid.TryGetValue(guid, out AssetInfo info))
                {
                    // No result for this GUID, mark as failed
                    _failedLookups.Add(guid);
                    continue;
                }
                
                LoadStaticIcon(guid, info, previewFolder, now);
            }
            
            _pendingStaticLookups.Clear();
            _pendingAnimatedLookups.Clear();
            _nextBatchQueryTime = 0f;
        }
        
        private static void LoadStaticIcon(string guid, AssetInfo info, string previewFolder, float accessTime)
        {
            string previewFile = info.GetPreviewFile(previewFolder);
            if (!File.Exists(previewFile))
            {
                _failedLookups.Add(guid);
                return;
            }
            
            // Load the preview as the icon
            Texture2D icon = new Texture2D(2, 2);
            byte[] fileData = File.ReadAllBytes(previewFile);
            if (icon.LoadImage(fileData))
            {
                _iconCache[guid] = icon;
                _iconLastAccess[guid] = accessTime;
            }
            else
            {
                Object.DestroyImmediate(icon);
                _failedLookups.Add(guid);
            }
        }
        
        private static void CleanupOldCacheEntries()
        {
            float now = Time.realtimeSinceStartup;
            
            // Clean up old static icons
            List<string> iconsToRemove = new List<string>();
            foreach (KeyValuePair<string, float> kvp in _iconLastAccess)
            {
                if (now - kvp.Value > STATIC_ICON_TTL)
                {
                    iconsToRemove.Add(kvp.Key);
                }
            }
            
            foreach (string guid in iconsToRemove)
            {
                if (_iconCache.TryGetValue(guid, out Texture2D texture))
                {
                    if (texture != null) Object.DestroyImmediate(texture);
                    _iconCache.Remove(guid);
                }
                _iconLastAccess.Remove(guid);
            }
            
            // Clean up old animations (more aggressively since they use more memory)
            List<string> animationsToRemove = new List<string>();
            foreach (KeyValuePair<string, float> kvp in _animationLastAccess)
            {
                if (now - kvp.Value > ANIMATION_TTL)
                {
                    animationsToRemove.Add(kvp.Key);
                }
            }
            
            foreach (string guid in animationsToRemove)
            {
                _animationManager.DisposeAnimation(guid);
                _animationLastAccess.Remove(guid);
            }
        }

        private static void UpdateAnimations()
        {
            if (!CanUseDatabaseBackedPreviews()) return;

            float now = Time.realtimeSinceStartup;
            
            // Process batch queries if scheduled
            if (_nextBatchQueryTime > 0f && now >= _nextBatchQueryTime)
            {
                ProcessBatchQueries();
            }
            
            // Process animation load queue via manager
            _animationManager.ProcessQueue();
            
            // Periodic cache cleanup
            if (now >= _nextCleanupTime)
            {
                CleanupOldCacheEntries();
                _nextCleanupTime = now + CLEANUP_INTERVAL;
            }
            
            // Only trigger continuous repaints when animations are actively playing
            if (AI.Config.overrideProjectPreviews
                && AI.Config.playProjectWindowAnimations
                && _animationManager.LoadedCount > 0
                && now >= _nextRepaintTime)
            {
                _nextRepaintTime = now + AI.Config.animationSpeed;
                EditorApplication.RepaintProjectWindow();
            }
        }

        private static bool CanUseDatabaseBackedPreviews()
        {
            return (IndexUI.HasOpenInstances || AI.Config.forceInitOnDomainReload) && AI.IsInitialized && DBAdapter.IsDBOpen();
        }

        private static Rect GetIconRect(Rect selectionRect)
        {
            // Detect view mode based on rect dimensions
            bool isListView = selectionRect.width > selectionRect.height + 10; // List view is wide and short

            if (isListView)
            {
                // List view: icon is on the left side, square, matching the row height
                float iconSize = EditorGUIUtility.singleLineHeight - 2f;
                return new Rect(selectionRect.x + 1f, selectionRect.y, iconSize, iconSize);
            }
            else
            {
                // Icon/Grid view: icon is at the top, label is at the bottom
                // Unity uses 2 lines of text for the label (asset name can wrap)
                float labelHeight = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
                float iconHeight = selectionRect.height - labelHeight;

                // Make it square, centered horizontally if needed
                float iconSize = Mathf.Min(iconHeight, selectionRect.width - 4); // 4px total padding
                float xOffset = (selectionRect.width - iconSize) * 0.5f - 1f;

                return new Rect(
                    selectionRect.x + xOffset,
                    selectionRect.y + xOffset,
                    iconSize + 2f,
                    iconSize + 2f
                );
            }
        }

        // Method to clear cache when previews are regenerated
        public static void ClearCache()
        {
            foreach (Texture2D texture in _iconCache.Values)
            {
                if (texture != null) Object.DestroyImmediate(texture);
            }
            _iconCache.Clear();
            _iconLastAccess.Clear();

            _animationManager.DisposeAll();
            _animationLastAccess.Clear();
            
            _pendingStaticLookups.Clear();
            _pendingAnimatedLookups.Clear();
            _failedLookups.Clear();
            _nonPrefabGuids.Clear();
            _nextBatchQueryTime = 0f;
        }

        // Method to clear specific icon from cache
        public static void ClearIcon(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;

            if (_iconCache.TryGetValue(guid, out Texture2D texture))
            {
                if (texture != null) Object.DestroyImmediate(texture);
                _iconCache.Remove(guid);
            }
            _iconLastAccess.Remove(guid);

            _animationManager.DisposeAnimation(guid);
            _animationLastAccess.Remove(guid);
            
            _failedLookups.Remove(guid);
            _nonPrefabGuids.Remove(guid);
            _pendingStaticLookups.Remove(guid);
            _pendingAnimatedLookups.Remove(guid);
        }
    }
}
