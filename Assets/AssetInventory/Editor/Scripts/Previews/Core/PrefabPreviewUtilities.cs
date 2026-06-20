using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.VFX;
#if USE_TEXTMESHPRO || UNITY_2023_1_OR_NEWER
using TMPro;
#endif

namespace AssetInventory
{
    /// <summary>
    /// Static utility methods for prefab preview generation.
    /// Contains common functionality shared across UI, VFX, Particle, Model, and FBX generators.
    /// </summary>
    public static class PrefabPreviewUtilities
    {
        // Prefab type enum (matches CustomPrefabPreviewGenerator)
        public enum PrefabType { UI, VFX, Particles, Model, FBX }

        // ====== Detection Methods ======

        public static PrefabType DetectPrefabType(GameObject prefab)
        {
            // Count 3D renderers once - used for Canvas, VFX, and Particles checks
            MeshRenderer[] meshRenderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
            SkinnedMeshRenderer[] skinnedMeshRenderers = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            int total3DRenderers = meshRenderers.Length + skinnedMeshRenderers.Length;

            // Check for Canvas in children or parents (for temporary canvas case)
            bool hasCanvas = prefab.GetComponentInChildren<Canvas>() != null || prefab.GetComponentInParent<Canvas>() != null;

            if (hasCanvas)
            {
                // Check if prefab also has significant 3D content (MeshRenderers or SkinnedMeshRenderers)
                // Prefabs with both Canvas and 3D renderers should be treated as Model type,
                // as they're likely 3D scenes with UI overlays (e.g., adventure games, FPS demos)

                // If there are 3D renderers, treat as Model to use perspective camera and 3D bounds
                // Pure UI prefabs (Canvas only, no 3D content) will still be treated as UI
                if (total3DRenderers > 0)
                {
                    return PrefabType.Model;
                }

                return PrefabType.UI;
            }

            // Check for VFX, but also check for significant 3D content
            // Prefabs with both VFX AND substantial mesh content should be treated as Model
            if (prefab.GetComponentInChildren<VisualEffect>())
            {
                // If there are more than a few 3D renderers, this is primarily a 3D model
                // with VFX effects for decoration (magic effects, glows, etc.)
                if (total3DRenderers > 3)
                {
                    return PrefabType.Model;
                }

                return PrefabType.VFX;
            }

            // Check for particle systems, but also check for significant 3D content
            // Prefabs with both particles AND substantial mesh content (like a space station
            // with some particle effects for lighting/smoke) should be treated as Model
            if (prefab.GetComponentInChildren<ParticleSystem>())
            {
                // If there are more than a few 3D renderers, this is primarily a 3D model
                // with particle effects for decoration (dust, lights, smoke, etc.)
                if (total3DRenderers > 3)
                {
                    return PrefabType.Model;
                }

                return PrefabType.Particles;
            }

            return PrefabType.Model;
        }

        private static bool HasTrails(GameObject go)
        {
            // Check for TrailRenderer components
            if (go.GetComponentInChildren<TrailRenderer>() != null) return true;

            // Check for ParticleSystem Trails module
            ParticleSystem[] particleSystems = go.GetComponentsInChildren<ParticleSystem>();
            foreach (ParticleSystem ps in particleSystems)
            {
                if (ps.trails.enabled) return true;
            }

            return false;
        }

        private static bool HasSkinnedMeshRenderer(GameObject go)
        {
            return go.GetComponentInChildren<SkinnedMeshRenderer>() != null;
        }

        private static bool HasTextMeshPro(GameObject go)
        {
#if USE_TEXTMESHPRO || UNITY_2023_1_OR_NEWER
            return go.GetComponentInChildren<TMP_Text>(true) != null;
#else
            return false;
#endif
        }

        // ====== UI Utilities ======

        public static bool NeedsTemporaryCanvas(GameObject prefab)
        {
            // First check if this is primarily a 3D prefab with MeshRenderers/SkinnedMeshRenderers
            // 3D prefabs with UI overlays should NOT get a temporary canvas - they should be treated as Model type
            MeshRenderer[] meshRenderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
            SkinnedMeshRenderer[] skinnedMeshRenderers = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            int total3DRenderers = meshRenderers.Length + skinnedMeshRenderers.Length;

            if (total3DRenderers > 0)
            {
                // This is a 3D prefab (possibly with UI overlays) - don't create temporary canvas
                return false;
            }

            // Check if the prefab has any RectTransform components
            RectTransform[] rectTransforms = prefab.GetComponentsInChildren<RectTransform>(true);
            if (rectTransforms.Length == 0)
            {
                return false; // No UI elements at all
            }

            // Check if any of the RectTransforms lack a Canvas ancestor
            foreach (RectTransform rectTransform in rectTransforms)
            {
                Canvas canvasAncestor = rectTransform.GetComponentInParent<Canvas>();
                if (canvasAncestor == null)
                {
                    return true; // Found a RectTransform without Canvas - needs temporary canvas
                }
            }

            return false; // All RectTransforms have Canvas ancestors
        }

