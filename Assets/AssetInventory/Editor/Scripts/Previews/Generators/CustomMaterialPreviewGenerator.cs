using System;
using System.Collections;
using System.Threading.Tasks;
using ImpossibleRobert.Common;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace AssetInventory
{
    /// <summary>
    /// Custom preview generator for materials.
    /// Renders materials on primitive meshes (Sphere, Cube, Plane) with configurable lighting.
    /// </summary>
    public static class CustomMaterialPreviewGenerator
    {
        /// <summary>
        /// Mesh shape options for material preview rendering.
        /// </summary>
        public enum PreviewMeshType
        {
            Sphere = 0,
            Cube = 1,
            Plane = 2,
            Cylinder = 3
        }

        /// <summary>
        /// Creates a preview texture for the material at the specified path.
        /// </summary>
        /// <param name="materialPath">Asset path to the material file.</param>
        /// <param name="size">Target preview size in pixels.</param>
        /// <returns>Preview texture, or null if generation failed.</returns>
        public static Task<Texture2D> Create(string materialPath, int size = 128)
        {
            TaskCompletionSource<Texture2D> tcs = new TaskCompletionSource<Texture2D>();

            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                Debug.LogError($"Failed to load material from: {materialPath}");
                tcs.SetResult(null);
                return tcs.Task;
            }

            // Start the coroutine to handle the preview generation
            EditorCoroutineUtility.StartCoroutineOwnerless(ProcessMaterial(material, materialPath, size, tcs));

            return tcs.Task;
        }

        /// <summary>
        /// Creates a preview texture for the specified material object.
        /// </summary>
        /// <param name="material">Material to preview.</param>
        /// <param name="size">Target preview size in pixels.</param>
        /// <returns>Preview texture, or null if generation failed.</returns>
        public static Task<Texture2D> Create(Material material, int size = 128)
        {
            TaskCompletionSource<Texture2D> tcs = new TaskCompletionSource<Texture2D>();

            if (material == null)
            {
                Debug.LogError("Material is null");
                tcs.SetResult(null);
                return tcs.Task;
            }

            // Start the coroutine to handle the preview generation
            EditorCoroutineUtility.StartCoroutineOwnerless(ProcessMaterial(material, null, size, tcs));

            return tcs.Task;
        }

        /// <summary>
        /// Checks if a material uses a skybox shader.
        /// </summary>
        /// <param name="material">Material to check.</param>
        /// <returns>True if the material uses a skybox shader.</returns>
        private static bool IsSkyboxMaterial(Material material)
        {
            if (material == null || material.shader == null)
            {
                return false;
            }

            return material.shader.name.StartsWith("Skybox/");
        }

        /// <summary>
        /// Camera pitch angle for skybox preview (looking upward to position horizon in lower portion).
        /// A steeper angle positions the horizon lower, showing more sky in the preview.
        /// </summary>
        private const float SKYBOX_CAMERA_PITCH = -23f;

        /// <summary>
        /// Creates a preview for a skybox material by rendering it with a camera looking slightly upward.
        /// This positions the horizon in the lower portion of the preview, matching Unity's native behavior.
        /// </summary>
        /// <param name="material">Skybox material to preview.</param>
        /// <param name="size">Target preview size in pixels.</param>
        /// <returns>Preview texture, or null if rendering failed.</returns>
        private static Texture2D CreateSkyboxPreview(Material material, int size)
        {
            Scene previewScene = default;
            GameObject cameraObject = null;
            Texture2D result = null;

            // Store original render settings
            Material originalSkybox = RenderSettings.skybox;
            AmbientMode originalAmbientMode = RenderSettings.ambientMode;

            try
            {
                // Create a preview scene for isolated rendering
                previewScene = EditorSceneManager.NewPreviewScene();
                if (EditorSceneManager.GetSceneCullingMask(previewScene) == 0)
                {
                    throw new OutOfMemoryException("Too many preview scenes were not closed correctly.");
                }

                // Create camera for skybox rendering
                cameraObject = new GameObject("SkyboxPreviewCamera");
                SceneManager.MoveGameObjectToScene(cameraObject, previewScene);

                Camera camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox;
                camera.cullingMask = 0; // Don't render any objects, just the skybox
                camera.fieldOfView = 60f;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 100f;
                camera.scene = previewScene;

                // Position camera at origin, looking slightly upward to place horizon in lower portion
                cameraObject.transform.position = Vector3.zero;
                cameraObject.transform.rotation = Quaternion.Euler(SKYBOX_CAMERA_PITCH, 0f, 0f);

                // Set up skybox for the preview scene
                RenderSettings.skybox = material;
                RenderSettings.ambientMode = AmbientMode.Skybox;

                // Create render texture
                bool useLinearColorSpace = QualitySettings.activeColorSpace == ColorSpace.Linear;
                RenderTextureDescriptor rtd = new RenderTextureDescriptor(size, size)
                {
                    depthBufferBits = 16,
                    msaaSamples = 1,
                    useMipMap = false,
                    sRGB = useLinearColorSpace,
                    graphicsFormat = useLinearColorSpace
                        ? UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB
                        : UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm
                };
                RenderTexture rt = new RenderTexture(rtd);
                rt.Create();

                // Render skybox
                camera.targetTexture = rt;
                camera.Render();
                camera.targetTexture = null;

                // Convert to Texture2D
                result = new Texture2D(size, size, TextureFormat.RGBA32, false);
                RenderTexture oldActive = RenderTexture.active;
                RenderTexture.active = rt;
                result.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                result.Apply();
                RenderTexture.active = oldActive;

                // Cleanup render texture
                rt.Release();
                Object.DestroyImmediate(rt);
            }
            finally
            {
                // Restore original render settings
                RenderSettings.skybox = originalSkybox;
                RenderSettings.ambientMode = originalAmbientMode;

                // Cleanup
                if (cameraObject != null)
                {
                    Object.DestroyImmediate(cameraObject);
                }

                if (previewScene.IsValid())
                {
                    EditorSceneManager.ClosePreviewScene(previewScene);
                }
            }

            return result;
        }

        /// <summary>
        /// Creates a primitive GameObject with the specified mesh type.
        /// </summary>
        private static GameObject CreatePreviewMesh(PreviewMeshType meshType)
        {
            GameObject meshObject;

            switch (meshType)
            {
                case PreviewMeshType.Cube:
                    meshObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    break;

                case PreviewMeshType.Plane:
                    meshObject = GameObject.CreatePrimitive(PrimitiveType.Plane);
                    // Rotate plane to face camera better (default is horizontal)
                    meshObject.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                    // Scale plane down (default Unity plane is 10x10 units)
                    meshObject.transform.localScale = new Vector3(0.1f, 0.1f, 0.1f);
                    break;

                case PreviewMeshType.Cylinder:
                    meshObject = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    break;

                case PreviewMeshType.Sphere:
                default:
                    meshObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    break;
            }

            // Remove collider - not needed for preview
            Collider collider = meshObject.GetComponent<Collider>();
            if (collider != null)
            {
                Object.DestroyImmediate(collider);
            }

            meshObject.name = "MaterialPreviewMesh";
            return meshObject;
        }

        /// <summary>
        /// Coroutine that handles the material preview generation process.
        /// </summary>
        private static IEnumerator ProcessMaterial(Material material, string materialPath, int size, TaskCompletionSource<Texture2D> tcs)
        {
            CustomPreviewStage stage = null;
            Scene previewScene = default;
            Texture2D resultTexture = null;
            bool hasError = false;
            string errorMessage = "";

            // Check if we can create preview stage
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            {
                Debug.LogWarning("Cannot create preview stage during play mode or compilation.");
                tcs.SetResult(null);
                yield break;
            }

            // Check for skybox materials - render as flat texture instead of on 3D mesh
            if (IsSkyboxMaterial(material))
            {
                try
                {
                    resultTexture = CreateSkyboxPreview(material, size);
                    if (resultTexture != null)
                    {
                        tcs.SetResult(resultTexture);
                        yield break;
                    }
                    // If skybox preview failed, fall through to 3D render as fallback
                }
                catch (Exception e)
                {
                    if (AI.Config.LogPreviewCreation)
                    {
                        string name = materialPath ?? material?.name ?? "Unknown";
                        Debug.LogWarning($"Failed to create skybox preview for '{name}': {e.Message}. Falling back to 3D render.");
                    }
                    // Fall through to standard 3D preview
                }
            }

            // Create preview scene
            try
            {
                previewScene = EditorSceneManager.NewPreviewScene();
                if (EditorSceneManager.GetSceneCullingMask(previewScene) == 0)
                {
                    throw new OutOfMemoryException("Too many preview scenes were not closed correctly. Restart Unity to fix.");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to create preview scene for material: {e.Message}");
                tcs.SetResult(null);
                yield break;
            }

            // Setup phase (before yield) - no try-catch around yield
            try
            {
                // Create preview mesh based on configured type
                PreviewMeshType meshType = (PreviewMeshType)AI.Config.materialPreviewMesh;
                GameObject meshObject = CreatePreviewMesh(meshType);

                // Apply material to mesh
                MeshRenderer renderer = meshObject.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    renderer.sharedMaterial = material;
                }

                // Create and setup stage
                stage = ScriptableObject.CreateInstance<CustomPreviewStage>();
                stage.SetScene(previewScene, true);

                // Setup scene with the mesh object
                stage.SetupSceneForMaterial(meshObject, meshType);
            }
            catch (Exception e)
            {
                hasError = true;
                errorMessage = e.Message;
                Debug.LogError($"Error setting up material preview: {e.Message}\n{e.StackTrace}");
            }

            if (hasError)
            {
                if (stage != null)
                {
                    stage.RestoreEnvironment();
                    Object.DestroyImmediate(stage);
                }
                if (AI.Config.LogPreviewCreation)
                {
                    string name = materialPath ?? material?.name ?? "Unknown";
                    Debug.LogWarning($"Failed to generate preview for material '{name}': {errorMessage}");
                }
                tcs.SetResult(null);
                yield break;
            }

            // Wait a frame for any shaders to compile (yield outside try-catch)
            yield return null;

            // Diagnostic: detect race-condition damage during the yield above.
            // True damage = a saved reference exists on disk (non-zero instance id) but
            // resolves to null at runtime. Empty texture slots in the source .mat are
            // legitimate (e.g. Standard shader has 9 TexEnv slots, most go unused).

            // Render phase (after yield)
            try
            {
                // Calculate native render size with super-sampling
                int superSamplingMultiplier = AI.Config.cpSuperSamplingMultiplier;
                int nativeSize = size * superSamplingMultiplier;

                // Render the frame
                resultTexture = stage.RenderFrame(nativeSize, nativeSize);

                // Downscale if super-sampled
                if (resultTexture != null && superSamplingMultiplier > 1)
                {
                    Texture2D downscaled = resultTexture.Downscale(size, size);
                    Object.DestroyImmediate(resultTexture);
                    resultTexture = downscaled;
                }
            }
            catch (Exception e)
            {
                hasError = true;
                errorMessage = e.Message;
                Debug.LogError($"Error rendering material preview: {e.Message}\n{e.StackTrace}");
            }

            // Cleanup
            if (stage != null)
            {
                stage.RestoreEnvironment();
                Object.DestroyImmediate(stage);
            }

            if (hasError)
            {
                if (AI.Config.LogPreviewCreation)
                {
                    string name = materialPath ?? material?.name ?? "Unknown";
                    Debug.LogWarning($"Failed to generate preview for material '{name}': {errorMessage}");
                }
                tcs.SetResult(null);
            }
            else
            {
                tcs.SetResult(resultTexture);
            }
        }

    }
}
