using System;
using Automator;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImpossibleRobert.Common;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    public sealed class AssetUsage : ActionProgress
    {
        public async Task<List<AssetInfo>> Calculate()
        {
            ReadOnly = true;

            List<AssetInfo> result = new List<AssetInfo>();

            // identify asset packages through guids lookup
            CurrentMain = "Phase 1/2: Gathering guids";
            string[] allGuids = AssetDatabase.FindAssets("", new[] {"Assets"});

            // exclude Asset Inventory temp folders
            List<string> guids = new List<string>();
            foreach (string guid in allGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path) && !path.Contains(AI.TEMP_FOLDER) && !path.Contains(UnityPreviewGenerator.PREVIEW_FOLDER))
                {
                    guids.Add(guid);
                }
            }

            CurrentMain = "Phase 2/2: Looking up assets";
            MainCount = guids.Count;
            MainProgress = 0;

            // pre-fetch all GUIDs with asset origin in one native call (2023+)
            HashSet<string> guidsWithOrigin = AssetStore.GetGuidsWithOrigin();

            // check if current project is indexed via additional folder
            // exclude as otherwise that would result in current project being reported as source
            string curPath = Path.GetDirectoryName(Application.dataPath);
            curPath = Paths.MakeRelative(curPath);
            List<int> ids = DBAdapter.DB.Query<Asset>("select Id from Asset where Location = ?", curPath).Select(a => a.Id).ToList();

            int batchSize = AI.Config.reportingBatchSize;
            for (int i = 0; i < guids.Count; i += batchSize)
            {
                if (CancellationRequested) break;

                List<string> batch = guids.Skip(i).Take(batchSize).ToList();
                MainProgress += batch.Count;

                await Task.Yield();

                Dictionary<string, List<AssetInfo>> batchFiles = AssetUtils.Guids2Files(batch, true, ids, guidsWithOrigin);
                foreach (KeyValuePair<string, List<AssetInfo>> kvp in batchFiles)
                {
                    List<AssetInfo> files = kvp.Value;
                    if (files == null || files.Count == 0) continue;

                    files = files.Where(file => file != null).ToList();
                    if (files.Count == 0) continue;

                    if (files.Count > 1)
                    {
                        if (AI.Config.reportingAutoResolve)
                        {
                            string projectPath = files[0].ProjectPath;
                            int bestScore = files.Max(f => GetPathMatchScore(projectPath, f.Path));

                            // Only resolve if there is a meaningful path match (beyond the shared "Assets" root)
                            // and exactly one candidate has the best score, otherwise the attribution is ambiguous.
                            if (bestScore > 0)
                            {
                                List<AssetInfo> bestMatches = files.Where(f => GetPathMatchScore(projectPath, f.Path) == bestScore).ToList();
                                if (bestMatches.Count == 1)
                                {
                                    AssetInfo best = bestMatches[0];
                                    best.Origin = null;
                                    result.Add(best);
                                }
                            }
                        }
                        else
                        {
                            Debug.LogWarning($"Multiple origin candidates for found for guid {kvp.Key}: \n\n"
                                + string.Join("\n", files.Select(ai => $"{ai.Path} ({ai.GetDisplayName()} - {ai.Id})")) + "\n");
                        }
                        continue;
                    }
                    result.Add(files[0]);
                }
            }

            return result;
        }

        private static int GetPathMatchScore(string projectPath, string candidatePath)
        {
            if (string.IsNullOrEmpty(projectPath) || string.IsNullOrEmpty(candidatePath)) return 0;

            string[] projectSegments = projectPath.Split('/');
            string[] candidateSegments = candidatePath.Split('/');

            // Skip the leading "Assets" segment which every path shares and is therefore not meaningful
            int start = (projectSegments.Length > 0 && candidateSegments.Length > 0
                && string.Equals(projectSegments[0], "Assets", StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidateSegments[0], "Assets", StringComparison.OrdinalIgnoreCase))
                ? 1 : 0;

            int matchCount = 0;
            int limit = Math.Min(projectSegments.Length, candidateSegments.Length);
            for (int i = start; i < limit; i++)
            {
                if (string.Equals(projectSegments[i], candidateSegments[i], StringComparison.OrdinalIgnoreCase))
                    matchCount++;
                else
                    break;
            }
            return matchCount;
        }
    }
}
