using ImpossibleRobert.Common;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
#if UNITY_EDITOR_WIN && NET_4_6
using System.Drawing;
#else
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp;
#endif
using UnityEngine;

namespace AssetInventory
{
    public sealed class PreviewManager
    {
        private const int MAX_REQUESTS = 50;
        private const int OPEN_REQUESTS = 5;
        public const int DEFAULT_PREVIEW_SIZE = 128;

#if UNITY_EDITOR_WIN && NET_4_6
        public static bool IsErrorShader(Bitmap image) => PreviewValidation.IsErrorShader(image);
        public static bool IsDefaultIcon(Bitmap image) => PreviewValidation.IsDefaultIcon(image);
#else
        public static bool IsErrorShader(Image<Rgba32> image) => PreviewValidation.IsErrorShader(image);
        public static bool IsDefaultIcon(Image<Rgba32> image) => PreviewValidation.IsDefaultIcon(image);
#endif

        public static async Task<bool> Create(AssetInfo info, string sourcePath = null, Action onCreated = null, Action<PreviewRequest> onDone = null, DependencyResultCache cache = null)
        {
            sourcePath = AssetUtils.GetAssetDatabasePath(sourcePath) ?? sourcePath;

            // check if previewable at all
            if (!IsPreviewable(info.FileName, true, info)) return false;

            if (sourcePath == null)
            {
                sourcePath = await Assets.EnsureMaterialized(info);
                if (sourcePath == null)
                {
                    if (!info.HasPreview(true))
                    {
                        DBAdapter.DB.Execute("update AssetFile set PreviewState=? where Id=?", AssetFile.PreviewOptions.Error, info.Id);
                    }
                    onDone?.Invoke(CreateFailureRequest(info, sourcePath, null, "The asset could not be materialized for preview generation."));
                    return false;
                }
            }

            // Determine preview size based on upscaling setting
            int previewSize = AI.Config.upscalePreviews ? AI.Config.upscaleSize : DEFAULT_PREVIEW_SIZE;

            // short-cut for directly accessible small media images to avoid copying these around
            if (info.AssetSource == Asset.Source.Directory &&
                AI.Config.directMediaPreviews &&
                info.Width > 0 && info.Height > 0 &&
                info.Width <= previewSize && info.Height <= previewSize &&
                (info.Type == "png" || info.Type == "jpg" || info.Type == "jpeg"))
            {
                PreviewRequest req = new PreviewRequest {DestinationFile = sourcePath, Id = info.Id, Icon = Texture2D.grayTexture, SourceFile = sourcePath};
                PreviewDatabaseOperations.StorePreviewResult(req);
                onCreated?.Invoke();
                onDone?.Invoke(req);

                return true;
            }

            string previewFile = info.GetPreviewFile(Paths.GetPreviewFolder());
            string animPreviewFile = info.GetPreviewFile(Paths.GetPreviewFolder(), true);

            Texture2D texture = null;
            Texture2D animTexture = null;
            bool directPreview = false;
            int fbxAnimationCount = -1; // Store animation count for FBX files (-1 means not FBX or not detected)
            PreviewRequest previewRequest = null; // Store the request object to preserve FileData

            // Try to handle image preview using fast shortcuts
            if (ImagePreviewHandler.TryCreateImagePreview(info, sourcePath, previewFile, previewSize, onCreated, onDone))
            {
                return true;
            }
            if (AI.IsFileType(info.FileName, AI.AssetGroup.Fonts))
            {
                if (!AI.Config.generateFontPreviews) return false;

                PreviewRequest req = UnityPreviewGenerator.Localize(info, sourcePath, previewFile);
                previewRequest = req;
                texture = FontPreviewGenerator.Create(req.TempFileRel, previewSize);
                directPreview = true;
            }
            else if ((info.Type == "fbx" || info.Type == "obj") && AI.Config.generateFBXPreviews)
            {
                // Ensure dependencies are materialized before localization
                // Required for "Copy From Other Avatar" FBX files that reference other models
                if (info.InProject)
                {
                    sourcePath = info.ProjectPath;
                }
                else
                {
                    string updatedSourcePath = await PreviewAssetManager.EnsureDependenciesAsync(info, sourcePath, cache);
                    if (updatedSourcePath == null)
                    {
                        onDone?.Invoke(CreateFailureRequest(info, sourcePath, previewFile, "Required dependencies could not be materialized for preview generation."));
                        return false;
                    }
                    sourcePath = updatedSourcePath;
                }

                // For FBX with dependencies (like sourceAvatar), use source directly to preserve references
                bool useSourceDirectly = info.InProject || info.Dependencies?.Count > 0;
                previewRequest = UnityPreviewGenerator.Localize(info, sourcePath, previewFile, null, useSourceDirectly);
                if (previewRequest != null)
                {
                    // Load existing FileData from database if available (from indexing)
                    // This avoids re-extraction during preview generation
                    if (!string.IsNullOrEmpty(info.FileData))
                    {
                        previewRequest.FileData = info.FileData;
                    }

                    // Detect animation count from FBX file for preview generation
                    int animationCount = 0;
                    try
                    {
                        UnityEngine.Object[] allAssets = AssetDatabase.LoadAllAssetsAtPath(previewRequest.TempFileRel);
                        AnimationClip[] clips = allAssets
                            .OfType<AnimationClip>()
                            .Where(c => !c.name.StartsWith("__preview__") && !c.empty)
                            .ToArray();

                        animationCount = clips.Length;

                        // Store animation count for later DB update
                        fbxAnimationCount = animationCount;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"Could not detect animations in FBX file '{info.FileName}': {e.Message}");
                    }

                    // Generate static preview (FBX data extraction happens inside if not already present)
                    texture = await CustomPrefabPreviewGenerator.CreateFBX(previewRequest.TempFileRel, previewSize, 1, animationCount, previewRequest);

                    await Task.Yield();

                    // Generate animated preview if enabled and animations exist or 360� rotation is enabled
                    if (texture != null && AI.Config.generateAnimatedFBXPreviews && (animationCount > 0 || AI.Config.generate360FBXPreviews))
                    {
                        int frameCount = AI.Config.animationGrid * AI.Config.animationGrid;
                        animTexture = await CustomPrefabPreviewGenerator.CreateFBX(previewRequest.TempFileRel, previewSize, frameCount, animationCount, previewRequest);
                        await Task.Yield();
                    }

                    directPreview = true;
                }
            }
            else if (info.Type == "anim" && AI.Config.generateAnimPreviews)
            {
                // Check if any dependency is a model or prefab that can be instantiated
                bool hasGameObjectDependency = info.Dependencies != null && info.Dependencies.Any(dep =>
                    AI.IsFileType(dep.FileName, AI.AssetGroup.Models) ||
                    AI.IsFileType(dep.FileName, AI.AssetGroup.Prefabs));

                // Ensure dependencies are materialized when they exist (FBX models, Avatars)
                if (hasGameObjectDependency)
                {
                    if (info.InProject)
                    {
                        sourcePath = info.ProjectPath;
                    }
                    else
                    {
                        string updatedSourcePath = await PreviewAssetManager.EnsureDependenciesAsync(info, sourcePath, cache);
                        if (updatedSourcePath == null)
                        {
                            onDone?.Invoke(CreateFailureRequest(info, sourcePath, previewFile, "Required dependencies could not be materialized for preview generation."));
                            return false;
                        }
                        sourcePath = updatedSourcePath;
                    }
                }
                else if (info.InProject)
                {
                    sourcePath = info.ProjectPath;
                }

                // Standalone .anim files without model dependencies will use Unity's default
                // humanoid/generic model as fallback in ResolveModelForAnimation()
                bool useSourceDirectly = info.InProject || (info.Dependencies != null && info.Dependencies.Count > 0);
                previewRequest = UnityPreviewGenerator.Localize(info, sourcePath, previewFile, null, useSourceDirectly);

                if (previewRequest != null)
                {
                    // For clips without a model dependency, only proceed for humanoid animations.
                    // Non-humanoid clips target custom bone paths that won't match the generic default
                    // model and will produce an empty render. Mark them as not applicable instead.
                    if (!hasGameObjectDependency)
                    {
                        AnimationClip clipCheck = AssetDatabase.LoadAssetAtPath<AnimationClip>(previewRequest.TempFileRel);
                        if (clipCheck == null || !clipCheck.isHumanMotion)
                        {
                            if (!info.HasPreview(true))
                            {
                                info.PreviewState = AssetFile.PreviewOptions.NotApplicable;
                                DBAdapter.DB.Execute("update AssetFile set PreviewState=? where Id=?", AssetFile.PreviewOptions.NotApplicable, info.Id);
                            }
                            onDone?.Invoke(CreateFailureRequest(info, sourcePath, previewFile, "Animation preview requires a compatible model or prefab dependency. Only humanoid animations can be previewed without one."));
                            return false;
                        }
                    }

                    // Load existing FileData from database if available (from indexing)
                    if (!string.IsNullOrEmpty(info.FileData))
                    {
                        previewRequest.FileData = info.FileData;
                    }

                    // Pass dependencies to CreateAnim for model resolution
                    texture = await CustomPrefabPreviewGenerator.CreateAnim(
                        previewRequest.TempFileRel, previewSize, 1, previewRequest, info.Dependencies);

                    await Task.Yield();

                    // Generate animated preview if enabled
                    if (texture != null && AI.Config.generateAnimatedAnimPreviews)
                    {
                        int frameCount = AI.Config.animationGrid * AI.Config.animationGrid;
                        animTexture = await CustomPrefabPreviewGenerator.CreateAnim(
                            previewRequest.TempFileRel, previewSize, frameCount, previewRequest, info.Dependencies);
                        await Task.Yield();
                    }

                    directPreview = true;
                }
            }
#if UNITY_EDITOR_WIN
            else if (AI.IsFileType(info.FileName, AI.AssetGroup.Videos))
            {
                if (!AI.Config.generateVideoPreviews) return false;

                PreviewRequest req = UnityPreviewGenerator.Localize(info, sourcePath, previewFile);
                previewRequest = req;

                // first static
                texture = await VideoPreviewGenerator.Create(req.TempFileRel, previewSize, 1, clip =>
                {
                    info.Width = (int)clip.width;
                    info.Height = (int)clip.height;
                    info.Length = (float)clip.length;
                    DBAdapter.DB.Execute("update AssetFile set Width=?, Height=?, Length=? where Id=?", info.Width, info.Height, info.Length, info.Id);
                });

                // give time for video player cleanup, might result in black textures otherwise when done in quick succession
                await Task.Yield();

                if (texture != null && AI.Config.generateAnimatedVideoPreviews)
                {
                    // now animated
                    animTexture = await VideoPreviewGenerator.Create(req.TempFileRel, previewSize, AI.Config.animationGrid * AI.Config.animationGrid, _ => {});
                    await Task.Yield();
                }

                directPreview = true;
            }
#endif
            else if (AI.IsFileType(info.FileName, AI.AssetGroup.Scenes))
            {
                if (!AI.Config.generateScenePreviews) return false;

                PreviewRequest req = UnityPreviewGenerator.Localize(info, sourcePath, previewFile);
                previewRequest = req;

                if (req != null)
                {
                    // Use ScenePreviewGenerator for scene preview
                    texture = ScenePreviewGenerator.Create(req.TempFileRel, previewSize, req);
                    directPreview = true;

                    if (texture != null)
                    {
                        directPreview = true;
                    }
                }
            }
            else if (AI.IsFileType(info.FileName, AI.AssetGroup.Prefabs) || AI.IsFileType(info.FileName, AI.AssetGroup.Effects))
            {
                // Check if custom pipeline should be used for prefabs/VFX
                bool useCustomPipeline = false;
                bool customPipelineSucceeded = false;
                bool isVFX = AI.IsFileType(info.FileName, AI.AssetGroup.Effects);

                // Ensure dependencies are materialized before localization
                if (info.InProject)
                {
                    sourcePath = info.ProjectPath;
                }
                else
                {
                    string updatedSourcePath = await PreviewAssetManager.EnsureDependenciesAsync(info, sourcePath, cache);
                    if (updatedSourcePath == null)
                    {
                        onDone?.Invoke(CreateFailureRequest(info, sourcePath, previewFile, "Required dependencies could not be materialized for preview generation."));
                        return false;
                    }
                    sourcePath = updatedSourcePath;
                }

                string localizedSourcePath = AssetUtils.GetAssetDatabasePath(sourcePath) ?? sourcePath;
                bool sourceIsProjectAsset = AssetUtils.IsAssetDatabasePath(localizedSourcePath);

                // For VFX, use the materialized source directly when it is already importable.
                // External extracted files still need localization so AssetDatabase can load them.
                bool useSourceDirectly = info.InProject || info.Dependencies?.Count > 0 || (isVFX && sourceIsProjectAsset);
                PreviewRequest req = UnityPreviewGenerator.Localize(info, sourcePath, previewFile, null, useSourceDirectly);
                previewRequest = req;

                if (req != null)
                {
                    GameObject prefab = null;
                    GameObject vfxGameObject = null;
                    string prefabPath = null; // Track the path to pass to Create()

                    // Load differently based on file type
                    if (isVFX)
                    {
                        // Load VFX asset
                        UnityEngine.VFX.VisualEffectAsset vfxAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.VFX.VisualEffectAsset>(req.TempFileRel);

                        if (vfxAsset != null)
                        {
                            // Create a temporary prefab with the VFX so Unity treats it like a proper scene object
                            // Required for VFX shaders to compile correctly
                            vfxGameObject = new GameObject("TempVFXPreview");
                            UnityEngine.VFX.VisualEffect vfxComponent = vfxGameObject.AddComponent<UnityEngine.VFX.VisualEffect>();
                            vfxComponent.visualEffectAsset = vfxAsset;

                            // Save as a temporary prefab in the same folder as the VFX
                            string vfxFolder = Path.GetDirectoryName(req.TempFileRel);
                            string tempPrefabPath = Path.Combine(vfxFolder, "_TempVFXPrefab.prefab");
                            tempPrefabPath = tempPrefabPath.Replace("\\", "/");

                            // Save and reload as prefab
                            PrefabUtility.SaveAsPrefabAsset(vfxGameObject, tempPrefabPath);
                            UnityEngine.Object.DestroyImmediate(vfxGameObject);

                            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(tempPrefabPath);
                            prefabPath = tempPrefabPath; // Use the temporary prefab path for VFX
                        }
                        else
                        {
                            Debug.LogError($"[PreviewManager] Failed to load VFX asset from '{req.TempFileRel}'");
                            req.FailureReason = "The VFX asset could not be loaded for preview generation.";
                        }
                    }
                    else
                    {
                        // Load prefab directly
                        prefab = AssetDatabase.LoadAssetAtPath<GameObject>(req.TempFileRel);
                        prefabPath = req.TempFileRel; // Use the original path for non-VFX
                    }

                    if (prefab != null)
                    {
                        // Check for UI (with or without Canvas), VFX, or Particles ? use custom if enabled
                        bool hasCanvas = prefab.GetComponentInChildren<Canvas>() != null;
                        bool needsCanvas = PrefabPreviewUtilities.NeedsTemporaryCanvas(prefab);
                        bool isUI = hasCanvas || needsCanvas;
                        bool hasVFX = prefab.GetComponentInChildren<UnityEngine.VFX.VisualEffect>() != null;
                        bool hasParticles = prefab.GetComponentInChildren<ParticleSystem>() != null;

                        // Check if respective preview types are enabled
                        if (isUI && AI.Config.generateUIPreviews)
                        {
                            useCustomPipeline = true;
                        }
                        else if (hasVFX && AI.Config.generateVFXPreviews)
                        {
                            useCustomPipeline = true;
                        }
                        else if (hasParticles && AI.Config.generateParticlePreviews)
                        {
                            useCustomPipeline = true;
                        }
                        // Check if 3D custom is enabled
                        else if (AI.Config.generateCustomModelPreviews)
                        {
                            useCustomPipeline = true;
                        }

                        if (useCustomPipeline)
                        {
                            // Determine if animated (VFX or Particles, but NOT UI)
                            bool isAnimated = (hasVFX || hasParticles) && !isUI;

                            // Check if animated preview should be generated based on type
                            bool shouldGenerateAnimated = false;
                            if (hasVFX && AI.Config.generateAnimatedVFXPreviews)
                            {
                                shouldGenerateAnimated = true;
                            }
                            else if (hasParticles && AI.Config.generateAnimatedParticlePreviews)
                            {
                                shouldGenerateAnimated = true;
                            }
                            else if (!isAnimated && !isUI && AI.Config.generateAnimatedModelPreviews)
                            {
                                // Only enable 360 rotation for non-UI, non-animated 3D models
                                shouldGenerateAnimated = true;
                            }

                            bool is360Rotation = AI.Config.generateAnimatedModelPreviews && !isAnimated && !isUI;

                            // Generate static preview first (single frame)
                            // Use the already-loaded GameObject and pass the path
                            texture = await CustomPrefabPreviewGenerator.Create(
                                prefab,
                                previewSize,
                                1,
                                prefabPath,
                                req
                            );

                            await Task.Yield();

                            if (texture == null && string.Equals(req.FailureReason, CustomPrefabPreviewGenerator.NO_VISIBLE_CONTENT_REASON, StringComparison.Ordinal))
                            {
                                if (isVFX || !info.HasPreview(true))
                                {
                                    TryDeletePreviewFile(previewFile);
                                    TryDeletePreviewFile(animPreviewFile);

                                    info.PreviewState = AssetFile.PreviewOptions.Error;
                                    DBAdapter.DB.Execute("update AssetFile set PreviewState=? where Id=?", AssetFile.PreviewOptions.Error, info.Id);
                                }

                                onDone?.Invoke(req);
                                return false;
                            }

                            if (texture != null)
                            {
                                // Verify preview for error shaders (incompatible render pipeline)
                                // Only verify non-image types as images work by default and can lead to false positives
                                string fileType = info.Type.ToLowerInvariant();
                                if (AI.Config.verifyPreviews && !AI.TypeGroups[AI.AssetGroup.Images].Contains(fileType))
                                {
                                    if (IsErrorShader(texture.ToImage()))
                                    {
                                        // Incompatible render pipeline detected - skip preview generation entirely
                                        UnityEngine.Object.DestroyImmediate(texture);

                                        // Clean up in-memory VFX GameObject
                                        if (vfxGameObject != null) UnityEngine.Object.DestroyImmediate(vfxGameObject);

                                        if (AI.Config.LogPreviewCreation)
                                        {
                                            Debug.LogWarning($"Custom preview pipeline generated error shader (incompatible render pipeline) for '{info.FileName}'. Skipping preview generation.");
                                        }

                                        // Mark as error and skip Unity fallback
                                        customPipelineSucceeded = true; // Prevent fallback to Unity
                                        directPreview = true; // Skip Unity preview generation path
                                        MarkFailure(req, "The item is incompatible to the currently used render pipeline.", true);
                                        if (!info.HasPreview(true))
                                        {
                                            info.PreviewState = AssetFile.PreviewOptions.Error;
                                            DBAdapter.DB.Execute("update AssetFile set PreviewState=? where Id=?", AssetFile.PreviewOptions.Error, info.Id);
                                        }
                                        onDone?.Invoke(req);
                                        return false;
                                    }
                                }
                                if (texture != null)
                                {
                                    customPipelineSucceeded = true;
                                    directPreview = true;

                                    // Generate animated/360 rotation previews
                                    // VFX now uses Simulate() method for fast time advancement (no real-time waiting needed)
                                    // Only skip 360 rotation for VFX (not applicable to effects)
                                    if (shouldGenerateAnimated && (isAnimated || (is360Rotation && !isVFX)))
                                    {
                                        // Generate animated/360 rotation version (multi-frame atlas)
                                        int frameCount = AI.Config.animationGrid * AI.Config.animationGrid;
                                        float minimumVisibleDuration = GetMinimumEffectVisibleDuration(hasParticles, hasVFX);
                                        req.AnimatedFrameInterval = minimumVisibleDuration > 0f
                                            ? AnimationPlayer.ResolveFrameInterval(AI.Config.animationSpeed, minimumVisibleDuration, frameCount)
                                            : 0f;

                                        // Use the already-loaded GameObject and pass the path
                                        animTexture = await CustomPrefabPreviewGenerator.Create(
                                            prefab,
                                            previewSize,
                                            frameCount,
                                            prefabPath,
                                            req
                                        );

                                        await Task.Yield();

                                        if (animTexture == null && AI.Config.LogPreviewCreation)
                                        {
                                            Debug.LogWarning($"Failed to generate animated preview for '{info.FileName}', using static preview only.");
                                        }
                                    }
                                    else
                                    {
                                        // Delete any existing animated preview file since we're only creating a static version
                                        // (or if it's a VFX file, which we don't animate)
                                        TryDeletePreviewFile(animPreviewFile);
                                    }
                                }
                            }
                        }

                        // Clean up in-memory VFX GameObject
                        if (vfxGameObject != null) UnityEngine.Object.DestroyImmediate(vfxGameObject);
                    }
                }

                // Fall through to Unity default if custom pipeline not used or failed
                // VFX files have no Unity fallback, so mark as error
                if (!customPipelineSucceeded)
                {
                    if (isVFX)
                    {
                        if (req != null && string.IsNullOrWhiteSpace(req.FailureReason))
                        {
                            req.FailureReason = "The VFX preview pipeline did not produce an image.";
                        }
                        if (!info.HasPreview(true))
                        {
                            info.PreviewState = AssetFile.PreviewOptions.Error;
                            DBAdapter.DB.Execute("update AssetFile set PreviewState=? where Id=?", AssetFile.PreviewOptions.Error, info.Id);
                        }
                        onDone?.Invoke(req ?? CreateFailureRequest(info, sourcePath, previewFile, "The VFX preview pipeline did not produce an image."));
                        return false;
                    }

                    directPreview = false; // Ensure we don't skip Unity default for prefabs
                }
            }
            // Custom Material Preview Pipeline
            else if (AI.IsFileType(info.FileName, AI.AssetGroup.Materials) && AI.Config.generateMaterialPreviews)
            {
                // Ensure material is imported
                if (info.InProject)
                {
                    sourcePath = info.ProjectPath;
                }
                else
                {
                    string updatedSourcePath = await PreviewAssetManager.EnsureDependenciesAsync(info, sourcePath, cache);
                    if (updatedSourcePath == null)
                    {
                        onDone?.Invoke(CreateFailureRequest(info, sourcePath, previewFile, "Required dependencies could not be materialized for preview generation."));
                        return false;
                    }
                    sourcePath = updatedSourcePath;
                }

                PreviewRequest req = UnityPreviewGenerator.Localize(info, sourcePath, previewFile, null, info.InProject || info.Dependencies?.Count > 0);
                previewRequest = req;
                if (req != null)
                {
                    // Generate material preview
                    texture = await CustomMaterialPreviewGenerator.Create(req.TempFileRel, previewSize);

                    await Task.Yield();

                    if (texture != null)
                    {
                        // Verify preview for error shaders (incompatible render pipeline)
                        if (AI.Config.verifyPreviews)
                        {
                            if (IsErrorShader(texture.ToImage()))
                            {
                                UnityEngine.Object.DestroyImmediate(texture);

                                if (AI.Config.LogPreviewCreation)
                                {
                                    Debug.LogWarning($"Custom material preview generated error shader (incompatible render pipeline) for '{info.FileName}'. Falling back to Unity preview.");
                                }

                                texture = null; // Let Unity fallback handle it
                            }
                            else
                            {
                                directPreview = true;
                            }
                        }
                        else
                        {
                            directPreview = true;
                        }
                    }
                }
            }

