using ImpossibleRobert.Common;
using UnityEditor;

namespace AudioTool
{
    internal static class AudioToolsAbout
    {
        [MenuItem("Tools/Audio Tools/About...", false, 602)]
        private static void ShowAbout()
        {
            AboutWindow.Show("AudioTools");
        }
    }
}
