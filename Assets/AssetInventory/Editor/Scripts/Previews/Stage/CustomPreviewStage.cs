using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
#if USE_HDRP
using UnityEngine.Rendering.HighDefinition;
#endif
using UnityEngine.SceneManagement;
#if USE_TEXTMESHPRO || UNITY_2023_1_OR_NEWER
using TMPro;
#endif

namespace AssetInventory
{
    public class CustomPreviewStage : PreviewSceneStage
    {
        private const int BACKGROUND_LAYER = 31; // Use layer 31 for background quad (typically unused)

        public Camera Camera { get; private set; }
        public Light Light { get; private set; }
        public Light SecondaryLight { get; private set; }
        public GameObject InstantiatedPrefab { get; private set; }
        public CustomPrefabPreviewGenerator.PrefabType PrefabType { get; set; }
        public AnimationClip FBXAnimationClip { get; set; } // For FBX files with animations
        public float FBXStaticPreviewSampleTime { get; set; } = -1f; // Time to sample for static preview (-1 = not set)
        public bool NeedsBoneVisualization { get; set; } // For animation-only FBX files without geometry

        // Environment preservation
        private Color _originalAmbientLight;
        private AmbientMode _originalAmbientMode;
        private float _originalAmbientIntensity;

        // Background objects
        private GameObject _backgroundQuad;

        // Store the original scene of the prefab so we can move it back
        private Scene _originalPrefabScene;

        // Track whether this is a preview scene or regular scene (VFX needs regular scene)
        private bool _isPreviewScene = true;

        // Track first render for HDRP initialization (needs RepaintAllViews on first frame)
        private bool _hasRenderedOnce;

        public void SetScene(Scene sceneIn, bool isPreviewScene = true)
        {
            scene = sceneIn;
            _isPreviewScene = isPreviewScene;
        }

        public void ReplaceInstantiatedPrefab(GameObject newPrefab)
        {
            // Destroy the old prefab
            if (InstantiatedPrefab != null)
            {
                DestroyImmediate(InstantiatedPrefab);
            }

            // Set the new prefab
            InstantiatedPrefab = newPrefab;

            // Move to preview scene
            if (newPrefab != null && scene.IsValid())
            {
                StageUtility.PlaceGameObjectInCurrentStage(newPrefab);
                SceneManager.MoveGameObjectToScene(newPrefab, scene);
                newPrefab.SetActive(true);
            }

            // Reset first-render flag so the next RenderFrame does the warm-up render and
            // shader compilation wait for the replacement model's (possibly new) materials.
            _hasRenderedOnce = false;
        }

