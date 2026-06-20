using ImpossibleRobert.Common;
using Database;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Unity.EditorCoroutines.Editor;
using UnityEngine;

namespace AssetInventory
{
    /// <summary>
    /// Determines the materialization strategy for preview generation.
    /// </summary>
    public enum MaterializationMode
    {
        /// <summary>Per-file materialization (traditional path). Each file is materialized and imported individually.</summary>
        None,

        /// <summary>
        /// Only dependency-needing files (prefabs, materials, FBX, shaders, etc.) are bulk-imported.
        /// Non-dependency files (images, audio, video) use per-file materialization.
        /// Best when a small fraction of files need dependency scanning (e.g. 91 FBX out of 85k total).
        /// </summary>
        Selective,

        /// <summary>
        /// All files are bulk-imported in one go with a single AssetDatabase.Refresh.
        /// Best when a large fraction of files need dependency scanning.
        /// </summary>
        Full
    }

    public sealed class PreviewPipeline : AssetImporter
    {
        public async Task<int> RecreateScheduledPreviews(List<AssetInfo> assets, List<AssetInfo> allAssets, bool noSRPCheck = false)
        {
            string assetFilter = GetAssetFilter(assets);
            string subAssetFilter = GetAssetFilter(assets, "af2.AssetId");
            string excludeFilter = GetExcludeExtensionFilter();
            string scheduledPreviewSource = GetScheduledPreviewSource(assetFilter);
            string query = $@"
                SELECT *, af.Id as Id 
                FROM AssetFile af
                INNER JOIN Asset ON Asset.Id = af.AssetId 
                WHERE Asset.Exclude = 0
                AND (af.PreviewState=? OR af.PreviewState=?) 
                {assetFilter}
                {excludeFilter}
                AND af.Id IN (
                    SELECT MIN(af2.Id) 
                    FROM {scheduledPreviewSource}
                    WHERE (af2.PreviewState=? OR af2.PreviewState=?)
                    {subAssetFilter}
                    GROUP BY af2.AssetId
                )
                ORDER BY Asset.Id";
            List<AssetInfo> files = DBAdapter.DB.Query<AssetInfo>(query, AssetFile.PreviewOptions.Redo, AssetFile.PreviewOptions.RedoMissing, AssetFile.PreviewOptions.Redo, AssetFile.PreviewOptions.RedoMissing).ToList();

            if (!noSRPCheck)
            {
                // filter out packages not for current render pipeline, for BIRP remove all support packages
                AssetUtils.FilterIncompatibleSRPPackages(files);
            }

            return await RecreatePreviews(files, true, allAssets);
        }

        private static string GetScheduledPreviewSource(string assetFilter)
        {
            bool isGlobalSQLiteSearch = string.IsNullOrEmpty(assetFilter)
                && string.Equals(AI.Config?.databaseType ?? DatabaseFactory.SQLITE, DatabaseFactory.SQLITE, StringComparison.OrdinalIgnoreCase);

            return isGlobalSQLiteSearch ? "AssetFile af2 INDEXED BY AssetFile_PreviewState" : "AssetFile af2";
        }

        public static string GetAssetFilter(List<AssetInfo> assets, string fieldName = "Asset.Id")
        {
            if (assets == null || assets.Count == 0) return string.Empty;
            return $"and {fieldName} in ({string.Join(",", assets.Select(a => a.AssetId))})";
        }

        // Overload for IEnumerable to avoid unnecessary ToList() calls
        public static string GetAssetFilter(IEnumerable<AssetInfo> assets, string fieldName = "Asset.Id")
        {
            if (assets == null) return string.Empty;
            string ids = string.Join(",", assets.Select(a => a.AssetId));
            return string.IsNullOrEmpty(ids) ? string.Empty : $"and {fieldName} in ({ids})";
        }

