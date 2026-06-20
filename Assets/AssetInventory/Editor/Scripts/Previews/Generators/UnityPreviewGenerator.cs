using ImpossibleRobert.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AssetInventory
{
    public static class UnityPreviewGenerator
    {
        public const string PREVIEW_FOLDER = "_AssetInventoryPreviewsTemp";

        private const int MIN_PREVIEW_CACHE_SIZE = 200;
        private const int MAX_PREVIEW_CACHE_SIZE = 1000;
        private const float PREVIEW_TIMEOUT = 25f;
        private const int BREAK_INTERVAL = 100;
        private static readonly List<PreviewRequest> _requests = new List<PreviewRequest>();
        private static readonly object _requestsLock = new object();

        public static void Init(int expectedFileCount)
        {
            int cacheSize = Mathf.Clamp(expectedFileCount + 100, MIN_PREVIEW_CACHE_SIZE, MAX_PREVIEW_CACHE_SIZE);
            AssetPreview.SetPreviewTextureCacheSize(cacheSize);
        }

        public static int ActiveRequestCount()
        {
            lock (_requestsLock)
            {
                return _requests.Count;
            }
        }

        public static string GetPreviewWorkFolder()
        {
            string targetDir = Path.Combine(Application.dataPath, PREVIEW_FOLDER);
            Directory.CreateDirectory(targetDir);

            return targetDir;
        }

        public static async Task<bool> RegisterPreviewRequest(AssetInfo info, string sourceFile, string previewDestination, Action<PreviewRequest> onDone, bool useSourceDirectly = false)
        {
            PreviewRequest request = Localize(info, sourceFile, previewDestination, onDone, useSourceDirectly);
            if (request == null) return false;

            // trigger creation, fetch later as it takes a while
            request.Obj = AssetDatabase.LoadAssetAtPath<Object>(request.TempFileRel);

            // When multiple previews are generated in parallel, concurrent imports can invalidate
            // this asset's import state between CopyTo finishing and LoadAssetAtPath being called here.
            // Re-import to update the in-memory database, then retry. ImportAsset alone is sufficient
            // for single files - Refresh() would scan the entire project causing major slowdowns.
            if (request.Obj == null && File.Exists(request.TempFile))
            {
                AssetDatabase.ImportAsset(request.TempFileRel, ImportAssetOptions.ForceSynchronousImport);
                request.Obj = AssetDatabase.LoadAssetAtPath<Object>(request.TempFileRel);

                if (request.Obj == null)
                {
                    // Some asset types need a full editor frame update for their import to finalize,
                    // especially when concurrent tasks are also importing/refreshing.
                    await Task.Yield();
                    AssetDatabase.ImportAsset(request.TempFileRel, ImportAssetOptions.ForceSynchronousImport);
                    request.Obj = AssetDatabase.LoadAssetAtPath<Object>(request.TempFileRel);
                }
            }

            if (request.Obj != null)
            {
                request.TimeStarted = Time.realtimeSinceStartup;
                AssetPreview.GetAssetPreview(request.Obj);
                lock (_requestsLock)
                {
                    _requests.Add(request);
                }
            }
            else
            {
                Debug.LogError($"Queuing preview request failed for: {sourceFile}");
                return false;
            }

            return true;
        }

        public static PreviewRequest Localize(AssetInfo info, string sourceFile, string previewDestination, Action<PreviewRequest> onDone = null, bool useSourceDirectly = false)
        {
            PreviewRequest request = new PreviewRequest
            {
                Id = info.Id, SourceFile = sourceFile, DestinationFile = previewDestination, OnDone = onDone
            };

            // ensure target folder exists for subsequent write operations
            string resultDir = Path.GetDirectoryName(request.DestinationFile);
            Directory.CreateDirectory(resultDir);

            bool needsImport = false; // Track whether Localize performed a file copy requiring import

            if (useSourceDirectly)
            {
                string relativeSourceFile = AssetUtils.GetAssetDatabasePath(sourceFile) ?? sourceFile;

                if (AssetUtils.IsAssetDatabasePath(relativeSourceFile))
                {
                    request.TempFile = AssetUtils.AddProjectRoot(relativeSourceFile);
                }
                else
                {
                    request.TempFile = sourceFile;
                }
            }
            else
            {
                string relativeSourceFile = AssetUtils.GetAssetDatabasePath(sourceFile) ?? sourceFile;

                if (AssetUtils.IsAssetDatabasePath(relativeSourceFile))
                {
                    request.TempFile = AssetUtils.AddProjectRoot(relativeSourceFile);
                }
                else
                {
                    // Check if a file with this GUID already exists (using helper from Assets class)
                    // If it does, reuse it to avoid GUID collisions
                    // Use validateLoadable=true to ensure we don't reuse assets with import errors
                    string existingPath = Assets.GetExistingAssetPathForGuid(info.Guid, previewMode: true, validateLoadable: true);
                    if (!string.IsNullOrEmpty(existingPath))
                    {
                        // Reuse existing file with same GUID
                        request.TempFile = AssetUtils.AddProjectRoot(existingPath);
                    }
                    else
                    {
                        // No existing file found, proceed with normal copy
                        string targetDir = GetPreviewWorkFolder();
                        request.TempFile = Path.Combine(targetDir, info.Id + Path.GetExtension(sourceFile));
                        try
                        {
                            File.Copy(sourceFile, request.TempFile, true);
                            string sourceFileMeta = sourceFile + ".meta";
                            if (File.Exists(sourceFileMeta)) File.Copy(sourceFileMeta, request.TempFile + ".meta", true);
                            needsImport = true; // File was copied, needs ImportAsset + Refresh
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"File is inaccessible. Preview could not be generated for '{sourceFile}': {e.Message}");
                            return null;
                        }
                    }
                }
            }

            request.TempFileRel = IOUtils.MakeProjectRelative(request.TempFile);
            if (!AssetUtils.IsAssetDatabasePath(request.TempFileRel))
            {
                Debug.LogWarning($"Preview asset path is outside the current project and cannot be loaded by AssetDatabase. Source: {sourceFile}, localized: {request.TempFile}");
                return null;
            }
            
            // Check file existence using absolute path for reliability
            if (!File.Exists(request.TempFile))
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
            if (!File.Exists(request.TempFile))
            {
                Debug.LogWarning($"Preview could not be generated for: {sourceFile}");
                return null;
            }

            // Only import when Localize actually copied a new file.
            // For bulk-materialized or dependency-resolved assets the file is already imported;
            // re-importing would be redundant and can disrupt ongoing shader compilation.
            // Note: ImportAsset alone is sufficient for single files - Refresh() is not needed
            // and would scan the entire project, causing severe slowdowns for audio previews.
            if (needsImport)
            {
                AssetDatabase.ImportAsset(request.TempFileRel, ImportAssetOptions.ForceSynchronousImport);
            }

            return request;
        }

        public static void EnsureProgress()
        {
            // Unity is so buggy when creating previews, you need to hammer the GetAssetPreview call
            lock (_requestsLock)
            {
                for (int i = _requests.Count - 1; i >= 0; i--)
                {
                    PreviewRequest req = _requests[i];
                    if (req.Icon != null) continue;

                    req.Icon = AssetPreview.GetAssetPreview(req.Obj);
                    if (req.Icon == null && UnityEditorCompat.IsLoadingPreview(req.Obj))
                    {
                        AssetPreview.GetAssetPreview(req.Obj);
                    }
                }
            }
        }

        public static async Task ExportPreviews(int limit = 0)
        {
            int yieldCounter = 0;

            while (_requests.Count > limit)
            {
                // Only yield every few iterations to reduce overhead
                yieldCounter++;
                if (yieldCounter >= 5)
                {
                    await Task.Yield();
                    yieldCounter = 0;
                }

                List<PreviewRequest> requestsToCleanup = new List<PreviewRequest>();
                int processedThisIteration = 0;

                lock (_requestsLock)
                {
                    for (int i = _requests.Count - 1; i >= 0; i--)
                    {
                        PreviewRequest req = _requests[i];
                        if (req.Icon == null)
                        {
                            req.Icon = AssetPreview.GetAssetPreview(req.Obj);
                            if (req.Icon == null)
                            {
                                // Only check loading state and timeout, reduce redundant GetAssetPreview calls
                                if (UnityEditorCompat.IsLoadingPreview(req.Obj))
                                {
                                    if (Time.realtimeSinceStartup - req.TimeStarted < PREVIEW_TIMEOUT) continue;
                                }
                                else if (Time.realtimeSinceStartup - req.TimeStarted < AI.Config.minPreviewWait)
                                {
                                    continue;
                                }
                            }
                        }

                        // still will not return something for all assets
                        if (req.Icon != null && req.Icon.isReadable)
                        {
                            // only verify non-image types as images work by default and can lead to false positives
                            string fileType = IOUtils.GetExtensionWithoutDot(req.TempFile).ToLowerInvariant();
                            if (AI.Config.verifyPreviews && !AI.TypeGroups[AI.AssetGroup.Images].Contains(fileType))
                            {
                                if (PreviewManager.IsErrorShader(req.Icon.ToImage()))
                                {
                                    req.Icon = null;
                                    req.IncompatiblePipeline = true;
                                }
                            }
                            byte[] bytes = req.Icon?.EncodeToPNG();
                            if (bytes != null)
                            {
                                // using await async variant will result in req.Icon getting set to Null in some cases for yet unknown reasons
                                try
                                {
                                    File.WriteAllBytes(req.DestinationFile, bytes);
                                }
                                catch (IOException ioEx)
                                {
                                    Debug.LogError($"Failed to write preview for '{req.SourceFile}'. Disk may be full: {ioEx.Message}");
                                    req.Icon = null; // Mark as failed
                                }
                            }
                        }
                        req.OnDone?.Invoke(req);

                        // Ensure file is actually within the preview folder to avoid deleting reused files from elsewhere in the project
                        if (req.TempFile.Contains(PREVIEW_FOLDER, StringComparison.OrdinalIgnoreCase))
                        {
                            requestsToCleanup.Add(req);
                        }

                        _requests.RemoveAt(i);
                        processedThisIteration++;
                        if (processedThisIteration >= BREAK_INTERVAL) break; // let editor breathe
                    }
                }

                // Handle cleanup outside the lock.
                // Delete files directly from the filesystem instead of using AssetDatabase.DeleteAsset.
                // DeleteAsset triggers Unity to reimport every still-alive prefab that references the
                // deleted file, producing "Missing Nested Prefab" errors for each one. Since the
                // entire preview temp folder is cleaned up properly by CleanUp() at the end (which
                // does a single DeleteAsset on the whole folder + Refresh), individual file deletions
                // during export don't need to go through the AssetDatabase at all.
                if (requestsToCleanup.Count > 0)
                {
                    CopyCoordinator.InvalidateFolder(PREVIEW_FOLDER);

                    foreach (PreviewRequest req in requestsToCleanup)
                    {
                        await IOUtils.DeleteFileOrDirectory(req.TempFile);
                        await IOUtils.DeleteFileOrDirectory(req.TempFile + ".meta");
                    }
                }

                // If nothing was processed, yield to allow Unity to generate previews
                if (processedThisIteration == 0)
                {
                    await Task.Yield();
                    yieldCounter = 0;
                }
            }
        }

        public static void CleanUp()
        {
            lock (_requestsLock)
            {
                _requests.Clear();
            }

            // Invalidate all cache entries for the preview folder
            CopyCoordinator.InvalidateFolder(PREVIEW_FOLDER);

            string targetDir = Path.Combine(Application.dataPath, PREVIEW_FOLDER);
            if (!Directory.Exists(targetDir)) return;

            try
            {
                if (!AssetDatabase.DeleteAsset($"Assets/{PREVIEW_FOLDER}"))
                {
                    Directory.Delete(targetDir, true);
                    FileUtil.DeleteFileOrDirectory(targetDir + ".meta");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Could not remove temporary preview folder '{targetDir}'. Please do so manually: {e.Message}");
            }

            AssetDatabase.Refresh();
        }
    }

    public sealed class PreviewRequest
    {
        public int Id;
        public string SourceFile;
        public string TempFile;
        public string TempFileRel;
        public string DestinationFile;
        public Object Obj;
        public Action<PreviewRequest> OnDone;

        // runtime properties
        public float TimeStarted;
        public Texture2D Icon;
        public bool IncompatiblePipeline;
        public int AnimationCount; // For FBX files: number of animations detected during preview generation
        public string FileData; // Generic field for file-specific data (JSON)
        public string FailureReason;
        public float AnimatedFrameInterval; // Optional per-preview playback interval for animated sprite sheets
    }
}