        public void SetupScene(GameObject prefab, float animationDuration = 0f)
        {
            if (!scene.IsValid())
            {
                Debug.LogError("Preview scene is not valid. Call SetScene before SetupScene.");
                return;
            }

            // Store original environment settings
            _originalAmbientLight = RenderSettings.ambientLight;
            _originalAmbientMode = RenderSettings.ambientMode;
            _originalAmbientIntensity = RenderSettings.ambientIntensity;

            // Check if this is a prefab asset that needs to be instantiated
            bool isPrefabAsset = PrefabUtility.IsPartOfPrefabAsset(prefab);

            if (isPrefabAsset)
            {
                // Instantiate the prefab
                InstantiatedPrefab = Instantiate(prefab);
                InstantiatedPrefab.SetActive(true); // Ensure main GameObject is always enabled
                InstantiatedPrefab.name = prefab.name; // Keep original name without (Clone) suffix
                _originalPrefabScene = default; // No original scene since this is a new instance
            }
            else
            {
                // Already an instance, use it directly
                InstantiatedPrefab = prefab;
                _originalPrefabScene = prefab.scene;
            }

            // Place in current stage and move to preview scene
            StageUtility.PlaceGameObjectInCurrentStage(InstantiatedPrefab);
            SceneManager.MoveGameObjectToScene(InstantiatedPrefab, scene);

            InstantiatedPrefab.SetActive(true);

            // Activate inactive GameObjects that have Renderer components
            // This handles prefabs that are disabled by default but need to render
            // Only activate if no renderers are currently active (to avoid artifacts with variant prefabs)
            Renderer[] allRenderers = InstantiatedPrefab.GetComponentsInChildren<Renderer>(true);
            bool hasActiveRenderer = allRenderers.Any(r => r.gameObject.activeInHierarchy);

            if (!hasActiveRenderer)
            {
                // No active renderers found - activate the first inactive renderer
                Renderer firstInactive = allRenderers.FirstOrDefault(r => !r.gameObject.activeInHierarchy);
                if (firstInactive != null)
                {
                    firstInactive.gameObject.SetActive(true);
                }
            }

            // Force TextMeshPro components to generate their meshes immediately
            // TextMeshPro (both UI and 3D) need explicit mesh generation after activation
#if USE_TEXTMESHPRO || UNITY_2023_1_OR_NEWER
            TMP_Text[] tmpComponents = InstantiatedPrefab.GetComponentsInChildren<TMP_Text>(true);

            // Destroy TMP components with missing font assets to prevent NullReferenceException
            // during ForceMeshUpdate(), Canvas.ForceUpdateCanvases(), and LayoutRebuilder calls.
            // This happens when previewing prefabs from packages that reference fonts not imported
            // into the project. Using DestroyImmediate because disabling alone doesn't prevent
            // the layout system (ILayoutElement) from querying preferredWidth on the component.
            // Safe since this is a temporary preview instance.
            foreach (TMP_Text tmp in tmpComponents)
            {
                if (tmp.font == null)
                {
                    Object.DestroyImmediate(tmp);
                }
            }

            // Re-query after destruction and force mesh generation for remaining valid components
            tmpComponents = InstantiatedPrefab.GetComponentsInChildren<TMP_Text>(true);
            foreach (TMP_Text tmp in tmpComponents)
            {
                // Force mesh generation - this is critical for 3D TextMeshPro
                tmp.ForceMeshUpdate(true, true);
            }
#endif

            // Force mesh bounds recalculation for instantiated prefabs
            // Without this, renderer bounds are (0,0,0) and camera positioning fails
            if (isPrefabAsset)
            {
                Renderer[] renderers = InstantiatedPrefab.GetComponentsInChildren<Renderer>();

                foreach (Renderer renderer in renderers)
                {
                    // Force the renderer to update its bounds based on type
                    if (renderer is SkinnedMeshRenderer skinnedRenderer)
                    {
                        skinnedRenderer.updateWhenOffscreen = true;
                        _ = skinnedRenderer.bounds; // Force update
                    }
                    else if (renderer is MeshRenderer)
                    {
                        MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
                        if (meshFilter != null && meshFilter.sharedMesh != null)
                        {
                            Mesh mesh = meshFilter.sharedMesh;
                            mesh.RecalculateBounds();
                            _ = renderer.bounds; // Force renderer update
                        }
                    }
                    else
                    {
                        // For other renderer types (ParticleSystemRenderer, SpriteRenderer, etc.)
                        // Just access bounds to force Unity to calculate them
                        _ = renderer.bounds;
                    }
                }
            }

            // Force all LOD groups to use highest quality level (LOD 0)
            PrefabPreviewUtilities.ForceLODLevel0(InstantiatedPrefab);

            SetupCameraAndLights();
            ConfigureCameraBackground();

            // Check if the prefab needs a temporary Canvas (UI elements without Canvas ancestor)
            bool needsCanvas = PrefabPreviewUtilities.NeedsTemporaryCanvas(InstantiatedPrefab);
            if (needsCanvas)
            {
                PrefabPreviewUtilities.CreateTemporaryCanvas(InstantiatedPrefab, Camera, scene);

                // The InstantiatedPrefab is now parented under the temporary canvas
                // Force canvas update to ensure hierarchy is properly recognized
                Canvas.ForceUpdateCanvases();
            }

            // Configure existing canvases for preview rendering
            // This must happen after camera creation but before any rendering/detection
            // Handles Screen Space - Camera (assigns camera) and Screen Space - Overlay (converts to World Space)
            PrefabPreviewUtilities.ConfigureCanvasForPreview(InstantiatedPrefab, Camera);

            // Detect prefab type
            // If we created a temporary canvas, this is definitely a UI prefab
            if (needsCanvas)
            {
                PrefabType = CustomPrefabPreviewGenerator.PrefabType.UI;
            }
            else
            {
                PrefabType = (CustomPrefabPreviewGenerator.PrefabType)(int)PrefabPreviewUtilities.DetectPrefabType(InstantiatedPrefab);
            }

            // Set camera FOV based on prefab type (must be after PrefabType is determined)
            // Only apply custom FOV for 3D models, use default 60° for particles/VFX/UI
            if (PrefabType == CustomPrefabPreviewGenerator.PrefabType.Model)
            {
                Camera.fieldOfView = AI.Config.cpCameraFOV;
            }

            // For UI, use orthographic camera for perfect flat rendering
            if (PrefabType == CustomPrefabPreviewGenerator.PrefabType.UI)
            {
                Camera.orthographic = true;
                // orthographicSize will be set during camera positioning based on canvas bounds
            }

            // Enable secondary light only for Model type
            if (AI.Config.cpUseSecondaryLight && PrefabType == CustomPrefabPreviewGenerator.PrefabType.Model)
            {
                SecondaryLight.enabled = true;
            }

            // Convert preview-instance materials to the active render pipeline before rendering.
            // This is the non-destructive fallback that runs on the instantiated preview copy only.
            // Only runs when the custom pipeline converter is enabled.
            if (AI.Config.useCustomPipelineConverter)
            {
                PrefabPreviewUtilities.ConvertMaterialsToCurrentPipeline(InstantiatedPrefab);
            }

            // Handle special prefab types
            if (PrefabType == CustomPrefabPreviewGenerator.PrefabType.Particles)
            {
                CustomPrefabPreviewGenerator.HandleParticleSystems(InstantiatedPrefab);

                // Force particle system bounds update after simulation
                ParticleSystemRenderer[] particleRenderers = InstantiatedPrefab.GetComponentsInChildren<ParticleSystemRenderer>();
                foreach (ParticleSystemRenderer renderer in particleRenderers)
                {
                    // Accessing bounds forces Unity to recalculate them based on current particle positions
                    _ = renderer.bounds;
                }
            }
            // Note: VFX systems are NOT initialized here - they are handled in ProcessPrefab
            // to ensure proper timing and avoid double-initialization issues

            // Force Unity to update all transforms and recalculate bounds before camera positioning
            Physics.SyncTransforms();

            // Position camera based on prefab (do this first to get proper framing)
            // For animations, pass duration to calculate bounds over time (accounts for particle motion like fireworks)
            // Skip for UI prefabs - camera will be positioned after layout is complete in ProcessPrefab
            if (PrefabType != CustomPrefabPreviewGenerator.PrefabType.UI)
            {
                CustomPrefabPreviewGenerator.PositionCameraForPrefab(InstantiatedPrefab, Camera, PrefabType, animationDuration);
            }

            // Setup gradient background if enabled (after camera positioning)
            if (AI.Config.cpBackgroundType == CustomPreviewBackgroundType.TwoColorGradient ||
                AI.Config.cpBackgroundType == CustomPreviewBackgroundType.FourColorGradient)
            {
                SetupGradientBackground();
            }

            // Position light relative to camera
            PreviewLightingSetup.PositionLight(Light, Camera, InstantiatedPrefab, PrefabType);

            // Position secondary light relative to camera (only if enabled for Model type)
            if (AI.Config.cpUseSecondaryLight && PrefabType == CustomPrefabPreviewGenerator.PrefabType.Model)
            {
                PreviewLightingSetup.PositionLight(SecondaryLight, Camera, InstantiatedPrefab, PrefabType, isSecondaryLight: true);
            }

            // Note: We don't apply environment settings here anymore - they're applied per-frame in RenderFrame()
        }

