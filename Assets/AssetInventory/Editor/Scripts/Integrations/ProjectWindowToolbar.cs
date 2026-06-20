#if !ASSET_INVENTORY_HIDE_PROJECT_TOOLBAR
using System;
using System.Collections.Generic;
using ImpossibleRobert.Common;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace AssetInventory
{
    [InitializeOnLoad]
    public static class ProjectWindowToolbar
    {
        private static readonly Dictionary<int, VisualElement> _injectedToolbars = new Dictionary<int, VisualElement>();
        private static Type _projectBrowserType;
        private static readonly string ToolbarClassName = "asset-inventory-project-toolbar";

        private const double POLL_INTERVAL = 3.0;
        private static double _nextPollTime;

        private static bool _lastOverrideProjectPreviews;
        private static bool _lastPlayAnimations;
        private static bool _configInitialized;

        private static readonly List<EditorWindow> _cachedBrowsers = new List<EditorWindow>();

        static ProjectWindowToolbar()
        {
            _projectBrowserType = Type.GetType("UnityEditor.ProjectBrowser,UnityEditor");
            if (_projectBrowserType == null)
            {
                Debug.LogWarning("Asset Inventory: Could not find ProjectBrowser type for toolbar integration.");
                return;
            }

            _nextPollTime = 0;

            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;
        }

        private static void OnEditorUpdate()
        {
            if (_projectBrowserType == null) return;

            double now = EditorApplication.timeSinceStartup;

            bool configChanged = false;
            if (_configInitialized)
            {
                bool curOverride = AI.Config.overrideProjectPreviews;
                bool curPlay = AI.Config.playProjectWindowAnimations;
                if (curOverride != _lastOverrideProjectPreviews || curPlay != _lastPlayAnimations)
                {
                    _lastOverrideProjectPreviews = curOverride;
                    _lastPlayAnimations = curPlay;
                    configChanged = true;
                }
            }
            else
            {
                _lastOverrideProjectPreviews = AI.Config.overrideProjectPreviews;
                _lastPlayAnimations = AI.Config.playProjectWindowAnimations;
                _configInitialized = true;
                configChanged = true;
            }

            bool shouldPoll = now >= _nextPollTime;
            if (!shouldPoll && !configChanged) return;

            if (shouldPoll)
            {
                _nextPollTime = now + POLL_INTERVAL;
                RefreshBrowserCache();
            }

            if (configChanged)
            {
                for (int i = 0; i < _cachedBrowsers.Count; i++)
                {
                    UpdateToolbar(_cachedBrowsers[i]);
                }
            }
        }

        private static void RefreshBrowserCache()
        {
            UnityEngine.Object[] projectBrowsers = Resources.FindObjectsOfTypeAll(_projectBrowserType);

            _cachedBrowsers.Clear();
            for (int i = 0; i < projectBrowsers.Length; i++)
            {
                EditorWindow window = projectBrowsers[i] as EditorWindow;
                if (window == null) continue;

                _cachedBrowsers.Add(window);
                int windowId = window.GetStableId();

                if (!_injectedToolbars.ContainsKey(windowId))
                {
                    InjectToolbar(window, windowId);
                }
            }

            // Clean up entries for windows that no longer exist
            if (_injectedToolbars.Count > _cachedBrowsers.Count)
            {
                List<int> toRemove = null;
                foreach (int id in _injectedToolbars.Keys)
                {
                    bool found = false;
                    for (int i = 0; i < _cachedBrowsers.Count; i++)
                    {
                        if (_cachedBrowsers[i].GetStableId() == id)
                        {
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        if (toRemove == null) toRemove = new List<int>();
                        toRemove.Add(id);
                    }
                }
                if (toRemove != null)
                {
                    for (int i = 0; i < toRemove.Count; i++)
                    {
                        _injectedToolbars.Remove(toRemove[i]);
                    }
                }
            }
        }

        private static void InjectToolbar(EditorWindow window, int windowId)
        {
            VisualElement root = window.rootVisualElement;
            if (root == null) return;

            // Check if our toggles already exist
            VisualElement existingPreview = root.Query(className: ToolbarClassName + "-preview").First();
            if (existingPreview != null) return;

            // Find the bottom toolbar - search for any slider in the bottom area
            // Unity's Project window has a size slider in the bottom toolbar
            Slider slider = null;
            VisualElement bottomToolbar = null;

            // Try multiple approaches to find the slider and toolbar
            List<Slider> sliders = root.Query<Slider>().ToList();
            foreach (Slider s in sliders)
            {
                // Check if this slider is in a toolbar-like container near the bottom
                VisualElement parent = s.parent;
                while (parent != null && parent != root)
                {
                    // Look for toolbar or elements positioned at the bottom
                    if (parent is Toolbar ||
                        parent.resolvedStyle.position == Position.Absolute ||
                        parent.name?.Contains("toolbar") == true ||
                        parent.name?.Contains("Toolbar") == true)
                    {
                        // Check if it's near the bottom
                        if (parent.resolvedStyle.position == Position.Absolute)
                        {
                            if (parent.resolvedStyle.bottom >= 0 && parent.resolvedStyle.bottom < 5)
                            {
                                slider = s;
                                bottomToolbar = parent;
                                break;
                            }
                        }
                        else
                        {
                            // For relative positioned toolbars, check if slider parent looks right
                            slider = s;
                            bottomToolbar = parent;
                            break;
                        }
                    }
                    parent = parent.parent;
                }
                if (slider != null) break;
            }

            // If we couldn't find a suitable location, try to find the immediate parent of any slider
            if (slider == null && sliders.Count > 0)
            {
                slider = sliders[sliders.Count - 1]; // Use the last slider (most likely the size slider)
                bottomToolbar = slider.parent;
            }

            // If we couldn't find a good location, fall back to absolute positioning
            if (slider == null || bottomToolbar == null)
            {
                InjectToolbarAbsolute(window, windowId, root);
                return;
            }

            // Create Animation Toggle
            ToolbarToggle animationToggle = new ToolbarToggle
            {
                value = AI.Config.playProjectWindowAnimations,
                tooltip = "Enable/Disable Animation Playback in Project Window",
                name = "animation-toggle"
            };
            animationToggle.AddToClassList(ToolbarClassName + "-animation");
            animationToggle.style.marginLeft = 4;
            animationToggle.style.marginRight = 2;
            animationToggle.style.display = AI.Config.overrideProjectPreviews ? DisplayStyle.Flex : DisplayStyle.None;

            Image animationIcon = new Image
            {
                image = EditorGUIUtility.IconContent("Animation.Play").image,
                scaleMode = ScaleMode.ScaleToFit
            };
            animationIcon.style.width = 16;
            animationIcon.style.height = 16;
            animationToggle.Add(animationIcon);

            animationToggle.RegisterValueChangedCallback(evt =>
            {
                AI.Config.playProjectWindowAnimations = evt.newValue;
                AI.SaveConfig();
                EditorApplication.RepaintProjectWindow();
            });

            // Create Preview Toggle
            ToolbarToggle previewToggle = new ToolbarToggle
            {
                value = AI.Config.overrideProjectPreviews,
                tooltip = "Enable/Disable Custom Previews in Project Window",
                name = "preview-toggle"
            };
            previewToggle.AddToClassList(ToolbarClassName + "-preview");
            previewToggle.style.marginLeft = 2;
            previewToggle.style.marginRight = 2;

            Image previewIcon = new Image
            {
                image = EditorGUIUtility.IconContent("animationvisibilitytoggleon").image,
                scaleMode = ScaleMode.ScaleToFit
            };
            previewIcon.style.width = 16;
            previewIcon.style.height = 16;
            previewToggle.Add(previewIcon);

            previewToggle.RegisterValueChangedCallback(evt =>
            {
                AI.Config.overrideProjectPreviews = evt.newValue;
                AI.SaveConfig();
                UnityIconOverlay.ClearCache();
                EditorApplication.RepaintProjectWindow();

                // Update all toolbars to show/hide animation toggle
                UpdateAllToolbars();
            });

            // Insert toggles before the slider
            int sliderIndex = bottomToolbar.IndexOf(slider);
            if (sliderIndex >= 0)
            {
                bottomToolbar.Insert(sliderIndex, animationToggle);
                bottomToolbar.Insert(sliderIndex + 1, previewToggle);

                // Store references
                _injectedToolbars[windowId] = bottomToolbar;
            }
        }

        private static void InjectToolbarAbsolute(EditorWindow window, int windowId, VisualElement root)
        {
            // Fallback to absolute positioning
            Toolbar toolbar = new Toolbar();
            toolbar.AddToClassList(ToolbarClassName);
            toolbar.style.position = Position.Absolute;
            toolbar.style.bottom = -1;
            toolbar.style.height = 20.5f;
            toolbar.style.right = 78;
            toolbar.style.paddingRight = 5;
            toolbar.style.paddingLeft = 5;
            toolbar.style.flexDirection = FlexDirection.Row;
            toolbar.style.justifyContent = Justify.FlexStart;

            // Match Unity's theme colors
            Color backgroundColor = EditorGUIUtility.isProSkin
                ? new Color(0.25f, 0.25f, 0.25f, 1)
                : new Color(0.811f, 0.811f, 0.811f, 1.0f);
            toolbar.style.backgroundColor = backgroundColor;
            toolbar.style.borderBottomColor = backgroundColor;

            // Create Animation Toggle
            ToolbarToggle animationToggle = new ToolbarToggle
            {
                value = AI.Config.playProjectWindowAnimations,
                tooltip = "Enable/Disable Animation Playback in Project Window",
                name = "animation-toggle"
            };
            animationToggle.AddToClassList(ToolbarClassName + "-animation");
            animationToggle.style.display = AI.Config.overrideProjectPreviews ? DisplayStyle.Flex : DisplayStyle.None;

            Image animationIcon = new Image
            {
                image = EditorGUIUtility.IconContent("Animation.Play").image,
                scaleMode = ScaleMode.ScaleToFit
            };
            animationIcon.style.width = 16;
            animationIcon.style.height = 16;
            animationToggle.Add(animationIcon);

            animationToggle.RegisterValueChangedCallback(evt =>
            {
                AI.Config.playProjectWindowAnimations = evt.newValue;
                AI.SaveConfig();
                EditorApplication.RepaintProjectWindow();
            });

            // Create Preview Toggle
            ToolbarToggle previewToggle = new ToolbarToggle
            {
                value = AI.Config.overrideProjectPreviews,
                tooltip = "Enable/Disable Custom Previews in Project Window",
                name = "preview-toggle"
            };
            previewToggle.AddToClassList(ToolbarClassName + "-preview");

            Image previewIcon = new Image
            {
                image = EditorGUIUtility.IconContent("animationvisibilitytoggleon").image,
                scaleMode = ScaleMode.ScaleToFit
            };
            previewIcon.style.width = 16;
            previewIcon.style.height = 16;
            previewToggle.Add(previewIcon);

            previewToggle.RegisterValueChangedCallback(evt =>
            {
                AI.Config.overrideProjectPreviews = evt.newValue;
                AI.SaveConfig();
                UnityIconOverlay.ClearCache();
                EditorApplication.RepaintProjectWindow();

                // Update all toolbars to show/hide animation toggle
                UpdateAllToolbars();
            });

            // Add toggles to toolbar
            toolbar.Add(animationToggle);
            toolbar.Add(previewToggle);

            // Add toolbar to window
            root.Add(toolbar);

            // Store reference
            _injectedToolbars[windowId] = toolbar;
        }

        private static void UpdateToolbar(EditorWindow window)
        {
            VisualElement root = window.rootVisualElement;
            if (root == null) return;

            // Find our toggles by class name
            ToolbarToggle previewToggle = root.Query<ToolbarToggle>(className: ToolbarClassName + "-preview").First();
            ToolbarToggle animationToggle = root.Query<ToolbarToggle>(className: ToolbarClassName + "-animation").First();

            if (previewToggle != null)
            {
                previewToggle.SetValueWithoutNotify(AI.Config.overrideProjectPreviews);
            }

            if (animationToggle != null)
            {
                animationToggle.SetValueWithoutNotify(AI.Config.playProjectWindowAnimations);
                animationToggle.style.display = AI.Config.overrideProjectPreviews ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private static void UpdateAllToolbars()
        {
            if (_projectBrowserType == null) return;

            RefreshBrowserCache();
            for (int i = 0; i < _cachedBrowsers.Count; i++)
            {
                UpdateToolbar(_cachedBrowsers[i]);
            }
        }
    }
}
#endif
