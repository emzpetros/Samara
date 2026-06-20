#if ASSET_INVENTORY_MCP
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.AI.MCP.Editor.Helpers;

namespace AssetInventory
{
    internal static class McpResultHelper
    {
        internal static object EnsureInit()
        {
            AI.Init();
            if (!AI.IsInitialized)
            {
                return Response.Error("Asset Inventory is not initialized. Open the Asset Inventory window first to set up the database.");
            }
            if (!DBAdapter.IsDBOpen())
            {
                return Response.Error("Asset Inventory database is not open.");
            }
            return null;
        }

        internal static object ToAssetFileResult(AssetInfo info)
        {
            string previewPath = GetFilePreviewPath(info);
            return new
            {
                id = info.Id,
                assetId = info.AssetId,
                fileName = info.FileName,
                path = info.Path,
                type = info.Type,
                assetGroup = ClassifyAssetGroup(info.Type),
                packageName = info.GetDisplayName(),
                publisher = info.GetDisplayPublisher(),
                category = info.GetDisplayCategory(),
                description = info.AICaption,
                size = info.Size,
                width = info.Width,
                height = info.Height,
                length = info.Length,
                guid = info.Guid,
                previewImagePath = previewPath,
                hasPreview = !string.IsNullOrEmpty(previewPath)
            };
        }

        internal static object ToPackageResult(AssetInfo info)
        {
            string previewPath = GetPackagePreviewPath(info);
            return new
            {
                id = info.AssetId,
                name = info.SafeName,
                displayName = info.GetDisplayName(),
                publisher = info.GetDisplayPublisher(),
                category = info.GetDisplayCategory(),
                source = info.AssetSource.ToString(),
                version = info.Version,
                latestVersion = info.LatestVersion,
                description = info.Description,
                keyFeatures = info.KeyFeatures,
                keywords = info.Keywords,
                priceUsd = info.PriceUsd,
                priceEur = info.PriceEur,
                rating = info.AssetRating,
                ratingCount = info.RatingCount,
                hotness = info.Hotness,
                fileCount = info.FileCount,
                packageSize = info.PackageSize,
                isDeprecated = info.IsDeprecated,
                isAbandoned = info.IsAbandoned,
                isDownloaded = info.IsDownloaded,
                isIndexed = info.IsIndexed,
                srpCompatibility = new
                {
                    birp = info.BIRPCompatible,
                    urp = info.URPCompatible,
                    hdrp = info.HDRPCompatible
                },
                license = info.License,
                firstRelease = info.FirstRelease > System.DateTime.MinValue ? info.FirstRelease.ToString("yyyy-MM-dd") : null,
                lastRelease = info.LastRelease > System.DateTime.MinValue ? info.LastRelease.ToString("yyyy-MM-dd") : null,
                lastUpdate = info.LastUpdate > System.DateTime.MinValue ? info.LastUpdate.ToString("yyyy-MM-dd") : null,
                purchaseDate = info.PurchaseDate > System.DateTime.MinValue ? info.PurchaseDate.ToString("yyyy-MM-dd") : null,
                slug = info.Slug,
                previewImagePath = previewPath
            };
        }

        internal static object ToPackageDetailResult(AssetInfo info)
        {
            string previewFolder = Paths.GetPreviewFolder();
            string previewPath = GetPackagePreviewPath(info);

            List<Tag> tags = Tagging.GetPackageTags(info.AssetId)?.Select(t => new Tag {Name = t.Name, Color = t.Color}).ToList();
            List<AssetMedia> media = DBAdapter.DB.Query<AssetMedia>("select * from AssetMedia where AssetId=? order by [Order]", info.AssetId);