        /// <summary>
        /// Simplified setup for material previews. Sets up scene with a pre-created mesh object.
        /// </summary>
        /// <param name="meshObject">The primitive mesh GameObject with material already applied.</param>
        /// <param name="meshType">The type of mesh being used for the preview.</param>
        public void SetupSceneForMaterial(GameObject meshObject, CustomMaterialPreviewGenerator.PreviewMeshType meshType)
        {
            if (!scene.IsValid())
            {
                Debug.LogError("Preview scene is not valid. Call SetScene before SetupSceneForMaterial.");
                return;
            }

            // Store original environment settings
            _originalAmbientLight = RenderSettings.ambientLight;
            _originalAmbientMode = RenderSettings.ambientMode;
            _originalAmbientIntensity = RenderSettings.ambientIntensity;

            // Store reference and move to preview scene
            InstantiatedPrefab = meshObject;
            _originalPrefabScene = default;

            StageUtility.PlaceGameObjectInCurrentStage(meshObject);
            SceneManager.MoveGameObjectToScene(meshObject, scene);
            meshObject.SetActive(true);

            // Set prefab type for materials
            PrefabType = CustomPrefabPreviewGenerator.PrefabType.Model;

            // Convert preview-instance materials to the active render pipeline before rendering.
            // Unity 6.3's built-in URP converter is gated off, so BIRP shaders (Standard,
            // Legacy, Particles, Self-Illum, ...) render magenta on URP/HDRP and trip the
            // IsErrorShader detector. This non-destructive conversion mirrors the prefab path
            // (CustomPreviewStage.SetupScene) and runs on the throwaway preview mesh only.
            if (AI.Config.useCustomPipelineConverter)
            {
                PrefabPreviewUtilities.ConvertMaterialsToCurrentPipeline(meshObject);
            }

            // Setup camera and lights
            SetupCameraAndLights();
            ConfigureCameraBackground();

            // Set camera FOV
            Camera.fieldOfView = AI.Config.cpCameraFOV;

            // Enable secondary light for materials (same as models)
            if (AI.Config.cpUseSecondaryLight)
            {
                SecondaryLight.enabled = true;
            }

            // Position camera for material preview with tighter framing
            PositionCameraForMaterial(InstantiatedPrefab, Camera, meshType);

            // Setup gradient background if enabled
            if (AI.Config.cpBackgroundType == CustomPreviewBackgroundType.TwoColorGradient ||
                AI.Config.cpBackgroundType == CustomPreviewBackgroundType.FourColorGradient)
            {
                SetupGradientBackground();
            }

            // Position lights
            PreviewLightingSetup.PositionLight(Light, Camera, InstantiatedPrefab, PrefabType);

            if (AI.Config.cpUseSecondaryLight)
            {
                PreviewLightingSetup.PositionLight(SecondaryLight, Camera, InstantiatedPrefab, PrefabType, isSecondaryLight: true);
            }
        }

        /// <summary>
        /// Positions camera for material preview with correct framing.
        /// Uses tighter framing than 3D models to make materials fill more of the preview.
        /// </summary>
        private void PositionCameraForMaterial(GameObject meshObject, Camera camera, CustomMaterialPreviewGenerator.PreviewMeshType meshType)
        {
            Renderer[] renderers = meshObject.GetComponentsInChildren<Renderer>();
            if (renderers == null || renderers.Length == 0)
            {
                camera.transform.position = new Vector3(0, 2, -5);
                camera.transform.LookAt(Vector3.zero);
                return;
            }

            // Use shared bounds calculation (same as 3D models)
            Bounds bounds = PreviewBoundsCalculator.GetGlobalBounds(renderers);

            // Adjust fillFraction based on mesh type for optimal framing
            // Lower values bring camera closer (object fills more of frame)
            // Values > 1.0 push camera back (object fills less of frame)
            float fillFraction;
            switch (meshType)
            {
                case CustomMaterialPreviewGenerator.PreviewMeshType.Sphere:
                    fillFraction = 0.7f; // Sphere looks good with tighter framing
                    break;
                case CustomMaterialPreviewGenerator.PreviewMeshType.Cube:
                    fillFraction = 1.15f; // Cube corners extend when viewed at angle, need more room
                    break;
                case CustomMaterialPreviewGenerator.PreviewMeshType.Plane:
                    fillFraction = 1.1f; // Plane needs moderate room
                    break;
                case CustomMaterialPreviewGenerator.PreviewMeshType.Cylinder:
                    fillFraction = 1.25f; // Cylinder is 2x taller than wide, needs more room
                    break;
                default:
                    fillFraction = 0.7f;
                    break;
            }

            float distance = PreviewCameraSetup.CalculateCameraDistance(bounds, camera, AI.Config.cpFramingPadding, AI.Config.cpCameraAngleX, AI.Config.cpCameraAngleY, fillFraction);

            // Calculate compensated look target first (before any rotations) to keep object centered
            float verticalAngleRad = AI.Config.cpCameraAngleX * Mathf.Deg2Rad;
            float perspectiveCompensation = bounds.extents.y * Mathf.Sin(verticalAngleRad) * 0.15f;
            Vector3 lookTarget = bounds.center + Vector3.up * perspectiveCompensation;

            // Position camera at calculated distance
            camera.transform.position = new Vector3(bounds.center.x, bounds.center.y + distance, bounds.center.z);
            camera.transform.LookAt(lookTarget);

            // Apply custom angles
            camera.transform.RotateAround(bounds.center, Vector3.left, AI.Config.cpCameraAngleX);
            camera.transform.RotateAround(bounds.center, Vector3.up, AI.Config.cpCameraAngleY);
        }

        private void ApplyEnvironmentSettings()
        {
            // Note: We're modifying global RenderSettings here, but we'll restore them in RestoreEnvironment()
            // This is necessary because RenderSettings are global and can't be set per-scene in edit mode

            // Apply ambient intensity
            RenderSettings.ambientLight = _originalAmbientLight * AI.Config.cpAmbientIntensity;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientIntensity = AI.Config.cpAmbientIntensity;
        }