        /// <summary>
        /// Returns a SQL filter clause to exclude file types from preview generation based on user settings.
        /// </summary>
        /// <param name="fieldName">The field name to filter on (default: af.Type)</param>
        /// <returns>SQL AND clause excluding specified types, or empty string if disabled</returns>
        public static string GetExcludeExtensionFilter(string fieldName = "af.Type")
        {
            if (!AI.Config.excludePreviewExtensions || string.IsNullOrWhiteSpace(AI.Config.excludedPreviewExtensions))
            {
                return string.Empty;
            }

            string[] excludedTypes = AI.ResolveExtensionList(AI.Config.excludedPreviewExtensions);
            if (excludedTypes.Length == 0) return string.Empty;

            return $"AND {fieldName} NOT IN ('{string.Join("','", excludedTypes)}')";
        }

        /// <summary>
        /// Determines the materialization strategy for a batch of files.
        /// Uses a ratio-based approach to find the sweet spot between:
        /// - Full bulk (imports all files, best when most need dependencies)
        /// - Selective bulk (imports only dependency-needing files + their deps, best for
        ///   large packages where few files need dependency scanning, e.g. 91 FBX out of 85k)
        /// - None (per-file materialization, best for small batches)
        /// Threshold is configurable via AI.Config.bulkPreviewThreshold.
        /// </summary>
        /// <param name="files">Files to process for preview generation</param>
        /// <returns>The materialization mode to use</returns>
        private static MaterializationMode DetermineMaterializationMode(List<AssetInfo> files)
        {
            // Check if bulk mode is disabled
            if (AI.Config.bulkPreviewThreshold <= 0) return MaterializationMode.None;

            if (files == null || files.Count < 1 || files.Count < AI.Config.bulkPreviewThreshold) return MaterializationMode.None;

            // usually archives and dirs are less intertwined with dependencies than packages
            if (files[0].AssetSource == Asset.Source.Archive || files[0].AssetSource == Asset.Source.Directory || files[0].AssetSource == Asset.Source.AssetManager)
            {
                return MaterializationMode.None;
            }

            // Count files that need dependency scanning (prefabs, materials, FBX, shaders, etc.)
            int filesWithDependencies = files.Count(f => DependencyAnalysis.NeedsScan(f.Type));

            // Not enough dependency files to justify any bulk mode
            if (filesWithDependencies < AI.Config.bulkPreviewThreshold) return MaterializationMode.None;

            // Calculate the ratio of dependency-needing files to total files
            float dependencyRatio = filesWithDependencies / (float)files.Count;

            // High ratio: most files need dependencies, import everything in one Refresh
            if (dependencyRatio >= 0.3f) return MaterializationMode.Full;

            // Low ratio but enough dependency files: selectively import only the files
            // that need dependency scanning. This avoids importing thousands of images/audio
            // that can be handled without Unity import (e.g. ImagePreviewHandler) or with
            // a simple per-file Localize call.
            return MaterializationMode.Selective;
        }

