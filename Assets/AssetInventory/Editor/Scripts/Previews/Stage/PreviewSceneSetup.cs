using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace AssetInventory
{
    public static class PreviewSceneSetup
    {
        private const int BACKGROUND_LAYER = 31;
        
        /// <summary>
        /// Setup complete preview scene with lighting, environment, and background
        /// </summary>
        public static PreviewSceneContext SetupPreviewScene(Scene scene, Camera camera, bool apply3DModelSettings = false)
        {
            PreviewSceneContext context = new PreviewSceneContext();
            
            // Store original environment settings
            context.OriginalAmbientLight = RenderSettings.ambientLight;
            context.OriginalAmbientMode = RenderSettings.ambientMode;
            context.OriginalAmbientIntensity = RenderSettings.ambientIntensity;
            
            // Apply environment settings
            ApplyEnvironmentSettings();
            
            // Create lighting
            context.Light = CreateLight(scene);
            
            // Setup camera
            SetupCameraForBackground(camera);
            SetupCameraQuality(camera, apply3DModelSettings);
            
            // Setup HDRP-specific camera and volume settings
            if (AssetUtils.IsOnHDRP())
            {
#if USE_HDRP
                // Add HDRP camera data - required for proper background color rendering
                UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData cameraData = camera.gameObject.AddComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();
                cameraData.clearColorMode = UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData.ClearColorMode.Color;
                
                // For transparent backgrounds, ensure the HDR color also has zero alpha
                if (AI.Config.cpBackgroundType == CustomPreviewBackgroundType.Transparent)
                {
                    cameraData.backgroundColorHDR = new Color(0f, 0f, 0f, 0f);
                }
                else
                {
                    cameraData.backgroundColorHDR = camera.backgroundColor;
                }

                // Create Volume with fixed exposure to prevent auto-exposure issues
                GameObject volumeGO = new GameObject("PreviewVolume");
                SceneManager.MoveGameObjectToScene(volumeGO, scene);

                Volume volume = volumeGO.AddComponent<Volume>();
                VolumeProfile profile = ScriptableObject.CreateInstance<VolumeProfile>();
                volume.profile = profile;
                volume.isGlobal = true;

                // Fixed exposure prevents background color shifting
                UnityEngine.Rendering.HighDefinition.Exposure exposure = profile.Add<UnityEngine.Rendering.HighDefinition.Exposure>();
                exposure.active = true;
                exposure.mode.overrideState = true;
                exposure.mode.value = UnityEngine.Rendering.HighDefinition.ExposureMode.Fixed;
                exposure.fixedExposure.overrideState = true;
                exposure.fixedExposure.value = 11f;

                // Disable fog to prevent it affecting preview renders
                UnityEngine.Rendering.HighDefinition.Fog fog = profile.Add<UnityEngine.Rendering.HighDefinition.Fog>();
                fog.active = true;
                fog.enabled.overrideState = true;
                fog.enabled.value = false;
#endif
            }
            
            // Create background (needs to be after camera setup for positioning)
            context.BackgroundQuad = CreateBackgroundQuad(scene, camera);
            
            return context;
        }
        
        /// <summary>
        /// Restore environment settings to their original values
        /// </summary>
        public static void RestoreEnvironmentSettings(PreviewSceneContext context)
        {
            if (context == null) return;
            
            RenderSettings.ambientLight = context.OriginalAmbientLight;
            RenderSettings.ambientMode = context.OriginalAmbientMode;
            RenderSettings.ambientIntensity = context.OriginalAmbientIntensity;
        }
        
        /// <summary>
        /// Create light for preview scene
        /// </summary>
        public static Light CreateLight(Scene scene)
        {
            GameObject lightGO = new GameObject("PreviewLight");
            SceneManager.MoveGameObjectToScene(lightGO, scene);
            
            Light light = lightGO.AddComponent<Light>();
            light.type = AI.Config.cpUseDirectionalLight ? LightType.Directional : LightType.Point;
            
            // Parse light color
            Color lightColor = Color.white;
            if (!string.IsNullOrEmpty(AI.Config.cpLightColor) && 
                ColorUtility.TryParseHtmlString("#" + AI.Config.cpLightColor, out Color lc))
            {
                lightColor = lc;
            }
            light.color = lightColor;
            light.shadows = LightShadows.None;
            
            // Set intensity based on render pipeline
            if (AssetUtils.IsOnHDRP())
            {
                light.intensity = AI.Config.cpLightIntensityHDRP;
#if USE_HDRP
                UnityEngine.Rendering.HighDefinition.HDAdditionalLightData lightData = light.gameObject.AddComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>();
#if UNITY_2023_1_OR_NEWER
                lightData.SetIntensity(AI.Config.cpLightIntensityHDRP, UnityEngine.Rendering.LightUnit.Lux);
#else
                lightData.SetIntensity(AI.Config.cpLightIntensityHDRP, UnityEngine.Rendering.HighDefinition.LightUnit.Lux);
#endif
#endif
            }
            else if (AssetUtils.IsOnURP())
            {
                light.intensity = AI.Config.cpLightIntensityURP;
#if USE_URP
                light.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalLightData>();
#endif
            }
            else
            {
                light.intensity = AI.Config.cpLightIntensity;
            }
            
            // Position based on light type
            if (light.type == LightType.Directional)
            {
                lightGO.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            }
            else
            {
                lightGO.transform.position = new Vector3(5f, 5f, -5f);
                lightGO.transform.LookAt(Vector3.zero);
            }
            
            return light;
        }
        
        /// <summary>
        /// Create background quad for gradient backgrounds
        /// </summary>
        public static GameObject CreateBackgroundQuad(Scene scene, Camera camera)
        {
            if (AI.Config.cpBackgroundType == CustomPreviewBackgroundType.Transparent ||
                AI.Config.cpBackgroundType == CustomPreviewBackgroundType.SolidColor)
            {
                return null; // No quad needed
            }
            
            // Create a quad that fills the camera view
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "GradientBackground";
            SceneManager.MoveGameObjectToScene(quad, scene);
            quad.layer = BACKGROUND_LAYER;
            
            // Remove the collider (we don't need it)
            Object.DestroyImmediate(quad.GetComponent<Collider>());
            
            // Position quad far behind
            float distance = camera.farClipPlane * 0.95f;
            Vector3 quadPosition = camera.transform.position + camera.transform.forward * distance;
            quad.transform.position = quadPosition;
            quad.transform.rotation = camera.transform.rotation;
            
            // Apply rotation around the camera's forward axis
            quad.transform.Rotate(camera.transform.forward, AI.Config.cpGradientRotation, Space.World);
            
            // Scale the quad to fill the camera view at that distance
            float halfWidth, halfHeight;
            
            if (camera.orthographic)
            {
                // For orthographic cameras, size is based on orthographicSize
                halfHeight = camera.orthographicSize;
                halfWidth = halfHeight * camera.aspect;
            }
            else
            {
                // For perspective cameras, calculate based on FOV
                halfHeight = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * distance;
                halfWidth = halfHeight * camera.aspect;
            }
            
            // When rotated, the quad needs to be larger to still cover the entire view
            // Calculate the scale factor needed based on the rotation angle
            // At 45 degrees, we need sqrt(2) times the size
            float rotationRad = AI.Config.cpGradientRotation * Mathf.Deg2Rad;
            float scaleFactor = Mathf.Max(Mathf.Abs(Mathf.Cos(rotationRad)) + Mathf.Abs(Mathf.Sin(rotationRad)), 1f);
            
            quad.transform.localScale = new Vector3(halfWidth * 2 * scaleFactor, halfHeight * 2 * scaleFactor, 1);
            
            // Create gradient material
            Material material = CreateGradientMaterial(quad);
            if (material != null)
            {
                quad.GetComponent<Renderer>().material = material;
            }
            
            return quad;
        }
        
        /// <summary>
        /// Update background quad size after camera settings change
        /// </summary>
        public static void UpdateBackgroundQuadSize(GameObject quad, Camera camera)
        {
            if (quad == null) return;
            
            // Recalculate quad size based on current camera settings
            float distance = camera.farClipPlane * 0.95f;
            Vector3 quadPosition = camera.transform.position + camera.transform.forward * distance;
            quad.transform.position = quadPosition;
            quad.transform.rotation = camera.transform.rotation;
            
            // Apply rotation around the camera's forward axis
            quad.transform.Rotate(camera.transform.forward, AI.Config.cpGradientRotation, Space.World);
            
            // Scale the quad to fill the camera view at that distance
            float halfWidth, halfHeight;
            
            if (camera.orthographic)
            {
                // For orthographic cameras, size is based on orthographicSize
                halfHeight = camera.orthographicSize;
                halfWidth = halfHeight * camera.aspect;
            }
            else
            {
                // For perspective cameras, calculate based on FOV
                halfHeight = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * distance;
                halfWidth = halfHeight * camera.aspect;
            }
            
            // When rotated, the quad needs to be larger to still cover the entire view
            // Calculate the scale factor needed based on the rotation angle
            // At 45 degrees, we need sqrt(2) times the size
            float rotationRad = AI.Config.cpGradientRotation * Mathf.Deg2Rad;
            float scaleFactor = Mathf.Max(Mathf.Abs(Mathf.Cos(rotationRad)) + Mathf.Abs(Mathf.Sin(rotationRad)), 1f);
            
            quad.transform.localScale = new Vector3(halfWidth * 2 * scaleFactor, halfHeight * 2 * scaleFactor, 1);
        }
        
        /// <summary>
        /// Setup camera background based on settings
        /// </summary>
        public static void SetupCameraForBackground(Camera camera)
        {
            switch (AI.Config.cpBackgroundType)
            {
                case CustomPreviewBackgroundType.Transparent:
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = Color.clear;
                    break;
                    
                case CustomPreviewBackgroundType.SolidColor:
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    if (AssetUtils.IsOnHDRP())
                    {
                        Color bgColorHDRP = new Color(34f / 255, 34f / 255, 34f / 255);
                        if (!string.IsNullOrEmpty(AI.Config.cpBackgroundColorHDRP) &&
                            ColorUtility.TryParseHtmlString("#" + AI.Config.cpBackgroundColorHDRP, out Color bch))
                        {
                            bgColorHDRP = bch;
                        }
                        camera.backgroundColor = bgColorHDRP;
                    }
                    else
                    {
                        Color bgColor = Color.gray;
                        if (!string.IsNullOrEmpty(AI.Config.cpBackgroundColor) &&
                            ColorUtility.TryParseHtmlString("#" + AI.Config.cpBackgroundColor, out Color bc))
                        {
                            bgColor = bc;
                        }
                        camera.backgroundColor = bgColor;
                    }
                    break;
                    
                case CustomPreviewBackgroundType.TwoColorGradient:
                case CustomPreviewBackgroundType.FourColorGradient:
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = Color.black; // Covered by gradient quad
                    break;
            }
        }
        
        /// <summary>
        /// Setup camera quality settings
        /// </summary>
        public static void SetupCameraQuality(Camera camera, bool apply3DModelSettings = false)
        {
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 100000;
            
            // Only apply custom FOV for 3D models (prefabs)
            // For particles/VFX, use default 60° FOV
            if (apply3DModelSettings)
            {
                camera.fieldOfView = AI.Config.cpCameraFOV;
            }
            else
            {
                // Explicitly set default FOV for non-3D content (particles, VFX, UI)
                camera.fieldOfView = 60f;
            }
            
            camera.depthTextureMode = DepthTextureMode.Depth;
            camera.clearFlags = CameraClearFlags.Color;
            camera.cullingMask = -1; // Render all layers
        }
        
        /// <summary>
        /// Apply environment settings from config
        /// </summary>
        public static void ApplyEnvironmentSettings()
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientIntensity = AI.Config.cpAmbientIntensity;
        }
        
        /// <summary>
        /// Create gradient material based on config
        /// </summary>
        private static Material CreateGradientMaterial(GameObject quad)
        {
            if (AI.Config.cpBackgroundType == CustomPreviewBackgroundType.TwoColorGradient)
            {
                return Create2ColorGradientMaterial();
            }
            else if (AI.Config.cpBackgroundType == CustomPreviewBackgroundType.FourColorGradient)
            {
                return Create4ColorGradientMaterial(quad);
            }
            return null;
        }
        
        private static Material Create2ColorGradientMaterial()
        {
            // Parse gradient colors
            Color topColor = new Color(0.5f, 0.5f, 0.5f);
            Color bottomColor = new Color(0.25f, 0.25f, 0.25f);
            
            if (!string.IsNullOrEmpty(AI.Config.cpGradient2TopColor))
                ColorUtility.TryParseHtmlString("#" + AI.Config.cpGradient2TopColor, out topColor);
            if (!string.IsNullOrEmpty(AI.Config.cpGradient2BottomColor))
                ColorUtility.TryParseHtmlString("#" + AI.Config.cpGradient2BottomColor, out bottomColor);
            
            // Create a gradient texture
            Texture2D gradientTexture = Create2ColorGradientTexture(topColor, bottomColor, 256);
            
            // Create material and apply texture
            Material gradientMaterial = new Material(Shader.Find("Unlit/Texture"));
            gradientMaterial.mainTexture = gradientTexture;
            
            return gradientMaterial;
        }
        
        private static Material Create4ColorGradientMaterial(GameObject quad)
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
            
            // Get the quad's mesh and modify it to add vertex colors
            MeshFilter meshFilter = quad.GetComponent<MeshFilter>();
            Mesh mesh = Object.Instantiate(meshFilter.sharedMesh); // Create a copy
            
            // Set vertex colors (Quad vertices: bottom-left, bottom-right, top-left, top-right)
            Color[] vertexColors = new Color[4];
            vertexColors[0] = bottomLeft;
            vertexColors[1] = bottomRight;
            vertexColors[2] = topLeft;
            vertexColors[3] = topRight;
            
            mesh.colors = vertexColors;
            
            // Assign the modified mesh back to the quad
            meshFilter.sharedMesh = mesh;
            
            // Use vertex color shader
            Shader vertexColorShader = Shader.Find("AssetInventory/UnlitVertexColor");
            if (vertexColorShader == null)
            {
                Debug.LogWarning("AssetInventory/UnlitVertexColor shader not found. Using Unlit/Color as fallback.");
                vertexColorShader = Shader.Find("Unlit/Color");
            }
            
            Material gradientMaterial = new Material(vertexColorShader);
            return gradientMaterial;
        }
        
        private static Texture2D Create2ColorGradientTexture(Color topColor, Color bottomColor, int height)
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
    }
    
    /// <summary>
    /// Context for preview scene setup that needs to be restored
    /// </summary>
    public class PreviewSceneContext
    {
        public Light Light;
        public GameObject BackgroundQuad;
        public Color OriginalAmbientLight;
        public AmbientMode OriginalAmbientMode;
        public float OriginalAmbientIntensity;
    }
}