        /// <summary>
        /// Creates camera and light GameObjects for the preview scene.
        /// </summary>
        private void SetupCameraAndLights()
        {
            // Create camera and light GameObjects
            GameObject camGO = new GameObject("PreviewCamera");
            GameObject lightGO = new GameObject("PreviewLight");

            // Place in current stage and move to preview scene
            StageUtility.PlaceGameObjectInCurrentStage(camGO);
            StageUtility.PlaceGameObjectInCurrentStage(lightGO);
            SceneManager.MoveGameObjectToScene(camGO, scene);
            SceneManager.MoveGameObjectToScene(lightGO, scene);

            // Setup camera
            Camera = camGO.AddComponent<Camera>();
            Camera.scene = scene;
            Camera.enabled = false;
            Camera.tag = "MainCamera";
            Camera.nearClipPlane = 0.01f;
            Camera.farClipPlane = 100000;
            Camera.fieldOfView = 60f;
            Camera.depthTextureMode = DepthTextureMode.Depth;
            Camera.clearFlags = CameraClearFlags.Color;
            Camera.cullingMask = -1;

            // CRITICAL: Set aspect explicitly to prevent Unity from using screen aspect ratio.
            // Without this, camera.aspect defaults to the display's aspect (~1.78 for 16:9),
            // causing WorldToViewportPoint() to return incorrect coordinates during camera
            // positioning, which shifts non-square objects off-center in previews.
            Camera.aspect = 1.0f;

            // Add HDRP camera components
            if (AssetUtils.IsOnHDRP())
            {
#if USE_HDRP
                UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData cameraData = Camera.gameObject.AddComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();
                cameraData.clearColorMode = UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData.ClearColorMode.Color;

                // Create Volume with fixed exposure
                GameObject volumeGO = new GameObject("PreviewVolume");
                StageUtility.PlaceGameObjectInCurrentStage(volumeGO);
                SceneManager.MoveGameObjectToScene(volumeGO, scene);

                Volume volume = volumeGO.AddComponent<Volume>();
                VolumeProfile profile = CreateInstance<VolumeProfile>();
                volume.profile = profile;
                volume.isGlobal = true;

                UnityEngine.Rendering.HighDefinition.Exposure exposure = profile.Add<UnityEngine.Rendering.HighDefinition.Exposure>();
                exposure.active = true;
                exposure.mode.overrideState = true;
                exposure.mode.value = UnityEngine.Rendering.HighDefinition.ExposureMode.Fixed;
                exposure.fixedExposure.overrideState = true;
                exposure.fixedExposure.value = 11f;

                UnityEngine.Rendering.HighDefinition.Fog fog = profile.Add<UnityEngine.Rendering.HighDefinition.Fog>();
                fog.active = true;
                fog.enabled.overrideState = true;
                fog.enabled.value = false;
#endif
            }

#if USE_URP
            if (AssetUtils.IsOnURP())
            {
                Camera.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            }
#endif

            // Setup primary light
            lightGO.transform.rotation = Quaternion.Euler(AI.Config.cpLightRotationX, AI.Config.cpLightRotationY, 0f);
            Light = lightGO.AddComponent<Light>();
            Light.type = AI.Config.cpUseDirectionalLight ? LightType.Directional : LightType.Point;

            Color lightColor = Color.white;
            if (!string.IsNullOrEmpty(AI.Config.cpLightColor) && ColorUtility.TryParseHtmlString("#" + AI.Config.cpLightColor, out Color lc))
            {
                lightColor = lc;
            }
            Light.color = lightColor;
            Light.shadows = LightShadows.None;

            if (AssetUtils.IsOnHDRP())
            {
                Light.intensity = AI.Config.cpLightIntensityHDRP;
#if USE_HDRP
                HDAdditionalLightData lightData = Light.gameObject.AddComponent<HDAdditionalLightData>();
#if UNITY_2023_1_OR_NEWER
                lightData.SetIntensity(AI.Config.cpLightIntensityHDRP, UnityEngine.Rendering.LightUnit.Lux);
#else
                lightData.SetIntensity(AI.Config.cpLightIntensityHDRP, LightUnit.Lux);
#endif
#endif
            }
            else if (AssetUtils.IsOnURP())
            {
                Light.intensity = AI.Config.cpLightIntensityURP;
#if USE_URP
                Light.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalLightData>();
#endif
            }
            else
            {
                Light.intensity = AI.Config.cpLightIntensity;
            }

            // Setup secondary light
            GameObject secondaryLightGO = new GameObject("PreviewSecondaryLight");
            StageUtility.PlaceGameObjectInCurrentStage(secondaryLightGO);
            SceneManager.MoveGameObjectToScene(secondaryLightGO, scene);

            secondaryLightGO.transform.rotation = Quaternion.Euler(AI.Config.cpSecondaryLightRotationX, AI.Config.cpSecondaryLightRotationY, 0f);

            SecondaryLight = secondaryLightGO.AddComponent<Light>();
            SecondaryLight.type = LightType.Directional;

            Color secondaryLightColor = new Color(0.4f, 0.4f, 0.45f);
            if (!string.IsNullOrEmpty(AI.Config.cpSecondaryLightColor) &&
                ColorUtility.TryParseHtmlString("#" + AI.Config.cpSecondaryLightColor, out Color slc))
                secondaryLightColor = slc;
            SecondaryLight.color = secondaryLightColor;
            SecondaryLight.shadows = LightShadows.None;

            float intensityMultiplier = AI.Config.cpSecondaryLightIntensityMultiplier;
            if (AssetUtils.IsOnHDRP())
            {
                SecondaryLight.intensity = AI.Config.cpLightIntensityHDRP * intensityMultiplier;
#if USE_HDRP
                HDAdditionalLightData secondaryLightData = SecondaryLight.gameObject.AddComponent<HDAdditionalLightData>();
#if UNITY_2023_1_OR_NEWER
                secondaryLightData.SetIntensity(AI.Config.cpLightIntensityHDRP * intensityMultiplier, UnityEngine.Rendering.LightUnit.Lux);
#else
                secondaryLightData.SetIntensity(AI.Config.cpLightIntensityHDRP * intensityMultiplier, LightUnit.Lux);
#endif
#endif
            }
            else if (AssetUtils.IsOnURP())
            {
                SecondaryLight.intensity = AI.Config.cpLightIntensityURP * intensityMultiplier;
#if USE_URP
                SecondaryLight.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalLightData>();
#endif
            }
            else
            {
                SecondaryLight.intensity = AI.Config.cpLightIntensity * intensityMultiplier;
            }

            SecondaryLight.enabled = false;
        }