        public static GameObject CreateTemporaryCanvas(GameObject uiPrefab, Camera previewCamera, Scene targetScene)
        {
            // Create Canvas GameObject
            GameObject canvasGO = new GameObject("TemporaryCanvas");
            StageUtility.PlaceGameObjectInCurrentStage(canvasGO);
            SceneManager.MoveGameObjectToScene(canvasGO, targetScene);

            // Add Canvas component - use ScreenSpaceCamera for proper ContentSizeFitter support
            Canvas canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = previewCamera; // Assign the preview camera
            canvas.planeDistance = 1f; // Set canvas plane very close to camera (1 unit distance)

            // Add CanvasScaler for proper UI scaling
            UnityEngine.UI.CanvasScaler scaler = canvasGO.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;

            // Set canvas RectTransform to a reasonable size
            RectTransform canvasRect = canvasGO.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(1920, 1080); // Standard resolution for layout calculations
            canvasRect.localScale = Vector3.one;

            // Add GraphicRaycaster (required for proper UI rendering)
            canvasGO.AddComponent<UnityEngine.UI.GraphicRaycaster>();

            // Reparent the UI prefab under the canvas
            // Use 'false' to preserve local position/rotation/scale
            uiPrefab.transform.SetParent(canvasGO.transform, false);

            // DON'T modify the prefab's anchor configuration!
            // Many UI elements (like navigation bars) rely on specific anchoring
            // (e.g., anchored to top and stretched horizontally) for correct layout.
            // Forcing center anchors breaks their layout and child element sizing.
            // The bounds calculation will handle framing the visible content correctly.

            return canvasGO;
        }

        public static void ConfigureCanvasForPreview(GameObject prefab, Camera previewCamera)
        {
            // Find all Canvas components in the prefab (including in children AND parents)
            // This is important for temporary canvases that parent UI elements
            List<Canvas> canvasList = new List<Canvas>();

            // Check children
            canvasList.AddRange(prefab.GetComponentsInChildren<Canvas>(true));

            // Check parents (for temporary canvas case)
            Canvas parentCanvas = prefab.GetComponentInParent<Canvas>();
            if (parentCanvas != null && !canvasList.Contains(parentCanvas))
            {
                canvasList.Add(parentCanvas);
            }

            Canvas[] canvases = canvasList.ToArray();

            foreach (Canvas canvas in canvases)
            {
                RenderMode originalMode = canvas.renderMode;

                switch (originalMode)
                {
                    case RenderMode.ScreenSpaceCamera:
                        // Screen Space - Camera: Convert to World Space after layout is complete
                        // (We use ScreenSpaceCamera initially for ContentSizeFitter support)
                        ConvertScreenSpaceCameraToWorldSpace(canvas, previewCamera);
                        break;

                    case RenderMode.ScreenSpaceOverlay:
                        // Screen Space - Overlay: Must convert to World Space
                        ConvertOverlayToWorldSpace(canvas, previewCamera);
                        break;

                    case RenderMode.WorldSpace:
                        // World Space: Already compatible, no changes needed
                        break;
                }
            }
        }

        private static void ConvertScreenSpaceCameraToWorldSpace(Canvas canvas, Camera previewCamera)
        {
            // Convert from ScreenSpaceCamera to WorldSpace for rendering
            // This preserves the layout calculated by ContentSizeFitter while using world units for camera positioning
            canvas.renderMode = RenderMode.WorldSpace;

            RectTransform canvasRect = canvas.GetComponent<RectTransform>();
            if (canvasRect != null)
            {
                // Scale the canvas down to make UI elements reasonably sized in world space
                // Canvas was 1920x1080 units (pixels), scale down by 1/100 to make it ~19x10 world units
                // This makes individual UI elements (which are typically 50-200 pixels) become 0.5-2 world units
                float pixelToWorldScale = 0.01f; // 1 world unit = 100 pixels
                canvasRect.localScale = new Vector3(pixelToWorldScale, pixelToWorldScale, 1f);
            }
        }

        private static void ConvertOverlayToWorldSpace(Canvas canvas, Camera previewCamera)
        {
            // Get the RectTransform
            RectTransform canvasRect = canvas.GetComponent<RectTransform>();
            if (canvasRect == null)
            {
                Debug.LogWarning("[ConvertOverlayToWorldSpace] Canvas has no RectTransform, cannot convert");
                return;
            }

            // Determine the reference resolution to use
            Vector2 referenceResolution = new Vector2(1920, 1080); // Default HD resolution

            // Check for CanvasScaler to get the original reference resolution
            UnityEngine.UI.CanvasScaler scaler = canvas.GetComponent<UnityEngine.UI.CanvasScaler>();
            if (scaler != null && scaler.uiScaleMode == UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize)
            {
                referenceResolution = scaler.referenceResolution;
            }

            // Convert to World Space
            canvas.renderMode = RenderMode.WorldSpace;

            // Set the canvas size based on reference resolution
            canvasRect.sizeDelta = referenceResolution;

            // Position canvas at origin facing the camera direction
            canvasRect.position = Vector3.zero;
            canvasRect.rotation = Quaternion.identity;

            // Scale down to make it a reasonable size in world space
            float worldScale = 0.01f;
            canvasRect.localScale = new Vector3(worldScale, worldScale, worldScale);

            // Important: Update the canvas immediately to recalculate layout
            Canvas.ForceUpdateCanvases();
        }

