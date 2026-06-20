using AudioTool;
using Automator;
using ImpossibleRobert.Common;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor.PackageManager;
using UnityEngine;

namespace AssetInventory
{
    public abstract class AssetImporter : ActionProgress
    {
        protected static bool CanDownload(AssetInfo info)
        {
            if (info == null) return false;

            // check if metadata is already available for triggering and monitoring
            if (string.IsNullOrWhiteSpace(info.OriginalLocation)) return false;
            if (info.CurrentSubState == Asset.SubState.Outdated) return false;
            if (info.IsAbandoned) return false;

            // skip if too large or unknown download size yet
            if (AI.Config.limitAutoDownloads && (info.PackageSize == 0 || Mathf.RoundToInt(info.PackageSize / 1024f / 1024f) >= AI.Config.downloadLimit)) return false;

            AI.GetObserver().Attach(info);
            if (!info.PackageDownloader.IsDownloadSupported()) return false;

            return true;
        }

        protected IEnumerator DownloadAsset(AssetInfo info)
        {
            string oldMain = CurrentMain;

            // Ensure PackageDownloader is attached
            if (info.PackageDownloader == null)
            {
                AI.GetObserver().Attach(info);
                if (info.PackageDownloader == null)
                {
                    Debug.LogError($"Failed to attach PackageDownloader for '{info.GetDisplayName()}'. Cannot proceed with download.");
                    yield break;
                }
            }

            // Verify download is supported
            if (!info.PackageDownloader.IsDownloadSupported())
            {
                Debug.LogError($"Download not supported for '{info.GetDisplayName()}'.");
                yield break;
            }

            // refresh in case parallel download has finished by now
            info.Refresh();

            // Refresh state with error handling (outside try-catch to avoid yield issues)
            bool refreshFailed = false;
            try
            {
                info.PackageDownloader.RefreshState();
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to refresh download state for '{info.GetDisplayName()}': {e.Message}");
                refreshFailed = true;
            }

            if (refreshFailed) yield break;

            if (info.IsDownloading() || !info.IsDownloaded)
            {
                CurrentMain = $"Downloading {info.GetDisplayName()}";
                CurrentSub = IOUtils.RemoveInvalidChars(info.GetDisplayName());
                SubCount = 0;
                SubProgress = 0;

                // Start download only if not already downloading (avoid race condition)
                bool shouldStartDownload = !info.IsDownloading();
                bool downloadStartFailed = false;

                if (shouldStartDownload)
                {
                    try
                    {
                        info.PackageDownloader.Download(true);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"Failed to start download for '{info.GetDisplayName()}': {e.Message}. Check if ThreadUtils is initialized and you're on the main thread.");
                        downloadStartFailed = true;
                    }

                    if (downloadStartFailed) yield break;

                    // Give download a moment to start before checking state
                    yield return null;
                }

                do
                {
                    if (CancellationRequested) break; // download will finish in that case and not be removed

                    // Update progress with error handling
                    try
                    {
                        AssetDownloadState state = info.PackageDownloader.GetState();
                        SubCount = Mathf.RoundToInt(state.bytesTotal / 1024f / 1024f);
                        SubProgress = Mathf.RoundToInt(state.bytesDownloaded / 1024f / 1024f);
                        if (SubProgress > SubCount) SubCount = SubProgress; // in case download is bigger than info about it
                        if (SubCount == 0) SubCount = SubProgress; // in case total size was not available yet
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"Error checking download progress for '{info.GetDisplayName()}': {e.Message}");
                    }

                    yield return null;
                } while (info.IsDownloading());
            }
            CurrentMain = oldMain;
            if (CancellationRequested) yield break;

            // Finalize download with error handling
            try
            {
                info.SetLocation(info.PackageDownloader.GetAsset().Location);
                info.Refresh();
                info.PackageDownloader.RefreshState();
            }
            catch (Exception e)
            {
                Debug.LogError($"Error finalizing download for '{info.GetDisplayName()}': {e.Message}");
            }

