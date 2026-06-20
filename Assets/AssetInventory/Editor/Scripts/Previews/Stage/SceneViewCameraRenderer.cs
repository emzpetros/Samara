using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AssetInventory
{
    /// <summary>
    /// Utility for rendering GameObjects using a temporary scene and camera.
    /// This bypasses VFX Graph's visibility limitations with custom cameras.
    /// </summary>
    public static class SceneViewCameraRenderer
    {
        /// <summary>
        /// Capture a GameObject using a temporary scene and camera
        /// </summary>
        /// <param name="target">GameObject to render</param>
        /// <param name="width">Render texture width</param>
        /// <param name="height">Render texture height</param>
        /// <param name="setupCamera">Optional callback to setup camera (position, settings, etc.)</param>
        /// <returns>Rendered texture or null on failure</returns>
        public static Texture2D CaptureWithSceneCamera(
            GameObject target,
            int width,
            int height,
            Action<Camera, Scene, PreviewSceneContext> setupCamera = null)
        {
            if (target == null)
            {
                Debug.LogError("[SceneViewCameraRenderer] Target GameObject is null");
                return null;
            }

            Scene renderScene = default;
            GameObject sceneObject = null;
            GameObject cameraObject;
            PreviewSceneContext sceneContext = null;
            Scene originalScene = default;
            bool wasTargetPrefabAsset = false;

            try
            {
                // TODO: needs more thought, will block execution with a modal popup during preview recreation
                // base-problem is that scene gets dirty since some objects must be created in the main scene
                // and even when setting dontsave flags the scene gets dirty downstream 
                if (AI.Config.proposeSaveSceneDialog)
                {
                    // Check if current scene is untitled/unsaved and save if necessary
                    // Unity cannot create additive scenes when there's an untitled unsaved scene
                    Scene activeScene = SceneManager.GetActiveScene();
                    if (string.IsNullOrEmpty(activeScene.path))
                    {
                        // Attempt to save the untitled scene
                        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                        {
                            // User cancelled or save failed
                            Debug.LogWarning("[SceneViewCameraRenderer] Cannot create preview scene: Current scene is untitled and unsaved.");
                            return null;
                        }
                    }
                }

                // Create dedicated rendering scene
                renderScene = EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene,
                    NewSceneMode.Additive
                );

                // Move or instantiate target GameObject to render scene
                wasTargetPrefabAsset = PrefabUtility.IsPartOfPrefabAsset(target);
                if (wasTargetPrefabAsset)
                {
                    sceneObject = Object.Instantiate(target);
                    SceneManager.MoveGameObjectToScene(sceneObject, renderScene);
                }
                else
                {
                    originalScene = target.scene;
                    SceneManager.MoveGameObjectToScene(target, renderScene);
                    sceneObject = target;
                }

                sceneObject.SetActive(true);

                // Create camera GameObject in the render scene
                cameraObject = new GameObject("RenderCamera");
                SceneManager.MoveGameObjectToScene(cameraObject, renderScene);
                Camera camera = cameraObject.AddComponent<Camera>();

                // Setup preview scene (lighting, environment, background)
                sceneContext = PreviewSceneSetup.SetupPreviewScene(renderScene, camera, apply3DModelSettings: false);

                // Setup render texture with proper format for transparency support
                // Use RenderTextureDescriptor for better HDRP compatibility
                bool useLinearColorSpace = QualitySettings.activeColorSpace == ColorSpace.Linear;
                RenderTextureDescriptor rtd = new RenderTextureDescriptor(width, height)
                {
                    depthBufferBits = 24,
                    msaaSamples = 4,
                    useMipMap = false,
                    sRGB = useLinearColorSpace,
                    graphicsFormat = useLinearColorSpace
                        ? UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB
                        : UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm
                };
                RenderTexture rt = new RenderTexture(rtd);
                rt.Create();
                camera.targetTexture = rt;

                // Custom camera setup if provided
                setupCamera?.Invoke(camera, renderScene, sceneContext);

                // Update background quad size after camera setup (in case camera settings changed)
                PreviewSceneSetup.UpdateBackgroundQuadSize(sceneContext?.BackgroundQuad, camera);

                // Render
                camera.Render();

                // Read pixels
                Texture2D result = new Texture2D(width, height, TextureFormat.RGBA32, false);
                RenderTexture.active = rt;
                result.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                result.Apply();

                // Cleanup render texture
                RenderTexture.active = null;
                camera.targetTexture = null;
                rt.Release();

                return result;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SceneViewCameraRenderer] Failed to capture: {e.Message}");
                return null;
            }
            finally
            {
                // Always cleanup

                // Restore environment settings
                if (sceneContext != null)
                {
                    PreviewSceneSetup.RestoreEnvironmentSettings(sceneContext);
                }

                // Handle target GameObject first (before destroying scene)
                if (sceneObject != null)
                {
                    if (wasTargetPrefabAsset)
                    {
                        // Destroy instantiated copy
                        Object.DestroyImmediate(sceneObject);
                    }
                    else if (originalScene.IsValid())
                    {
                        // Move back to original scene
                        SceneManager.MoveGameObjectToScene(sceneObject, originalScene);
                    }
                }

                // Close render scene (destroys all objects in it)
                if (renderScene.IsValid())
                {
                    EditorSceneManager.CloseScene(renderScene, true);
                }
            }
        }
    }
}