        // ====== Particle System Utilities ======

        public static void HandleParticleSystems(GameObject go)
        {
            ParticleSystem[] particleSystems = go.GetComponentsInChildren<ParticleSystem>();
            if (particleSystems.Length == 0) return;

            // Check if any system is prewarmed
            bool hasPrewarmedSystem = false;
            foreach (ParticleSystem ps in particleSystems)
            {
                if (ps.main.prewarm)
                {
                    hasPrewarmedSystem = true;
                    break;
                }
            }

            // Calculate optimal time for all particle systems
            float optimalTime = CalculateOptimalParticleTime(go);

            // For prewarmed systems, simulate like normal systems for preview
            if (hasPrewarmedSystem)
            {
                foreach (ParticleSystem ps in particleSystems)
                {
                    ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(false);
                    ps.useAutoRandomSeed = false;
                    ps.randomSeed = AI.Config.cpParticleSeed;

                    ParticleSystem.MainModule main = ps.main;
                    float realTime = main.simulationSpeed > 0f ? optimalTime / main.simulationSpeed : optimalTime;

                    ps.Simulate(realTime, true, true, false);
                    ps.Play(true);
                }
                return;
            }

            // For non-prewarmed systems
            foreach (ParticleSystem ps in particleSystems)
            {
                ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                ps.Clear(false);
                ps.useAutoRandomSeed = false;
                ps.randomSeed = AI.Config.cpParticleSeed;

                ParticleSystem.MainModule main = ps.main;
                float realTime = main.simulationSpeed > 0f ? optimalTime / main.simulationSpeed : optimalTime;

                ps.Simulate(realTime, true, true, false);
                ps.Play(true);
            }
        }

        public static float CalculateOptimalParticleTime(GameObject go)
        {
            ParticleSystem[] particleSystems = go.GetComponentsInChildren<ParticleSystem>();
            if (particleSystems.Length == 0) return 0f;

            float maxStartLifetime = 0f;
            float maxStartDelay = 0f;
            float minDuration = float.MaxValue;

            foreach (ParticleSystem ps in particleSystems)
            {
                ParticleSystem.MainModule main = ps.main;

                // Get start lifetime
                float startLifetime = main.startLifetime.constant;
                if (main.startLifetime.mode == ParticleSystemCurveMode.TwoConstants)
                {
                    startLifetime = (main.startLifetime.constantMin + main.startLifetime.constantMax) / 2f;
                }
                maxStartLifetime = Mathf.Max(maxStartLifetime, startLifetime);

                // Get start delay
                float startDelay = main.startDelay.constant;
                if (main.startDelay.mode == ParticleSystemCurveMode.TwoConstants)
                {
                    startDelay = (main.startDelay.constantMin + main.startDelay.constantMax) / 2f;
                }
                maxStartDelay = Mathf.Max(maxStartDelay, startDelay);

                // Get duration
                float duration = main.duration;
                minDuration = Mathf.Min(minDuration, duration);
            }

            // Calculate optimal time
            float optimalTime;
            if (minDuration < maxStartLifetime)
            {
                // Short burst effect
                optimalTime = maxStartDelay + (maxStartLifetime * 0.5f);
            }
            else
            {
                // Continuous effect
                optimalTime = maxStartDelay + Mathf.Min(minDuration * 0.5f, maxStartLifetime);
            }

            return Mathf.Max(0.1f, optimalTime); // At least 0.1s
        }

        public static float GetParticleSystemDuration(GameObject go)
        {
            ParticleSystem[] particleSystems = go.GetComponentsInChildren<ParticleSystem>();
            if (particleSystems.Length == 0) return 1f;

            float maxDuration = 0f;
            foreach (ParticleSystem ps in particleSystems)
            {
                float duration = ps.main.duration + ps.main.startLifetime.constant + ps.main.startDelay.constant;
                maxDuration = Mathf.Max(maxDuration, duration);
            }

            return Mathf.Max(0.5f, maxDuration);
        }

        // ====== Other Utilities ======
        // Note: VFX utilities (HandleVFXSystems, GetVFXDuration) are in CustomPrefabPreviewGenerator

        public static void ForceLODLevel0(GameObject go)
        {
            LODGroup[] lodGroups = go.GetComponentsInChildren<LODGroup>();
            if (lodGroups.Length == 0) return;

            foreach (LODGroup lodGroup in lodGroups)
            {
                lodGroup.ForceLOD(0);
            }
        }

