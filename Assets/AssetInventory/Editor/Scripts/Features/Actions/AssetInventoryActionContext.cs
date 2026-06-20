using System.Collections.Generic;
using System.Linq;
using Automator;

namespace AssetInventory
{
    /// <summary>
    /// AssetInventory-specific action context that extends the base context with
    /// loaded assets and files for action steps to use.
    /// </summary>
    public sealed class AssetInventoryActionContext : DictionaryActionContext
    {
        private const string KEY_LOADED_ASSETS = "AI_LoadedAssets";
        private const string KEY_LOADED_FILES = "AI_LoadedFiles";

        /// <summary>
        /// Gets or sets the loaded assets for subsequent steps.
        /// </summary>
        public List<AssetInfo> LoadedAssets
        {
            get => Get<List<AssetInfo>>(KEY_LOADED_ASSETS);
            set => Set(KEY_LOADED_ASSETS, value);
        }

        /// <summary>
        /// Gets or sets the loaded files for subsequent steps.
        /// </summary>
        public List<AssetInfo> LoadedFiles
        {
            get => Get<List<AssetInfo>>(KEY_LOADED_FILES);
            set => Set(KEY_LOADED_FILES, value);
        }

        #region Static Helpers

        /// <summary>
        /// Gets the current context as AssetInventoryActionContext, or null if not available.
        /// </summary>
        public static AssetInventoryActionContext Current => ActionRunner.Context as AssetInventoryActionContext;

        /// <summary>
        /// Sets the loaded assets with the specified operation.
        /// </summary>
        public static void SetLoadedAssets(List<AssetInfo> assets, LoadAssetsStep.AssetOperation operation)
        {
            AssetInventoryActionContext context = Current;
            if (context == null)
            {
                throw new System.Exception("No active AssetInventoryActionContext. SetLoadedAssets can only be called during action execution.");
            }

            List<AssetInfo> currentAssets = context.LoadedAssets;

            switch (operation)
            {
                case LoadAssetsStep.AssetOperation.Replace:
                    context.LoadedAssets = new List<AssetInfo>(assets);
                    break;

                case LoadAssetsStep.AssetOperation.Additive:
                    if (currentAssets == null)
                    {
                        context.LoadedAssets = new List<AssetInfo>(assets);
                    }
                    else
                    {
                        // Add assets that don't already exist (by Id)
                        HashSet<int> existingIds = new HashSet<int>(currentAssets.Select(a => a.Id));
                        foreach (AssetInfo asset in assets)
                        {
                            if (!existingIds.Contains(asset.Id))
                            {
                                currentAssets.Add(asset);
                                existingIds.Add(asset.Id);
                            }
                        }
                    }
                    break;

                case LoadAssetsStep.AssetOperation.Subtractive:
                    if (currentAssets != null)
                    {
                        // Remove assets that match by Id
                        HashSet<int> idsToRemove = new HashSet<int>(assets.Select(a => a.Id));
                        currentAssets.RemoveAll(a => idsToRemove.Contains(a.Id));
                    }
                    break;
            }
        }

        /// <summary>
        /// Gets assets for a step to use. Returns loaded assets if LoadAssetsStep was executed,
        /// otherwise returns default query with filters.
        /// </summary>
        public static List<AssetInfo> GetAssetsForStep()
        {
            AssetInventoryActionContext context = Current;
            if (context?.LoadedAssets != null)
            {
                return context.LoadedAssets;
            }

            // Otherwise return default query with default filters
            return Assets.Load()
                .Where(info => !info.Exclude)
                .Where(info => info.ParentId <= 0)
                .Where(info => info.AssetSource != Asset.Source.RegistryPackage)
                .ToList();
        }

        /// <summary>
        /// Sets the loaded files with the specified operation.
        /// </summary>
        public static void SetLoadedFiles(List<AssetInfo> files, LoadFilesStep.FileOperation operation)
        {
            AssetInventoryActionContext context = Current;
            if (context == null)
            {
                throw new System.Exception("No active AssetInventoryActionContext. SetLoadedFiles can only be called during action execution.");
            }

            List<AssetInfo> currentFiles = context.LoadedFiles;

            switch (operation)
            {
                case LoadFilesStep.FileOperation.Replace:
                    context.LoadedFiles = new List<AssetInfo>(files);
                    break;

                case LoadFilesStep.FileOperation.Additive:
                    if (currentFiles == null)
                    {
                        context.LoadedFiles = new List<AssetInfo>(files);
                    }
                    else
                    {
                        // Add files that don't already exist (by Id)
                        HashSet<int> existingIds = new HashSet<int>(currentFiles.Select(a => a.Id));
                        foreach (AssetInfo file in files)
                        {
                            if (!existingIds.Contains(file.Id))
                            {
                                currentFiles.Add(file);
                                existingIds.Add(file.Id);
                            }
                        }
                    }
                    break;

                case LoadFilesStep.FileOperation.Subtractive:
                    if (currentFiles != null)
                    {
                        // Remove files that match by Id
                        HashSet<int> idsToRemove = new HashSet<int>(files.Select(a => a.Id));
                        currentFiles.RemoveAll(a => idsToRemove.Contains(a.Id));
                    }
                    break;
            }
        }

        /// <summary>
        /// Gets files for a step to use. Returns loaded files if LoadFilesStep was executed,
        /// otherwise returns empty list.
        /// </summary>
        public static List<AssetInfo> GetFilesForStep()
        {
            AssetInventoryActionContext context = Current;
            return context?.LoadedFiles ?? new List<AssetInfo>();
        }

        #endregion
    }
}