            if (!directPreview && (AI.IsFileType(info.FileName, AI.AssetGroup.Models) ||
                AI.IsFileType(info.FileName, AI.AssetGroup.Images) ||
                AI.IsFileType(info.FileName, AI.AssetGroup.Prefabs) ||
                AI.IsFileType(info.FileName, AI.AssetGroup.Materials) ||
                AI.IsFileType(info.FileName, AI.AssetGroup.Audio)))
            {
                // potential short-cut: check if already imported
                if (info.InProject)
                {
                    sourcePath = info.ProjectPath;
                }
                else
                {
                    // import through Unity
                    sourcePath = await PreviewAssetManager.EnsureDependenciesAsync(info, sourcePath, cache);
                    if (sourcePath == null)
                    {
                        onDone?.Invoke(CreateFailureRequest(info, sourcePath, previewFile, "Required dependencies could not be materialized for preview generation."));
                        return false;
                    }
                }

                if (!await UnityPreviewGenerator.RegisterPreviewRequest(info, sourcePath, previewFile, req =>
                    {
                        AssetFile af = PreviewDatabaseOperations.StorePreviewResult(req);
                        if (req.Icon != null)
                        {
                            // Delete any existing animated preview file since Unity pipeline doesn't create animated versions
                            TryDeletePreviewFile(animPreviewFile);
                            onCreated?.Invoke();
                        }
                        else if (req.IncompatiblePipeline)
                        {
                            req.FailureReason ??= "The item is incompatible to the currently used render pipeline.";
                            Debug.LogWarning($"Unity did return a pink preview image for '{info.FileName}' due to the currently incompatible render pipeline. Reverting to previous version.");
                        }
                        else if (af.PreviewState != AssetFile.PreviewOptions.Error) // otherwise error already logged
                        {
                            req.FailureReason ??= "Unity did not return a preview image.";
                            if (AI.Config.LogPreviewCreation) Debug.LogWarning($"Unity did not return any preview image for '{info.FileName}'.");
                        }
                        onDone?.Invoke(req);
                    }, info.InProject || info.Dependencies?.Count > 0))
                {
                    if (!info.HasPreview(true))
                    {
                        info.PreviewState = AssetFile.PreviewOptions.Error;
                        DBAdapter.DB.Execute("update AssetFile set PreviewState=? where Id=?", AssetFile.PreviewOptions.Error, info.Id);
                    }
                    onDone?.Invoke(CreateFailureRequest(info, sourcePath, previewFile, "The Unity preview pipeline could not be started."));
                    return false;
                }

                await EnsureProgress();
            }
            if (directPreview)
            {
                if (texture != null)
                {
                    // Embed play icon indicator if animated preview exists and config allows it
                    if (animTexture != null && AI.Config.embedAnimatedPreviewIndicator)
                    {
                        texture = ImageUtils.EmbedPlayIconIndicator(texture);
                    }

                    try
                    {
                        await File.WriteAllBytesAsync(previewFile, texture.EncodeToPNG());
                    }
                    catch (IOException ioEx)
                    {
                        Debug.LogError($"Failed to write preview '{previewFile}'. Disk may be full: {ioEx.Message}");
                        UnityEngine.Object.DestroyImmediate(texture);
                        if (animTexture != null) UnityEngine.Object.DestroyImmediate(animTexture);
                        return false;
                    }

                    // Create or reuse preview request and add animation count if FBX was processed
                    if (previewRequest == null)
                    {
                        previewRequest = new PreviewRequest
                        {
                            DestinationFile = previewFile,
                            Id = info.Id,
                            Icon = Texture2D.grayTexture,
                            SourceFile = sourcePath,
                            AnimationCount = fbxAnimationCount // Will be -1 for non-FBX files
                        };
                    }
                    else
                    {
                        // Reuse existing request (e.g., for FBX with FileData) and update fields
                        previewRequest.DestinationFile = previewFile;
                        previewRequest.Icon = Texture2D.grayTexture;
                        previewRequest.AnimationCount = fbxAnimationCount;
                        previewRequest.FailureReason = null;
                        previewRequest.IncompatiblePipeline = false;
                    }
                    PreviewDatabaseOperations.StorePreviewResult(previewRequest);
                    onCreated?.Invoke();

                    // Clear icon cache so the new preview shows up in Project window
                    UnityIconOverlay.ClearIcon(info.Guid);

                    onDone?.Invoke(previewRequest);

                    if (animTexture != null)
                    {
                        try
                        {
                            // Encode PNG and inject frame grid metadata
                            byte[] pngBytes = animTexture.EncodeToPNG();
                            string frameGridValue = AI.Config.animationGrid.ToString();
                            byte[] pngWithMetadata = ImageUtils.InjectPngMetadata(pngBytes, "AssetInventory:FrameGrid", frameGridValue);
                            if (previewRequest.AnimatedFrameInterval > 0f)
                            {
                                string frameIntervalValue = previewRequest.AnimatedFrameInterval.ToString("0.######", CultureInfo.InvariantCulture);
                                pngWithMetadata = ImageUtils.InjectPngMetadata(pngWithMetadata, "AssetInventory:FrameInterval", frameIntervalValue);
                            }

                            await File.WriteAllBytesAsync(animPreviewFile, pngWithMetadata);
                        }
                        catch (IOException ioEx)
                        {
                            Debug.LogError($"Failed to write animated preview '{animPreviewFile}'. Disk may be full: {ioEx.Message}");
                        }
                        UnityEngine.Object.DestroyImmediate(animTexture);
                    }
                    else
                    {
                        // Delete any existing animated preview file since we're only creating a static version
                        TryDeletePreviewFile(animPreviewFile);
                    }

                    UnityEngine.Object.DestroyImmediate(texture);
                    return true;
                }

                string failureReason = previewRequest?.FailureReason;
                if (string.IsNullOrWhiteSpace(failureReason))
                {
                    failureReason = "Preview generation did not produce an image.";
                }

                PreviewRequest failedRequest = previewRequest != null
                    ? MarkFailure(previewRequest, failureReason, previewRequest.IncompatiblePipeline)
                    : CreateFailureRequest(info, sourcePath, previewFile, failureReason);

                if (!info.HasPreview(true))
                {
                    TryDeletePreviewFile(previewFile);
                    TryDeletePreviewFile(animPreviewFile);

                    info.PreviewState = AssetFile.PreviewOptions.Error;
                    DBAdapter.DB.Execute("update AssetFile set PreviewState=? where Id=?", AssetFile.PreviewOptions.Error, info.Id);
                }

                onDone?.Invoke(failedRequest);
                return false;
            }
            return true;
        }