            if (!info.IsDownloaded)
            {
                Debug.LogError($"Downloading '{info.GetRoot()}' failed. Continuing with next package.");
            }

            SubProgress = SubCount; // ensure 100% progress
        }

        protected static IEnumerator RemoveDownload(Asset asset)
        {
            if (!AI.Config.keepAutoDownloads)
            {
                // perform backup before deleting, as otherwise the file would not be considered
                if (AI.Actions.CreateBackups)
                {
                    AssetBackup backup = new AssetBackup();
                    Task task2 = backup.Backup(asset.Id);
                    yield return new WaitWhile(() => !task2.IsCompleted);
                }

                IOUtils.TryDeleteFile(asset.GetLocation(true));
            }
        }

        protected static bool ApplyPackageTags(FolderSpec spec, Asset asset, bool fromAssetStore = false)
        {
            bool somethingAdded = false;

            if (spec.assignTag && !string.IsNullOrWhiteSpace(spec.tag))
            {
                string[] tags = StringUtils.Split(spec.tag, new[] {';', ','});
                foreach (string tag in tags)
                {
                    if (string.IsNullOrWhiteSpace(tag)) continue;

                    if (Tagging.AddAssignment(asset.Id, tag, TagAssignment.Target.Package, fromAssetStore)) somethingAdded = true;
                }
            }

            return somethingAdded;
        }

        protected static bool HasNoIndexTag(int assetId)
        {
            List<TagInfo> tags = Tagging.GetPackageTags(assetId);
            return tags != null && tags.Any(t => string.Equals(t.Name, "noindex", StringComparison.OrdinalIgnoreCase));
        }

        protected static bool HasNoIndexTag(Asset asset)
        {
            // Check asset itself
            if (HasNoIndexTag(asset.Id)) return true;

            // Check parent if this is a sub-package
            if (asset.ParentId > 0 && HasNoIndexTag(asset.ParentId)) return true;

            return false;
        }

        /// <summary>
        /// Checks if a path should be ignored during indexing.
        /// </summary>
        /// <param name="path">The path to check (can be relative or absolute)</param>
        /// <param name="normalize">Whether to normalize path separators to forward slashes</param>
        /// <returns>True if the path should be ignored</returns>
        internal static bool IsIgnoredPath(string path, bool normalize)
        {
            if (normalize) path = path.Replace('\\', '/');

            // Library folder of complete project
            string[] parts = path.Split('/');
            bool isLibrary = (parts.Length > 0 && string.Equals(parts[0], "Library", StringComparison.OrdinalIgnoreCase))
                || (parts.Length > 1 && string.Equals(parts[1], "Library", StringComparison.OrdinalIgnoreCase));

            // skip MacOS resource fork folders, git folders, hidden directories (dot-prefixed)
            return isLibrary
                || parts.Any(p => p.StartsWith("."))
                || path.Contains("__MACOSX", StringComparison.OrdinalIgnoreCase)
                || path.Contains(".git/", StringComparison.OrdinalIgnoreCase)
                || path.Contains(".plastic/", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsIgnoredPathBelowScanRoot(string path, string scanRoot, bool normalize = true)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (string.IsNullOrEmpty(scanRoot)) return IsIgnoredPath(path, normalize);

            string normalizedPath = normalize ? IOUtils.NormalizePath(path) : path.Replace('\\', '/').TrimEnd('/');
            string normalizedRoot = normalize ? IOUtils.NormalizePath(scanRoot) : scanRoot.Replace('\\', '/').TrimEnd('/');

            if (string.IsNullOrEmpty(normalizedPath) || string.IsNullOrEmpty(normalizedRoot)) return IsIgnoredPath(path, normalize);

            if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)) return false;

            string rootPrefix = normalizedRoot + "/";
            if (normalizedPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string relativePath = normalizedPath.Substring(rootPrefix.Length);
                return IsIgnoredPath(relativePath, false);
            }

            return IsIgnoredPath(path, normalize);
        }

        internal static bool IsExcludedDirectory(string path, string[] excludedDirectories, bool normalize = true)
        {
            if (excludedDirectories == null || excludedDirectories.Length == 0) return false;
            if (normalize) path = path.Replace('\\', '/');

            string[] parts = path.Split('/');
            return parts.Any(p => excludedDirectories.Any(e => string.Equals(p, e, StringComparison.OrdinalIgnoreCase)));
        }

        protected static void RemoveWorkFolder(Asset asset, string tempPath)
        {
            // remove files again, no need to wait
            if (asset != null && !asset.KeepExtracted)
            {
                Task.Run(() =>
                {
                    try
                    {
                        Directory.Delete(tempPath, true);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"Could not clean up work folder '{tempPath}': {e.Message}");
                    }
                });
            }
        }

        protected static void MarkDone(Asset asset)
        {
            // update only individual fields to not override potential changes in metadata during indexing
            asset.CurrentState = Asset.State.Done;
            DBAdapter.DB.Execute("update Asset set CurrentState=? where Id=?", asset.CurrentState, asset.Id);
        }

        protected static void MarkInProcess(Asset asset)
        {
            asset.CurrentState = Asset.State.InProcess;
            DBAdapter.DB.Execute("update Asset set CurrentState=? where Id=?", asset.CurrentState, asset.Id);
        }

        internal static void ApplyOverrides(Asset asset)
        {
            string overFile = asset.GetLocation(true) + ".overrides.json";
            if (File.Exists(overFile))
            {
                try
                {
                    PackageOverrides overrides = JsonConvert.DeserializeObject<PackageOverrides>(File.ReadAllText(overFile));
                    if (overrides != null)
                    {
                        if (overrides.foreignId > 0) asset.ForeignId = overrides.foreignId;
                        if (!string.IsNullOrWhiteSpace(overrides.displayName)) asset.DisplayName = overrides.displayName;
                        if (!string.IsNullOrWhiteSpace(overrides.displayCategory)) asset.DisplayCategory = overrides.displayCategory;
                        if (!string.IsNullOrWhiteSpace(overrides.safeCategory)) asset.SafeCategory = overrides.safeCategory;
                        if (!string.IsNullOrWhiteSpace(overrides.displayPublisher)) asset.DisplayPublisher = overrides.displayPublisher;
                        if (!string.IsNullOrWhiteSpace(overrides.safePublisher)) asset.SafePublisher = overrides.safePublisher;
                        if (overrides.publisherId > 0) asset.PublisherId = overrides.publisherId;
                        if (!string.IsNullOrWhiteSpace(overrides.slug)) asset.Slug = overrides.slug;
                        if (overrides.revision > 0) asset.Revision = overrides.revision;
                        if (!string.IsNullOrWhiteSpace(overrides.description)) asset.Description = overrides.description;
                        if (!string.IsNullOrWhiteSpace(overrides.keyFeatures)) asset.KeyFeatures = overrides.keyFeatures;
                        if (!string.IsNullOrWhiteSpace(overrides.compatibilityInfo)) asset.CompatibilityInfo = overrides.compatibilityInfo;
                        if (!string.IsNullOrWhiteSpace(overrides.supportedUnityVersions)) asset.SupportedUnityVersions = overrides.supportedUnityVersions;
                        if (!string.IsNullOrWhiteSpace(overrides.keywords)) asset.Keywords = overrides.keywords;
                        if (!string.IsNullOrWhiteSpace(overrides.version)) asset.Version = overrides.version;
                        if (!string.IsNullOrWhiteSpace(overrides.latestVersion)) asset.LatestVersion = overrides.latestVersion;
                        if (!string.IsNullOrWhiteSpace(overrides.license)) asset.License = overrides.license;
                        if (!string.IsNullOrWhiteSpace(overrides.licenseLocation)) asset.LicenseLocation = overrides.licenseLocation;
                        if (overrides.purchaseDate != default(DateTime)) asset.PurchaseDate = overrides.purchaseDate;
                        if (overrides.firstRelease != default(DateTime)) asset.FirstRelease = overrides.firstRelease;
                        if (overrides.lastRelease != default(DateTime)) asset.LastRelease = overrides.lastRelease;
                        if (overrides.assetRating > 0) asset.AssetRating = overrides.assetRating;
                        if (overrides.ratingCount > 0) asset.RatingCount = overrides.ratingCount;
                        if (overrides.hotness > 0) asset.Hotness = overrides.hotness;
                        if (overrides.priceEur > 0) asset.PriceEur = overrides.priceEur;
                        if (overrides.priceUsd > 0) asset.PriceUsd = overrides.priceUsd;
                        if (overrides.priceCny > 0) asset.PriceCny = overrides.priceCny;
                        if (!string.IsNullOrWhiteSpace(overrides.requirements)) asset.Requirements = overrides.requirements;
                        if (!string.IsNullOrWhiteSpace(overrides.releaseNotes)) asset.ReleaseNotes = overrides.releaseNotes;
                        if (overrides.officialState > 0)
                        {
                            // Convert int to enum (0 = None, 1 = Published, 2 = Deprecated, 3 = Disabled)
                            asset.OfficialState = (Asset.OfficialStateType)overrides.officialState;
                        }
                        if (!string.IsNullOrWhiteSpace(overrides.registry)) asset.Registry = overrides.registry;
                        if (!string.IsNullOrWhiteSpace(overrides.repository)) asset.Repository = overrides.repository;

                        // booleans are hard to import correctly, assume setting to positive only
                        if (overrides.bIRPCompatible) asset.BIRPCompatible = true;
                        if (overrides.hDRPCompatible) asset.HDRPCompatible = true;
                        if (overrides.uRPCompatible) asset.URPCompatible = true;

                        if (overrides.tags != null && overrides.tags.Length > 0)
                        {
                            foreach (string tag in overrides.tags)
                            {
                                Tagging.AddAssignment(new AssetInfo(asset), tag, TagAssignment.Target.Package);
                            }
                        }

                        Persist(asset);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"Could not read overrides file '{overFile}': {e.Message}");
                }
            }
        }

        protected static Asset Fetch(Asset asset)
        {
            if (asset.Id > 0)
            {
                return DBAdapter.DB.Find<Asset>(asset.Id);
            }
            if (asset.AssetSource == Asset.Source.RegistryPackage)
            {
                if (asset.PackageSource == PackageSource.Local)
                {
                    return DBAdapter.DB.Find<Asset>(a => a.PackageSource == PackageSource.Local && a.Location == asset.Location);
                }
                return DBAdapter.DB.Find<Asset>(a => a.SafeName == asset.SafeName);
            }
            if (asset.AssetSource == Asset.Source.Archive)
            {
                return DBAdapter.DB.Find<Asset>(a => a.ParentId == asset.ParentId && a.Location == asset.Location);
            }
            if (asset.AssetSource == Asset.Source.AssetManager)
            {
                return DBAdapter.DB.Find<Asset>(a => a.ParentId == asset.ParentId && a.SafeName == asset.SafeName);
            }

            Asset result = null;

            // main index is location + foreign Id since Asset Store supports multiple versions under the same location potentially
            // cater for cases when folder capitalization changes due to metadata changes

            // use most specific data if available to differentiate between multi-version assets
            // RW: raw SQL with COLLATE NOCASE avoids LINQ translation + per-row ToLower() allocations
            //     while preserving the original case on disk for Location values.
            // Note: IDatabaseConnection exposes Query<T> but not FindWithQuery; use Query + FirstOrDefault.
            if (asset.ForeignId > 0 && !string.IsNullOrEmpty(asset.Location))
            {
                result = DBAdapter.DB.Query<Asset>(
                    "select * from Asset where ForeignId = ? and ParentId = ? and Location = ? COLLATE NOCASE limit 1",
                    asset.ForeignId, asset.ParentId, asset.Location).FirstOrDefault();
            }

            // check for Id only if from Asset Store with no location yet
            if (result == null && asset.ForeignId > 0)
            {
                result = DBAdapter.DB.Query<Asset>(
                    "select * from Asset where ForeignId = ? and Location is null limit 1",
                    asset.ForeignId).FirstOrDefault();
            }

            // check for location only if not from Asset Store
            if (result == null && asset.ForeignId <= 0 && !string.IsNullOrEmpty(asset.Location))
            {
                result = DBAdapter.DB.Query<Asset>(
                    "select * from Asset where ParentId = ? and Location = ? COLLATE NOCASE limit 1",
                    asset.ParentId, asset.Location).FirstOrDefault();
            }

            return result;
        }

        protected static bool Exists(AssetFile file)
        {
            if (string.IsNullOrEmpty(file.Guid))
            {
                return DBAdapter.DB.ExecuteScalar<int>("select count(*) from AssetFile where AssetId = ? and Path = ? COLLATE NOCASE limit 1", file.AssetId, file.Path) > 0;
            }
            return DBAdapter.DB.ExecuteScalar<int>("select count(*) from AssetFile where AssetId = ? and Guid = ? limit 1", file.AssetId, file.Guid) > 0;
        }

        protected static AssetFile Fetch(AssetFile file)
        {
            if (string.IsNullOrEmpty(file.Guid))
            {
                return DBAdapter.DB.Query<AssetFile>(
                    "select * from AssetFile where AssetId = ? and Path = ? COLLATE NOCASE limit 1",
                    file.AssetId, file.Path).FirstOrDefault();
            }
            return DBAdapter.DB.Find<AssetFile>(f => f.AssetId == file.AssetId && f.Guid == file.Guid);
        }

        protected static AssetFile Fetch(AssetFile file, IEnumerable<AssetFile> existing)
        {
            if (string.IsNullOrEmpty(file.Guid))
            {
                return existing.FirstOrDefault(f => f.AssetId == file.AssetId && f.Path == file.Path);
            }
            return existing.FirstOrDefault(f => f.AssetId == file.AssetId && f.Guid == file.Guid);
        }

        protected static AssetFile Fetch(AssetFile file, Dictionary<string, List<AssetFile>> existingByGuid, Dictionary<(string, int), AssetFile> existingByPathAndAssetId)
        {
            if (string.IsNullOrEmpty(file.Guid))
            {
                if (existingByPathAndAssetId.TryGetValue((file.Path, file.AssetId), out AssetFile assetFile))
                {
                    return assetFile;
                }
            }
            else
            {
                if (existingByGuid.TryGetValue(file.Guid, out List<AssetFile> filesByGuid))
                {
                    return filesByGuid.FirstOrDefault(f => f.AssetId == file.AssetId);
                }
            }

            return null;
        }

        protected static Dictionary<string, List<AssetFile>> ToGuidDict(IEnumerable<AssetFile> files)
        {
            return files
                .Where(f => !string.IsNullOrEmpty(f.Guid))
                .GroupBy(f => f.Guid)
                .ToDictionary(g => g.Key, g => g.ToList());
        }

        public static Dictionary<int, AssetFile> ToIdDict(IEnumerable<AssetFile> files)
        {
            return files
                .GroupBy(f => f.Id)
                .ToDictionary(g => g.Key, g => g.First());
        }

        protected static Dictionary<(string Path, int AssetId), AssetFile> ToPathIdDict(IEnumerable<AssetFile> files)
        {
            return files
                .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(f => (f.First().Path, f.First().AssetId), f => f.First());
        }

        public static void Persist(Asset asset)
        {
            if (asset.Id > 0)
            {
                DBAdapter.DB.Update(asset);
                return;
            }

            Asset existing = Fetch(asset);
            if (existing != null)
            {
                asset.Id = existing.Id;
                if (asset.ForeignId > 0) existing.ForeignId = asset.ForeignId;
                existing.Version = asset.Version;
                existing.SafeCategory = asset.SafeCategory;
                existing.SafePublisher = asset.SafePublisher;
                existing.CurrentState = asset.CurrentState;
                existing.AssetSource = asset.AssetSource;
                existing.PackageSize = asset.PackageSize;
                existing.SetLocation(asset.Location);

                DBAdapter.DB.Update(existing);
            }
            else
            {
                DBAdapter.DB.Insert(asset);
            }
        }

        public static string ValidatePreviewFile(AssetFile file, string previewFolder, bool nullOnError = true)
        {
            string previewFile = file.GetPreviewFile(previewFolder);
            if (!File.Exists(previewFile))
            {
                if (file.PreviewState != AssetFile.PreviewOptions.Redo && file.PreviewState != AssetFile.PreviewOptions.RedoMissing)
                {
                    Debug.LogWarning($"Preview file for '{file}' does not exist anymore. Scheduling it for recreation.");
                    DBAdapter.DB.Execute("update AssetFile set PreviewState=? where Id=?", AssetFile.PreviewOptions.RedoMissing, file.Id);
                    file.PreviewState = AssetFile.PreviewOptions.RedoMissing;
                }
                if (nullOnError) return null;
            }
            return previewFile;
        }

        protected static void Persist(AssetFile file)
        {
            if (file.Id > 0)
            {
                DBAdapter.DB.Update(file);
                return;
            }

            AssetFile existing = Fetch(file);
            if (existing != null)
            {
                file.Id = existing.Id;
                DBAdapter.DB.Update(file);
            }
            else
            {
                DBAdapter.DB.Insert(file);
            }
        }

        protected static void UpdateOrInsert(Asset asset)
        {
            if (asset.Id > 0)
            {
                DBAdapter.DB.Update(asset);
            }
            else
            {
                DBAdapter.DB.Insert(asset);
            }
        }

        protected static async Task ProcessMediaAttributes(string file, AssetFile info, Asset asset)
        {
            // special processing for supported file types
            if (ImageUtils.SYSTEM_IMAGE_TYPES.Contains(info.Type))
            {
                Tuple<int, int> dimensions = ImageUtils.GetDimensions(file, false, "." + info.Type);
                if (dimensions != null)
                {
                    info.Width = dimensions.Item1;
                    info.Height = dimensions.Item2;
                }
            }

            if (AI.IsFileType(info.FileName, AI.AssetGroup.Audio))
            {
                // Try lightweight header parsing first (WAV, AIFF, OGG) - avoids expensive UnityWebRequest/FMOD loading.
                // This is also thread-safe unlike the UnityWebRequest fallback.
                AudioHeaderReader.AudioHeaderInfo headerInfo = AudioHeaderReader.ReadHeader(file);
                if (headerInfo != null && headerInfo.Duration > 0)
                {
                    info.Length = headerInfo.Duration;
                    return;
                }

                // Fall back to full Unity audio loading for unsupported formats (MP3, tracker formats, etc.)
                // or when header parsing fails (corrupt/non-standard files)
                string contentFile = asset.AssetSource != Asset.Source.Directory ? await Assets.EnsureMaterialized(asset, info) : file;
                AudioClip clip = null;
                try
                {
                    clip = await AssetUtils.LoadAudioFromFile(contentFile);
                    if (clip != null)
                    {
                        info.Length = clip.length;
                    }
                }
                catch
                {
                    if (AI.Config.LogAudioParsing)
                    {
                        Debug.LogWarning($"Audio file '{Path.GetFileName(file)}' from {info} seems to have incorrect format.");
                    }
                }
                finally
                {
                    if (clip != null)
                    {
                        clip.UnloadAudioData();
                        UnityEngine.Object.DestroyImmediate(clip);
                    }
                }
            }
        }

        protected static FolderSpec GetDefaultImportSpec()
        {
            return new FolderSpec
            {
                pattern = "*.*",
                createPreviews = true,
                folderType = 1,
                scanFor = 7
            };
        }
    }
}
