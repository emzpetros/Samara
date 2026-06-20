using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace AudioTool
{
    /// <summary>
    /// Audio source for files directly on disk (standalone mode).
    /// </summary>
    public class FileAudioSource : IAudioSource
    {
        private readonly string _fullPath;
        private readonly bool _isInProject;
        private readonly string _projectPath;

        /// <summary>
        /// Creates an audio source from a file on disk.
        /// </summary>
        /// <param name="fullPath">Full file system path to the audio file</param>
        public FileAudioSource(string fullPath)
        {
            _fullPath = fullPath;

            // Check if file is inside the project's Assets folder
            // Normalize paths for comparison (handle forward/backslash differences)
            string dataPath = Path.GetFullPath(Application.dataPath);
            string normalizedFullPath = Path.GetFullPath(fullPath);
            
            if (normalizedFullPath.StartsWith(dataPath, System.StringComparison.OrdinalIgnoreCase))
            {
                _isInProject = true;
                _projectPath = "Assets" + normalizedFullPath.Substring(dataPath.Length).Replace('\\', '/');
            }
            else
            {
                _isInProject = false;
                _projectPath = null;
            }
        }

        public string FileName => Path.GetFileName(_fullPath);

        public bool IsInProject => _isInProject;

        public string ProjectPath => _projectPath;

        /// <summary>
        /// Gets the full path to the audio file on disk.
        /// </summary>
        public string FullPath => _fullPath;

        public Task<string> GetMaterializedPathAsync(CancellationToken ct = default)
        {
            // For files already on disk, just return the path directly
            return Task.FromResult(_fullPath);
        }
    }
}
