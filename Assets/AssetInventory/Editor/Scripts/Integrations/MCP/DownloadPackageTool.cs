#if ASSET_INVENTORY_MCP
using Unity.AI.MCP.Editor.Helpers;
using Unity.AI.MCP.Editor.ToolRegistry;

namespace AssetInventory
{
    public static class DownloadPackageTool
    {
        public class DownloadPackageParams
        {
            [McpDescription("Package ID to download (the 'id' field from searchPackages results). Check 'isDownloaded' first to avoid redundant downloads.", Required = true)]
            public int PackageId { get; set; }
        }

        [McpTool("AssetInventory_downloadPackage",
            "Download a package so its files become extractable. Check isDownloaded from search results first; skip if already downloaded. Poll getDownloadProgress for status.",
            Groups = new[] {"Asset Inventory/Import"})]
        public static object DownloadPackage(DownloadPackageParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            AssetInfo info = Assets.GetPackage(parameters.PackageId);
            if (info == null)
            {
                return Response.Error($"Package with ID {parameters.PackageId} not found.");
            }

            if (info.IsDownloaded)
            {
                return Response.Success($"Package '{info.GetDisplayName()}' is already downloaded.", new
                {
                    state = "Downloaded",
                    isAlreadyDownloaded = true
                });
            }

            if (info.PackageDownloader == null)
            {
                info.PackageDownloader = new AssetDownloader(info);
            }

            if (!info.PackageDownloader.IsDownloadSupported())
            {
                return Response.Error($"Download is not supported for package '{info.GetDisplayName()}' (source: {info.AssetSource}).");
            }

            info.PackageDownloader.Download(true);

            return Response.Success($"Download started for package '{info.GetDisplayName()}'. Use AssetInventory_getDownloadProgress to monitor.", new
            {
                state = "Downloading",
                isAlreadyDownloaded = false,
                packageSize = info.PackageSize
            });
        }

        #region Download Progress

        public class DownloadProgressParams
        {
            [McpDescription("Package ID to check download progress for (same ID used with downloadPackage).", Required = true)]
            public int PackageId { get; set; }
        }

        [McpTool("AssetInventory_getDownloadProgress",
            "Check download state, progress percentage, and bytes transferred for a package.",
            Groups = new[] {"Asset Inventory/Import"})]
        public static object GetDownloadProgress(DownloadProgressParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            AssetInfo info = Assets.GetPackage(parameters.PackageId);
            if (info == null)
            {
                return Response.Error($"Package with ID {parameters.PackageId} not found.");
            }

            if (info.IsDownloaded)
            {
                return Response.Success($"Package '{info.GetDisplayName()}' is downloaded.", new
                {
                    state = "Downloaded",
                    progress = 1.0f,
                    bytesDownloaded = info.PackageSize,
                    bytesTotal = info.PackageSize
                });
            }

            if (info.PackageDownloader == null)
            {
                return Response.Success($"No download in progress for package '{info.GetDisplayName()}'.", new
                {
                    state = "None",
                    progress = 0f,
                    bytesDownloaded = 0L,
                    bytesTotal = info.PackageSize
                });
            }

            info.PackageDownloader.RefreshState();
            AssetDownloadState downloadState = info.PackageDownloader.GetState();

            return Response.Success($"Package '{info.GetDisplayName()}': {downloadState.state} ({downloadState.progress:P0}).", new
            {
                state = downloadState.state.ToString(),
                progress = downloadState.progress,
                bytesDownloaded = downloadState.bytesDownloaded,
                bytesTotal = downloadState.bytesTotal
            });
        }

        #endregion
    }
}
#endif