            return new
            {
                id = info.AssetId,
                name = info.SafeName,
                displayName = info.GetDisplayName(),
                publisher = info.GetDisplayPublisher(),
                category = info.GetDisplayCategory(),
                source = info.AssetSource.ToString(),
                version = info.Version,
                latestVersion = info.LatestVersion,
                description = info.Description,
                keyFeatures = info.KeyFeatures,
                keywords = info.Keywords,
                compatibilityInfo = info.CompatibilityInfo,
                supportedUnityVersions = info.SupportedUnityVersions,
                requirements = info.Requirements,
                releaseNotes = info.ReleaseNotes,
                priceUsd = info.PriceUsd,
                priceEur = info.PriceEur,
                rating = info.AssetRating,
                ratingCount = info.RatingCount,
                hotness = info.Hotness,
                fileCount = info.FileCount,
                packageSize = info.PackageSize,
                isDeprecated = info.IsDeprecated,
                isAbandoned = info.IsAbandoned,
                isDownloaded = info.IsDownloaded,
                isIndexed = info.IsIndexed,
                srpCompatibility = new
                {
                    birp = info.BIRPCompatible,
                    urp = info.URPCompatible,
                    hdrp = info.HDRPCompatible
                },
                license = info.License,
                firstRelease = info.FirstRelease > System.DateTime.MinValue ? info.FirstRelease.ToString("yyyy-MM-dd") : null,
                lastRelease = info.LastRelease > System.DateTime.MinValue ? info.LastRelease.ToString("yyyy-MM-dd") : null,
                lastUpdate = info.LastUpdate > System.DateTime.MinValue ? info.LastUpdate.ToString("yyyy-MM-dd") : null,
                purchaseDate = info.PurchaseDate > System.DateTime.MinValue ? info.PurchaseDate.ToString("yyyy-MM-dd") : null,
                slug = info.Slug,
                previewImagePath = previewPath,
                tags = tags?.Select(t => new {name = t.Name, color = t.Color}).ToArray(),
                media = media?.Select(m => new
                {
                    type = m.Type,
                    url = m.GetUrl(),
                    thumbnailUrl = m.ThumbnailUrl,
                    localPath = GetMediaLocalPath(info.AssetId, m, previewFolder),
                    width = m.Width,
                    height = m.Height
                }).ToArray()
            };
        }

        internal static string ClassifyAssetGroup(string type)
        {
            if (string.IsNullOrEmpty(type)) return null;

            string ext = type.ToLowerInvariant().TrimStart('.');
            foreach (KeyValuePair<AI.AssetGroup, string[]> group in AI.TypeGroups)
            {
                if (group.Value.Contains(ext)) return group.Key.ToString();
            }
            return null;
        }

        internal static string GetFilePreviewPath(AssetInfo info)
        {
            string previewFolder = Paths.GetPreviewFolder();
            if (info.PreviewState == AssetFile.PreviewOptions.UseOriginal)
            {
                string sourcePath = info.GetSourcePath(true);
                if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath)) return sourcePath;
            }

            string previewFile = info.GetPreviewFile(previewFolder);
            if (!string.IsNullOrEmpty(previewFile) && File.Exists(previewFile)) return previewFile;

            return null;
        }

        internal static string GetPackagePreviewPath(AssetInfo info)
        {
            string previewFolder = Paths.GetPreviewFolder();
            Asset asset = info.ToAsset();
            string previewFile = asset.GetPreviewFile(previewFolder);
            return previewFile;
        }

        private static string GetMediaLocalPath(int assetId, AssetMedia media, string previewFolder)
        {
            string file = Path.Combine(previewFolder, assetId.ToString(), $"m-{media.Id}{Path.GetExtension(media.Url)}");
            return File.Exists(file) ? file : null;
        }

        internal static object PageResults<T>(List<T> items, int page, int pageSize, int totalCount)
        {
            int skip = (page - 1) * pageSize;
            List<T> paged = items.Skip(skip).Take(pageSize).ToList();
            return new
            {
                results = paged,
                totalCount,
                page,
                pageSize,
                totalPages = (totalCount + pageSize - 1) / pageSize
            };
        }
    }
}
#endif
