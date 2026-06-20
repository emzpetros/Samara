using UnityEngine;
using UnityEngine.SceneManagement;

namespace AssetInventory
{
    /// <summary>
    /// Centralized lighting creation and positioning for all preview types.
    /// Consolidates lighting logic from PreviewManager, CustomPreviewStage, and PreviewSceneSetup.
    /// </summary>
    public static class PreviewLightingSetup
    {
        /// <summary>
        /// Create main preview light
        /// </summary>
        public static Light CreateMainLight(Scene scene, bool addToScene = true)
        {
            GameObject lightGO = new GameObject("PreviewLight");
            if (addToScene && scene.IsValid())
            {
                SceneManager.MoveGameObjectToScene(lightGO, scene);
            }

            lightGO.transform.rotation = Quaternion.Euler(AI.Config.cpLightRotationX, AI.Config.cpLightRotationY, 0f);
            
            Light light = lightGO.AddComponent<Light>();
            light.type = AI.Config.cpUseDirectionalLight ? LightType.Directional : LightType.Point;
            light.shadows = LightShadows.None;

            Color lightColor = GetLightColor();
            light.color = lightColor;

            SetLightIntensity(light, false);

            return light;
        }

        /// <summary>
        /// Create secondary rim light
        /// </summary>
        public static Light CreateSecondaryLight(Scene scene, bool addToScene = true)
        {
            GameObject lightGO = new GameObject("PreviewSecondaryLight");
            if (addToScene && scene.IsValid())
            {
                SceneManager.MoveGameObjectToScene(lightGO, scene);
            }

            lightGO.transform.rotation = Quaternion.Euler(
                AI.Config.cpSecondaryLightRotationX,
                AI.Config.cpSecondaryLightRotationY,
                0f);

            Light light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.None;

            Color secondaryLightColor = GetSecondaryLightColor();
            light.color = secondaryLightColor;

            SetLightIntensity(light, true);

            // Initially disabled - should be enabled only for Model type
            light.enabled = false;

            return light;
        }

        /// <summary>
        /// Get main light color from configuration
        /// </summary>
        public static Color GetLightColor()
        {
            Color lightColor = Color.white;
            if (!string.IsNullOrEmpty(AI.Config.cpLightColor) &&
                ColorUtility.TryParseHtmlString("#" + AI.Config.cpLightColor, out Color lc))
            {
                lightColor = lc;
            }
            return lightColor;
        }

        /// <summary>
        /// Get secondary light color from configuration
        /// </summary>
        public static Color GetSecondaryLightColor()
        {
            Color secondaryLightColor = new Color(0.4f, 0.4f, 0.45f); // Default blue-grey
            if (!string.IsNullOrEmpty(AI.Config.cpSecondaryLightColor) &&
                ColorUtility.TryParseHtmlString("#" + AI.Config.cpSecondaryLightColor, out Color slc))
            {
                secondaryLightColor = slc;
            }
            return secondaryLightColor;
        }

        /// <summary>
        /// Set light intensity based on render pipeline
        /// </summary>
        public static void SetLightIntensity(Light light, bool isSecondary)
        {
            float intensityMultiplier = isSecondary ? AI.Config.cpSecondaryLightIntensityMultiplier : 1f;

            if (AssetUtils.IsOnHDRP())
            {
                light.intensity = AI.Config.cpLightIntensityHDRP * intensityMultiplier;
#if USE_HDRP
                UnityEngine.Rendering.HighDefinition.HDAdditionalLightData lightData = light.gameObject.AddComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>();
#if UNITY_2023_1_OR_NEWER
                lightData.SetIntensity(AI.Config.cpLightIntensityHDRP * intensityMultiplier, UnityEngine.Rendering.LightUnit.Lux);
#else
                lightData.SetIntensity(AI.Config.cpLightIntensityHDRP * intensityMultiplier, UnityEngine.Rendering.HighDefinition.LightUnit.Lux);
#endif
#endif
            }
            else if (AssetUtils.IsOnURP())
            {
                light.intensity = AI.Config.cpLightIntensityURP * intensityMultiplier;
#if USE_URP
                light.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalLightData>();
#endif
            }
            else
            {
                light.intensity = AI.Config.cpLightIntensity * intensityMultiplier;
            }
        }

        /// <summary>
        /// Position light relative to camera and target
        /// </summary>
        public static void PositionLight(Light light, Camera camera, GameObject target, CustomPrefabPreviewGenerator.PrefabType prefabType = CustomPrefabPreviewGenerator.PrefabType.Model, bool isSecondaryLight = false)
        {
            Renderer[] renderers = target.GetComponentsInChildren<Renderer>();
            if (renderers == null || renderers.Length == 0) return;

            // Use accurate particle bounds calculation for particle systems
            Bounds bounds = PreviewBoundsCalculator.GetGlobalBounds(renderers, prefabType, target);

            light.transform.position = camera.transform.position;
            light.transform.LookAt(bounds.center);

            // Apply angle offset - use secondary light rotation if it's the secondary light
            if (isSecondaryLight)
            {
                // Secondary light uses its own rotation angles
                light.transform.rotation = Quaternion.Euler(
                    AI.Config.cpSecondaryLightRotationX,
                    AI.Config.cpSecondaryLightRotationY,
                    0f);
            }
            else
            {
                // Main light uses cpLightRotationX/Y
                light.transform.rotation = Quaternion.Euler(
                    AI.Config.cpLightRotationX,
                    AI.Config.cpLightRotationY,
                    0f);
            }
        }

        /// <summary>
        /// Position light for scene rendering
        /// </summary>
        public static void PositionLightForScene(Light light, Camera camera, Bounds bounds)
        {
            light.transform.position = camera.transform.position;
            light.transform.LookAt(bounds.center);

            // Apply angle offset
            light.transform.RotateAround(bounds.center, Vector3.forward, AI.Config.cpLightAngleX);
        }
    }
}

