using ImpossibleRobert.Common;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.VFX;
using UnityEngine.UI;
using System.Threading.Tasks;
using Unity.EditorCoroutines.Editor;
using Object = UnityEngine.Object;
#if USE_TEXTMESHPRO || UNITY_2023_1_OR_NEWER
using TMPro;
#endif

namespace AssetInventory
{
    // Custom preview generator for prefabs
    public static class CustomPrefabPreviewGenerator
    {
        private const int DETECTION_SIZE = 128; // Small size for fast detection

        public enum PrefabType { UI, VFX, Particles, Model, FBX, Anim }

        // Cache of VFX assets that have been compiled during this session
        // Used to skip compilation wait for already-compiled VFX shaders
        private static readonly HashSet<string> _compiledVFXAssets = new HashSet<string>();

        // Gate to serialize VFX preview generation: Unity only allows one untitled
        // additive scene at a time, so parallel VFX coroutines must wait their turn.
        private static bool _vfxSceneInUse;
        private const float VFX_SCENE_WAIT_TIMEOUT = 30f;

        // Reason message for when no visible content is detected during preview generation
        public const string NO_VISIBLE_CONTENT_REASON = "No visible content detected";

        public static void ClearVFXCache()
        {
            _compiledVFXAssets.Clear();
        }

        private static bool IsVFXAlreadyCompiled(VisualEffect[] vfxComponents)
        {
            foreach (VisualEffect vfx in vfxComponents)
            {
                if (vfx.visualEffectAsset == null) continue;
                string assetPath = AssetDatabase.GetAssetPath(vfx.visualEffectAsset);
                if (!string.IsNullOrEmpty(assetPath) && _compiledVFXAssets.Contains(assetPath))
                {
                    return true;
                }
            }
            return false;
        }

        private static void MarkVFXAsCompiled(VisualEffect[] vfxComponents)
        {
            foreach (VisualEffect vfx in vfxComponents)
            {
                if (vfx.visualEffectAsset == null) continue;
                string assetPath = AssetDatabase.GetAssetPath(vfx.visualEffectAsset);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    _compiledVFXAssets.Add(assetPath);
                }
            }
        }

        public static Task<Texture2D> Create(string prefabPath, int size = 128, int frameCount = 1, PreviewRequest previewRequest = null)
        {
            TaskCompletionSource<Texture2D> tcs = new TaskCompletionSource<Texture2D>();

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                Debug.LogError($"Failed to load prefab from: {prefabPath}");
                tcs.SetResult(null);
                return tcs.Task;
            }

            // Start the coroutine to handle the preview generation
            EditorCoroutineUtility.StartCoroutineOwnerless(ProcessPrefab(prefab, prefabPath, size, frameCount, tcs, 0, previewRequest));

