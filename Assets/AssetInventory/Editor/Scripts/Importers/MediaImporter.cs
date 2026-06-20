using Database;
using ImpossibleRobert.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    public sealed class MediaImporter : AssetImporter
    {
        private const double BREAK_INTERVAL = 0.5;

        /// <summary>
        /// Holds pre-gathered file metadata from the parallel I/O phase.
        /// </summary>
        private class FileWorkItem
        {
            public int Index;
            public string File;
            public AssetFile AssetFile;
            public string Type;
            public long Size;
        }

        /// <summary>
        /// Holds data needed for the deferred preview creation pass.
        /// </summary>
        private class PreviewWorkItem
        {
            public AssetInfo Info;
            public string SourcePath;
        }

        public async Task Index(FolderSpec spec, Asset attachedAsset = null, bool storeRelativePath = false, bool actAsSubImporter = false, bool skipSubPackages = false)
        {
            if (string.IsNullOrEmpty(spec.location)) return;

            string fullLocation = spec.GetLocation(true).Replace("\\", "/");
            if (!Directory.Exists(fullLocation)) return;

            // First Level / Second Level Mode - Parent Coordinator Path
            if (spec.attachToPackage && (spec.packageMode == 1 || spec.packageMode == 2))
            {
                string[] excludedDirs = StringUtils.Split(spec.excludedDirectories, new[] {';', ','});

                // Get first-level subdirectories (used directly for mode 1, as base for mode 2)
                IEnumerable<string> targetDirectories = Directory.GetDirectories(fullLocation)
                    .Where(subdir =>
                    {
                        string relPath = subdir.Substring(fullLocation.Length + 1).Replace("\\", "/");
                        return !IsIgnoredPath(relPath, true) && !IsExcludedDirectory(relPath, excludedDirs, false);
                    });

                // For Second Level mode, get subdirectories of first-level directories
                if (spec.packageMode == 2)
                {
                    targetDirectories = targetDirectories
                        .SelectMany(firstLevel => Directory.GetDirectories(firstLevel)
                            .Where(secondLevel =>
                            {
                                string relPath = secondLevel.Substring(fullLocation.Length + 1).Replace("\\", "/");
                                return !IsIgnoredPath(relPath, true) && !IsExcludedDirectory(relPath, excludedDirs, false);
                            }));
                }

                string[] directories = targetDirectories.ToArray();

                MainCount = directories.Length;

                for (int i = 0; i < directories.Length; i++)
                {
                    if (CancellationRequested) break;

                    string directory = directories[i].Replace("\\", "/");
                    string folderName = Path.GetFileName(directory);
                    SetProgress(folderName, i + 1);

                    // Create a new FolderSpec that inherits all settings from the parent spec
                    FolderSpec subFolderSpec = new FolderSpec(spec);
                    subFolderSpec.location = directory;
                    subFolderSpec.packageMode = 0; // Child specs use Root Folder mode

                    // Create a new MediaImporter instance for the subfolder
                    await AI.Actions.RunWithProgress<MediaImporter>(
                        ActionHandler.ACTION_MEDIA_FOLDERS_INDEX,
                        "Updating media folder index",
                        imp => imp.Index(subFolderSpec, null, storeRelativePath, false, skipSubPackages));
                }

                return; // Skip root-level file processing
            }

            // Root Folder Mode - Existing Behavior
            List<string> searchPatterns = new List<string>();
            List<AI.AssetGroup> types = new List<AI.AssetGroup>();
            switch (spec.scanFor)
            {
                case 0:
                    types.AddRange(new[] {AI.AssetGroup.Audio, AI.AssetGroup.Images, AI.AssetGroup.Models});
                    break;

                case 1:
                    searchPatterns.Add("*.*");
                    break;

                case 3:
                    types.Add(AI.AssetGroup.Audio);
                    break;

                case 4:
                    types.Add(AI.AssetGroup.Images);
                    break;

                case 5:
                    types.Add(AI.AssetGroup.Models);
                    break;

                case 7:
                    if (!string.IsNullOrWhiteSpace(spec.pattern)) searchPatterns.AddRange(spec.pattern.Split(';'));
                    break;
            }

            if (attachedAsset == null)
            {
                if (spec.attachToPackage)
                {
                    attachedAsset = DBAdapter.DB.Table<Asset>().Where(a => a.SafeName == spec.location).OrderByDescending(a => a.Id).FirstOrDefault();
                    if (attachedAsset == null)
                    {
                        attachedAsset = new Asset();
                        attachedAsset.SafeName = fullLocation;
                        attachedAsset.SetLocation(fullLocation);
                        attachedAsset.DisplayName = Path.GetFileNameWithoutExtension(fullLocation);
                        attachedAsset.AssetSource = Asset.Source.Directory;
                        Persist(attachedAsset);
                    }
                }
                else
                {
                    // use generic catch-all package
                    attachedAsset = DBAdapter.DB.Find<Asset>(a => a.SafeName == Asset.NONE);
                    if (attachedAsset == null)
                    {
                        attachedAsset = Asset.GetNoAsset();
                        Persist(attachedAsset);
                    }
                }
            }

            // load existing for orphan checking and caching 
            List<string> fileTypes = new List<string>();
            types.ForEach(t => fileTypes.AddRange(AI.TypeGroups[t]));

            ITableQuery<AssetFile> existingQuery = DBAdapter.DB.Table<AssetFile>();
            if (fileTypes.Count > 0) existingQuery = existingQuery.Where(af => fileTypes.Contains(af.Type));
            if (actAsSubImporter || attachedAsset != null)
            {
                existingQuery = existingQuery.Where(af => af.AssetId == attachedAsset.Id);
            }
            else
            {
                existingQuery = existingQuery.Where(af => af.SourcePath.StartsWith(spec.location));
            }
            List<AssetFile> existing = existingQuery.ToList();

            // clean up existing
            if (spec.removeOrphans)
            {
                DBAdapter.DB.RunInTransaction(() =>
                {
                    foreach (AssetFile file in existing)
                    {
                        string sourcePath = file.GetSourcePath(true);
                        if (actAsSubImporter && attachedAsset != null) sourcePath = Path.Combine(spec.location, sourcePath);

                        if (!File.Exists(sourcePath) || IsIgnoredPath(file.Path, true))
                        {
                            Debug.Log($"Removing orphaned entry from index: {file.SourcePath}");

                            Assets.ForgetAssetFile(file);
                        }
                    }
                });
            }

            bool treatAsUnityProject = spec.detectUnityProjects && AssetUtils.IsUnityProject(fullLocation);

            // scan for new files
            string[] excludedExtensions = StringUtils.Split(spec.excludedExtensions, new[] {';', ','});
            string[] excludedDirectories = StringUtils.Split(spec.excludedDirectories, new[] {';', ','});
            string[] excludedPreviewExtensions = AI.ResolveExtensionList(AI.Config.excludedPreviewExtensions);

            types.ForEach(t => searchPatterns.AddRange(AI.TypeGroups[t].Select(ext => $"*.{ext}")));

            // Detect network drive and adjust enumeration strategy
            bool isNetworkDrive = IOUtils.IsNetworkDrive(fullLocation);
            string scanPath = treatAsUnityProject ? Path.Combine(fullLocation, "Assets") : fullLocation;

            if (isNetworkDrive)
            {
                CurrentMain = "Scanning network drive (this may take a while for large folders)...";
            }

            string[] files = IOUtils.GetFiles(scanPath, searchPatterns, SearchOption.AllDirectories, allowParallel: !isNetworkDrive)
                .Where(file =>
                {
                    string type = IOUtils.GetExtensionWithoutDot(file).ToLowerInvariant();
                    return type != "meta" && !excludedExtensions.Contains(type) && !IsExcludedDirectory(file, excludedDirectories);
                })
                .ToArray();
            int fileCount = files.Length;

            // cache
            int specLength = fullLocation.Length + 1;
            Dictionary<string, List<AssetFile>> guidDict = ToGuidDict(existing);
            Dictionary<(string, int), AssetFile> pathIdDict = ToPathIdDict(existing);

            // do actual indexing
            double nextBreak = 0;
            List<AssetFile> subPackages = new List<AssetFile>();

            // Phase 1: Parallel I/O pre-gathering
            // Gather file metadata (size, GUID, existing lookup, skip decisions) off the main thread.
            // All operations here are thread-safe: FileInfo, File.Exists, ExtractGuidFromFile,
            // IsIgnoredPath, Paths.MakeRelative, dictionary reads on guidDict/pathIdDict.
            MainCount = fileCount;
            CurrentMain = "Gathering file metadata...";

            long totalSize = 0;
            ConcurrentBag<FileWorkItem> workItems = new ConcurrentBag<FileWorkItem>();

            int assetId = attachedAsset.Id;
            Asset.State currentState = attachedAsset.CurrentState;
            bool checkSize = spec.checkSize;

            await Task.Run(() =>
            {
                Parallel.ForEach(
                    Enumerable.Range(0, files.Length),
                    new ParallelOptions {MaxDegreeOfParallelism = Environment.ProcessorCount},
                    i =>
                    {
                        string file = files[i];
                        string relPath = file.Substring(specLength);
                        if (IsIgnoredPath(relPath, true)) return;

                        AssetFile af = new AssetFile();
                        af.AssetId = assetId;
                        af.SetSourcePath(storeRelativePath ? relPath : Paths.MakeRelative(file));
                        af.SetPath(actAsSubImporter ? relPath : af.SourcePath);

                        string metaFile = $"{file}.meta";
                        if (File.Exists(metaFile)) af.Guid = AssetUtils.ExtractGuidFromFile(metaFile);

                        AssetFile existingAf = Fetch(af, guidDict, pathIdDict);

                        // Early skip: already indexed and size check disabled
                        if (existingAf != null && !checkSize)
                        {
                            if (currentState != Asset.State.SubInProcess || (!existingAf.IsUnityPackage() && !existingAf.IsArchive()))
                            {
                                Interlocked.Add(ref totalSize, existingAf.Size);
                                return;
                            }
                        }

                        if (!File.Exists(file)) return;

                        string type = IOUtils.GetExtensionWithoutDot(file).ToLowerInvariant();

                        long size;
                        try
                        {
                            FileInfo fileInfo = new FileInfo(file);
                            fileInfo.Refresh();
                            size = fileInfo.Length;
                        }
                        catch
                        {
                            return; // can't stat file, skip
                        }

                        // Check size-based skip for existing files
                        if (existingAf != null)
                        {
                            if (currentState != Asset.State.SubInProcess || (!existingAf.IsUnityPackage() && !existingAf.IsArchive()))
                            {
                                if (existingAf.Size == size)
                                {
                                    Interlocked.Add(ref totalSize, size);
                                    return;
                                }
                            }

                            // carry over path changes to existing record
                            existingAf.SetSourcePath(af.SourcePath);
                            existingAf.SetPath(af.Path);
                            if (!string.IsNullOrWhiteSpace(af.Guid)) existingAf.Guid = af.Guid;

                            af = existingAf;
                        }

                        Interlocked.Add(ref totalSize, size);

                        workItems.Add(new FileWorkItem
                        {
                            Index = i,
                            File = file,
                            AssetFile = af,
                            Type = type,
                            Size = size
                        });
                    });
            });

            // Sort by original index to maintain deterministic processing order
            List<FileWorkItem> sortedItems = workItems.OrderBy(w => w.Index).ToList();

            // Phase 2: Sequential main-thread indexing
            // ProcessMediaAttributes (audio via UnityWebRequest for unsupported formats),
            // progress updates, and DB writes must all run on the main thread.
            // Previews are deferred to Phase 3 so all files are indexed first (fixing
            // dependency resolution for previews that reference other files in this folder).
            MainCount = sortedItems.Count;
            int processedCount = 0;
            List<PreviewWorkItem> previewItems = new List<PreviewWorkItem>();

            for (int i = 0; i < sortedItems.Count; i++)
            {
                if (CancellationRequested) break;
                if (EditorApplication.timeSinceStartup > nextBreak)
                {
                    nextBreak = EditorApplication.timeSinceStartup + BREAK_INTERVAL;
                    await Task.Yield(); // let editor breathe
                    await AI.Cooldown.Do();
                }

                FileWorkItem item = sortedItems[i];
                processedCount++;
                SetProgress(item.File, processedCount);

                try
                {
                    CurrentMain = item.File + " (" + EditorUtility.FormatBytes(item.Size) + ")";
                    if (processedCount % 50 == 0) await Task.Yield();
                    AI.MemoryObserver.Do(item.Size);

                    item.AssetFile.FileName = Path.GetFileName(item.AssetFile.SourcePath);
                    item.AssetFile.Size = item.Size;
                    item.AssetFile.Type = item.Type;
                    if (AI.Config.gatherExtendedMetadata)
                    {
                        await ProcessMediaAttributes(item.File, item.AssetFile, attachedAsset);
                    }
                    Persist(item.AssetFile);

                    if (item.AssetFile.IsUnityPackage() || item.AssetFile.IsArchive()) subPackages.Add(item.AssetFile);
                }
                catch (Exception e)
                {
                    Debug.LogError($"File '{item.File}' could not be indexed: {e.Message}");
                }

                // Collect previewable items for the deferred preview pass
                if (spec.createPreviews && PreviewManager.IsPreviewable(item.AssetFile.FileName, false))
                {
                    if (!AI.Config.excludePreviewExtensions || !excludedPreviewExtensions.Contains(item.Type))
                    {
                        previewItems.Add(new PreviewWorkItem
                        {
                            Info = new AssetInfo().CopyFrom(attachedAsset, item.AssetFile),
                            SourcePath = item.File
                        });
                    }
                }
            }

            // Phase 3: Batched parallel preview creation
            // All files are now indexed so dependency resolution can find any file in this folder.
            if (spec.createPreviews && previewItems.Count > 0 && !CancellationRequested)
            {
                UnityPreviewGenerator.Init(previewItems.Count);
                DependencyResultCache depCache = new DependencyResultCache();

                MainCount = previewItems.Count;
                MainProgress = 0;
                CurrentMain = "Creating previews...";

                int batchSize = Math.Max(1, AI.Config.parallelPreviewBatchSize);
                for (int i = 0; i < previewItems.Count; i += batchSize)
                {
                    if (CancellationRequested) break;

                    int currentBatchSize = Math.Min(batchSize, previewItems.Count - i);
                    List<Task> batchTasks = new List<Task>();

                    for (int j = 0; j < currentBatchSize; j++)
                    {
                        if (CancellationRequested) break;

                        PreviewWorkItem pItem = previewItems[i + j];
                        MainProgress = i + j + 1;
                        CurrentMain = pItem.Info.FileName;

                        batchTasks.Add(PreviewManager.Create(pItem.Info, pItem.SourcePath, cache: depCache));
                    }

                    if (batchTasks.Count > 0)
                    {
                        try
                        {
                            await Task.WhenAll(batchTasks);
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"Preview batch failed: {e.Message}");
                        }
                    }

                    // Let the editor breathe between batches
                    if (batchSize > 1)
                    {
                        await AI.Cooldown.Do();
                        await Task.Yield();
                    }
                }

                CurrentMain = "Finalizing preview images";
                await UnityPreviewGenerator.ExportPreviews();
                UnityPreviewGenerator.CleanUp();

                depCache.Clear();
                CustomPrefabPreviewGenerator.ClearVFXCache();
            }

            // Register first valid preview icon as package icon for media folders
            if (spec.createPreviews && attachedAsset.SafeName != Asset.NONE && attachedAsset.AssetSource == Asset.Source.Directory)
            {
                string packageIconPath = attachedAsset.GetPreviewFile(Paths.GetPreviewFolder(), validate: true);
                if (string.IsNullOrEmpty(packageIconPath))
                {
                    // Find first valid preview from AssetFile objects
                    List<AssetFile> filesWithPreviews = DBAdapter.DB.Table<AssetFile>()
                        .Where(af => af.AssetId == attachedAsset.Id &&
                            (af.PreviewState == AssetFile.PreviewOptions.Provided ||
                                af.PreviewState == AssetFile.PreviewOptions.Custom ||
                                af.PreviewState == AssetFile.PreviewOptions.UseOriginal))
                        .OrderBy(af => af.Id)
                        .Take(1)
                        .ToList();

                    if (filesWithPreviews.Count > 0)
                    {
                        AssetFile firstPreviewFile = filesWithPreviews[0];
                        string sourcePreviewPath = firstPreviewFile.GetPreviewFile(Paths.GetPreviewFolder());
                        string targetPackageIconPath = attachedAsset.GetPreviewFile(Paths.GetPreviewFolder(), validate: false);

                        if (!string.IsNullOrEmpty(sourcePreviewPath) && !string.IsNullOrEmpty(targetPackageIconPath) && File.Exists(sourcePreviewPath))
                        {
                            try
                            {
                                string targetDir = Path.GetDirectoryName(targetPackageIconPath);
                                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                                {
                                    Directory.CreateDirectory(targetDir);
                                }

                                File.Copy(sourcePreviewPath, targetPackageIconPath, true);
                            }
                            catch (Exception e)
                            {
                                Debug.LogError($"Could not copy preview '{sourcePreviewPath}' to package icon '{targetPackageIconPath}': {e.Message}");
                            }
                        }
                    }
                }
            }

            // Create AssetMedia entries from evenly-spaced previews for media folders
            if (spec.createPreviews && attachedAsset.SafeName != Asset.NONE && attachedAsset.AssetSource == Asset.Source.Directory && AI.Config.directoryPackageMediaCount > 0)
            {
                string previewFolder = Paths.GetPreviewFolder();

                // Get count of available previews
                int previewCount = DBAdapter.DB.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM AssetFile WHERE AssetId = ? AND (PreviewState = ? OR PreviewState = ? OR PreviewState = ?)",
                    attachedAsset.Id,
                    AssetFile.PreviewOptions.Provided,
                    AssetFile.PreviewOptions.Custom,
                    AssetFile.PreviewOptions.UseOriginal
                );

                if (previewCount > 0)
                {
                    int mediaCount = AI.Config.directoryPackageMediaCount;
                    List<int> indices = new List<int>();

                    // Calculate evenly-spaced indices
                    if (mediaCount == 1)
                    {
                        indices.Add(0);
                    }
                    else if (previewCount <= mediaCount)
                    {
                        // Select all available
                        for (int i = 0; i < previewCount; i++)
                        {
                            indices.Add(i);
                        }
                    }
                    else
                    {
                        // Calculate step and select evenly-spaced indices
                        double step = (double)(previewCount - 1) / (mediaCount - 1);
                        for (int i = 0; i < mediaCount; i++)
                        {
                            indices.Add((int)Math.Floor(i * step));
                        }
                        // Ensure last index is the last file
                        if (indices[indices.Count - 1] != previewCount - 1)
                        {
                            indices[indices.Count - 1] = previewCount - 1;
                        }
                    }

                    // Get existing AssetMedia entries for this asset
                    List<AssetMedia> existingMedia = DBAdapter.DB.Query<AssetMedia>(
                        "SELECT * FROM AssetMedia WHERE AssetId = ? AND Type = ?",
                        attachedAsset.Id,
                        "screenshot"
                    ).ToList();

                    // If count differs, remove all and recreate
                    bool shouldRecreate = existingMedia.Count != indices.Count;

                    if (shouldRecreate)
                    {
                        // Delete all existing entries
                        foreach (AssetMedia media in existingMedia)
                        {
                            DBAdapter.DB.Delete<AssetMedia>(media.Id);
                        }
                    }

                    // Query and process each selected preview
                    for (int order = 0; order < indices.Count; order++)
                    {
                        int index = indices[order];

                        // Query only this specific file using LIMIT/OFFSET
                        List<AssetFile> selectedFiles = DBAdapter.DB.Query<AssetFile>(
                            "SELECT * FROM AssetFile WHERE AssetId = ? AND (PreviewState = ? OR PreviewState = ? OR PreviewState = ?) ORDER BY Id LIMIT 1 OFFSET ?",
                            attachedAsset.Id,
                            AssetFile.PreviewOptions.Provided,
                            AssetFile.PreviewOptions.Custom,
                            AssetFile.PreviewOptions.UseOriginal,
                            index
                        );

                        if (selectedFiles.Count == 0) continue;

                        AssetFile selectedFile = selectedFiles[0];
                        string previewPath = selectedFile.GetPreviewFile(previewFolder);

                        if (string.IsNullOrEmpty(previewPath) || !File.Exists(previewPath))
                        {
                            continue;
                        }

                        // Get dimensions before normalizing path (ImageUtils may need the actual file path)
                        int width = selectedFile.Width;
                        int height = selectedFile.Height;
                        if (width <= 0 || height <= 0)
                        {
                            Tuple<int, int> dimensions = ImageUtils.GetDimensions(previewPath);
                            if (dimensions != null && dimensions.Item1 > 0 && dimensions.Item2 > 0)
                            {
                                width = dimensions.Item1;
                                height = dimensions.Item2;
                            }
                        }

                        // Normalize path to short path with forward slashes (remove long path prefix if present)
                        previewPath = IOUtils.ToShortPath(previewPath);

                        if (shouldRecreate)
                        {
                            // Create new entry
                            AssetMedia newMedia = new AssetMedia
                            {
                                AssetId = attachedAsset.Id,
                                Type = "screenshot",
                                Order = order,
                                Url = previewPath,
                                ThumbnailUrl = previewPath,
                                Width = width,
                                Height = height,
                                WebpUrl = null
                            };
                            DBAdapter.DB.Insert(newMedia);
                        }
                        else
                        {
                            // Try to find existing entry by order
                            AssetMedia existingEntry = existingMedia.FirstOrDefault(m => m.Order == order);
                            if (existingEntry != null)
                            {
                                // Update existing entry
                                bool needsUpdate = false;
                                if (existingEntry.Url != previewPath)
                                {
                                    existingEntry.Url = previewPath;
                                    existingEntry.ThumbnailUrl = previewPath;
                                    needsUpdate = true;
                                }
                                if (existingEntry.Width != width && width > 0)
                                {
                                    existingEntry.Width = width;
                                    needsUpdate = true;
                                }
                                if (existingEntry.Height != height && height > 0)
                                {
                                    existingEntry.Height = height;
                                    needsUpdate = true;
                                }
                                if (needsUpdate)
                                {
                                    DBAdapter.DB.Update(existingEntry);
                                }
                            }
                            else
                            {
                                // Create new entry if order doesn't match
                                AssetMedia newMedia = new AssetMedia
                                {
                                    AssetId = attachedAsset.Id,
                                    Type = "screenshot",
                                    Order = order,
                                    Url = previewPath,
                                    ThumbnailUrl = previewPath,
                                    Width = width,
                                    Height = height,
                                    WebpUrl = null
                                };
                                DBAdapter.DB.Insert(newMedia);
                            }
                        }
                    }
                }
                else
                {
                    // No previews available, but still cleanup orphaned entries
                    string previewFolderPath = previewFolder.Replace("\\", "/");
                    List<AssetMedia> orphanedMedia = DBAdapter.DB.Query<AssetMedia>(
                            "SELECT * FROM AssetMedia WHERE AssetId = ? AND Type = ?",
                            attachedAsset.Id,
                            "screenshot"
                        ).Where(m => !string.IsNullOrEmpty(m.Url) &&
                            m.Url.StartsWith(previewFolderPath, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    foreach (AssetMedia orphaned in orphanedMedia)
                    {
                        DBAdapter.DB.Delete<AssetMedia>(orphaned.Id);
                    }
                }
            }

            if (attachedAsset.SafeName != Asset.NONE)
            {
                // update date
                attachedAsset = Fetch(attachedAsset);

                if (!CancellationRequested)
                {
                    attachedAsset.CurrentState = Asset.State.Done;
                }

                if (attachedAsset.AssetSource != Asset.Source.Archive)
                {
                    attachedAsset.LastRelease = DateTime.Now;
                    attachedAsset.PackageSize = totalSize;
                }

                // update location of attached asset to reflect current spec
                // but not for children as that would put extracted path into location
                if (!actAsSubImporter) attachedAsset.SetLocation(fullLocation);

                Persist(attachedAsset);

                if (!CancellationRequested) ApplyPackageTags(spec, attachedAsset);

                if (!CancellationRequested && !skipSubPackages) await Assets.ProcessSubPackages(attachedAsset, subPackages);
            }
        }
    }
}