using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace AssetInventory
{
    public sealed class FolderImporter : AssetImporter
    {
        public async Task Run(bool force = false)
        {
            List<FolderSpec> folders = AI.Config.folders.Where(f => f.enabled).ToList();
            MainCount = folders.Count;
            for (int i = 0; i < folders.Count; i++)
            {
                if (CancellationRequested) break;

                FolderSpec spec = folders[i];

                SetProgress(spec.location, i + 1);

                if (!Directory.Exists(spec.GetLocation(true)))
                {
                    Debug.LogWarning($"Specified folder to scan for assets does not exist anymore: {spec.location}");
                    continue;
                }

                switch (spec.folderType)
                {
                    case 0:
                        bool hasAssetStoreLayout = Path.GetFileName(spec.GetLocation(true)) == AI.ASSET_STORE_FOLDER_NAME;
                        await AI.Actions.RunWithProgress<UnityPackageImporter>(
                            ActionHandler.ACTION_PACKAGE_FOLDERS_INDEX,
                            "Updating Unity package index",
                            async imp =>
                            {
                                await imp.IndexRoughLocal(spec, hasAssetStoreLayout, force);
                                if (AI.Config.indexAssetPackageContents)
                                {
                                    imp.RestartProgress("Indexing package contents");
                                    await imp.IndexDetails();
                                }
                            });
                        break;

                    case 1:
                        await AI.Actions.RunWithProgress<MediaImporter>(
                            ActionHandler.ACTION_MEDIA_FOLDERS_INDEX,
                            "Updating media folder index",
                            imp => imp.Index(spec, null, false, false, true));
                        break;

                    case 2:
                        await AI.Actions.RunWithProgress<ArchiveImporter>(
                            ActionHandler.ACTION_ARCHIVE_FOLDERS_INDEX,
                            "Updating archives index",
                            imp => imp.Run(spec));
                        break;

                    case 3:
                        await AI.Actions.RunWithProgress<DevPackageImporter>(
                            ActionHandler.ACTION_DEVPACKAGE_FOLDERS_INDEX,
                            "Updating dev package index",
                            imp => imp.Index(spec));
                        break;

                    default:
                        Debug.LogError($"Unsupported folder scan type: {spec.folderType}");
                        break;
                }
            }
        }
    }
}