            return tcs.Task;
        }

        public static Task<Texture2D> Create(GameObject prefab, int size = 128, int frameCount = 1, string prefabPath = null, PreviewRequest previewRequest = null)
        {
            TaskCompletionSource<Texture2D> tcs = new TaskCompletionSource<Texture2D>();

            if (prefab == null)
            {
                Debug.LogError("Prefab GameObject is null");
                tcs.SetResult(null);
                return tcs.Task;
            }

            // If prefabPath not provided, try to resolve it from the GameObject
            if (string.IsNullOrEmpty(prefabPath))
            {
                prefabPath = AssetDatabase.GetAssetPath(prefab);
            }

            // Start the coroutine to handle the preview generation
            EditorCoroutineUtility.StartCoroutineOwnerless(ProcessPrefab(prefab, prefabPath, size, frameCount, tcs, 0, previewRequest));

            return tcs.Task;
        }

        // FBX-specific entry point with animation count and preview request
        public static Task<Texture2D> CreateFBX(string fbxPath, int size = 128, int frameCount = 1, int animationCount = 0, PreviewRequest previewRequest = null)
        {
            TaskCompletionSource<Texture2D> tcs = new TaskCompletionSource<Texture2D>();

            GameObject fbxModel = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (fbxModel == null)
            {
                Debug.LogError($"Failed to load FBX from: {fbxPath}");
                tcs.SetResult(null);
                return tcs.Task;
            }

            // Start the coroutine with FBX-specific metadata
            EditorCoroutineUtility.StartCoroutineOwnerless(ProcessPrefab(fbxModel, fbxPath, size, frameCount, tcs, animationCount, previewRequest));

            return tcs.Task;
        }

        /// <summary>
        /// Creates a preview for a standalone .anim (AnimationClip) file.
        /// Unlike FBX, .anim files contain only animation data and require a model to preview on.
        /// </summary>
        /// <param name="animPath">Path to the .anim file</param>
        /// <param name="size">Preview size in pixels</param>
        /// <param name="frameCount">Number of frames (1 for static, more for animated)</param>
        /// <param name="previewRequest">Optional preview request for metadata storage</param>
        /// <param name="dependencies">List of dependencies that may contain FBX models</param>
        public static Task<Texture2D> CreateAnim(string animPath, int size = 128, int frameCount = 1, PreviewRequest previewRequest = null, List<AssetFile> dependencies = null)
        {
            TaskCompletionSource<Texture2D> tcs = new TaskCompletionSource<Texture2D>();

            // Load the AnimationClip
            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(animPath);
            if (clip == null)
            {
                Debug.LogError($"[Anim Preview] Failed to load AnimationClip from: {animPath}");
                tcs.SetResult(null);
                return tcs.Task;
            }

            // Check for empty or invalid clips
            if (clip.empty || clip.length <= 0)
            {
                Debug.LogWarning($"[Anim Preview] AnimationClip is empty or has no length: {animPath}");
                tcs.SetResult(null);
                return tcs.Task;
            }

            // Start the coroutine to handle animation preview generation
            EditorCoroutineUtility.StartCoroutineOwnerless(ProcessAnimClip(clip, animPath, size, frameCount, tcs, previewRequest, dependencies));

            return tcs.Task;
        }

        /// <summary>
        /// Coroutine to process an AnimationClip and generate preview frames.
        /// </summary>
        private static IEnumerator ProcessAnimClip(
            AnimationClip clip,
            string animPath,
            int size,
            int frameCount,
            TaskCompletionSource<Texture2D> tcs,
            PreviewRequest previewRequest,
            List<AssetFile> dependencies)
        {
            GameObject modelInstance;
            CustomPreviewStage stage = null;
            List<Texture2D> frames = new List<Texture2D>();
            Scene previewScene = default;

            try
            {
                // Resolve a model to preview the animation on
                modelInstance = ResolveModelForAnimation(clip, dependencies, out Avatar avatar, out string modelSourcePath);

                if (modelInstance == null)
                {
                    Debug.LogWarning($"[Anim Preview] Could not resolve any model for animation: {animPath}");
                    tcs.SetResult(null);
                    yield break;
                }

                // Convert materials to current render pipeline
                PrefabPreviewUtilities.ConvertMaterialsToCurrentPipeline(modelInstance);

                // Set up or get Animator component
                Animator animator = modelInstance.GetComponent<Animator>();
                if (animator == null)
                {
                    animator = modelInstance.AddComponent<Animator>();
                }

                // Assign avatar if we have one
                if (avatar != null && avatar.isValid)
                {
                    animator.avatar = avatar;
                }

                // Configure animator
                animator.enabled = true;
                animator.speed = 0f;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                // Create a temporary RuntimeAnimatorController
                UnityEditor.Animations.AnimatorController tempController = new UnityEditor.Animations.AnimatorController();
                tempController.name = $"TempAnimController_{modelInstance.GetStableId()}";
                tempController.AddLayer("Base Layer");
                UnityEditor.Animations.AnimatorState state = tempController.layers[0].stateMachine.AddState(clip.name);
                state.motion = clip;
                tempController.layers[0].stateMachine.defaultState = state;
                animator.runtimeAnimatorController = tempController;

                // Create preview scene and stage
                previewScene = EditorSceneManager.NewPreviewScene();
                stage = ScriptableObject.CreateInstance<CustomPreviewStage>();
                stage.SetScene(previewScene);
                stage.PrefabType = PrefabType.Anim;
                stage.FBXAnimationClip = clip;

                // Setup the scene with the model
                stage.SetupScene(modelInstance);

                // Give a few frames for initialization
                for (int i = 0; i < 3; i++)
                {
                    EditorApplication.QueuePlayerLoopUpdate();
                    yield return null;
                }

                // Check if model has renderable geometry
                Renderer[] renderers = stage.InstantiatedPrefab.GetComponentsInChildren<Renderer>();
                bool hasGeometry = renderers != null && renderers.Length > 0;

                if (!hasGeometry)
                {
                    // No geometry - use bone visualization
                    stage.NeedsBoneVisualization = true;
                    VisualizeSkeletonBones(stage.InstantiatedPrefab);
                }

                // Sample initial frame
                SampleAnimationPose(stage.InstantiatedPrefab, clip, 0f);
                Physics.SyncTransforms();

                // Force SkinnedMeshRenderer updates
                SkinnedMeshRenderer[] skinnedMeshes = stage.InstantiatedPrefab.GetComponentsInChildren<SkinnedMeshRenderer>();
                foreach (SkinnedMeshRenderer smr in skinnedMeshes)
                {
                    if (smr != null && smr.enabled)
                    {
                        smr.forceMatrixRecalculationPerRender = true;
                        _ = smr.bounds;
                    }
                }

                // Calculate bounds for camera framing using animation bounds
                Bounds animBounds = PreviewBoundsCalculator.GetFBXAnimationBoundsOverTime(stage.InstantiatedPrefab, clip);

                // Use the same camera positioning logic as FBX animations for consistent framing
                // FBX animation bounds may be inflated, so use tighter framing (fillFraction=0.88f)
                float distance = PreviewCameraSetup.CalculateCameraDistance(animBounds, stage.Camera, 0f, AI.Config.cpCameraAngleX, AI.Config.cpCameraAngleY, 0.88f);

                // Position camera above the animation bounds center
                stage.Camera.transform.position = new Vector3(animBounds.center.x, animBounds.center.y + distance, animBounds.center.z);
                stage.Camera.transform.LookAt(animBounds.center);

                // Apply custom camera angles (same as FBX animation positioning)
                stage.Camera.transform.RotateAround(animBounds.center, Vector3.left, AI.Config.cpCameraAngleX);
                stage.Camera.transform.RotateAround(animBounds.center, Vector3.up, AI.Config.cpCameraAngleY);

                // Compensate for perspective shift when viewing at an angle
                float verticalAngleRad = AI.Config.cpCameraAngleX * Mathf.Deg2Rad;
                float perspectiveCompensation = animBounds.extents.y * Mathf.Sin(verticalAngleRad) * 0.15f;
                Vector3 lookTarget = animBounds.center + Vector3.up * perspectiveCompensation;
                stage.Camera.transform.LookAt(lookTarget);

                // Reposition lights relative to camera position
                PreviewLightingSetup.PositionLight(stage.Light, stage.Camera, stage.InstantiatedPrefab, stage.PrefabType);
                if (AI.Config.cpUseSecondaryLight && stage.SecondaryLight != null && stage.SecondaryLight.enabled)
                {
                    PreviewLightingSetup.PositionLight(stage.SecondaryLight, stage.Camera, stage.InstantiatedPrefab, stage.PrefabType, isSecondaryLight: true);
                }

                // Native render size (with super-sampling)
                int nativeSize = size * AI.Config.cpSuperSamplingMultiplier;

                // Capture frames
                float animDuration = clip.length;

                for (int i = 0; i < frameCount; i++)
                {
                    // Calculate normalized time through animation
                    float normalizedTime = frameCount > 1 ? (float)i / (frameCount - 1) : 0.5f;
                    float sampleTime = normalizedTime * animDuration;

                    // Sample the animation
                    SampleAnimationPose(stage.InstantiatedPrefab, clip, sampleTime);
                    Physics.SyncTransforms();

                    // Force SkinnedMeshRenderer updates
                    foreach (SkinnedMeshRenderer smr in skinnedMeshes)
                    {
                        if (smr != null && smr.enabled)
                        {
                            smr.forceMatrixRecalculationPerRender = true;
                            _ = smr.bounds;
                        }
                    }

                    // Update bone visualization if needed
                    if (stage.NeedsBoneVisualization)
                    {
                        ClearBoneVisualization(stage.InstantiatedPrefab);
                        VisualizeSkeletonBones(stage.InstantiatedPrefab);
                    }

                    // Render frame
                    Texture2D frame = stage.RenderFrame(nativeSize, nativeSize);
                    if (frame != null)
                    {
                        frames.Add(frame);
                    }

                    EditorApplication.QueuePlayerLoopUpdate();
                    yield return null;
                }

                // Extract and store AnimData metadata
                if (previewRequest != null && string.IsNullOrEmpty(previewRequest.FileData))
                {
                    AnimData animData = new AnimData
                    {
                        name = clip.name,
                        length = clip.length,
                        isHumanMotion = clip.isHumanMotion,
                        isLooping = clip.isLooping,
                        frameRate = clip.frameRate,
                        referencedModelGuid = !string.IsNullOrEmpty(modelSourcePath) ? AssetDatabase.AssetPathToGUID(modelSourcePath) : null
                    };
                    previewRequest.FileData = JsonConvert.SerializeObject(animData);
                }

                // Assemble result texture
                Texture2D resultTexture = AssembleResultTexture(frames, size, frameCount);
                tcs.SetResult(resultTexture);
            }
            finally
            {
                // Cleanup - must destroy the instantiated model since it was created by ResolveModelForAnimation
                // (not loaded as a prefab asset), so RestoreEnvironment() would just disable it instead of destroying
                GameObject instanceToDestroy = stage?.InstantiatedPrefab;

                if (stage != null)
                {
                    stage.RestoreEnvironment();
                }

                // Explicitly destroy the model instance if it still exists (RestoreEnvironment may have disabled but not destroyed it)
                if (instanceToDestroy != null)
                {
                    Object.DestroyImmediate(instanceToDestroy);
                }
            }
        }

        /// <summary>
        /// Resolves a model to use for animation preview.
        /// Tries dependencies first, then falls back to Unity's default models.
        /// </summary>
        private static GameObject ResolveModelForAnimation(AnimationClip clip, List<AssetFile> dependencies, out Avatar avatar, out string modelPath)
        {
            avatar = null;
            modelPath = null;
            GameObject modelInstance = null;

            // First, try to find an FBX model in dependencies
            if (dependencies != null && dependencies.Count > 0)
            {
                foreach (AssetFile dep in dependencies)
                {
                    if (dep.Type?.ToLowerInvariant() == "fbx")
                    {
                        // Try to load the FBX model
                        string fbxPath = dep.ProjectPath;
                        if (string.IsNullOrEmpty(fbxPath) && !string.IsNullOrEmpty(dep.Guid))
                        {
                            fbxPath = AssetDatabase.GUIDToAssetPath(dep.Guid);
                        }

                        if (!string.IsNullOrEmpty(fbxPath))
                        {
                            GameObject fbxModel = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
                            if (fbxModel != null)
                            {
                                modelInstance = Object.Instantiate(fbxModel);
                                modelInstance.name = fbxModel.name;
                                modelPath = fbxPath;

                                // Try to get Avatar from the FBX
                                ModelImporter importer = UnityEditor.AssetImporter.GetAtPath(fbxPath) as ModelImporter;
                                if (importer != null)
                                {
                                    if (importer.sourceAvatar != null)
                                    {
                                        avatar = importer.sourceAvatar;
                                    }
                                    else
                                    {
                                        // Load embedded Avatar
                                        Object[] fbxAssets = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
                                        avatar = fbxAssets.OfType<Avatar>().FirstOrDefault();
                                    }
                                }

                                // Verify avatar compatibility with clip
                                if (clip.isHumanMotion && (avatar == null || !avatar.isValid || !avatar.isHuman))
                                {
                                    // Humanoid clip but no valid humanoid avatar - try another dependency
                                    Object.DestroyImmediate(modelInstance);
                                    modelInstance = null;
                                    avatar = null;
                                    modelPath = null;
                                    continue;
                                }

                                return modelInstance;
                            }
                        }
                    }
                }
            }

            // Fallback to Unity's default models
            GameObject defaultModel = clip.isHumanMotion ? GetUnityHumanoidModel() : GetUnityGenericModel();
            if (defaultModel != null)
            {
                modelInstance = Object.Instantiate(defaultModel);
                modelInstance.name = clip.isHumanMotion ? "UnityHumanoidPreview" : "UnityGenericPreview";

                // Get avatar from default model
                Animator defaultAnimator = modelInstance.GetComponent<Animator>();
                if (defaultAnimator != null && defaultAnimator.avatar != null)
                {
                    avatar = defaultAnimator.avatar;
                }
            }

            return modelInstance;
        }

        // ====== Extracted Helper Methods (Refactored from ProcessPrefab) ======

        /// <summary>
        /// Check if prefab should skip frame capture (no visible content)
        /// </summary>
        private static bool ShouldSkipFrameCapture(CustomPreviewStage stage, out string reason)
        {
            reason = null;

            // Fast-path: Skip detection for types guaranteed to have content
            // Particles/VFX may not be visible immediately before simulation
            // SkinnedMeshRenderers require bone initialization
            // TextMeshPro generates meshes asynchronously
            // FBX files may have materials that don't render in quick detection pass
            bool hasSkinnedMesh = HasSkinnedMeshRenderer(stage.InstantiatedPrefab);
            bool hasTextMeshPro = HasTextMeshPro(stage.InstantiatedPrefab);
            bool skipDetection = stage.PrefabType == PrefabType.Particles ||
                stage.PrefabType == PrefabType.VFX ||
                stage.PrefabType == PrefabType.FBX ||
                hasSkinnedMesh ||
                hasTextMeshPro;

            if (skipDetection)
            {
                return false; // Don't skip capture for these types
            }

            // Perform detection render
            bool hasContent = HasVisibleContentAtCurrentState(stage, DETECTION_SIZE, out float _);

            if (!hasContent)
            {
                reason = NO_VISIBLE_CONTENT_REASON;
                return true; // Skip frame capture
            }

            return false; // Don't skip
        }

        private static bool HasVisibleContentAtCurrentState(CustomPreviewStage stage, int detectionSize, out float detectionPercentage)
        {
            detectionPercentage = 0f;

            if (stage == null)
            {
                return false;
            }

            Texture2D detectionTexture = stage.RenderFrameForDetection(detectionSize, detectionSize);
            if (detectionTexture == null)
            {
                return false;
            }

            try
            {
                return detectionTexture.HasVisibleContent(new Color(1f, 0f, 1f, 0f), out detectionPercentage);
            }
            finally
            {
                Object.DestroyImmediate(detectionTexture);
            }
        }

        internal static bool ShouldRetryVFXPreviewWithEvent(List<Texture2D> frames, Color backgroundColor, out float bestVisiblePercentage)
        {
            return !HasVisiblePreviewFrame(frames, backgroundColor, out bestVisiblePercentage);
        }

        internal static string SelectFirstCustomVFXEventName(IEnumerable<string> eventNames)
        {
            if (eventNames == null) return null;

            foreach (string eventName in eventNames)
            {
                if (string.IsNullOrWhiteSpace(eventName)) continue;
                if (IsDefaultVFXEventName(eventName)) continue;

                return eventName;
            }

            return null;
        }

        private static bool HasVisiblePreviewFrame(List<Texture2D> frames, Color backgroundColor, out float bestVisiblePercentage)
        {
            bestVisiblePercentage = 0f;
            if (frames == null || frames.Count == 0) return false;

            foreach (Texture2D frame in frames)
            {
                if (frame == null) continue;

                if (frame.HasVisibleContent(backgroundColor, out float pct))
                {
                    bestVisiblePercentage = Mathf.Max(bestVisiblePercentage, pct);
                    return true;
                }

                bestVisiblePercentage = Mathf.Max(bestVisiblePercentage, pct);
            }

            return false;
        }

        private static bool IsDefaultVFXEventName(string eventName)
        {
            return string.Equals(eventName, VisualEffectAsset.PlayEventName, StringComparison.Ordinal) ||
                string.Equals(eventName, VisualEffectAsset.StopEventName, StringComparison.Ordinal) ||
                string.Equals(eventName, "OnPlay", StringComparison.Ordinal) ||
                string.Equals(eventName, "OnStop", StringComparison.Ordinal) ||
                string.Equals(eventName, "stop", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetFirstCustomVFXEventName(VisualEffect vfx, out string eventName)
        {
            eventName = null;
            if (vfx == null || vfx.visualEffectAsset == null) return false;

            List<string> eventNames = new List<string>();
            vfx.visualEffectAsset.GetEvents(eventNames);
            eventName = SelectFirstCustomVFXEventName(eventNames);
            return !string.IsNullOrEmpty(eventName);
        }

        private static bool TryGetAnyCustomVFXEventName(VisualEffect[] vfxComponents, out string eventName)
        {
            eventName = null;
            if (vfxComponents == null) return false;

            foreach (VisualEffect vfx in vfxComponents)
            {
                if (TryGetFirstCustomVFXEventName(vfx, out eventName))
                {
                    return true;
                }
            }

            return false;
        }

        private static void DestroyFrames(List<Texture2D> frames)
        {
            if (frames == null) return;

            foreach (Texture2D frame in frames)
            {
                if (frame != null)
                {
                    Object.DestroyImmediate(frame);
                }
            }

            frames.Clear();
        }

        private static bool SimulateVFXToTime(VisualEffect[] vfxComponents, float targetTime, bool triggerFirstCustomEvent)
        {
            if (vfxComponents == null || vfxComponents.Length == 0) return false;

            bool triggeredEvent = false;
            float timeStep = 0.016f;
            uint steps = (uint)Mathf.Max(1, targetTime / timeStep);

            foreach (VisualEffect vfx in vfxComponents)
            {
                if (vfx == null) continue;

                vfx.Reinit();
                vfx.Play();

                if (triggerFirstCustomEvent && TryGetFirstCustomVFXEventName(vfx, out string eventName))
                {
                    vfx.SendEvent(eventName);
                    triggeredEvent = true;
                }

                vfx.Simulate(timeStep, steps);
            }

            return !triggerFirstCustomEvent || triggeredEvent;
        }

        private static IEnumerator CaptureVFXFramesWithFirstCustomEvent(CustomPreviewStage stage, int nativeSize, int frameCount, List<Texture2D> frames, string prefabPath)
        {
            if (stage == null || stage.InstantiatedPrefab == null || frames == null) yield break;

            VisualEffect[] vfxComponents = stage.InstantiatedPrefab.GetComponentsInChildren<VisualEffect>();
            if (!TryGetAnyCustomVFXEventName(vfxComponents, out string _)) yield break;

            float duration = GetVFXDuration(stage.InstantiatedPrefab);
            if (duration <= 0f) duration = Mathf.Min(1f, AI.Config.cpVFXMaxDuration);

            if (frameCount > 1)
            {
                float startOffset = Mathf.Min(duration * 0.05f, 0.03f);
                float framingTime = Mathf.Clamp(duration * 0.35f, startOffset, duration);
                SimulateVFXToTime(vfxComponents, framingTime, true);

                for (int w = 0; w < 3; w++)
                {
                    EditorApplication.QueuePlayerLoopUpdate();
                    yield return null;
                }

                PositionCameraForCurrentVFXState(stage, duration);

                for (int i = 0; i < frameCount; i++)
                {
                    float t = frameCount == 1 ? 0.5f : i / (float)(frameCount - 1);
                    float time = startOffset + (duration - startOffset) * t;
                    SimulateVFXToTime(vfxComponents, time, true);

                    int waitCount = i < 3 ? 2 : 1;
                    for (int w = 0; w < waitCount; w++)
                    {
                        EditorApplication.QueuePlayerLoopUpdate();
                        yield return null;
                    }

                    Texture2D frame = stage.RenderFrame(nativeSize, nativeSize);
                    if (frame != null)
                    {
                        frames.Add(frame);
                    }
                }

                yield break;
            }

            float[] samplePercentages = {0.10f, 0.25f, 0.40f, 0.55f, 0.70f, 0.85f};
            List<Texture2D> candidateFrames = new List<Texture2D>();
            Color bgColor = stage.Camera.backgroundColor;

            foreach (float pct in samplePercentages)
            {
                float targetTime = duration * pct;
                SimulateVFXToTime(vfxComponents, targetTime, true);

                for (int w = 0; w < 3; w++)
                {
                    EditorApplication.QueuePlayerLoopUpdate();
                    yield return null;
                }

                PositionCameraForCurrentVFXState(stage, duration);

                Texture2D candidateFrame = stage.RenderFrame(nativeSize, nativeSize);
                if (candidateFrame != null)
                {
                    candidateFrames.Add(candidateFrame);
                }
            }

            if (candidateFrames.Count == 0) yield break;

            float bestScore = -1f;
            int bestFrameIndex = 0;
            for (int i = 0; i < candidateFrames.Count; i++)
            {
                candidateFrames[i].HasVisibleContent(bgColor, out float visiblePct);
                if (visiblePct > bestScore)
                {
                    bestScore = visiblePct;
                    bestFrameIndex = i;
                }
            }

            Texture2D bestFrame = candidateFrames[bestFrameIndex];
            for (int i = 0; i < candidateFrames.Count; i++)
            {
                if (i != bestFrameIndex)
                {
                    Object.DestroyImmediate(candidateFrames[i]);
                }
            }

            frames.Add(bestFrame);
        }

        private static void PositionCameraForCurrentVFXState(CustomPreviewStage stage, float duration)
        {
            if (stage == null || stage.InstantiatedPrefab == null || stage.Camera == null) return;

            Physics.SyncTransforms();

            Renderer[] renderers = stage.InstantiatedPrefab.GetComponentsInChildren<Renderer>();
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || !renderer.enabled) continue;
                Bounds rendererBounds = renderer.bounds;
                if (float.IsInfinity(rendererBounds.size.magnitude) || float.IsNaN(rendererBounds.size.magnitude)) continue;
            }

            PositionCameraForPrefab(stage.InstantiatedPrefab, stage.Camera, PrefabType.VFX, Mathf.Max(duration, 0.1f));

            if (stage.Light != null)
            {
                PreviewLightingSetup.PositionLight(stage.Light, stage.Camera, stage.InstantiatedPrefab, stage.PrefabType);
            }

            if (AI.Config.cpUseSecondaryLight && stage.SecondaryLight != null && stage.SecondaryLight.enabled)
            {
                PreviewLightingSetup.PositionLight(stage.SecondaryLight, stage.Camera, stage.InstantiatedPrefab, stage.PrefabType, isSecondaryLight: true);
            }
        }

        private static void PositionCameraForCurrentFBXFrame(CustomPreviewStage stage)
        {
            if (stage == null || stage.InstantiatedPrefab == null || stage.Camera == null) return;

            SkinnedMeshRenderer[] skinnedMeshes = stage.InstantiatedPrefab.GetComponentsInChildren<SkinnedMeshRenderer>();
            foreach (SkinnedMeshRenderer smr in skinnedMeshes)
            {
                if (smr != null && smr.enabled)
                {
                    smr.forceMatrixRecalculationPerRender = true;
                    _ = smr.bounds;
                }
            }

            PositionCameraForPrefab(stage.InstantiatedPrefab, stage.Camera, stage.PrefabType, 0f);
            PreviewLightingSetup.PositionLight(stage.Light, stage.Camera, stage.InstantiatedPrefab, stage.PrefabType);
            if (AI.Config.cpUseSecondaryLight && stage.SecondaryLight != null && stage.SecondaryLight.enabled)
            {
                PreviewLightingSetup.PositionLight(stage.SecondaryLight, stage.Camera, stage.InstantiatedPrefab, stage.PrefabType, isSecondaryLight: true);
            }
        }

        /// <summary>
        /// Assemble frames into final texture with proper downscaling
        /// </summary>
        private static Texture2D AssembleResultTexture(List<Texture2D> frames, int targetSize, int frameCount)
        {
            if (frames == null || frames.Count == 0)
            {
                return null;
            }

            Texture2D resultTexture;

            if (frames.Count == 1)
            {
                resultTexture = frames[0];
            }
            else
            {
                resultTexture = ImageUtils.AssembleTextureSheet(frames);
                // Cleanup individual frames
                foreach (Texture2D frame in frames)
                {
                    Object.DestroyImmediate(frame);
                }
            }

            // Downscale if needed (native size is larger than target size due to super-sampling)
            if (resultTexture.width > targetSize || resultTexture.height > targetSize)
            {
                // Calculate target dimensions based on whether this is a multi-frame atlas
                int targetWidth = targetSize;
                int targetHeight = targetSize;

                if (frameCount > 1)
                {
                    // For multi-frame atlases, maintain the grid layout
                    int columns = Mathf.CeilToInt(Mathf.Sqrt(frameCount));
                    int rows = Mathf.CeilToInt((float)frameCount / columns);
                    targetWidth = targetSize * columns;
                    targetHeight = targetSize * rows;
                }

                Texture2D downscaled = resultTexture.Downscale(targetWidth, targetHeight);
                Object.DestroyImmediate(resultTexture);
                resultTexture = downscaled;
            }

            return resultTexture;
        }

        /// <summary>
        /// Check if a prefab contains VFX Graph components (pre-detection before scene creation)
        /// </summary>
        private static bool PrefabContainsVFX(GameObject prefab)
        {
            if (prefab == null) return false;

            // Check for VisualEffect components in the prefab hierarchy
            VisualEffect[] vfxComponents = prefab.GetComponentsInChildren<VisualEffect>(true);
            return vfxComponents != null && vfxComponents.Length > 0;
        }

        /// <summary>
        /// Cleanup stage and scene (handles both preview and regular scenes)
        /// </summary>
        private static void CleanupPreviewStage(CustomPreviewStage stage, Scene scene, bool isPreviewScene)
        {
            if (stage != null)
            {
                stage.RestoreEnvironment();
                Object.DestroyImmediate(stage);
            }
            else if (scene.IsValid())
            {
                if (isPreviewScene)
                {
                    EditorSceneManager.ClosePreviewScene(scene);
                }
                else
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        private static IEnumerator ProcessPrefab(GameObject prefab, string prefabPath, int size, int frameCount, TaskCompletionSource<Texture2D> tcs, int animationCount = 0, PreviewRequest previewRequest = null)
        {
            CustomPreviewStage stage;
            Scene previewScene;
            Texture2D resultTexture = null;
            bool hasError = false;
            bool skipFrameCapture = false;
            string errorMessage = "";

            if (previewRequest != null) previewRequest.FailureReason = null;

            // Ensure prefabPath is set - use fallback if still null/empty
            if (string.IsNullOrEmpty(prefabPath))
            {
                prefabPath = prefab != null ? (prefab.name ?? "Unknown") : "Unknown";
            }

            // Pre-detect if prefab contains VFX - VFX requires a regular scene, not a preview scene
            // Preview scenes have visibility limitations that cause VFX to be culled
            bool isVFXPrefab = PrefabContainsVFX(prefab);
            bool isPreviewScene = !isVFXPrefab;

            // Check if we can create preview stage (not in play mode, compiling, etc.)
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            {
                Debug.LogWarning("Cannot create preview stage during play mode or compilation.");
                tcs.SetResult(null);
                yield break;
            }

            // Unity only allows one untitled additive scene at a time.
            // Wait for any other VFX preview to finish before creating ours.
            if (isVFXPrefab)
            {
                float waitStart = (float)EditorApplication.timeSinceStartup;
                while (_vfxSceneInUse)
                {
                    if ((float)EditorApplication.timeSinceStartup - waitStart > VFX_SCENE_WAIT_TIMEOUT)
                    {
                        Debug.LogWarning($"[CustomPrefabPreviewGenerator] Timed out waiting for VFX scene slot for '{prefabPath}'. Skipping.");
                        tcs.SetResult(null);
                        yield break;
                    }
                    yield return null;
                }
                _vfxSceneInUse = true;
            }

            // Create scene - use regular additive scene for VFX, preview scene for everything else
            try
            {
                if (isVFXPrefab)
                {
                    // VFX requires a regular scene to bypass visibility limitations
                    previewScene = EditorSceneManager.NewScene(
                        NewSceneSetup.EmptyScene,
                        NewSceneMode.Additive
                    );
                }
                else
                {
                    // Use preview scene for non-VFX prefabs (following RapidIcon's pattern)
                    previewScene = EditorSceneManager.NewPreviewScene();
                    if (EditorSceneManager.GetSceneCullingMask(previewScene) == 0)
                    {
                        throw new OutOfMemoryException("Too many preview scenes were not closed correctly. Restart Unity to fix.");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to create scene: {e.Message}");
                if (isVFXPrefab) _vfxSceneInUse = false;
                tcs.SetResult(null);
                yield break;
            }

            // Create preview stage
            try
            {
                stage = ScriptableObject.CreateInstance<CustomPreviewStage>();
                if (stage == null)
                {
                    throw new Exception("Unable to create CustomPreviewStage");
                }

                // Set the scene on the stage before setup (critical step from RapidIcon)
                stage.SetScene(previewScene, isPreviewScene);
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to create preview stage: {e.Message}");
                if (previewScene.IsValid())
                {
                    if (isPreviewScene)
                    {
                        EditorSceneManager.ClosePreviewScene(previewScene);
                    }
                    else
                    {
                        EditorSceneManager.CloseScene(previewScene, true);
                    }
                }
                if (isVFXPrefab) _vfxSceneInUse = false;
                tcs.SetResult(null);
                yield break;
            }

            // Wrap the entire processing body in try-finally to guarantee cleanup and tcs.SetResult
            // even if an unhandled exception crashes the coroutine (e.g. TMP NullReferenceException
            // propagating through Canvas.SendWillRenderCanvases during a yield return null).
            // C# allows yield return inside try-finally (but not try-catch).
            bool cleanupDone = false;
            try
            {

                yield return null;

                // Setup stage with prefab (without animation duration first)
                try
                {
                    stage.SetupScene(prefab);
                    if (stage.InstantiatedPrefab != null && !string.IsNullOrEmpty(prefabPath) && prefabPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                    {
                        int fallbackAssignments = FBXMaterialFallbackResolver.ApplyExternalMaterialFallbacks(stage.InstantiatedPrefab, prefabPath);
                        if (fallbackAssignments > 0 && AI.Config.useCustomPipelineConverter)
                        {
                            PrefabPreviewUtilities.ConvertMaterialsToCurrentPipeline(stage.InstantiatedPrefab);
                        }
                    }
                }
                catch (Exception e)
                {
                    hasError = true;
                    errorMessage = e.Message;
                    resultTexture = null;
                }

                yield return null;

                // For UI prefabs, give additional time for Canvas layout updates
                // Canvas layouts need time to propagate, especially with ScrollRects and nested layouts
                if (!hasError && stage.PrefabType == PrefabType.UI)
                {
                    // Force Canvas layout updates to ensure RectTransforms have correct world positions
                    try { Canvas.ForceUpdateCanvases(); }
                    catch (Exception ex)
                    {
                        if (AI.Config.LogPreviewCreation) Debug.LogWarning($"[CustomPrefabPreviewGenerator] Canvas.ForceUpdateCanvases failed for '{prefabPath}': {ex.Message}");
                    }

                    // Give the Canvas layout system several frames to settle
                    for (int i = 0; i < 5; i++)
                    {
                        EditorApplication.QueuePlayerLoopUpdate();
                        yield return null;
                    }

                    // Force one more update after yields to ensure everything is current
                    try { Canvas.ForceUpdateCanvases(); }
                    catch (Exception ex)
                    {
                        if (AI.Config.LogPreviewCreation) Debug.LogWarning($"[CustomPrefabPreviewGenerator] Canvas.ForceUpdateCanvases failed for '{prefabPath}': {ex.Message}");
                    }
                }

                // For prefabs with TextMeshPro components, give additional time for mesh generation
                // TextMeshPro (especially 3D) needs time to generate geometry after activation
#if USE_TEXTMESHPRO || UNITY_2023_1_OR_NEWER
                if (!hasError && stage.InstantiatedPrefab != null)
                {
                    // Disable TMP components with missing font or fontSharedMaterial to prevent
                    // TMP_MaterialManager.GetFallbackMaterial NullReferenceException during Canvas updates.
                    // This NRE propagates through Canvas.SendWillRenderCanvases and can crash the coroutine.
                    TMP_Text[] allTmpComponents = stage.InstantiatedPrefab.GetComponentsInChildren<TMP_Text>(true);
                    if (allTmpComponents != null)
                    {
                        foreach (TMP_Text tmp in allTmpComponents)
                        {
                            if (tmp != null && tmp.enabled && (tmp.font == null || tmp.fontSharedMaterial == null))
                            {
                                if (AI.Config.LogPreviewCreation) Debug.LogWarning($"[CustomPrefabPreviewGenerator] Disabling TMP component with missing font/material on '{tmp.gameObject.name}' in '{prefabPath}'");
                                tmp.enabled = false;
                            }
                        }
                    }

                    TMP_Text[] tmpComponents = stage.InstantiatedPrefab.GetComponentsInChildren<TMP_Text>(true)
                        .Where(t => t.enabled && t.font != null).ToArray();
                    if (tmpComponents != null && tmpComponents.Length > 0)
                    {
                        // Give TextMeshPro a few frames to complete mesh generation
                        for (int i = 0; i < 5; i++)
                        {
                            EditorApplication.QueuePlayerLoopUpdate();
                            yield return null;
                        }

                        // For UI prefabs with TextMeshPro, force layout rebuilds for ContentSizeFitter
                        // This allows ContentSizeFitter and LayoutGroups to recalculate based on final text bounds
                        if (stage.PrefabType == PrefabType.UI)
                        {
                            // Find all RectTransforms with ContentSizeFitter or LayoutGroup components
                            ContentSizeFitter[] fitters = stage.InstantiatedPrefab.GetComponentsInChildren<ContentSizeFitter>(true);
                            LayoutGroup[] layoutGroups = stage.InstantiatedPrefab.GetComponentsInChildren<LayoutGroup>(true);

                            // Force rebuild layouts from bottom-up (children first, then parents)
                            // Do this multiple times as nested layouts may need multiple passes
                            for (int pass = 0; pass < 3; pass++)
                            {
                                // Rebuild ContentSizeFitters
                                foreach (ContentSizeFitter fitter in fitters)
                                {
                                    if (fitter != null && fitter.gameObject.activeInHierarchy)
                                    {
                                        try
                                        {
                                            LayoutRebuilder.ForceRebuildLayoutImmediate(fitter.GetComponent<RectTransform>());
                                        }
                                        catch (NullReferenceException)
                                        {
                                            // Can occur if child components (e.g. TMP with missing fonts) throw during layout calculation
                                        }
                                    }
                                }

                                // Rebuild LayoutGroups
                                foreach (LayoutGroup layoutGroup in layoutGroups)
                                {
                                    if (layoutGroup != null && layoutGroup.gameObject.activeInHierarchy)
                                    {
                                        try
                                        {
                                            LayoutRebuilder.ForceRebuildLayoutImmediate(layoutGroup.GetComponent<RectTransform>());
                                        }
                                        catch (NullReferenceException)
                                        {
                                            // Can occur if child components (e.g. TMP with missing fonts) throw during layout calculation
                                        }
                                    }
                                }

                                // Force Canvas update after each pass
                                try { Canvas.ForceUpdateCanvases(); }
                                catch (Exception ex) { Debug.LogWarning($"[CustomPrefabPreviewGenerator] Canvas.ForceUpdateCanvases failed for '{prefabPath}': {ex.Message}"); }
                                EditorApplication.QueuePlayerLoopUpdate();
                                yield return null;
                            }

                            // Final update to ensure everything is settled
                            try { Canvas.ForceUpdateCanvases(); }
                            catch (Exception ex) { Debug.LogWarning($"[CustomPrefabPreviewGenerator] Canvas.ForceUpdateCanvases failed for '{prefabPath}': {ex.Message}"); }
                        }
                    }
                }
#endif

                // For all UI prefabs, convert canvas to WorldSpace and position camera after layout is complete
                if (!hasError && stage.PrefabType == PrefabType.UI && stage.InstantiatedPrefab != null)
                {
                    // After layout is complete, convert ScreenSpaceCamera canvas to WorldSpace
                    // This preserves the ContentSizeFitter layout while using world units for camera positioning
                    PrefabPreviewUtilities.ConfigureCanvasForPreview(stage.InstantiatedPrefab, stage.Camera);

                    // Now position the camera based on the final layout
                    // (This was skipped in SetupScene for UI prefabs to wait for layout completion)
                    PositionCameraForPrefab(stage.InstantiatedPrefab, stage.Camera, stage.PrefabType, 0f);
                }

                // For FBX files, handle animations and skeleton visualization
                if (!hasError && stage.InstantiatedPrefab != null && !string.IsNullOrEmpty(prefabPath))
                {
                    if (prefabPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                    {
                        // Mark as FBX type (DetectPrefabType returns Model for FBX files)
                        stage.PrefabType = PrefabType.FBX;

                        // Check if has renderable geometry
                        Renderer[] renderers = stage.InstantiatedPrefab.GetComponentsInChildren<Renderer>();
                        bool hasGeometry = renderers != null && renderers.Length > 0;

                        // Track if we need bone visualization (for animation-only FBX files)
                        stage.NeedsBoneVisualization = !hasGeometry;

                        // Extract FBX data for storage if PreviewRequest was provided and data not already extracted
                        if (previewRequest != null && string.IsNullOrEmpty(previewRequest.FileData))
                        {
                            // Use the utility method to extract FBX data from already-instantiated prefab
                            // This avoids re-instantiation and reuses the prefab instance created for preview generation
                            string fbxDataJson = FBXDataExtractor.ExtractFBXDataAsJson(prefabPath, stage.InstantiatedPrefab);
                            if (!string.IsNullOrEmpty(fbxDataJson))
                            {
                                previewRequest.FileData = fbxDataJson;
                            }
                        }

                        // Load animation clips from FBX
                        Object[] allAssets = AssetDatabase.LoadAllAssetsAtPath(prefabPath);
                        // Filter out Unity's internal preview clips and empty clips
                        AnimationClip[] clips = allAssets.OfType<AnimationClip>()
                            .Where(c => !c.name.StartsWith("__preview__") && !c.empty)
                            .ToArray();

                        if (clips.Length > 0)
                        {
                            // Select the best animation clip (skip T-pose, prefer base animations)
                            AnimationClip clip = SelectBestAnimationClip(clips);

                            // Store clip for later use in frame capture
                            stage.FBXAnimationClip = clip;

                            // Sample animation first to show bones in animated pose, not T-pose
                            if (stage.NeedsBoneVisualization && clip != null)
                            {
                                float sampleTime = clip.length * 0.5f; // Sample at midpoint
                                SampleAnimationPose(stage.InstantiatedPrefab, clip, sampleTime);
                                Physics.SyncTransforms();
                            }

                            // For humanoid animations, we need an Animator component with an Avatar
                            Animator animator = stage.InstantiatedPrefab.GetComponentInChildren<Animator>();

                            // Track whether we have a valid Avatar for humanoid animations
                            bool hasValidAvatar = false;

                            if (clip.isHumanMotion)
                            {
                                Avatar avatar = null;

                                // First, check if the instantiated prefab already has an Animator with Avatar
                                if (animator != null && animator.avatar != null)
                                {
                                    avatar = animator.avatar;
                                }

                                // If no Avatar found, try loading from the FBX import settings
                                if (avatar == null)
                                {
                                    ModelImporter importer = UnityEditor.AssetImporter.GetAtPath(prefabPath) as ModelImporter;
                                    if (importer != null)
                                    {
                                        if (importer.sourceAvatar != null)
                                        {
                                            avatar = importer.sourceAvatar;
                                        }
                                        else
                                        {
                                            // Load all assets to find an embedded Avatar
                                            Object[] fbxAssets = AssetDatabase.LoadAllAssetsAtPath(prefabPath);
                                            avatar = fbxAssets.OfType<Avatar>().FirstOrDefault();

                                            if (avatar == null)
                                            {
                                                // For humanoid animations without an Avatar, try to generate one
                                                // by temporarily changing the import settings.
                                                // Only attempt on temp/preview files to avoid modifying
                                                // import settings (and .meta) of user project assets.
                                                bool isSafeToModifyImporter = prefabPath.Contains(AI.TEMP_FOLDER)
                                                    || prefabPath.Contains(UnityPreviewGenerator.PREVIEW_FOLDER);
                                                if (isSafeToModifyImporter
                                                    && (importer.avatarSetup == ModelImporterAvatarSetup.CopyFromOther
                                                        || importer.avatarSetup == ModelImporterAvatarSetup.NoAvatar))
                                                {
                                                    // Cache original settings to restore after extraction
                                                    ModelImporterAvatarSetup originalAvatarSetup = importer.avatarSetup;
                                                    Avatar originalSourceAvatar = importer.sourceAvatar;

                                                    // Change to CreateFromThisModel to generate an Avatar
                                                    importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                                                    AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceUpdate);

                                                    // Reload assets to get the newly generated Avatar
                                                    fbxAssets = AssetDatabase.LoadAllAssetsAtPath(prefabPath);
                                                    avatar = fbxAssets.OfType<Avatar>().FirstOrDefault();

                                                    // Restore original import settings to avoid permanent mutation
                                                    importer.avatarSetup = originalAvatarSetup;
                                                    if (originalAvatarSetup == ModelImporterAvatarSetup.CopyFromOther)
                                                    {
                                                        importer.sourceAvatar = originalSourceAvatar;
                                                    }
                                                    importer.SaveAndReimport();

                                                    if (avatar == null)
                                                    {
                                                        Debug.LogWarning("[FBX] Failed to generate Avatar for humanoid animation");
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }

                                // If we found an Avatar, ensure it's properly set up
                                if (avatar != null && avatar.isValid && avatar.isHuman)
                                {
                                    // Ensure we're working with the root GameObject for the Animator
                                    if (animator == null || animator.gameObject != stage.InstantiatedPrefab)
                                    {
                                        animator = stage.InstantiatedPrefab.GetComponent<Animator>();
                                        if (animator == null)
                                        {
                                            animator = stage.InstantiatedPrefab.AddComponent<Animator>();
                                        }
                                    }

                                    animator.avatar = avatar;
                                    animator.enabled = true;
                                    hasValidAvatar = true;

                                    // Create a temporary RuntimeAnimatorController for humanoid animations
                                    // Required for SampleAnimation() to properly resolve humanoid muscle curves
                                    // to bone transforms via the Avatar's Mecanim playback pipeline
                                    if (animator.runtimeAnimatorController == null)
                                    {
                                        UnityEditor.Animations.AnimatorController tempController = new UnityEditor.Animations.AnimatorController();
                                        tempController.name = $"TempHumanoidController_{stage.InstantiatedPrefab.GetStableId()}";
                                        tempController.AddLayer("Base Layer");

                                        UnityEditor.Animations.AnimatorState state = tempController.layers[0].stateMachine.AddState(clip.name);
                                        state.motion = clip;
                                        tempController.layers[0].stateMachine.defaultState = state;

                                        animator.runtimeAnimatorController = tempController;
                                    }
                                }
                                else
                                {
                                    // No valid Avatar found - we'll need to fall back to generic animation
                                    hasValidAvatar = false;
                                }
                            }

                            // Store Avatar status in the stage for later use during animation playback
                            stage.InstantiatedPrefab.name = hasValidAvatar ? stage.InstantiatedPrefab.name : stage.InstantiatedPrefab.name + "_NoAvatar";

                            // Handle Generic rig animations (non-humanoid)
                            // For Generic animations, AnimationMode works just as well as for Humanoid
                            // We just need to ensure the Animator component is properly configured
                            if (!clip.isHumanMotion)
                            {
                                // Get the ModelImporter to check animation type
                                ModelImporter importer = UnityEditor.AssetImporter.GetAtPath(prefabPath) as ModelImporter;

                                if (importer != null)
                                {
                                    if (importer.animationType == ModelImporterAnimationType.Generic)
                                    {
                                        // Ensure Animator component exists on root GameObject
                                        if (animator == null || animator.gameObject != stage.InstantiatedPrefab)
                                        {
                                            animator = stage.InstantiatedPrefab.GetComponent<Animator>();
                                            if (animator == null)
                                            {
                                                animator = stage.InstantiatedPrefab.AddComponent<Animator>();
                                            }
                                        }

                                        // Enable the Animator
                                        if (animator != null)
                                        {
                                            animator.enabled = true;

                                            // For Generic rigs with "Create From This Model" avatar definition,
                                            // assign the Avatar from the FBX
                                            if (animator.avatar == null && importer.avatarSetup == ModelImporterAvatarSetup.CreateFromThisModel)
                                            {
                                                // Try to load the Generic avatar
                                                Object[] fbxAssets = AssetDatabase.LoadAllAssetsAtPath(prefabPath);
                                                Avatar genericAvatar = fbxAssets.OfType<Avatar>().FirstOrDefault();

                                                if (genericAvatar != null && genericAvatar.isValid)
                                                {
                                                    animator.avatar = genericAvatar;
                                                    hasValidAvatar = true; // Mark that we have a valid Avatar now
                                                }
                                            }

                                            // Create a temporary RuntimeAnimatorController
                                            // Use unique name per instance to avoid conflicts during parallel preview generation
                                            if (animator.runtimeAnimatorController == null)
                                            {
                                                UnityEditor.Animations.AnimatorController tempController = new UnityEditor.Animations.AnimatorController();
                                                tempController.name = $"TempGenericController_{stage.InstantiatedPrefab.GetStableId()}";
                                                tempController.AddLayer("Base Layer");

                                                // Add the animation clip to the controller
                                                UnityEditor.Animations.AnimatorState state = tempController.layers[0].stateMachine.AddState(clip.name);
                                                state.motion = clip;

                                                // Set as default state
                                                tempController.layers[0].stateMachine.defaultState = state;

                                                // Assign to animator
                                                animator.runtimeAnimatorController = tempController;
                                            }

                                            // Update the _NoAvatar suffix now that we have proper Generic setup
                                            string baseName = stage.InstantiatedPrefab.name.Replace("_NoAvatar", "");
                                            stage.InstantiatedPrefab.name = hasValidAvatar ? baseName : baseName + "_NoAvatar";
                                        }
                                    }
                                }
                            }

                            // Load the source model for humanoid FBX files that reference an external avatar
                            // This covers two cases:
                            //   1. Animation-only FBX (no geometry) - always needs the source model
                            //   2. CopyFromOther FBX with geometry - the bundled meshes are reference geometry
                            //      (e.g. unskinned props, debug meshes) that won't animate correctly;
                            //      the source model has properly-rigged SkinnedMeshRenderers
                            if (clip.isHumanMotion)
                            {
                                ModelImporter importer = UnityEditor.AssetImporter.GetAtPath(prefabPath) as ModelImporter;
                                bool shouldLoadSourceModel = !hasGeometry ||
                                    (importer != null && importer.avatarSetup == ModelImporterAvatarSetup.CopyFromOther && importer.sourceAvatar != null);
                                if (shouldLoadSourceModel && importer != null && importer.sourceAvatar != null)
                                {
                                    // Get the source model path from the Avatar
                                    string sourceModelPath = AssetDatabase.GetAssetPath(importer.sourceAvatar);

                                    if (!string.IsNullOrEmpty(sourceModelPath) && sourceModelPath != prefabPath)
                                    {
                                        // Load the source model
                                        GameObject sourceModel = AssetDatabase.LoadAssetAtPath<GameObject>(sourceModelPath);
                                        if (sourceModel != null)
                                        {
                                            // Instantiate and replace the empty animation-only prefab with the source model
                                            GameObject instantiatedSourceModel = Object.Instantiate(sourceModel);
                                            instantiatedSourceModel.name = sourceModel.name; // Remove (Clone) suffix
                                            stage.ReplaceInstantiatedPrefab(instantiatedSourceModel);

                                            // Convert materials to current render pipeline (fixes pink shaders in URP/HDRP)
                                            PrefabPreviewUtilities.ConvertMaterialsToCurrentPipeline(instantiatedSourceModel);

                                            // Only disable bone visualization if source model actually has renderable geometry
                                            Renderer[] sourceRenderers = stage.InstantiatedPrefab.GetComponentsInChildren<Renderer>();
                                            bool sourceHasGeometry = sourceRenderers != null && sourceRenderers.Length > 0;
                                            stage.NeedsBoneVisualization = !sourceHasGeometry;

                                            // Ensure the source model has the Avatar set up
                                            Animator sourceAnimator = stage.InstantiatedPrefab.GetComponentInChildren<Animator>();
                                            if (sourceAnimator == null)
                                            {
                                                sourceAnimator = stage.InstantiatedPrefab.GetComponent<Animator>();
                                                if (sourceAnimator == null)
                                                {
                                                    sourceAnimator = stage.InstantiatedPrefab.AddComponent<Animator>();
                                                }
                                            }

                                            if (sourceAnimator != null && importer.sourceAvatar != null)
                                            {
                                                sourceAnimator.avatar = importer.sourceAvatar;
                                                sourceAnimator.enabled = true;

                                                // Update the flag - we now have a valid Avatar from the source model
                                                if (importer.sourceAvatar.isValid && importer.sourceAvatar.isHuman)
                                                {
                                                    hasValidAvatar = true;
                                                }

                                                // Create a temporary RuntimeAnimatorController for the source model
                                                // Required for SampleAnimation() to resolve humanoid muscle curves
                                                if (sourceAnimator.runtimeAnimatorController == null)
                                                {
                                                    UnityEditor.Animations.AnimatorController tempController = new UnityEditor.Animations.AnimatorController();
                                                    tempController.name = $"TempHumanoidController_{stage.InstantiatedPrefab.GetStableId()}";
                                                    tempController.AddLayer("Base Layer");

                                                    UnityEditor.Animations.AnimatorState state = tempController.layers[0].stateMachine.AddState(clip.name);
                                                    state.motion = clip;
                                                    tempController.layers[0].stateMachine.defaultState = state;

                                                    sourceAnimator.runtimeAnimatorController = tempController;
                                                }
                                            }

                                            // Update the name based on whether we have a valid Avatar
                                            // Remove any existing "_NoAvatar" suffix first
                                            string baseName = stage.InstantiatedPrefab.name.Replace("_NoAvatar", "");
                                            stage.InstantiatedPrefab.name = hasValidAvatar ? baseName : baseName + "_NoAvatar";
                                        }
                                        else
                                        {
                                            Debug.LogWarning($"[FBX] Failed to load source model from: {sourceModelPath}");
                                        }
                                    }
                                }
                            }

                            // Give a few frames for the prefab to fully initialize
                            for (int i = 0; i < 5; i++)
                            {
                                EditorApplication.QueuePlayerLoopUpdate();
                                yield return null;
                            }

                            // CRITICAL: Configure ALL Animator components to prevent auto-update interference
                            // Keep them enabled but frozen so SkinnedMeshRenderers can process bone deformations
                            // Required for bulk processing where multiple coroutines run in parallel
                            Animator[] animatorsToControl = stage.InstantiatedPrefab.GetComponentsInChildren<Animator>();
                            foreach (Animator anim in animatorsToControl)
                            {
                                if (anim != null && anim.enabled)
                                {
                                    // Freeze animator time progression but keep it active for bone processing
                                    anim.speed = 0f; // Prevent automatic time progression
                                    anim.cullingMode = AnimatorCullingMode.AlwaysAnimate; // Always update bones even when off-screen
                                }
                            }

                            // Give Unity a frame to process the Animator configuration  
                            EditorApplication.QueuePlayerLoopUpdate();
                            yield return null;

                            // Handle visualization for animation-only FBX files without geometry
                            // User can choose between bone visualization or Unity's default models
                            if (stage.NeedsBoneVisualization)
                            {
                                bool useDefaultModel = AI.Config.fbxAnimationPreviewMode == FBXAnimationPreviewMode.UnityHumanoid;
                                bool defaultModelUsed = false;

                                if (useDefaultModel)
                                {
                                    // Load the appropriate Unity default model based on animation type
                                    GameObject modelPrefab = clip.isHumanMotion
                                        ? GetUnityHumanoidModel()
                                        : GetUnityGenericModel();

                                    if (modelPrefab != null)
                                    {
                                        // Instantiate the model
                                        GameObject modelInstance = Object.Instantiate(modelPrefab);
                                        modelInstance.name = clip.isHumanMotion ? "UnityHumanoidPreview" : "UnityGenericPreview";

                                        // Convert materials to current render pipeline (fixes purple shaders in URP/HDRP)
                                        PrefabPreviewUtilities.ConvertMaterialsToCurrentPipeline(modelInstance);

                                        // Get the Animator from the model
                                        Animator modelAnimator = modelInstance.GetComponent<Animator>();
                                        if (modelAnimator == null)
                                        {
                                            modelAnimator = modelInstance.AddComponent<Animator>();
                                        }

                                        // Check if the model is valid for the animation type
                                        bool isValidModel = false;
                                        if (clip.isHumanMotion)
                                        {
                                            // For humanoid, check if avatar is valid and human
                                            Avatar modelAvatar = modelAnimator.avatar;
                                            isValidModel = modelAvatar != null && modelAvatar.isValid && modelAvatar.isHuman;
                                        }
                                        else
                                        {
                                            // For generic, the model just needs to exist with renderers
                                            Renderer[] modelRenderers = modelInstance.GetComponentsInChildren<Renderer>();
                                            isValidModel = modelRenderers != null && modelRenderers.Length > 0;
                                        }

                                        if (isValidModel)
                                        {
                                            // Replace the instantiated prefab with the model
                                            stage.ReplaceInstantiatedPrefab(modelInstance);

                                            // Set up the animator
                                            modelAnimator = stage.InstantiatedPrefab.GetComponent<Animator>();
                                            if (modelAnimator != null)
                                            {
                                                modelAnimator.enabled = true;
                                                modelAnimator.speed = 0f;
                                                modelAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                                            }

                                            // Sample the animation on the model
                                            float sampleTime = clip.length * 0.5f;
                                            SampleAnimationPose(stage.InstantiatedPrefab, clip, sampleTime);
                                            Physics.SyncTransforms();

                                            // No longer need bone visualization since we have a mesh
                                            stage.NeedsBoneVisualization = false;
                                            defaultModelUsed = true;
                                        }
                                        else
                                        {
                                            // Model not valid for this animation type, destroy the instance
                                            Object.DestroyImmediate(modelInstance);
                                        }
                                    }
                                }

                                // Fall back to bone visualization if default model wasn't used
                                if (!defaultModelUsed)
                                {
                                    VisualizeSkeletonBones(stage.InstantiatedPrefab);
                                }
                            }
                        }
                    }
                }

                // Recalculate camera position to frame entire animation motion (e.g., character jumping)
                // NOTE: Animators are now disabled at this point, preventing interference with sampling
                if (!hasError && stage.FBXAnimationClip != null && stage.InstantiatedPrefab != null)
                {
                    // For humanoid clips with bone visualization, zero out the FBX import rotation
                    // before computing bounds. Mecanim's pipeline already converts bone transforms
                    // to Y-up internally; keeping the root's -90° X rotation would double-apply it.
                    if (stage.NeedsBoneVisualization && stage.FBXAnimationClip.isHumanMotion)
                    {
                        ClearBoneVisualization(stage.InstantiatedPrefab);
                        stage.InstantiatedPrefab.transform.localRotation = Quaternion.identity;
                    }

                    Bounds animBounds = PreviewBoundsCalculator.GetFBXAnimationBoundsOverTime(stage.InstantiatedPrefab, stage.FBXAnimationClip);

                    // Use projected bounds calculation for proper framing
                    // FBX animation bounds may be inflated, so use tighter framing
                    float distance = PreviewCameraSetup.CalculateCameraDistance(animBounds, stage.Camera, 0f, AI.Config.cpCameraAngleX, AI.Config.cpCameraAngleY, 0.88f);

                    // Position camera above the animation bounds center
                    stage.Camera.transform.position = new Vector3(animBounds.center.x, animBounds.center.y + distance, animBounds.center.z);
                    stage.Camera.transform.LookAt(animBounds.center);

                    // Apply custom camera angles (same as standard Model positioning)
                    stage.Camera.transform.RotateAround(animBounds.center, Vector3.left, AI.Config.cpCameraAngleX);
                    stage.Camera.transform.RotateAround(animBounds.center, Vector3.up, AI.Config.cpCameraAngleY);

                    // Compensate for perspective shift when viewing at an angle
                    float verticalAngleRad = AI.Config.cpCameraAngleX * Mathf.Deg2Rad;
                    float perspectiveCompensation = animBounds.extents.y * Mathf.Sin(verticalAngleRad) * 0.15f;
                    Vector3 lookTarget = animBounds.center + Vector3.up * perspectiveCompensation;
                    stage.Camera.transform.LookAt(lookTarget);

                    // Reposition lights relative to new camera position
                    PreviewLightingSetup.PositionLight(stage.Light, stage.Camera, stage.InstantiatedPrefab, stage.PrefabType);
                    if (AI.Config.cpUseSecondaryLight && stage.SecondaryLight != null && stage.SecondaryLight.enabled)
                    {
                        PreviewLightingSetup.PositionLight(stage.SecondaryLight, stage.Camera, stage.InstantiatedPrefab, stage.PrefabType, isSecondaryLight: true);
                    }
                }

                // For particles and VFX, recalculate camera position with time-based bounds
                // This must happen AFTER SetupScene so the prefab is instantiated and active
                // Use time-based bounds for ALL particle/VFX previews (static and animated) to ensure consistent framing
                if (!hasError && (stage.PrefabType == PrefabType.Particles || stage.PrefabType == PrefabType.VFX))
                {
                    float animationDuration;
                    if (stage.PrefabType == PrefabType.VFX)
                    {
                        animationDuration = GetVFXDuration(stage.InstantiatedPrefab);

                        // Give VFX a moment to initialize and spawn initial particles
                        HandleVFXSystems(stage.InstantiatedPrefab);

                        // Brief wait for VFX initialization (reduced from 10 to 3 frames)
                        for (int i = 0; i < 3; i++)
                        {
                            EditorApplication.QueuePlayerLoopUpdate();
                            yield return null;
                        }
                    }
                    else // Particles
                    {
                        animationDuration = GetVisuallyActiveDuration(stage.InstantiatedPrefab);
                    }

                    // Reposition camera with time-based bounds for particle motion (applies to both static and animated)
                    if (animationDuration > 0f)
                    {
                        PositionCameraForPrefab(stage.InstantiatedPrefab, stage.Camera, stage.PrefabType, animationDuration);
                    }

                }

                yield return null;

                // Detection pass: Check if prefab has visible content
                if (!hasError)
                {
                    skipFrameCapture = ShouldSkipFrameCapture(stage, out string skipReason);

                    // If no visible content detected, try rotating 180° and check again
                    // Handles one-sided objects like windows that are only visible from one direction
                    if (skipFrameCapture && skipReason == NO_VISIBLE_CONTENT_REASON)
                    {
                        // Rotate the object 180° around Y axis
                        stage.InstantiatedPrefab.transform.Rotate(0f, 180f, 0f);

                        // Force bounds recalculation after rotation
                        Physics.SyncTransforms();

                        // Try detection again
                        yield return null;
                        skipFrameCapture = ShouldSkipFrameCapture(stage, out string retrySkipReason);

                        if (skipFrameCapture)
                        {
                            // Use the retry reason if it failed again, otherwise keep original reason
                            skipReason = retrySkipReason ?? skipReason;
                        }
                    }

                    if (skipFrameCapture && !string.IsNullOrEmpty(skipReason) && AI.Config.LogPreviewCreation)
                    {
                        Debug.LogWarning($"[CustomPrefabPreviewGenerator] Skipping frame capture for '{prefabPath}': {skipReason}");
                    }
                    if (skipFrameCapture && previewRequest != null)
                    {
                        previewRequest.FailureReason = skipReason;
                    }
                    yield return null;
                }

                // Only proceed with frame capture if we haven't already determined there's no content
                if (!hasError && !skipFrameCapture)
                {
                    // PRE-PASS: Calculate optimal camera framing for consistent static/animated previews
                    if (stage.PrefabType == PrefabType.Particles)
                    {
                        float prePassOptimalTime = CalculateOptimalParticleTime(stage.InstantiatedPrefab);

                        ParticleSystem[] prePassSystems = stage.InstantiatedPrefab.GetComponentsInChildren<ParticleSystem>();
                        List<ParticleSystem> prePassRoots = FindRootParticleSystems(prePassSystems, stage.InstantiatedPrefab);

                        if (prePassRoots.Count > 0 && prePassOptimalTime > 0f)
                        {
                            // Clear and simulate to optimal time using same combined loop as frame capture
                            foreach (ParticleSystem ps in prePassSystems)
                            {
                                ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                                ps.Clear(false);
                                ps.useAutoRandomSeed = false;
                                ps.randomSeed = AI.Config.cpParticleSeed;
                            }

                            // Use first root's simulation speed for time scaling (consistent with previous behavior)
                            ParticleSystem.MainModule rootMain = prePassRoots[0].main;
                            float realOptimalTime = rootMain.simulationSpeed > 0f ? prePassOptimalTime / rootMain.simulationSpeed : prePassOptimalTime;
                            SimulateAllRoots(prePassRoots, realOptimalTime, true, false);

                            // Get bounds at optimal time
                            Bounds optimalBounds = PreviewBoundsCalculator.GetCurrentParticleBounds(stage.InstantiatedPrefab);

                            // Position camera based on these optimal bounds using shared helper
                            PositionParticleCamera(optimalBounds, stage.Camera, stage.InstantiatedPrefab.transform.position);

                            // Clean up: stop and clear all particle systems
                            foreach (ParticleSystem ps in prePassSystems)
                            {
                                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                                ps.Clear(true);
                            }
                        }
                    }

                    // Capture frames
                    List<Texture2D> frames = new List<Texture2D>();
                    int nativeSize = size * AI.Config.cpSuperSamplingMultiplier;

                    // Handle multi-frame animated previews for particles and VFX
                    if ((stage.PrefabType == PrefabType.Particles || stage.PrefabType == PrefabType.VFX) && frameCount > 1)
                    {
                        // Calculate duration based on effect type
                        // Use visually-active duration for particles to avoid wasting frames on the dying tail
                        bool isBurstDominated = false;
                        float duration;
                        if (stage.PrefabType == PrefabType.VFX)
                        {
                            duration = GetVFXDuration(stage.InstantiatedPrefab);
                        }
                        else
                        {
                            duration = GetVisuallyActiveDuration(stage.InstantiatedPrefab, out isBurstDominated);
                        }

                        // Calculate start offset to avoid capturing at t=0 when burst particles haven't spawned
                        // For ultra-short effects, use smaller proportional offset
                        float startOffset = Mathf.Min(duration * 0.05f, 0.03f);

                        // Detect if particle system has trails - they need incremental simulation
                        bool hasTrails = stage.PrefabType == PrefabType.Particles && HasTrails(stage.InstantiatedPrefab);

                        // For VFX, check compilation status before the animation loop
                        // VFX was already initialized by HandleVFXSystems - do NOT call Reinit() again
                        if (stage.PrefabType == PrefabType.VFX)
                        {
                            VisualEffect[] vfxComponents = stage.InstantiatedPrefab.GetComponentsInChildren<VisualEffect>();

                            // Check if VFX shaders are already compiled from previous preview
                            bool vfxAlreadyCompiled = IsVFXAlreadyCompiled(vfxComponents);

                            // Use much shorter wait if already compiled (just need particles to spawn)
                            int waitFrames = 0;
                            int maxWaitFrames = vfxAlreadyCompiled ? 10 : 60; // 10 frames if cached, 60 if not
                            int graceFrames = vfxAlreadyCompiled ? 5 : 30; // Shorter grace period if cached
                            bool compilationProgressed = vfxAlreadyCompiled; // Assume compiled if cached
                            bool anyAlive = false;

                            // First check if particles are already alive (from earlier initialization)
                            foreach (VisualEffect vfx in vfxComponents)
                            {
                                int particleCount = vfx.aliveParticleCount;
                                if (particleCount > 0)
                                {
                                    anyAlive = true;
                                    break;
                                }
                            }

                            // Only wait for compilation if no particles are alive yet
                            if (!anyAlive)
                            {
                                while (waitFrames < maxWaitFrames)
                                {
                                    anyAlive = false;

                                    foreach (VisualEffect vfx in vfxComponents)
                                    {
                                        int particleCount = vfx.aliveParticleCount;

                                        // Track if compilation progressed beyond -4 (not loaded)
                                        if (particleCount > -4) compilationProgressed = true;

                                        if (particleCount > 0)
                                        {
                                            anyAlive = true;
                                            break;
                                        }
                                    }

                                    // Exit if particles spawned OR if compilation progressed and we've waited grace period
                                    if (anyAlive || (compilationProgressed && waitFrames >= graceFrames))
                                    {
                                        break;
                                    }

                                    EditorApplication.QueuePlayerLoopUpdate();
                                    yield return null;
                                    waitFrames++;
                                }
                            }

                            // Mark VFX as compiled for future cache hits
                            if (anyAlive || compilationProgressed)
                            {
                                MarkVFXAsCompiled(vfxComponents);
                            }

                            // Simulate forward to spawn more particles for better visibility
                            float warmupTime = Mathf.Min(duration * 0.3f, 1.5f);
                            foreach (VisualEffect vfx in vfxComponents)
                            {
                                float timeStep = 0.016f;
                                uint warmupSteps = (uint)Mathf.Max(1, warmupTime / timeStep);
                                vfx.Simulate(timeStep, warmupSteps);
                            }

                            // Wait for particles to spawn from warmup (reduced from 10 to 3 frames)
                            for (int w = 0; w < 3; w++)
                            {
                                EditorApplication.QueuePlayerLoopUpdate();
                                yield return null;
                            }

                            PositionCameraForCurrentVFXState(stage, duration);
                        }

                        // PRE-PASS: Calculate optimal camera framing BEFORE recording for consistent framing
                        bool hasPrePassSubEmitters = stage.PrefabType == PrefabType.Particles && HasSubEmitters(stage.InstantiatedPrefab);

                        if (hasPrePassSubEmitters)
                        {
                            ParticleSystem[] prePassSystems = stage.InstantiatedPrefab.GetComponentsInChildren<ParticleSystem>();
                            ParticleSystem prePassRoot = prePassSystems.Length > 0 ? prePassSystems[0] : null;

                            if (prePassRoot != null)
                            {
                                float prePassOptimalTime = CalculateOptimalParticleTime(stage.InstantiatedPrefab);
                                if (prePassOptimalTime <= 0f) prePassOptimalTime = 0.5f;

                                // Build set of sub-emitter target systems
                                HashSet<ParticleSystem> prePassSubTargets = new HashSet<ParticleSystem>();
                                foreach (ParticleSystem ps in prePassSystems)
                                {
                                    ParticleSystem.SubEmittersModule subMod = ps.subEmitters;
                                    if (subMod.enabled)
                                    {
                                        for (int s = 0; s < subMod.subEmittersCount; s++)
                                        {
                                            ParticleSystem target = subMod.GetSubEmitterSystem(s);
                                            if (target != null) prePassSubTargets.Add(target);
                                        }
                                    }
                                }

                                // Stop and clear all systems individually, then set seeds
                                foreach (ParticleSystem ps in prePassSystems)
                                {
                                    ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                                    ps.Clear(false);
                                }
                                foreach (ParticleSystem ps in prePassSystems)
                                {
                                    ps.useAutoRandomSeed = false;
                                    ps.randomSeed = AI.Config.cpParticleSeed;
                                }

                                // Single root.Simulate(withChildren=true) to fire all regular children
                                ParticleSystem.MainModule rootMain = prePassRoot.main;
                                float realOptimalTime = rootMain.simulationSpeed > 0f ? prePassOptimalTime / rootMain.simulationSpeed : prePassOptimalTime;
                                prePassRoot.Simulate(realOptimalTime, true, true, true);
                                prePassRoot.Play(true);

                                // Handle sub-emitter targets that Simulate() couldn't trigger
                                if (prePassSubTargets.Count > 0)
                                {
                                    Vector3 emitPos = prePassRoot.transform.position;
                                    if (prePassRoot.particleCount > 0)
                                    {
                                        ParticleSystem.Particle[] pp = new ParticleSystem.Particle[1];
                                        prePassRoot.GetParticles(pp, 1);
                                        Vector3 pPos = pp[0].position;
                                        if (prePassRoot.main.simulationSpace == ParticleSystemSimulationSpace.Local)
                                            emitPos = prePassRoot.transform.TransformPoint(pPos);
                                        else
                                            emitPos = pPos;
                                    }

                                    System.Collections.Generic.List<System.Tuple<ParticleSystem, Vector3>> prePassMovedTransforms =
                                        new System.Collections.Generic.List<System.Tuple<ParticleSystem, Vector3>>();

                                    foreach (ParticleSystem subPS in prePassSubTargets)
                                    {
                                        if (subPS.particleCount > 0) continue;

                                        ParticleSystem.EmissionModule subEmission = subPS.emission;
                                        int emitCount = subEmission.burstCount > 0
                                            ? (int)subEmission.GetBurst(0).count.constant
                                            : 50;

                                        Vector3 origPos = subPS.transform.position;
                                        prePassMovedTransforms.Add(new System.Tuple<ParticleSystem, Vector3>(subPS, origPos));
                                        subPS.transform.position = emitPos;
                                        subPS.Emit(emitCount);

                                        float advanceTime = Mathf.Min(realOptimalTime, subPS.main.startLifetime.constant * 0.3f);
                                        if (advanceTime > 0f)
                                        {
                                            subPS.Simulate(advanceTime, false, false, false);
                                        }
                                    }

                                    // Calculate bounds with all particles present
                                    Bounds prePassBounds = PreviewBoundsCalculator.GetCurrentParticleBounds(stage.InstantiatedPrefab);
                                    PositionParticleCamera(prePassBounds, stage.Camera, stage.InstantiatedPrefab.transform.position);

                                    // Restore sub-emitter transforms
                                    foreach (System.Tuple<ParticleSystem, Vector3> mt in prePassMovedTransforms)
                                    {
                                        if (mt.Item1 != null)
                                        {
                                            mt.Item1.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                                            mt.Item1.Clear(true);
                                            mt.Item1.transform.position = mt.Item2;
                                        }
                                    }
                                }
                                else
                                {
                                    // No sub-emitter targets needed special handling
                                    Bounds prePassBounds = PreviewBoundsCalculator.GetCurrentParticleBounds(stage.InstantiatedPrefab);
                                    PositionParticleCamera(prePassBounds, stage.Camera, stage.InstantiatedPrefab.transform.position);
                                }

                                // Clean up
                                prePassRoot.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                                prePassRoot.Clear(true);
                            }
                        }

                        // Store the pre-pass camera state before the frame loop.
                        // Per-frame adaptive zoom will clamp adjustments relative to this baseline.
                        Vector3 prePassCamPosition = stage.Camera.transform.position;
                        Quaternion prePassCamRotation = stage.Camera.transform.rotation;

                        // Temporal smoothing state for adaptive camera
                        float smoothedDistance = Vector3.Distance(prePassCamPosition, stage.InstantiatedPrefab.transform.position);
                        Vector3 smoothedCenter = prePassCamPosition + prePassCamRotation * Vector3.forward * smoothedDistance;

                        for (int i = 0; i < frameCount; i++)
                        {
                            // Track transforms moved for explosion positioning (restored after frame capture)
                            System.Collections.Generic.List<System.Tuple<Transform, Vector3>> movedTransformsForFrame =
                                new System.Collections.Generic.List<System.Tuple<Transform, Vector3>>();

                            // Proper frame distribution: first frame at startOffset, last frame at full duration
                            float time;
                            if (frameCount == 1)
                            {
                                time = duration * 0.5f; // Single frame: capture at midpoint
                            }
                            else
                            {
                                float t = i / (float)(frameCount - 1);

                                // For burst-dominated effects (all particles fire at t=0), use front-loaded
                                // distribution so more frames capture the initial burst and expansion phase
                                // where the visual action is, rather than the slow fade-out tail.
                                // Power 1.5: first half of frames covers ~35% of timeline, second half covers ~65%.
                                if (isBurstDominated)
                                {
                                    t = Mathf.Pow(t, 1.5f);
                                }

                                time = startOffset + (duration - startOffset) * t;
                            }

                            // For particles, reset and simulate to the specific time point
                            if (stage.PrefabType == PrefabType.Particles)
                            {
                                ParticleSystem[] particleSystems = stage.InstantiatedPrefab.GetComponentsInChildren<ParticleSystem>();
                                bool hasAnimSubEmitters = HasSubEmitters(stage.InstantiatedPrefab);
                                List<ParticleSystem> rootPSList = FindRootParticleSystems(particleSystems, stage.InstantiatedPrefab);

                                // Build set of sub-emitter target systems (if any)
                                HashSet<ParticleSystem> subEmitterTargets = new HashSet<ParticleSystem>();
                                if (hasAnimSubEmitters && rootPSList.Count > 0)
                                {
                                    foreach (ParticleSystem ps in particleSystems)
                                    {
                                        ParticleSystem.SubEmittersModule subMod = ps.subEmitters;
                                        if (subMod.enabled)
                                        {
                                            for (int s = 0; s < subMod.subEmittersCount; s++)
                                            {
                                                ParticleSystem target = subMod.GetSubEmitterSystem(s);
                                                if (target != null) subEmitterTargets.Add(target);
                                            }
                                        }
                                    }
                                }

                                if (rootPSList.Count > 0)
                                {
                                    // Step 1: Stop and clear ALL systems individually
                                    foreach (ParticleSystem ps in particleSystems)
                                    {
                                        ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                                        ps.Clear(false);
                                    }

                                    // Step 2: Set seeds on ALL systems
                                    foreach (ParticleSystem ps in particleSystems)
                                    {
                                        ps.useAutoRandomSeed = false;
                                        ps.randomSeed = AI.Config.cpParticleSeed;
                                    }

                                    // Step 3: Simulate all root-level particle systems with withChildren=true.
                                    // For typical prefabs where particleSystems[0] is the common ancestor, this
                                    // is identical to before. For branched hierarchies (sibling PS branches with
                                    // no common PS ancestor), each branch root is simulated independently.
                                    ParticleSystem.MainModule rootMain = rootPSList[0].main;
                                    float simSpeed = rootMain.simulationSpeed;
                                    float realTime = simSpeed > 0f ? time / simSpeed : time;

                                    SimulateAllRoots(rootPSList, realTime, true, hasTrails);

                                    // Step 4: Handle sub-emitter targets that Simulate() couldn't trigger.
                                    // Simulate() doesn't generate sub-emitter trigger events, so systems
                                    // that are ONLY triggered as sub-emitters (not independent emitters)
                                    // need manual Emit(). Skip systems that already have particles from
                                    // their own emission (e.g., they have their own bursts/rateOverTime).
                                    if (subEmitterTargets.Count > 0)
                                    {
                                        // Determine parent particle position for sub-emitter placement
                                        // Use first root PS that has particles for positioning
                                        ParticleSystem emitSourcePS = rootPSList[0];
                                        foreach (ParticleSystem rps in rootPSList)
                                        {
                                            if (rps.particleCount > 0)
                                            {
                                                emitSourcePS = rps;
                                                break;
                                            }
                                        }
                                        Vector3 emitPosition = emitSourcePS.transform.position;
                                        if (emitSourcePS.particleCount > 0)
                                        {
                                            ParticleSystem.Particle[] rootParticles = new ParticleSystem.Particle[1];
                                            emitSourcePS.GetParticles(rootParticles, 1);
                                            Vector3 pPos = rootParticles[0].position;
                                            if (emitSourcePS.main.simulationSpace == ParticleSystemSimulationSpace.Local)
                                                emitPosition = emitSourcePS.transform.TransformPoint(pPos);
                                            else
                                                emitPosition = pPos;
                                        }

                                        foreach (ParticleSystem subPS in subEmitterTargets)
                                        {
                                            // Only manually emit if the system has 0 particles
                                            // (meaning its own emission didn't produce anything)
                                            if (subPS.particleCount > 0) continue;

                                            ParticleSystem.EmissionModule subEmission = subPS.emission;
                                            int emitCount = subEmission.burstCount > 0
                                                ? (int)subEmission.GetBurst(0).count.constant
                                                : 50;

                                            // Move to parent particle position for proper spatial placement
                                            Vector3 originalPos = subPS.transform.position;
                                            movedTransformsForFrame.Add(new System.Tuple<Transform, Vector3>(subPS.transform, originalPos));
                                            subPS.transform.position = emitPosition;

                                            subPS.Emit(emitCount);

                                            // Advance sub-emitter particles to spread them out
                                            float advanceTime = Mathf.Min(realTime, subPS.main.startLifetime.constant * 0.5f);
                                            if (advanceTime > 0f)
                                            {
                                                subPS.Simulate(advanceTime, false, false, false);
                                            }

                                            ParticleSystemRenderer subRend = subPS.GetComponent<ParticleSystemRenderer>();
                                            if (subRend != null)
                                            {
                                                Bounds _ = subRend.bounds;
                                            }
                                        }
                                    }


                                }

                                // Queue a player loop update so particle renderers generate GPU mesh
                                // data from the CPU particle buffer. Without this + yield,
                                // Camera.Render() sees empty meshes and produces all-black frames.
                                EditorApplication.QueuePlayerLoopUpdate();

                                yield return null;

                                // Extra yield for trail systems to allow mesh updates
                                if (hasTrails)
                                {
                                    yield return null;
                                }

                                ParticleSystemRenderer[] particleRenderers = stage.InstantiatedPrefab.GetComponentsInChildren<ParticleSystemRenderer>();
                                foreach (ParticleSystemRenderer renderer in particleRenderers)
                                {
                                    Bounds _ = renderer.bounds;
                                }

                                // Per-frame adaptive zoom: gently adjust camera to track current particles
                                // Uses tight clamping + exponential smoothing to prevent erratic movement
                                {
                                    Bounds frameBounds = PreviewBoundsCalculator.GetCurrentParticleBounds(stage.InstantiatedPrefab);
                                    if (frameBounds.size.magnitude > 0.01f)
                                    {
                                        float adaptAngleX = 20f;
                                        float adaptAngleY = -50f;
                                        float idealDistance = PreviewCameraSetup.CalculateCameraDistance(frameBounds, stage.Camera, AI.Config.cpFramingPadding, adaptAngleX, adaptAngleY, 1f);

                                        CalculateEffectiveCenterAndScale(frameBounds, stage.InstantiatedPrefab.transform.position, out Vector3 frameCenter, out float frameDistScale);
                                        idealDistance *= frameDistScale;

                                        // Clamp the per-frame distance tightly to [0.9x, 1.1x] of the pre-pass distance
                                        // This keeps the camera mostly stable with only subtle zoom adaptation
                                        float prePassDist = Vector3.Distance(prePassCamPosition, stage.InstantiatedPrefab.transform.position);
                                        if (prePassDist < 0.01f) prePassDist = idealDistance;
                                        float clampedDistance = Mathf.Clamp(idealDistance, prePassDist * 0.9f, prePassDist * 1.1f);

                                        // Blend the look-at target: mostly anchored to the pre-pass center (75%)
                                        // with gentle tracking of per-frame particle center (25%)
                                        Vector3 prePassImpliedCenter = prePassCamPosition + prePassCamRotation * Vector3.forward * prePassDist;
                                        Vector3 blendedCenter = Vector3.Lerp(prePassImpliedCenter, frameCenter, 0.25f);

                                        // Exponential smoothing: ease toward target values across frames
                                        // Higher alpha = more responsive, lower = more stable
                                        float smoothAlpha = 0.3f;
                                        smoothedDistance = Mathf.Lerp(smoothedDistance, clampedDistance, smoothAlpha);
                                        smoothedCenter = Vector3.Lerp(smoothedCenter, blendedCenter, smoothAlpha);

                                        // NaN guard
                                        if (!float.IsNaN(smoothedDistance) && !float.IsInfinity(smoothedDistance) && smoothedDistance > 0f &&
                                            !float.IsNaN(smoothedCenter.x) && !float.IsNaN(smoothedCenter.y) && !float.IsNaN(smoothedCenter.z))
                                        {
                                            stage.Camera.transform.position = new Vector3(smoothedCenter.x, smoothedCenter.y, smoothedCenter.z - smoothedDistance);
                                            stage.Camera.transform.LookAt(smoothedCenter);
                                            stage.Camera.transform.RotateAround(smoothedCenter, Vector3.right, adaptAngleX);
                                            stage.Camera.transform.RotateAround(smoothedCenter, Vector3.up, adaptAngleY);
                                        }
                                    }
                                }
                            }
                            else if (stage.PrefabType == PrefabType.VFX)
                            {
                                // For VFX, reset and simulate to the specific time point
                                VisualEffect[] vfxComponents = stage.InstantiatedPrefab.GetComponentsInChildren<VisualEffect>();

                                foreach (VisualEffect vfx in vfxComponents)
                                {
                                    vfx.Stop();
                                    vfx.Play();

                                    float timeStep = 0.016f;
                                    uint stepCount = (uint)Mathf.Max(1, time / timeStep);
                                    vfx.Simulate(timeStep, stepCount);
                                }

                                // Wait for VFX to process simulation (reduced: 2 frames for first 3, 1 frame after)
                                int waitCount = (i < 3) ? 2 : 1;
                                for (int w = 0; w < waitCount; w++)
                                {
                                    EditorApplication.QueuePlayerLoopUpdate();
                                    yield return null;
                                }
                            }

                            // Use regular rendering for all prefab types
                            Texture2D frame = null;
                            try
                            {
                                frame = stage.RenderFrame(nativeSize, nativeSize);

                                if (frame != null)
                                {
                                    frames.Add(frame);
                                }
                                else
                                {
                                    Debug.LogWarning($"[CustomPrefabPreviewGenerator] Frame {i} is NULL!");
                                }
                            }
                            catch (System.Exception ex)
                            {
                                Debug.LogWarning($"[CustomPrefabPreviewGenerator] Frame {i} rendering failed: {ex.Message}");
                            }
                            finally
                            {
                                // CRITICAL: Restore transforms in finally block to ensure cleanup even if rendering fails
                                // This must happen AFTER frame capture for LOCAL simulation space particles
                                foreach (System.Tuple<Transform, Vector3> movedTransform in movedTransformsForFrame)
                                {
                                    if (movedTransform.Item1 != null)
                                    {
                                        movedTransform.Item1.position = movedTransform.Item2;
                                    }
                                }
                            }

                            EditorApplication.QueuePlayerLoopUpdate();
                            yield return null;
                        }
                    }
                    else if (!string.IsNullOrEmpty(prefabPath) && prefabPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) && frameCount > 1)
                    {
                        // FBX animation playback with optional 360° rotation
                        // Check if animation clip was stored during setup
                        bool handledFBX = false;

                        if (stage.FBXAnimationClip != null)
                        {
                            AnimationClip clip = stage.FBXAnimationClip;

                            float animDuration = clip.length;
                            bool use360 = AI.Config.generate360FBXPreviews;


                            // Use animation bounds to ensure rotation center accounts for full motion
                            // NOTE: Animators were already disabled earlier (after initialization), so sampling is safe
                            if (stage.NeedsBoneVisualization && clip.isHumanMotion)
                            {
                                stage.InstantiatedPrefab.transform.localRotation = Quaternion.identity;
                            }

                            Bounds bounds = PreviewBoundsCalculator.GetFBXAnimationBoundsOverTime(stage.InstantiatedPrefab, clip);

                            // Sample frame 0 initially to prevent T-pose from appearing
                            SampleAnimationPose(stage.InstantiatedPrefab, clip, 0f);
                            Physics.SyncTransforms();

                            // Force SkinnedMeshRenderer updates after initial sampling
                            SkinnedMeshRenderer[] initialSkinnedMeshes = stage.InstantiatedPrefab.GetComponentsInChildren<SkinnedMeshRenderer>();
                            foreach (SkinnedMeshRenderer smr in initialSkinnedMeshes)
                            {
                                if (smr != null && smr.enabled)
                                {
                                    _ = smr.bounds; // Force recalculation
                                }
                            }

                            for (int i = 0; i < frameCount; i++)
                            {
                                // Calculate time for this frame
                                float time = (animDuration * i) / frameCount;

                                // Optional 360° rotation (do this before yielding)
                                if (use360)
                                {
                                    float angleIncrement = 360f / frameCount;
                                    stage.Camera.transform.RotateAround(bounds.center, Vector3.up, -angleIncrement);

                                    if (AI.Config.cpRotateLightWith360)
                                    {
                                        stage.Light.transform.RotateAround(bounds.center, Vector3.up, -angleIncrement);
                                        if (AI.Config.cpUseSecondaryLight && stage.SecondaryLight != null && stage.SecondaryLight.enabled)
                                        {
                                            stage.SecondaryLight.transform.RotateAround(bounds.center, Vector3.up, -angleIncrement);
                                        }
                                    }
                                }

                                yield return null;

                                // Clear existing bone visualization if needed
                                if (stage.NeedsBoneVisualization)
                                {
                                    ClearBoneVisualization(stage.InstantiatedPrefab);
                                }

                                // Sample animation immediately before rendering
                                // For humanoid clips: uses Animator.Play + Update to evaluate muscle curves
                                // through the Mecanim pipeline (SampleAnimation cannot resolve muscle curves)
                                // For generic clips: falls back to direct SampleAnimation (parallel-safe)
                                SampleAnimationPose(stage.InstantiatedPrefab, clip, time);

                                // Force Unity to update transforms after sampling
                                Physics.SyncTransforms();

                                // CRITICAL: Force SkinnedMeshRenderers to update mesh deformation for this frame
                                // In bulk mode, bone updates may not propagate properly without explicit forcing
                                SkinnedMeshRenderer[] skinnedMeshes = stage.InstantiatedPrefab.GetComponentsInChildren<SkinnedMeshRenderer>();
                                foreach (SkinnedMeshRenderer smr in skinnedMeshes)
                                {
                                    if (smr != null && smr.enabled)
                                    {
                                        // Force immediate update of skinned mesh based on current bone positions
                                        smr.forceMatrixRecalculationPerRender = true;
                                        _ = smr.bounds; // Access bounds to trigger recalculation
                                    }
                                }

                                // Recreate bone visualization at the new animated pose
                                if (stage.NeedsBoneVisualization)
                                {
                                    if (clip.isHumanMotion)
                                    {
                                        stage.InstantiatedPrefab.transform.localRotation = Quaternion.identity;
                                    }

                                    VisualizeSkeletonBones(stage.InstantiatedPrefab);
                                }

                                // Render frame immediately after sampling (atomic)
                                Texture2D frame = stage.RenderFrame(nativeSize, nativeSize);
                                if (frame != null)
                                {
                                    frames.Add(frame);
                                }
                                else
                                {
                                    Debug.LogWarning($"[FBX] Frame {i} rendering failed!");
                                }

                                EditorApplication.QueuePlayerLoopUpdate();
                                yield return null;
                            }

                            handledFBX = true;
                        }

                        // If no animation component or no clip, check if we should do 360° or single frame
                        if (!handledFBX)
                        {
                            if (AI.Config.generate360FBXPreviews)
                            {
                                // 360° rotation without animation - continue to next else-if block
                            }
                            else
                            {
                                // No animation and no 360° - single frame
                                Texture2D frame = stage.RenderFrame(nativeSize, nativeSize);
                                if (frame != null)
                                {
                                    frames.Add(frame);
                                }
                                handledFBX = true;
                            }
                        }

                        // Skip the 360° block below if we already handled this FBX
                        if (handledFBX)
                        {
                            // Mark as handled by adding a flag that the next condition can check
                            // (we can't easily skip it, so we'll modify the condition instead)
                        }
                    }

                    if (!string.IsNullOrEmpty(prefabPath) && prefabPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) &&
                        frameCount > 1 && AI.Config.generate360FBXPreviews && frames.Count == 0)
                    {
                        // FBX without animation but with 360° rotation enabled
                        // Pass prefabType to exclude particle/VFX renderers from bounds - must match camera positioning
                        Bounds bounds = PreviewBoundsCalculator.GetGlobalBounds(stage.InstantiatedPrefab.GetComponentsInChildren<Renderer>(), stage.PrefabType, stage.InstantiatedPrefab);

                        for (int i = 0; i < frameCount; i++)
                        {
                            float angleIncrement = 360f / frameCount;
                            stage.Camera.transform.RotateAround(bounds.center, Vector3.up, -angleIncrement);

                            if (AI.Config.cpRotateLightWith360)
                            {
                                stage.Light.transform.RotateAround(bounds.center, Vector3.up, -angleIncrement);
                                if (AI.Config.cpUseSecondaryLight && stage.SecondaryLight != null && stage.SecondaryLight.enabled)
                                {
                                    stage.SecondaryLight.transform.RotateAround(bounds.center, Vector3.up, -angleIncrement);
                                }
                            }

                            Texture2D frame = stage.RenderFrame(nativeSize, nativeSize);
                            if (frame != null)
                            {
                                frames.Add(frame);
                            }

                            EditorApplication.QueuePlayerLoopUpdate();
                            yield return null;
                        }
                    }
                    else if (AI.Config.generateAnimatedModelPreviews && stage.PrefabType == PrefabType.Model && frameCount > 1 &&
                             (string.IsNullOrEmpty(prefabPath) || !prefabPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)))
                    {
                        // 360° rotation capture for regular models (non-FBX)
                        // Pass prefabType to exclude particle/VFX renderers from bounds - must match camera positioning
                        Bounds bounds = PreviewBoundsCalculator.GetGlobalBounds(stage.InstantiatedPrefab.GetComponentsInChildren<Renderer>(), stage.PrefabType, stage.InstantiatedPrefab);

                        for (int i = 0; i < frameCount; i++)
                        {
                            float angleIncrement = 360f / frameCount;

                            // Rotate camera around object
                            stage.Camera.transform.RotateAround(bounds.center, Vector3.up, -angleIncrement);

                            // Optionally rotate lights with camera to maintain consistent lighting
                            if (AI.Config.cpRotateLightWith360)
                            {
                                stage.Light.transform.RotateAround(bounds.center, Vector3.up, -angleIncrement);

                                // Also rotate secondary light if enabled
                                if (AI.Config.cpUseSecondaryLight && stage.SecondaryLight != null && stage.SecondaryLight.enabled)
                                {
                                    stage.SecondaryLight.transform.RotateAround(bounds.center, Vector3.up, -angleIncrement);
                                }
                            }

                            Texture2D frame = stage.RenderFrame(nativeSize, nativeSize);
                            if (frame != null)
                            {
                                frames.Add(frame);
                            }

                            EditorApplication.QueuePlayerLoopUpdate();
                            yield return null;
                        }
                    }
                    // Model+Particles hybrid: animate particles while keeping camera fixed on 3D model
                    // This handles prefabs that are primarily 3D models but contain particle effects
                    else if (frames.Count == 0 && stage.PrefabType == PrefabType.Model && frameCount > 1 &&
                             (AI.Config.generateAnimatedParticlePreviews || AI.Config.generateAnimatedVFXPreviews))
                    {
                        ParticleSystem[] hybridParticles = stage.InstantiatedPrefab.GetComponentsInChildren<ParticleSystem>(true);
                        VisualEffect[] hybridVFX = stage.InstantiatedPrefab.GetComponentsInChildren<VisualEffect>(true);

                        // Only proceed if there are effects matching enabled config options
                        bool hasEnabledParticles = hybridParticles.Length > 0 && AI.Config.generateAnimatedParticlePreviews;
                        bool hasEnabledVFX = hybridVFX.Length > 0 && AI.Config.generateAnimatedVFXPreviews;

                        if (hasEnabledParticles || hasEnabledVFX)
                        {
                            // Calculate duration from particle systems
                            float duration = hybridParticles.Length > 0
                                ? GetVisuallyActiveDuration(stage.InstantiatedPrefab)
                                : GetVFXDuration(stage.InstantiatedPrefab);
                            if (duration <= 0f) duration = 2f;

                            bool hasTrails = HasTrails(stage.InstantiatedPrefab);

                            // Capture frames at different particle simulation times
                            for (int i = 0; i < frameCount; i++)
                            {
                                float time = duration * (i + 1) / (frameCount + 1);

                                // Reset and simulate particles to this time
                                foreach (ParticleSystem ps in hybridParticles)
                                {
                                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                                    ps.Clear(true);
                                    ps.randomSeed = 12345;

                                    if (hasTrails)
                                    {
                                        // Incremental simulation for trails
                                        ps.Play(true);
                                        float stepSize = 0.033f;
                                        int steps = Mathf.CeilToInt(time / stepSize);
                                        for (int s = 0; s < steps; s++)
                                        {
                                            ps.Simulate(stepSize, true, false, false);
                                        }
                                    }
                                    else
                                    {
                                        ps.Simulate(time, true, true, false);
                                    }
                                }

                                // Simulate VFX if present
                                foreach (VisualEffect vfx in hybridVFX)
                                {
                                    vfx.Stop();
                                    vfx.Play();
                                    float timeStep = 0.016f;
                                    uint stepCount = (uint)Mathf.Max(1, time / timeStep);
                                    vfx.Simulate(timeStep, stepCount);
                                }

                                EditorApplication.QueuePlayerLoopUpdate();
                                yield return null;

                                // Extra yield for trails
                                if (hasTrails)
                                {
                                    yield return null;
                                }

                                Texture2D frame = stage.RenderFrame(nativeSize, nativeSize);
                                if (frame != null)
                                {
                                    frames.Add(frame);
                                }

                                EditorApplication.QueuePlayerLoopUpdate();
                                yield return null;
                            }
                        }
                    }
                    else if (frames.Count == 0)
                    {
                        // Single frame capture (only if no frames have been captured yet)

                        // For FBX animations, store the sampling info to be applied just before capture
                        // Samples pose atomically with capture to prevent parallel coroutines from interfering with global AnimationMode state
                        if (stage.FBXAnimationClip != null && stage.InstantiatedPrefab != null)
                        {
                            // Store the animation sampling parameters for later
                            stage.FBXStaticPreviewSampleTime = stage.FBXAnimationClip.length * 0.5f;
                        }
                        // For particle systems, use multi-frame sampling to find the best capture time
                        else if (stage.PrefabType == PrefabType.Particles)
                        {
                            // Calculate base duration for sampling distribution
                            // Use visually-active duration to concentrate samples in the active emission period
                            float duration = GetVisuallyActiveDuration(stage.InstantiatedPrefab);
                            float optimalTime = CalculateOptimalParticleTime(stage.InstantiatedPrefab);

                            // Check if this effect has sub-emitters
                            bool hasSubEmitters = HasSubEmitters(stage.InstantiatedPrefab);

                            // Detect if particle system has trails - they need incremental simulation
                            bool hasTrails = HasTrails(stage.InstantiatedPrefab);

                            ParticleSystem[] particleSystems = stage.InstantiatedPrefab.GetComponentsInChildren<ParticleSystem>();

                            // Multi-frame sampling: capture at multiple time points to find best frame
                            // Sample times distributed around optimal time and across effect duration
                            float[] samplePercentages = {0.10f, 0.25f, 0.40f, 0.55f, 0.70f, 0.85f};
                            List<float> sampleTimes = new List<float>();
                            foreach (float pct in samplePercentages)
                            {
                                sampleTimes.Add(duration * pct);
                            }
                            // Also include the calculated optimal time as a candidate
                            if (!sampleTimes.Contains(optimalTime))
                            {
                                sampleTimes.Add(optimalTime);
                                sampleTimes.Sort();
                            }

                            List<Texture2D> candidateFrames = new List<Texture2D>();

                            // Capture a frame at each sample time
                            foreach (float sampleTime in sampleTimes)
                            {
                                // Simulate particles to this sample time
                                yield return SimulateParticlesToTime(stage, particleSystems, sampleTime, hasSubEmitters, hasTrails);

                                // Force bounds update after simulation
                                yield return null;
                                if (hasTrails)
                                {
                                    yield return null;
                                }

                                ParticleSystemRenderer[] particleRenderers = stage.InstantiatedPrefab.GetComponentsInChildren<ParticleSystemRenderer>();
                                foreach (ParticleSystemRenderer renderer in particleRenderers)
                                {
                                    Bounds _ = renderer.bounds;
                                }

                                // Capture frame at this sample time
                                Texture2D candidateFrame = stage.RenderFrame(nativeSize, nativeSize);
                                if (candidateFrame != null)
                                {
                                    candidateFrames.Add(candidateFrame);
                                }
                            }

                            // Analyze all captured frames and select the best one
                            // Use background-aware scoring: count non-background pixels rather than
                            // luminance variance, which fails for sparse particles on uniform backgrounds
                            if (candidateFrames.Count > 0)
                            {
                                float bestScore = -1f;
                                int bestFrameIndex = 0;
                                Color bgColor = stage.Camera.backgroundColor;

                                for (int i = 0; i < candidateFrames.Count; i++)
                                {
                                    candidateFrames[i].HasVisibleContent(bgColor, out float visiblePct);
                                    if (visiblePct > bestScore)
                                    {
                                        bestScore = visiblePct;
                                        bestFrameIndex = i;
                                    }
                                }

                                // Keep only the best frame, destroy others
                                Texture2D bestFrame = candidateFrames[bestFrameIndex];
                                for (int i = 0; i < candidateFrames.Count; i++)
                                {
                                    if (i != bestFrameIndex)
                                    {
                                        Object.DestroyImmediate(candidateFrames[i]);
                                    }
                                }

                                frames.Add(bestFrame);
                            }

                            // Skip the standard single-frame capture below since we already captured
                            // (we set frames to contain the best frame)
                        }
                        // Legacy single-time particle simulation for sub-emitter systems (kept as fallback)
                        else if (stage.PrefabType == PrefabType.Particles && false) // Disabled - using multi-sample above
                        {
                            // Calculate optimal capture time for best particle visibility
                            float optimalTime = CalculateOptimalParticleTime(stage.InstantiatedPrefab);

                            // Check if this effect has sub-emitters
                            bool hasSubEmitters = HasSubEmitters(stage.InstantiatedPrefab);

                            // Detect if particle system has trails - they need incremental simulation
                            bool hasTrails = HasTrails(stage.InstantiatedPrefab);

                            ParticleSystem[] particleSystems = stage.InstantiatedPrefab.GetComponentsInChildren<ParticleSystem>();

                            // For sub-emitter systems (fireworks, explosions), use intelligent reconfiguration
                            if (hasSubEmitters)
                            {
                                // Store original sub-emitter configurations for restoration
                                // Tuple: parentPS, subPS, originalType, index, originalDelay, originalRateOverTime, originalBursts, nested(parentSubEmitterModuleEnabled, originalDuration)
                                // Note: C# Tuple supports max 7 items directly, 8th+ items must be nested
                                System.Collections.Generic.List<System.Tuple<ParticleSystem, ParticleSystem, ParticleSystemSubEmitterType, int, float, float, ParticleSystem.Burst[], System.Tuple<bool, float>>> subEmitterBackups =
                                    new System.Collections.Generic.List<System.Tuple<ParticleSystem, ParticleSystem, ParticleSystemSubEmitterType, int, float, float, ParticleSystem.Burst[], System.Tuple<bool, float>>>();

                                // Reconfigure sub-emitters to be time-based instead of event-based
                                foreach (ParticleSystem ps in particleSystems)
                                {
                                    ParticleSystem.SubEmittersModule subEmitters = ps.subEmitters;
                                    if (subEmitters.enabled && subEmitters.subEmittersCount > 0)
                                    {
                                        // Calculate when parent particles die
                                        ParticleSystem.MainModule parentMain = ps.main;
                                        float parentLifetime = parentMain.startLifetime.constant;
                                        if (parentMain.startLifetime.mode == ParticleSystemCurveMode.TwoConstants)
                                        {
                                            parentLifetime = (parentMain.startLifetime.constantMin + parentMain.startLifetime.constantMax) / 2f;
                                        }

                                        float parentDelay = parentMain.startDelay.constant;
                                        if (parentMain.startDelay.mode == ParticleSystemCurveMode.TwoConstants)
                                        {
                                            parentDelay = (parentMain.startDelay.constantMin + parentMain.startDelay.constantMax) / 2f;
                                        }

                                        float parentDeathTime = parentDelay + parentLifetime;

                                        // Reconfigure each sub-emitter
                                        for (int i = 0; i < subEmitters.subEmittersCount; i++)
                                        {
                                            ParticleSystem subEmitterPS = subEmitters.GetSubEmitterSystem(i);
                                            ParticleSystemSubEmitterType originalType = subEmitters.GetSubEmitterType(i);

                                            if (subEmitterPS != null && originalType == ParticleSystemSubEmitterType.Death)
                                            {
                                                // Back up original configuration
                                                ParticleSystem.MainModule subMain = subEmitterPS.main;
                                                ParticleSystem.EmissionModule subEmission = subEmitterPS.emission;
                                                float originalDelay = subMain.startDelay.constant;
                                                float originalRateOverTime = subEmission.rateOverTime.constant;

                                                // Back up original bursts
                                                ParticleSystem.Burst[] originalBursts = new ParticleSystem.Burst[subEmission.burstCount];
                                                for (int b = 0; b < subEmission.burstCount; b++)
                                                {
                                                    originalBursts[b] = subEmission.GetBurst(b);
                                                }

                                                // Backup original duration
                                                float originalDuration = subMain.duration;

                                                subEmitterBackups.Add(new System.Tuple<ParticleSystem, ParticleSystem, ParticleSystemSubEmitterType, int, float, float, ParticleSystem.Burst[], System.Tuple<bool, float>>(
                                                    ps, subEmitterPS, originalType, i, originalDelay, originalRateOverTime, originalBursts, new System.Tuple<bool, float>(subEmitters.enabled, originalDuration)));

                                                // HYPOTHESIS R FIX: Sub-emitters fundamentally don't work with Simulate()
                                                // Disable the sub-emitter module entirely - make explosion particles independent
                                                // Don't change the sub-emitter type, just disable the whole module

                                                // CRITICAL: Stop the system before modifying duration
                                                subEmitterPS.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                                                subEmitterPS.Clear(true);

                                                subMain.startDelay = 0f; // Start immediately, not delayed

                                                // HYPOTHESIS BB FIX: Duration too short blocks emission
                                                // Set duration long enough to cover the entire simulation (must be done while stopped)
                                                subMain.duration = optimalTime + 1f; // Extra 1 second buffer

                                                // HYPOTHESIS Z: startDelay blocks emission - remove it and use direct simulation
                                                // Get the particle count from the original bursts
                                                int particleCount = originalBursts.Length > 0 ? (int)originalBursts[0].count.constant : 50;

                                                // Clear all bursts - we'll use rate-over-time instead
                                                subEmission.SetBursts(new ParticleSystem.Burst[0]);

                                                // Set continuous emission rate (particles per second)
                                                // We want 'particleCount' particles to spawn quickly
                                                // Use a high rate for a short duration
                                                float emissionRate = particleCount * 10f; // Spawn all particles in 0.1 seconds
                                                subEmission.rateOverTime = emissionRate;

                                                // CRITICAL FIX: Set startDelay to 0, NOT parentDeathTime
                                                // We'll handle timing by simulating to the right time
                                                subMain.startDelay = 0f;

                                            }
                                        }

                                        // CRITICAL: Disable the sub-emitter module entirely on this parent
                                        // This makes the explosion particles independent, not sub-emitters
                                        subEmitters.enabled = false;

                                    }
                                }

                                // Build a list of explosion systems to exclude from parent simulation
                                System.Collections.Generic.HashSet<ParticleSystem> explosionSystems = new System.Collections.Generic.HashSet<ParticleSystem>();
                                foreach (System.Tuple<ParticleSystem, ParticleSystem, ParticleSystemSubEmitterType, int, float, float, ParticleSystem.Burst[], System.Tuple<bool, float>> backup in subEmitterBackups)
                                {
                                    explosionSystems.Add(backup.Item2); // Add explosion system
                                }

                                // Simulate parent systems ONLY (withChildren=false to prevent auto-simulating explosion children)
                                foreach (ParticleSystem ps in particleSystems)
                                {
                                    // Skip explosion systems - we'll simulate them separately
                                    if (explosionSystems.Contains(ps)) continue;

                                    ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                                    ps.Clear(false);
                                    ps.useAutoRandomSeed = false;
                                    ps.randomSeed = AI.Config.cpParticleSeed;

                                    ParticleSystem.MainModule main = ps.main;
                                    float realTime = main.simulationSpeed > 0f ? optimalTime / main.simulationSpeed : optimalTime;
                                    // CRITICAL: withChildren=false to prevent auto-simulating explosion children
                                    ps.Simulate(realTime, false, true, true);
                                    ps.Play(false); // Don't play children
                                }

                                // Hypothesis R: Simulate explosion particles as independent systems (not sub-emitters)
                                foreach (System.Tuple<ParticleSystem, ParticleSystem, ParticleSystemSubEmitterType, int, float, float, ParticleSystem.Burst[], System.Tuple<bool, float>> backup in subEmitterBackups)
                                {
                                    ParticleSystem subPS = backup.Item2;
                                    ParticleSystem.MainModule subMain = subPS.main;
                                    ParticleSystem.EmissionModule subEmission = subPS.emission;

                                    // Ensure system is fully stopped before seeding
                                    subPS.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                                    subPS.Clear(false);
                                    subPS.useAutoRandomSeed = false;
                                    subPS.randomSeed = AI.Config.cpParticleSeed;

                                    float subRealTime = subMain.simulationSpeed > 0f ? optimalTime / subMain.simulationSpeed : optimalTime;

                                    // Hypothesis HH: Simulate() doesn't emit particles - use Emit() directly!
                                    // This bypasses the emission module and creates particles directly

                                    // Get the target particle count from original burst
                                    int emitCount = backup.Item5 > 0 ? (int)backup.Item5 : 100; // originalBurstCount or default

                                    // Clear and prepare system
                                    subPS.Clear(true);
                                    subPS.time = 0f;

                                    // DIRECTLY EMIT particles - bypasses emission module completely!
                                    subPS.Emit(emitCount);

                                    // Now simulate to advance particle positions (without restarting)
                                    // Use a shorter time to keep particles visible (half of startLifetime)
                                    float advanceTime = Mathf.Min(subRealTime, subMain.startLifetime.constant * 0.5f);
                                    subPS.Simulate(advanceTime, false, false, false);

                                }

                                yield return null;

                                // Check particle counts
                                System.Collections.Generic.Dictionary<string, int> reconfiguredCounts = new System.Collections.Generic.Dictionary<string, int>();
                                int reconfiguredTotal = 0;
                                foreach (ParticleSystem ps in particleSystems)
                                {
                                    reconfiguredCounts[ps.name] = ps.particleCount;
                                    reconfiguredTotal += ps.particleCount;
                                }

                                // CRITICAL: Recalculate bounds and reposition camera AFTER emitting particles!
                                // The original bounds were calculated before we emitted explosion particles,
                                // so the camera is pointing at the wrong location.
                                if (reconfiguredTotal > 1)
                                {
                                    // Get current particle bounds (now includes our emitted explosion particles)
                                    Bounds particleBounds = PreviewBoundsCalculator.GetCurrentParticleBounds(stage.InstantiatedPrefab);

                                    // Reposition camera using shared helper
                                    PositionParticleCamera(particleBounds, stage.Camera, stage.InstantiatedPrefab.transform.position);
                                }

                                // Restore original sub-emitter configurations
                                System.Collections.Generic.HashSet<ParticleSystem> restoredParents = new System.Collections.Generic.HashSet<ParticleSystem>();
                                foreach (System.Tuple<ParticleSystem, ParticleSystem, ParticleSystemSubEmitterType, int, float, float, ParticleSystem.Burst[], System.Tuple<bool, float>> backup in subEmitterBackups)
                                {
                                    ParticleSystem parentPS = backup.Item1;
                                    ParticleSystem subPS = backup.Item2;
                                    ParticleSystemSubEmitterType originalType = backup.Item3;
                                    int index = backup.Item4;
                                    float originalDelay = backup.Item5;
                                    float originalRateOverTime = backup.Item6;
                                    ParticleSystem.Burst[] originalBursts = backup.Item7;
                                    bool originalSubEmitterModuleEnabled = backup.Rest.Item1; // 8th item in nested tuple
                                    float originalDuration = backup.Rest.Item2; // 9th item in nested tuple

                                    // Restore sub-emitter type
                                    ParticleSystem.SubEmittersModule subEmitters = parentPS.subEmitters;
                                    subEmitters.SetSubEmitterType(index, originalType);

                                    // Restore sub-emitter module enabled state (only once per parent)
                                    if (!restoredParents.Contains(parentPS))
                                    {
                                        subEmitters.enabled = originalSubEmitterModuleEnabled;
                                        restoredParents.Add(parentPS);
                                    }

                                    // DON'T stop the system - we need to preserve particles for the preview!
                                    // Skip duration restoration since it requires clearing particles.
                                    // This is fine because we're working with a temporary instance, not the original prefab.

                                    // Restore emission module settings (these don't require stopping)
                                    ParticleSystem.MainModule subMain = subPS.main;
                                    subMain.startDelay = originalDelay;
                                    // NOTE: Duration NOT restored - requires StopEmittingAndClear which clears particles

                                    ParticleSystem.EmissionModule subEmission = subPS.emission;
                                    subEmission.rateOverTime = originalRateOverTime;
                                    subEmission.SetBursts(originalBursts);
                                }

                            }
                            else
                            {
                                // For non-sub-emitter systems, use standard simulation (faster)
                                // Use withChildren=false so each system gets its own correct realTime
                                foreach (ParticleSystem ps in particleSystems)
                                {
                                    // Stop and clear only this system
                                    ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                                    ps.Clear(false);

                                    // Disable autoRandomSeed to allow manual seed control
                                    ps.useAutoRandomSeed = false;
                                    // Set random seed while fully stopped
                                    ps.randomSeed = AI.Config.cpParticleSeed;

                                    // Adjust time for this system's simulation speed
                                    // Simulate() takes real-time, not particle-time
                                    ParticleSystem.MainModule main = ps.main;
                                    float realTime = main.simulationSpeed > 0f ? optimalTime / main.simulationSpeed : optimalTime;

                                    if (hasTrails)
                                    {
                                        // Incremental simulation for trails - they need frame-by-frame updates
                                        // to accumulate trail geometry as particles move
                                        float timeStep = 0.016f; // 60fps - small enough for smooth trails
                                        float currentTime = 0f;

                                        // Reset to t=0 with restart (this system only)
                                        ps.Simulate(0f, false, true, true);
                                        ps.Play(false);

                                        // Simulate incrementally to allow trails to build up geometry
                                        while (currentTime < realTime)
                                        {
                                            float deltaTime = Mathf.Min(timeStep, realTime - currentTime);
                                            ps.Simulate(deltaTime, false, false, false); // Incremental, no restart
                                            currentTime += deltaTime;
                                        }
                                    }
                                    else
                                    {
                                        // Standard single-step simulation for non-trail particles
                                        // withChildren=false: each system individually simulated with its own realTime
                                        ps.Simulate(realTime, false, true, true);

                                        // Ensure the system is in playing state for rendering
                                        ps.Play(false);
                                    }
                                }
                            }

                            // Pause all systems to prevent editor ticks from advancing the simulation during yield
                            foreach (ParticleSystem ps in particleSystems)
                            {
                                ps.Pause(false);
                            }

                            // Force bounds update after simulation and before rendering
                            yield return null;

                            // Extra yield for trail systems to allow mesh updates
                            if (hasTrails)
                            {
                                yield return null;
                            }

                            ParticleSystemRenderer[] particleRenderers = stage.InstantiatedPrefab.GetComponentsInChildren<ParticleSystemRenderer>();
                            foreach (ParticleSystemRenderer renderer in particleRenderers)
                            {
                                Bounds _ = renderer.bounds;
                            }

                        }
                        else if (stage.PrefabType == PrefabType.VFX)
                        {
                            // For single-frame VFX, use multi-frame sampling to find best capture time
                            VisualEffect[] vfxComponents = stage.InstantiatedPrefab.GetComponentsInChildren<VisualEffect>();

                            // Check if VFX shaders are already compiled from previous preview
                            bool vfxAlreadyCompiled = IsVFXAlreadyCompiled(vfxComponents);

                            // Use much shorter wait if already compiled (just need particles to spawn)
                            int waitFrames = 0;
                            int maxWaitFrames = vfxAlreadyCompiled ? 10 : 60; // 10 frames if cached, 60 if not
                            int graceFrames = vfxAlreadyCompiled ? 5 : 30; // Shorter grace period if cached
                            bool compilationProgressed = vfxAlreadyCompiled; // Assume compiled if cached
                            bool anyAlive = false;

                            // First check if particles are already alive (from earlier initialization)
                            foreach (VisualEffect vfx in vfxComponents)
                            {
                                int particleCount = vfx.aliveParticleCount;
                                if (particleCount > 0)
                                {
                                    anyAlive = true;
                                    break;
                                }
                            }

                            // Only wait for compilation if no particles are alive yet
                            if (!anyAlive)
                            {
                                while (waitFrames < maxWaitFrames)
                                {
                                    anyAlive = false;

                                    foreach (VisualEffect vfx in vfxComponents)
                                    {
                                        int particleCount = vfx.aliveParticleCount;

                                        // Track if compilation progressed beyond -4 (not loaded)
                                        if (particleCount > -4) compilationProgressed = true;

                                        if (particleCount > 0)
                                        {
                                            anyAlive = true;
                                            break;
                                        }
                                    }

                                    // Exit if particles spawned OR if compilation progressed and we've waited grace period
                                    if (anyAlive || (compilationProgressed && waitFrames >= graceFrames))
                                    {
                                        break;
                                    }

                                    EditorApplication.QueuePlayerLoopUpdate();
                                    yield return null;
                                    waitFrames++;
                                }
                            }

                            // Mark VFX as compiled for future cache hits
                            if (anyAlive || compilationProgressed)
                            {
                                MarkVFXAsCompiled(vfxComponents);
                            }

                            // Multi-frame sampling: capture at multiple time points to find best frame
                            float duration = GetVFXDuration(stage.InstantiatedPrefab);
                            float[] samplePercentages = {0.10f, 0.25f, 0.40f, 0.55f, 0.70f, 0.85f};
                            List<Texture2D> candidateFrames = new List<Texture2D>();

                            foreach (float pct in samplePercentages)
                            {
                                float targetTime = duration * pct;

                                // Reset and simulate VFX to target time
                                foreach (VisualEffect vfx in vfxComponents)
                                {
                                    vfx.Reinit();
                                    vfx.Play();

                                    float timeStep = 0.016f;
                                    uint steps = (uint)Mathf.Max(1, targetTime / timeStep);
                                    vfx.Simulate(timeStep, steps);
                                }

                                // Wait for particles to spawn and render
                                for (int w = 0; w < 3; w++)
                                {
                                    EditorApplication.QueuePlayerLoopUpdate();
                                    yield return null;
                                }

                                PositionCameraForCurrentVFXState(stage, duration);

                                // Capture frame at this sample time
                                Texture2D candidateFrame = stage.RenderFrame(nativeSize, nativeSize);
                                if (candidateFrame != null)
                                {
                                    candidateFrames.Add(candidateFrame);
                                }
                            }

                            // Analyze all captured frames and select the best one
                            if (candidateFrames.Count > 0)
                            {
                                float bestScore = -1f;
                                int bestFrameIndex = 0;

                                for (int i = 0; i < candidateFrames.Count; i++)
                                {
                                    float score = ImageUtils.AnalyzeFrameQuality(candidateFrames[i]);
                                    if (score > bestScore)
                                    {
                                        bestScore = score;
                                        bestFrameIndex = i;
                                    }
                                }

                                // Keep only the best frame, destroy others
                                Texture2D bestFrame = candidateFrames[bestFrameIndex];
                                for (int i = 0; i < candidateFrames.Count; i++)
                                {
                                    if (i != bestFrameIndex)
                                    {
                                        Object.DestroyImmediate(candidateFrames[i]);
                                    }
                                }

                                frames.Add(bestFrame);
                            }
                        }

                        // For FBX static previews, sample the animation immediately before capture
                        // Use direct sampling (parallel-safe - no global AnimationMode state)
                        if (stage.FBXStaticPreviewSampleTime >= 0f && stage.FBXAnimationClip != null && stage.InstantiatedPrefab != null)
                        {
                            float sampleTime = stage.FBXStaticPreviewSampleTime;

                            // Clear existing bone visualization if needed (it was created at initialization pose)
                            if (stage.NeedsBoneVisualization)
                            {
                                ClearBoneVisualization(stage.InstantiatedPrefab);
                            }

                            // Use Mecanim pipeline for humanoid clips, direct sampling for generic
                            SampleAnimationPose(stage.InstantiatedPrefab, stage.FBXAnimationClip, sampleTime);

                            // Force Unity to update transforms after sampling
                            Physics.SyncTransforms();

                            // Recreate bone visualization at the sampled pose
                            if (stage.NeedsBoneVisualization)
                            {
                                // For humanoid clips, Mecanim's pipeline already applies the FBX coordinate
                                // conversion internally when computing bone transforms through the Avatar.
                                // The root transform still carries the FBX import rotation (typically -90° X
                                // for Z-up to Y-up conversion), which would cause double rotation.
                                // Zero it out so bone world positions reflect Mecanim's already-converted output.
                                if (stage.FBXAnimationClip != null && stage.FBXAnimationClip.isHumanMotion)
                                {
                                    stage.InstantiatedPrefab.transform.localRotation = Quaternion.identity;
                                }

                                VisualizeSkeletonBones(stage.InstantiatedPrefab);
                            }

                            PositionCameraForCurrentFBXFrame(stage);
                        }

                        // Capture frame immediately after sampling (atomic operation)
                        // Skip for Particles and VFX since multi-sample capture already added the best frame to frames list
                        if (stage.PrefabType != PrefabType.Particles && stage.PrefabType != PrefabType.VFX)
                        {
                            // For Model-type prefabs that also contain particle effects (e.g., a car on fire),
                            // simulate particles to optimal time before capturing. Without this, particles remain
                            // at t=0 with zero emitted particles since no game loop runs in preview scenes.
                            // The multi-frame (animated) path has its own Model+Particles hybrid block,
                            // but the single-frame path previously had no particle simulation for Model type.
                            if (stage.PrefabType == PrefabType.Model)
                            {
                                ParticleSystem[] modelParticles = stage.InstantiatedPrefab.GetComponentsInChildren<ParticleSystem>();
                                if (modelParticles.Length > 0)
                                {
                                    HandleParticleSystems(stage.InstantiatedPrefab);

                                    // Force particle system bounds update after simulation
                                    ParticleSystemRenderer[] particleRenderers = stage.InstantiatedPrefab.GetComponentsInChildren<ParticleSystemRenderer>();
                                    foreach (ParticleSystemRenderer renderer in particleRenderers)
                                    {
                                        _ = renderer.bounds;
                                    }

                                    EditorApplication.QueuePlayerLoopUpdate();
                                }
                            }

                            Texture2D frame = stage.RenderFrame(nativeSize, nativeSize);
                            if (frame != null)
                            {
                                frames.Add(frame);
                            }
                        }

                        // Reset the sample time flag
                        stage.FBXStaticPreviewSampleTime = -1f;
                    }

                    // Log if no frames were captured
                    if (frames.Count == 0)
                    {
                        Debug.LogWarning($"[CustomPrefabPreviewGenerator] Nothing was captured for '{prefabPath}'");
                    }

                    // Post-render validation: verify frames have actual visible content (not just background).
                    // The pre-render ShouldSkipFrameCapture check exempts several types because they may not
                    // render in a quick detection pass. This catch-all validates the actual captured output.
                    // Particles remain exempt because sparse effects can cover less than the visibility threshold.
                    Color previewBackgroundColor = stage.Camera.backgroundColor;

                    if (stage.PrefabType == PrefabType.VFX && ShouldRetryVFXPreviewWithEvent(frames, previewBackgroundColor, out _))
                    {
                        VisualEffect[] vfxComponents = stage.InstantiatedPrefab.GetComponentsInChildren<VisualEffect>();
                        if (TryGetAnyCustomVFXEventName(vfxComponents, out string eventName))
                        {
                            DestroyFrames(frames);
                            yield return CaptureVFXFramesWithFirstCustomEvent(stage, nativeSize, frameCount, frames, prefabPath);
                        }
                    }

                    bool skipPostRenderValidation = stage.PrefabType == PrefabType.Particles;

                    if (frames.Count == 0 && stage.PrefabType == PrefabType.VFX)
                    {
                        if (AI.Config.LogPreviewCreation) Debug.LogWarning($"[CustomPrefabPreviewGenerator] Preview is empty (no frames) for '{prefabPath}'. Discarding.");
                        if (previewRequest != null) previewRequest.FailureReason = NO_VISIBLE_CONTENT_REASON;
                        skipFrameCapture = true;
                    }
                    else if (frames.Count > 0 && !skipPostRenderValidation)
                    {
                        if (!HasVisiblePreviewFrame(frames, previewBackgroundColor, out float bestVisiblePercentage))
                        {
                            if (AI.Config.LogPreviewCreation) Debug.LogWarning($"[CustomPrefabPreviewGenerator] Preview is empty (just background, {bestVisiblePercentage:F2}% different) for '{prefabPath}'. Discarding.");

                            DestroyFrames(frames);

                            if (previewRequest != null) previewRequest.FailureReason = NO_VISIBLE_CONTENT_REASON;
                            skipFrameCapture = true;
                        }
                    }

                    // Assemble result
                    try
                    {
                        resultTexture = AssembleResultTexture(frames, size, frameCount);
                    }
                    catch (Exception e)
                    {
                        hasError = true;
                        errorMessage = e.Message;
                        resultTexture = null;
                    }
                }

            } // end try
            finally
            {
                if (!cleanupDone)
                {
                    cleanupDone = true;

                    // Cleanup stage and scene
                    try
                    {
                        CleanupPreviewStage(stage, previewScene, isPreviewScene);
                    }
                    catch (Exception cleanupEx)
                    {
                        Debug.LogWarning($"[CustomPrefabPreviewGenerator] Cleanup failed for '{prefabPath}': {cleanupEx.Message}");
                    }
                    if (isVFXPrefab) _vfxSceneInUse = false;

                    // Set result
                    if (hasError)
                    {
                        Debug.LogError($"Error generating custom preview for {prefabPath}: {errorMessage}");
                    }
                    else if (resultTexture == null && !skipFrameCapture && AI.Config.LogPreviewCreation)
                    {
                        Debug.LogWarning($"[CustomPrefabPreviewGenerator] Preview generation did not return something for '{prefabPath}' (skipFrameCapture={skipFrameCapture})");
                    }
                    tcs.SetResult(resultTexture);
                }
            }
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

        private static bool HasSubEmitters(GameObject go)
        {
            // Check if any particle system has sub-emitters configured
            // Sub-emitters spawn child particles (commonly used for explosions, fireworks)
            ParticleSystem[] particleSystems = go.GetComponentsInChildren<ParticleSystem>();
            foreach (ParticleSystem ps in particleSystems)
            {
                ParticleSystem.SubEmittersModule subEmitters = ps.subEmitters;
                if (subEmitters.enabled && subEmitters.subEmittersCount > 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Simulates all particle systems to a specific time for frame capture.
        /// Supports both standard and trail-based particle systems.
        /// </summary>
        private static IEnumerator SimulateParticlesToTime(CustomPreviewStage stage, ParticleSystem[] particleSystems, float targetTime, bool hasSubEmitters, bool hasTrails)
        {
            // Find hierarchy-root particle systems for proper simulation of branched hierarchies
            List<ParticleSystem> rootPSList = FindRootParticleSystems(particleSystems, stage.InstantiatedPrefab);

            // For sub-emitter systems, use direct emission approach
            if (hasSubEmitters)
            {
                // Build list of sub-emitter target systems
                HashSet<ParticleSystem> subEmitterSystems = new HashSet<ParticleSystem>();
                foreach (ParticleSystem ps in particleSystems)
                {
                    ParticleSystem.SubEmittersModule subEmitters = ps.subEmitters;
                    if (subEmitters.enabled)
                    {
                        for (int i = 0; i < subEmitters.subEmittersCount; i++)
                        {
                            ParticleSystem subPS = subEmitters.GetSubEmitterSystem(i);
                            if (subPS != null)
                            {
                                subEmitterSystems.Add(subPS);
                            }
                        }
                    }
                }

                // Simulate all root-level particle systems
                if (rootPSList.Count > 0)
                {
                    foreach (ParticleSystem ps in particleSystems)
                    {
                        ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                        ps.Clear(false);
                    }
                    foreach (ParticleSystem ps in particleSystems)
                    {
                        ps.useAutoRandomSeed = false;
                        ps.randomSeed = AI.Config.cpParticleSeed;
                    }

                    ParticleSystem.MainModule rootMain = rootPSList[0].main;
                    float realTime = rootMain.simulationSpeed > 0f ? targetTime / rootMain.simulationSpeed : targetTime;
                    SimulateAllRoots(rootPSList, realTime, true, false);
                }

                // Handle sub-emitter targets that Simulate() couldn't trigger.
                // Simulate() doesn't generate sub-emitter trigger events, so systems
                // that are ONLY triggered as sub-emitters need manual Emit().
                if (subEmitterSystems.Count > 0 && rootPSList.Count > 0)
                {
                    // Determine parent particle position for sub-emitter placement
                    ParticleSystem emitSourcePS = rootPSList[0];
                    foreach (ParticleSystem rps in rootPSList)
                    {
                        if (rps.particleCount > 0)
                        {
                            emitSourcePS = rps;
                            break;
                        }
                    }
                    Vector3 emitPosition = emitSourcePS.transform.position;
                    if (emitSourcePS.particleCount > 0)
                    {
                        ParticleSystem.Particle[] rootParticles = new ParticleSystem.Particle[1];
                        emitSourcePS.GetParticles(rootParticles, 1);
                        Vector3 pPos = rootParticles[0].position;
                        if (emitSourcePS.main.simulationSpace == ParticleSystemSimulationSpace.Local)
                            emitPosition = emitSourcePS.transform.TransformPoint(pPos);
                        else
                            emitPosition = pPos;
                    }

                    foreach (ParticleSystem subPS in subEmitterSystems)
                    {
                        if (subPS.particleCount > 0) continue;

                        ParticleSystem.EmissionModule emission = subPS.emission;
                        int emitCount = emission.burstCount > 0 ? (int)emission.GetBurst(0).count.constant : 50;

                        // Move to parent particle position for proper spatial placement.
                        // Do NOT restore position here - the frame hasn't been rendered yet.
                        // Local-space particles move with the transform, so restoring before
                        // rendering would pull them back out of the camera view.
                        subPS.transform.position = emitPosition;

                        subPS.Emit(emitCount);

                        // Advance simulation to spread particles
                        ParticleSystem.MainModule subMain = subPS.main;
                        float advanceTime = Mathf.Min(targetTime, subMain.startLifetime.constant * 0.5f);
                        if (advanceTime > 0f)
                        {
                            subPS.Simulate(advanceTime, false, false, false);
                        }
                    }
                }
            }
            else
            {
                // Simulate all root-level particle systems with withChildren=true.
                // For typical prefabs this is identical to the previous single-root approach.
                if (rootPSList.Count > 0)
                {
                    foreach (ParticleSystem ps in particleSystems)
                    {
                        ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                        ps.Clear(false);
                    }
                    foreach (ParticleSystem ps in particleSystems)
                    {
                        ps.useAutoRandomSeed = false;
                        ps.randomSeed = AI.Config.cpParticleSeed;
                    }

                    ParticleSystem.MainModule rootMain = rootPSList[0].main;
                    float simSpeed = rootMain.simulationSpeed;
                    float realTime = simSpeed > 0f ? targetTime / simSpeed : targetTime;

                    SimulateAllRoots(rootPSList, realTime, true, hasTrails);
                }
            }

            // Allow renderer mesh preparation
            EditorApplication.QueuePlayerLoopUpdate();
            yield return null;
        }

        private static bool HasSkinnedMeshRenderer(GameObject go)
        {
            // Check if the prefab contains any SkinnedMeshRenderer components
            // SkinnedMeshRenderers (character models, animated meshes) require bone/skeleton initialization
            // and may not render correctly in the initial detection frame
            return go.GetComponentInChildren<SkinnedMeshRenderer>() != null;
        }

        private static bool HasTextMeshPro(GameObject go)
        {
            // Check if the prefab contains any TextMeshPro components
            // TextMeshPro generates meshes asynchronously and may not be ready for detection render
#if USE_TEXTMESHPRO || UNITY_2023_1_OR_NEWER
            return go.GetComponentInChildren<TMP_Text>(true) != null;
#else
            return false;
#endif
        }

        public static void HandleParticleSystems(GameObject go)
        {
            ParticleSystem[] particleSystems = go.GetComponentsInChildren<ParticleSystem>();
            if (particleSystems.Length == 0) return;

            // Check if any system is prewarmed - prewarmed systems are already at steady state
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

            // For prewarmed systems, we can't preserve the prewarm state in preview
            // Prewarm is designed for looping systems that start at steady-state, 
            // but our preview needs to capture a specific moment in time
            // Solution: Simulate them like normal systems (destroys prewarm but generates visible particles)
            if (hasPrewarmedSystem)
            {
                // Treat prewarmed systems like normal systems for preview purposes
                foreach (ParticleSystem ps in particleSystems)
                {
                    ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(false);
                    ps.useAutoRandomSeed = false;
                    ps.randomSeed = AI.Config.cpParticleSeed;

                    // Adjust optimal time for this system's simulation speed
                    // Simulate() takes real-time, not particle-time
                    ParticleSystem.MainModule main = ps.main;
                    float realTime = main.simulationSpeed > 0f ? optimalTime / main.simulationSpeed : optimalTime;

                    // Simulate to optimal time WITHOUT restart (restart breaks prewarmed state)
                    ps.Simulate(realTime, true, true, false);
                    ps.Play(true);
                }
                return;
            }

            // For non-prewarmed systems, simulate with optimal time
            foreach (ParticleSystem ps in particleSystems)
            {
                ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                ps.Clear(false);
                // Disable autoRandomSeed to allow manual seed control
                ps.useAutoRandomSeed = false;
                ps.randomSeed = AI.Config.cpParticleSeed;

                // Adjust optimal time for this system's simulation speed
                // Simulate() takes real-time, not particle-time
                ParticleSystem.MainModule main = ps.main;
                float realTime = main.simulationSpeed > 0f ? optimalTime / main.simulationSpeed : optimalTime;

                // Simulate to optimal time, including children, with restart and fixed time step
                ps.Simulate(realTime, true, true, false);
                // Ensure the system is in playing state for rendering
                ps.Play(true);
            }
        }

        public static void HandleVFXSystems(GameObject go)
        {
            VisualEffect[] vfxSystems = go.GetComponentsInChildren<VisualEffect>();
            if (vfxSystems.Length == 0) return;

            foreach (VisualEffect vfx in vfxSystems)
            {
                // Ensure VFX is enabled and active
                vfx.enabled = true;
                vfx.gameObject.SetActive(true);

                // Reset spawn event attributes to ensure fresh start
                vfx.Reinit();
                vfx.resetSeedOnPlay = false; // Keep consistent seed for preview

                // Start playing
                vfx.Play();
            }
        }

        public static float GetVFXDuration(GameObject go)
        {
            VisualEffect[] vfxSystems = go.GetComponentsInChildren<VisualEffect>();
            if (vfxSystems.Length == 0) return AI.Config.cpVFXMaxDuration;

            float maxDuration = 0f;

            foreach (VisualEffect vfx in vfxSystems)
            {
                if (vfx.visualEffectAsset == null) continue;

                float duration = AI.Config.cpVFXMaxDuration; // Fallback

                // Try to detect duration from common exposed properties
                if (vfx.HasFloat("Duration"))
                {
                    duration = vfx.GetFloat("Duration");
                }
                else if (vfx.HasFloat("LifeTime"))
                {
                    duration = vfx.GetFloat("LifeTime");
                }
                else if (vfx.HasFloat("Lifetime"))
                {
                    duration = vfx.GetFloat("Lifetime");
                }
                else if (vfx.HasFloat("LoopDuration"))
                {
                    duration = vfx.GetFloat("LoopDuration");
                }
                else
                {
                    // Check if the effect is set to loop or play once
                    // For looping effects, use a reasonable default
                    // For one-shot effects, we might be able to estimate from particle lifetime
                    if (vfx.HasBool("Loop"))
                    {
                        bool isLooping = vfx.GetBool("Loop");
                        if (!isLooping)
                        {
                            // One-shot effect, try to find max lifetime
                            if (vfx.HasFloat("ParticleLifetime") || vfx.HasFloat("Particle Lifetime"))
                            {
                                string lifetimeProperty = vfx.HasFloat("ParticleLifetime") ? "ParticleLifetime" : "Particle Lifetime";
                                duration = vfx.GetFloat(lifetimeProperty);
                            }
                        }
                    }
                }

                // Cap at max duration setting to prevent extremely long waits
                duration = Mathf.Min(duration, AI.Config.cpVFXMaxDuration);
                maxDuration = Mathf.Max(maxDuration, duration);
            }

            // If we couldn't detect any duration, use a shorter default for better preview performance
            // Most VFX effects show their key features within 2-3 seconds
            if (maxDuration == 0f || maxDuration > 3f)
            {
                maxDuration = Mathf.Min(3f, AI.Config.cpVFXMaxDuration);
            }

            return maxDuration;
        }

        private static float GetPeakParticleTime(GameObject go, float totalDuration, int sampleCount = 10)
        {
            // For effects with sub-emitters (fireworks, explosions), particle sampling doesn't work
            // because sub-emitters only spawn during actual runtime events (particle death, collision)
            // which don't occur during Simulate() calls. Instead, calculate based on parent timing.

            ParticleSystem[] particleSystems = go.GetComponentsInChildren<ParticleSystem>();
            if (particleSystems.Length == 0) return totalDuration * 0.5f;

            // Check for sub-emitter systems and calculate when they would trigger
            float subEmitterTriggerTime = 0f;
            bool hasSubEmitterSystem = false;

            foreach (ParticleSystem ps in particleSystems)
            {
                ParticleSystem.SubEmittersModule subEmitters = ps.subEmitters;
                if (subEmitters.enabled && subEmitters.subEmittersCount > 0)
                {
                    // Sub-emitters typically trigger on Death events for fireworks
                    ParticleSystem.MainModule main = ps.main;

                    // Get parent particle lifetime - this is when sub-emitters would trigger
                    float parentLifetime = main.startLifetime.constant;
                    if (main.startLifetime.mode == ParticleSystemCurveMode.TwoConstants)
                    {
                        parentLifetime = (main.startLifetime.constantMin + main.startLifetime.constantMax) / 2f;
                    }

                    // Get parent start delay
                    float parentDelay = main.startDelay.constant;
                    if (main.startDelay.mode == ParticleSystemCurveMode.TwoConstants)
                    {
                        parentDelay = (main.startDelay.constantMin + main.startDelay.constantMax) / 2f;
                    }

                    // Sub-emitters trigger when parent particles die
                    // For Death sub-emitters, this happens at: delay + lifetime
                    float triggerTime = parentDelay + parentLifetime;

                    // Now find the sub-emitter particles and get their properties
                    for (int i = 0; i < subEmitters.subEmittersCount; i++)
                    {
                        ParticleSystem subEmitterPS = subEmitters.GetSubEmitterSystem(i);
                        if (subEmitterPS != null)
                        {
                            ParticleSystem.MainModule subMain = subEmitterPS.main;
                            float subLifetime = subMain.startLifetime.constant;
                            if (subMain.startLifetime.mode == ParticleSystemCurveMode.TwoConstants)
                            {
                                subLifetime = (subMain.startLifetime.constantMin + subMain.startLifetime.constantMax) / 2f;
                            }

                            // Skip sub-emitters with extremely long lifetimes (> 10s)
                            // These are typically ambient effects (smoke, fog) not the main explosion
                            // For fireworks, the explosion particles usually have lifetimes < 5s
                            if (subLifetime > 10f)
                            {
                                continue;
                            }

                            // Optimal capture time is when sub-emitter is at 30-50% of its lifetime
                            // This is when explosion particles are at peak visibility
                            float optimalOffset = subLifetime * 0.4f;
                            float optimalTime = triggerTime + optimalOffset;

                            // For multiple short-lived sub-emitters, use the first one
                            // (the first is typically the main explosion, later ones are secondary effects)
                            if (!hasSubEmitterSystem || optimalTime < subEmitterTriggerTime)
                            {
                                subEmitterTriggerTime = optimalTime;
                                hasSubEmitterSystem = true;
                            }

                        }
                    }
                }
            }

            // If we found sub-emitter systems, use the calculated trigger time
            if (hasSubEmitterSystem)
            {
                return subEmitterTriggerTime > 0f ? subEmitterTriggerTime : totalDuration * 0.5f;
            }

            // For non-sub-emitter systems, fall back to particle sampling
            float peakTime = 0f;
            int maxParticleCount = 0;

            // Sample at multiple time points
            for (int sample = 0; sample < sampleCount; sample++)
            {
                float time = totalDuration * (sample / (float)(sampleCount - 1));
                int totalParticlesAtTime = 0;

                // Simulate all systems to this time point
                foreach (ParticleSystem ps in particleSystems)
                {
                    ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(false);
                    ps.useAutoRandomSeed = false;
                    ps.randomSeed = AI.Config.cpParticleSeed;

                    // Adjust for simulation speed
                    ParticleSystem.MainModule main = ps.main;
                    float realTime = main.simulationSpeed > 0f ? time / main.simulationSpeed : time;
                    ps.Simulate(realTime, true, true, true);
                }

                // Count total particles alive at this time
                foreach (ParticleSystem ps in particleSystems)
                {
                    totalParticlesAtTime += ps.particleCount;
                }

                // Track the time with maximum particles
                if (totalParticlesAtTime > maxParticleCount)
                {
                    maxParticleCount = totalParticlesAtTime;
                    peakTime = time;
                }
            }

            // If no particles were found at any time, fall back to midpoint
            if (maxParticleCount == 0)
            {
                return totalDuration * 0.5f;
            }

            return peakTime;
        }

        public static float CalculateOptimalParticleTime(GameObject go)
        {
            ParticleSystem[] particleSystems = go.GetComponentsInChildren<ParticleSystem>();
            if (particleSystems.Length == 0) return AI.Config.cpParticleSimulateTime;

            // For effects with sub-emitters (fireworks, explosions), use peak particle time analysis
            // Sub-emitters spawn child particles later in the timeline, creating delayed visual peaks
            bool hasSubEmitters = HasSubEmitters(go);

            // Also detect delayed bursts by checking emission module
            bool hasDelayedBursts = false;
            foreach (ParticleSystem ps in particleSystems)
            {
                ParticleSystem.EmissionModule emission = ps.emission;
                if (emission.enabled)
                {
                    // Check if there are bursts that occur after initial emission
                    for (int i = 0; i < emission.burstCount; i++)
                    {
                        ParticleSystem.Burst burst = emission.GetBurst(i);
                        if (burst.time > 0.1f) // Burst happens after 0.1s
                        {
                            hasDelayedBursts = true;
                            break;
                        }
                    }
                }
                if (hasDelayedBursts) break;
            }

            // For effects with sub-emitters or delayed bursts, use particle sampling to find peak moment
            if (hasSubEmitters || hasDelayedBursts)
            {
                // Calculate total duration first
                float totalDuration = GetParticleSystemDuration(go);

                // Use peak particle time analysis to find the explosion/burst moment
                float peakTime = GetPeakParticleTime(go, totalDuration, 10);

                // Cap at reasonable time
                peakTime = Mathf.Min(peakTime, AI.Config.cpVFXMaxDuration);


                return peakTime > 0f ? peakTime : AI.Config.cpParticleSimulateTime;
            }

            float optimalTime = 0f;

            foreach (ParticleSystem ps in particleSystems)
            {
                ParticleSystem.MainModule main = ps.main;
                float particleTime;

                // Get start delay
                float startDelay = main.startDelay.constant;
                if (main.startDelay.mode == ParticleSystemCurveMode.TwoConstants)
                {
                    startDelay = (main.startDelay.constantMin + main.startDelay.constantMax) / 2f;
                }

                // Get particle lifetime
                float particleLifetime = main.startLifetime.constant;
                if (main.startLifetime.mode == ParticleSystemCurveMode.TwoConstants)
                {
                    particleLifetime = (main.startLifetime.constantMin + main.startLifetime.constantMax) / 2f;
                }
                else if (main.startLifetime.mode == ParticleSystemCurveMode.Curve || main.startLifetime.mode == ParticleSystemCurveMode.TwoCurves)
                {
                    // For curves, estimate the average lifetime
                    if (main.startLifetime.mode == ParticleSystemCurveMode.Curve)
                    {
                        AnimationCurve curve = main.startLifetime.curve;
                        if (curve.length > 0)
                        {
                            float sum = 0f;
                            foreach (Keyframe key in curve.keys)
                            {
                                sum += key.value;
                            }
                            particleLifetime = sum / curve.length;
                        }
                    }
                    else // TwoCurves
                    {
                        particleLifetime = (main.startLifetime.curveMin.keys[0].value + main.startLifetime.curveMax.keys[0].value) / 2f;
                    }
                }

                // Calculate optimal capture time based on system type
                if (main.prewarm)
                {
                    // Prewarmed systems are already at steady state
                    // For explosions and fast effects with Color/Size Over Lifetime curves,
                    // we need to capture VERY early (at peak brightness) before fade curves take effect
                    // Capture at 1-3% of lifetime, which catches particles at their brightest moment
                    particleTime = Mathf.Clamp(particleLifetime * 0.02f, 0.01f, 0.05f);
                }
                else if (main.loop)
                {
                    // For looping systems, capture at the middle of the cycle when particle density is stable
                    // This is typically after initial ramp-up
                    float cycleTime = Mathf.Min(main.duration, particleLifetime);
                    particleTime = startDelay + cycleTime * 0.5f;
                }
                else
                {
                    // For non-looping systems, capture when particles are at peak visibility
                    // This is typically 30-50% through the emission phase, before particles start dying
                    // We want to capture when we have good particle count but before they start fading

                    // Calculate total effect duration (all in particle-time)
                    float totalDuration = startDelay + main.duration + particleLifetime;

                    // For ultra-short effects (< 0.5s), use 50% to ensure peak visibility
                    // For very short effects (< 0.2s like muzzle flashes), use 50-60%
                    float capturePercent;
                    if (totalDuration < 0.2f)
                    {
                        capturePercent = 0.55f; // 55% for extremely short effects
                    }
                    else if (totalDuration < 0.5f)
                    {
                        capturePercent = 0.5f; // 50% for ultra-short effects
                    }
                    else
                    {
                        capturePercent = 0.4f; // 40% for normal length effects
                    }

                    float emissionPhase = main.duration * capturePercent;
                    particleTime = startDelay + Mathf.Min(emissionPhase, particleLifetime * 0.5f);

                    // Ensure minimum time to allow burst particles to spawn
                    particleTime = Mathf.Max(particleTime, 0.01f);
                }

                // Cap at a reasonable time to prevent extremely long waits
                // Guard against infinity/NaN from particle system configuration
                if (float.IsInfinity(particleTime) || float.IsNaN(particleTime))
                {
                    particleTime = AI.Config.cpVFXMaxDuration;
                }
                particleTime = Mathf.Min(particleTime, AI.Config.cpVFXMaxDuration);

                // NOTE: We calculate in "particle-time" here, NOT real-time
                // The simulationSpeed adjustment happens when calling Simulate() per-system
                optimalTime = Mathf.Max(optimalTime, particleTime);
            }

            // If we couldn't calculate a time, use the config default
            // Guard against infinity/NaN
            if (float.IsInfinity(optimalTime) || float.IsNaN(optimalTime) || optimalTime == 0f)
            {
                optimalTime = AI.Config.cpParticleSimulateTime;
            }

            return optimalTime;
        }

        public static float GetParticleSystemDuration(GameObject go)
        {
            ParticleSystem[] particleSystems = go.GetComponentsInChildren<ParticleSystem>();
            if (particleSystems.Length == 0) return AI.Config.cpVFXMaxDuration;

            float maxDuration = 0f;

            foreach (ParticleSystem ps in particleSystems)
            {
                ParticleSystem.MainModule main = ps.main;
                float duration;

                // Get start delay
                float startDelay = main.startDelay.constant;
                if (main.startDelay.mode == ParticleSystemCurveMode.TwoConstants)
                {
                    startDelay = main.startDelay.constantMax; // Use max delay for duration calculation
                }

                // Get particle lifetime
                float particleLifetime = main.startLifetime.constant;
                if (main.startLifetime.mode == ParticleSystemCurveMode.TwoConstants)
                {
                    particleLifetime = main.startLifetime.constantMax; // Use max lifetime for duration
                }
                else if (main.startLifetime.mode == ParticleSystemCurveMode.Curve || main.startLifetime.mode == ParticleSystemCurveMode.TwoCurves)
                {
                    // For curves, use the maximum value
                    if (main.startLifetime.mode == ParticleSystemCurveMode.Curve)
                    {
                        AnimationCurve curve = main.startLifetime.curve;
                        if (curve.length > 0)
                        {
                            float maxValue = curve.keys[0].value;
                            foreach (Keyframe key in curve.keys)
                            {
                                if (key.value > maxValue) maxValue = key.value;
                            }
                            particleLifetime = maxValue;
                        }
                    }
                    else // TwoCurves
                    {
                        float maxValue = main.startLifetime.curveMax.keys[0].value;
                        foreach (Keyframe key in main.startLifetime.curveMax.keys)
                        {
                            if (key.value > maxValue) maxValue = key.value;
                        }
                        particleLifetime = maxValue;
                    }
                }

                // For looping particle systems, use the loop duration
                if (main.loop)
                {
                    // If prewarm is enabled, particles are already at steady state
                    // so we only need the duration itself
                    if (main.prewarm)
                    {
                        duration = main.duration;
                    }
                    else
                    {
                        // For non-prewarmed looping systems, cap at a reasonable preview duration
                        // Use the system duration as a reasonable capture duration
                        duration = Mathf.Min(main.duration, AI.Config.cpVFXMaxDuration);
                    }
                }
                else
                {
                    // For non-looping systems, total effect time includes delay, duration, and particle lifetime
                    duration = startDelay + main.duration + particleLifetime;
                }

                // NOTE: Duration is returned in "particle-time" (universal time for the effect)
                // The simulationSpeed adjustment happens per-system when calling Simulate()

                // Cap at max duration setting to prevent extremely long waits
                // Also guard against infinity/NaN values from particle system configuration
                if (float.IsInfinity(duration) || float.IsNaN(duration))
                {
                    duration = AI.Config.cpVFXMaxDuration;
                }
                duration = Mathf.Min(duration, AI.Config.cpVFXMaxDuration);
                maxDuration = Mathf.Max(maxDuration, duration);
            }

            // If we couldn't detect any duration, use the config max duration
            if (float.IsInfinity(maxDuration) || float.IsNaN(maxDuration) || maxDuration == 0f)
            {
                maxDuration = AI.Config.cpVFXMaxDuration;
            }

            return maxDuration;
        }

        /// <summary>
        /// Returns a shorter duration focused on the visually active period of the effect.
        /// For non-looping systems this is the emission phase plus a brief aftermath (25% of
        /// max particle lifetime, capped at 0.5s). For looping systems the full duration is used.
        /// This prevents animated previews from wasting frames on the dying/fading tail.
        /// </summary>
        public static float GetVisuallyActiveDuration(GameObject go)
        {
            return GetVisuallyActiveDuration(go, out _);
        }

        /// <summary>
        /// Returns a shorter duration focused on the visually active period of the effect.
        /// Detects burst-only systems (no continuous rateOverTime/rateOverDistance) and uses
        /// lastBurstTime + particleLifetime instead of the system duration which may be much longer.
        /// </summary>
        /// <param name="go">The particle system root GameObject</param>
        /// <param name="isBurstDominated">Output: true when majority of systems are burst-only,
        /// useful for callers that want to front-load frame distribution.</param>
        public static float GetVisuallyActiveDuration(GameObject go, out bool isBurstDominated)
        {
            isBurstDominated = false;
            ParticleSystem[] particleSystems = go.GetComponentsInChildren<ParticleSystem>();
            if (particleSystems.Length == 0) return AI.Config.cpVFXMaxDuration;

            float maxActiveEnd = 0f;
            int burstOnlyCount = 0;
            int nonLoopingCount = 0;

            foreach (ParticleSystem ps in particleSystems)
            {
                ParticleSystem.MainModule main = ps.main;

                // Get start delay
                float startDelay = main.startDelay.constant;
                if (main.startDelay.mode == ParticleSystemCurveMode.TwoConstants)
                {
                    startDelay = main.startDelay.constantMax;
                }

                // Get particle lifetime
                float particleLifetime = main.startLifetime.constant;
                if (main.startLifetime.mode == ParticleSystemCurveMode.TwoConstants)
                {
                    particleLifetime = main.startLifetime.constantMax;
                }
                else if (main.startLifetime.mode == ParticleSystemCurveMode.Curve ||
                         main.startLifetime.mode == ParticleSystemCurveMode.TwoCurves)
                {
                    if (main.startLifetime.mode == ParticleSystemCurveMode.Curve)
                    {
                        AnimationCurve curve = main.startLifetime.curve;
                        if (curve.length > 0)
                        {
                            float maxVal = curve.keys[0].value;
                            foreach (Keyframe k in curve.keys)
                            {
                                if (k.value > maxVal) maxVal = k.value;
                            }
                            particleLifetime = maxVal;
                        }
                    }
                    else
                    {
                        float maxVal = main.startLifetime.curveMax.keys[0].value;
                        foreach (Keyframe k in main.startLifetime.curveMax.keys)
                        {
                            if (k.value > maxVal) maxVal = k.value;
                        }
                        particleLifetime = maxVal;
                    }
                }

                float systemActiveEnd;

                if (main.loop)
                {
                    float loopDur = main.prewarm ? main.duration : Mathf.Min(main.duration, AI.Config.cpVFXMaxDuration);
                    systemActiveEnd = loopDur;
                }
                else
                {
                    nonLoopingCount++;

                    // Detect burst-only systems: no continuous emission rate, particles come from bursts only
                    ParticleSystem.EmissionModule emission = ps.emission;
                    bool systemIsBurstOnly = false;
                    float lastBurstTime = 0f;

                    if (emission.enabled)
                    {
                        float rateOT = emission.rateOverTimeMultiplier;
                        float rateOD = emission.rateOverDistanceMultiplier;

                        if (rateOT <= 0f && rateOD <= 0f && emission.burstCount > 0)
                        {
                            systemIsBurstOnly = true;
                            burstOnlyCount++;
                            for (int b = 0; b < emission.burstCount; b++)
                            {
                                ParticleSystem.Burst burst = emission.GetBurst(b);
                                lastBurstTime = Mathf.Max(lastBurstTime, burst.time);
                            }
                        }
                    }

                    if (systemIsBurstOnly)
                    {
                        // Burst-only: visual content spans from burst fire until particles die.
                        // No need for extra aftermath — the lifetime IS the aftermath.
                        systemActiveEnd = startDelay + lastBurstTime + particleLifetime;
                    }
                    else
                    {
                        // Rate-based or mixed emission: emission lasts the full duration.
                        // Add a brief aftermath to show initial particle trajectory after emission stops.
                        float aftermath = Mathf.Min(particleLifetime * 0.25f, 0.5f);
                        systemActiveEnd = startDelay + main.duration + aftermath;
                    }
                }

                maxActiveEnd = Mathf.Max(maxActiveEnd, systemActiveEnd);
            }

            // Effect is burst-dominated if majority of non-looping systems are burst-only
            isBurstDominated = nonLoopingCount > 0 && burstOnlyCount >= nonLoopingCount * 0.6f;

            // Guard against degenerate values
            if (float.IsInfinity(maxActiveEnd) || float.IsNaN(maxActiveEnd) || maxActiveEnd <= 0f)
            {
                maxActiveEnd = AI.Config.cpVFXMaxDuration;
            }

            return Mathf.Min(maxActiveEnd, AI.Config.cpVFXMaxDuration);
        }

        /// <summary>
        /// Calculate the effective look-at center and distance scale factor for particle effects
        /// where the bounds center is significantly offset from the prefab root.
        /// This consolidates the repeated effectiveCenter correction logic into a single method.
        /// </summary>
        /// <param name="bounds">The particle bounds</param>
        /// <param name="prefabRoot">The prefab's root world position</param>
        /// <param name="effectiveCenter">Output: the corrected look-at center</param>
        /// <param name="distanceScale">Output: multiplier for camera distance (0.5–1.0)</param>
        public static void CalculateEffectiveCenterAndScale(Bounds bounds, Vector3 prefabRoot, out Vector3 effectiveCenter, out float distanceScale)
        {
            effectiveCenter = bounds.center;
            distanceScale = 1f;

            float offsetFromRoot = Vector3.Distance(bounds.center, prefabRoot);
            float boundsRadius = bounds.extents.magnitude;
            float offsetRatio = boundsRadius > 0.01f ? offsetFromRoot / boundsRadius : 0f;

            if (offsetRatio > 0.7f && boundsRadius > 0.01f)
            {
                float extremeRatio = Mathf.Clamp01((offsetRatio - 0.7f) / 0.3f);
                float rootWeight = Mathf.Lerp(0.8f, 1.0f, extremeRatio);
                effectiveCenter = Vector3.Lerp(bounds.center, prefabRoot, rootWeight);

                // Keep X and Y from bounds.center to maintain horizontal and vertical centering
                // Only Z (depth) should be shifted toward root to handle trails extending away
                effectiveCenter.x = bounds.center.x;
                effectiveCenter.y = bounds.center.y;

                // Scale distance based on shift relative to max bounds dimension
                Vector3 centerShift = bounds.center - effectiveCenter;
                float shiftMagnitude = centerShift.magnitude;
                float maxBoundsDimension = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
                float shiftRatio = maxBoundsDimension > 0.01f ? shiftMagnitude / maxBoundsDimension : 0f;

                if (shiftRatio > 0.10f)
                {
                    // Map shiftRatio to distance scale:
                    // shiftRatio 0.10 -> distanceScale 1.0 (no change)
                    // shiftRatio 0.30+ -> distanceScale 0.5 (moderate zoom in, was 0.25)
                    // Raised floor from 0.25 to 0.5 to prevent animated frames from flying off-screen
                    float scaleFactor = Mathf.Clamp01((shiftRatio - 0.10f) / 0.20f);
                    distanceScale = Mathf.Lerp(1f, 0.5f, scaleFactor);
                }
                else
                {
                    // For smaller shifts, use the horizontal-dominated logic
                    Vector3 offsetVector = bounds.center - prefabRoot;
                    float horizontalOffset = Mathf.Sqrt(offsetVector.x * offsetVector.x + offsetVector.z * offsetVector.z);
                    float verticalOffset = Mathf.Abs(offsetVector.y);
                    if (horizontalOffset > verticalOffset * 0.5f)
                    {
                        distanceScale = Mathf.Lerp(1f, 0.5f, extremeRatio);
                    }
                }
            }
        }

        /// <summary>
        /// Position the camera for a particle effect using the standard particle camera angles
        /// and the effectiveCenter correction. This consolidates the repeated inline camera
        /// positioning code used in pre-passes and post-emit repositioning.
        /// </summary>
        /// <param name="bounds">The particle bounds to frame</param>
        /// <param name="camera">The preview camera</param>
        /// <param name="prefabRoot">The prefab's root world position</param>
        /// <returns>True if camera was successfully repositioned, false if bounds were invalid</returns>
        public static bool PositionParticleCamera(Bounds bounds, Camera camera, Vector3 prefabRoot)
        {
            if (bounds.size.magnitude <= 0.01f) return false;

            float particleAngleX = 20f;
            float particleAngleY = -50f;
            float camDistance = PreviewCameraSetup.CalculateCameraDistance(bounds, camera, AI.Config.cpFramingPadding, particleAngleX, particleAngleY, 1f);

            // NaN guard
            bool hasNaN = float.IsNaN(bounds.center.x) || float.IsNaN(bounds.center.y) || float.IsNaN(bounds.center.z) ||
                float.IsNaN(camDistance) || float.IsInfinity(camDistance);
            if (hasNaN || camDistance <= 0f) return false;

            CalculateEffectiveCenterAndScale(bounds, prefabRoot, out Vector3 effectiveCenter, out float distanceScale);
            camDistance *= distanceScale;

            camera.transform.position = new Vector3(effectiveCenter.x, effectiveCenter.y, effectiveCenter.z - camDistance);
            camera.transform.LookAt(effectiveCenter);
            camera.transform.RotateAround(effectiveCenter, Vector3.right, particleAngleX);
            camera.transform.RotateAround(effectiveCenter, Vector3.up, particleAngleY);

            return true;
        }

        public static void PositionCameraForPrefab(GameObject prefab, Camera camera, PrefabType prefabType, float animationDuration = 0f)
        {
            if (prefabType == PrefabType.Model || prefabType == PrefabType.FBX)
            {
                camera.fieldOfView = AI.Config.cpCameraFOV;
            }
            else
            {
                camera.fieldOfView = PreviewCameraSetup.DefaultPreviewFOV;
            }

            // For UI, use RectTransform bounds instead of Renderer bounds
            if (prefabType == PrefabType.UI)
            {
                Canvas canvas = prefab.GetComponentInChildren<Canvas>();
                if (canvas == null)
                {
                    canvas = prefab.GetComponentInParent<Canvas>();
                }

                if (canvas == null)
                {
                    camera.transform.position = new Vector3(0, 0, -100);
                    camera.transform.rotation = Quaternion.identity;
                    camera.orthographicSize = 1f;
                    return;
                }

                Bounds contentBounds = PreviewBoundsCalculator.GetVisibleUIBounds(prefab);

                Vector3 center = contentBounds.center;
                float width = contentBounds.size.x;
                float height = contentBounds.size.y;

                if (width < 0.01f || height < 0.01f)
                {
                    camera.transform.position = new Vector3(0, 0, -100);
                    camera.transform.rotation = Quaternion.identity;
                    camera.orthographicSize = 1f;
                    return;
                }

                camera.transform.position = new Vector3(center.x, center.y, center.z - 100f);
                camera.transform.rotation = Quaternion.identity;

                float paddingFactor = 1.1f;
                float heightBasedSize = (height / 2f) * paddingFactor;
                float widthBasedSize = (width / 2f) * paddingFactor;

                camera.orthographicSize = Mathf.Max(heightBasedSize, widthBasedSize);
                return;
            }

            Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>();
            if (renderers == null || renderers.Length == 0)
            {
                camera.transform.position = new Vector3(0, 2, -5);
                camera.transform.LookAt(Vector3.zero);
                return;
            }

            Bounds bounds;
            if (prefabType == PrefabType.Particles && animationDuration > 0f)
            {
                bounds = PreviewBoundsCalculator.GetParticleBoundsOverTime(prefab, animationDuration, 10);
            }
            else if (prefabType == PrefabType.VFX && animationDuration > 0f)
            {
                bounds = PreviewBoundsCalculator.GetVFXBoundsOverTime(prefab, animationDuration);
            }
            else
            {
                bounds = PreviewBoundsCalculator.GetGlobalBounds(renderers, prefabType, prefab);
            }

            // Check if particle/VFX prefab contains non-particle renderers (models/meshes)
            // If so, use 3D model camera positioning logic for better framing of the combined content
            bool hasNonParticleRenderers = false;
            if (prefabType == PrefabType.Particles || prefabType == PrefabType.VFX)
            {
                foreach (Renderer renderer in renderers)
                {
                    // Skip ParticleSystemRenderer and VFXRenderer (check by type name since VFXRenderer is internal)
                    if (renderer is ParticleSystemRenderer) continue;
                    if (renderer.GetType().Name == "VFXRenderer") continue;

                    hasNonParticleRenderers = true;
                    break;
                }
            }

            // Position camera based on prefab type
            // For particles/VFX with models, use 3D model positioning to properly frame the geometry
            if ((prefabType == PrefabType.Particles || prefabType == PrefabType.VFX) && !hasNonParticleRenderers)
            {
                // Particles-only and VFX-only use fixed angles for consistent viewing
                float particleAngleX = 20f; // Slight downward angle
                float particleAngleY = -50f; // Slight side angle (negative for left rotation)

                float vfxFill = prefabType == PrefabType.VFX ? 0.5f : 1f;

                // Calculate distance using projected bounds with particle-specific angles
                float distance = PreviewCameraSetup.CalculateCameraDistance(bounds, camera, AI.Config.cpFramingPadding, particleAngleX, particleAngleY, vfxFill);

                // Use shared helper for effectiveCenter correction and distance scaling
                Vector3 prefabRoot = prefab.transform.position;
                CalculateEffectiveCenterAndScale(bounds, prefabRoot, out Vector3 effectiveCenter, out float distanceScale);

                // Apply distance scale
                distance *= distanceScale;

                // Position camera in front (along -Z axis) to match Unity editor's default view
                // Ensures particles that emit horizontally (e.g., confetti to the right) appear correctly oriented
                // Use bounds-based effectiveCenter directly - this represents where particles actually are
                Vector3 lookAtTarget = effectiveCenter;

                camera.transform.position = new Vector3(lookAtTarget.x, lookAtTarget.y, lookAtTarget.z - distance);
                camera.transform.LookAt(lookAtTarget);

                // Apply the fixed angles - rotate around lookAtTarget (the centered position)
                camera.transform.RotateAround(lookAtTarget, Vector3.right, particleAngleX);
                camera.transform.RotateAround(lookAtTarget, Vector3.up, particleAngleY);
            }
            else
            {
                // For 3D models (including FBX and particles with models), use projected bounds calculation
                // This properly considers object dimensions, FOV, aspect ratio, and viewing angles
                // FBX models use mesh-based bounds for accurate sizing
                float framingPadding = prefabType == PrefabType.FBX ? 0f : AI.Config.cpFramingPadding;
                float fillFraction = prefabType == PrefabType.FBX ? 0.8f : 1f; // Bring camera closer for 10-15% larger appearance
                float distance = PreviewCameraSetup.CalculateCameraDistance(bounds, camera, framingPadding, AI.Config.cpCameraAngleX, AI.Config.cpCameraAngleY, fillFraction);

                // Position camera above (along +Y axis) and apply custom angles.
                // Use the calculated distance directly so that padding is controlled solely by the
                // distance algorithm (including cpFramingPadding) rather than an extra fixed factor.
                camera.transform.position = new Vector3(bounds.center.x, bounds.center.y + distance, bounds.center.z);
                camera.transform.LookAt(bounds.center);

                // Apply custom camera angles for models
                camera.transform.RotateAround(bounds.center, Vector3.left, AI.Config.cpCameraAngleX);
                camera.transform.RotateAround(bounds.center, Vector3.up, AI.Config.cpCameraAngleY);

                // Compensate for perspective shift when viewing at an angle
                // When camera looks down at the object, parts above center appear larger (closer)
                // and parts below appear smaller (farther), shifting the visual center downward.
                // Looking at a point above geometric center compensates for this effect.
                float verticalAngleRad = AI.Config.cpCameraAngleX * Mathf.Deg2Rad;
                float perspectiveCompensation = bounds.extents.y * Mathf.Sin(verticalAngleRad) * -0.15f;
                Vector3 lookTarget = bounds.center + Vector3.up * perspectiveCompensation;
                camera.transform.LookAt(lookTarget);

                // Refine distance using the actual viewport coverage of the bounds so that
                // with 0% padding the object almost touches the frame, and with N% padding
                // the margin is predictable.
                {
                    Vector3 center = bounds.center;
                    Vector3 extents = bounds.extents;

                    Vector3[] corners = new Vector3[8];
                    corners[0] = center + new Vector3(-extents.x, -extents.y, -extents.z);
                    corners[1] = center + new Vector3(-extents.x, -extents.y, +extents.z);
                    corners[2] = center + new Vector3(-extents.x, +extents.y, -extents.z);
                    corners[3] = center + new Vector3(-extents.x, +extents.y, +extents.z);
                    corners[4] = center + new Vector3(+extents.x, -extents.y, -extents.z);
                    corners[5] = center + new Vector3(+extents.x, -extents.y, +extents.z);
                    corners[6] = center + new Vector3(+extents.x, +extents.y, -extents.z);
                    corners[7] = center + new Vector3(+extents.x, +extents.y, +extents.z);

                    float minVX = 1f;
                    float maxVX = 0f;
                    float minVY = 1f;
                    float maxVY = 0f;

                    for (int i = 0; i < corners.Length; i++)
                    {
                        Vector3 vp = camera.WorldToViewportPoint(corners[i]);
                        if (vp.z <= 0f) continue;

                        if (vp.x < minVX) minVX = vp.x;
                        if (vp.x > maxVX) maxVX = vp.x;
                        if (vp.y < minVY) minVY = vp.y;
                        if (vp.y > maxVY) maxVY = vp.y;
                    }

                    float halfWidth = (maxVX - minVX) * 0.5f;
                    float halfHeight = (maxVY - minVY) * 0.5f;
                    float halfMax = Mathf.Max(halfWidth, halfHeight);

                    // Center the projected bounds in the viewport by shifting the camera
                    // along its right and up axes based on the offset between the projected
                    // bounds center and the viewport center (0.5, 0.5).
                    {
                        // Recompute viewport extents after distance refinement
                        float cMinVX = 1f;
                        float cMaxVX = 0f;
                        float cMinVY = 1f;
                        float cMaxVY = 0f;

                        for (int i = 0; i < corners.Length; i++)
                        {
                            Vector3 vp = camera.WorldToViewportPoint(corners[i]);
                            if (vp.z <= 0f) continue;

                            if (vp.x < cMinVX) cMinVX = vp.x;
                            if (vp.x > cMaxVX) cMaxVX = vp.x;
                            if (vp.y < cMinVY) cMinVY = vp.y;
                            if (vp.y > cMaxVY) cMaxVY = vp.y;
                        }

                        float centerVX = (cMinVX + cMaxVX) * 0.5f;
                        float centerVY = (cMinVY + cMaxVY) * 0.5f;

                        // Determine target vertical center. For objects that occupy most of the
                        // viewport height (very tall, thin), bias the center slightly downward so
                        // there is a bit of top margin and less unused space at the bottom.
                        float targetCenterY = 0.5f;
                        float projectedHeightForCentering = cMaxVY - cMinVY;
                        if (projectedHeightForCentering > 0.85f)
                        {
                            // Tallness in [0,1] starting when the projected height exceeds 85% of the viewport.
                            // At 85% we keep the original 0.5 center; at 100% we move down toward 0.44.
                            float tallCenter = Mathf.Clamp01((projectedHeightForCentering - 0.85f) / 0.15f);
                            targetCenterY = Mathf.Lerp(0.5f, 0.44f, tallCenter);
                        }

                        float offsetX = 0.5f - centerVX;
                        float offsetY = targetCenterY - centerVY;

                        if (Mathf.Abs(offsetX) > 0.0001f || Mathf.Abs(offsetY) > 0.0001f)
                        {
                            float vertFOV = camera.fieldOfView * Mathf.Deg2Rad;
                            float aspect = camera.aspect <= 0f ? 1f : camera.aspect;
                            float horizFOV = 2f * Mathf.Atan(Mathf.Tan(vertFOV * 0.5f) * aspect);

                            float worldOffsetRight = offsetX * 2f * distance * Mathf.Tan(horizFOV * 0.5f);
                            float worldOffsetUp = offsetY * 2f * distance * Mathf.Tan(vertFOV * 0.5f);

                            Vector3 right = camera.transform.right;
                            Vector3 up = camera.transform.up;

                            camera.transform.position += right * worldOffsetRight + up * worldOffsetUp;
                            camera.transform.LookAt(lookTarget);
                        }

                        // After centering, RECOMPUTE viewport bounds since camera position changed.
                        // Then ensure all corners are within the viewport by backing up if needed.
                        float postCenterMinVY = 1f;
                        float postCenterMaxVY = 0f;
                        float postCenterMinVX = 1f;
                        float postCenterMaxVX = 0f;

                        for (int i = 0; i < corners.Length; i++)
                        {
                            Vector3 vp = camera.WorldToViewportPoint(corners[i]);
                            if (vp.z <= 0f) continue;

                            if (vp.x < postCenterMinVX) postCenterMinVX = vp.x;
                            if (vp.x > postCenterMaxVX) postCenterMaxVX = vp.x;
                            if (vp.y < postCenterMinVY) postCenterMinVY = vp.y;
                            if (vp.y > postCenterMaxVY) postCenterMaxVY = vp.y;
                        }

                        // If the projected bounds exceed the viewport, back up the camera
                        float currentProjectedWidth = postCenterMaxVX - postCenterMinVX;
                        float currentProjectedHeight = postCenterMaxVY - postCenterMinVY;
                        bool clipsHorizontally = postCenterMinVX < 0.01f || postCenterMaxVX > 0.99f;
                        bool clipsVertically = postCenterMinVY < 0.01f || postCenterMaxVY > 0.99f;

                        // Iteratively back up camera until object fits (accounts for perspective distortion on deep objects)
                        for (int iteration = 0; iteration < 3; iteration++)
                        {
                            // Recompute viewport bounds
                            float iterMinVX = 1f, iterMaxVX = 0f, iterMinVY = 1f, iterMaxVY = 0f;
                            for (int j = 0; j < corners.Length; j++)
                            {
                                Vector3 vp = camera.WorldToViewportPoint(corners[j]);
                                if (vp.z <= 0f) continue;
                                if (vp.x < iterMinVX) iterMinVX = vp.x;
                                if (vp.x > iterMaxVX) iterMaxVX = vp.x;
                                if (vp.y < iterMinVY) iterMinVY = vp.y;
                                if (vp.y > iterMaxVY) iterMaxVY = vp.y;
                            }

                            float iterWidth = iterMaxVX - iterMinVX;
                            float iterHeight = iterMaxVY - iterMinVY;
                            bool stillClips = iterMinVX < 0.02f || iterMaxVX > 0.98f || iterMinVY < 0.02f || iterMaxVY > 0.98f;

                            if (!stillClips && iterWidth <= 0.96f && iterHeight <= 0.96f)
                            {
                                break; // Fits within viewport
                            }

                            float targetSize = 0.92f; // Leave 4% margin on each side for safety
                            float scaleForWidth = iterWidth > 0.0001f ? iterWidth / targetSize : 1f;
                            float scaleForHeight = iterHeight > 0.0001f ? iterHeight / targetSize : 1f;
                            float scale = Mathf.Max(scaleForWidth, scaleForHeight);

                            // Add safety margin for perspective distortion on deep objects
                            scale *= 1.05f;

                            if (scale > 1.001f)
                            {
                                Vector3 viewDir = (camera.transform.position - lookTarget).normalized;
                                float currentDistance = Vector3.Distance(camera.transform.position, lookTarget);
                                float newDistance = currentDistance * scale;

                                camera.transform.position = lookTarget + viewDir * newDistance;
                                camera.transform.LookAt(lookTarget);
                            }
                            else
                            {
                                break; // Scale too small to matter
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Loads Unity's built-in humanoid model for previewing humanoid animations.
        /// </summary>
        /// <returns>The humanoid model GameObject, or null if loading failed.</returns>
        private static GameObject GetUnityHumanoidModel()
        {
            GameObject model = EditorGUIUtility.Load("Avatar/DefaultAvatar.fbx") as GameObject;
            if (model == null)
            {
                Debug.LogWarning("[FBX Preview] Could not load Unity's default humanoid model (Avatar/DefaultAvatar.fbx)");
            }
            return model;
        }

        /// <summary>
        /// Loads Unity's built-in generic model for previewing generic (non-humanoid) animations.
        /// </summary>
        /// <returns>The generic model GameObject, or null if loading failed.</returns>
        private static GameObject GetUnityGenericModel()
        {
            GameObject model = EditorGUIUtility.Load("Avatar/DefaultGeneric.fbx") as GameObject;
            if (model == null)
            {
                Debug.LogWarning("[FBX Preview] Could not load Unity's default generic model (Avatar/DefaultGeneric.fbx)");
            }
            return model;
        }

        // Clear existing bone visualization
        private static void ClearBoneVisualization(GameObject instance)
        {
            if (instance == null) return;

            // Find and destroy the BoneVisualization root object
            Transform boneVizTransform = instance.transform.Find("BoneVisualization");
            if (boneVizTransform != null)
            {
                Object.DestroyImmediate(boneVizTransform.gameObject);
            }
        }

        // Visualize skeleton bones for FBX files without renderable geometry
        private enum BoneRegion
        {
            Spine, // Spine, chest, torso, pelvis, hips
            Head, // Head, neck, jaw, eyes
            LeftArm, // Left arm, shoulder, elbow, wrist
            RightArm, // Right arm, shoulder, elbow, wrist
            LeftHand, // Left hand, fingers
            RightHand, // Right hand, fingers
            LeftLeg, // Left leg, thigh, knee, ankle
            RightLeg, // Right leg, thigh, knee, ankle
            LeftFoot, // Left foot, toes
            RightFoot, // Right foot, toes
            Other // Unknown/other bones
        }

        private static BoneRegion GetBoneRegion(string boneName)
        {
            string name = boneName.ToLowerInvariant();

            // Determine left/right side
            bool isLeft = name.Contains("left") || name.Contains("_l_") || name.Contains(".l.") ||
                name.StartsWith("l_") || name.StartsWith("l.") ||
                name.EndsWith("_l") || name.EndsWith(".l") ||
                (name.Contains("_l") && !name.Contains("_lo")) ||
                name.Contains("l ") || name.StartsWith("l ") ||
                System.Text.RegularExpressions.Regex.IsMatch(name, @"\bl\b");

            bool isRight = name.Contains("right") || name.Contains("_r_") || name.Contains(".r.") ||
                name.StartsWith("r_") || name.StartsWith("r.") ||
                name.EndsWith("_r") || name.EndsWith(".r") ||
                (name.Contains("_r") && !name.Contains("_ro")) ||
                name.Contains("r ") || name.StartsWith("r ") ||
                System.Text.RegularExpressions.Regex.IsMatch(name, @"\br\b");

            // Check for specific body parts
            bool isHand = name.Contains("hand") || name.Contains("finger") || name.Contains("thumb") ||
                name.Contains("index") || name.Contains("middle") || name.Contains("ring") ||
                name.Contains("pinky") || name.Contains("palm");

            bool isFoot = name.Contains("foot") || name.Contains("toe") || name.Contains("ball");

            bool isArm = name.Contains("arm") || name.Contains("shoulder") || name.Contains("elbow") ||
                name.Contains("wrist") || name.Contains("clavicle") || name.Contains("upperarm") ||
                name.Contains("forearm") || name.Contains("lowerarm");

            bool isLeg = name.Contains("leg") || name.Contains("thigh") || name.Contains("knee") ||
                name.Contains("ankle") || name.Contains("shin") || name.Contains("calf") ||
                name.Contains("upperleg") || name.Contains("lowerleg") || name.Contains("upleg");

            bool isSpine = name.Contains("spine") || name.Contains("chest") || name.Contains("torso") ||
                name.Contains("pelvis") || name.Contains("hip") || name.Contains("root") ||
                name.Contains("abdomen") || name.Contains("waist");

            bool isHead = name.Contains("head") || name.Contains("neck") || name.Contains("jaw") ||
                name.Contains("eye") || name.Contains("skull") || name.Contains("face") ||
                name.Contains("brow") || name.Contains("nose") || name.Contains("lip") ||
                name.Contains("ear") || name.Contains("tongue") || name.Contains("teeth");

            // Determine region based on body part and side
            if (isHead) return BoneRegion.Head;
            if (isSpine) return BoneRegion.Spine;

            if (isHand)
            {
                if (isLeft) return BoneRegion.LeftHand;
                if (isRight) return BoneRegion.RightHand;
                return BoneRegion.Other;
            }

            if (isFoot)
            {
                if (isLeft) return BoneRegion.LeftFoot;
                if (isRight) return BoneRegion.RightFoot;
                return BoneRegion.Other;
            }

            if (isArm)
            {
                if (isLeft) return BoneRegion.LeftArm;
                if (isRight) return BoneRegion.RightArm;
                return BoneRegion.Other;
            }

            if (isLeg)
            {
                if (isLeft) return BoneRegion.LeftLeg;
                if (isRight) return BoneRegion.RightLeg;
                return BoneRegion.Other;
            }

            // If we have a side but no specific part, try to infer from common patterns
            if (isLeft) return BoneRegion.Other;
            if (isRight) return BoneRegion.Other;

            return BoneRegion.Other;
        }

        private static Color GetBoneRegionColor(BoneRegion region)
        {
            switch (region)
            {
                case BoneRegion.Spine:
                    return new Color(1.0f, 0.7f, 0.3f, 1f); // Warm orange
                case BoneRegion.Head:
                    return new Color(0.7f, 0.5f, 0.9f, 1f); // Purple
                case BoneRegion.LeftArm:
                    return new Color(0.3f, 0.6f, 1.0f, 1f); // Blue
                case BoneRegion.RightArm:
                    return new Color(1.0f, 0.4f, 0.4f, 1f); // Red
                case BoneRegion.LeftHand:
                    return new Color(0.5f, 0.8f, 1.0f, 1f); // Light blue
                case BoneRegion.RightHand:
                    return new Color(1.0f, 0.6f, 0.6f, 1f); // Light red/pink
                case BoneRegion.LeftLeg:
                    return new Color(0.3f, 0.8f, 0.8f, 1f); // Teal/cyan
                case BoneRegion.RightLeg:
                    return new Color(1.0f, 0.5f, 0.7f, 1f); // Pink/magenta
                case BoneRegion.LeftFoot:
                    return new Color(0.5f, 0.9f, 0.9f, 1f); // Light teal
                case BoneRegion.RightFoot:
                    return new Color(1.0f, 0.7f, 0.8f, 1f); // Light pink
                default:
                    return new Color(0.6f, 0.6f, 0.6f, 1f); // Gray for unknown
            }
        }

        private static void VisualizeSkeletonBones(GameObject instance)
        {
            if (instance == null) return;

            // Find all transforms in hierarchy (potential bones)
            Transform[] allTransforms = instance.GetComponentsInChildren<Transform>();
            if (allTransforms.Length <= 1) return; // Need at least 2 transforms to make connections

            // Create materials for each bone region
            Shader unlitShader = Shader.Find("Unlit/Color");
            Dictionary<BoneRegion, Material> regionMaterials = new Dictionary<BoneRegion, Material>();
            foreach (BoneRegion region in System.Enum.GetValues(typeof (BoneRegion)))
            {
                Material mat = new Material(unlitShader);
                mat.color = GetBoneRegionColor(region);
                regionMaterials[region] = mat;
            }

            // Create a parent object to hold all bone visualizations
            GameObject boneVisualizationRoot = new GameObject("BoneVisualization");
            boneVisualizationRoot.transform.SetParent(instance.transform, false);

            // Iterate through transforms and create cone connections between parent and children
            foreach (Transform bone in allTransforms)
            {
                if (bone.parent == null || bone.parent == instance.transform) continue;

                // Create cone from parent to this bone
                Vector3 start = bone.parent.position;
                Vector3 end = bone.position;
                float distance = Vector3.Distance(start, end);

                if (distance < 0.001f) continue; // Skip bones that are at the same position

                // Skip if parent is at the same position as the root (likely an armature container, not a real bone)
                if (Vector3.Distance(bone.parent.position, instance.transform.position) < 0.001f) continue;

                // Determine bone region for coloring
                BoneRegion region = GetBoneRegion(bone.name);

                // Create cone mesh
                GameObject coneObject = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Object.DestroyImmediate(coneObject.GetComponent<Collider>()); // Remove collider

                // Position and orient the cone
                coneObject.transform.position = (start + end) / 2f;
                coneObject.transform.up = (end - start).normalized;

                // Scale: thin cylinder representing bone
                float boneThickness = Mathf.Max(distance * 0.05f, 0.01f);
                coneObject.transform.localScale = new Vector3(boneThickness, distance / 2f, boneThickness);

                // Apply region-specific material
                MeshRenderer renderer = coneObject.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    renderer.sharedMaterial = regionMaterials[region];
                }

                // Parent to visualization root
                coneObject.transform.SetParent(boneVisualizationRoot.transform, true);
            }

            // Create small spheres at bone joints for better visualization
            foreach (Transform bone in allTransforms)
            {
                if (bone == instance.transform) continue; // Skip root

                // Skip transforms at the root position (likely armature containers, not real bones)
                if (Vector3.Distance(bone.position, instance.transform.position) < 0.001f) continue;

                // Determine bone region for coloring
                BoneRegion region = GetBoneRegion(bone.name);

                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Object.DestroyImmediate(sphere.GetComponent<Collider>());

                sphere.transform.position = bone.position;
                sphere.transform.localScale = Vector3.one * 0.03f; // Small sphere

                // Apply region-specific material
                MeshRenderer renderer = sphere.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    renderer.sharedMaterial = regionMaterials[region];
                }

                sphere.transform.SetParent(boneVisualizationRoot.transform, true);
            }
        }

        /// <summary>
        /// Samples an animation clip on a GameObject at a specific time.
        /// For humanoid clips (which store muscle curves, not direct Transform curves),
        /// uses Animator.Play + Animator.Update to properly evaluate through the Mecanim pipeline
        /// and Avatar retargeting. This also handles optimized hierarchies where bone Transforms
        /// are stripped from the GameObject tree.
        /// For non-humanoid clips, falls back to AnimationClip.SampleAnimation() which directly
        /// sets Transform curves on matching GameObjects.
        /// </summary>
        internal static void SampleAnimationPose(GameObject go, AnimationClip clip, float time)
        {
            if (clip.isHumanMotion)
            {
                Animator animator = go.GetComponentInChildren<Animator>();
                if (animator != null && animator.runtimeAnimatorController != null && clip.length > 0)
                {
                    float normalizedTime = Mathf.Clamp01(time / clip.length);
                    animator.Play(clip.name, 0, normalizedTime);
                    animator.Update(0f);
                    return;
                }
            }

            // Fallback for generic/legacy clips or when Animator pipeline is not available
            clip.SampleAnimation(go, time);
        }

        /// <summary>
        /// Select the best animation clip for FBX preview.
        /// Skips T-pose animations and prefers base animations over split variants.
        /// </summary>
        private static AnimationClip SelectBestAnimationClip(AnimationClip[] clips)
        {
            if (clips == null || clips.Length == 0)
            {
                return null;
            }

            // Step 1: Filter out non-animation clips (T-pose, empty/rest actions)
            AnimationClip[] validClips = clips.Where(c =>
            {
                string lower = c.name.ToLowerInvariant();
                if (lower.Contains("tpose")) return false;
                string leafName = c.name;
                int pipeIdx = leafName.LastIndexOf('|');
                if (pipeIdx >= 0) leafName = leafName.Substring(pipeIdx + 1);
                if (leafName.Equals("EmptyAction", StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }).ToArray();

            // If all clips were T-pose, fall back to original array
            if (validClips.Length == 0)
            {
                validClips = clips;
            }

            // Step 2: Build a set of base names (names without underscore suffix)
            HashSet<string> baseNames = new HashSet<string>();
            foreach (AnimationClip clip in validClips)
            {
                int underscoreIndex = clip.name.LastIndexOf('_');
                if (underscoreIndex > 0)
                {
                    string baseName = clip.name.Substring(0, underscoreIndex);
                    baseNames.Add(baseName);
                }
            }

            // Step 3: Find the earliest clip that is either:
            // - A base animation (matches a base name exactly), OR
            // - A clip without underscore that doesn't have split variants
            for (int i = 0; i < validClips.Length; i++)
            {
                AnimationClip clip = validClips[i];

                // Check if this clip name matches a base name (no underscore suffix)
                if (baseNames.Contains(clip.name))
                {
                    // Prefer base animation
                    return clip;
                }

                // Check if this clip has no underscore at all
                if (!clip.name.Contains("_"))
                {
                    // This clip has no underscore and is not a base name,
                    // which means it's a standalone animation - prefer it
                    return clip;
                }
            }

            // Step 4: Fall back to first valid clip if no base animations found
            return validClips[0];
        }

        /// <summary>
        /// Finds the hierarchy-root particle systems in a prefab. A root PS is one whose
        /// parent chain (up to the prefab root) contains no other ParticleSystem component.
        /// When the first PS in GetComponentsInChildren order IS the common ancestor of all
        /// others (the typical case), this returns just that one system — identical to the
        /// previous particleSystems[0] behavior. For branched hierarchies where no single PS
        /// is the ancestor of all siblings, this returns each independent sub-tree root so
        /// that Simulate(withChildren:true) on each one covers the entire hierarchy.
        /// </summary>
        private static List<ParticleSystem> FindRootParticleSystems(ParticleSystem[] allSystems, GameObject prefabRoot)
        {
            if (allSystems.Length == 0) return new List<ParticleSystem>();

            // Fast path: if the first system's GameObject is the prefab root or an ancestor
            // of all others, it already covers everything via withChildren=true.
            ParticleSystem first = allSystems[0];
            bool firstIsAncestorOfAll = true;
            Transform firstTransform = first.transform;
            for (int i = 1; i < allSystems.Length; i++)
            {
                if (!allSystems[i].transform.IsChildOf(firstTransform))
                {
                    firstIsAncestorOfAll = false;
                    break;
                }
            }
            if (firstIsAncestorOfAll)
            {
                return new List<ParticleSystem> {first};
            }

            // Slow path: collect every PS that has no PS-ancestor between itself and prefabRoot.
            HashSet<ParticleSystem> psSet = new HashSet<ParticleSystem>(allSystems);
            List<ParticleSystem> roots = new List<ParticleSystem>();
            foreach (ParticleSystem ps in allSystems)
            {
                bool hasAncestorPS = false;
                Transform t = ps.transform.parent;
                Transform rootTransform = prefabRoot.transform;
                while (t != null && t != rootTransform)
                {
                    ParticleSystem parentPS = t.GetComponent<ParticleSystem>();
                    if (parentPS != null && psSet.Contains(parentPS))
                    {
                        hasAncestorPS = true;
                        break;
                    }
                    t = t.parent;
                }
                if (!hasAncestorPS)
                {
                    roots.Add(ps);
                }
            }
            return roots;
        }

        /// <summary>
        /// Simulates all root-level particle systems using Simulate(withChildren:true).
        /// For typical prefabs where particleSystems[0] is the common ancestor, this behaves
        /// identically to the previous single-root approach. For branched hierarchies (e.g.,
        /// sibling PS branches with no common PS ancestor), each branch root is simulated
        /// independently so all particles appear.
        /// </summary>
        private static void SimulateAllRoots(List<ParticleSystem> rootSystems, float targetTime, bool restart, bool hasTrails)
        {
            if (hasTrails)
            {
                foreach (ParticleSystem root in rootSystems)
                {
                    root.Simulate(0f, true, true, true);
                    root.Play(true);
                }
                float timeStep = 0.016f;
                float currentTime = 0f;
                while (currentTime < targetTime)
                {
                    float deltaTime = Mathf.Min(timeStep, targetTime - currentTime);
                    foreach (ParticleSystem root in rootSystems)
                    {
                        root.Simulate(deltaTime, true, false, false);
                    }
                    currentTime += deltaTime;
                }
            }
            else
            {
                foreach (ParticleSystem root in rootSystems)
                {
                    root.Simulate(targetTime, true, restart, true);
                }
            }

            foreach (ParticleSystem root in rootSystems)
            {
                root.Play(true);
            }
        }
    }
}
