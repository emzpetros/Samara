using System.IO;
using UnityEditor;
using UnityEngine;

namespace AudioTool
{
    /// <summary>
    /// Context menu integration for editing audio files directly from the Project window.
    /// </summary>
    public static class AudioContextMenu
    {
        private static readonly string[] SupportedExtensions = {".wav", ".mp3", ".ogg", ".aiff", ".aif", ".flac"};

        [MenuItem("Assets/Edit Audio...", false, 200)]
        private static void EditAudio()
        {
            Object[] selectedObjects = Selection.objects;

            foreach (Object obj in selectedObjects)
            {
                string assetPath = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(assetPath)) continue;

                if (IsAudioFile(assetPath))
                {
                    string fullPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), assetPath);
                    FileAudioSource source = new FileAudioSource(fullPath);

                    AudioEditorUI window = AudioEditorUI.ShowWindow();
                    window.Init(source, Path.GetDirectoryName(fullPath));
                }
            }
        }

        [MenuItem("Assets/Edit Audio...", true)]
        private static bool ValidateEditAudio()
        {
            Object[] selectedObjects = Selection.objects;
            if (selectedObjects == null || selectedObjects.Length != 1) return false;

            foreach (Object obj in selectedObjects)
            {
                string assetPath = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(assetPath) && IsAudioFile(assetPath))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsAudioFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            string extension = Path.GetExtension(path).ToLowerInvariant();
            foreach (string ext in SupportedExtensions)
            {
                if (extension == ext) return true;
            }

            return false;
        }
    }
}
