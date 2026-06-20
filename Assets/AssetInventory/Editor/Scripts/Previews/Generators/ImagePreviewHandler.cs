using ImpossibleRobert.Common;
using System;
using System.IO;
using UnityEngine;

namespace AssetInventory
{
    /// <summary>
    /// Image preview generation with fast resize shortcuts.
    /// Extracted from PreviewManager for better separation of concerns.
    /// </summary>
    public static class ImagePreviewHandler
    {
        /// <summary>
        /// Try to create or copy image preview using shortcuts (resize or original)
        /// Returns true if handled successfully, false if Unity import pipeline should be used
        /// </summary>
        public static bool TryCreateImagePreview(AssetInfo info, string sourcePath, string previewFile, int previewSize, Action onCreated = null, Action<PreviewRequest> onDone = null)
        {
            if (!ImageUtils.SYSTEM_IMAGE_TYPES.Contains(info.Type))
            {
                return false; // Not a system image type, use Unity pipeline
            }

            // Take shortcut for images and skip Unity importer
            if (ImageUtils.ResizeImage(sourcePath, previewFile, previewSize, !AI.Config.upscaleLossless, info.Type))
            {
                PreviewRequest req = new PreviewRequest
                {
                    DestinationFile = previewFile,
                    Id = info.Id,
                    Icon = Texture2D.grayTexture,
                    SourceFile = sourcePath
                };
                PreviewDatabaseOperations.StorePreviewResult(req);
                onCreated?.Invoke();
                onDone?.Invoke(req);
                return true;
            }

            // Try to use original preview
            string originalPreviewFile = PreviewAssetManager.DerivePreviewFile(sourcePath);
            if (File.Exists(originalPreviewFile))
            {
                File.Copy(originalPreviewFile, previewFile, true);
                PreviewRequest req = new PreviewRequest
                {
                    DestinationFile = previewFile,
                    Id = info.Id,
                    Icon = Texture2D.grayTexture,
                    SourceFile = originalPreviewFile
                };
                PreviewDatabaseOperations.StorePreviewResult(req);
                info.PreviewState = AssetFile.PreviewOptions.Provided;
                DBAdapter.DB.Execute("update AssetFile set PreviewState=? where Id=?", AssetFile.PreviewOptions.Provided, info.Id);
                onCreated?.Invoke();
                onDone?.Invoke(req);
                return true;
            }

            PreviewRequest failedRequest = new PreviewRequest
            {
                DestinationFile = previewFile,
                Id = info.Id,
                SourceFile = sourcePath,
                FailureReason = "The image could not be resized and no bundled preview image was available."
            };

            if (!info.HasPreview(true))
            {
                info.PreviewState = AssetFile.PreviewOptions.Error;
                DBAdapter.DB.Execute("update AssetFile set PreviewState=? where Id=?", AssetFile.PreviewOptions.Error, info.Id);
            }

            onDone?.Invoke(failedRequest);

            // signal we are done with the image pipeline, nothing for Unity to fall back to as well, even in an error state
            return true;
        }
    }
}