        /// <summary>
        /// Converts materials on a GameObject to use shaders compatible with the current render pipeline.
        /// Unity's built-in avatar models use the Standard shader which appears purple in URP/HDRP.
        /// Replaces incompatible materials with ones using the appropriate Lit shader.
        /// </summary>
        /// <param name="go">The GameObject whose materials should be converted.</param>
        public static void ConvertMaterialsToCurrentPipeline(GameObject go)
        {
            if (go == null) return;

            // Only need to convert if we're on SRP (URP or HDRP)
            bool isOnURP = AssetUtils.IsOnURP();
            bool isOnHDRP = AssetUtils.IsOnHDRP();

            if (!isOnURP && !isOnHDRP)
            {
                // Already on BIRP, no conversion needed
                return;
            }

            // Determine the correct shader for the current render pipeline
            Shader targetShader;
            if (isOnURP)
            {
                targetShader = Shader.Find("Universal Render Pipeline/Lit");
            }
            else
            {
                targetShader = Shader.Find("HDRP/Lit");
            }

            if (targetShader == null)
            {
                Debug.LogWarning("[Preview] Could not find Lit shader for current render pipeline");
                return;
            }

            // Replace materials on all renderers (including SkinnedMeshRenderer)
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in renderers)
            {
#if UNITY_2023_1_OR_NEWER
                // VFXRenderer does not allow setting materials - skip to avoid warnings
                if (renderer is VFXRenderer) continue;
#endif

                // Use sharedMaterials to avoid material leak warnings in edit mode
                Material[] sharedMats = renderer.sharedMaterials;

                // When materialImportMode is None, Unity creates renderers with an empty
                // materials array (length 0) even though the mesh has submeshes. Each
                // submesh without a material renders as magenta. Detect this by comparing
                // the material count against the mesh's submesh count and fill the gap.
                int requiredSlots = 0;
                if (sharedMats.Length == 0)
                {
                    if (renderer is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                    {
                        requiredSlots = smr.sharedMesh.subMeshCount;
                    }
                    else if (renderer is MeshRenderer)
                    {
                        MeshFilter mf = renderer.GetComponent<MeshFilter>();
                        if (mf != null && mf.sharedMesh != null)
                        {
                            requiredSlots = mf.sharedMesh.subMeshCount;
                        }
                    }

                    if (requiredSlots > 0)
                    {
                        sharedMats = new Material[requiredSlots]; // entries are null
                    }
                }

                Material[] newMaterials = new Material[sharedMats.Length];
                bool materialsChanged = false;

                for (int i = 0; i < sharedMats.Length; i++)
                {
                    Material originalMaterial = sharedMats[i];
                    if (originalMaterial == null)
                    {
                        // ParticleSystemRenderer commonly has a null second slot (trail material).
                        // Leave null slots untouched on particle/trail renderers — they are expected.
                        if (renderer is ParticleSystemRenderer || renderer is TrailRenderer)
                        {
                            newMaterials[i] = null;
                            continue;
                        }

                        // Null material slots render as magenta, which trips the error-shader
                        // detector (IsErrorShader). Replace with a neutral material that matches
                        // the preview background so null submeshes blend in instead.
                        Material bgMat = new Material(targetShader);
                        bgMat.hideFlags = HideFlags.HideAndDontSave;
                        Color bgColor = Color.gray; // fallback
                        string hex = AssetUtils.IsOnHDRP() ? AI.Config.cpBackgroundColorHDRP : AI.Config.cpBackgroundColor;
                        if (!string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString("#" + hex, out Color parsed))
                        {
                            bgColor = parsed;
                        }
                        if (bgMat.HasProperty("_BaseColor"))
                        {
                            bgMat.SetColor("_BaseColor", bgColor);
                        }
                        newMaterials[i] = bgMat;
                        materialsChanged = true;
                        continue;
                    }

                    Shader shader = originalMaterial.shader;
                    string shaderName = shader != null ? shader.name : "";
                    bool isParticleRenderer = renderer is ParticleSystemRenderer || renderer is TrailRenderer;

                    if (PipelineConverter.IsErrorShaderMaterial(originalMaterial))
                    {
                        if (PipelineConverter.TryCreateErrorShaderPreviewMaterial(originalMaterial, isParticleRenderer, isOnURP, isOnHDRP, out Material fallbackMaterial))
                        {
                            newMaterials[i] = fallbackMaterial;
                            materialsChanged = true;
                            continue;
                        }
                    }

                    // Check if the material uses a known Built-in Render Pipeline (BIRP) shader
                    // or a simple legacy custom transparent/cutout shader that can be remapped safely.
                    // Leave everything else (URP, HDRP, Shader Graph, opaque custom shaders, etc.) untouched.
                    if (isParticleRenderer && IsLegacyParticleShader(shaderName))
                    {
                        Material particleMat = CreateURPParticleMaterial(shaderName, originalMaterial, isOnURP);
                        if (particleMat != null)
                        {
                            newMaterials[i] = particleMat;
                            materialsChanged = true;
                            continue;
                        }
                    }

                    if (!PipelineConverter.CanSafelyAnalyzeMaterialForConversion(shaderName))
                    {
                        PipelineConverter.LogSkippedPreviewMaterialConversion(shaderName);

                        Material fallbackMaterial = new Material(targetShader);
                        fallbackMaterial.hideFlags = HideFlags.HideAndDontSave;
                        fallbackMaterial.name = $"{originalMaterial.name} Preview Fallback";
                        newMaterials[i] = fallbackMaterial;
                        materialsChanged = true;
                        continue;
                    }

                    PipelineConverter.MaterialConversionInfo conversionInfo = PipelineConverter.AnalyzeMaterialForConversion(originalMaterial, shaderName);
                    bool needsConversion = conversionInfo.ShouldConvert;

                    // Only convert recognized legacy shaders; leave everything else as-is
                    if (needsConversion)
                    {
                        // For particle shaders — or any BIRP/custom legacy shader on a ParticleSystemRenderer —
                        // use the URP/HDRP Particles shader so vertex colors and particle blend modes are respected.
                        if (isParticleRenderer && ShouldUseParticleMaterialForRendererConversion(shaderName, conversionInfo))
                        {
                            Material particleMat = CreateURPParticleMaterial(shaderName, originalMaterial, isOnURP);
                            if (particleMat != null)
                            {
                                newMaterials[i] = particleMat;
                                materialsChanged = true;
                            }
                            else
                            {
                                newMaterials[i] = originalMaterial;
                            }
                            continue;
                        }

                        // Create a new material with the compatible shader
                        Shader materialTargetShader = targetShader;
                        if (conversionInfo.CustomLegacySurface && isOnURP)
                        {
                            Shader simpleLit = Shader.Find("Universal Render Pipeline/Simple Lit");
                            if (simpleLit != null) materialTargetShader = simpleLit;
                        }
                        Material newMaterial = new Material(materialTargetShader);
                        newMaterial.hideFlags = HideFlags.HideAndDontSave; // Prevent leak warnings

                        // --- Color & albedo ---
                        if (originalMaterial.HasProperty("_Color") && newMaterial.HasProperty("_BaseColor"))
                        {
                            newMaterial.SetColor("_BaseColor", originalMaterial.GetColor("_Color"));
                        }

                        // --- Main texture ---
                        if (originalMaterial.HasProperty("_MainTex"))
                        {
                            Texture mainTex = originalMaterial.GetTexture("_MainTex");
                            if (mainTex != null && newMaterial.HasProperty("_BaseMap"))
                            {
                                newMaterial.SetTexture("_BaseMap", mainTex);
                            }

                            // Preserve tiling & offset
                            if (newMaterial.HasProperty("_BaseMap"))
                            {
                                newMaterial.SetTextureScale("_BaseMap", originalMaterial.GetTextureScale("_MainTex"));
                                newMaterial.SetTextureOffset("_BaseMap", originalMaterial.GetTextureOffset("_MainTex"));
                            }
                        }

                        // --- Metallic / Smoothness ---
                        if (originalMaterial.HasProperty("_Metallic") && newMaterial.HasProperty("_Metallic"))
                        {
                            newMaterial.SetFloat("_Metallic", originalMaterial.GetFloat("_Metallic"));
                        }
                        bool hasMetallicGlossMap = false;
                        if (originalMaterial.HasProperty("_MetallicGlossMap"))
                        {
                            Texture metallicMap = originalMaterial.GetTexture("_MetallicGlossMap");
                            if (metallicMap != null && newMaterial.HasProperty("_MetallicGlossMap"))
                            {
                                newMaterial.SetTexture("_MetallicGlossMap", metallicMap);
                                newMaterial.EnableKeyword("_METALLICSPECGLOSSMAP");
                                hasMetallicGlossMap = true;
                            }
                        }
                        // When a metallic gloss map is present, Unity's converter uses _GlossMapScale
                        // as the smoothness value (it controls the texture interpretation), not _Glossiness.
                        if (hasMetallicGlossMap && originalMaterial.HasProperty("_GlossMapScale") && newMaterial.HasProperty("_Smoothness"))
                        {
                            newMaterial.SetFloat("_Smoothness", originalMaterial.GetFloat("_GlossMapScale"));
                        }
                        else if (originalMaterial.HasProperty("_Glossiness") && newMaterial.HasProperty("_Smoothness"))
                        {
                            newMaterial.SetFloat("_Smoothness", originalMaterial.GetFloat("_Glossiness"));
                        }

                        // --- Normal map ---
                        if (originalMaterial.HasProperty("_BumpMap"))
                        {
                            Texture bumpMap = originalMaterial.GetTexture("_BumpMap");
                            if (bumpMap != null && newMaterial.HasProperty("_BumpMap"))
                            {
                                newMaterial.SetTexture("_BumpMap", bumpMap);
                                newMaterial.EnableKeyword("_NORMALMAP");
                            }
                            if (originalMaterial.HasProperty("_BumpScale") && newMaterial.HasProperty("_BumpScale"))
                            {
                                newMaterial.SetFloat("_BumpScale", originalMaterial.GetFloat("_BumpScale"));
                            }
                        }

                        // --- Occlusion ---
                        if (originalMaterial.HasProperty("_OcclusionMap"))
                        {
                            Texture occlusionMap = originalMaterial.GetTexture("_OcclusionMap");
                            if (occlusionMap != null && newMaterial.HasProperty("_OcclusionMap"))
                            {
                                newMaterial.SetTexture("_OcclusionMap", occlusionMap);
                                newMaterial.EnableKeyword("_OCCLUSIONMAP");
                            }
                            if (originalMaterial.HasProperty("_OcclusionStrength") && newMaterial.HasProperty("_OcclusionStrength"))
                            {
                                newMaterial.SetFloat("_OcclusionStrength", originalMaterial.GetFloat("_OcclusionStrength"));
                            }
                        }

                        // --- Emission ---
                        // Only enable emission if the original material actually had it enabled.
                        // BIRP Standard shader stores emission state in:
                        //   - _EMISSION keyword (most reliable)
                        //   - globalIlluminationFlags: EmissiveIsBlack (0x4) means emission is OFF
                        // Materials can have non-zero _EmissionColor or an _EmissionMap texture
                        // while emission is actually disabled (e.g. m_LightmapFlags: 4).
                        bool emissionWasEnabled = originalMaterial.IsKeywordEnabled("_EMISSION")
                            || (originalMaterial.globalIlluminationFlags & MaterialGlobalIlluminationFlags.EmissiveIsBlack) == 0;

                        if (emissionWasEnabled)
                        {
                            if (originalMaterial.HasProperty("_EmissionColor") && newMaterial.HasProperty("_EmissionColor"))
                            {
                                Color emission = originalMaterial.GetColor("_EmissionColor");
                                if (emission.maxColorComponent > 0f)
                                {
                                    newMaterial.SetColor("_EmissionColor", emission);
                                    newMaterial.EnableKeyword("_EMISSION");
                                    newMaterial.globalIlluminationFlags = originalMaterial.globalIlluminationFlags;
                                }
                            }
                            if (originalMaterial.HasProperty("_EmissionMap"))
                            {
                                Texture emissionMap = originalMaterial.GetTexture("_EmissionMap");
                                if (emissionMap != null && newMaterial.HasProperty("_EmissionMap"))
                                {
                                    newMaterial.SetTexture("_EmissionMap", emissionMap);
                                    newMaterial.EnableKeyword("_EMISSION");
                                }
                            }
                        }

                        // --- Alpha / transparency ---
                        if (originalMaterial.HasProperty("_Mode"))
                        {
                            int mode = (int)originalMaterial.GetFloat("_Mode");
                            // Standard shader: 0=Opaque, 1=Cutout, 2=Fade, 3=Transparent
                            if (mode == 1) // Cutout
                            {
                                newMaterial.SetFloat("_Surface", 0); // Opaque
                                newMaterial.SetFloat("_AlphaClip", 1);
                                newMaterial.SetFloat("_AlphaToMask", 1);
                                newMaterial.EnableKeyword("_ALPHATEST_ON");
                                newMaterial.SetOverrideTag("RenderType", "TransparentCutout");
                                newMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                                if (originalMaterial.HasProperty("_Cutoff") && newMaterial.HasProperty("_Cutoff"))
                                {
                                    newMaterial.SetFloat("_Cutoff", originalMaterial.GetFloat("_Cutoff"));
                                }
                            }
                            else if (mode >= 2) // Fade or Transparent
                            {
                                newMaterial.SetFloat("_Surface", 1); // Transparent
                                newMaterial.SetFloat("_Blend", mode == 2 ? 0 : 1); // Alpha/Premultiply
                                newMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                                newMaterial.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                                newMaterial.SetOverrideTag("RenderType", "Transparent");
                                newMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                                // Blend state matching URP BaseShaderGUI (preserveSpecular mode)
                                newMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                                newMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                                newMaterial.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
                                newMaterial.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                                newMaterial.SetInt("_ZWrite", 0);
                            }
                        }
                        else
                        {
                            float cutoff = originalMaterial.HasProperty("_Cutoff") ? originalMaterial.GetFloat("_Cutoff") : 0.5f;
                            PipelineConverter.ApplyConvertedSurfaceSettings(newMaterial, conversionInfo.SurfaceMode, cutoff, isOnURP, isOnHDRP);
                        }

                        PipelineConverter.ApplyDoubleSidedSettings(newMaterial, conversionInfo.DoubleSided, isOnURP, isOnHDRP);

                        newMaterials[i] = newMaterial;
                        materialsChanged = true;
                    }
                    else
                    {
                        newMaterials[i] = originalMaterial;
                    }
                }

                if (materialsChanged)
                {
                    renderer.sharedMaterials = newMaterials;
                }
            }
        }