        public static bool IsPreviewable(string file, bool includeComplex, AssetInfo autoMarkNA = null)
        {
            bool previewable = false;
            if (!file.Contains("__MACOSX"))
            {
                if (includeComplex)
                {
                    previewable = AI.IsFileType(file, AI.AssetGroup.Audio)
                        || AI.IsFileType(file, AI.AssetGroup.Images)
#if UNITY_EDITOR_WIN
                        || (AI.IsFileType(file, AI.AssetGroup.Videos) && AI.Config.generateVideoPreviews)
#endif
                        || AI.IsFileType(file, AI.AssetGroup.Models)
                        || AI.IsFileType(file, AI.AssetGroup.Effects)
                        || (AI.IsFileType(file, AI.AssetGroup.Fonts) && AI.Config.generateFontPreviews)
                        || AI.IsFileType(file, AI.AssetGroup.Prefabs)
                        || AI.IsFileType(file, AI.AssetGroup.Materials, new[] {"physicmaterial", "physicsmaterial"})
                        || (AI.IsFileType(file, AI.AssetGroup.Animations) && AI.Config.generateAnimPreviews)
                        || (AI.IsFileType(file, AI.AssetGroup.Scenes) && AI.Config.generateScenePreviews);
                }
                else
                {
                    previewable = AI.IsFileType(file, AI.AssetGroup.Audio)
                        || AI.IsFileType(file, AI.AssetGroup.Images)
#if UNITY_EDITOR_WIN
                        || (AI.IsFileType(file, AI.AssetGroup.Videos) && AI.Config.generateVideoPreviews)
#endif
                        || (AI.IsFileType(file, AI.AssetGroup.Fonts) && AI.Config.generateFontPreviews);
                }
            }
            if (!previewable && autoMarkNA != null)
            {
                if (!autoMarkNA.HasPreview(true))
                {
                    autoMarkNA.PreviewState = AssetFile.PreviewOptions.NotApplicable;
                    DBAdapter.DB.Execute("update AssetFile set PreviewState=? where Id=?", autoMarkNA.PreviewState, autoMarkNA.Id);
                }
            }

            return previewable;
        }