        public async Task<int> RecreatePreviews(List<AssetInfo> files, bool packageMode, List<AssetInfo> allAssets, bool autoRemoveCache = true, Action<PreviewRequest> onDone = null)
        {
            int created = 0;
            Dictionary<int, List<BackupInfo>> backupState = null;

            // check if previewable at all, do here once in non-package mode as that can reduce the resultset dramatically 
            if (!packageMode) files.RemoveAll(item => !PreviewManager.IsPreviewable(item.FileName, true, item));

            // in case not all items have parents resolved yet, as otherwise sub-packages to be recreated pointing to sub-packages not being recreated cannot resolve downloads 
            Assets.ResolveParents(allAssets, allAssets);

            List<IGrouping<int, AssetInfo>> assetGroups = files
                .GroupBy(info => info.AssetId)
                .OrderByDescending(group => group.Key)
                .ToList();

            MainCount = assetGroups.Count;
            foreach (IGrouping<int, AssetInfo> grouping in assetGroups)
            {
                if (CancellationRequested) break;

                List<AssetInfo> forcedBackupRoots = null;

                List<AssetInfo> infos;
                if (packageMode)
                {
                    // Check package-level SRP compatibility before querying files
                    if (!AssetUtils.IsPackageCompatibleWithCurrentSRP(grouping.First()))
                    {
                        continue; // Skip incompatible package
                    }

                    // in package mode, files are not loaded yet to save memory 
                    string excludeFilter = GetExcludeExtensionFilter();
                    string query = $@"
                        SELECT *, af.Id as Id 
                        FROM AssetFile af
                        INNER JOIN Asset ON Asset.Id = af.AssetId 
                        WHERE Asset.Id = ?
                        AND Asset.Exclude = 0
                        AND (af.PreviewState=? OR af.PreviewState=?)
                        {excludeFilter}";
                    infos = DBAdapter.DB.Query<AssetInfo>(query, grouping.Key, AssetFile.PreviewOptions.Redo, AssetFile.PreviewOptions.RedoMissing).ToList();

                    // check if previewable at all
                    infos.RemoveAll(item => !PreviewManager.IsPreviewable(item.FileName, true, item));
                    if (infos.Count == 0) continue;

                    Assets.ResolveParents(infos, allAssets);
                }
                else
                {
                    infos = grouping.ToList();
                }
                AssetInfo info = infos.First();

                // check state
                Asset asset = info.ToAsset();
                string tempPath = Paths.GetMaterializedAssetPath(asset);
                bool wasCached = Directory.Exists(tempPath);
                bool wasDownloaded = false;

                MainProgress++;
                SetProgress(asset.DisplayName, MainProgress);

                AssetInfo root = info.GetRoot();
                try
                {
                    // check backups before downloading
                    if (!root.IsDownloaded && !info.IsMaterialized)
                    {
                        backupState ??= AssetBackup.GatherState();
                        forcedBackupRoots = ForceBackupVersion(infos, backupState);
                        if (forcedBackupRoots != null)
                        {
                            Debug.Log($"Using backup copy to recreate previews for '{asset}'.");
                        }
                    }

                    // download on demand
                    if (!root.IsDownloaded && !info.IsMaterialized)
                    {
                        if (!AI.Config.downloadPackagesForPreviews)
                        {
                            Debug.Log($"Could not recreate previews for '{asset}' since the package is not downloaded.");
                            continue;
                        }
                        if (root.CurrentSubState == Asset.SubState.Outdated)
                        {
                            Debug.Log($"Cannot download outdated package '{asset}' to recreate previews. Usually such packages can be deleted.");
                            continue;
                        }
                        if (!CanDownload(root))
                        {
                            Debug.Log($"Cannot download package '{asset}' to recreate previews.");
                            continue;
                        }

                        // ensure package is downloaded - use EditorCoroutineUtility to properly execute on main thread
                        bool downloadDone = false;
                        EditorCoroutineUtility.StartCoroutineOwnerless(DownloadAssetWrapper(root, () => downloadDone = true));

                        // Wait for download to complete
                        while (!downloadDone && !CancellationRequested)
                        {
                            await Task.Delay(100);
                        }
                        await Task.Delay(2000); // grace period for decryption etc.

                        if (CancellationRequested) break;

                        root.Refresh();
                        if (!root.IsDownloaded)
                        {
                            Debug.Log($"Could not recreate preview for '{asset}' since the package could not be downloaded.");
                            continue;
                        }
                        wasDownloaded = true;
                    }

                    // perform actual preview recreation
                    created += await RecreatePackagePreviews(infos, onDone);

                    // clean up again
                    if (!wasCached && autoRemoveCache) RemoveWorkFolder(asset, tempPath);
                    if (wasDownloaded)
                    {
                        // remove downloaded package if it was not cached - use EditorCoroutineUtility for proper main thread execution
                        bool removeDone = false;
                        EditorCoroutineUtility.StartCoroutineOwnerless(RemoveDownloadWrapper(root.ToAsset(), () => removeDone = true));

                        // Wait for removal to complete
                        while (!removeDone && !CancellationRequested)
                        {
                            await Task.Delay(100);
                        }
                    }
                }
                finally
                {
                    ClearForcedBackupVersion(forcedBackupRoots);
                }
            }

            return created;
        }

        private static List<AssetInfo> ForceBackupVersion(List<AssetInfo> files, Dictionary<int, List<BackupInfo>> backupState)
        {
            List<AssetInfo> roots = files
                .Select(file => file.GetRoot())
                .Where(root => root != null && !root.IsDownloaded)
                .Distinct()
                .ToList();

            bool anyForced = false;
            foreach (AssetInfo root in roots)
            {
                string version = root.GetVersion(true);
                if (string.IsNullOrWhiteSpace(version)) continue;

                root.ForcedUnityPackageVersion = version;
                if (root.IsDownloaded)
                {
                    anyForced = true;
                }
                else
                {
                    root.ForcedUnityPackageVersion = null;
                }
            }

            return anyForced ? roots : null;
        }

