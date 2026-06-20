using System;
using System.Linq;
using ImpossibleRobert.Common;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AssetInventory
{
    /// <summary>
    /// Generates previews for Unity scene files.
    /// Extracted from PreviewManager for better separation of concerns.
    /// </summary>
    public static class ScenePreviewGenerator
    {
        /// <summary>
        /// Create preview for a scene file
        /// </summary>
        public static Texture2D Create(string sceneFile, int size = PreviewManager.DEFAULT_PREVIEW_SIZE, PreviewRequest previewRequest = null)
        {
            Scene originalActiveScene = SceneManager.GetActiveScene();
            Scene loadedScene = default;
            Camera previewCamera = null;
            Light previewLight = null;
            Light previewSecondaryLight = null;
            Texture2D texture = null;

            try
            {
                // Load scene additively
                loadedScene = EditorSceneManager.OpenScene(sceneFile, OpenSceneMode.Additive);

                if (!loadedScene.IsValid())
                {
                    if (previewRequest != null) previewRequest.FailureReason = "The scene could not be loaded for preview generation.";
                    if (AI.Config.LogPreviewCreation)
                    {
                        Debug.LogWarning($"Failed to load scene '{sceneFile}'");
                    }
                    return null;
                }

                // Set the loaded scene as active (for camera rendering)
                SceneManager.SetActiveScene(loadedScene);

                // Get all renderers in the scene
                GameObject[] rootObjects = loadedScene.GetRootGameObjects();

                if (rootObjects.Length == 0)
                {
                    if (previewRequest != null) previewRequest.FailureReason = "The scene has no root objects to preview.";
                    if (AI.Config.LogPreviewCreation)
                    {
                        Debug.LogWarning($"Scene '{sceneFile}' has no root objects.");
                    }
                    return null;
                }

                Renderer[] allRenderers = rootObjects
                    .SelectMany(go => go.GetComponentsInChildren<Renderer>())
                    .ToArray();

                if (allRenderers.Length == 0)
                {
                    if (previewRequest != null) previewRequest.FailureReason = "The scene has no renderable objects to preview.";
                    if (AI.Config.LogPreviewCreation)
                    {
                        Debug.LogWarning($"Scene '{sceneFile}' has no renderable objects.");
                    }
                    return null;
                }

                // Calculate scene bounds
                Bounds sceneBounds = PreviewBoundsCalculator.GetGlobalBounds(allRenderers);

                // Create camera for rendering
                previewCamera = PreviewCameraSetup.CreatePreviewCamera(loadedScene);

                // Position camera to frame the scene
                PreviewCameraSetup.PositionCameraForScene(previewCamera, sceneBounds);

                // Create main light
                previewLight = PreviewLightingSetup.CreateMainLight(loadedScene);

                // Position light
                PreviewLightingSetup.PositionLightForScene(previewLight, previewCamera, sceneBounds);

                // Create secondary light if enabled
                if (AI.Config.cpUseSecondaryLight)
                {
                    previewSecondaryLight = PreviewLightingSetup.CreateSecondaryLight(loadedScene);
                }

                // Render the scene
                int nativeSize = size * AI.Config.cpSuperSamplingMultiplier;
                texture = PreviewRenderUtilities.RenderCameraToTexture(previewCamera, nativeSize);

                // Downscale if needed
                if (texture != null && (texture.width > size || texture.height > size))
                {
                    Texture2D downscaled = texture.Downscale(size, size);
                    UnityEngine.Object.DestroyImmediate(texture);
                    texture = downscaled;
                }

                // Verify preview for error shaders (incompatible render pipeline)
                if (texture != null && AI.Config.verifyPreviews)
                {
                    if (PreviewValidation.IsErrorShader(texture.ToImage()))
                    {
                        // Incompatible render pipeline detected - skip preview generation
                        UnityEngine.Object.DestroyImmediate(texture);
                        texture = null;
                        if (previewRequest != null)
                        {
                            previewRequest.IncompatiblePipeline = true;
                            previewRequest.FailureReason = "The item is incompatible to the currently used render pipeline.";
                        }

                        if (AI.Config.LogPreviewCreation)
                        {
                            Debug.LogWarning($"Scene preview generated error shader (incompatible render pipeline) for '{sceneFile}'. Skipping preview generation.");
                        }
                    }
                }

                return texture;
            }
            catch (Exception ex)
            {
                if (previewRequest != null) previewRequest.FailureReason = ex.Message;
                if (AI.Config.LogPreviewCreation)
                {
                    Debug.LogError($"Error loading scene for preview '{sceneFile}': {ex.Message}");
                }
                return null;
            }
            finally
            {
                // Cleanup: destroy camera and lights
                if (previewCamera != null) UnityEngine.Object.DestroyImmediate(previewCamera.gameObject);
                if (previewLight != null) UnityEngine.Object.DestroyImmediate(previewLight.gameObject);
                if (previewSecondaryLight != null) UnityEngine.Object.DestroyImmediate(previewSecondaryLight.gameObject);

                // Close the loaded scene
                if (loadedScene.IsValid())
                {
                    EditorSceneManager.CloseScene(loadedScene, true);
                }

                // Restore original active scene
                if (originalActiveScene.IsValid())
                {
                    SceneManager.SetActiveScene(originalActiveScene);
                }
            }
        }
    }
}