        /// <summary>
        /// Configures camera background based on settings. Called for each prefab.
        /// </summary>
        private void ConfigureCameraBackground()
        {
            switch (AI.Config.cpBackgroundType)
            {
                case CustomPreviewBackgroundType.Transparent:
                    Camera.clearFlags = CameraClearFlags.SolidColor;
                    Camera.backgroundColor = Color.clear;
                    break;

                case CustomPreviewBackgroundType.TwoColorGradient:
                case CustomPreviewBackgroundType.FourColorGradient:
                    Camera.clearFlags = CameraClearFlags.SolidColor;
                    Camera.backgroundColor = Color.black;
                    break;

                case CustomPreviewBackgroundType.SolidColor:
                default:
                    Camera.clearFlags = CameraClearFlags.SolidColor;
                    if (AssetUtils.IsOnHDRP())
                    {
                        Color bgColorHDRP = new Color(34f / 255, 34f / 255, 34f / 255);
                        if (!string.IsNullOrEmpty(AI.Config.cpBackgroundColorHDRP) && ColorUtility.TryParseHtmlString("#" + AI.Config.cpBackgroundColorHDRP, out Color bch))
                        {
                            bgColorHDRP = bch;
                        }
                        Camera.backgroundColor = bgColorHDRP;
                    }
                    else
                    {
                        Color bgColor = new Color(82f / 255, 82f / 255, 82f / 255);
                        if (!string.IsNullOrEmpty(AI.Config.cpBackgroundColor) && ColorUtility.TryParseHtmlString("#" + AI.Config.cpBackgroundColor, out Color bc))
                        {
                            bgColor = bc;
                        }
                        Camera.backgroundColor = bgColor;
                    }
                    break;
            }

#if USE_HDRP
            // Update HDRP camera background color if component exists
            if (AssetUtils.IsOnHDRP())
            {
                UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData cameraData = Camera.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();
                if (cameraData != null)
                {
                    cameraData.backgroundColorHDR = Camera.backgroundColor;
                }
            }
#endif
        }