        private static void ClearForcedBackupVersion(List<AssetInfo> roots)
        {
            if (roots == null) return;

            foreach (AssetInfo root in roots)
            {
                root.ForcedUnityPackageVersion = null;
            }
        }

        private IEnumerator DownloadAssetWrapper(AssetInfo info, Action onComplete)
        {
            yield return DownloadAsset(info);
            onComplete?.Invoke();
        }

        private IEnumerator RemoveDownloadWrapper(Asset asset, Action onComplete)
        {
            yield return RemoveDownload(asset);
            onComplete?.Invoke();
        }

        private async Task<int> RecreatePackagePreviews(List<AssetInfo> files, Action<PreviewRequest> onDone = null)
        {
            int created = 0;

            // Reset copy coordinator cache for fresh state without stale entries from previous runs
            CopyCoordinator.Reset();

            UnityPreviewGenerator.Init(files.Count);

            SubCount = files.Count;
            SubProgress = 0;

            // Create cache for dependency memoization - persists across all files in this package
            DependencyResultCache depCache = new DependencyResultCache();

            // Determine materialization strategy based on file composition
            // This dramatically reduces AssetDatabase.Refresh() calls for large batches
            MaterializationMode matMode = DetermineMaterializationMode(files);
            Dictionary<int, string> materializedPaths = null;

            // Check once whether an SRP support package exists for this asset.
            // MaterializePackageFilesAsync uses the same result internally; caching it here
            // avoids a redundant DB query when deciding whether to run the URP converter.
            Asset srpSupportPackage = DependencyAnalysis.FindSRPSupportPackage(files[0].AssetId, warnOnMultiple: false);

            // Progress callback bridges static MaterializePackageFilesAsync to our ActionProgress properties
            Action<string, int, int> onMatProgress = (msg, cur, total) =>
            {
                CurrentSub = msg;
                SubProgress = cur;
                SubCount = total;
            };
            Func<bool> isMatCancelled = () => CancellationRequested;

            if (matMode == MaterializationMode.Full)
            {
                CurrentSub = "Bulk materializing all files...";
                materializedPaths = await PreviewAssetManager.MaterializePackageFilesAsync(
                    files, depCache, false, onMatProgress, isMatCancelled);
            }
            else if (matMode == MaterializationMode.Selective)
            {
                CurrentSub = "Selectively materializing dependency files...";
                materializedPaths = await PreviewAssetManager.MaterializePackageFilesAsync(
                    files, depCache, true, onMatProgress, isMatCancelled);
            }

            // Run Unity's URP converter once after bulk materialization when enabled.
            // This persistently converts materialized assets in the preview work area for maximum fidelity.
            if (AI.Config.useUnityPipelineConverter && srpSupportPackage == null && (matMode == MaterializationMode.Full || matMode == MaterializationMode.Selective))
            {
                CurrentSub = "Converting materials to current render pipeline...";
                await PipelineConverter.RunUnityConverterAsync();
            }

            // Reset sub-progress for the preview generation phase
            SubCount = files.Count;
            SubProgress = 0;

            // Process files in batches for parallel processing
            for (int i = 0; i < files.Count; i += AI.Config.parallelPreviewBatchSize)
            {
                if (CancellationRequested) break;

                // Get current batch
                int batchSize = Math.Min(AI.Config.parallelPreviewBatchSize, files.Count - i);
                List<AssetInfo> batch = files.GetRange(i, batchSize);

                // Process batch in parallel (but still on main thread)
                List<Task> batchTasks = new List<Task>();
                foreach (AssetInfo info in batch)
                {
                    if (CancellationRequested) break;

                    SubProgress++;
                    CurrentSub = info.FileName;

                    // Get pre-computed source path if bulk/selective mode was used
                    string sourcePath = null;
                    if (matMode != MaterializationMode.None && materializedPaths != null && materializedPaths.TryGetValue(info.Id, out string path))
                    {
                        sourcePath = path;
                    }

                    // Create task for this preview - pass cache and optional pre-computed source path
                    Task previewTask = PreviewManager.Create(info, sourcePath, () => created++, onDone, depCache);
                    batchTasks.Add(previewTask);
                }

                // Wait for all previews in this batch to complete
                if (batchTasks.Count > 0)
                {
                    await Task.WhenAll(batchTasks);
                }

                // Let the editor breathe between batches
                if (AI.Config.parallelPreviewBatchSize > 1)
                {
                    await AI.Cooldown.Do();
                    await Task.Yield();
                }
            }
            SubProgress = SubCount; // ensure 100% progress

            CurrentSub = "Exporting Previews...";
            await UnityPreviewGenerator.ExportPreviews();

            CurrentSub = "Cleaning Up...";
            UnityPreviewGenerator.CleanUp();

            // Clear caches after package completion
            depCache.Clear();
            CustomPrefabPreviewGenerator.ClearVFXCache();

            return created;
        }

