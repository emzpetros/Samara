using ImpossibleRobert.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditorInternal;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace AssetInventory
{
    public static class AssetUtils
    {
        private static readonly Regex NoSpecialChars = new Regex("[^a-zA-Z0-9 -]"); // private static Regex AssetStoreContext.s_InvalidPathCharsRegExp = new Regex("[^a-zA-Z0-9() _-]");
        private static readonly Dictionary<string, Texture2D> PreviewCache = new Dictionary<string, Texture2D>();

        public static bool IsPrefab(string mainFile)
        {
            return IOUtils.GetExtensionWithoutDot(mainFile).ToLowerInvariant() == "prefab";
        }

        public static bool CanAddToScene(string mainFile)
        {
            if (string.IsNullOrEmpty(mainFile)) return false;

            if (IsPrefab(mainFile)) return true;
            if (AI.IsFileType(mainFile, AI.AssetGroup.Models)) return true;
            if (AI.IsFileType(mainFile, AI.AssetGroup.Audio)) return true;

            return false;
        }

        public static void AddToScene(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath)) return;

            // Check if there's a selected GameObject in the hierarchy to use as parent
            GameObject selectedParent = Selection.activeGameObject;
            Transform parentTransform = selectedParent != null && selectedParent.scene.IsValid() ? selectedParent.transform : null;

            SceneView sceneView = SceneView.lastActiveSceneView;
            Vector3 targetPosition = sceneView != null ? sceneView.pivot : Vector3.zero;

            AddToScene(projectPath, targetPosition, parentTransform);
        }

        public static void AddToScene(string projectPath, Vector3 worldPosition, Transform parentTransform = null)
        {
            if (string.IsNullOrEmpty(projectPath)) return;

            GameObject instanceObj = null;

            // Try loading as GameObject first (works for prefabs, FBX, OBJ, and other model formats)
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(projectPath);
            if (prefab != null)
            {
                instanceObj = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if (instanceObj != null && IOUtils.GetExtensionWithoutDot(projectPath).Equals("fbx", StringComparison.OrdinalIgnoreCase))
                {
                    FBXMaterialFallbackResolver.ApplyExternalMaterialFallbacks(instanceObj, projectPath);
                }
            }
            else
            {
                // Handle audio clips: create a new GameObject with AudioSource (matches Unity's native drag behavior)
                AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(projectPath);
                if (clip != null)
                {
                    instanceObj = ObjectFactory.CreateGameObject(clip.name, typeof(AudioSource));
                    AudioSource audioSource = instanceObj.GetComponent<AudioSource>();
                    audioSource.clip = clip;
                }
            }

            if (instanceObj == null)
            {
                EditorUtility.DisplayDialog("Add To Scene", "Selected item cannot be added to the scene. Supported types: prefabs, 3D models (FBX, OBJ, ...), and audio clips.", "OK");
                return;
            }

            // Use provided parent, or check for selected GameObject in hierarchy, or use world position
            Transform finalParent = parentTransform;
            if (finalParent == null)
            {
                GameObject selectedParent = Selection.activeGameObject;
                if (selectedParent != null && selectedParent.scene.IsValid())
                {
                    finalParent = selectedParent.transform;
                }
            }

            if (finalParent != null)
            {
                instanceObj.transform.SetParent(finalParent, false);
                instanceObj.transform.localPosition = Vector3.zero;
            }
            else
            {
                instanceObj.transform.position = worldPosition;
            }

            Undo.RegisterCreatedObjectUndo(instanceObj, "Add Asset From Asset Inventory");
            Selection.activeGameObject = instanceObj;
            if (instanceObj.scene.IsValid()) EditorSceneManager.MarkSceneDirty(instanceObj.scene);
        }

        public static string GetProjectRoot()
        {
            return Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length).Replace("\\", "/");
        }

        public static bool IsAssetDatabasePath(string path, bool allowPackages = true)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            string normalized = path.Replace("\\", "/");
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) return true;
            return allowPackages && normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);
        }

        public static string GetAssetDatabasePath(string path, bool allowPackages = true)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            string normalized = path.Replace("\\", "/");
            if (IsAssetDatabasePath(normalized, allowPackages)) return normalized;

            string relativePath = IOUtils.MakeProjectRelative(path)?.Replace("\\", "/");
            return IsAssetDatabasePath(relativePath, allowPackages) ? relativePath : null;
        }

        public static string AddProjectRoot(string path)
        {
            if (!path.ToLowerInvariant().StartsWith("asset")) return path;

            return Path.Combine(GetProjectRoot(), path);
        }

        public static string RemoveProjectRoot(string path)
        {
            path = IOUtils.ToShortPath(path);
            string relativePath = IOUtils.MakeProjectRelative(path);
            return relativePath.Replace("\\", "/");
        }

        public static void RemoveLODGroups(string path)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            LODGroup[] groups = root.GetComponentsInChildren<LODGroup>(true);

            foreach (LODGroup group in groups)
            {
                if (group == null) continue;

                // handle prefabs recursively as GameObjects cannot be removed otherwise
                if (PrefabUtility.IsPartOfPrefabInstance(group.gameObject))
                {
                    string nestedPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(group.gameObject);
                    if (!string.IsNullOrEmpty(nestedPath) && nestedPath != path)
                    {
                        RemoveLODGroups(nestedPath);
                    }
                }
                else
                {
                    LOD[] lods = group.GetLODs();
                    for (int i = 1; i < lods.Length; i++)
                    {
                        Renderer[] renderers = lods[i].renderers;
                        for (int j = 0; j < renderers.Length; j++)
                        {
                            if (renderers[j] != null)
                            {
                                GameObject go = renderers[j].gameObject;

                                // try deleting directly first
                                bool retryNested = false;
                                try
                                {
                                    Object.DestroyImmediate(go);
                                }
                                catch (Exception)
                                {
                                    // this will happen if the object to delete is a child of the actual object to be deleted, e.g. a child inside a model file
                                    retryNested = true;
                                }

                                if (retryNested)
                                {
                                    // work our way up to the actual prefab instance root
                                    while (!PrefabUtility.IsAnyPrefabInstanceRoot(go) && go.transform.parent != null)
                                    {
                                        go = go.transform.parent.gameObject;
                                    }
                                    if (go.transform.parent != null) Object.DestroyImmediate(go);
                                }
                            }
                        }
                    }
                    Object.DestroyImmediate(group);
                }
            }

            // cannot be saved otherwise
            root.transform.RemoveMissingScripts();

            PrefabUtility.SaveAsPrefabAsset(root, path);
            PrefabUtility.UnloadPrefabContents(root);
        }

        public static int GetPageCount(int resultCount, int maxResults)
        {
            return (int)Math.Ceiling((double)resultCount / (maxResults > 0 ? maxResults : int.MaxValue));
        }

        public static void ClearCache()
        {
            foreach (Texture2D texture in PreviewCache.Values)
            {
                if (texture != null)
                {
                    Object.DestroyImmediate(texture);
                }
            }
            PreviewCache.Clear();
        }

        public static int RemoveMissingScripts(this Transform obj)
        {
            int result = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(obj.gameObject);
            for (int i = 0; i < obj.childCount; i++)
            {
                result += RemoveMissingScripts(obj.GetChild(i));
            }
            return result;
        }

        /// <summary>
        /// Loads an audio file from disk. Uses streaming by default for better performance.
        /// </summary>
        /// <param name="filePath">Path to the audio file</param>
        /// <param name="allowStreaming">If true, uses streaming for better performance. Set to false if you need to access raw sample data with GetData().</param>
        public static async Task<AudioClip> LoadAudioFromFile(string filePath, bool allowStreaming = true)
        {
            if (!File.Exists(filePath)) return null;

            // workaround for Unity not supporting loading local files with # or + or unicode chars in the name
            if (filePath.Contains("#") || filePath.Contains("+") || filePath.IsUnicode())
            {
                string newName = Path.Combine(Application.temporaryCachePath, "AIAudioPreview" + Path.GetExtension(filePath));
                File.Copy(filePath, newName, true);
                filePath = newName;
            }

            // use uri form to support network shares
            filePath = IOUtils.ToShortPath(filePath);
            string fileUri;
            try
            {
                fileUri = new Uri(filePath).AbsoluteUri;
            }
            catch (UriFormatException e)
            {
                Debug.LogError($"Could not convert path to URI '{filePath}': {e.Message}");
                return null;
            }

            // select appropriate audio type from extension where UNKNOWN heuristic can fail, especially for AIFF
            // retry with other types since some authors store especially wav files under the wrong format (e.g. ogg)
            List<AudioType> fallbackChain;
            switch (Path.GetExtension(filePath).ToLowerInvariant())
            {
                case ".aiff":
                case ".aif":
                    fallbackChain = new List<AudioType> {AudioType.AIFF, AudioType.OGGVORBIS, AudioType.WAV, AudioType.UNKNOWN};
                    break;

                case ".ogg":
                    fallbackChain = new List<AudioType> {AudioType.OGGVORBIS, AudioType.WAV, AudioType.UNKNOWN, AudioType.AIFF};
                    break;

                case ".wav":
                    fallbackChain = new List<AudioType> {AudioType.WAV, AudioType.OGGVORBIS, AudioType.UNKNOWN, AudioType.AIFF};
                    break;

                case ".mp3":
                    fallbackChain = new List<AudioType> {AudioType.MPEG, AudioType.WAV, AudioType.UNKNOWN, AudioType.AIFF};
                    break;

                default:
                    fallbackChain = new List<AudioType> {AudioType.UNKNOWN, AudioType.OGGVORBIS, AudioType.WAV, AudioType.AIFF};
                    break;
            }
            fallbackChain.AddRange(new List<AudioType> {AudioType.MPEG, AudioType.IT, AudioType.S3M, AudioType.XM, AudioType.ACC, AudioType.MOD, AudioType.VAG, AudioType.XMA, AudioType.AUDIOQUEUE});

            foreach (AudioType type in fallbackChain)
            {
                using (UnityWebRequest uwr = UnityWebRequestMultimedia.GetAudioClip(fileUri, type))
                {
                    // Streaming is a huge performance boost, but tracker formats (MOD, IT, S3M, XM) don't support streaming
                    // Also, streaming prevents access to raw sample data via GetData()
                    bool shouldStream = allowStreaming && (type == AudioType.OGGVORBIS ||
                        type == AudioType.MPEG ||
                        type == AudioType.WAV ||
                        type == AudioType.AIFF);
                    ((DownloadHandlerAudioClip)uwr.downloadHandler).streamAudio = shouldStream;
                    uwr.timeout = AI.Config.timeout;
                    UnityWebRequestAsyncOperation request = uwr.SendWebRequest();
                    while (!request.isDone) await Task.Yield();

                    if (uwr.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogError($"Error fetching '{filePath} ({fileUri})': {uwr.error}");
                        return null;
                    }

                    DownloadHandlerAudioClip dlHandler = (DownloadHandlerAudioClip)uwr.downloadHandler;
                    if (dlHandler.isDone)
                    {
                        // can fail if FMOD encounters incorrect file, will return zero-length then, error cannot be suppressed
                        AudioClip clip = dlHandler.audioClip;
                        if (clip == null || (clip.channels == 0 && clip.length == 0)) continue;

                        return clip;
                    }
                }
            }
            if (AI.Config.LogAudioParsing) Debug.LogError($"Unity could not load incompatible audio clip '{filePath} ({fileUri})'");

            return null;
        }

        public static async void LoadTextures(List<AssetInfo> infos, CancellationToken ct, Action<int, Texture2D> callback = null)
        {
            int chunkSize = AI.Config.previewChunkSize;

            for (int i = 0; i < infos.Count; i += chunkSize)
            {
                if (ct.IsCancellationRequested) break;

                List<Task> tasks = new List<Task>();

                int chunkEnd = Math.Min(i + chunkSize, infos.Count);
                for (int idx = i; idx < chunkEnd; idx++)
                {
                    int localIdx = idx; // capture value
                    AssetInfo info = infos[idx];

                    tasks.Add(ProcessAssetInfoAsync(info, localIdx, ct, callback));
                }
                await Task.WhenAll(tasks);
            }
        }

        private static async Task ProcessAssetInfoAsync(AssetInfo info, int idx, CancellationToken ct, Action<int, Texture2D> callback = null)
        {
            if (ct.IsCancellationRequested) return;

            if (info.ParentInfo != null)
            {
                await LoadPackageTexture(info.ParentInfo);
                info.PreviewTexture = info.ParentInfo.PreviewTexture;
            }
            else
            {
                await LoadPackageTexture(info);
            }
            callback?.Invoke(idx, info.PreviewTexture);
        }

        public static async Task LoadPackageTexture(AssetInfo info, bool useCache = true)
        {
            string file = info.ToAsset().GetPreviewFile(Paths.GetPreviewFolder());
            if (string.IsNullOrEmpty(file)) return;

            Texture2D texture;
            if (useCache && PreviewCache.TryGetValue(file, out Texture2D pt) && pt != null)
            {
                texture = pt;
            }
            else
            {
                texture = await LoadLocalTexture(file, true);
                if (texture != null)
                {
                    if (AI.Config.mediaCornerRadius > 0)
                    {
                        Texture2D roundedTexture = texture.WithRoundedCorners(AI.Config.mediaCornerRadius);
                        PreviewCache[file] = roundedTexture;

                        // Dispose of the original texture since we only need the rounded version
                        Object.DestroyImmediate(texture);

                        texture = roundedTexture; // Use the rounded texture for assignment below
                    }
                    else
                    {
                        PreviewCache[file] = texture;
                    }
                }
                else
                {
                    // texture could not be loaded, remove if it could never be loaded so far to auto-heal the state
                    if (PreviewCache.ContainsKey(file)) return;
                    if (AI.Config.LogMediaDownloads) Debug.LogWarning($"Could not load texture for {info.DisplayName} ({file}), removing from file system.");
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"Could not delete texture file '{file}': {e.Message}");
                    }
                }
            }
            if (texture != null) info.PreviewTexture = texture;
        }

        public static void RemoveFromPreviewCache(string file)
        {
            PreviewCache.Remove(file);
        }

        private static void DeleteFileInsidePreviews(string file)
        {
            // safety check to avoid deleting "original" preview files
            string previewFolder = Paths.GetPreviewFolder();
            if (string.IsNullOrEmpty(previewFolder)) return;
            if (!IOUtils.ToShortPath(file).StartsWith(IOUtils.ToShortPath(previewFolder))) return;

            File.Delete(file);
        }

        public static async Task<Texture2D> LoadLocalTexture(string file, bool useCache, int upscale = 0, bool upscaleIsMax = false)
        {
            file = IOUtils.ToShortPath(file);

            if (useCache && PreviewCache.TryGetValue(file, out Texture2D texture))
            {
                if (texture != null) return texture;

                PreviewCache.Remove(file); // entry became null, remove it
            }

            try
            {
                byte[] content = await Task.Run(() => IOUtils.ReadAllBytesWithShare(file));
                if (content == null || content.Length == 0)
                {
                    if (content != null && content.Length == 0)
                    {
                        DeleteFileInsidePreviews(file); // erroneous file, clean up right away
                        if (AI.Config.LogMediaDownloads) Debug.LogError($"Failed to read file data from '{file}'. Cleaning up automatically.");
                    }
                    else
                    {
                        if (AI.Config.LogMediaDownloads) Debug.LogError($"Failed to read file data from '{file}'.");
                    }
                    return null;
                }

                Texture2D result = new Texture2D(2, 2);
                if (!result.LoadImage(content))
                {
                    if (AI.Config.LogMediaDownloads) Debug.LogWarning($"Failed to load image from '{file}'. The data might be corrupted.");

                    // Dispose of the failed texture to prevent memory leak
                    Object.DestroyImmediate(result);
                    DeleteFileInsidePreviews(file); // erroneous file, clean up right away

                    return null;
                }

                result.hideFlags = HideFlags.HideAndDontSave;
                if (upscale > 0 && ((result.width < upscale && result.height < upscale) || upscaleIsMax))
                {
                    Texture2D original = result;
                    result = result.Downscale(upscale);
                    Object.DestroyImmediate(original);
                }

                if (useCache) PreviewCache[file] = result;

                return result;
            }
            catch (Exception e)
            {
                if (AI.Config.LogMediaDownloads) Debug.LogError($"Unhandled error loading local texture '{file}': {e.Message}");
                return null;
            }
        }

        public static async Task<T> FetchAPIData<T>(string uri, string method = "GET", string postContent = null, string token = null, string etag = null, Action<string> eTagCallback = null, int retries = 1, Action<long> responseIssueCodeCallback = null, bool suppressErrors = false, string postType = "application/json")
        {
            Restart:
            using (UnityWebRequest uwr = method == "GET" ? UnityWebRequest.Get(uri) : new UnityWebRequest(uri, method))
            {
                if (!string.IsNullOrEmpty(token)) uwr.SetRequestHeader("Authorization", "Bearer " + token);
                if (!string.IsNullOrEmpty(etag)) uwr.SetRequestHeader("If-None-Match", etag);
                if (!string.IsNullOrEmpty(postContent))
                {
                    byte[] bodyRaw = Encoding.UTF8.GetBytes(postContent);
                    uwr.uploadHandler = new UploadHandlerRaw(bodyRaw);
                    uwr.uploadHandler.contentType = postType;
                    uwr.downloadHandler = new DownloadHandlerBuffer();
                }
                uwr.SetRequestHeader("Content-Type", postType);
                uwr.SetRequestHeader("User-Agent", $"UnityEditor/{Application.unityVersion} ({SystemInfo.operatingSystemFamily}; {SystemInfo.operatingSystem})");
                uwr.timeout = AI.Config.timeout;
                UnityWebRequestAsyncOperation request = uwr.SendWebRequest();
                while (!request.isDone) await Task.Yield();

                if (uwr.result == UnityWebRequest.Result.ConnectionError)
                {
                    if (retries > 0)
                    {
                        retries--;
                        goto Restart;
                    }
                    if (!suppressErrors) Debug.LogError($"Could not fetch API data from {uri} due to network issues: {uwr.error}");
                }
                else if (uwr.result == UnityWebRequest.Result.ProtocolError)
                {
                    responseIssueCodeCallback?.Invoke(uwr.responseCode);
                    if (uwr.responseCode == (int)HttpStatusCode.Unauthorized)
                    {
                        if (!suppressErrors) Debug.LogError($"Invalid or expired API Token when contacting {uri}");
                    }
                    else
                    {
                        if (!suppressErrors) Debug.LogError($"Error fetching API data from {uri} ({uwr.responseCode}): {uwr.downloadHandler.text}");
                    }
                }
                else
                {
                    if (typeof (T) == typeof (string))
                    {
                        return (T)Convert.ChangeType(uwr.downloadHandler.text, typeof (T));
                    }

                    string newEtag = uwr.GetResponseHeader("ETag");
                    if (!string.IsNullOrEmpty(newEtag) && newEtag != etag) eTagCallback?.Invoke(newEtag);

                    try
                    {
                        return JsonConvert.DeserializeObject<T>(uwr.downloadHandler.text);
                    }
                    catch (Exception e)
                    {
                        // can happen if deserializers in local project have been added/altered
                        Debug.LogError($"Error parsing API data from {uri}: {e.Message}\n\n{uwr.downloadHandler.text}");
                    }
                }
            }

            return default(T);
        }

        public static async Task LoadImageAsync(string imageUrl, string targetFile)
        {
            using (UnityWebRequest uwr = UnityWebRequestTexture.GetTexture(imageUrl))
            {
                // Send the request and wait for the response without blocking the main thread
                uwr.timeout = AI.Config.timeout;
                UnityWebRequestAsyncOperation request = uwr.SendWebRequest();
                while (!request.isDone) await Task.Yield();

                if (uwr.result == UnityWebRequest.Result.ConnectionError || uwr.result == UnityWebRequest.Result.ProtocolError)
                {
                    if (AI.Config.LogMediaDownloads) Debug.LogWarning($"Failed to download image from {imageUrl}: {uwr.error}");
                }
                else
                {
                    Texture2D texture = DownloadHandlerTexture.GetContent(uwr);
                    byte[] imageBytes;
                    switch (IOUtils.GetExtensionWithoutDot(targetFile).ToLowerInvariant())
                    {
                        case "jpg":
                        case "jpeg":
                            imageBytes = texture.EncodeToJPG();
                            break;

                        case "tga":
                            imageBytes = texture.EncodeToTGA();
                            break;

                        default:
                            imageBytes = texture.EncodeToPNG();
                            break;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(targetFile));
                    int retries = 3;
                    do
                    {
                        try
                        {
                            await File.WriteAllBytesAsync(targetFile, imageBytes);
                            break;
                        }
                        catch (Exception e)
                        {
                            if (AI.Config.LogMediaDownloads) Debug.LogWarning($"Could not download image to {targetFile}, retrying: {e.Message}");

                            // can happen if file is locked (sharing violation)
                            retries--;
                            await Task.Delay(100);
                        }
                    } while (retries > 0);
                }
            }
        }

        // https://forum.unity.com/threads/handle-cannot-create-fmod-on-unitywebrequestmultimedia-getaudioclip.1139980/
        public static bool IsMp3File(string filePath)
        {
            byte[] mp3Header = {0xFF, 0xFB}; // Typical MP3 frame sync bits.
            byte[] id3Header = {0x49, 0x44, 0x33}; // 'ID3' in ASCII.
            byte[] bytes = new byte[3]; // Read the first three bytes of the file.

            using (FileStream file = File.OpenRead(filePath))
            {
                if (file.Length < 3)
                {
                    return false;
                }

                file.Read(bytes, 0, 3);
            }

            // Return true if we found an MP3 frame header or an ID3v2 tag.
            return bytes.SequenceEqual(mp3Header) || bytes.SequenceEqual(id3Header);
        }

        public static string GuessSafeName(string name, string replacement = "")
        {
            // remove special characters like Unity does when saving to disk
            // This will work in 99% of cases but sometimes items get renamed and
            // Unity will keep the old safe name so this needs to be synced with the 
            // download info API.
            string clean = name;

            // remove special characters
            clean = NoSpecialChars.Replace(clean, replacement);

            // remove duplicate spaces
            clean = Regex.Replace(clean, @"\s+", " ");

            return clean.Trim();
        }

        public static Dictionary<string, List<AssetInfo>> Guids2Files(List<string> guids, bool returnOriginIfNotFound = false, List<int> excludeIds = null, HashSet<string> guidsWithOrigin = null)
        {
            Dictionary<string, List<AssetInfo>> result = new Dictionary<string, List<AssetInfo>>();
            Dictionary<string, AssetOrigin> origins = new Dictionary<string, AssetOrigin>();

            // Check if Unity can give us a definite origin (2023+), otherwise guids will hit multiple assets potentially
            // When guidsWithOrigin is provided (batch pre-filter), only query origin for GUIDs known to have one
            foreach (string guid in guids)
            {
                result[guid] = new List<AssetInfo>(); // initialize

                if (guidsWithOrigin != null && !guidsWithOrigin.Contains(guid)) continue;

                AssetOrigin origin = AssetStore.GetAssetOrigin(guid);
                if (origin != null && origin.productId > 0) origins[guid] = origin;
            }

            List<AssetInfo> files = new List<AssetInfo>();
            if (origins.Count > 0)
            {
                string query = "with TempTable(ForeignId, Guid) as (";
                List<string> pairs = new List<string>();
                foreach (string guid in origins.Keys)
                {
                    pairs.Add($"select {origins[guid].productId}, '{guid}'");
                }
                query += string.Join(" union all ", pairs);
                query += ") select * from AssetFile inner join Asset on Asset.Id = AssetFile.AssetId ";
                query += "inner join TempTable on Asset.ForeignId = TempTable.ForeignId AND AssetFile.Guid = TempTable.Guid";

                if (excludeIds != null && excludeIds.Count > 0)
                {
                    query += $" where Asset.Id not in ({string.Join(",", excludeIds)})";
                    query += $" and Asset.ParentId not in ({string.Join(",", excludeIds)})"; // TODO: will misattribute children in deeper levels still
                }

                files.AddRange(DBAdapter.DB.Query<AssetInfo>($"{query}"));
            }

            // Retry origin GUIDs that didn't match (e.g. sub-packages with ForeignId=0
            // where Unity reports the parent's productId but the DB Asset has ForeignId=0)
            List<string> unmatchedOriginGuids = origins.Keys.Except(files.Select(f => f.Guid)).ToList();
            if (unmatchedOriginGuids.Count > 0)
            {
                // Match via parent's ForeignId for sub-packages
                string subQuery = "select *, AssetFile.Id as Id from AssetFile inner join Asset on Asset.Id = AssetFile.AssetId where Guid in (";
                subQuery += "'" + string.Join("','", unmatchedOriginGuids) + "')";

                if (excludeIds != null && excludeIds.Count > 0)
                {
                    subQuery += $" and Asset.Id not in ({string.Join(",", excludeIds)})";
                    subQuery += $" and Asset.ParentId not in ({string.Join(",", excludeIds)})";
                }

                files.AddRange(DBAdapter.DB.Query<AssetInfo>($"{subQuery}"));
            }

            List<string> noOrigin = guids.Except(origins.Keys).ToList();
            if (noOrigin.Count > 0)
            {
                string query = "select *, AssetFile.Id as Id from AssetFile inner join Asset on Asset.Id = AssetFile.AssetId where Guid in (";
                query += "'" + string.Join("','", noOrigin) + "')";

                if (excludeIds != null && excludeIds.Count > 0)
                {
                    query += $" and Asset.Id not in ({string.Join(",", excludeIds)})";
                    query += $" and Asset.ParentId not in ({string.Join(",", excludeIds)})"; // TODO: will misattribute children in deeper levels still
                }

                files.AddRange(DBAdapter.DB.Query<AssetInfo>($"{query}"));
            }

            // check for non-indexed assets
            List<string> nonIndexedGuids = guids.Except(files.Select(f => f.Guid)).ToList();
            if (nonIndexedGuids.Count > 0 && returnOriginIfNotFound)
            {
                foreach (string guid in nonIndexedGuids)
                {
                    AssetInfo ai = new AssetInfo();
                    ai.Guid = guid;
                    ai.CurrentState = Asset.State.Unknown;
                    if (origins.ContainsKey(guid))
                    {
                        ai.SafeName = origins[guid].packageName;
                        ai.ForeignId = origins[guid].productId;
                        ai.ProjectPath = origins[guid].assetPath;
                    }
                    files.Add(ai);
                }
            }

            // generate result
            files.ForEach(a =>
            {
                // add back origin information
                if (origins.TryGetValue(a.Guid, out AssetOrigin origin))
                {
                    a.Origin = origin;
                    a.DisplayName = origin.packageName;
                    a.Version = origin.packageVersion;
                    a.ProjectPath = origin.assetPath;
                    a.UploadId = origin.uploadId;
                }

                // use real path (asset origin will contain original package path)
                a.ProjectPath = AssetDatabase.GUIDToAssetPath(a.Guid);

                result[a.Guid].Add(a);
            });

            return result;
        }

        public static string ExtractGuidFromFile(string path)
        {
            string guid = null;
            try
            {
                using (StreamReader sr = new StreamReader(IOUtils.ToLongPath(path), Encoding.UTF8, true, 4096))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.StartsWith("guid:") || line.StartsWith("guid :")) // both can exist
                        {
                            guid = line;
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error reading guid from '{path}': {e.Message}");
                return null;
            }

            if (string.IsNullOrEmpty(guid))
            {
                Debug.LogWarning($"Could not find guid in meta file: {path}");
                return null;
            }

            return guid.Substring(guid.IndexOf(':') + 1).Trim();
        }

        public static bool UpdateGuidInMetaFile(string metaPath, string newGuid)
        {
            if (string.IsNullOrEmpty(metaPath) || string.IsNullOrEmpty(newGuid)) return false;

            try
            {
                string[] lines = File.ReadAllLines(IOUtils.ToLongPath(metaPath));
                bool found = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith("guid:") || lines[i].StartsWith("guid :"))
                    {
                        // Skip rewrite if GUID already matches to avoid unnecessary file changes
                        string existingGuid = lines[i].Substring(lines[i].IndexOf(':') + 1).Trim();
                        if (existingGuid == newGuid) return false;

                        lines[i] = $"guid: {newGuid}";
                        found = true;
                        break;
                    }
                }

                if (found)
                {
                    File.WriteAllLines(IOUtils.ToLongPath(metaPath), lines);
                    return true;
                }

                Debug.LogWarning($"Could not find guid line in meta file: {metaPath}");
                return false;
            }
            catch (Exception e)
            {
                Debug.LogError($"Error updating guid in '{metaPath}': {e.Message}");
                return false;
            }
        }

        public static bool IsOnURP()
        {
            RenderPipelineAsset rpa = GraphicsSettings.defaultRenderPipeline;
            if (rpa == null) return false;

            return rpa.GetType().Name.Contains("UniversalRenderPipelineAsset");
        }

        public static bool IsOnHDRP()
        {
            RenderPipelineAsset rpa = GraphicsSettings.defaultRenderPipeline;
            if (rpa == null) return false;

            return rpa.GetType().Name.Contains("HDRenderPipelineAsset");
        }

        /// <summary>
        /// Checks if a package is compatible with the current project's render pipeline.
        /// Package-level flags: URPCompatible, HDRPCompatible, BIRPCompatible.
        /// </summary>
        /// <param name="asset">Asset/package to check</param>
        /// <returns>True if package is compatible with current render pipeline</returns>
        public static bool IsPackageCompatibleWithCurrentSRP(AssetInfo asset)
        {
            if (asset == null) return true;

            if (IsOnURP())
            {
                // On URP: Exclude HDRP-only packages
                return !(asset.HDRPCompatible && !asset.URPCompatible);
            }
            if (IsOnHDRP())
            {
                // On HDRP: Exclude URP-only packages
                return !(asset.URPCompatible && !asset.HDRPCompatible);
            }
            // BIRP: Exclude all SRP support packages
            return !((asset.URPCompatible || asset.HDRPCompatible) && !asset.BIRPCompatible);
        }

        /// <summary>
        /// Filters out files from render pipeline support packages that are incompatible with the current project's render pipeline.
        /// For BIRP projects, removes all URP and HDRP support packages.
        /// For URP projects, removes HDRP-only support packages.
        /// For HDRP projects, removes URP-only support packages.
        /// </summary>
        /// <param name="files">List of AssetInfo files to filter</param>
        public static void FilterIncompatibleSRPPackages(List<AssetInfo> files)
        {
            if (files == null || files.Count == 0) return;

            if (IsOnURP())
            {
                files.RemoveAll(item => item.HDRPCompatible && !item.URPCompatible);
            }
            else if (IsOnHDRP())
            {
                files.RemoveAll(item => item.URPCompatible && !item.HDRPCompatible);
            }
            else
            {
                // BIRP: Remove all SRP support packages
                files.RemoveAll(item => (item.URPCompatible || item.HDRPCompatible) && !item.BIRPCompatible);
            }
        }

        public static string GetURPVersion()
        {
            PackageInfo packageInfo = PackageInfo.FindForAssetPath("Packages/com.unity.render-pipelines.universal");
            if (packageInfo == null) return null;

            return packageInfo.version.Split('.').First();
        }

        public static string GetHDRPVersion()
        {
            PackageInfo packageInfo = PackageInfo.FindForAssetPath("Packages/com.unity.render-pipelines.high-definition");
            if (packageInfo == null) return null;

            return packageInfo.version.Split('.').First();
        }

        public static bool IsUnityProject(string folder)
        {
            return Directory.Exists(Path.Combine(folder, "Assets"))
                && Directory.Exists(Path.Combine(folder, "Library"))
                && Directory.Exists(Path.Combine(folder, "Packages"))
                && Directory.Exists(Path.Combine(folder, "ProjectSettings"));
        }

        private static bool MatchesBIRPKeywords(string name)
        {
            // Normalize underscores to spaces for Unity safe names (e.g., "Built_in" -> "Built in")
            string normalized = name.Replace('_', ' ');
            
            return normalized.Contains("birp", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("standard rp", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("bi rp", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("built-in", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("build-in", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("built in", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("build in", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("buildin", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("builtin", StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesURPKeywords(string name)
        {
            // Normalize underscores to spaces for Unity safe names (e.g., "Universal_RP" -> "Universal RP")
            string normalized = name.Replace('_', ' ');
            
            return normalized.Contains("urp", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("lwrp", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("universalrp", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("universal rp", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("universal render", StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesHDRPKeywords(string name)
        {
            // Normalize underscores to spaces for Unity safe names (e.g., "HD_RP" -> "HD RP")
            string normalized = name.Replace('_', ' ');
            
            return normalized.Contains("hdrp", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("hd rp", StringComparison.OrdinalIgnoreCase);
        }

        private static bool CheckSRPCompatibility(string assetName, Func<string, bool> matchPredicate)
        {
            // Handle "X to Y" pattern - prioritize the part after "to" (target), fall back to before (source)
            // Support both display names (" to ") and safe names ("_to_")
            int toIndex = -1;
            int toLength = 4;
            
            // Check for " to " first (display names)
            toIndex = assetName.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
            
            // Check for "_to_" pattern (safe names)
            if (toIndex < 0)
            {
                toIndex = assetName.IndexOf("_to_", StringComparison.OrdinalIgnoreCase);
            }
            
            if (toIndex >= 0)
            {
                string afterTo = assetName.Substring(toIndex + toLength);
                
                // Check if target matches this SRP
                if (matchPredicate(afterTo)) return true;
                
                // Only fall back to source if target doesn't contain ANY SRP keywords
                // (e.g., "URP 12.1 To 16" where "16" is just a version number)
                // If target contains other SRP keywords (e.g., "urp to built in"), don't fall back
                bool targetContainsSRP = MatchesBIRPKeywords(afterTo) || MatchesURPKeywords(afterTo) || MatchesHDRPKeywords(afterTo);
                if (!targetContainsSRP)
                {
                    string beforeTo = assetName.Substring(0, toIndex);
                    return matchPredicate(beforeTo);
                }
                
                return false;
            }
            
            return matchPredicate(assetName);
        }

        public static bool ShouldBeBIRPCompatible(string assetName)
        {
            return CheckSRPCompatibility(assetName, MatchesBIRPKeywords);
        }

        public static bool ShouldBeURPCompatible(string assetName)
        {
            return CheckSRPCompatibility(assetName, MatchesURPKeywords);
        }

        public static bool ShouldBeHDRPCompatible(string assetName)
        {
            return CheckSRPCompatibility(assetName, MatchesHDRPKeywords);
        }

        public static List<Rect> CreateUVs(int columns, int rows)
        {
            List<Rect> rects = new List<Rect>();

            float frameWidth = 1f / columns;
            float frameHeight = 1f / rows;

            for (int y = rows - 1; y >= 0; y--)
            {
                for (int x = 0; x < columns; x++)
                {
                    Rect rect = new Rect(x * frameWidth, y * frameHeight, frameWidth, frameHeight);
                    rects.Add(rect);
                }
            }

            return rects;
        }

        public static Texture2D ExtractFrame(Texture2D sourceTexture, Rect uvRect)
        {
            int x = Mathf.RoundToInt(uvRect.x * sourceTexture.width);
            int y = Mathf.RoundToInt(uvRect.y * sourceTexture.height);
            int width = Mathf.RoundToInt(uvRect.width * sourceTexture.width);
            int height = Mathf.RoundToInt(uvRect.height * sourceTexture.height);

            // No y-flip needed - CreateUVs already generates coordinates that match AssembleTextureSheet's layout

            // Create a new Texture2D to hold the frame
            Texture2D frameTexture = new Texture2D(width, height, sourceTexture.format, false);
            frameTexture.hideFlags = HideFlags.HideAndDontSave;
            frameTexture.SetPixels(sourceTexture.GetPixels(x, y, width, height));
            frameTexture.Apply();

            return frameTexture;
        }

        /// <summary>
        /// Converts a binary serialized Unity file to YAML text format.
        /// </summary>
        /// <param name="binaryPath">Path to the binary serialized file</param>
        /// <param name="targetPath">Target path for the YAML text file</param>
        /// <returns>True if conversion succeeded, false otherwise</returns>
        public static bool ConvertBinaryToYaml(string binaryPath, string targetPath)
        {
            try
            {
                UnityEngine.Object[] objects = InternalEditorUtility.LoadSerializedFileAndForget(binaryPath);
                if (objects != null && objects.Length > 0)
                {
                    InternalEditorUtility.SaveToSerializedFileAndForget(objects, targetPath, true);
                    return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Could not convert binary file using Unity API: {e.Message}");
            }
            return false;
        }
    }
}
