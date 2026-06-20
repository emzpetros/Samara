using System.Threading;
using System.Threading.Tasks;

namespace AudioTool
{
    /// <summary>
    /// Abstraction for an audio file source, allowing the Audio Tool to work with different sources
    /// (standalone files from disk, assets in packages, etc.)
    /// </summary>
    public interface IAudioSource
    {
        /// <summary>
        /// The display name of the audio file.
        /// </summary>
        string FileName { get; }

        /// <summary>
        /// Whether the source is already in the Unity project's Assets folder.
        /// </summary>
        bool IsInProject { get; }

        /// <summary>
        /// The project-relative path if IsInProject is true (e.g., "Assets/Audio/sound.wav").
        /// </summary>
        string ProjectPath { get; }

        /// <summary>
        /// Gets a path to the materialized audio file on disk.
        /// For files already in the project, this returns the full path directly.
        /// For files in packages/archives, this extracts to a temporary location.
        /// </summary>
        /// <param name="ct">Cancellation token for async operations</param>
        /// <returns>Full file system path to the audio file</returns>
        Task<string> GetMaterializedPathAsync(CancellationToken ct = default);
    }
}