        public async Task<int> RestorePreviews(List<AssetInfo> assets, List<AssetInfo> allAssets)
        {
            int restored = 0;

            string previewPath = Paths.GetPreviewFolder();
            string assetFilter = GetAssetFilter(assets);
            string query = $"select *, AssetFile.Id as Id from AssetFile inner join Asset on Asset.Id = AssetFile.AssetId where Asset.Exclude = 0 and (Asset.AssetSource = ? or Asset.AssetSource = ?) and AssetFile.PreviewState != ? {assetFilter} order by Asset.Id";
            List<AssetInfo> files = DBAdapter.DB.Query<AssetInfo>(query, Asset.Source.AssetStorePackage, Asset.Source.CustomPackage, AssetFile.PreviewOptions.Provided).ToList();
            Assets.ResolveParents(files, allAssets);

            MainCount = files.Count;
            foreach (AssetInfo info in files)
            {
                MainProgress++;
                SetProgress(info.FileName, MainProgress);

                if (CancellationRequested) break;
                await AI.Cooldown.Do();
                if (MainProgress % 50 == 0) await Task.Yield(); // let editor breath 

                try
                {
                    if (!info.IsDownloaded && !info.IsMaterialized) continue;

                    string animPreviewFile = info.GetPreviewFile(previewPath, true);
                    IOUtils.TryDeleteFile(animPreviewFile);

                    string previewFile = info.GetPreviewFile(previewPath);
                    string sourcePath = await Assets.EnsureMaterialized(info);
                    if (CancellationRequested) break;

                    if (sourcePath == null)
                    {
                        if (info.PreviewState != AssetFile.PreviewOptions.NotApplicable)
                        {
                            info.PreviewState = AssetFile.PreviewOptions.None;
                            DBAdapter.DB.Execute("update AssetFile set PreviewState=? where Id=?", info.PreviewState, info.Id);
                        }
                        continue;
                    }

                    string originalPreviewFile = PreviewAssetManager.DerivePreviewFile(sourcePath);
                    if (!File.Exists(originalPreviewFile))
                    {
                        if (info.PreviewState != AssetFile.PreviewOptions.NotApplicable)
                        {
                            info.PreviewState = AssetFile.PreviewOptions.None;
                            DBAdapter.DB.Execute("update AssetFile set PreviewState=? where Id=?", info.PreviewState, info.Id);
                        }
                        continue;
                    }

                    if (CancellationRequested) break;

                    bool copied = await IOUtils.TryCopyFile(originalPreviewFile, previewFile, true);
                    if (!copied)
                    {
                        Debug.LogError($"Failed to restore preview for '{info.FileName}' (Asset {info.AssetId}, File {info.Id}) from '{originalPreviewFile}' to '{previewFile}'.");
                        continue;
                    }

                    info.PreviewState = AssetFile.PreviewOptions.Provided;
                    info.Hue = -1f;
                    DBAdapter.DB.Execute("update AssetFile set PreviewState=?, Hue=? where Id=?", info.PreviewState, info.Hue, info.Id);

                    restored++;
                }
                catch (Exception e)
                {
                    Debug.LogError($"Failed to restore preview for '{info.FileName}' (Asset {info.AssetId}, File {info.Id}): {e.Message}");
                }
            }

            return restored;
        }
    }
}