        internal static bool ShouldUseParticleMaterialForRendererConversion(string shaderName, PipelineConverter.MaterialConversionInfo conversionInfo)
        {
            return conversionInfo.KnownBirpShader ||
                conversionInfo.CustomLegacySurface ||
                ShaderNameLooksLikeParticle(shaderName);
        }

        private static bool ShaderNameLooksLikeParticle(string shaderName)
        {
            return !string.IsNullOrEmpty(shaderName) &&
                shaderName.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Creates a URP/HDRP particle material that matches the blending mode of the original BIRP particle shader.
        /// </summary>
        private static Material CreateURPParticleMaterial(string originalShaderName, Material originalMaterial, bool isOnURP)
        {
            // Find the appropriate URP/HDRP particle shader
            string particleShaderName = isOnURP
                ? "Universal Render Pipeline/Particles/Unlit"
                : "HDRP/Particles/Unlit"; // HDRP equivalent

            Shader particleShader = Shader.Find(particleShaderName);
            if (particleShader == null) return null;

            Material newMat = new Material(particleShader);
            newMat.hideFlags = HideFlags.HideAndDontSave;

            // Copy base texture (URP uses _BaseMap, BIRP uses _MainTex)
            string textureProperty = originalMaterial.HasProperty("_MainTex") && originalMaterial.GetTexture("_MainTex") != null
                ? "_MainTex"
                : originalMaterial.HasProperty("_BaseMap") && originalMaterial.GetTexture("_BaseMap") != null
                    ? "_BaseMap"
                    : null;

            if (textureProperty != null)
            {
                Texture mainTex = originalMaterial.GetTexture(textureProperty);
                if (mainTex != null && newMat.HasProperty("_BaseMap"))
                {
                    newMat.SetTexture("_BaseMap", mainTex);
                    newMat.SetTextureScale("_BaseMap", originalMaterial.GetTextureScale(textureProperty));
                    newMat.SetTextureOffset("_BaseMap", originalMaterial.GetTextureOffset(textureProperty));
                }
            }

            // Copy color — legacy BIRP particle shaders (Particles/Additive, Alpha Blended, etc.)
            // use _TintColor with a 2x multiply convention: finalColor = 2 * _TintColor * texture.
            // URP Particles/Unlit uses _BaseColor directly (no 2x multiply), so we apply the factor.
            if (originalMaterial.HasProperty("_TintColor") && newMat.HasProperty("_BaseColor"))
            {
                Color tint = originalMaterial.GetColor("_TintColor");
                float tintMultiplier = UsesLegacyParticleDoubleTintConvention(originalShaderName) ? 2f : 1f;
                newMat.SetColor("_BaseColor", new Color(tint.r * tintMultiplier, tint.g * tintMultiplier, tint.b * tintMultiplier, tint.a));
            }
            else if (originalMaterial.HasProperty("_Color") && newMat.HasProperty("_BaseColor"))
            {
                newMat.SetColor("_BaseColor", originalMaterial.GetColor("_Color"));
            }

            // Determine blend mode from the original BIRP particle shader.
            // Particles/Standard Unlit uses _Mode: 0=Opaque, 1=Cutout, 2=Fade, 3=Transparent, 4=Additive
            // We also detect from shader name for simpler shaders (Particles/Additive, etc.)
            string normalizedShaderName = NormalizeLegacyParticleShaderName(originalShaderName);
            int blendMode = ResolveParticleBlendModeForShaderName(originalShaderName); // 0 = alpha blend in URP (maps to _Blend)

            if (normalizedShaderName == "Particles/Standard Unlit" || normalizedShaderName == "Particles/Standard Surface")
            {
                // Read _Mode from the material to determine blend mode
                if (originalMaterial.HasProperty("_Mode"))
                {
                    int mode = (int)originalMaterial.GetFloat("_Mode");
                    // Particles/Standard Unlit _Mode: 0=Opaque, 1=Cutout, 2=Fade, 3=Transparent, 4=Additive, 5=Subtractive, 6=Modulate
                    switch (mode)
                    {
                        case 0: // Opaque
                            newMat.SetFloat("_Surface", 0);
                            newMat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); // still needed for particle rendering
                            return newMat;
                        case 1: // Cutout
                            newMat.SetFloat("_Surface", 0);
                            newMat.EnableKeyword("_ALPHATEST_ON");
                            if (originalMaterial.HasProperty("_Cutoff") && newMat.HasProperty("_Cutoff"))
                                newMat.SetFloat("_Cutoff", originalMaterial.GetFloat("_Cutoff"));
                            return newMat;
                        case 4: // Additive
                            blendMode = 2;
                            break;
                        case 5: // Subtractive
                            blendMode = 4;
                            break;
                        case 6: // Modulate
                            blendMode = 3;
                            break;
                        default: // 2=Fade, 3=Transparent → alpha blend
                            blendMode = 0;
                            break;
                    }
                }
            }
            else if (normalizedShaderName == "Standard" || normalizedShaderName == "Standard (Specular setup)")
            {
                // Standard shader on a ParticleSystemRenderer — read _Mode for blend mode
                // Standard _Mode: 0=Opaque, 1=Cutout, 2=Fade, 3=Transparent
                if (originalMaterial.HasProperty("_Mode"))
                {
                    int mode = (int)originalMaterial.GetFloat("_Mode");
                    switch (mode)
                    {
                        case 0:
                            newMat.SetFloat("_Surface", 0);
                            return newMat;
                        case 1:
                            newMat.SetFloat("_Surface", 0);
                            newMat.EnableKeyword("_ALPHATEST_ON");
                            if (originalMaterial.HasProperty("_Cutoff") && newMat.HasProperty("_Cutoff"))
                                newMat.SetFloat("_Cutoff", originalMaterial.GetFloat("_Cutoff"));
                            return newMat;
                        case 2: // Fade → alpha blend
                            blendMode = 0;
                            break;
                        case 3: // Transparent → alpha blend
                            blendMode = 0;
                            break;
                    }
                }
            }
            // Other custom particle shaders keep the blend mode inferred from their shader name.

            // Apply URP particle transparent settings.
            // The shader uses Blend[_SrcBlend][_DstBlend], [_SrcBlendAlpha][_DstBlendAlpha] directly.
            // We must set these to the correct values since no ShaderGUI ValidateMaterial() runs at runtime.
            // Values derived from URP BaseShaderGUI.SetupMaterialBlendModeInternal().
            newMat.SetFloat("_Surface", 1); // Transparent
            newMat.SetFloat("_Blend", blendMode); // 0=Alpha, 2=Additive, 3=Multiply
            newMat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");

            switch (blendMode)
            {
                case 0: // Alpha
                    newMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    newMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    newMat.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
                    newMat.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    newMat.SetInt("_ZWrite", 0);
                    break;
                case 2: // Additive
                    // preserveSpecular lifts srcBlend to One; shader handles alpha multiply internally
                    newMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                    newMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
                    newMat.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
                    newMat.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
                    newMat.SetInt("_ZWrite", 0);
                    newMat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                    break;
                case 3: // Multiply
                    newMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.DstColor);
                    newMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
                    newMat.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.Zero);
                    newMat.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
                    newMat.SetInt("_ZWrite", 0);
                    newMat.EnableKeyword("_ALPHAMODULATE_ON");
                    break;
                default: // Fallback: alpha blend
                    newMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    newMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    newMat.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
                    newMat.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    newMat.SetInt("_ZWrite", 0);
                    break;
            }

