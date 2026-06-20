using ImpossibleRobert.Common;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    public static class Assets
    {
        private const string PARTIAL_INDICATOR = "ai-partial.info";
        private const int MAX_DROPDOWN_ITEMS = 25;
        private static readonly string[] ConversionExtensions = {"mat", "fbx"};

        // Thread-safe tracking of ongoing extractions to prevent race conditions
        private static readonly Dictionary<int, Task<string>> _ongoingExtractions = new Dictionary<int, Task<string>>();
        private static readonly object _extractionLock = new object();

        // Extraction queue state
        private static Queue<Asset> _extractionQueue = new Queue<Asset>();
        private static Tuple<Asset, Task> _currentExtraction;
        private static int _extractionProgress;

        /// <summary>
        /// Should be called from EditorApplication.update.
        /// </summary>
        internal static void ProcessExtractionQueue()
        {
            if (_extractionQueue.Count > 0)
            {
                if (_extractionProgress == 0) _extractionProgress = MetaProgress.Start("Package Extraction");
                if (_currentExtraction == null || _currentExtraction.Item2.IsCompleted)
                {
                    Asset next = _extractionQueue.Dequeue();
                    MetaProgress.Report(_extractionProgress, 1, _extractionQueue.Count, next.DisplayName);

                    Task task = EnsureMaterialized(next);
                    _currentExtraction = new Tuple<Asset, Task>(next, task);
                }
            }
            else if (_extractionProgress > 0)
            {
                MetaProgress.Remove(_extractionProgress);
                _extractionProgress = 0;
            }
        }

        /// <summary>
        /// Enqueues an asset for extraction.
        /// </summary>
        internal static void EnqueueExtraction(Asset asset)
        {
            _extractionQueue.Enqueue(asset);
        }

        /// <summary>
        /// Clears all extraction state including ongoing extractions and queue.
        /// Call this when switching database backends to prevent stale asset ID references.
        /// </summary>
        internal static void ClearExtractionState()
        {
            lock (_extractionLock)
            {
                _ongoingExtractions.Clear();
            }
            _extractionQueue.Clear();
            _currentExtraction = null;
            if (_extractionProgress > 0)
            {
                MetaProgress.Remove(_extractionProgress);
                _extractionProgress = 0;
            }
        }

        /// <summary>
        /// Extracts an asset from its archive to the materialized cache folder.
        /// </summary>
        public static async Task<string> Extract(Asset asset, AssetFile assetFile = null, bool fileOnly = false, CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrEmpty(asset.GetLocation(true))) return null;

            // make sure parents are extracted first
            string archivePath = IOUtils.ToLongPath(await asset.GetLocation(true, true));
            if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath))
            {
                if (asset.ParentId <= 0)
                {
                    Debug.LogError($"Asset has vanished since last refresh and cannot be indexed: {archivePath}");

                    // reflect new state
                    asset.SetLocation(null);
                    DBAdapter.DB.Execute("update Asset set Location=null where Id=?", asset.Id);
                }
                return null;
            }

            string tempPath = Paths.GetMaterializedAssetPath(asset);
            string indicator = Path.Combine(tempPath, PARTIAL_INDICATOR);

            // Check available disk space before extraction
            long freeSpace = IOUtils.GetFreeSpace(tempPath);
            long required = asset.PackageSize * 5; // Conservative estimate (5x compression ratio)
            if (freeSpace >= 0 && freeSpace < required)
            {
                Debug.LogError($"Cannot extract '{asset}': Insufficient disk space. " +
                    $"Estimated need: {StringUtils.FormatBytes(required)}, " +
                    $"Available: {StringUtils.FormatBytes(freeSpace)}");
                return null;
            }

            // don't extract again if already done and version is known
            if (!string.IsNullOrWhiteSpace(asset.GetSafeVersion()) && Directory.Exists(tempPath) && !File.Exists(indicator)) return tempPath;

            // Create a unique key for this extraction to prevent race conditions
            int extractionKey = asset.Id;

            // Check if this extraction is already in progress and register our intent
            Task<string> ongoingTask;
            TaskCompletionSource<string> taskCompletionSource = null;
            lock (_extractionLock)
            {
                if (_ongoingExtractions.TryGetValue(extractionKey, out ongoingTask))
                {
                    // Another thread is already extracting this asset
                }
                else
                {
                    // Register our intent to extract by creating a placeholder task
                    taskCompletionSource = new TaskCompletionSource<string>();
                    _ongoingExtractions[extractionKey] = taskCompletionSource.Task;
                }
            }

            // If we found an ongoing task, wait for it outside the lock
            if (ongoingTask != null)
            {
                return await ongoingTask;
            }

            // Create the extraction task and complete our placeholder
            Task<string> extractionTask = Task.Run(async () =>
            {
                try
                {
                    // delete existing cache if interested in whole bundle to make sure everything is there
                    if (assetFile == null || !fileOnly || asset.KeepExtracted) // if only asset file but asset should be kept extracted, then treat as full package
                    {
                        int retries = 0;
                        while (retries < 5 && Directory.Exists(tempPath))
                        {
                            try
                            {
                                await Task.Run(() => Directory.Delete(tempPath, true));
                                break;
                            }
                            catch (Exception)
                            {
                                retries++;
                                await Task.Delay(500);
                            }
                        }
                        if (Directory.Exists(tempPath)) Debug.LogWarning($"Could not remove temporary directory: {tempPath}");

                        try
                        {
                            if (asset.AssetSource == Asset.Source.Archive)
                            {
                                if (!await Task.Run(() => CompressionUtil.ExtractArchive(archivePath, tempPath, ct)))
                                {
                                    // stop here when archive could not be extracted (e.g. path too long, canceled) as otherwise files get removed from index
                                    return null;
                                }
                            }
                            else
                            {
                                // special handling for Tar as that will throw null errors with SharpCompress
                                await Task.Run(() => CompressionUtil.ExtractGz(archivePath, tempPath, ct));
                            }

                            // safety delay in case this is a network drive which needs some time to unlock all files
                            await Task.Delay(100);
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"Could not extract archive '{archivePath}' due to errors. Index results will be partial: {e.Message}");
                            return null;
                        }
                        AI.RunCacheLimiter();

                        return Directory.Exists(tempPath) ? tempPath : null;
                    }

                    // single file only
                    string targetPath = Path.Combine(Paths.GetMaterializedAssetPath(asset), assetFile.GetSourcePath(true));
                    if (File.Exists(targetPath)) return targetPath;

                    try
                    {
                        if (asset.AssetSource == Asset.Source.Archive)
                        {
                            // TODO: switch to single file
                            await Task.Run(() => CompressionUtil.ExtractArchive(archivePath, tempPath, ct));
                        }
                        else
                        {
                            // special handling for Tar as that will throw null errors with SharpCompress
                            await Task.Run(() => CompressionUtil.ExtractGzFile(archivePath, assetFile.GetSourcePath(true), tempPath, ct));
                            if (!File.Exists(indicator) && Directory.Exists(tempPath))
                            {
                                await File.WriteAllTextAsync(indicator, DateTime.Now.ToString(CultureInfo.InvariantCulture), ct);
                            }
                        }

                        // safety delay in case this is a network drive which needs some time to unlock all files
                        await Task.Delay(100);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"Could not extract archive '{archivePath}' due to errors: {e.Message}");

                        // Clean up partial extraction to avoid wasting disk space
                        try
                        {
                            if (Directory.Exists(tempPath))
                            {
                                Directory.Delete(tempPath, true);
                            }
                        }
                        catch (Exception cleanupEx)
                        {
                            Debug.LogWarning($"Could not clean up partial extraction at '{tempPath}': {cleanupEx.Message}. Manual cleanup may be required.");
                        }

                        return null;
                    }
                    AI.RunCacheLimiter();

                    return File.Exists(targetPath) ? targetPath : null;
                }
                finally
                {
                    // Clean up the extraction tracking when done
                    lock (_extractionLock)
                    {
                        _ongoingExtractions.Remove(extractionKey);
                    }
                }
            });

            // Complete the placeholder task with the result
            try
            {
                string result = await extractionTask;
                taskCompletionSource.SetResult(result);
                return result;
            }
            catch (Exception ex)
            {
                taskCompletionSource.SetException(ex);
                throw;
            }
        }

        public static bool IsMaterialized(Asset asset, AssetFile assetFile = null)
        {
            // check if currently being extracted
            if (_ongoingExtractions.ContainsKey(asset.Id)) return false;

            if (asset.AssetSource == Asset.Source.Directory || asset.AssetSource == Asset.Source.RegistryPackage)
            {
                if (assetFile != null) return File.Exists(assetFile.GetSourcePath(true));
                return Directory.Exists(asset.GetLocation(true));
            }

            string assetPath = Paths.GetMaterializedAssetPath(asset);
            if (asset.AssetSource == Asset.Source.AssetManager)
            {
                if (assetFile == null) return false;
                return Directory.Exists(Path.Combine(assetPath, assetFile.Guid));
            }

            if (assetFile != null) return File.Exists(Path.Combine(assetPath, assetFile.GetSourcePath(true)));

            string indicator = Path.Combine(assetPath, PARTIAL_INDICATOR);
            return Directory.Exists(assetPath) && !File.Exists(indicator);
        }

        public static async Task<string> EnsureMaterialized(AssetInfo info, bool fileOnly = false, CancellationToken ct = default(CancellationToken))
        {
            string targetPath = await EnsureMaterialized(info.ToAsset(), info, fileOnly, ct);
            info.IsMaterialized = IsMaterialized(info.ToAsset(), info);
            return targetPath;
        }

        public static async Task<string> EnsureMaterialized(Asset asset, AssetFile assetFile = null, bool fileOnly = false, CancellationToken ct = default(CancellationToken))
        {
            if (asset.AssetSource == Asset.Source.Directory || asset.AssetSource == Asset.Source.RegistryPackage)
            {
                return File.Exists(assetFile.GetSourcePath(true)) ? assetFile.GetSourcePath(true) : null;
            }

            string targetPath;
            if (asset.AssetSource == Asset.Source.AssetManager)
            {
                if (assetFile == null) return null;

                targetPath = Path.Combine(Paths.GetMaterializedAssetPath(asset), assetFile.Guid);
                if (!Directory.Exists(targetPath))
                {
#if USE_ASSET_MANAGER && USE_CLOUD_IDENTITY
                    CloudAssetManagement cam = await AI.GetCloudAssetManagement();

                    List<string> files = await cam.FetchAssetFromRemote(asset, assetFile, targetPath);
                    if (files == null || files.Count == 0) return null;
                    AI.RunCacheLimiter();
#else
                    return null;
#endif
                }

                // special handling for single files
                List<string> allFiles = await Task.Run(() => Directory.GetFiles(targetPath, "*.*", SearchOption.AllDirectories).ToList());
                if (allFiles.Count == 1) return allFiles[0];

                return targetPath;
            }

            targetPath = Paths.GetMaterializedAssetPath(asset);
            if (_ongoingExtractions.TryGetValue(asset.Id, out Task<string> process)) await process;
            if (!Directory.Exists(targetPath) || File.Exists(Path.Combine(targetPath, PARTIAL_INDICATOR)))
            {
                // ensure parent hierarchy is extracted first
                string archivePath = IOUtils.ToLongPath(await asset.GetLocation(true, true));
                if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath)) return null;

                await Task.Run(() => Extract(asset, assetFile, fileOnly, ct));
                if (!Directory.Exists(targetPath)) return null;
            }

            // race condition protection
            if (_ongoingExtractions.TryGetValue(asset.Id, out Task<string> process2)) await process2;

            if (assetFile != null)
            {
                string sourcePath = Path.Combine(Paths.GetMaterializedAssetPath(asset), assetFile.GetSourcePath(true));
                if (!File.Exists(sourcePath))
                {
                    // file is most likely not contained in package anymore
                    Debug.LogError($"File '{assetFile.FileName}' is not contained in this version of the package '{asset}' anymore. Reindexing might solve this.");

                    // remove from index
                    if (AI.Config.removeUnresolveableDBFiles)
                    {
                        Debug.LogError($"Removing from index: {assetFile.FileName}");

                        DBAdapter.DB.Execute("delete from AssetFile where Id=?", assetFile.Id);
                        assetFile.Id = 0;
                    }

                    // mark asset for reindexing
                    if (AI.Config.markUnresolveableForReindexing)
                    {
                        ForgetPackage(new AssetInfo(asset));
                    }

                    return null;
                }

                // use file directly, no need to transform
                if (asset.AssetSource == Asset.Source.Archive) return sourcePath;

                // Unity packages have special format
                targetPath = Path.Combine(Path.GetDirectoryName(sourcePath), "Content", Path.GetFileName(assetFile.GetPath(true)));
                try
                {
                    if (!File.Exists(targetPath))
                    {
                        string directoryName = Path.GetDirectoryName(targetPath);
                        Directory.CreateDirectory(directoryName);
                        File.Copy(sourcePath, targetPath, true);
                    }

                    string sourceMetaPath = sourcePath + ".meta";
                    string targetMetaPath = targetPath + ".meta";

                    if (!File.Exists(targetMetaPath))
                    {
                        // Handle YAML .meta file (newer packages use asset.meta)
                        if (File.Exists(sourceMetaPath))
                        {
                            File.Copy(sourceMetaPath, targetMetaPath, true);
                        }
                        else
                        {
                            // Binary metaData file (older packages use metaData without extension) currently not supported
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"Could not extract file. Most likely the target device ran out of space: {e.Message}");
                    return null;
                }
            }

            return targetPath;
        }

        public static List<AssetInfo> Load()
        {
            string indexedQuery = "SELECT af.AssetId as AssetId, Count(*) as FileCount, Sum(af.Size) as UncompressedSize from AssetFile af group by af.AssetId";
            Dictionary<int, AssetInfo> indexedResult = DBAdapter.DB.Query<AssetInfo>(indexedQuery).ToDictionary(a => a.AssetId);

            string allQuery = "SELECT *, Id as AssetId from Asset order by SafeName COLLATE NOCASE";
            List<AssetInfo> result = DBAdapter.DB.Query<AssetInfo>(allQuery);

            // sqlite does not support "right join", therefore merge two queries manually 
            result.ForEach(asset =>
            {
                if (indexedResult.TryGetValue(asset.Id, out AssetInfo match))
                {
                    asset.FileCount = match.FileCount;
                    asset.UncompressedSize = match.UncompressedSize;
                }
            });

            InitAssets(result);

            return result;
        }

        internal static void InitAssets(List<AssetInfo> result)
        {
            ResolveParents(result, result);
            AI.GetObserver().SetAll(result);
        }

        internal static void ResolveParents(List<AssetInfo> assets, List<AssetInfo> allAssets)
        {
            if (assets == null || allAssets == null) return;

            Dictionary<int, AssetInfo> assetDict = allAssets
                .Where(asset => asset != null)
                .GroupBy(asset => asset.AssetId)
                .ToDictionary(group => group.Key, group => group.First());

            ResolveParents(assets, assetDict);
        }

        internal static void ResolveParents(List<AssetInfo> assets, Dictionary<int, AssetInfo> assetDict)
        {
            if (assets == null || assetDict == null) return;

            foreach (AssetInfo asset in assets)
            {
                if (asset == null) continue;

                // copy over additional metadata from allAssets (mostly file count which enables other features)
                if (asset.FileCount == 0 && assetDict.TryGetValue(asset.AssetId, out AssetInfo fullInfo))
                {
                    asset.FileCount = fullInfo.FileCount;
                    asset.UncompressedSize = fullInfo.UncompressedSize;
                }

                if (asset.ParentId > 0 && asset.ParentInfo == null)
                {
                    if (assetDict.TryGetValue(asset.ParentId, out AssetInfo parentInfo))
                    {
                        asset.ParentInfo = parentInfo;
                        if (asset.IsPackage()) parentInfo.AddChildInfo(asset);
                    }
                }
            }
        }

        internal static void ResolveChildren(AssetInfo info, List<AssetInfo> allAssets)
        {
            ResolveChildren(new List<AssetInfo> {info}, allAssets);
        }

        internal static void ResolveChildren(List<AssetInfo> assets, List<AssetInfo> allAssets)
        {
            if (allAssets == null) return;

            foreach (AssetInfo asset in assets)
            {
                List<AssetInfo> children = allAssets.Where(a => a.ParentId == asset.AssetId).ToList();
                asset.ChildInfos = children.Count > 0 ? children : null;
            }
        }

        /// <summary>
        /// Extracts asset names for dropdown menus.
        /// </summary>
        internal static string[] ExtractAssetNames(IEnumerable<AssetInfo> assets, bool includeIdForDuplicates)
        {
            bool intoSubmenu = AI.Config.groupLists && assets.Count(a => a.FileCount > 0) > MAX_DROPDOWN_ITEMS;
            List<string> result = new List<string> {"-all-"};
            List<AssetEntry> assetEntries = new List<AssetEntry>();

            foreach (AssetInfo asset in assets)
            {
                if (asset.FileCount > 0 && !asset.Exclude)
                {
                    // Use display name when IDs are included
                    string name = includeIdForDuplicates ? asset.GetDisplayName().Replace("/", " ") : asset.SafeName;

                    if (includeIdForDuplicates && asset.SafeName != Asset.NONE)
                    {
                        name = $"{name} [{asset.AssetId}]";
                    }

                    bool isSubPackage = asset.ParentId > 0;
                    string groupKey = intoSubmenu && !asset.SafeName.StartsWith("-")
                        ? name.Substring(0, 1).ToUpperInvariant()
                        : string.Empty;

                    assetEntries.Add(new AssetEntry
                    {
                        Name = name,
                        IsSubPackage = isSubPackage,
                        GroupKey = groupKey
                    });
                }
            }

            // Custom sorting
            assetEntries.Sort((a, b) =>
            {
                int cmp = a.IsSubPackage.CompareTo(b.IsSubPackage); // Non-sub-packages first
                if (cmp != 0) return cmp;

                cmp = string.Compare(a.GroupKey, b.GroupKey, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;

                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            // Building the final list
            if (assetEntries.Count > 0)
            {
                int noneIdx = -1;
                result.Add(string.Empty);
                for (int i = 0; i < assetEntries.Count; i++)
                {
                    AssetEntry entry = assetEntries[i];

                    string displayName;
                    if (intoSubmenu)
                    {
                        if (entry.IsSubPackage)
                        {
                            // Sub-packages under "-Sub- / GroupKey / Name"
                            displayName = "-Sub-/" + entry.GroupKey + "/" + entry.Name;
                        }
                        else
                        {
                            // Non-sub-packages under "GroupKey / Name"
                            displayName = entry.GroupKey + "/" + entry.Name;
                        }
                    }
                    else
                    {
                        if (entry.IsSubPackage)
                        {
                            displayName = "-Sub- " + entry.Name;
                        }
                        else
                        {
                            displayName = entry.Name;
                        }
                    }

                    result.Add(displayName);
                    if (entry.Name == Asset.NONE) noneIdx = result.Count - 1;
                }

                if (noneIdx >= 0)
                {
                    result.RemoveAt(noneIdx);
                    result.Insert(1, Asset.NONE);
                }
            }

            return result.ToArray();
        }

        /// <summary>
        /// Extracts tag names for dropdown menus.
        /// Note: Tags don't use groupLists since they can contain "/" literally and dropdowns treat slashes literally for tags.
        /// </summary>
        internal static string[] ExtractTagNames(List<Tag> tags)
        {
            List<string> result = new List<string> {"-all-", "-none-", string.Empty};
            result.AddRange(tags
                .Select(a => a.Name)
                .OrderBy(s => s));

            if (result.Count == 2) result.RemoveAt(1);

            return result.ToArray();
        }

        internal static SearchablePopup.PopupItem[] ExtractTagPopupItems(List<Tag> tags)
        {
            List<SearchablePopup.PopupItem> result = new List<SearchablePopup.PopupItem>
            {
                new SearchablePopup.PopupItem("-all-"),
                new SearchablePopup.PopupItem("-none-"),
                new SearchablePopup.PopupItem(string.Empty)
            };

            result.AddRange(tags
                .OrderBy(t => t.Name)
                .Select(t =>
                {
                    Color color = t.GetColor();
                    bool tintBackground = color != Tag.DefaultColor;
                    return new SearchablePopup.PopupItem(t.Name, color, tintBackground);
                }));

            return result.ToArray();
        }

        /// <summary>
        /// Extracts publisher names for dropdown menus.
        /// </summary>
        internal static string[] ExtractPublisherNames(IEnumerable<AssetInfo> assets)
        {
            bool intoSubmenu =
                AI.Config.groupLists &&
                assets.Count(a => a.FileCount > 0) >
                MAX_DROPDOWN_ITEMS; // approximation, publishers != assets but roughly the same
            List<string> result = new List<string> {"-all-", string.Empty};
            result.AddRange(assets
                .Where(a => a.FileCount > 0)
                .Where(a => !a.Exclude)
                .Where(a => !string.IsNullOrEmpty(a.SafePublisher))
                .Select(a =>
                    intoSubmenu
                        ? a.SafePublisher.Substring(0, 1).ToUpperInvariant() + "/" + a.SafePublisher
                        : a.SafePublisher)
                .Distinct()
                .OrderBy(s => s));

            if (result.Count == 2) result.RemoveAt(1);

            return result.ToArray();
        }

        /// <summary>
        /// Extracts category names for dropdown menus.
        /// </summary>
        internal static string[] ExtractCategoryNames(IEnumerable<AssetInfo> assets)
        {
            List<string> result = new List<string> {"-all-", string.Empty};
            result.AddRange(assets
                .Where(a => a.FileCount > 0)
                .Where(a => !a.Exclude)
                .Where(a => !string.IsNullOrEmpty(a.GetDisplayCategory()))
                .Select(a => a.GetDisplayCategory())
                .Distinct()
                .OrderBy(s => s));

            if (result.Count == 2) result.RemoveAt(1);

            return result.ToArray();
        }

        internal static string[] LoadTypes()
        {
            List<string> result = new List<string> {"-all-", string.Empty};

            string query = "SELECT Distinct(Type) from AssetFile where Type IS NOT NULL and Type != \"\" order by Type";
            List<string> raw = DBAdapter.DB.QueryScalars<string>($"{query}");

            List<string> groupTypes = new List<string>();
            foreach (KeyValuePair<AI.AssetGroup, string[]> group in AI.TypeGroups)
            {
                groupTypes.AddRange(group.Value);
                foreach (string type in group.Value)
                {
                    if (raw.Contains(type))
                    {
                        result.Add($"{group.Key}");
                        break;
                    }
                }
            }

            if (AI.Config.showExtensionsList)
            {
                if (result.Last() != "") result.Add(string.Empty);

                // others
                result.AddRange(raw.Where(r => !groupTypes.Contains(r)).Select(type => $"Others/{type}"));

                // all
                result.AddRange(raw.Select(type => $"All/{type}"));
            }

            if (result.Count == 2) result.RemoveAt(1);

            return result.ToArray();
        }

        internal static AssetInfo GetAssetByPath(string path, Asset asset)
        {
            string query = "SELECT *, AssetFile.Id as Id from AssetFile left join Asset on Asset.Id = AssetFile.AssetId where AssetFile.Path = ? COLLATE NOCASE and Asset.Id = ?";
            List<AssetInfo> result = DBAdapter.DB.Query<AssetInfo>(query, path, asset.Id);

            return result.FirstOrDefault();
        }

        /// <summary>
        /// Copies an asset file to a destination folder, optionally including dependencies.
        /// Uses batched processing to minimize AssetDatabase.Refresh() calls and handles
        /// parallel execution safely via CopyCoordinator.
        /// </summary>
        public static async Task<string> CopyTo(AssetInfo info, string folder, bool withDependencies = false, int scriptMode = 0, bool fromDragDrop = false, bool outOfProject = false, bool reimport = false, bool previewMode = false)
        {
            // Use coordinator for deduplication of parallel calls
            string cacheKey = info.Guid;

            // Invalidate cache when reimport is requested to force actual file replacement
            // Otherwise the cached result would be returned without invoking DoCopyToInternal
            if (reimport && !string.IsNullOrEmpty(cacheKey))
            {
                CopyCoordinator.Invalidate(cacheKey, folder);
            }

            // Let the coordinator handle caching and deduplication
            // Don't short-circuit with ValidateAndGet here because that would skip dependency processing
            // if a previous incomplete operation left the main file but not dependencies
            return await CopyCoordinator.AcquireOrCopy(cacheKey, folder, async () =>
            {
                return await DoCopyToInternal(info, folder, withDependencies, scriptMode, fromDragDrop, outOfProject, reimport, previewMode);
            });
        }

        /// <summary>
        /// Copies multiple asset files and their dependencies to a destination folder in a single batch.
        /// This dramatically reduces AssetDatabase.Refresh() calls by batching all files together.
        /// Used for bulk preview generation where many files from the same package need to be materialized.
        /// </summary>
        /// <param name="infos">List of assets to copy</param>
        /// <param name="folder">Target folder (typically the preview work folder)</param>
        /// <param name="withDependencies">Whether to include dependencies</param>
        /// <param name="previewMode">Whether this is for preview generation (affects GUID reuse behavior)</param>
        /// <returns>Dictionary mapping AssetInfo.Id to the project-relative path of the copied file</returns>
        public static async Task<Dictionary<int, string>> CopyToBatch(List<AssetInfo> infos, string folder, bool withDependencies, bool previewMode)
        {
            Dictionary<int, string> results = new Dictionary<int, string>();
            if (infos == null || infos.Count == 0) return results;

            // Collect all work items from all infos, deduplicating by GUID
            List<CopyWorkItem> allWorkItems = new List<CopyWorkItem>();
            HashSet<string> processedGuids = new HashSet<string>();
            Dictionary<AssetInfo, AssetInfo> infoToWorkInfo = new Dictionary<AssetInfo, AssetInfo>();

            foreach (AssetInfo info in infos)
            {
                // Handle SRP replacement
                AssetInfo workInfo = info;
                if (info.SRPMainReplacement != null)
                {
                    workInfo = new AssetInfo()
                        .CopyFrom(workInfo, false)
                        .CopyFrom(info.SRPSupportPackage, info.SRPMainReplacement);
                }
                infoToWorkInfo[info] = workInfo;

                string sourcePath = await EnsureMaterialized(workInfo);
                if (sourcePath == null) continue;

                // Calculate target path - use import structure 1 (original path) for preview mode
                string targetPath = Path.Combine(folder, workInfo.Path);
                if (previewMode) targetPath = targetPath.Replace("~", "");
                string mainFileType = IOUtils.GetExtensionWithoutDot(targetPath).ToLowerInvariant();

                // Skip if GUID already processed (deduplication)
                if (!string.IsNullOrEmpty(workInfo.Guid) && processedGuids.Contains(workInfo.Guid))
                {
                    // Find existing work item to get the target path
                    CopyWorkItem existing = allWorkItems.FirstOrDefault(w => w.Info.Guid == workInfo.Guid);
                    if (existing != null)
                    {
                        results[info.Id] = AssetUtils.RemoveProjectRoot(existing.TargetPath);
                    }
                    continue;
                }

                CopyWorkItem mainItem = new CopyWorkItem
                {
                    Info = workInfo,
                    SourcePath = sourcePath,
                    TargetPath = targetPath,
                    Category = CopyCoordinator.ClassifyFile(sourcePath, workInfo.Guid, mainFileType),
                    IsMainFile = true
                };

                // Add dependencies
                if (withDependencies)
                {
                    List<AssetFile> deps = GetDependenciesForScriptMode(workInfo, 0); // scriptMode 0 = no scripts
                    if (deps != null)
                    {
                        foreach (AssetFile dep in deps)
                        {
                            // Skip if already processed
                            if (!string.IsNullOrEmpty(dep.Guid) && processedGuids.Contains(dep.Guid))
                                continue;

                            string depFileType = IOUtils.GetExtensionWithoutDot(dep.FileName).ToLowerInvariant();

                            // Get asset for this dependency
                            Asset asset = workInfo.CrossPackageDependencies?.FirstOrDefault(p => p.Id == dep.AssetId);
                            if (asset == null)
                            {
                                asset = workInfo.SRPSupportPackage == null ? workInfo.ToAsset() : workInfo.SRPOriginalBackup.ToAsset();
                            }

                            string depSourcePath = await EnsureMaterialized(asset, dep);
                            if (depSourcePath == null) continue;

                            // Calculate dependency target path preserving folder structure
                            string depPath = dep.Path;
                            string lowerPath = depPath.ToLowerInvariant();
                            int assetsIndex = lowerPath.StartsWith("assets/") ? 0 : lowerPath.IndexOf("/assets/", StringComparison.OrdinalIgnoreCase);
                            if (assetsIndex >= 0)
                            {
                                depPath = depPath.Substring(assetsIndex == 0 ? 7 : assetsIndex + 8);
                            }

                            string depTargetPath = Path.Combine(folder, depPath);
                            if (previewMode) depTargetPath = depTargetPath.Replace("~", "");

                            AssetInfo depInfo = new AssetInfo().CopyFrom(asset, dep);

                            allWorkItems.Add(new CopyWorkItem
                            {
                                Info = depInfo,
                                SourcePath = depSourcePath,
                                TargetPath = depTargetPath,
                                Category = CopyCoordinator.ClassifyFile(depSourcePath, depInfo.Guid, depFileType),
                                IsMainFile = false
                            });

                            if (!string.IsNullOrEmpty(dep.Guid))
                                processedGuids.Add(dep.Guid);
                        }
                    }
                }

                // Add main file
                allWorkItems.Add(mainItem);
                if (!string.IsNullOrEmpty(workInfo.Guid))
                    processedGuids.Add(workInfo.Guid);
            }

            if (allWorkItems.Count == 0) return results;

            // Process all files in a single batch - ONE refresh for everything
            Dictionary<AssetInfo, string> batchResults = await ProcessCopyBatch(allWorkItems, false, false, previewMode);

            // Map results back to original AssetInfo.Id
            foreach (AssetInfo info in infos)
            {
                if (infoToWorkInfo.TryGetValue(info, out AssetInfo workInfo))
                {
                    if (batchResults.TryGetValue(workInfo, out string path))
                    {
                        results[info.Id] = path;
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Internal implementation of CopyTo with batched processing.
        /// </summary>
        private static async Task<string> DoCopyToInternal(AssetInfo info, string folder, bool withDependencies, int scriptMode, bool fromDragDrop, bool outOfProject, bool reimport, bool previewMode)
        {
            // copy over SRP support reference if required for main file
            AssetInfo workInfo = info;
            if (info.SRPMainReplacement != null)
            {
                workInfo = new AssetInfo()
                    .CopyFrom(workInfo, false)
                    .CopyFrom(info.SRPSupportPackage, info.SRPMainReplacement);
            }

            string sourcePath = await EnsureMaterialized(workInfo);
            bool conversionNeeded = false;
            if (sourcePath == null) return null;

            string finalPath = folder;

            // complex import structure only supported for Unity Packages
            int finalImportStructure = workInfo.AssetSource == Asset.Source.CustomPackage ||
                workInfo.AssetSource == Asset.Source.Archive ||
                workInfo.AssetSource == Asset.Source.RegistryPackage ||
                workInfo.AssetSource == Asset.Source.AssetStorePackage
                    ? AI.Config.importStructure
                    : 0;

            // calculate dependencies on demand
            while (workInfo.DependencyState == AssetInfo.DependencyStateOptions.Calculating) await Task.Yield();
            if (withDependencies && (info.DependencyState == AssetInfo.DependencyStateOptions.Unknown || info.DependencyState == AssetInfo.DependencyStateOptions.Partial))
            {
                await AI.CalculateDependencies(workInfo);
            }

            // override again for single files without dependencies in drag & drop scenario as that feels more natural
            if (fromDragDrop && (workInfo.Dependencies == null || workInfo.Dependencies.Count == 0)) finalImportStructure = 0;

            switch (finalImportStructure)
            {
                case 0:
                    // put into subfolder if multiple files are affected
                    if (withDependencies && workInfo.Dependencies != null && workInfo.Dependencies.Count > 0)
                    {
                        finalPath = Path.Combine(finalPath.RemoveTrailing("."), Path.GetFileNameWithoutExtension(workInfo.FileName)).Trim().RemoveTrailing(".");
                        Directory.CreateDirectory(finalPath);
                    }
                    break;

                case 1:
                    string path = workInfo.Path;
                    if (path.ToLowerInvariant().StartsWith("assets/")) path = path.Substring(7);
                    finalPath = Path.Combine(
                        folder,
                        workInfo.AssetSource == Asset.Source.RegistryPackage && !previewMode ? workInfo.SafeName : "",
                        Path.GetDirectoryName(path));
                    break;
            }

            // Collect all files to copy into work items
            List<CopyWorkItem> workItems = new List<CopyWorkItem>();

            // Add main file
            string mainTargetPath = Path.Combine(finalPath, Path.GetFileName(sourcePath));
            if (previewMode) mainTargetPath = mainTargetPath.Replace("~", "");
            string mainFileType = IOUtils.GetExtensionWithoutDot(mainTargetPath).ToLowerInvariant();
            if (ConversionExtensions.Contains(mainFileType)) conversionNeeded = true;

            CopyWorkItem mainItem = new CopyWorkItem
            {
                Info = workInfo,
                SourcePath = sourcePath,
                TargetPath = mainTargetPath,
                Category = CopyCoordinator.ClassifyFile(sourcePath, workInfo.Guid, mainFileType),
                IsMainFile = true
            };

            // Collect dependencies first (they will be processed before main)
            if (withDependencies)
            {
                List<AssetFile> deps = GetDependenciesForScriptMode(workInfo, scriptMode);
                if (deps != null)
                {
                    for (int i = 0; i < deps.Count; i++)
                    {
                        string depFileType = IOUtils.GetExtensionWithoutDot(deps[i].FileName).ToLowerInvariant();
                        if (ConversionExtensions.Contains(depFileType)) conversionNeeded = true;

                        // special handling for Asset Manager assets, as they will bring in dependencies automatically
                        if (workInfo.AssetSource == Asset.Source.AssetManager) continue;

                        // select correct asset from pool
                        Asset asset = workInfo.CrossPackageDependencies.FirstOrDefault(p => p.Id == deps[i].AssetId);
                        if (asset == null)
                        {
                            // if not found this is either the SRP original or the current asset
                            asset = workInfo.SRPSupportPackage == null ? workInfo.ToAsset() : workInfo.SRPOriginalBackup.ToAsset();
                        }

                        string depSourcePath = await EnsureMaterialized(asset, deps[i]);
                        if (depSourcePath == null) continue;

                        string depTargetPath;
                        switch (finalImportStructure)
                        {
                            case 0:
                                depTargetPath = Path.Combine(finalPath, Path.GetFileName(deps[i].Path));
                                break;

                            case 1:
                                string depPath = deps[i].Path;
                                string lowerPath = depPath.ToLowerInvariant();

                                // Handle both relative paths (assets/...) and absolute paths (.../Assets/...)
                                int assetsIndex = -1;
                                if (lowerPath.StartsWith("assets/"))
                                {
                                    assetsIndex = 0;
                                }
                                else
                                {
                                    int slashIndex = lowerPath.IndexOf("/assets/", StringComparison.OrdinalIgnoreCase);
                                    if (slashIndex >= 0) assetsIndex = slashIndex + 1;
                                }

                                if (assetsIndex >= 0)
                                {
                                    depPath = depPath.Substring(assetsIndex + 7); // Skip "assets/"
                                }

                                depTargetPath = Path.Combine(
                                    folder,
                                    asset.AssetSource == Asset.Source.RegistryPackage && !previewMode ? asset.SafeName : "",
                                    depPath);
                                break;

                            default:
                                depTargetPath = Path.Combine(finalPath, Path.GetFileName(deps[i].Path));
                                break;
                        }

                        if (previewMode) depTargetPath = depTargetPath.Replace("~", "");

                        AssetInfo depInfo = new AssetInfo().CopyFrom(asset, deps[i]);

                        workItems.Add(new CopyWorkItem
                        {
                            Info = depInfo,
                            SourcePath = depSourcePath,
                            TargetPath = depTargetPath,
                            Category = CopyCoordinator.ClassifyFile(depSourcePath, depInfo.Guid, depFileType),
                            IsMainFile = false
                        });
                    }
                }
                else
                {
                    Debug.LogError($"Dependency calculation failed for '{workInfo}'.");
                }
            }

            // Add main file last (will be sorted appropriately in ProcessCopyBatch)
            workItems.Add(mainItem);

            // Process all files in optimized batch
            Dictionary<AssetInfo, string> results = await ProcessCopyBatch(workItems, reimport, outOfProject, previewMode);

            // Get the result for the main file
            string result = null;
            if (results.TryGetValue(workInfo, out string mainResult))
            {
                result = mainResult;
            }

            if (result == null)
            {
                Debug.LogError($"Failed to copy main file for '{workInfo}'.");
                return null;
            }

            // Update project path
            if (string.IsNullOrEmpty(info.Guid))
            {
                // special case of original index without GUID, fall back to file check only
                string fullPath = AssetUtils.AddProjectRoot(result);
                if (File.Exists(fullPath)) info.ProjectPath = result;
            }
            else
            {
                info.ProjectPath = AssetDatabase.GUIDToAssetPath(workInfo.Guid);
            }

            if ((AI.Config.useCustomPipelineConverter || AI.Config.useUnityPipelineConverter) && !previewMode && conversionNeeded && info.SRPSupportPackage == null)
            {
                bool unityConverterSucceeded = false;
                if (AI.Config.useUnityPipelineConverter)
                {
                    unityConverterSucceeded = await PipelineConverter.RunUnityConverterAsync();
                }
                if (!unityConverterSucceeded && AI.Config.useCustomPipelineConverter)
                {
                    PipelineConverter.ConvertImportedMaterials(results.Values);
                }
            }

            // do post steps after all files are materialized as otherwise nested prefab operations will fail
            foreach (string file in results.Values)
            {
                PerformPostImportOperations(file);
            }

            AI.Config.statsImports++;

            return result;
        }

        private static List<AssetFile> GetDependenciesForScriptMode(AssetInfo info, int scriptMode)
        {
            switch (scriptMode)
            {
                case 0: // Never Import
                    return info.MediaDependencies;

                case 2: // Direct Only
                    return info.Dependencies; // MediaDependencies + ScriptDependencies (GUID-based)

                case 3: // Extended Analysis
                    List<AssetFile> extended = new List<AssetFile>(info.Dependencies ?? new List<AssetFile>());
                    if (info.ExtendedScriptDependencies != null)
                    {
                        extended.AddRange(info.ExtendedScriptDependencies);
                    }
                    return extended.GroupBy(f => f.Id).Select(g => g.First()).ToList();

                case 4: // All Scripts
                    List<AssetFile> all = new List<AssetFile>(info.Dependencies ?? new List<AssetFile>());
                    List<AssetFile> allScriptFiles = DBAdapter.DB.Query<AssetFile>(
                        $"SELECT * FROM AssetFile WHERE AssetId=? AND {DependencyAnalysis.ScriptRelatedSqlFilter()}",
                        info.AssetId);
                    all.AddRange(allScriptFiles);
                    return all.GroupBy(f => f.Id).Select(g => g.First()).ToList();

                default:
                    return info.MediaDependencies;
            }
        }

        /// <summary>
        /// Runs the URP material converter if available (fire-and-forget).
        /// </summary>
        /// <returns>True if the Unity converter ran without error, false otherwise.</returns>
        public static bool RunURPConverter() => PipelineConverter.RunUnityConverter();

        /// <summary>
        /// Runs the URP material converter and waits for it to complete.
        /// </summary>
        /// <returns>True if the Unity converter ran successfully, false otherwise.</returns>
        public static Task<bool> RunURPConverterAsync() => PipelineConverter.RunUnityConverterAsync();

        /// <summary>
        /// Converts material assets at the given project-relative paths from BIRP to the current render pipeline.
        /// </summary>
        public static void ConvertImportedMaterialsToPipeline(IEnumerable<string> importedPaths) => PipelineConverter.ConvertImportedMaterials(importedPaths);

        /// <summary>
        /// Scans all materials in the project and converts any remaining BIRP materials to the current render pipeline.
        /// </summary>
        public static void ConvertAllProjectMaterialsToPipeline() => PipelineConverter.ConvertAllProjectMaterials();

        private static void PerformPostImportOperations(string path)
        {
            if (!AI.Config.removeLODs) return;

            string type = IOUtils.GetExtensionWithoutDot(path).ToLowerInvariant();
            switch (type)
            {
                case "prefab":
                    if (AI.Config.removeLODs) AssetUtils.RemoveLODGroups(path);
                    break;
            }
        }

        /// <summary>
        /// Checks if a path is within the Unity Packages/ folder (case-insensitive).
        /// </summary>
        /// <param name="path">The path to check (can be project-relative or absolute)</param>
        /// <returns>True if the path is in the Packages/ folder</returns>
        private static bool IsInPackagesFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            // Normalize to forward slashes and handle both relative and absolute paths
            string normalizedPath = path.Replace("\\", "/");

            // Check if it's an absolute path containing /Packages/
            if (normalizedPath.IndexOf("/Packages/", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // Check if it starts with Packages/ (relative path)
            if (normalizedPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        /// <summary>
        /// Checks if a path is within the TextMesh Pro folder (case-insensitive).
        /// </summary>
        /// <param name="path">The path to check (can be project-relative or absolute)</param>
        /// <returns>True if the path is in the Assets/TextMesh Pro/ folder or below</returns>
        private static bool IsInTextMeshProFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            // Normalize to forward slashes and handle both relative and absolute paths
            string normalizedPath = path.Replace("\\", "/");

            // Check if it's an absolute path containing /Assets/TextMesh Pro/
            if (normalizedPath.IndexOf("/Assets/TextMesh Pro/", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // Check if it starts with Assets/TextMesh Pro/ (relative path)
            if (normalizedPath.StartsWith("Assets/TextMesh Pro/", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        /// <summary>
        /// Checks if a file with the given GUID already exists in the project.
        /// </summary>
        /// <param name="guid">The GUID to check</param>
        /// <param name="previewMode">If true, includes temp/preview folders; if false, excludes them</param>
        /// <returns>The existing asset path if found and should be reused, null otherwise</returns>
        public static string GetExistingAssetPathForGuid(string guid, bool previewMode, bool validateLoadable = false)
        {
            if (string.IsNullOrEmpty(guid)) return null;

            string existing = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrWhiteSpace(existing)) return null;

            // Verify the file actually exists on disk (use absolute path for reliability)
            string absolutePath = AssetUtils.AddProjectRoot(existing);
            if (!File.Exists(absolutePath)) return null;

            // Optionally validate that the asset can actually be loaded (catches assets with import errors)
            if (validateLoadable)
            {
                UnityEngine.Object obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(existing);
                if (obj == null) return null;
            }

            // In preview mode, reuse any existing file (including temp/preview folders)
            // In non-preview mode, exclude files in temp folders to avoid reusing temporary files
            if (previewMode)
            {
                return existing;
            }

            // Exclude both temp folders in non-preview mode
            if (existing.Contains(AI.TEMP_FOLDER) || existing.Contains(UnityPreviewGenerator.PREVIEW_FOLDER))
            {
                return null;
            }

            // Files in Packages/ folder are always reused as-is (immutable)
            return existing;
        }

        internal static async Task ProcessSubPackages(Asset asset, List<AssetFile> subPackages)
        {
            List<AssetFile> unityPackages = subPackages.Where(p => p.IsUnityPackage()).ToList();
            List<AssetFile> archives = subPackages.Where(p => p.IsArchive()).ToList();

            if (unityPackages.Count > 0)
            {
                await AI.Actions.RunWithProgress<UnityPackageImporter>(
                    ActionHandler.ACTION_SUB_PACKAGES_INDEX,
                    "Indexing sub-packages",
                    imp => imp.ProcessSubPackages(asset, unityPackages));
            }

            if (archives.Count > 0)
            {
                await AI.Actions.RunWithProgress<ArchiveImporter>(
                    ActionHandler.ACTION_SUB_PACKAGES_INDEX,
                    "Indexing sub-archives",
                    imp => imp.ProcessSubArchives(asset, archives));
            }
        }

        public static void ForgetAssetFile(AssetFile info)
        {
            string previewFile = info.GetPreviewFile(Paths.GetPreviewFolder());
            string animPreviewFile = info.GetPreviewFile(Paths.GetPreviewFolder(), true);

            IOUtils.TryDeleteFile(previewFile);
            IOUtils.TryDeleteFile(animPreviewFile);

            DBAdapter.DB.Execute("DELETE from AssetFile where Id=?", info.Id);
            DBAdapter.DB.Execute("DELETE from TagAssignment where TagTarget=? and TargetId=?", TagAssignment.Target.Asset, info.Id);
            DBAdapter.DB.Execute("DELETE from MetadataAssignment where MetadataTarget=? and TargetId=?", MetadataAssignment.Target.Asset, info.Id);
        }

        public static void SetFilesHidden(List<int> fileIds, bool hidden)
        {
            if (fileIds == null || fileIds.Count == 0) return;

            int value = hidden ? 1 : 0;
            foreach (int id in fileIds)
            {
                DBAdapter.DB.Execute("UPDATE AssetFile SET Hidden=? WHERE Id=?", value, id);
            }
        }

        public static List<string> GetHiddenPaths(int assetId)
        {
            return DBAdapter.DB.Query<AssetFile>("SELECT Path FROM AssetFile WHERE AssetId=? AND Hidden=1", assetId)
                .Select(f => f.Path)
                .ToList();
        }

        public static void RestoreHiddenPaths(int assetId, List<string> paths)
        {
            if (paths == null || paths.Count == 0) return;

            foreach (string path in paths)
            {
                DBAdapter.DB.Execute("UPDATE AssetFile SET Hidden=1 WHERE AssetId=? AND Path=?", assetId, path);
            }
        }

        public static void ApplyHidePatterns(int assetId)
        {
            Dictionary<int, HideRuleSet> hideRules = Metadata.GetHideRuleSets();
            if (!hideRules.TryGetValue(assetId, out HideRuleSet ruleSet)) return;

            ApplyHideRuleSet(assetId, ruleSet, ruleSet.Mode == HideRuleMode.Include);
        }

        internal static void ApplyHidePatternsFromScratch(int assetId)
        {
            Dictionary<int, HideRuleSet> hideRules = Metadata.GetHideRuleSets();
            HideRuleSet ruleSet = hideRules.TryGetValue(assetId, out HideRuleSet parsedRuleSet)
                ? parsedRuleSet
                : new HideRuleSet(HideRuleMode.Hide, null);

            ApplyHideRuleSet(assetId, ruleSet, true);
        }

        private static void ApplyHideRuleSet(int assetId, HideRuleSet ruleSet, bool resetHiddenState)
        {
            if (ruleSet == null) return;

            bool includeMode = ruleSet.Mode == HideRuleMode.Include;
            if (resetHiddenState)
            {
                int defaultHidden = includeMode ? 1 : 0;
                DBAdapter.DB.Execute("UPDATE AssetFile SET Hidden=? WHERE AssetId=?", defaultHidden, assetId);
            }

            foreach (string pattern in ruleSet.Rules)
            {
                if (string.IsNullOrWhiteSpace(pattern)) continue;

                int hidden = includeMode ? 0 : 1;
                if (pattern.StartsWith("*"))
                {
                    string ext = pattern.TrimStart('*').TrimStart('.');
                    if (!string.IsNullOrEmpty(ext))
                    {
                        DBAdapter.DB.Execute("UPDATE AssetFile SET Hidden=? WHERE AssetId=? AND Type=?", hidden, assetId, ext);
                    }
                }
                else if (pattern.Contains("/"))
                {
                    DBAdapter.DB.Execute("UPDATE AssetFile SET Hidden=? WHERE AssetId=? AND Path LIKE ?", hidden, assetId, $"%{pattern}%");
                }
                else
                {
                    DBAdapter.DB.Execute("UPDATE AssetFile SET Hidden=? WHERE AssetId=? AND (Path LIKE ? OR Path LIKE ?)", hidden, assetId, $"%/{pattern}/%", $"{pattern}/%");
                }
            }
        }

        /// <summary>
        /// Resets a package to "new" state for reindexing.
        /// </summary>
        public static Asset ForgetPackage(AssetInfo info, bool removeExclusion = false, bool removeFiles = false)
        {
            if (removeFiles)
            {
                // delete child packages first
                foreach (AssetInfo childInfo in info.EnumerateChildInfos())
                {
                    RemovePackage(childInfo, true);
                }

                DBAdapter.DB.Execute("DELETE from AssetFile where AssetId=?", info.AssetId);
                // TODO: remove assetfile tag assignments
            }

            // already set
            if (info.CurrentState == Asset.State.New) return info.ToAsset();

            Asset existing = DBAdapter.DB.Find<Asset>(info.AssetId);
            if (existing == null) return null;

            existing.CurrentState = Asset.State.New;
            info.CurrentState = Asset.State.New;
            existing.LastOnlineRefresh = DateTime.MinValue;
            info.LastOnlineRefresh = DateTime.MinValue;
            existing.ETag = null;
            info.ETag = null;
            if (removeExclusion)
            {
                existing.Exclude = false;
                info.Exclude = false;
            }

            DBAdapter.DB.Update(existing);

            return existing;
        }

        /// <summary>
        /// Completely removes a package and all its data from the database.
        /// </summary>
        public static void RemovePackage(AssetInfo info, bool deleteFiles)
        {
            // delete child packages first
            foreach (AssetInfo childInfo in info.EnumerateChildInfos())
            {
                RemovePackage(childInfo, deleteFiles);
            }

            // actual files
            if (deleteFiles && info.ParentId == 0)
            {
                if (File.Exists(info.GetLocation(true)))
                {
                    IOUtils.TryDeleteFile(info.GetLocation(true));
                }
                if (Directory.Exists(info.GetLocation(true)))
                {
                    Task.Run(() => IOUtils.DeleteFileOrDirectory(info.GetLocation(true)));
                }
            }

            // previews
            string previewFolder = Path.Combine(Paths.GetPreviewFolder(), info.AssetId.ToString());
            if (Directory.Exists(previewFolder))
            {
                Task.Run(() => IOUtils.DeleteFileOrDirectory(previewFolder));
            }

            // cache
            string cacheFolder = Paths.GetMaterializedAssetPath(info.ToAsset());
            if (Directory.Exists(cacheFolder))
            {
                Task.Run(() => IOUtils.DeleteFileOrDirectory(cacheFolder));
            }

            Asset existing = ForgetPackage(info, false, true);
            if (existing == null) return;

            DBAdapter.DB.Execute("DELETE from AssetMedia where AssetId=?", info.AssetId);
            DBAdapter.DB.Execute("DELETE from TagAssignment where TagTarget=? and TargetId=?", TagAssignment.Target.Package, info.AssetId);
            DBAdapter.DB.Execute("DELETE from MetadataAssignment where MetadataTarget=? and TargetId=?", MetadataAssignment.Target.Package, info.AssetId);
            DBAdapter.DB.Execute("DELETE from Asset where Id=?", info.AssetId);

            info.ForeignId = 0; // reset foreign ID to avoid online refreshes 
        }

        /// <summary>
        /// Counts the number of purchased assets in the list.
        /// </summary>
        public static int CountPurchasedAssets(IEnumerable<AssetInfo> assets)
        {
            return assets.Count(a => a.ParentId == 0 && (a.AssetSource == Asset.Source.AssetStorePackage || (a.AssetSource == Asset.Source.RegistryPackage && a.ForeignId > 0)));
        }

        /// <summary>
        /// Plays an audio file from an asset, extracting it if necessary.
        /// </summary>
        /// <param name="info">The asset info containing the audio file</param>
        /// <param name="ct">Cancellation token for async operations</param>
        public static async Task PlayAudio(AssetInfo info, CancellationToken ct = default(CancellationToken))
        {
            string targetPath;

            // check if in project already, then skip extraction
            if (info.InProject)
            {
                targetPath = IOUtils.PathCombine(Path.GetDirectoryName(Application.dataPath), info.ProjectPath);
            }
            else
            {
                targetPath = await EnsureMaterialized(info, AI.Config.extractSingleFiles, ct);
                if (targetPath != null && !AI.Config.extractSingleFiles && AI.Config.keepExtractedOnAudio && !info.KeepExtracted)
                {
                    // ensure extraction is set to true for future audio playback
                    AI.SetAssetExtraction(info, true);
                }
            }

            if (targetPath != null)
            {
                AudioClip clip = await AudioTool.AudioManager.LoadAudioFromFile(targetPath);
                if (clip != null)
                {
                    AudioTool.AudioManager.PlayClip(clip, 0, AI.Config.loopAudio);
                }
            }
        }

        /// <summary>
        /// Helper class for asset name extraction sorting.
        /// </summary>
        private class AssetEntry
        {
            public string Name;
            public bool IsSubPackage;
            public string GroupKey;
        }

        #region Stateless Copy Helpers

        /// <summary>
        /// Represents a file to be copied with all necessary metadata.
        /// </summary>
        private sealed class CopyWorkItem
        {
            public AssetInfo Info;
            public string SourcePath;
            public string TargetPath;
            public CopyCoordinator.FileCategory Category;
            public bool IsMainFile;
            public bool IsInPackagesFolder;
        }

        private static void ApplyCaseSensitivePathGuard(List<CopyWorkItem> nonScripts, List<CopyWorkItem> scripts)
        {
            List<CopyWorkItem> allItems = nonScripts.Concat(scripts).ToList();
            if (allItems.Count == 0) return;

            List<CaseSensitivePathGuard.PathCandidate> adjustedPaths = CaseSensitivePathGuard.AdjustPaths(
                allItems.Select(item => new CaseSensitivePathGuard.PathCandidate
                {
                    Path = AssetUtils.AddProjectRoot(IOUtils.ToShortPath(item.TargetPath)),
                    IsDirectory = Directory.Exists(item.SourcePath),
                    Tag = item
                }),
                AssetUtils.GetProjectRoot(),
                "import");

            HashSet<CopyWorkItem> remainingItems = new HashSet<CopyWorkItem>();
            foreach (CaseSensitivePathGuard.PathCandidate entry in adjustedPaths)
            {
                CopyWorkItem item = (CopyWorkItem)entry.Tag;
                item.TargetPath = AssetUtils.RemoveProjectRoot(entry.Path);
                remainingItems.Add(item);
            }

            nonScripts.RemoveAll(item => !remainingItems.Contains(item));
            scripts.RemoveAll(item => !remainingItems.Contains(item));
        }

        /// <summary>
        /// Ensures a folder path exists in Unity's AssetDatabase by creating any missing parent folders.
        /// This is required before using AssetDatabase.MoveAsset to a new location.
        /// </summary>
        /// <param name="folderPath">The relative folder path (e.g., "Assets/ThirdParty/MyFolder")</param>
        private static void EnsureAssetDatabaseFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;

            // Normalize path separators
            folderPath = folderPath.Replace("\\", "/").TrimEnd('/');

            // Check if folder already exists
            if (AssetDatabase.IsValidFolder(folderPath)) return;

            // Split into parts and create recursively
            string[] parts = folderPath.Split('/');
            string currentPath = parts[0]; // Start with "Assets"

            for (int i = 1; i < parts.Length; i++)
            {
                string nextPath = currentPath + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(nextPath))
                {
                    AssetDatabase.CreateFolder(currentPath, parts[i]);
                }
                currentPath = nextPath;
            }
        }

        /// <summary>
        /// Cleans up empty folders after files have been moved during reorganization.
        /// Recursively checks parent directories up to the Assets folder.
        /// </summary>
        private static void CleanEmptyFoldersAfterMove(HashSet<string> directories)
        {
            bool deleted;
            do
            {
                deleted = false;
                List<string> currentDirs = directories.ToList();
                directories.Clear();

                foreach (string dir in currentDirs)
                {
                    string fullPath = AssetUtils.AddProjectRoot(dir);
                    if (Directory.Exists(fullPath) && IOUtils.IsDirectoryEmpty(fullPath))
                    {
                        if (AssetDatabase.DeleteAsset(dir))
                        {
                            deleted = true;

                            // Check parent directory too
                            string parent = Path.GetDirectoryName(dir);
                            if (!string.IsNullOrEmpty(parent) && parent.StartsWith("Assets") && parent != "Assets")
                            {
                                directories.Add(parent);
                            }
                        }
                    }
                }
            } while (deleted);
        }

        /// <summary>
        /// Copies a file without any AssetDatabase calls.
        /// </summary>
        private static async Task<bool> CopyFileOnly(string sourcePath, string targetPath)
        {
            try
            {
                string targetFolder = Path.GetDirectoryName(targetPath);
                Directory.CreateDirectory(targetFolder);

                return await IOUtils.TryCopyFile(sourcePath, targetPath, true);
            }
            catch (Exception e)
            {
                Debug.LogError($"Error copying file '{sourcePath}' to '{targetPath}': {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Synchronous version of CopyFileOnly. Must be used inside StartAssetEditing/StopAssetEditing
        /// blocks to prevent async yields from allowing other tasks to nest StartAssetEditing calls,
        /// which would cause subsequent Refresh/ImportAsset calls to become no-ops.
        /// </summary>
        private static bool CopyFileOnlySync(string sourcePath, string targetPath)
        {
            try
            {
                string targetFolder = Path.GetDirectoryName(targetPath);
                Directory.CreateDirectory(targetFolder);

                File.Copy(sourcePath, targetPath, true);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Error copying file '{sourcePath}' to '{targetPath}': {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Copies a meta file if it exists, without any AssetDatabase calls.
        /// </summary>
        private static async Task<bool> CopyMetaOnly(string sourceMetaPath, string targetMetaPath)
        {
            try
            {
                if (!File.Exists(sourceMetaPath)) return false;

                // Delete any existing target meta file first to avoid sharing violations
                await IOUtils.TryDeleteFileAsync(targetMetaPath);
                return await IOUtils.TryCopyFile(sourceMetaPath, targetMetaPath, true);
            }
            catch (Exception e)
            {
                Debug.LogError($"Error copying meta file '{sourceMetaPath}' to '{targetMetaPath}': {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Synchronous version of CopyMetaOnly. Must be used inside StartAssetEditing/StopAssetEditing
        /// blocks to prevent async yields from causing editing counter nesting across parallel tasks.
        /// </summary>
        private static bool CopyMetaOnlySync(string sourceMetaPath, string targetMetaPath)
        {
            try
            {
                if (!File.Exists(sourceMetaPath)) return false;

                // Delete any existing target meta file first to avoid sharing violations
                if (File.Exists(targetMetaPath))
                {
                    try { File.SetAttributes(targetMetaPath, FileAttributes.Normal); }
                    catch {}
                    File.Delete(targetMetaPath);
                }
                File.Copy(sourceMetaPath, targetMetaPath, true);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Error copying meta file '{sourceMetaPath}' to '{targetMetaPath}': {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Updates the GUID in a meta file to match the known GUID.
        /// </summary>
        private static bool EnsureCorrectGuid(string targetMetaPath, string knownGuid)
        {
            if (string.IsNullOrEmpty(knownGuid)) return false;
            if (!File.Exists(targetMetaPath)) return false;

            return AssetUtils.UpdateGuidInMetaFile(targetMetaPath, knownGuid);
        }

        /// <summary>
        /// Injects AssetOrigin metadata into a meta file (file I/O only, no AssetDatabase calls).
        /// </summary>
        private static async Task<bool> InjectAssetOrigin(string targetMetaPath, AssetInfo info, bool outOfProject)
        {
            try
            {
                if (!File.Exists(targetMetaPath)) return false;

                string[] metaContent = await IOUtils.TryReadAllLinesAsync(targetMetaPath);
                if (metaContent == null || metaContent.Length == 0) return false;

                // Skip if already has AssetOrigin
                if (metaContent.Any(l => l.StartsWith("AssetOrigin:"))) return true;

                AssetOrigin origin = info.ToAsset().GetAssetOrigin();
                string assetPath = targetMetaPath.Replace("\\", "/").Replace(".meta", "");
                try
                {
                    origin.assetPath = assetPath.Substring(assetPath.IndexOf("Assets/", StringComparison.Ordinal));
                }
                catch (Exception e)
                {
                    if (!outOfProject) Debug.LogError($"Could not determine asset path from '{assetPath}': {e.Message}");
                }

                List<string> newMetaContent = new List<string>(metaContent)
                {
                    "AssetOrigin:",
                    "  serializedVersion: 1",
                    $"  productId: {origin.productId}",
                    $"  packageName: {ToYamlQuotedString(origin.packageName)}",
                    $"  packageVersion: {ToYamlQuotedString(origin.packageVersion)}",
                    $"  assetPath: {ToYamlQuotedString(origin.assetPath)}",
                    $"  uploadId: {origin.uploadId}"
                };
                await File.WriteAllLinesAsync(targetMetaPath, newMetaContent);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Error injecting AssetOrigin into '{targetMetaPath}': {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Synchronous version of InjectAssetOrigin. Must be used inside StartAssetEditing/StopAssetEditing
        /// blocks to prevent async yields from causing editing counter nesting across parallel tasks.
        /// </summary>
        private static bool InjectAssetOriginSync(string targetMetaPath, AssetInfo info, bool outOfProject)
        {
            try
            {
                if (!File.Exists(targetMetaPath)) return false;

                string[] metaContent = File.ReadAllLines(targetMetaPath);
                if (metaContent == null || metaContent.Length == 0) return false;

                // Skip if already has AssetOrigin
                if (metaContent.Any(l => l.StartsWith("AssetOrigin:"))) return true;

                AssetOrigin origin = info.ToAsset().GetAssetOrigin();
                string assetPath = targetMetaPath.Replace("\\", "/").Replace(".meta", "");
                try
                {
                    origin.assetPath = assetPath.Substring(assetPath.IndexOf("Assets/", StringComparison.Ordinal));
                }
                catch (Exception e)
                {
                    if (!outOfProject) Debug.LogError($"Could not determine asset path from '{assetPath}': {e.Message}");
                }

                List<string> newMetaContent = new List<string>(metaContent)
                {
                    "AssetOrigin:",
                    "  serializedVersion: 1",
                    $"  productId: {origin.productId}",
                    $"  packageName: {ToYamlQuotedString(origin.packageName)}",
                    $"  packageVersion: {ToYamlQuotedString(origin.packageVersion)}",
                    $"  assetPath: {ToYamlQuotedString(origin.assetPath)}",
                    $"  uploadId: {origin.uploadId}"
                };
                File.WriteAllLines(targetMetaPath, newMetaContent);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Error injecting AssetOrigin into '{targetMetaPath}': {e.Message}");
                return false;
            }
        }

        private static string ToYamlQuotedString(string value)
        {
            if (value == null) return "\"\"";

            return "\"" + value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t") + "\"";
        }

        /// <summary>
        /// Copies all files in a directory without any AssetDatabase calls.
        /// </summary>
        private static async Task<bool> CopyDirectoryOnly(string sourcePath, string targetPath)
        {
            try
            {
                string targetFolder = Path.GetDirectoryName(targetPath);
                Directory.CreateDirectory(targetFolder);

                string[] files = Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories);
                HashSet<string> createdDirs = new HashSet<string>();

                foreach (string file in files)
                {
                    string relativePath = file.Substring(sourcePath.Length + 1);
                    string targetFile = Path.Combine(targetFolder, relativePath);
                    string targetFolder2 = Path.GetDirectoryName(targetFile);

                    if (createdDirs.Add(targetFolder2))
                    {
                        Directory.CreateDirectory(targetFolder2);
                    }

                    if (!await IOUtils.TryCopyFile(file, targetFile, true))
                    {
                        Debug.LogWarning($"Failed to copy file '{file}' to '{targetFile}'");
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Error copying directory '{sourcePath}' to '{targetPath}': {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Synchronous version of CopyDirectoryOnly. Must be used inside StartAssetEditing/StopAssetEditing
        /// blocks to prevent async yields from causing editing counter nesting across parallel tasks.
        /// </summary>
        private static bool CopyDirectoryOnlySync(string sourcePath, string targetPath)
        {
            try
            {
                string targetFolder = Path.GetDirectoryName(targetPath);
                Directory.CreateDirectory(targetFolder);

                string[] files = Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories);
                HashSet<string> createdDirs = new HashSet<string>();

                foreach (string file in files)
                {
                    string relativePath = file.Substring(sourcePath.Length + 1);
                    string targetFile = Path.Combine(targetFolder, relativePath);
                    string targetFolder2 = Path.GetDirectoryName(targetFile);

                    if (createdDirs.Add(targetFolder2))
                    {
                        Directory.CreateDirectory(targetFolder2);
                    }

                    try
                    {
                        File.Copy(file, targetFile, true);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Failed to copy file '{file}' to '{targetFile}': {ex.Message}");
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Error copying directory '{sourcePath}' to '{targetPath}': {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Processes a batch of files to copy with optimized refresh strategy.
        /// Files are processed in order: dependencies with meta, dependencies with GUID, dependencies without info,
        /// then main file, and finally scripts (to avoid domain reload until the end).
        /// </summary>
        private static Task<Dictionary<AssetInfo, string>> ProcessCopyBatch(
            List<CopyWorkItem> workItems,
            bool reimport,
            bool outOfProject,
            bool previewMode)
        {
            Dictionary<AssetInfo, string> results = new Dictionary<AssetInfo, string>();
            if (workItems == null || workItems.Count == 0) return Task.FromResult(results);


            // Single-pass categorization using ToLookup (avoids multiple iterations over workItems)
            ILookup<CopyCoordinator.FileCategory, CopyWorkItem> byCategory = workItems.ToLookup(w => w.Category);

            // Get ordered groups - each OrderBy creates a lazy IOrderedEnumerable, no allocation until enumerated
            IOrderedEnumerable<CopyWorkItem> scripts = byCategory[CopyCoordinator.FileCategory.Script].OrderBy(w => w.IsMainFile);
            IOrderedEnumerable<CopyWorkItem> hasMeta = byCategory[CopyCoordinator.FileCategory.HasMeta].OrderBy(w => w.IsMainFile);
            IOrderedEnumerable<CopyWorkItem> hasGuidNoMeta = byCategory[CopyCoordinator.FileCategory.HasGuidNoMeta].OrderBy(w => w.IsMainFile);
            IOrderedEnumerable<CopyWorkItem> noInfo = byCategory[CopyCoordinator.FileCategory.NoInfo].OrderBy(w => w.IsMainFile);

            // Build ordered non-scripts list: deps first (IsMainFile=false), then mains (IsMainFile=true)
            // Using Concat to chain enumerables lazily before final materialization
            List<CopyWorkItem> orderedNonScripts = hasMeta.Where(w => !w.IsMainFile)
                .Concat(hasGuidNoMeta.Where(w => !w.IsMainFile))
                .Concat(noInfo.Where(w => !w.IsMainFile))
                .Concat(hasMeta.Where(w => w.IsMainFile))
                .Concat(hasGuidNoMeta.Where(w => w.IsMainFile))
                .Concat(noInfo.Where(w => w.IsMainFile))
                .ToList();
            List<CopyWorkItem> orderedScripts = scripts.ToList();

            ApplyCaseSensitivePathGuard(orderedNonScripts, orderedScripts);

            // --- PHASE 1: Copy all non-script files (file I/O only, no refresh yet) ---
            // HasMeta files get their .meta copied alongside, so GUID fix + AssetOrigin injection
            // can happen immediately on the copied .meta without waiting for Unity to generate one.
            // HasGuidNoMeta/NoInfo files have no .meta yet — they need a Refresh first so Unity
            // generates the .meta, then we can patch GUID / inject origin.
            bool anyFilesCopied = false;
            bool needsMetaGenerationRefresh = false; // only true when HasGuidNoMeta or NoInfo files are actually copied
            List<CopyWorkItem> copiedHasMetaItems = new List<CopyWorkItem>();
            List<CopyWorkItem> copiedNeedsMetaItems = new List<CopyWorkItem>(); // HasGuidNoMeta + NoInfo that were actually copied
            HashSet<string> movedFromDirectories = new HashSet<string>(); // Track source directories for empty folder cleanup

            // Pre-create folders for reorganization BEFORE StartAssetEditing, since CreateFolder
            // operations are deferred inside that block and MoveAsset would fail.
            if (reimport && AI.Config.reorganizeOnReimport && !outOfProject && !previewMode)
            {
                HashSet<string> foldersToCreate = new HashSet<string>();
                CollectReorganizeFolders(orderedNonScripts, previewMode, foldersToCreate);
                CollectReorganizeFolders(orderedScripts, previewMode, foldersToCreate);
                foreach (string folder in foldersToCreate)
                {
                    EnsureAssetDatabaseFolder(folder);
                }
            }

            // CRITICAL: All file I/O inside Start/StopAssetEditing MUST be synchronous.
            // Using await inside this block would yield to the Unity main thread, allowing
            // another parallel task to call StartAssetEditing() and increment the global
            // editing counter. When this task resumes and calls StopAssetEditing(), the counter
            // won't reach zero, making all subsequent Refresh/ImportAsset calls no-ops.
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (CopyWorkItem item in orderedNonScripts)
                {
                    item.TargetPath = IOUtils.ToLongPath(AssetUtils.AddProjectRoot(IOUtils.ToShortPath(item.TargetPath)));

                    // Check for existing GUID and handle reorganization
                    if (TryResolveExistingAsset(item, outOfProject, previewMode, reimport, movedFromDirectories, out string resolvedPath))
                    {
                        results[item.Info] = resolvedPath;

                        // Reused files already have their .meta in the project; no refresh needed.
                        // Inject AssetOrigin if missing during explicit user imports (not in Packages/).
                        // In preview mode, never touch existing user .meta files — the file is only
                        // being reused as a reference, not actively imported by the user.
                        if (!previewMode && !item.IsInPackagesFolder && item.Category != CopyCoordinator.FileCategory.Script)
                        {
                            copiedHasMetaItems.Add(item); // treat as HasMeta since .meta exists on disk
                        }
                        continue;
                    }

                    // Skip file operations for Packages/ folder files (immutable)
                    if (item.IsInPackagesFolder)
                    {
                        continue;
                    }

                    bool isDirectory = Directory.Exists(item.SourcePath);
                    if (isDirectory)
                    {
                        CopyDirectoryOnlySync(item.SourcePath, item.TargetPath);
                        results[item.Info] = AssetUtils.RemoveProjectRoot(item.TargetPath);
                        anyFilesCopied = true;
                        // Directories have their own .meta file that Unity generates - need refresh + origin injection
                        copiedNeedsMetaItems.Add(item);
                        needsMetaGenerationRefresh = true;
                        continue;
                    }

                    if (!CopyFileOnlySync(item.SourcePath, item.TargetPath))
                    {
                        Debug.LogError($"Failed to copy '{item.SourcePath}' to '{item.TargetPath}'");
                        continue;
                    }

                    // Copy meta file if available (HasMeta category)
                    if (item.Category == CopyCoordinator.FileCategory.HasMeta)
                    {
                        CopyMetaOnlySync(item.SourcePath + ".meta", item.TargetPath + ".meta");
                        copiedHasMetaItems.Add(item);
                    }
                    else
                    {
                        // HasGuidNoMeta or NoInfo — no .meta on disk yet, Unity must generate it
                        copiedNeedsMetaItems.Add(item);
                        needsMetaGenerationRefresh = true;
                    }

                    results[item.Info] = AssetUtils.RemoveProjectRoot(item.TargetPath);
                    anyFilesCopied = true;
                }

                // While still inside StartAssetEditing (suppressing per-file imports):
                // HasMeta items already have their .meta on disk from the copy above.
                // We can safely patch GUIDs and inject AssetOrigin now — it's pure file I/O
                // on files we just wrote, and Unity hasn't imported them yet.
                foreach (CopyWorkItem item in copiedHasMetaItems)
                {
                    if (!results.TryGetValue(item.Info, out string resultPath)) continue;

                    if (IsInPackagesFolder(resultPath)) continue;
                    if (IsInTextMeshProFolder(resultPath)) continue;

                    string targetMetaPath = AssetUtils.AddProjectRoot(resultPath) + ".meta";

                    // GUID is already correct in copied .meta for HasMeta, but ensure it just in case
                    if (!string.IsNullOrEmpty(item.Info.Guid))
                    {
                        EnsureCorrectGuid(targetMetaPath, item.Info.Guid);
                    }

                    InjectAssetOriginSync(targetMetaPath, item.Info, outOfProject);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            // Clean up empty directories after files were moved during reorganization
            if (movedFromDirectories.Count > 0 && AI.Config.deleteEmptyFoldersOnReorganize)
            {
                CleanEmptyFoldersAfterMove(movedFromDirectories);
            }

            // --- PHASE 2: Refresh only if files without .meta were copied ---
            // Unity needs to generate .meta files for HasGuidNoMeta and NoInfo categories.
            // HasMeta files already have complete .meta files (with correct GUID + AssetOrigin)
            // and don't need a separate refresh before meta manipulation.
            if (needsMetaGenerationRefresh)
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }

            // --- PHASE 3: Patch Unity-generated .meta files for HasGuidNoMeta/NoInfo items ---
            // After the refresh above, Unity has created .meta files for these items.
            // Now we can fix GUIDs and inject AssetOrigin in a single pass.
            bool anyMetaPatched = false;
            foreach (CopyWorkItem item in copiedNeedsMetaItems)
            {
                if (!results.ContainsKey(item.Info)) continue;

                string resultPath = results[item.Info];
                if (IsInPackagesFolder(resultPath)) continue;
                if (IsInTextMeshProFolder(resultPath)) continue;

                string targetMetaPath = AssetUtils.AddProjectRoot(resultPath) + ".meta";

                // Fix GUID for HasGuidNoMeta files (NoInfo files have no known GUID to set)
                if (item.Category == CopyCoordinator.FileCategory.HasGuidNoMeta)
                {
                    if (EnsureCorrectGuid(targetMetaPath, item.Info.Guid))
                    {
                        anyMetaPatched = true;
                    }
                }

                // Inject AssetOrigin for all (sync to avoid yielding between Phases 2-4,
                // which could let another parallel task enter StartAssetEditing)
                if (InjectAssetOriginSync(targetMetaPath, item.Info, outOfProject))
                {
                    anyMetaPatched = true;
                }
            }

            // --- PHASE 4: Single final refresh for all non-script files ---
            // Covers: HasMeta files that need importing, plus any .meta patches from Phase 3.
            // When only HasMeta files exist and no meta-less files were copied, this is the
            // only refresh that fires (down from 3 in the previous implementation).
            if (anyFilesCopied || anyMetaPatched)
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }

            // --- PHASE 5: Process scripts LAST (will trigger domain reload) ---
            // CRITICAL: Same as Phase 1 — all file I/O must be synchronous to prevent
            // editing counter nesting from parallel async tasks.
            if (orderedScripts.Any())
            {
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (CopyWorkItem item in orderedScripts)
                    {
                        item.TargetPath = IOUtils.ToLongPath(AssetUtils.AddProjectRoot(IOUtils.ToShortPath(item.TargetPath)));

                        // Check for existing GUID and handle reorganization
                        if (TryResolveExistingAsset(item, outOfProject, previewMode, reimport, movedFromDirectories, out string scriptResolvedPath))
                        {
                            results[item.Info] = scriptResolvedPath;

                            // Warn if script GUID exists in Packages/ but is being requested as dependency
                            if (item.IsInPackagesFolder && !item.IsMainFile)
                            {
                                Debug.LogWarning($"Script dependency '{item.Info.FileName}' from '{item.Info.SafeName}' already exists in immutable Packages/ folder at '{scriptResolvedPath}'. Using existing file as-is. If GUID mismatch occurs, reinstall the package.");
                            }
                            continue;
                        }

                        // Skip file operations for Packages/ folder files (immutable)
                        if (item.IsInPackagesFolder)
                        {
                            continue;
                        }

                        if (!CopyFileOnlySync(item.SourcePath, item.TargetPath))
                        {
                            Debug.LogError($"Failed to copy script '{item.SourcePath}' to '{item.TargetPath}'");
                            continue;
                        }

                        // Copy meta file if available
                        string sourceMetaPath = item.SourcePath + ".meta";
                        if (File.Exists(sourceMetaPath))
                        {
                            CopyMetaOnlySync(sourceMetaPath, item.TargetPath + ".meta");
                        }

                        results[item.Info] = AssetUtils.RemoveProjectRoot(item.TargetPath);
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }

                // Clean up empty directories from script moves BEFORE domain reload
                // (must happen here since Refresh() below will abort execution)
                if (movedFromDirectories.Count > 0 && AI.Config.deleteEmptyFoldersOnReorganize)
                {
                    CleanEmptyFoldersAfterMove(movedFromDirectories);
                }

                // Scripts refresh - THIS WILL TRIGGER DOMAIN RELOAD
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }

            return Task.FromResult(results);
        }

        /// <summary>
        /// Tries to resolve an existing asset by GUID and handles reorganization if needed.
        /// </summary>
        /// <param name="item">The work item to process</param>
        /// <param name="outOfProject">Whether importing outside project</param>
        /// <param name="previewMode">Whether in preview mode</param>
        /// <param name="reimport">Whether this is a reimport</param>
        /// <param name="movedFromDirectories">Set to track source directories for empty folder cleanup</param>
        /// <param name="resolvedPath">The resolved path (relative) if item was resolved</param>
        /// <returns>True if item was resolved and should skip copying, false if copying should proceed</returns>
        private static bool TryResolveExistingAsset(
            CopyWorkItem item,
            bool outOfProject,
            bool previewMode,
            bool reimport,
            HashSet<string> movedFromDirectories,
            out string resolvedPath)
        {
            resolvedPath = null;

            // No GUID or importing out of project - cannot resolve
            if (string.IsNullOrEmpty(item.Info.Guid) || outOfProject) return false;

            string existing = GetExistingAssetPathForGuid(item.Info.Guid, previewMode);
            if (existing == null) return false;

            // Files in Packages/ folder are always immutable - reuse as-is
            bool isInPackages = IsInPackagesFolder(existing);
            item.IsInPackagesFolder = isInPackages;

            if (previewMode || !reimport || isInPackages)
            {
                resolvedPath = AssetUtils.RemoveProjectRoot(existing);
                return true; // Skip copying - use existing
            }

            // Handle reimport: either move to target structure or overwrite at existing location
            string existingRelative = AssetUtils.RemoveProjectRoot(existing);
            string targetRelative = AssetUtils.RemoveProjectRoot(item.TargetPath);

            // Skip move if file is already at the target position
            if (string.Equals(existingRelative, targetRelative, StringComparison.OrdinalIgnoreCase))
            {
                item.TargetPath = existing;
            }
            else if (AI.Config.reorganizeOnReimport)
            {
                // Move file to target import structure, preserving GUID/references
                // (folders are pre-created before StartAssetEditing block)
                string moveError = AssetDatabase.MoveAsset(existingRelative, targetRelative);
                if (!string.IsNullOrEmpty(moveError))
                {
                    Debug.LogWarning($"Failed to move '{existingRelative}' to '{targetRelative}': {moveError}. Falling back to overwrite at existing location.");
                    item.TargetPath = existing;
                }
                else
                {
                    // Track source directory for potential empty folder cleanup
                    string sourceDir = Path.GetDirectoryName(existingRelative);
                    if (!string.IsNullOrEmpty(sourceDir)) movedFromDirectories.Add(sourceDir);
                }
            }
            else
            {
                // Default behavior: overwrite at existing location
                item.TargetPath = existing;
            }

            return false; // Continue with copying (at potentially updated TargetPath)
        }

        /// <summary>
        /// Collects folders that need to be pre-created for reorganization.
        /// </summary>
        private static void CollectReorganizeFolders(
            IEnumerable<CopyWorkItem> items,
            bool previewMode,
            HashSet<string> foldersToCreate)
        {
            foreach (CopyWorkItem item in items)
            {
                if (string.IsNullOrEmpty(item.Info.Guid)) continue;
                string existing = GetExistingAssetPathForGuid(item.Info.Guid, previewMode);
                if (existing == null || IsInPackagesFolder(existing)) continue;

                string targetPath = IOUtils.ToLongPath(AssetUtils.AddProjectRoot(IOUtils.ToShortPath(item.TargetPath)));
                string existingRelative = AssetUtils.RemoveProjectRoot(existing);
                string targetRelative = AssetUtils.RemoveProjectRoot(targetPath);

                if (!string.Equals(existingRelative, targetRelative, StringComparison.OrdinalIgnoreCase))
                {
                    string targetFolder = Path.GetDirectoryName(targetRelative);
                    if (!string.IsNullOrEmpty(targetFolder)) foldersToCreate.Add(targetFolder);
                }
            }
        }

        #endregion

        #region Public API

        public static AssetInfo GetPackage(int packageId)
        {
            List<AssetInfo> allAssets = Load();
            return allAssets.FirstOrDefault(a => a.AssetId == packageId && a.ParentId == 0);
        }

        public static async Task<bool> DownloadPackageAsync(AssetInfo info, CancellationToken ct = default)
        {
            if (info == null) return false;
            if (info.IsDownloaded) return true;

            if (info.PackageDownloader == null)
            {
                info.PackageDownloader = new AssetDownloader(info);
            }

            if (!info.PackageDownloader.IsDownloadSupported()) return false;

            info.PackageDownloader.Download(true);

            while (!ct.IsCancellationRequested)
            {
                info.PackageDownloader.RefreshState();
                AssetDownloader.State state = info.PackageDownloader.GetState().state;

                if (state == AssetDownloader.State.Downloaded || state == AssetDownloader.State.UpdateAvailable) return true;
                if (state == AssetDownloader.State.Unavailable || state == AssetDownloader.State.Paused) return false;

                await Task.Delay(500, ct);
            }

            return false;
        }

        public static InventoryStats GetInventoryStats()
        {
            List<AssetInfo> allAssets = Load();
            int totalFiles = DBAdapter.DB.ExecuteScalar<int>("select count(*) from AssetFile");

            int assetStore = 0;
            int custom = 0;
            int directory = 0;
            int registry = 0;
            int archive = 0;
            int assetManager = 0;
            int indexed = 0;
            int sub = 0;
            int deprecated = 0;
            int abandoned = 0;
            int excluded = 0;
            int backup = 0;
            int ai = 0;
            int customPackages = 0;

            foreach (AssetInfo a in allAssets)
            {
                if (a.ParentId > 0)
                {
                    sub++;
                    continue;
                }

                if (a.IsIndexed) indexed++;
                if (a.IsDeprecated) deprecated++;
                if (a.IsAbandoned) abandoned++;
                if (a.Exclude) excluded++;
                if (a.Backup) backup++;
                if (a.UseAI) ai++;
                if (a.AssetSource == Asset.Source.CustomPackage || a.SafeName == Asset.NONE) customPackages++;

                switch (a.AssetSource)
                {
                    case Asset.Source.AssetStorePackage: assetStore++; break;
                    case Asset.Source.CustomPackage: custom++; break;
                    case Asset.Source.Directory: directory++; break;
                    case Asset.Source.RegistryPackage: registry++; break;
                    case Asset.Source.Archive: archive++; break;
                    case Asset.Source.AssetManager: assetManager++; break;
                }
            }

            int totalPackages = assetStore + custom + directory + registry + archive + assetManager;
            int indexable = totalPackages - abandoned - registry - excluded;
            if (indexable < indexed) indexable = indexed;

            return new InventoryStats
            {
                TotalPackages = totalPackages,
                IndexedPackages = indexed,
                IndexablePackages = indexable,
                SubPackages = sub,
                TotalFiles = totalFiles,
                PurchasedAssets = CountPurchasedAssets(allAssets),
                DeprecatedPackages = deprecated,
                AbandonedPackages = abandoned,
                ExcludedPackages = excluded,
                BackupPackages = backup,
                AIPackages = ai,
                CustomPackages = customPackages,
                RegistryPackages = registry,
                BySource = new InventoryStats.SourceBreakdown
                {
                    AssetStore = assetStore,
                    Custom = custom,
                    Directory = directory,
                    Registry = registry,
                    Archive = archive,
                    AssetManager = assetManager
                }
            };
        }

        public static List<AssetInfo> LoadPackagesForExport(string searchQuery = null, HashSet<int> packageIdFilter = null)
        {
            PackageSearch.Options options = PackageSearch.Options.CreateDefault();
            if (!string.IsNullOrEmpty(searchQuery)) options.SearchPhrase = searchQuery;

            PackageSearch.Result result = PackageSearch.Execute(options);
            List<AssetInfo> packages = result.Packages ?? new List<AssetInfo>();

            if (packageIdFilter != null && packageIdFilter.Count > 0)
            {
                packages = packages.Where(p => packageIdFilter.Contains(p.Id)).ToList();
            }

            return packages;
        }

        #endregion
    }
}