        private static float GetMinimumEffectVisibleDuration(bool hasParticles, bool hasVFX)
        {
            float duration = 0f;
            if (hasParticles)
            {
                duration = Mathf.Max(duration, AI.Config.cpParticleMinimumVisibleDuration);
            }
            if (hasVFX)
            {
                duration = Mathf.Max(duration, AI.Config.cpVFXMinimumVisibleDuration);
            }
            return Mathf.Max(0f, duration);
        }

        private static async Task EnsureProgress()
        {
            UnityPreviewGenerator.EnsureProgress();
            if (UnityPreviewGenerator.ActiveRequestCount() > MAX_REQUESTS) await UnityPreviewGenerator.ExportPreviews(OPEN_REQUESTS);
        }

        private static PreviewRequest CreateFailureRequest(AssetInfo info, string sourcePath, string destinationFile, string failureReason, bool incompatiblePipeline = false)
        {
            return new PreviewRequest
            {
                Id = info?.Id ?? 0,
                SourceFile = sourcePath,
                DestinationFile = destinationFile,
                FailureReason = failureReason,
                IncompatiblePipeline = incompatiblePipeline
            };
        }

        private static PreviewRequest MarkFailure(PreviewRequest request, string failureReason, bool incompatiblePipeline = false)
        {
            if (request == null) return null;

            request.Icon = null;
            request.FailureReason = failureReason;
            request.IncompatiblePipeline = incompatiblePipeline;
            return request;
        }

        /// <summary>
        /// Safely deletes a preview file only if it resides inside the preview folder.
        /// Prevents accidental deletion of original asset files (e.g. UseOriginal previews).
        /// </summary>
        internal static bool TryDeletePreviewFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;

            string previewFolder = Paths.GetPreviewFolder(createOnDemand: false);
            if (string.IsNullOrEmpty(previewFolder)) return false;

            string fullPath = NormalizePreviewPath(filePath);
            string fullPreviewFolder = NormalizePreviewPath(previewFolder).TrimEnd('/', '\\');
            if (fullPath.Length <= fullPreviewFolder.Length) return false;
            if (!fullPath.StartsWith(fullPreviewFolder, StringComparison.OrdinalIgnoreCase)) return false;
            if (fullPath[fullPreviewFolder.Length] != '/' && fullPath[fullPreviewFolder.Length] != '\\') return false;

            return IOUtils.TryDeleteFile(filePath);
        }

        private static string NormalizePreviewPath(string path)
        {
            return Path.GetFullPath(IOUtils.ToShortPath(path)).Replace("\\", "/");
        }
    }
}
