using System.IO;
using UnityEngine;

namespace AssetInventory
{
    /// <summary>
    /// Database operations for preview management.
    /// Extracted from PreviewManager to separate concerns.
    /// </summary>
    public static class PreviewDatabaseOperations
    {
        /// <summary>
        /// Store preview result in database
        /// </summary>
        public static AssetFile StorePreviewResult(PreviewRequest req)
        {
            AssetFile af = DBAdapter.DB.Find<AssetFile>(req.Id);
            if (af == null) return null;

            if (!File.Exists(req.DestinationFile))
            {
                if (af.PreviewState != AssetFile.PreviewOptions.Provided)
                {
                    af.PreviewState = AssetFile.PreviewOptions.Error;
                    DBAdapter.DB.Update(af);
                }
                return af;
            }

            if (req.Obj != null)
            {
                if (req.Obj is Texture2D tex)
                {
                    af.Width = tex.width;
                    af.Height = tex.height;
                }
                if (req.Obj is AudioClip clip)
                {
                    af.Length = clip.length;
                }
            }

            // Update animation count for FBX files (stored in Length field)
            if (af.Type == "fbx" && req.AnimationCount >= 0)
            {
                af.Length = (float)req.AnimationCount;
            }

            // Store file-specific data (e.g., FBX data)
            if (!string.IsNullOrEmpty(req.FileData))
            {
                af.FileData = req.FileData;
            }

            if (req.SourceFile == req.DestinationFile)
            {
                af.PreviewState = AssetFile.PreviewOptions.UseOriginal;
            }
            else
            {
                // do not remove originally supplied previews even in case of error
                af.PreviewState = req.Icon != null ?
                    AssetFile.PreviewOptions.Custom :
                    (af.PreviewState != AssetFile.PreviewOptions.Provided ? AssetFile.PreviewOptions.Error : AssetFile.PreviewOptions.Provided);
            }

            // reset data to be recreated
            if (af.PreviewState != AssetFile.PreviewOptions.Error)
            {
                af.Hue = -1f;
                af.AICaption = null;
            }
            else
            {
                if (AI.Config.LogPreviewCreation) Debug.LogWarning($"No preview returned for '{req.SourceFile}'");
            }

            DBAdapter.DB.Update(af);

            return af;
        }
    }
}

