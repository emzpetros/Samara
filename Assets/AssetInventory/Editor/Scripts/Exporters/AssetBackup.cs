using Automator;
using ImpossibleRobert.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    public sealed class AssetBackup : ActionProgress
    {
        private Dictionary<int, List<BackupInfo>> _assetVersions;
        private static Dictionary<int, List<BackupInfo>> _staticCache;
        private static DateTime _cacheTime;
        private static readonly TimeSpan CACHE_DURATION = TimeSpan.FromSeconds(5); // Cache for 5 seconds

        public AssetBackup()
        {
            _assetVersions = new Dictionary<int, List<BackupInfo>>();
        }

        private void Refresh()
        {
            _assetVersions = GatherState();
        }

        /// <summary>
        /// Clears the static backup state cache.
        /// Call this when switching database backends to prevent stale data.
        /// </summary>
        public static void ClearCache()
        {
            _staticCache = null;
        }

        public static Dictionary<int, List<BackupInfo>> GatherState(bool useCache = true)
        {
            // Use cached result if available and recent
            if (useCache && _staticCache != null && (DateTime.Now - _cacheTime) < CACHE_DURATION)
            {
                return _staticCache;
            }

            Dictionary<int, List<BackupInfo>> assetVersions = new Dictionary<int, List<BackupInfo>>();

            string[] packages = Directory.GetFiles(Paths.GetBackupFolder(), "*.unitypackage", SearchOption.AllDirectories);
            string[] sep =
            {
                AI.SEPARATOR
            };
            foreach (string package in packages)
            {
                // expected filename format is "foreignId-version
                string fileName = Path.GetFileNameWithoutExtension(package);
                string[] arr = fileName.Split(sep, StringSplitOptions.None);

                // skip packages without leading Id
                if (arr.Length < 3) continue;
                if (!int.TryParse(arr[0], out int id)) continue;
                string version = arr[1];

                if (!assetVersions.ContainsKey(id)) assetVersions.Add(id, new List<BackupInfo>());
                assetVersions[id].Add(new BackupInfo(package, version));
                assetVersions[id] = assetVersions[id].OrderByDescending(v => v.semVersion).ToList();
            }

            // Update static cache
            if (useCache)
            {
                _staticCache = assetVersions;
                _cacheTime = DateTime.Now;
            }

            return assetVersions;
        }

        public async Task Run()
        {
            await Backup();
            ClearOut();
        }

        public async Task Backup(int assetId = -1)
        {
            Refresh();

            string backupFolder = Paths.GetBackupFolder();
            List<Asset> assets = DBAdapter.DB.Table<Asset>()
                .Where(a => a.ForeignId > 0 && a.ParentId == 0 && a.AssetSource != Asset.Source.RegistryPackage && a.Backup && !string.IsNullOrEmpty(a.Version) && !string.IsNullOrEmpty(a.Location))
                .ToList();
            if (assetId > 0) assets = assets.Where(a => a.Id == assetId).ToList();

            MainCount = assets.Count;
            for (int i = 0; i < assets.Count; i++)
            {
                if (CancellationRequested) break;
                await AI.Cooldown.Do();

                Asset asset = assets[i];
                MetaProgress.Report(ProgressId, i + 1, assets.Count, asset.SafeName);

                if (GetEffectiveBackupLimit(asset.Id) <= 0) continue;
                if (!File.Exists(asset.GetLocation(true))) continue;

                // Skip if a newer patch version already exists in backup and we only keep latest patches
                if (AI.Config.onlyLatestPatchVersion && _assetVersions.TryGetValue(asset.ForeignId, out List<BackupInfo> existingBackups))
                {
                    SemVer assetVersion = new SemVer(asset.GetSafeVersion());
                    bool newerPatchExists = existingBackups.Any(b => b.semVersion > assetVersion && assetVersion.OnlyDiffersInPatch(b.semVersion));
                    if (newerPatchExists) continue;
                }

                string targetFile = null;
                if (_assetVersions.TryGetValue(asset.ForeignId, out List<BackupInfo> backupInfos))
                {
                    BackupInfo bi = backupInfos.FirstOrDefault(b => b.version == asset.GetSafeVersion());
                    if (bi != null) targetFile = bi.location;
                }
                if (targetFile == null) targetFile = Path.Combine(backupFolder, $"{asset.ForeignId}{AI.SEPARATOR}{asset.GetSafeVersion()}{AI.SEPARATOR}{asset.SafeName}.unitypackage");
                if (!File.Exists(targetFile))
                {
                    CurrentMain = $"{asset.SafeName} ({EditorUtility.FormatBytes(asset.PackageSize)})";
                    MainProgress = i + 1;

                    try
                    {
                        string source = asset.GetLocation(true);
                        await Task.Run(() => File.Copy(source, targetFile, true));
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"Could not backup '{asset.DisplayName}': {e.Message}");
                    }
                }
            }
            Refresh();
        }

        private void ClearOut()
        {
            Dictionary<int, int> packageBackupLimits = GetPackageBackupLimitsByForeignId();
            int globalBackupLimit = GetGlobalBackupLimit();
            foreach (KeyValuePair<int, List<BackupInfo>> pair in _assetVersions)
            {
                // remove patch versions
                if (AI.Config.onlyLatestPatchVersion)
                {
                    for (int i = pair.Value.Count - 1; i >= 1; i--)
                    {
                        if (pair.Value[i].semVersion.OnlyDiffersInPatch(pair.Value[i - 1].semVersion))
                        {
                            Debug.Log($"Removing asset from backup (newer patch versions available): {pair.Value[i].location}");
                            if (!IOUtils.TryDeleteFile(pair.Value[i].location))
                            {
                                Debug.LogWarning($"Could not delete file: {pair.Value[i].location}");
                            }
                            pair.Value.RemoveAt(i);
                        }
                    }
                }

                // finally remove all remaining old ones 
                int backupLimit = packageBackupLimits.TryGetValue(pair.Key, out int packageBackupLimit)
                    ? packageBackupLimit
                    : globalBackupLimit;
                if (pair.Value.Count > backupLimit)
                {
                    for (int i = pair.Value.Count - 1; i >= backupLimit; i--)
                    {
                        Debug.Log($"Removing asset from backup (limit reached and newer versions available): {pair.Value[i].location}");
                        if (!IOUtils.TryDeleteFile(pair.Value[i].location))
                        {
                            Debug.LogWarning($"Could not delete file: {pair.Value[i].location}");
                        }
                    }
                }
            }
            Refresh();
        }

        private static int GetEffectiveBackupLimit(int assetId)
        {
            return Metadata.TryGetPackageMaxBackups(assetId, out int maxBackups)
                ? maxBackups
                : GetGlobalBackupLimit();
        }

        private static int GetGlobalBackupLimit()
        {
            return Math.Max(0, AI.Config.backupsPerAsset);
        }

        private static Dictionary<int, int> GetPackageBackupLimitsByForeignId()
        {
            Dictionary<int, int> result = new Dictionary<int, int>();
            List<Asset> assets = DBAdapter.DB.Table<Asset>()
                .Where(a => a.ForeignId > 0 && a.ParentId == 0)
                .ToList();

            foreach (Asset asset in assets)
            {
                if (result.ContainsKey(asset.ForeignId)) continue;
                if (Metadata.TryGetPackageMaxBackups(asset.Id, out int maxBackups))
                {
                    result.Add(asset.ForeignId, maxBackups);
                }
            }
            return result;
        }
    }

    public sealed class BackupInfo
    {
        public string location;
        public string version;
        public SemVer semVersion;

        public BackupInfo(string location, string version)
        {
            this.location = location;
            this.version = version;
            semVersion = new SemVer(version);
        }
    }
}
