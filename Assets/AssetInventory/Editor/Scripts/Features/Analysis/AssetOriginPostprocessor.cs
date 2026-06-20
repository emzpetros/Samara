using System.Collections.Generic;
using UnityEditor;

namespace AssetInventory
{
    public sealed class AssetOriginPostprocessor : AssetPostprocessor
    {
        private static HashSet<string> _changedGuids;

        public static HashSet<string> ConsumeChangedGuids()
        {
            HashSet<string> result = _changedGuids;
            _changedGuids = null;
            return result;
        }

        public static bool HasChanges => _changedGuids != null && _changedGuids.Count > 0;

        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (importedAssets.Length == 0) return;

            _changedGuids ??= new HashSet<string>();
            foreach (string path in importedAssets)
            {
                string guid = AssetDatabase.AssetPathToGUID(path);
                if (!string.IsNullOrEmpty(guid))
                {
                    _changedGuids.Add(guid);
                }
            }
        }
    }
}
