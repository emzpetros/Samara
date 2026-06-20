using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    internal static class FBXMaterialFallbackResolver
    {
        private static readonly Regex TextureFileNameRegex = new Regex(@"[A-Za-z0-9 _.\-\\/]+?\.(?:png|jpe?g|tga|tif|tiff|psd|bmp|exr)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly string[] TextureProperties = {"_MainTex", "_BaseMap"};

        internal struct MaterialCandidate
        {
            public string AssetPath;
            public string MaterialName;
            public string MainTextureFileName;
            public string ShaderName;
            public string RenderType;
            public string QueueTag;
            public bool HasAssetOrigin;

            public MaterialCandidate(string assetPath, string materialName, string mainTextureFileName, string shaderName, string renderType, string queueTag, bool hasAssetOrigin = false)
            {
                AssetPath = assetPath;
                MaterialName = materialName;
                MainTextureFileName = mainTextureFileName;
                ShaderName = shaderName;
                RenderType = renderType;
                QueueTag = queueTag;
                HasAssetOrigin = hasAssetOrigin;
            }
        }

        internal static int ApplyExternalMaterialFallbacks(GameObject instance, string fbxPath)
        {
            if (instance == null || string.IsNullOrEmpty(fbxPath)) return 0;

            HashSet<string> fbxReferences = ExtractReferencedNames(fbxPath);
            if (fbxReferences.Count == 0) return 0;

            List<MaterialCandidate> candidates = LoadMaterialCandidates(fbxPath);
            string materialPath = ResolveBestMaterialPath(fbxReferences, candidates);
            if (string.IsNullOrEmpty(materialPath)) return 0;

            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null) return 0;

            int assignments = AssignMaterialToGeneratedSlots(instance, material);
            if (assignments > 0 && AI.Config.LogPreviewCreation)
            {
                Debug.Log($"[Asset Inventory] Applied FBX external material fallback for '{fbxPath}': material='{material.name}', path='{materialPath}', slots={assignments}.");
            }

            return assignments;
        }

        internal static string ResolveBestMaterialPath(IEnumerable<string> fbxReferences, IEnumerable<MaterialCandidate> candidates)
        {
            if (fbxReferences == null || candidates == null) return null;

            HashSet<string> referenceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> referenceStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string reference in fbxReferences)
            {
                if (string.IsNullOrWhiteSpace(reference)) continue;

                string fileName = Path.GetFileName(NormalizePathFragment(reference));
                if (string.IsNullOrEmpty(fileName)) continue;

                referenceNames.Add(fileName);
                string stem = Path.GetFileNameWithoutExtension(fileName);
                if (!string.IsNullOrEmpty(stem)) referenceStems.Add(stem);
            }

            int bestScore = 0;
            string bestPath = null;
            foreach (MaterialCandidate candidate in candidates)
            {
                int score = ScoreCandidate(candidate, referenceNames, referenceStems);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPath = candidate.AssetPath;
                }
            }

            return bestScore >= 50 ? bestPath : null;
        }

        private static int ScoreCandidate(MaterialCandidate candidate, HashSet<string> referenceNames, HashSet<string> referenceStems)
        {
            int score = 0;

            string textureFileName = Path.GetFileName(NormalizePathFragment(candidate.MainTextureFileName));
            if (!string.IsNullOrEmpty(textureFileName))
            {
                if (referenceNames.Contains(textureFileName)) score += 100;

                string textureStem = Path.GetFileNameWithoutExtension(textureFileName);
                if (!string.IsNullOrEmpty(textureStem) && referenceStems.Contains(textureStem)) score += 75;
            }

            string materialName = Path.GetFileNameWithoutExtension(candidate.MaterialName);
            if (!string.IsNullOrEmpty(materialName) && referenceStems.Contains(materialName)) score += 60;

            string pathStem = Path.GetFileNameWithoutExtension(candidate.AssetPath);
            if (!string.IsNullOrEmpty(pathStem) && referenceStems.Contains(pathStem)) score += 60;

            if (PipelineConverter.AnalyzeShaderForConversion(
                    candidate.ShaderName,
                    candidate.RenderType,
                    candidate.QueueTag,
                    !string.IsNullOrEmpty(candidate.MainTextureFileName),
                    true,
                    false).SurfaceMode != PipelineConverter.MaterialSurfaceMode.Opaque)
            {
                score += 5;
            }

            if (candidate.HasAssetOrigin) score += 10;

            return score;
        }

        private static HashSet<string> ExtractReferencedNames(string fbxPath)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string fullPath = ToFullPath(fbxPath);
            if (!File.Exists(fullPath)) return result;

            try
            {
                byte[] bytes = File.ReadAllBytes(fullPath);
                string text = Encoding.UTF8.GetString(bytes);
                MatchCollection matches = TextureFileNameRegex.Matches(text);
                foreach (Match match in matches)
                {
                    string fileName = Path.GetFileName(NormalizePathFragment(match.Value));
                    if (!string.IsNullOrEmpty(fileName)) result.Add(fileName);
                    string stem = Path.GetFileNameWithoutExtension(fileName);
                    if (!string.IsNullOrEmpty(stem)) result.Add(stem);
                }
            }
            catch (Exception e)
            {
                if (AI.Config.LogPreviewCreation)
                {
                    Debug.LogWarning($"[Asset Inventory] Could not scan FBX texture names for material fallback '{fbxPath}': {e.Message}");
                }
            }

            return result;
        }

        private static List<MaterialCandidate> LoadMaterialCandidates(string fbxPath)
        {
            string folder = Path.GetDirectoryName(fbxPath);
            if (string.IsNullOrEmpty(folder)) return new List<MaterialCandidate>();

            string[] guids = AssetDatabase.FindAssets("t:Material", new[] {folder.Replace('\\', '/')});
            List<MaterialCandidate> candidates = new List<MaterialCandidate>();
            foreach (string guid in guids)
            {
                string materialPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(materialPath) || !materialPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) continue;

                Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null) continue;

                string textureFileName = GetMainTextureFileName(material);
                string shaderName = material.shader != null ? material.shader.name : "";
                string renderType = material.GetTag("RenderType", true, "");
                string queueTag = material.GetTag("Queue", true, "");
                candidates.Add(new MaterialCandidate(materialPath, material.name, textureFileName, shaderName, renderType, queueTag, HasAssetOrigin(materialPath)));
            }

            return candidates;
        }

        private static bool HasAssetOrigin(string materialPath)
        {
            if (string.IsNullOrEmpty(materialPath)) return false;

            string metaPath = ToFullPath(materialPath + ".meta");
            if (!File.Exists(metaPath)) return false;

            try
            {
                string meta = File.ReadAllText(metaPath);
                return meta.Contains("AssetOrigin:");
            }
            catch
            {
                return false;
            }
        }

        private static int AssignMaterialToGeneratedSlots(GameObject instance, Material material)
        {
            int assignments = 0;
            bool replacementHasAssetOrigin = HasAssetOrigin(AssetDatabase.GetAssetPath(material));
            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in renderers)
            {
                Material[] materials = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    if (!ShouldReplaceMaterialSlot(materials[i], replacementHasAssetOrigin)) continue;

                    materials[i] = material;
                    assignments++;
                    changed = true;
                }

                if (changed) renderer.sharedMaterials = materials;
            }

            return assignments;
        }

        private static bool ShouldReplaceMaterialSlot(Material material, bool replacementHasAssetOrigin)
        {
            if (material == null) return true;

            string materialPath = AssetDatabase.GetAssetPath(material);
            if (!string.IsNullOrEmpty(materialPath) && materialPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
            {
                return replacementHasAssetOrigin && !HasAssetOrigin(materialPath);
            }

            foreach (string property in TextureProperties)
            {
                if (material.HasProperty(property) && material.GetTexture(property) != null) return false;
            }

            return string.IsNullOrEmpty(materialPath);
        }

        private static string GetMainTextureFileName(Material material)
        {
            foreach (string property in TextureProperties)
            {
                if (!material.HasProperty(property)) continue;

                Texture texture = material.GetTexture(property);
                if (texture == null) continue;

                string texturePath = AssetDatabase.GetAssetPath(texture);
                if (string.IsNullOrEmpty(texturePath)) continue;

                return Path.GetFileName(texturePath);
            }

            return null;
        }

        private static string NormalizePathFragment(string path)
        {
            return string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/').Trim('\0', '"', '\'', ' ');
        }

        private static string ToFullPath(string assetPath)
        {
            if (Path.IsPathRooted(assetPath)) return assetPath;

            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }
    }
}
