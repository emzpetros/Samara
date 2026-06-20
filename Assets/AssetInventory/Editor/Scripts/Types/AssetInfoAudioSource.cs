using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AudioTool;
using ImpossibleRobert.Common;
using UnityEngine;

namespace AssetInventory
{
    /// <summary>
    /// Bridges AssetInventory's AssetInfo to AudioTool's IAudioSource interface.
    /// Enables seamless integration between AssetInventory and the standalone Audio Tool.
    /// </summary>
    public class AssetInfoAudioSource : IAudioSource
    {
        private readonly AssetInfo m_AssetInfo;

        public AssetInfoAudioSource(AssetInfo assetInfo)
        {
            m_AssetInfo = assetInfo;
        }

        public string FileName => m_AssetInfo.FileName;

        public bool IsInProject => m_AssetInfo.InProject;

        public string ProjectPath => m_AssetInfo.ProjectPath;

        /// <summary>
        /// Gets the AssetInfo this source was created from.
        /// </summary>
        public AssetInfo AssetInfo => m_AssetInfo;

        public async Task<string> GetMaterializedPathAsync(CancellationToken ct = default(CancellationToken))
        {
            if (m_AssetInfo.InProject)
            {
                // File is already in project - return full path directly
                return IOUtils.PathCombine(Path.GetDirectoryName(Application.dataPath), m_AssetInfo.ProjectPath);
            }

            // Extract from package/archive to temp location
            string targetPath = await Assets.EnsureMaterialized(m_AssetInfo, AI.Config.extractSingleFiles, ct);

            if (targetPath != null && !AI.Config.extractSingleFiles && AI.Config.keepExtractedOnAudio && !m_AssetInfo.KeepExtracted)
            {
                // Ensure extraction is set to true for future audio playback
                AI.SetAssetExtraction(m_AssetInfo, true);
            }

            return targetPath;
        }
    }
}
