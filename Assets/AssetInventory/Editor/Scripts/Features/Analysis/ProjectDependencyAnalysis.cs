using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;

namespace AssetInventory
{
    public static class ProjectDependencyAnalysis
    {
        private static int _virtualIdCounter;

        public static List<AssetFile> GetDependencies(string assetPath, bool recursive = true)
        {
            _virtualIdCounter = -1;

            string[] allDeps = AssetDatabase.GetDependencies(assetPath, recursive);
            string rootGuid = AssetDatabase.AssetPathToGUID(assetPath);

            // Build parent relationships via BFS for tree structure
            Dictionary<string, HashSet<string>> parentMap = new Dictionary<string, HashSet<string>>();
            if (recursive)
            {
                // Get direct deps of the root
                string[] directDeps = AssetDatabase.GetDependencies(assetPath, false);
                foreach (string dep in directDeps)
                {
                    if (dep == assetPath) continue;
                    if (!parentMap.ContainsKey(dep)) parentMap[dep] = new HashSet<string>();
                    parentMap[dep].Add(rootGuid);
                }

                // BFS: for each dep, find its own direct deps to build the tree
                Queue<string> queue = new Queue<string>(directDeps.Where(d => d != assetPath));
                HashSet<string> visited = new HashSet<string> {assetPath};

                while (queue.Count > 0)
                {
                    string current = queue.Dequeue();
                    if (!visited.Add(current)) continue;

                    string currentGuid = AssetDatabase.AssetPathToGUID(current);
                    string[] childDeps = AssetDatabase.GetDependencies(current, false);
                    foreach (string child in childDeps)
                    {
                        if (child == current) continue;
                        if (!parentMap.ContainsKey(child)) parentMap[child] = new HashSet<string>();
                        parentMap[child].Add(currentGuid);
                        queue.Enqueue(child);
                    }
                }
            }

            List<AssetFile> result = new List<AssetFile>();
            foreach (string dep in allDeps)
            {
                if (dep == assetPath) continue;
                if (!dep.StartsWith("Assets/")) continue;
                if (AssetDatabase.IsValidFolder(dep)) continue;

                string guid = AssetDatabase.AssetPathToGUID(dep);
                string fileName = Path.GetFileName(dep);
                string ext = Path.GetExtension(dep);
                string type = !string.IsNullOrEmpty(ext) ? ext.Substring(1).ToLowerInvariant() : "";

                AssetFile file = new AssetFile();
                file.Id = _virtualIdCounter--;
                file.AssetId = -1;
                file.Guid = guid;
                file.Path = dep;
                file.FileName = fileName;
                file.Type = type;
                file.ProjectPath = dep;

                if (parentMap.TryGetValue(dep, out HashSet<string> parents))
                {
                    file.ParentGuids = parents;
                }

                result.Add(file);
            }

            return result;
        }

        public static async Task<List<string>> FindReferencesAsync(string targetPath, Action<int, int> onProgress = null)
        {
            string targetGuid = AssetDatabase.AssetPathToGUID(targetPath);
            if (string.IsNullOrEmpty(targetGuid)) return new List<string>();

            HashSet<string> targetGuids = new HashSet<string> {targetGuid};
            Dictionary<string, List<string>> usages = await FindReferencesAsync(targetGuids, onProgress);

            if (usages.TryGetValue(targetPath, out List<string> refs))
            {
                return refs;
            }
            return new List<string>();
        }

        public static async Task<Dictionary<string, List<string>>> FindReferencesAsync(HashSet<string> targetGuids, Action<int, int> onProgress = null)
        {
            Dictionary<string, List<string>> usages = new Dictionary<string, List<string>>();

            string[] allAssetGuids = AssetDatabase.FindAssets("");
            int total = allAssetGuids.Length;

            for (int i = 0; i < total; i++)
            {
                string guid = allAssetGuids[i];
                if (targetGuids.Contains(guid)) continue;

                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/")) continue;
                if (AssetDatabase.IsValidFolder(path)) continue;

                string[] deps = AssetDatabase.GetDependencies(path, false);
                foreach (string depPath in deps)
                {
                    string depGuid = AssetDatabase.AssetPathToGUID(depPath);
                    if (targetGuids.Contains(depGuid))
                    {
                        if (!usages.ContainsKey(depPath))
                        {
                            usages[depPath] = new List<string>();
                        }
                        usages[depPath].Add(path);
                    }
                }

                if (i % 50 == 0)
                {
                    onProgress?.Invoke(i + 1, total);
                    await Task.Yield();
                }
            }

            onProgress?.Invoke(total, total);
            return usages;
        }
    }
}