            // Set render queue to -1 (auto) like Unity's converter does
            newMat.renderQueue = -1;

            return newMat;
        }

        internal static int ResolveParticleBlendModeForShaderName(string shaderName)
        {
            string normalizedShaderName = NormalizeLegacyParticleShaderName(shaderName);

            if (normalizedShaderName == "Particles/Additive" || normalizedShaderName == "Particles/Additive (Soft)" || ContainsIgnoreCase(normalizedShaderName, "Additive"))
            {
                return 2; // Additive
            }

            if (normalizedShaderName == "Particles/Multiply" || normalizedShaderName == "Particles/Multiply (Double)" ||
                ContainsIgnoreCase(normalizedShaderName, "Multiply") || ContainsIgnoreCase(normalizedShaderName, "Modulate"))
            {
                return 3; // Multiply
            }

            return 0; // Alpha
        }

        private static bool IsLegacyParticleShader(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return false;
            if (shaderName.StartsWith("Legacy Shaders/Particles/")) return true;
            if (shaderName.StartsWith("Mobile/Particles/")) return true;
            if (shaderName.StartsWith("Particles/")) return true;
            return false;
        }

        private static bool UsesLegacyParticleDoubleTintConvention(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return false;
            string normalizedShaderName = NormalizeLegacyParticleShaderName(shaderName);
            return normalizedShaderName.StartsWith("Particles/", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsIgnoreCase(string value, string search)
        {
            return !string.IsNullOrEmpty(value) &&
                value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string NormalizeLegacyParticleShaderName(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return string.Empty;
            if (shaderName.StartsWith("Legacy Shaders/")) return shaderName.Substring("Legacy Shaders/".Length);
            if (shaderName.StartsWith("Mobile/")) return shaderName.Substring("Mobile/".Length);
            return shaderName;
        }

        /// <summary>
        /// Returns true if the shader name is a known Built-in Render Pipeline shader
        /// that should be converted when running on URP or HDRP.
        /// </summary>
        internal static bool IsBIRPShader(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return false;

            // Exact matches for the main BIRP lit shaders that render incorrectly on SRP
            if (shaderName == "Standard" || shaderName == "Standard (Specular setup)") return true;

            // Legacy shader families that are definitely BIRP-only.
            // Legacy particle shaders are converted only when assigned to particle/trail renderers,
            // where SRP particle shaders are needed to preserve particle vertex colors.
            if (shaderName.StartsWith("Legacy Shaders/") && !shaderName.StartsWith("Legacy Shaders/Particles/")) return true;

            // Modern BIRP particle shaders that need conversion (Standard Surface/Unlit).
            // Legacy simple particle shaders are handled by renderer-contextual particle conversion above.
            if (shaderName == "Particles/Standard Surface" || shaderName == "Particles/Standard Unlit") return true;

            // Mobile shaders (but exclude Mobile/Particles/* — these render correctly in URP/HDRP)
            if (shaderName.StartsWith("Mobile/") && !shaderName.StartsWith("Mobile/Particles/")) return true;

            // Unity internal editor shaders (e.g. "Editor/Bumped Specular" used by DefaultAvatar.fbx)
            // These are BIRP-only and render as pink on URP/HDRP.
            if (shaderName.StartsWith("Editor/")) return true;

            return false;
        }
    }
}
