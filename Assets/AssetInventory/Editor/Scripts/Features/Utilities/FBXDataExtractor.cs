using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    /// <summary>
    /// Utility for extracting metadata from FBX files (animations, mesh statistics)
    /// </summary>
    public static class FBXDataExtractor
    {
        /// <summary>
        /// Extract FBX metadata from a file path (animations + mesh statistics)
        /// </summary>
        /// <param name="fbxPath">Path to the FBX file</param>
        /// <param name="instantiatedPrefab">Optional: already-instantiated prefab to extract from (avoids re-instantiation)</param>
        /// <returns>FBXData object with extracted metadata, or null if extraction failed</returns>
        public static FBXData ExtractFBXData(string fbxPath, GameObject instantiatedPrefab = null)
        {
            if (string.IsNullOrEmpty(fbxPath) || !fbxPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                FBXData fbxData = new FBXData();

                // Extract animation data from FBX file
                UnityEngine.Object[] allFBXAssets = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
                AnimationClip[] animClips = allFBXAssets
                    .OfType<AnimationClip>()
                    .Where(c => !c.name.StartsWith("__preview__") && !c.empty)
                    .ToArray();

                foreach (AnimationClip clip in animClips)
                {
                    fbxData.animations.Add(new AnimationInfo
                    {
                        name = clip.name,
                        length = clip.length
                    });
                }

                // Extract mesh statistics
                GameObject instanceToUse = instantiatedPrefab;
                bool needsCleanup = false;

                if (instanceToUse == null)
                {
                    // Load and instantiate the prefab
                    GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
                    if (prefab != null)
                    {
                        instanceToUse = UnityEngine.Object.Instantiate(prefab);
                        needsCleanup = true;
                    }
                }

                if (instanceToUse != null)
                {
                    try
                    {
                        // Get all mesh components
                        MeshFilter[] meshFilters = instanceToUse.GetComponentsInChildren<MeshFilter>();
                        SkinnedMeshRenderer[] skinnedRenderers = instanceToUse.GetComponentsInChildren<SkinnedMeshRenderer>();
                        Renderer[] allRenderers = instanceToUse.GetComponentsInChildren<Renderer>();

                        // Sum vertex and triangle counts from MeshFilters
                        foreach (MeshFilter mf in meshFilters)
                        {
                            if (mf.sharedMesh != null)
                            {
                                fbxData.vertexCount += mf.sharedMesh.vertexCount;
                                fbxData.triangleCount += mf.sharedMesh.triangles.Length / 3;
                                fbxData.meshCount++;
                            }
                        }

                        // Sum vertex and triangle counts from SkinnedMeshRenderers
                        foreach (SkinnedMeshRenderer smr in skinnedRenderers)
                        {
                            if (smr.sharedMesh != null)
                            {
                                fbxData.vertexCount += smr.sharedMesh.vertexCount;
                                fbxData.triangleCount += smr.sharedMesh.triangles.Length / 3;
                                fbxData.meshCount++;

                                // Count bones
                                if (smr.bones != null)
                                {
                                    fbxData.boneCount += smr.bones.Length;
                                }
                            }
                        }

                        // Count unique materials
                        HashSet<Material> uniqueMaterials = new HashSet<Material>();
                        foreach (Renderer renderer in allRenderers)
                        {
                            if (renderer.sharedMaterials != null)
                            {
                                foreach (Material mat in renderer.sharedMaterials)
                                {
                                    if (mat != null)
                                    {
                                        uniqueMaterials.Add(mat);
                                    }
                                }
                            }
                        }
                        fbxData.materialCount = uniqueMaterials.Count;
                    }
                    finally
                    {
                        // Cleanup instantiated prefab if we created it
                        if (needsCleanup)
                        {
                            UnityEngine.Object.DestroyImmediate(instanceToUse);
                        }
                    }
                }

                return fbxData;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Could not extract FBX data from '{fbxPath}': {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Extract FBX metadata and serialize to JSON string
        /// </summary>
        /// <param name="fbxPath">Path to the FBX file</param>
        /// <param name="instantiatedPrefab">Optional: already-instantiated prefab to extract from</param>
        /// <returns>JSON string of FBXData, or null if extraction failed</returns>
        public static string ExtractFBXDataAsJson(string fbxPath, GameObject instantiatedPrefab = null)
        {
            FBXData fbxData = ExtractFBXData(fbxPath, instantiatedPrefab);
            if (fbxData != null)
            {
                return JsonConvert.SerializeObject(fbxData);
            }
            return null;
        }
    }
}