        private void SetupGradientBackground()
        {
            // Create a quad that fills the camera view
            _backgroundQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _backgroundQuad.name = "GradientBackground";
            StageUtility.PlaceGameObjectInCurrentStage(_backgroundQuad);
            SceneManager.MoveGameObjectToScene(_backgroundQuad, scene);

            // Remove the collider (we don't need it)
            DestroyImmediate(_backgroundQuad.GetComponent<Collider>());

            // Position the quad far behind the object
            float distance = Camera.farClipPlane * 0.95f; // 95% of far clip plane
            Vector3 quadPosition = Camera.transform.position + Camera.transform.forward * distance;
            _backgroundQuad.transform.position = quadPosition;
            _backgroundQuad.transform.rotation = Camera.transform.rotation;

            // Apply rotation around the camera's forward axis (Z-axis rotation from camera's perspective)
            _backgroundQuad.transform.Rotate(Camera.transform.forward, AI.Config.cpGradientRotation, Space.World);

            // Scale the quad to fill the camera view at that distance
            float halfWidth, halfHeight;

            if (Camera.orthographic)
            {
                // For orthographic cameras (UI), size is based on orthographicSize
                halfHeight = Camera.orthographicSize;
                halfWidth = halfHeight * Camera.aspect;
            }
            else
            {
                // For perspective cameras (3D models, particles), calculate based on FOV
                halfHeight = Mathf.Tan(Camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * distance;
                halfWidth = halfHeight * Camera.aspect;
            }

            // When rotated, the quad needs to be larger to still cover the entire view
            float rotationRad = AI.Config.cpGradientRotation * Mathf.Deg2Rad;
            float scaleFactor = Mathf.Max(Mathf.Abs(Mathf.Cos(rotationRad)) + Mathf.Abs(Mathf.Sin(rotationRad)), 1f);

            _backgroundQuad.transform.localScale = new Vector3(halfWidth * 2 * scaleFactor, halfHeight * 2 * scaleFactor, 1);

            // Create gradient based on type
            if (AI.Config.cpBackgroundType == CustomPreviewBackgroundType.TwoColorGradient)
            {
                Setup2ColorGradient();
            }
            else if (AI.Config.cpBackgroundType == CustomPreviewBackgroundType.FourColorGradient)
            {
                Setup4ColorGradient();
            }

            // Set rendering layer to dedicated background layer for culling mask control
            _backgroundQuad.layer = BACKGROUND_LAYER;
        }

        private void Setup2ColorGradient()
        {
            // Parse gradient colors
            Color topColor = new Color(0.5f, 0.5f, 0.5f);
            Color bottomColor = new Color(0.25f, 0.25f, 0.25f);

            if (!string.IsNullOrEmpty(AI.Config.cpGradient2TopColor))
            {
                ColorUtility.TryParseHtmlString("#" + AI.Config.cpGradient2TopColor, out topColor);
            }
            if (!string.IsNullOrEmpty(AI.Config.cpGradient2BottomColor))
            {
                ColorUtility.TryParseHtmlString("#" + AI.Config.cpGradient2BottomColor, out bottomColor);
            }

            // Create a gradient texture
            Texture2D gradientTexture = Create2ColorGradientTexture(topColor, bottomColor, 256);

            // Create material and apply texture - use Unlit/Texture to display the texture
            Material gradientMaterial = new Material(Shader.Find("Unlit/Texture"));
            gradientMaterial.mainTexture = gradientTexture;

            _backgroundQuad.GetComponent<Renderer>().material = gradientMaterial;
        }

        private void Setup4ColorGradient()
        {
            // Parse all four corner colors
            Color topLeft = new Color(0.5f, 0.5f, 0.5f);
            Color topRight = new Color(0.375f, 0.375f, 0.375f);
            Color bottomLeft = new Color(0.25f, 0.25f, 0.25f);
            Color bottomRight = new Color(0.1875f, 0.1875f, 0.1875f);

            if (!string.IsNullOrEmpty(AI.Config.cpGradient4TopLeftColor))
                ColorUtility.TryParseHtmlString("#" + AI.Config.cpGradient4TopLeftColor, out topLeft);
            if (!string.IsNullOrEmpty(AI.Config.cpGradient4TopRightColor))
                ColorUtility.TryParseHtmlString("#" + AI.Config.cpGradient4TopRightColor, out topRight);
            if (!string.IsNullOrEmpty(AI.Config.cpGradient4BottomLeftColor))
                ColorUtility.TryParseHtmlString("#" + AI.Config.cpGradient4BottomLeftColor, out bottomLeft);
            if (!string.IsNullOrEmpty(AI.Config.cpGradient4BottomRightColor))
                ColorUtility.TryParseHtmlString("#" + AI.Config.cpGradient4BottomRightColor, out bottomRight);

            // Get the mesh and set vertex colors
            MeshFilter meshFilter = _backgroundQuad.GetComponent<MeshFilter>();
            Mesh mesh = Instantiate(meshFilter.sharedMesh); // Create a copy to avoid modifying the shared mesh

            // Quad vertices are ordered: bottom-left, bottom-right, top-left, top-right
            Color[] vertexColors = new Color[4];
            vertexColors[0] = bottomLeft; // bottom-left
            vertexColors[1] = bottomRight; // bottom-right
            vertexColors[2] = topLeft; // top-left
            vertexColors[3] = topRight; // top-right

            mesh.colors = vertexColors;
            meshFilter.sharedMesh = mesh; // Assign the modified copy back

            // Use the vertex color shader from the project
            Shader vertexColorShader = Shader.Find("AssetInventory/UnlitVertexColor");
            if (vertexColorShader == null)
            {
                Debug.LogError("AssetInventory/UnlitVertexColor shader not found. Please ensure the shader file exists.");
                vertexColorShader = Shader.Find("Unlit/Color"); // Fallback
            }
            Material gradientMaterial = new Material(vertexColorShader);
            _backgroundQuad.GetComponent<Renderer>().material = gradientMaterial;
        }

        private Texture2D Create2ColorGradientTexture(Color topColor, Color bottomColor, int height)
        {
            Texture2D texture = new Texture2D(1, height, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;

            for (int y = 0; y < height; y++)
            {
                float t = (float)y / (height - 1);
                Color color = Color.Lerp(bottomColor, topColor, t);
                texture.SetPixel(0, y, color);
            }

            texture.Apply();
            return texture;
        }

        public void UpdateBackgroundQuadPosition()
        {
            // Update background quad to always face the camera if it exists
            if (_backgroundQuad != null)
            {
                // Position the quad far behind relative to current camera position
                float distance = Camera.farClipPlane * 0.95f;
                Vector3 quadPosition = Camera.transform.position + Camera.transform.forward * distance;
                _backgroundQuad.transform.position = quadPosition;
                _backgroundQuad.transform.rotation = Camera.transform.rotation;

                // Apply rotation around the camera's forward axis (Z-axis rotation from camera's perspective)
                _backgroundQuad.transform.Rotate(Camera.transform.forward, AI.Config.cpGradientRotation, Space.World);

                // Scale the quad to fill the camera view at that distance
                float halfWidth, halfHeight;

                if (Camera.orthographic)
                {
                    // For orthographic cameras (UI), size is based on orthographicSize
                    halfHeight = Camera.orthographicSize;
                    halfWidth = halfHeight * Camera.aspect;
                }
                else
                {
                    // For perspective cameras (3D models, particles), calculate based on FOV
                    halfHeight = Mathf.Tan(Camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * distance;
                    halfWidth = halfHeight * Camera.aspect;
                }

                // When rotated, the quad needs to be larger to still cover the entire view
                // Calculate the scale factor needed based on the rotation angle
                // At 45 degrees, we need sqrt(2) times the size
                float rotationRad = AI.Config.cpGradientRotation * Mathf.Deg2Rad;
                float scaleFactor = Mathf.Max(Mathf.Abs(Mathf.Cos(rotationRad)) + Mathf.Abs(Mathf.Sin(rotationRad)), 1f);

                _backgroundQuad.transform.localScale = new Vector3(halfWidth * 2 * scaleFactor, halfHeight * 2 * scaleFactor, 1);
            }
        }

        public Texture2D RenderFrame(int width, int height)
        {
            // Apply environment settings before rendering
            ApplyEnvironmentSettings();

            // Set camera aspect BEFORE updating background quad so the quad is sized correctly.
            // After each render Camera.ResetAspect() reverts to an auto-detected value which is
            // unreliable in preview stages, causing the gradient quad to be too narrow.
            Camera.aspect = (float)width / (float)height;

            // Update background quad position before rendering to ensure it always faces the camera
            UpdateBackgroundQuadPosition();

            // Special handling for transparent backgrounds with particle systems
            // Particle shaders don't render correctly against Color.clear due to alpha blending
            // Solution: Render against black background, then extract alpha from luminance
            bool isTransparentBackground = AI.Config.cpBackgroundType == CustomPreviewBackgroundType.Transparent;
            bool needsAlphaExtraction = isTransparentBackground && (PrefabType == CustomPrefabPreviewGenerator.PrefabType.Particles);
            Color originalBgColor = Camera.backgroundColor;

            if (needsAlphaExtraction)
            {
                // Temporarily use black background for particle rendering
                Camera.backgroundColor = Color.black;
            }

            // Setup render texture with depth and MSAA
            // Use ARGB32 format to support alpha channel for transparent backgrounds
            // sRGB format is only supported in Linear color space, use UNorm in Gamma mode
            bool useLinearColorSpace = QualitySettings.activeColorSpace == ColorSpace.Linear;

            RenderTextureDescriptor rtd = new RenderTextureDescriptor(width, height)
            {
                depthBufferBits = AI.Config.cpDepth,
                msaaSamples = 4,
                useMipMap = false,
                sRGB = useLinearColorSpace, // Only use sRGB in Linear color space
                graphicsFormat = useLinearColorSpace
                    ? UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB
                    : UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm
            };
            RenderTexture rt = new RenderTexture(rtd);
            rt.Create();

            // Render camera to render texture
            Camera.targetTexture = rt;
            Camera.aspect = (float)width / (float)height;

            // HDRP needs initialization on first render - force Unity to update render state
            // This fixes empty previews on first try in HDRP projects
            if (!_hasRenderedOnce)
            {
                _hasRenderedOnce = true;
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();

                // SRPs need shader compilation before the actual render
                // Do a warm-up render to trigger shader compilation, then the actual render will work
                if (AssetUtils.IsOnURP() || AssetUtils.IsOnHDRP())
                {
                    System.Threading.Thread.Sleep(16); // Brief pause after RepaintAllViews
                    Camera.Render(); // Warm-up render triggers shader compilation

                    // Wait for on-demand shader variant compilation triggered by the warm-up render.
                    // In bulk mode the initial WaitForShaderCompilation() after Refresh only catches
                    // import-triggered compilation. Loading a prefab's materials later causes additional
                    // variant compilation (URP Lit keywords, particle additive/alpha, etc.) on background
                    // threads. The fixed 48ms was insufficient; polling ensures variants are ready.
                    int shaderWait = 0;
                    const int shaderWaitCap = 5000; // 5s safety cap per prefab
                    while (ShaderUtil.anythingCompiling && shaderWait < shaderWaitCap)
                    {
                        System.Threading.Thread.Sleep(50);
                        shaderWait += 50;
                    }

                    System.Threading.Thread.Sleep(16); // Allow SRP to process
                    UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                    System.Threading.Thread.Sleep(16); // Final pause before actual render
                }
                else
                {
                    System.Threading.Thread.Sleep(16); // ~1 frame for built-in pipeline
                }
            }

            // For particle systems, force ALL renderer updates (particles AND meshes)
            if (PrefabType == CustomPrefabPreviewGenerator.PrefabType.Particles && InstantiatedPrefab != null)
            {
                // Force particle renderers to update their meshes/buffers before Camera.Render()
                ParticleSystemRenderer[] psRenderers = InstantiatedPrefab.GetComponentsInChildren<ParticleSystemRenderer>();
                foreach (ParticleSystemRenderer psRenderer in psRenderers)
                {
                    // Get the associated ParticleSystem component to validate state
                    ParticleSystem ps = psRenderer.GetComponent<ParticleSystem>();

                    // Only access bounds and BakeMesh if particle system exists, is alive (playing or paused), and has particles
                    // This prevents Unity's internal assertions when accessing invalid particle arrays
                    // Note: isPaused is needed because Simulate() leaves systems paused with valid particles
                    if (ps != null && (ps.isPlaying || ps.isPaused) && ps.particleCount > 0)
                    {
                        // Accessing bounds forces an update of the renderer's internal state
                        Bounds _ = psRenderer.bounds;

                        // Force mesh baking for Billboard particles (converts billboards to actual mesh data)
                        // Only bake when system is truly playing — paused systems already had their
                        // renderer mesh prepared by the player loop update (QueuePlayerLoopUpdate + yield).
                        // Calling BakeMesh on freshly-simulated-then-paused systems causes
                        // 'ps->array_size()' assertions and garbage mesh bounds.
                        if (psRenderer.renderMode == ParticleSystemRenderMode.Billboard && ps.isPlaying && !ps.isPaused)
                        {
                            // BakeMesh forces the particle system to generate renderable geometry
                            Mesh tempMesh = new Mesh();
#if UNITY_2022_3_OR_NEWER
                            psRenderer.BakeMesh(tempMesh, Camera, ParticleSystemBakeMeshOptions.Default);
#else
                            psRenderer.BakeMesh(tempMesh, Camera);
#endif
                            DestroyImmediate(tempMesh);
                        }
                    }
                }

                // Also force mesh renderer updates for prefabs that have both particles AND meshes
                // HDRP requires mesh materials to be properly initialized before rendering
                MeshRenderer[] meshRenderers = InstantiatedPrefab.GetComponentsInChildren<MeshRenderer>();
                foreach (MeshRenderer meshRenderer in meshRenderers)
                {
                    // Only access bounds if renderer is enabled and has valid mesh
                    if (meshRenderer.enabled)
                    {
                        // Check if mesh is valid via MeshFilter component
                        MeshFilter meshFilter = meshRenderer.GetComponent<MeshFilter>();
                        if (meshFilter != null && meshFilter.sharedMesh != null)
                        {
                            Bounds bounds = meshRenderer.bounds;
                            // Validate bounds are not invalid (infinity/NaN) before using
                            // Note: Unity may still log warnings for invalid mesh data, but we validate what we can
                            if (!float.IsInfinity(bounds.size.magnitude) && !float.IsNaN(bounds.size.magnitude))
                            {
                                // Bounds are valid - accessing them forces renderer state initialization
                                _ = bounds;
                            }
                        }
                    }
                }

                SkinnedMeshRenderer[] skinnedRenderers = InstantiatedPrefab.GetComponentsInChildren<SkinnedMeshRenderer>();
                foreach (SkinnedMeshRenderer skinnedRenderer in skinnedRenderers)
                {
                    // Only access bounds if renderer is enabled and has valid mesh
                    // This prevents warnings about abnormal mesh bounds with invalid vertices
                    if (skinnedRenderer.enabled && skinnedRenderer.sharedMesh != null)
                    {
                        skinnedRenderer.updateWhenOffscreen = true;
                        Bounds bounds = skinnedRenderer.bounds;
                        // Validate bounds are not invalid (infinity/NaN) before using
                        // Note: Unity may still log warnings for invalid mesh data, but we validate what we can
                        if (!float.IsInfinity(bounds.size.magnitude) && !float.IsNaN(bounds.size.magnitude))
                        {
                            // Bounds are valid - accessing them forces renderer state initialization
                            _ = bounds;
                        }
                    }
                }
            }

            // For VFX systems, just force simulation update (removed RepaintAllViews and Sleep for performance)
            if (PrefabType == CustomPrefabPreviewGenerator.PrefabType.VFX && InstantiatedPrefab != null)
            {
                // Force VFX renderers to update their state
                UnityEngine.VFX.VisualEffect[] vfxSystems = InstantiatedPrefab.GetComponentsInChildren<UnityEngine.VFX.VisualEffect>();
                foreach (UnityEngine.VFX.VisualEffect vfx in vfxSystems)
                {
                    // Ensure VFX is playing and update its state
                    if (!vfx.isActiveAndEnabled) continue;

                    // Force a simulation step to update internal state right before render
                    vfx.Simulate(0.016f, 1);
                }
            }

            Camera.Render();
            Camera.targetTexture = null;
            Camera.ResetAspect();

            // Restore original background color
            if (needsAlphaExtraction)
            {
                Camera.backgroundColor = originalBgColor;
            }

            // Convert render texture to Texture2D
            Texture2D render = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            RenderTexture oldActive = RenderTexture.active;
            RenderTexture.active = rt;
            render.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            render.Apply();

            // Cleanup render texture
            RenderTexture.active = oldActive;
            rt.Release();

            // Post-process for transparent backgrounds with particles
            // Extract alpha from luminance (brighter pixels = more opaque)
            if (needsAlphaExtraction)
            {
                Color[] pixels = render.GetPixels();
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color pixel = pixels[i];
                    // Calculate luminance as alpha (grayscale brightness = opacity)
                    float alpha = (pixel.r + pixel.g + pixel.b) / 3f;
                    // If pixel has any brightness, keep it and set proper alpha
                    if (alpha > 0.01f)
                    {
                        // Keep original color but set alpha from luminance
                        pixels[i] = new Color(pixel.r, pixel.g, pixel.b, alpha);
                    }
                    else
                    {
                        // Fully transparent for black pixels
                        pixels[i] = Color.clear;
                    }
                }
                render.SetPixels(pixels);
                render.Apply();
            }

            return render;
        }

        public Texture2D RenderFrameForDetection(int width, int height)
        {
            // Apply environment settings before rendering
            ApplyEnvironmentSettings();

            // Set camera aspect BEFORE updating background quad so the quad is sized correctly.
            Camera.aspect = (float)width / (float)height;

            // Update background quad position before rendering to ensure it always faces the camera
            UpdateBackgroundQuadPosition();

            // Store original culling mask
            int originalCullingMask = Camera.cullingMask;

            // Exclude the background layer from rendering during detection
            // This allows us to see only the actual content, not the background
            Camera.cullingMask = originalCullingMask & ~(1 << BACKGROUND_LAYER);

            // Use a distinct clear color for detection (magenta makes it easy to identify)
            Color originalBackgroundColor = Camera.backgroundColor;
            Camera.backgroundColor = new Color(1f, 0f, 1f, 0f); // Magenta for detection

            // Setup render texture with depth and MSAA (same as regular render)
            bool useLinearColorSpace = QualitySettings.activeColorSpace == ColorSpace.Linear;

            RenderTextureDescriptor rtd = new RenderTextureDescriptor(width, height)
            {
                depthBufferBits = AI.Config.cpDepth,
                msaaSamples = 4,
                useMipMap = false,
                sRGB = useLinearColorSpace,
                graphicsFormat = useLinearColorSpace
                    ? UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB
                    : UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm
            };
            RenderTexture rt = new RenderTexture(rtd);
            rt.Create();

            // Render camera to render texture
            Camera.targetTexture = rt;
            Camera.aspect = (float)width / (float)height;
            Camera.Render();
            Camera.targetTexture = null;
            Camera.ResetAspect();

            // Convert render texture to Texture2D
            Texture2D render = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            RenderTexture oldActive = RenderTexture.active;
            RenderTexture.active = rt;
            render.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            render.Apply();

            // Cleanup render texture
            RenderTexture.active = oldActive;
            rt.Release();

            // Restore original camera settings
            Camera.cullingMask = originalCullingMask;
            Camera.backgroundColor = originalBackgroundColor;

            return render;
        }

        public void RestoreEnvironment()
        {
            // Handle cleanup of the instantiated prefab
            if (InstantiatedPrefab != null && InstantiatedPrefab.scene == scene)
            {
                if (_originalPrefabScene.IsValid())
                {
                    // Move back to original scene (this was an instance from a storage preview scene)
                    SceneManager.MoveGameObjectToScene(InstantiatedPrefab, _originalPrefabScene);
                    InstantiatedPrefab.SetActive(false);
                }
                else
                {
                    // This was instantiated from a prefab asset, so destroy the instance
                    // The preview scene closure will handle the cleanup, but we explicitly destroy it here
                    DestroyImmediate(InstantiatedPrefab);
                    InstantiatedPrefab = null;
                }
            }

            // Restore original RenderSettings
            RenderSettings.ambientLight = _originalAmbientLight;
            RenderSettings.ambientMode = _originalAmbientMode;
            RenderSettings.ambientIntensity = _originalAmbientIntensity;

            // Close the scene (this will clean up camera, light, and background objects)
            if (scene.IsValid())
            {
                if (_isPreviewScene)
                {
                    EditorSceneManager.ClosePreviewScene(scene);
                }
                else
                {
                    // VFX uses regular scene to bypass visibility limitations
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        protected override GUIContent CreateHeaderContent()
        {
            return new GUIContent("Asset Inventory Preview");
        }
    }
}
