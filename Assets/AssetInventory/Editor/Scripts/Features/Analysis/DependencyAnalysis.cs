using ImpossibleRobert.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using static AssetInventory.AssetInfo;

namespace AssetInventory
{
    public class DependencyAnalysis
    {
        private static readonly Regex GuidReference = new Regex(@"(?:guid:\s*|\\*""guid\\*""\s*:\s*\\*"")([a-f0-9]{32})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Matches material entries in fileIDToRecycleName: fileID 2100000+ (classID 21 = Material)
        private static readonly Regex ModelMaterialName = new Regex(@"^\s+(\d+):\s+(.+)$", RegexOptions.Compiled | RegexOptions.Multiline);
        // Matches material entries in internalIDToNameTable: classID 21 = Material (modern Unity, serializedVersion 22200+)
        private static readonly Regex ModelInternalIDMaterialName = new Regex(@"-\s+first:\s+21:\s+\d+\s+second:\s+(.+)", RegexOptions.Compiled);
        private static readonly Regex IncludeFilesRegex = new Regex(@"#include(?:_with_pragmas)?\s*""(.+?)""", RegexOptions.Compiled);
        private static readonly Regex CustomEditorRegex = new Regex(@"CustomEditor\s*""(.+?)""", RegexOptions.Compiled); // Regex to match custom editor lines and capture names

        // MTL file parsing patterns
        private static readonly Regex MtlNewMaterialRegex = new Regex(@"^newmtl\s+(.+)$", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex MtlTextureMapRegex = new Regex(@"^\s*(?:map_Kd|map_Ka|map_Ks|map_Ns|map_d|map_Tr|map_bump|map_Ke|bump|disp|decal|refl|norm|map_Pr|map_Pm)\s+(?:.*\s+)?(.+\..+)$", RegexOptions.Compiled | RegexOptions.Multiline);

        // Script code analysis patterns
        private static readonly Regex InheritanceRegex = new Regex(@"(?:class|struct|interface)\s+\w+(?:<[^>]+>)?\s*:\s*([^{]+)", RegexOptions.Compiled);
        private static readonly Regex TypeReferenceRegex = new Regex(@"\b([A-Z][a-zA-Z0-9_]*)\b", RegexOptions.Compiled);
        private static readonly Regex AttributeRegex = new Regex(@"\[\s*([A-Z][a-zA-Z0-9_]*)", RegexOptions.Compiled);
        private static readonly Regex AsmdefReferenceRegex = new Regex(@"""references"":\s*\[(.*?)\]", RegexOptions.Compiled | RegexOptions.Singleline);

        // Script-related file types for "All Scripts" mode
        private static readonly string[] ScriptRelatedTypes =
        {
            "cs", "dll", "so", "aar", "a", "bundle", "h", "mm", "c", "winmd",
            "pdb", "jslib", "asmdef", "asmref", "rsp", "modulecompilationtrigger", "rej", "inputactions", "proto"
        };

        // Script-related filenames that can trigger recompilation but can't be filtered by extension alone
        private static readonly string[] ScriptRelatedFilenames = {"package.json", "manifest.json", "link.xml"};

        /// <summary>
        /// Checks whether an AssetFile is script-related (by type or specific filename).
        /// Files matching this are excluded during "Never Import" script mode and preview generation.
        /// </summary>
        internal static bool IsScriptRelated(AssetFile af)
        {
            return ScriptRelatedTypes.Contains(af.Type) || ScriptRelatedFilenames.Contains(af.FileName);
        }

        /// <summary>
        /// Returns a SQL WHERE clause fragment that matches script-related files.
        /// Usage: $"SELECT * FROM AssetFile WHERE AssetId=? AND {ScriptRelatedSqlFilter()}"
        /// </summary>
        internal static string ScriptRelatedSqlFilter()
        {
            string typeFilter = string.Join("','", ScriptRelatedTypes);
            string nameFilter = string.Join("','", ScriptRelatedFilenames);
            return $"(Type IN ('{typeFilter}') OR FileName IN ('{nameFilter}'))";
        }

        /// <summary>
        /// Returns a SQL WHERE clause fragment that excludes script-related files.
        /// Usage: $"SELECT * FROM AssetFile WHERE AssetId=? AND {ScriptRelatedSqlNotFilter()}"
        /// </summary>
        internal static string ScriptRelatedSqlNotFilter()
        {
            string typeFilter = string.Join("','", ScriptRelatedTypes);
            string nameFilter = string.Join("','", ScriptRelatedFilenames);
            return $"(Type NOT IN ('{typeFilter}') AND FileName NOT IN ('{nameFilter}'))";
        }

        // Built-in types to exclude from code analysis
        private static readonly HashSet<string> BuiltInTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // C# primitive types and keywords
            "String", "Int32", "Int64", "Boolean", "Float", "Double", "Decimal", "Object", "Void",
            "Byte", "SByte", "Int16", "UInt16", "UInt32", "UInt64", "Char", "Single",
            "Nullable", "Guid", "Type", "Attribute", "Delegate", "IntPtr", "UIntPtr",
            "TimeSpan", "DateTime", "DateTimeOffset", "Enum", "ValueType",
            // Common Unity types (always available)
            "MonoBehaviour", "ScriptableObject", "GameObject", "Transform", "Component",
            "Vector2", "Vector3", "Vector4", "Vector2Int", "Vector3Int", "Quaternion", "Matrix4x4",
            "Color", "Color32", "Rect", "RectInt", "Bounds", "BoundsInt", "Ray", "Ray2D", "Plane",
            "Material", "Texture", "Texture2D", "Texture3D", "RenderTexture", "Sprite", "Shader",
            "Mesh", "MeshFilter", "MeshRenderer", "SkinnedMeshRenderer", "Renderer",
            "Camera", "Light", "AudioClip", "AudioSource", "AudioListener",
            "Rigidbody", "Rigidbody2D", "Collider", "Collider2D", "BoxCollider", "SphereCollider",
            "Canvas", "RectTransform", "Image", "Text", "Button", "Toggle", "Slider", "Dropdown",
            "Animator", "Animation", "AnimationClip", "AnimationCurve", "Keyframe",
            "ParticleSystem", "LineRenderer", "TrailRenderer", "SpriteRenderer",
            "LayerMask", "WaitForSeconds", "WaitForEndOfFrame", "WaitForFixedUpdate", "Coroutine",
            "Debug", "Application", "Time", "Input", "Physics", "Physics2D", "Screen", "Cursor",
            "Mathf", "Random", "PlayerPrefs", "Resources", "AssetBundle", "SceneManager",
            "GUILayout", "GUIStyle", "GUIContent", "GUI", "GUISkin", "Event",
            // Unity Editor types
            "AssetDatabase", "Editor", "EditorWindow", "EditorGUILayout", "EditorGUI",
            "SerializedProperty", "SerializedObject", "Undo", "Selection", "PrefabUtility",
            "Handles", "Gizmos", "SceneView", "EditorApplication", "EditorUtility",
            "MenuItem", "CustomEditor", "CustomPropertyDrawer", "PropertyDrawer", "DecoratorDrawer",
            // Unity methods commonly detected as types
            "Destroy", "Instantiate", "DontDestroyOnLoad", "FindObjectOfType", "FindObjectsOfType",
            "GetComponent", "GetComponents", "AddComponent", "GetComponentInChildren", "GetComponentsInChildren",
            "StartCoroutine", "StopCoroutine", "StopAllCoroutines", "Invoke", "InvokeRepeating", "CancelInvoke",
            // Common .NET types and namespaces
            "List", "Dictionary", "HashSet", "Queue", "Stack", "Array", "ArrayList",
            "LinkedList", "SortedList", "SortedDictionary", "SortedSet", "KeyValuePair",
            "Collections", "Generic", "Linq", "Text", "Threading", "IO", "Reflection", "Concurrent",
            "Task", "Action", "Func", "Predicate", "Comparison", "Converter", "EventHandler",
            "IEnumerable", "IEnumerator", "ICollection", "IList", "IDictionary", "ISet",
            "IDisposable", "IComparable", "IEquatable", "ICloneable", "IFormattable",
            "IReadOnlyList", "IReadOnlyCollection", "IReadOnlyDictionary",
            "Exception", "ArgumentException", "ArgumentNullException", "ArgumentOutOfRangeException",
            "InvalidOperationException", "NotImplementedException", "NotSupportedException",
            "NullReferenceException", "IndexOutOfRangeException", "KeyNotFoundException",
            "ObjectDisposedException", "OperationCanceledException", "TimeoutException", "FormatException",
            "File", "Directory", "Path", "Stream", "StreamReader", "StreamWriter", "FileStream", "MemoryStream",
            "BinaryReader", "BinaryWriter", "TextReader", "TextWriter", "StringReader", "StringWriter",
            "Regex", "Match", "Group", "Capture", "MatchCollection", "GroupCollection",
            "StringBuilder", "Encoding", "UTF8Encoding", "ASCIIEncoding", "UnicodeEncoding",
            "Convert", "BitConverter", "Math", "Buffer", "GC", "Environment", "Console",
            "CancellationToken", "CancellationTokenSource", "TaskCompletionSource",
            "Tuple", "ValueTuple", "Span", "Memory", "ReadOnlySpan", "ReadOnlyMemory",
            "Thread", "ThreadPool", "Monitor", "Mutex", "Semaphore", "ManualResetEvent", "AutoResetEvent",
            "Interlocked", "Volatile", "SpinLock", "SpinWait", "Barrier", "CountdownEvent",
            // Common attributes
            "Serializable", "SerializeField", "NonSerialized", "Header", "Tooltip", "Space",
            "Range", "Min", "Max", "TextArea", "Multiline", "ColorUsage", "GradientUsage",
            "HideInInspector", "FormerlySerializedAs", "ContextMenu", "ContextMenuItemAttribute",
            "RequireComponent", "DisallowMultipleComponent", "ExecuteInEditMode", "ExecuteAlways",
            "AddComponentMenu", "CreateAssetMenu", "SelectionBase", "DefaultExecutionOrder",
            "Conditional", "Obsolete", "Flags", "DllImport", "StructLayout", "FieldOffset",
            "MethodImpl", "CallerMemberName", "CallerFilePath", "CallerLineNumber"
        };

        // Thread-safe unique temp folder for this analysis instance
        private readonly string _uniqueTempFolder;
        private readonly string _uniqueTempFolderPath;

        // Cached pipeline checks to avoid repeated calls during dependency analysis
        private readonly bool _isOnURP;
        private readonly bool _isOnHDRP;
        private readonly bool _isOnBIRP;

        private static readonly string[] ScanDependencies =
        {
            "prefab", "mat", "controller", "overridecontroller", "anim", "asset", "physicmaterial", "physicsmaterial",
            "sbs", "sbsar", "cubemap", "shader", "cginc", "hlsl", "shadergraph", "shadersubgraph",
            "terrainlayer", "inputactions", "vfx", "vfxoperator", "unity", "preset"
        };

        private static readonly string[] ScanMetaDependencies =
        {
            "shader", "ttf", "otf", "js", "obj", "fbx", "uxml", "uss", "inputactions", "tss", "nn", "cs"
        };

        private static readonly HashSet<string> BinaryYamlConversionTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "prefab"
        };

        private static readonly HashSet<string> TextSerializedGuidReferenceTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "shadergraph",
            "shadersubgraph",
            "vfx"
        };

        // Optional cache for memorizing dependency results across multiple files
        private readonly DependencyResultCache _cache;

        public DependencyAnalysis(DependencyResultCache cache = null)
        {
            // Generate unique temp folder name with GUID suffix for thread safety
            string uniqueId = Guid.NewGuid().ToString("N").Substring(0, 8);
            _uniqueTempFolder = $"{AI.TEMP_FOLDER}_{uniqueId}";
            _uniqueTempFolderPath = Path.Combine(Application.dataPath, _uniqueTempFolder);

            // Cache pipeline checks once per analysis session
            _isOnURP = AssetUtils.IsOnURP();
            _isOnHDRP = AssetUtils.IsOnHDRP();
            _isOnBIRP = !_isOnURP && !_isOnHDRP;

            _cache = cache;
        }

        public static bool NeedsScan(string type)
        {
            return ScanDependencies.Contains(type) || ScanMetaDependencies.Contains(type);
        }

        internal static bool ShouldAttemptBinaryYamlConversion(string extension)
        {
            return !string.IsNullOrEmpty(extension) && BinaryYamlConversionTypes.Contains(extension);
        }

        internal static bool ShouldTryBinaryYamlConversionBeforePrefabLoad(string extension, string content)
        {
            return !string.IsNullOrEmpty(content) &&
                !content.StartsWith("%YAML") &&
                ShouldAttemptBinaryYamlConversion(extension);
        }

        internal static bool ShouldScanTextSerializedGuidReferencesDirectly(string extension)
        {
            return !string.IsNullOrEmpty(extension) && TextSerializedGuidReferenceTypes.Contains(extension);
        }

        internal static List<string> ExtractDependencyGuids(string content, string extension, string selfGuid = null, IEnumerable<string> processedGuids = null)
        {
            if (string.IsNullOrEmpty(content)) return new List<string>();

            HashSet<string> processed = processedGuids == null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(processedGuids.Where(g => !string.IsNullOrEmpty(g)), StringComparer.OrdinalIgnoreCase);

            return GuidReference.Matches(content)
                .Cast<Match>()
                .Select(m => m.Groups[1].Value.ToLowerInvariant())
                .Where(g => !string.Equals(g, selfGuid, StringComparison.OrdinalIgnoreCase) && !processed.Contains(g))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static List<string> FindUnknownDependencyGuids(string content, string extension, IEnumerable<string> knownGuids, string selfGuid = null)
        {
            HashSet<string> known = knownGuids == null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(knownGuids.Where(g => !string.IsNullOrEmpty(g)), StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(selfGuid)) known.Add(selfGuid);

            return ExtractDependencyGuids(content, extension, selfGuid)
                .Where(g => !known.Contains(g))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Cleans up all temporary folders used for dependency analysis.
        /// This should be called during application initialization to remove orphaned folders from previous sessions.
        /// </summary>
        public static void CleanUp()
        {
            try
            {
                string assetsPath = Application.dataPath;
                if (!Directory.Exists(assetsPath)) return;

                // Find all temp folders matching the pattern _AssetInventoryTemp*
                string[] allDirs = Directory.GetDirectories(assetsPath, $"{AI.TEMP_FOLDER}*", SearchOption.TopDirectoryOnly);
                foreach (string dir in allDirs)
                {
                    try
                    {
                        string dirName = Path.GetFileName(dir);
                        // Only delete folders that match our pattern (base name or base name with underscore and ID)
                        if (dirName == AI.TEMP_FOLDER || dirName.StartsWith(AI.TEMP_FOLDER + "_"))
                        {
                            Directory.Delete(dir, true);
                            string metaFile = dir + ".meta";
                            if (File.Exists(metaFile)) File.Delete(metaFile);
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"Could not remove temporary dependency analysis folder '{dir}': {e.Message}");
                    }
                }
                AssetDatabase.Refresh();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Could not clean up temporary dependency analysis folders: {e.Message}");
            }
        }

        public async Task Analyze(AssetInfo info, CancellationToken ct)
        {
            info.DependencyState = DependencyStateOptions.Calculating;
            info.Dependencies = new List<AssetFile>();
            info.CrossPackageDependencies = new List<Asset>();

            // Pre-load AssetFile cache for this package if cache is available
            if (_cache != null && !_cache.HasPackageCache(info.AssetId))
            {
                List<AssetFile> packageFiles = DBAdapter.DB.Query<AssetFile>(
                    "SELECT * FROM AssetFile WHERE AssetId=?", info.AssetId);
                _cache.PreloadPackageFiles(info.AssetId, packageFiles);
            }

            // Check cache first for bulk-imported files (already in project as YAML)
            string targetPath = null;
            string mainCacheKey = DependencyResultCache.GetMaterializedPathKey(info);
            if (_cache != null && _cache.TryGetMaterializedPath(mainCacheKey, out string cachedMainPath))
            {
                // Cached path is project-relative, convert to absolute for file reading
                string fullPath = AssetUtils.AddProjectRoot(cachedMainPath);
                if (File.Exists(fullPath))
                {
                    targetPath = fullPath;
                }
            }

            // Fallback to external cache materialization if not in project
            if (targetPath == null)
            {
                targetPath = await Assets.EnsureMaterialized(info, false, ct);
                if (targetPath == null)
                {
                    info.DependencyState = ct.IsCancellationRequested
                        ? DependencyStateOptions.Unknown
                        : DependencyStateOptions.Failed;
                    return;
                }
            }

            PreparePipelineDependencies(info);

            // work on a copy in case SRP will be used to not mess with the original data
            AssetInfo workInfo = new AssetInfo(info);
            if (info.SRPMainReplacement != null)
            {
                workInfo.CopyFrom(info.SRPSupportPackage, info.SRPMainReplacement);

                // Check cache for SRP file first
                string srpCacheKey = DependencyResultCache.GetMaterializedPathKey(workInfo);
                if (_cache != null && _cache.TryGetMaterializedPath(srpCacheKey, out string cachedSrpPath))
                {
                    string fullPath = AssetUtils.AddProjectRoot(cachedSrpPath);
                    if (File.Exists(fullPath))
                    {
                        targetPath = fullPath;
                    }
                    else
                    {
                        targetPath = await Assets.EnsureMaterialized(workInfo, false, ct);
                    }
                }
                else
                {
                    targetPath = await Assets.EnsureMaterialized(workInfo, false, ct);
                }

                if (targetPath == null)
                {
                    info.DependencyState = ct.IsCancellationRequested
                        ? DependencyStateOptions.Unknown
                        : DependencyStateOptions.Failed;
                    return;
                }
            }

            // calculate
            ConcurrentDictionary<string, bool> processedGuids = new ConcurrentDictionary<string, bool>();
            List<AssetFile> deps = await DoCalculateDependencies(workInfo, targetPath, processedGuids, null, ct, workInfo.Guid);

            // free up memory
            if (!workInfo.SRPUsed)
            {
                info.SRPOriginalBackup = null;
                info.SRPSupportPackage = null;
                info.SRPMainReplacement = null;
            }

            // ensure unique dependencies
            info.CrossPackageDependencies = workInfo.CrossPackageDependencies.GroupBy(d => d.Id).Select(g => g.First()).ToList();

            if (deps == null)
            {
                info.DependencyState = DependencyStateOptions.Failed;
                return;
            }
            info.DependencyState = workInfo.DependencyState;

            info.Dependencies = deps
                .OrderBy(f => f.AssetId)
                .ThenBy(f => f.Path)
                .ThenBy(f => f.Type)
                .ToList();
            info.DependencySize = info.Dependencies.Sum(af => af.Size);
            info.MediaDependencies = info.Dependencies.Where(af => !IsScriptRelated(af)).ToList();
            info.ScriptDependencies = info.Dependencies.Where(IsScriptRelated).ToList();

            // Extended script analysis (code-level dependencies + assembly definitions)
            if (AI.Config.scriptImportMode == 3)
            {
                // Include the initial file if it's a script (CS files don't get GUID-scanned)
                List<AssetFile> scriptsToAnalyze = new List<AssetFile>(info.ScriptDependencies);
                string initialExtension = IOUtils.GetExtensionWithoutDot(targetPath).ToLowerInvariant();
                if (initialExtension == "cs" && !string.IsNullOrEmpty(info.Guid))
                {
                    AssetFile initialScript = DBAdapter.DB.Find<AssetFile>(a => a.Guid == info.Guid && a.AssetId == info.AssetId);
                    if (initialScript != null && !scriptsToAnalyze.Any(s => s.Id == initialScript.Id))
                    {
                        scriptsToAnalyze.Insert(0, initialScript);
                    }
                }

                List<AssetFile> extendedDeps = await AnalyzeExtendedScriptDependencies(workInfo, scriptsToAnalyze, ct);
                if (extendedDeps.Count > 0)
                {
                    // Add to dependencies, avoiding duplicates, and merge ParentGuids
                    Dictionary<int, AssetFile> existingById = info.Dependencies
                        .GroupBy(d => d.Id)
                        .ToDictionary(g => g.Key, g => g.First());
                    foreach (AssetFile dep in extendedDeps)
                    {
                        if (!existingById.ContainsKey(dep.Id))
                        {
                            info.Dependencies.Add(dep);
                            existingById[dep.Id] = dep;
                        }
                        else if (dep.ParentGuids != null && dep.ParentGuids.Count > 0)
                        {
                            // Merge ParentGuids from extended analysis to existing dependency
                            AssetFile existing = existingById[dep.Id];
                            if (existing.ParentGuids == null) existing.ParentGuids = new HashSet<string>();
                            foreach (string pg in dep.ParentGuids)
                            {
                                if (pg != existing.Guid) existing.ParentGuids.Add(pg);
                            }
                        }
                    }

                    // Re-sort and recalculate
                    info.Dependencies = info.Dependencies
                        .OrderBy(f => f.AssetId)
                        .ThenBy(f => f.Path)
                        .ThenBy(f => f.Type)
                        .ToList();
                    info.DependencySize = info.Dependencies.Sum(af => af.Size);
                    info.ScriptDependencies = info.Dependencies.Where(IsScriptRelated).ToList();
                }
            }
            // All Scripts mode - include all script-related files from the package
            else if (AI.Config.scriptImportMode == 4)
            {
                List<AssetFile> allScriptFiles = DBAdapter.DB.Query<AssetFile>(
                    $"SELECT * FROM AssetFile WHERE AssetId=? AND {ScriptRelatedSqlFilter()}",
                    info.AssetId);

                // Add to dependencies, avoiding duplicates
                HashSet<int> existingIds = new HashSet<int>(info.Dependencies.Select(d => d.Id));
                foreach (AssetFile scriptFile in allScriptFiles)
                {
                    if (!existingIds.Contains(scriptFile.Id))
                    {
                        info.Dependencies.Add(scriptFile);
                        existingIds.Add(scriptFile.Id);
                    }
                }

                // Re-sort and recalculate
                info.Dependencies = info.Dependencies
                    .OrderBy(f => f.AssetId)
                    .ThenBy(f => f.Path)
                    .ThenBy(f => f.Type)
                    .ToList();
                info.DependencySize = info.Dependencies.Sum(af => af.Size);
                info.ScriptDependencies = info.Dependencies.Where(IsScriptRelated).ToList();
            }

            // Remove self-references from ParentGuids (a file should not be its own parent)
            foreach (AssetFile dep in info.Dependencies)
            {
                if (dep.ParentGuids != null && dep.Guid != null)
                {
                    dep.ParentGuids.Remove(dep.Guid);
                }
            }

            // clean-up again on-demand - use unique temp folder for this instance
            if (Directory.Exists(_uniqueTempFolderPath))
            {
                await IOUtils.DeleteFileOrDirectory(_uniqueTempFolderPath);
                await IOUtils.DeleteFileOrDirectory(_uniqueTempFolderPath + ".meta");
                AssetDatabase.Refresh();
            }
            if (info.DependencyState == DependencyStateOptions.Calculating) info.DependencyState = DependencyStateOptions.Done; // otherwise error along the way
        }

        private async Task<List<AssetFile>> DoCalculateDependencies(AssetInfo info, string path, ConcurrentDictionary<string, bool> processedGuids, List<AssetFile> result = null, CancellationToken ct = default(CancellationToken), string currentFileGuid = null)
        {
            if (result == null) result = new List<AssetFile>();

            if (ct.IsCancellationRequested)
            {
                info.DependencyState = DependencyStateOptions.Partial;
                return result;
            }

            path = IOUtils.ToLongPath(path);

            // Asset Manager dependencies are all files in the asset's folder
            if (info.AssetSource == Asset.Source.AssetManager)
            {
                if (File.Exists(path)) return result; // single-file asset 

                List<string> allFiles = await Task.Run(() => Directory.GetFiles(path, "*.*", SearchOption.AllDirectories).ToList());
                foreach (string file in allFiles)
                {
                    AssetFile af = new AssetFile();
                    af.Path = file.Substring(path.Length + 1);
                    af.FileName = Path.GetFileName(file);
                    af.Type = IOUtils.GetExtensionWithoutDot(file).ToLowerInvariant();
                    af.Size = new FileInfo(file).Length;

                    processedGuids.TryAdd(af.Guid, true);
                    result.Add(af);
                    // TODO: await ScanDependencyResult(info, result, af);
                }
                return result;
            }

            // only scan file types that contain guid references
            string extension = IOUtils.GetExtensionWithoutDot(path).ToLowerInvariant();

            // meta files can also contain dependencies
            if (ScanMetaDependencies.Contains(extension))
            {
                string metaPath = path + ".meta";
                bool metaExists = File.Exists(metaPath);
                if (metaExists) await DoCalculateDependencies(info, metaPath, processedGuids, result, ct, currentFileGuid);

                if (AI.Config.scanFBXDependencies && extension == "fbx")
                {
                    // also scan for texture references to image files inside the package (embedded materials)
                    string typeStr = string.Join("\",\"", AI.TypeGroups[AI.AssetGroup.Images]);
                    string query = "select * from AssetFile where AssetId = ? and Type in (\"" + typeStr + "\")";
                    List<AssetFile> files = DBAdapter.DB.Query<AssetFile>(query, info.AssetId).ToList();
                    if (files.Count > 0)
                    {
                        List<string> embedded = await IOUtils.FindMatchesInBinaryFile(path, files.Select(f => f.FileName).Distinct().ToList());
                        foreach (string embed in embedded)
                        {
                            AssetFile af = files.FirstOrDefault(f => f.FileName == embed);
                            await AddToResultAndCheckForSRPSupportReplacement(info, result, af, processedGuids, ct);

                            // Track parent relationship
                            if (af != null && !string.IsNullOrEmpty(currentFileGuid))
                            {
                                if (af.ParentGuids == null) af.ParentGuids = new HashSet<string>();
                                af.ParentGuids.Add(currentFileGuid); // HashSet handles duplicates automatically
                            }
                        }
                    }
                }

                // Detect external material dependencies for model files (FBX, OBJ).
                // When materialLocation: 0 and externalObjects is empty, Unity resolves materials
                // by NAME at import time using materialSearch (0=Local, 1=Recursive-Up, 2=Project-Wide).
                // Material names are extracted from internalIDToNameTable (modern) or fileIDToRecycleName (legacy).
                // Falls back to directory-based .mat search when the meta file doesn't list material names.
                if ((extension == "fbx" || (extension == "obj" && AI.Config.scanOBJMaterialDependencies)) && metaExists)
                {
                    await ScanModelExternalMaterialDependencies(info, path + ".meta", result, processedGuids, ct, currentFileGuid);
                }

                // Scan companion MTL files for OBJ models unconditionally.
                // MTL files list material names (newmtl) that map to .mat files and
                // texture map directives (map_Kd, bump, etc.) that reference image files.
                if (extension == "obj")
                {
                    await ScanOBJMtlDependencies(info, path, result, processedGuids, ct, currentFileGuid);
                }
            }

            if (extension != "meta" && !ScanDependencies.Contains(extension)) return result;

            if (string.IsNullOrEmpty(info.Guid))
            {
                info.DependencyState = DependencyStateOptions.Failed;
                return result;
            }

            string content;
            try
            {
                content = await File.ReadAllTextAsync(path);
            }
            catch (Exception e)
            {
                Debug.LogError($"Could not read file '{path}': {e.Message}");
                return null;
            }

            if (extension == "shader" || extension == "cginc" || extension == "hlsl")
            {
                string metaPath = path + ".meta";
                if (File.Exists(metaPath))
                {
                    string curGuid = AssetUtils.ExtractGuidFromFile(metaPath);
                    if (curGuid != null)
                    {
                        AssetFile curAf = DBAdapter.DB.Find<AssetFile>(a => a.Guid == curGuid && a.AssetId == info.AssetId);
                        if (curAf == null && info.SRPSupportPackage != null && info.SRPOriginalBackup != null && info.AssetId != info.SRPOriginalBackup.AssetId)
                        {
                            curAf = DBAdapter.DB.Find<AssetFile>(a => a.Guid == curGuid && a.AssetId == info.SRPOriginalBackup.AssetId);
                        }
                        if (curAf == null && info.ParentInfo != null)
                        {
                            curAf = DBAdapter.DB.Find<AssetFile>(a => a.Guid == curGuid && a.AssetId == info.ParentInfo.AssetId);
                        }
                        if (curAf != null)
                        {
                            // include files
                            HashSet<string> includedFiles = FindIncludeFiles(content);
                            foreach (string include in includedFiles)
                            {
                                string includePath = include.StartsWith("Assets") ? include : Path.Combine(Path.GetDirectoryName(curAf.Path), include);
                                includePath = includePath.Replace("\\", "/");
                                includePath = IOUtils.NormalizeRelative(includePath);

                                AssetFile af = DBAdapter.DB.Find<AssetFile>(a => a.AssetId == info.AssetId && a.Path == includePath);
                                if (af == null && info.SRPSupportPackage != null && info.SRPOriginalBackup != null && info.AssetId != info.SRPOriginalBackup.AssetId)
                                {
                                    af = DBAdapter.DB.Find<AssetFile>(a => a.AssetId == info.SRPOriginalBackup.AssetId && a.Path == includePath);
                                }
                                if (af == null && info.ParentInfo != null)
                                {
                                    af = DBAdapter.DB.Find<AssetFile>(a => a.AssetId == info.ParentInfo.AssetId && a.Path == includePath);
                                }
                                await AddToResultAndCheckForSRPSupportReplacement(info, result, af, processedGuids, ct);

                                // Track parent relationship
                                if (af != null && !string.IsNullOrEmpty(currentFileGuid))
                                {
                                    if (af.ParentGuids == null) af.ParentGuids = new HashSet<string>();
                                    af.ParentGuids.Add(currentFileGuid); // HashSet handles duplicates automatically
                                }
                            }
                        }
                    }
                }

                if (extension == "shader")
                {
                    // custom editors
                    List<string> editorFiles = FindCustomEditors(content);
                    foreach (string include in editorFiles)
                    {
                        // remove potential namespace
                        string[] arr = include.Split('.');
                        string includePath = arr.Last() + ".cs"; // file could also be named differently than class name, would require code analysis
                        AssetFile af = DBAdapter.DB.Find<AssetFile>(a => a.AssetId == info.AssetId && a.FileName == includePath);
                        if (af == null && info.SRPSupportPackage != null && info.SRPOriginalBackup != null && info.AssetId != info.SRPOriginalBackup.AssetId)
                        {
                            af = DBAdapter.DB.Find<AssetFile>(a => a.AssetId == info.SRPOriginalBackup.AssetId && a.FileName == includePath);
                        }
                        await AddToResultAndCheckForSRPSupportReplacement(info, result, af, processedGuids, ct);

                        // Track parent relationship
                        if (af != null && !string.IsNullOrEmpty(currentFileGuid))
                        {
                            if (af.ParentGuids == null) af.ParentGuids = new HashSet<string>();
                            af.ParentGuids.Add(currentFileGuid); // HashSet handles duplicates automatically
                        }
                    }
                }
            }
            else if (extension != "meta" && !content.StartsWith("%YAML") && !ShouldScanTextSerializedGuidReferencesDirectly(extension))
            {
                // Check if we have a bulk-imported version of this file in the project
                string targetFile = null;
                bool needsRefresh = false;

                if (_cache != null && !string.IsNullOrEmpty(currentFileGuid))
                {
                    string projectCacheKey = DependencyResultCache.GetMaterializedPathKey(info.AssetId, currentFileGuid);
                    if (_cache.TryGetMaterializedPath(projectCacheKey, out string projectPath))
                    {
                        // Only use cached paths that are project-relative (start with Assets/)
                        // Absolute paths from EnsureMaterialized cannot be used directly with Unity APIs
                        if (projectPath.StartsWith("Assets/") || projectPath.StartsWith("Assets\\"))
                        {
                            string fullProjectPath = AssetUtils.AddProjectRoot(projectPath);
                            if (File.Exists(fullProjectPath))
                            {
                                // File is already in project (bulk imported), use it directly
                                targetFile = projectPath; // Use project-relative path for Unity APIs
                                path = fullProjectPath;
                                content = await File.ReadAllTextAsync(path);
                            }
                        }
                    }
                }

                // If cached path points to a user project file (not in any temp folder)
                // and content is not YAML, force copy to temp folder to avoid modifying user's asset
                if (targetFile != null && !content.StartsWith("%YAML")
                    && !targetFile.Contains(AI.TEMP_FOLDER)
                    && !targetFile.Contains(UnityPreviewGenerator.PREVIEW_FOLDER))
                {
                    Directory.CreateDirectory(_uniqueTempFolderPath);
                    string safeCopy = Path.Combine("Assets", _uniqueTempFolder, Path.GetFileName(path));
                    if (!await IOUtils.TryCopyFile(path, safeCopy, true))
                    {
                        info.DependencyState = DependencyStateOptions.Failed;
                        return result;
                    }
                    targetFile = safeCopy;
                    needsRefresh = true;
                }

                // If not found in project, copy to temp folder
                if (targetFile == null)
                {
                    // reserialize prefabs on-the-fly by copying them over which will cause Unity to change the encoding upon refresh
                    // this will not work but throw missing script errors instead if there are any attached
                    Directory.CreateDirectory(_uniqueTempFolderPath);

                    targetFile = Path.Combine("Assets", _uniqueTempFolder, Path.GetFileName(path));

                    // file can be locked, retry automatically
                    if (!await IOUtils.TryCopyFile(path, targetFile, true))
                    {
                        info.DependencyState = DependencyStateOptions.Failed;
                        return result;
                    }

                    needsRefresh = true;
                }

                if (needsRefresh)
                {
                    AssetDatabase.Refresh();
                    content = await File.ReadAllTextAsync(targetFile);
                }

                // if still not YAML, might be because of missing scripts inside prefabs
                if (!content.StartsWith("%YAML"))
                {
                    if (ShouldTryBinaryYamlConversionBeforePrefabLoad(extension, content))
                    {
                        string convertedPath = TryConvertBinaryDependencyToYaml(targetFile, extension);
                        if (!string.IsNullOrEmpty(convertedPath))
                        {
                            string convertedContent = await File.ReadAllTextAsync(convertedPath);
                            if (convertedContent.StartsWith("%YAML"))
                            {
                                targetFile = AssetUtils.GetAssetDatabasePath(convertedPath) ?? convertedPath;
                                path = convertedPath;
                                content = convertedContent;
                            }
                        }
                    }

                    if (!content.StartsWith("%YAML") && targetFile.ToLowerInvariant().EndsWith(".prefab"))
                    {
                        try
                        {
                            GameObject go = PrefabUtility.LoadPrefabContents(targetFile);
                            go.transform.RemoveMissingScripts();
                            // Always save to force YAML serialization (if project uses Force Text mode)
                            PrefabUtility.SaveAsPrefabAsset(go, targetFile);
                            PrefabUtility.UnloadPrefabContents(go);
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"Invalid prefab '{info}' encountered: {e.Message}");
                            info.DependencyState = DependencyStateOptions.Failed;
                            return result;
                        }

                        content = await File.ReadAllTextAsync(targetFile.StartsWith("Assets/") ? AssetUtils.AddProjectRoot(targetFile) : targetFile);
                    }

                    if (ShouldTryBinaryYamlConversionBeforePrefabLoad(extension, content))
                    {
                        string convertedPath = TryConvertBinaryDependencyToYaml(targetFile, extension);
                        if (!string.IsNullOrEmpty(convertedPath))
                        {
                            string convertedContent = await File.ReadAllTextAsync(convertedPath);
                            if (convertedContent.StartsWith("%YAML"))
                            {
                                targetFile = AssetUtils.GetAssetDatabasePath(convertedPath) ?? convertedPath;
                                path = convertedPath;
                                content = convertedContent;
                            }
                        }
                    }

                    // final check (.asset are often binary files so don't fail for these)
                    if (!content.StartsWith("%YAML"))
                    {
                        if (extension != "asset") info.DependencyState = DependencyStateOptions.NotPossible;
                        return result;
                    }
                }
            }

            List<string> guids = ExtractDependencyGuids(content, extension, info.Guid, processedGuids.Keys);
            if (guids.Count == 0) return result;

            // Use cached AssetFile lookups if available, otherwise query database
            List<AssetFile> afCache;
            if (_cache != null && _cache.HasPackageCache(info.AssetId))
            {
                // Use pre-loaded package cache
                afCache = guids.Select(g => _cache.TryGetAssetFile(info.AssetId, g)).Where(af => af != null).ToList();
            }
            else
            {
                // Fallback to database query
                afCache = DBAdapter.DB.Table<AssetFile>().Where(a => a.AssetId == info.AssetId && guids.Contains(a.Guid)).ToList();
            }

            foreach (string guid in guids)
            {
                // search strategy:
                // if there is an SRP package available, check if the dependency is in there and use that one
                // if not, check if the dependency is in the original package and use that one
                // if not, check if the dependency is in any other package and use that one
                AssetFile af = null;
                if (info.SRPSupportFiles != null) af = info.SRPSupportFiles.FirstOrDefault(f => f.Guid == guid);
                if (af == null) af = afCache.FirstOrDefault(a => a.AssetId == info.AssetId && a.Guid == guid);
                if (af == null && info.SRPSupportPackage != null && info.AssetId != info.SRPOriginalBackup.AssetId)
                {
                    // Try cache first for SRP backup package
                    if (_cache != null && _cache.HasPackageCache(info.SRPOriginalBackup.AssetId))
                    {
                        af = _cache.TryGetAssetFile(info.SRPOriginalBackup.AssetId, guid);
                    }
                    else
                    {
                        af = DBAdapter.DB.Find<AssetFile>(a => a.AssetId == info.SRPOriginalBackup.AssetId && a.Guid == guid);
                    }
                }

                // potentially do cross-package search
                AssetInfo workInfo = info;
                if (af == null && AI.Config.allowCrossPackageDependencies)
                {
                    af = DBAdapter.DB.Find<AssetFile>(a => a.Guid == guid);
                    if (af == null)
                    {
                        processedGuids.TryAdd(guid, true); // we tried it all, give up
                        continue;
                    }

                    // Skip if current package already has a file with the same name (prioritize current package)
                    if (afCache.Any(a => a.FileName == af.FileName))
                    {
                        processedGuids.TryAdd(guid, true);
                        continue;
                    }

                    // break-out to other package
                    Asset crossAsset = null;
                    List<Asset> candidates = DBAdapter.DB.Table<Asset>().Where(a => a.Id == af.AssetId).ToList();
                    if (crossAsset == null && _isOnURP) crossAsset = candidates.FirstOrDefault(a => a.SafeName.ToLowerInvariant().Contains("urp"));
                    if (crossAsset == null && _isOnHDRP) crossAsset = candidates.FirstOrDefault(a => a.SafeName.ToLowerInvariant().Contains("hdrp"));
                    if (crossAsset == null) crossAsset = candidates.FirstOrDefault();
                    if (crossAsset != null)
                    {
                        workInfo = new AssetInfo(info).CopyFrom(crossAsset);

                        workInfo.SRPSupportPackage = null;
                        workInfo.SRPOriginalBackup = null;
                        workInfo.SRPMainReplacement = null;
                        workInfo.SRPSupportFiles = null;

                        // Resolve actual parent of the target package so nested includes can resolve from it
                        if (workInfo.ParentInfo == null && crossAsset.ParentId > 0)
                        {
                            Asset parentAsset = DBAdapter.DB.Find<Asset>(crossAsset.ParentId);
                            if (parentAsset != null) workInfo.ParentInfo = new AssetInfo(parentAsset);
                        }

                        if (!workInfo.CrossPackageDependencies.Any(p => p.Id == crossAsset.Id)) workInfo.CrossPackageDependencies.Add(crossAsset);
                        if (!info.CrossPackageDependencies.Any(p => p.Id == crossAsset.Id)) info.CrossPackageDependencies.Add(crossAsset);
                    }
                }

                // ignore missing guids as they are not in the package, so we can't do anything about them
                await AddToResultAndCheckForSRPSupportReplacement(workInfo, result, af, processedGuids, ct);

                // Track parent relationship
                if (af != null && !string.IsNullOrEmpty(currentFileGuid))
                {
                    if (af.ParentGuids == null) af.ParentGuids = new HashSet<string>();
                    af.ParentGuids.Add(currentFileGuid); // HashSet handles duplicates automatically
                }
            }

            return result.Distinct().ToList();
        }

        /// <summary>
        /// Finds the SRP support package for a given parent asset ID based on current render pipeline.
        /// This is the shared logic used by both dependency analysis and preview bulk import.
        /// </summary>
        /// <param name="parentAssetId">The parent package ID to search for SRP sub-packages</param>
        /// <param name="warnOnMultiple">Whether to log a warning when multiple candidates are found</param>
        /// <returns>The SRP support package Asset, or null if none found</returns>
        public static Asset FindSRPSupportPackage(int parentAssetId, bool warnOnMultiple = true)
        {
            bool isOnURP = AssetUtils.IsOnURP();
            bool isOnHDRP = AssetUtils.IsOnHDRP();
            bool isOnBIRP = !isOnURP && !isOnHDRP;

            string targetSRPVersion;
            string compatibilityField;

            if (isOnURP)
            {
                targetSRPVersion = AssetUtils.GetURPVersion();
                compatibilityField = "URPCompatible";
            }
            else if (isOnHDRP)
            {
                targetSRPVersion = AssetUtils.GetHDRPVersion();
                compatibilityField = "HDRPCompatible";
            }
            else if (isOnBIRP)
            {
                // BIRP doesn't have separate versions, skip version matching
                targetSRPVersion = null;
                compatibilityField = "BIRPCompatible";
            }
            else
            {
                return null;
            }

            // Query sub-packages by render pipeline compatibility flags
            // Check first with version number supplied in case there are versioned packages
            List<Asset> srpCandidates = DBAdapter.DB.Query<Asset>($"select * from Asset where ParentId=? and Exclude=0 and {compatibilityField}=1 "
                + (targetSRPVersion != null ? $" and (SafeName like '%{targetSRPVersion}%' or DisplayName like '%{targetSRPVersion}%')" : "")
                + " order by SafeName", parentAssetId);

            // If nothing was found, try again without version
            if (srpCandidates.Count == 0)
            {
                srpCandidates = DBAdapter.DB.Query<Asset>($"select * from Asset where ParentId=? and Exclude=0 and {compatibilityField}=1 order by SafeName", parentAssetId);
            }
            if (srpCandidates.Count == 0) return null;

            if (warnOnMultiple && srpCandidates.Count > 1)
            {
                Debug.LogWarning($"Multiple potential SRP candidate packages found for asset {parentAssetId}. Using last: {string.Join(", ", srpCandidates.Select(a => a.SafeName))}");
            }

            // Some packages have sub-set packages, use a heuristic to find the "all" package
            Asset result = srpCandidates.FirstOrDefault(c => c.SafeName.ToLowerInvariant().Contains("all"));
            return result ?? srpCandidates.Last();
        }

        private void PreparePipelineDependencies(AssetInfo info)
        {
            info.SRPOriginalBackup = null;
            info.SRPSupportPackage = null;
            info.SRPMainReplacement = null;
            info.SRPUsed = false;

            if (info.SRPSupportPackage == null)
            {
                info.SRPSupportPackage = FindSRPSupportPackage(info.AssetId);
                if (info.SRPSupportPackage == null) return;

                info.SRPOriginalBackup = new AssetInfo(info);
                info.CrossPackageDependencies.Add(info.SRPSupportPackage);
            }

            // Use parameterized query for SRP support files
            info.SRPSupportFiles = DBAdapter.DB.Query<AssetFile>("SELECT * FROM AssetFile WHERE AssetId=?", info.SRPSupportPackage.Id);

            // check main file as well, some packages have dedicated prefabs etc.
            AssetFile srpFile = info.SRPSupportFiles.FirstOrDefault(f => f.Guid == info.Guid);
            if (srpFile != null)
            {
                info.SRPMainReplacement = srpFile;
                info.SRPUsed = true;
            }
        }

        /// <summary>
        /// Scans a model .meta file (FBX, OBJ) for external material dependencies.
        /// When materialLocation=0 and externalObjects is empty, Unity resolves materials
        /// by name at import time. Material names are extracted from either:
        /// - internalIDToNameTable entries with classID 21 (modern Unity, serializedVersion 22200+)
        /// - fileIDToRecycleName entries with fileID in the 2100000 range (legacy Unity)
        /// If neither format yields material names, falls back to a directory-based search
        /// for .mat files scoped by the materialSearch setting (0=Local, 1=Recursive-Up, 2=Project-Wide).
        /// </summary>
        private async Task ScanModelExternalMaterialDependencies(AssetInfo info, string metaPath, List<AssetFile> result,
            ConcurrentDictionary<string, bool> processedGuids, CancellationToken ct, string currentFileGuid)
        {
            string metaContent;
            try
            {
                metaContent = await File.ReadAllTextAsync(metaPath);
            }
            catch
            {
                return;
            }

            // Only applies when using external materials mode (materialLocation: 0)
            if (!metaContent.Contains("materialLocation: 0")) return;

            // Skip if materialImportMode is 0 (None — no materials imported at all)
            if (metaContent.Contains("materialImportMode: 0")) return;

            // Check for empty externalObjects — if it has entries, the GUID scanner already handled them
            if (!metaContent.Contains("externalObjects: {}")) return;

            // Try to extract material names from the meta file
            List<string> materialNames = new List<string>();

            // --- First try: internalIDToNameTable (modern Unity, serializedVersion 22200+) ---
            // Format:
            //   internalIDToNameTable:
            //   - first:
            //       21: 2100000
            //     second: MaterialName
            MatchCollection idTableMatches = ModelInternalIDMaterialName.Matches(metaContent);
            foreach (Match m in idTableMatches)
            {
                string name = m.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(name))
                {
                    materialNames.Add(name);
                }
            }

            // --- Second try: fileIDToRecycleName (legacy Unity) ---
            // Format:
            //   fileIDToRecycleName:
            //     2100000: Mi_Props_01a
            //     4300000: SM_Lamp_01a
            if (materialNames.Count == 0)
            {
                int sectionStart = metaContent.IndexOf("fileIDToRecycleName:", StringComparison.Ordinal);
                if (sectionStart >= 0)
                {
                    string section = metaContent.Substring(sectionStart);
                    int headerEnd = section.IndexOf('\n');
                    if (headerEnd >= 0)
                    {
                        for (int i = headerEnd + 1; i < section.Length; i++)
                        {
                            if (section[i] == '\n') continue;
                            if (section[i] != ' ' && section[i] != '\t')
                            {
                                section = section.Substring(0, i);
                                break;
                            }
                            int nextNewline = section.IndexOf('\n', i);
                            if (nextNewline < 0) break;
                            i = nextNewline;
                        }

                        MatchCollection nameMatches = ModelMaterialName.Matches(section);
                        foreach (Match m in nameMatches)
                        {
                            string fileId = m.Groups[1].Value;
                            string name = m.Groups[2].Value.Trim();
                            if (fileId.StartsWith("21") && fileId.Length >= 7)
                            {
                                materialNames.Add(name);
                            }
                        }
                    }
                }
            }

            // --- If material names found, search by exact name ---
            if (materialNames.Count > 0)
            {
                foreach (string matName in materialNames)
                {
                    string matFileName = matName + ".mat";
                    AssetFile af = null;

                    // Check SRP support package first
                    if (info.SRPSupportFiles != null)
                    {
                        af = info.SRPSupportFiles.FirstOrDefault(f =>
                            string.Equals(f.FileName, matFileName, StringComparison.OrdinalIgnoreCase));
                    }

                    // Check current package
                    if (af == null)
                    {
                        af = DBAdapter.DB.Find<AssetFile>(f =>
                            f.AssetId == info.AssetId &&
                            f.FileName == matFileName &&
                            f.Type == "mat");
                    }

                    // Check original (non-SRP) package if working on the SRP copy
                    if (af == null && info.SRPOriginalBackup != null && info.AssetId != info.SRPOriginalBackup.AssetId)
                    {
                        af = DBAdapter.DB.Find<AssetFile>(f =>
                            f.AssetId == info.SRPOriginalBackup.AssetId &&
                            f.FileName == matFileName &&
                            f.Type == "mat");
                    }

                    if (af == null) continue;

                    await AddToResultAndCheckForSRPSupportReplacement(info, result, af, processedGuids, ct);

                    if (!string.IsNullOrEmpty(currentFileGuid))
                    {
                        if (af.ParentGuids == null) af.ParentGuids = new HashSet<string>();
                        af.ParentGuids.Add(currentFileGuid);
                    }
                }
                return;
            }

            // --- Fallback: directory-based search for .mat files ---
            // Neither format listed material names (common with modern Unity internalIDToNameTable
            // where classID 21 entries are absent). Search by directory structure using materialSearch scope.
            // Unity checks direct .mat files in the folder and its Materials/ subfolder at each level.
            int materialSearch = 1; // default: Recursive-Up
            Match msMatch = Regex.Match(metaContent, @"materialSearch:\s+(\d+)");
            if (msMatch.Success) materialSearch = int.Parse(msMatch.Groups[1].Value);

            // Use the FBX file's DB path from the info object (AssetInfo extends AssetFile)
            string fbxPath = info.Path?.Replace('\\', '/');
            if (string.IsNullOrEmpty(fbxPath)) return;
            int lastSlash = fbxPath.LastIndexOf('/');
            string fbxDir = lastSlash >= 0 ? fbxPath.Substring(0, lastSlash) : "";

            List<AssetFile> matFiles;
            if (materialSearch == 2) // Project-Wide: all .mat files in package
            {
                matFiles = DBAdapter.DB.Query<AssetFile>(
                    "SELECT * FROM AssetFile WHERE AssetId=? AND Type='mat'",
                    info.AssetId);
            }
            else
            {
                // Unity's material search checks direct children of specific folders:
                // - The FBX's own folder (direct children only)
                // - A "Materials" subfolder of that folder (direct children only)
                // For Recursive-Up (1): also walks up parent directories, checking each parent's Materials/ subfolder
                HashSet<string> addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                matFiles = new List<AssetFile>();

                string searchDir = fbxDir;
                bool isFirstLevel = true;
                do
                {
                    if (!string.IsNullOrEmpty(searchDir))
                    {
                        // At the FBX's own level, check direct .mat files in the folder itself
                        if (isFirstLevel)
                        {
                            List<AssetFile> directFiles = DBAdapter.DB.Query<AssetFile>(
                                "SELECT * FROM AssetFile WHERE AssetId=? AND Type='mat' AND Path LIKE ? AND Path NOT LIKE ?",
                                info.AssetId, searchDir + "/%", searchDir + "/%/%");
                            foreach (AssetFile f in directFiles)
                            {
                                if (f.Path != null && addedPaths.Add(f.Path)) matFiles.Add(f);
                            }
                        }

                        // Check the Materials/ subfolder (direct children only)
                        string materialsDir = searchDir + "/Materials";
                        List<AssetFile> matFolderFiles = DBAdapter.DB.Query<AssetFile>(
                            "SELECT * FROM AssetFile WHERE AssetId=? AND Type='mat' AND Path LIKE ? AND Path NOT LIKE ?",
                            info.AssetId, materialsDir + "/%", materialsDir + "/%/%");
                        foreach (AssetFile f in matFolderFiles)
                        {
                            if (f.Path != null && addedPaths.Add(f.Path)) matFiles.Add(f);
                        }
                    }

                    if (materialSearch == 0) break; // Local: only search FBX's own directory level

                    isFirstLevel = false;
                    int parentSlash = searchDir.LastIndexOf('/');
                    searchDir = parentSlash >= 0 ? searchDir.Substring(0, parentSlash) : "";
                } while (!string.IsNullOrEmpty(searchDir));
            }

            // Also include SRP support .mat files
            if (info.SRPSupportFiles != null)
            {
                foreach (AssetFile srpFile in info.SRPSupportFiles)
                {
                    if (string.Equals(srpFile.Type, "mat", StringComparison.OrdinalIgnoreCase))
                    {
                        matFiles.Add(srpFile);
                    }
                }
            }

            foreach (AssetFile af in matFiles)
            {
                await AddToResultAndCheckForSRPSupportReplacement(info, result, af, processedGuids, ct);

                if (!string.IsNullOrEmpty(currentFileGuid))
                {
                    if (af.ParentGuids == null) af.ParentGuids = new HashSet<string>();
                    af.ParentGuids.Add(currentFileGuid);
                }
            }
        }

        /// <summary>
        /// Scans for a companion .mtl file next to an OBJ model and parses it for dependencies.
        /// MTL files contain material definitions (newmtl) that map to identically named .mat files
        /// in the package, and texture map directives (map_Kd, map_Ka, map_Ks, bump, etc.) that
        /// reference image files. This runs unconditionally for all OBJ files.
        /// </summary>
        private async Task ScanOBJMtlDependencies(AssetInfo info, string objPath, List<AssetFile> result,
            ConcurrentDictionary<string, bool> processedGuids, CancellationToken ct, string currentFileGuid)
        {
            // Derive expected MTL path by replacing .obj extension with .mtl in the DB path
            string objDbPath = info.Path?.Replace('\\', '/');
            if (string.IsNullOrEmpty(objDbPath)) return;

            string mtlDbPath = Regex.Replace(objDbPath, @"\.obj$", ".mtl", RegexOptions.IgnoreCase);
            string mtlFileName = Path.GetFileName(mtlDbPath);

            // Look up the MTL file in the database
            AssetFile mtlFile = DBAdapter.DB.Find<AssetFile>(f => f.AssetId == info.AssetId && f.Path == mtlDbPath);

            // Fallback: search by filename in case the MTL is at a different path
            if (mtlFile == null)
            {
                mtlFile = DBAdapter.DB.Find<AssetFile>(f =>
                    f.AssetId == info.AssetId &&
                    f.FileName == mtlFileName &&
                    f.Type == "mtl");
            }

            if (mtlFile == null) return;

            // Add the MTL file itself as a dependency
            await AddToResultAndCheckForSRPSupportReplacement(info, result, mtlFile, processedGuids, ct);
            if (!string.IsNullOrEmpty(currentFileGuid))
            {
                if (mtlFile.ParentGuids == null) mtlFile.ParentGuids = new HashSet<string>();
                mtlFile.ParentGuids.Add(currentFileGuid);
            }

            // Materialize the MTL file to read its contents
            string mtlPath = await Assets.EnsureMaterialized(info.ToAsset(), mtlFile, false, ct);
            if (mtlPath == null) return;

            string mtlContent;
            try
            {
                mtlContent = await File.ReadAllTextAsync(mtlPath);
            }
            catch
            {
                return;
            }

            // --- Parse material names (newmtl entries) and resolve to .mat files ---
            MatchCollection materialMatches = MtlNewMaterialRegex.Matches(mtlContent);
            foreach (Match m in materialMatches)
            {
                string matName = m.Groups[1].Value.Trim();
                if (string.IsNullOrEmpty(matName)) continue;

                string matFileName = matName + ".mat";
                AssetFile af = null;

                // Check SRP support package first
                if (info.SRPSupportFiles != null)
                {
                    af = info.SRPSupportFiles.FirstOrDefault(f =>
                        string.Equals(f.FileName, matFileName, StringComparison.OrdinalIgnoreCase));
                }

                // Check current package
                if (af == null)
                {
                    af = DBAdapter.DB.Find<AssetFile>(f =>
                        f.AssetId == info.AssetId &&
                        f.FileName == matFileName &&
                        f.Type == "mat");
                }

                // Check original (non-SRP) package if working on the SRP copy
                if (af == null && info.SRPOriginalBackup != null && info.AssetId != info.SRPOriginalBackup.AssetId)
                {
                    af = DBAdapter.DB.Find<AssetFile>(f =>
                        f.AssetId == info.SRPOriginalBackup.AssetId &&
                        f.FileName == matFileName &&
                        f.Type == "mat");
                }

                if (af == null) continue;

                await AddToResultAndCheckForSRPSupportReplacement(info, result, af, processedGuids, ct);

                if (!string.IsNullOrEmpty(currentFileGuid))
                {
                    if (af.ParentGuids == null) af.ParentGuids = new HashSet<string>();
                    af.ParentGuids.Add(currentFileGuid);
                }
            }

            // --- Parse texture map references and resolve to image files ---
            MatchCollection textureMatches = MtlTextureMapRegex.Matches(mtlContent);
            if (textureMatches.Count > 0)
            {
                // Query all image files in the package once for efficient lookup
                List<AssetFile> imageFiles = DBAdapter.DB.Query<AssetFile>(
                    $"SELECT * FROM AssetFile WHERE AssetId=? AND Type IN ('{string.Join("','", AI.TypeGroups[AI.AssetGroup.Images])}')",
                    info.AssetId);

                // Build a case-insensitive filename lookup
                Dictionary<string, AssetFile> imagesByName = new Dictionary<string, AssetFile>(StringComparer.OrdinalIgnoreCase);
                foreach (AssetFile img in imageFiles)
                {
                    if (!imagesByName.ContainsKey(img.FileName))
                    {
                        imagesByName[img.FileName] = img;
                    }
                }

                HashSet<string> processedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in textureMatches)
                {
                    string texturePath = m.Groups[1].Value.Trim();
                    if (string.IsNullOrEmpty(texturePath)) continue;

                    // Extract just the filename (MTL paths can be relative)
                    string textureFileName = Path.GetFileName(texturePath);
                    if (string.IsNullOrEmpty(textureFileName) || !processedTextures.Add(textureFileName)) continue;

                    if (imagesByName.TryGetValue(textureFileName, out AssetFile af))
                    {
                        await AddToResultAndCheckForSRPSupportReplacement(info, result, af, processedGuids, ct);

                        if (!string.IsNullOrEmpty(currentFileGuid))
                        {
                            if (af.ParentGuids == null) af.ParentGuids = new HashSet<string>();
                            af.ParentGuids.Add(currentFileGuid);
                        }
                    }
                }
            }
        }

        private async Task AddToResultAndCheckForSRPSupportReplacement(AssetInfo info, List<AssetFile> result, AssetFile af, ConcurrentDictionary<string, bool> processedGuids, CancellationToken ct)
        {
            if (af == null) return;

            // Handle files with GUIDs (existing logic)
            if (af.Guid != null)
            {
                if (processedGuids.ContainsKey(af.Guid)) return;

                // check if there is an URP file with matching GUID and replace the original dependency with that one
                if (info.SRPSupportFiles != null && af.AssetId != info.SRPSupportPackage.Id)
                {
                    AssetFile srpFile = info.SRPSupportFiles.FirstOrDefault(f => f.Guid == af.Guid);
                    if (srpFile != null)
                    {
                        af = srpFile;
                        info.SRPUsed = true;
                    }
                }

                processedGuids.TryAdd(af.Guid, true);
                result.Add(af);
                await ScanDependencyResult(info, result, af, processedGuids, ct);
            }
            // Handle files without GUIDs (new logic)
            else
            {
                // Use composite key for tracking: AssetId_Path
                string compositeKey = $"{af.AssetId}_{af.Path}";
                if (processedGuids.ContainsKey(compositeKey)) return;

                processedGuids.TryAdd(compositeKey, true);
                result.Add(af);
                // Skip recursive scanning for files without GUIDs (they're leaf dependencies)
            }
        }

        private async Task ScanDependencyResult(AssetInfo info, List<AssetFile> result, AssetFile af, ConcurrentDictionary<string, bool> processedGuids, CancellationToken ct)
        {
            // CRITICAL OPTIMIZATION: Check cache before processing
            // This is where most performance gains come from - avoiding redundant recursive traversal
            // Key includes AssetId to prevent cross-package contamination when GUIDs are shared
            if (_cache != null && _cache.TryGetDependencies(af.AssetId, af.Guid, out DependencyResultCache.CachedDependencyResult cached))
            {
                // Cache hit! Reuse previously calculated dependencies
                result.AddRange(cached.Dependencies);

                // Propagate state flags from cache
                if (cached.SRPUsed) info.SRPUsed = true;
                if (cached.State != DependencyStateOptions.Unknown && cached.State != DependencyStateOptions.Calculating)
                {
                    info.DependencyState = cached.State;
                }
                foreach (Asset crossDep in cached.CrossPackageDependencies)
                {
                    if (!info.CrossPackageDependencies.Any(p => p.Id == crossDep.Id))
                    {
                        info.CrossPackageDependencies.Add(crossDep);
                    }
                }

                return; // Skip recursive calculation
            }

            AssetInfo workInfo = info;

            // switch to matching package if af does not refer to correct info
            if (af.AssetId != info.AssetId)
            {
                if (info.SRPSupportPackage != null && af.AssetId == info.SRPSupportPackage.Id)
                {
                    workInfo = new AssetInfo(workInfo).CopyFrom(info.SRPSupportPackage);
                }
                else if (info.SRPSupportPackage != null && info.SRPOriginalBackup != null && af.AssetId == info.SRPOriginalBackup.AssetId)
                {
                    workInfo = new AssetInfo(workInfo).CopyFrom(info.SRPOriginalBackup, true);
                }
                else
                {
                    // File belongs to a different package (e.g. parent package), switch context
                    Asset targetAsset = DBAdapter.DB.Find<Asset>(a => a.Id == af.AssetId);
                    if (targetAsset != null)
                    {
                        workInfo = new AssetInfo(workInfo).CopyFrom(targetAsset);
                    }
                }

                // Resolve actual parent of the target package so nested includes can resolve from it
                // CopyFrom nulls ParentInfo when AssetId changes; restore it from the DB
                if (workInfo.ParentInfo == null && workInfo.ParentId > 0)
                {
                    Asset parentAsset = DBAdapter.DB.Find<Asset>(workInfo.ParentId);
                    if (parentAsset != null) workInfo.ParentInfo = new AssetInfo(parentAsset);
                }

                // Pre-load package cache for the target package if not already loaded
                if (_cache != null && !_cache.HasPackageCache(af.AssetId))
                {
                    List<AssetFile> packageFiles = DBAdapter.DB.Query<AssetFile>(
                        "SELECT * FROM AssetFile WHERE AssetId=?", af.AssetId);
                    _cache.PreloadPackageFiles(af.AssetId, packageFiles);
                }
            }

            // Check materialization cache
            string cacheKey = DependencyResultCache.GetMaterializedPathKey(workInfo.AssetId, af);
            string targetPath;
            if (_cache != null && _cache.TryGetMaterializedPath(cacheKey, out string cachedPath))
            {
                // Only use cached paths that are project-relative (start with Assets/)
                if (cachedPath.StartsWith("Assets/") || cachedPath.StartsWith("Assets\\"))
                {
                    // Cached path is project-relative, convert to absolute for file reading
                    targetPath = AssetUtils.AddProjectRoot(cachedPath);
                }
                else
                {
                    // Cached path is absolute (external), use directly
                    targetPath = cachedPath;
                }
            }
            else
            {
                targetPath = await Assets.EnsureMaterialized(workInfo.ToAsset(), af, false, ct);
                if (targetPath == null)
                {
                    Debug.LogWarning($"Could not materialize dependency: {af.Path}");
                    processedGuids.TryAdd(af.Guid, true);
                    return;
                }

                // Cache the materialized path (only project-relative paths can be used with Unity APIs)
                if (_cache != null)
                {
                    _cache.StoreMaterializedPath(cacheKey, targetPath);
                }
            }

            // Track dependencies for this file before recursive call
            int depCountBefore = result.Count;

            await DoCalculateDependencies(workInfo, targetPath, processedGuids, result, ct, af.Guid);

            // Calculate what dependencies were added by this file
            List<AssetFile> newDeps = result.Skip(depCountBefore).ToList();

            // Store in cache for future reuse (scoped by AssetId to prevent cross-package contamination)
            if (_cache != null)
            {
                _cache.StoreDependencies(af.AssetId, af.Guid, new DependencyResultCache.CachedDependencyResult
                {
                    Dependencies = newDeps,
                    State = workInfo.DependencyState,
                    SRPUsed = workInfo.SRPUsed,
                    CrossPackageDependencies = new List<Asset>(workInfo.CrossPackageDependencies)
                });
            }

            // carry over results set during calculation
            if (workInfo.SRPUsed) info.SRPUsed = true;
            info.DependencyState = workInfo.DependencyState;
            foreach (Asset d in workInfo.CrossPackageDependencies)
            {
                if (!info.CrossPackageDependencies.Any(p => p.Id == d.Id)) info.CrossPackageDependencies.Add(d);
            }
        }

        private HashSet<string> FindIncludeFiles(string shaderCode, bool returnPackageReferences = false)
        {
            HashSet<string> result = new HashSet<string>();

            MatchCollection matches = IncludeFilesRegex.Matches(shaderCode);
            foreach (Match match in matches)
            {
                if (match.Success)
                {
                    string value = match.Groups[1].Value;
                    if (!returnPackageReferences && value.StartsWith("Packages/")) continue;
                    if (value.StartsWith("./")) value = value.Substring(2); // remove leading './'
                    result.Add(value);
                }
            }

            return result;
        }

        private List<string> FindCustomEditors(string shaderCode)
        {
            List<string> result = new List<string>();

            MatchCollection matches = CustomEditorRegex.Matches(shaderCode);
            foreach (Match match in matches)
            {
                if (match.Success)
                {
                    result.Add(match.Groups[1].Value);
                }
            }

            return result;
        }

        /// <summary>
        /// Analyzes C# scripts for code-level dependencies and resolves assembly definitions.
        /// </summary>
        private async Task<List<AssetFile>> AnalyzeExtendedScriptDependencies(
            AssetInfo info,
            List<AssetFile> directScripts,
            CancellationToken ct)
        {
            List<AssetFile> result = new List<AssetFile>();
            HashSet<string> processedGuids = new HashSet<string>();

            // Mark direct scripts as processed
            foreach (AssetFile s in directScripts ?? new List<AssetFile>())
            {
                if (s.Guid != null) processedGuids.Add(s.Guid);
            }

            // Get all script-related files in package
            List<AssetFile> packageScriptFiles = DBAdapter.DB.Query<AssetFile>(
                $"SELECT * FROM AssetFile WHERE AssetId=? AND {ScriptRelatedSqlFilter()}",
                info.AssetId);

            // Build class name lookup for .cs files
            Dictionary<string, AssetFile> classNameToFile = new Dictionary<string, AssetFile>(StringComparer.OrdinalIgnoreCase);
            foreach (AssetFile f in packageScriptFiles.Where(f => f.Type == "cs"))
            {
                string className = Path.GetFileNameWithoutExtension(f.FileName);
                if (!classNameToFile.ContainsKey(className))
                {
                    classNameToFile[className] = f;
                }
            }

            // Build asmdef name lookup
            Dictionary<string, AssetFile> asmdefNameToFile = new Dictionary<string, AssetFile>(StringComparer.OrdinalIgnoreCase);
            foreach (AssetFile asmdef in packageScriptFiles.Where(f => f.Type == "asmdef"))
            {
                string asmdefName = Path.GetFileNameWithoutExtension(asmdef.FileName);
                asmdefNameToFile[asmdefName] = asmdef;
            }

            // Analyze direct scripts for type dependencies
            Queue<AssetFile> scriptsToAnalyze = new Queue<AssetFile>(directScripts ?? new List<AssetFile>());

            while (scriptsToAnalyze.Count > 0 && !ct.IsCancellationRequested)
            {
                AssetFile script = scriptsToAnalyze.Dequeue();

                // Skip scripts from other packages to prevent cross-package duplicates
                if (script.AssetId != info.AssetId) continue;

                string path = await Assets.EnsureMaterialized(info.ToAsset(), script, false, ct);
                if (path == null) continue;

                string content;
                try
                {
                    content = await File.ReadAllTextAsync(path);
                }
                catch
                {
                    continue;
                }

                if (script.Type == "cs")
                {
                    // Analyze C# code for type references
                    HashSet<string> referencedTypes = ExtractTypeReferences(content);

                    foreach (string typeName in referencedTypes)
                    {
                        if (classNameToFile.TryGetValue(typeName, out AssetFile dep))
                        {
                            // Skip self-references (by GUID or by filename for duplicate files)
                            if (dep.Guid == script.Guid || dep.FileName == script.FileName) continue;

                            if (dep.Guid != null && !processedGuids.Contains(dep.Guid))
                            {
                                processedGuids.Add(dep.Guid);
                                // Set ParentGuids to track which script references this dependency
                                if (dep.ParentGuids == null) dep.ParentGuids = new HashSet<string>();
                                dep.ParentGuids.Add(script.Guid);
                                result.Add(dep);

                                scriptsToAnalyze.Enqueue(dep); // Analyze transitively
                            }
                        }
                    }

                    // Find and include the asmdef for this script's folder
                    AssetFile asmdef = FindAsmdefForScript(script, packageScriptFiles);
                    if (asmdef != null && asmdef.Guid != null && !processedGuids.Contains(asmdef.Guid))
                    {
                        processedGuids.Add(asmdef.Guid);
                        result.Add(asmdef);
                        // Note: We don't follow asmdef references as those are external assembly dependencies
                    }
                }
                else if (script.Type == "asmdef")
                {
                    // Note: We don't follow asmdef references as those are external assembly dependencies
                }
            }

            // Include any .asmref files that reference included asmdefs
            foreach (AssetFile asmref in packageScriptFiles.Where(f => f.Type == "asmref"))
            {
                if (asmref.Guid != null && !processedGuids.Contains(asmref.Guid))
                {
                    string path = await Assets.EnsureMaterialized(info.ToAsset(), asmref, false, ct);
                    if (path != null)
                    {
                        try
                        {
                            string content = await File.ReadAllTextAsync(path);
                            // Check if this asmref points to an included asmdef
                            foreach (AssetFile includedAsmdef in result.Where(f => f.Type == "asmdef"))
                            {
                                if (content.Contains(Path.GetFileNameWithoutExtension(includedAsmdef.FileName)))
                                {
                                    processedGuids.Add(asmref.Guid);
                                    result.Add(asmref);
                                    break;
                                }
                            }
                        }
                        catch
                        {
                            // Ignore read errors
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Finds the assembly definition file that governs a script based on folder hierarchy.
        /// An asmdef only governs scripts in its folder or subfolders (not parent folders).
        /// </summary>
        private AssetFile FindAsmdefForScript(AssetFile script, List<AssetFile> packageFiles)
        {
            string scriptDir = Path.GetDirectoryName(script.Path)?.Replace("\\", "/") ?? "";

            // Find asmdef in same folder or parent folders (asmdef must be at or above the script)
            List<AssetFile> asmdefs = packageFiles.Where(f => f.Type == "asmdef").ToList();

            AssetFile bestMatch = null;
            int bestMatchLength = -1;

            foreach (AssetFile asmdef in asmdefs)
            {
                string asmdefDir = Path.GetDirectoryName(asmdef.Path)?.Replace("\\", "/") ?? "";

                // Script must be in the same directory or a subdirectory of the asmdef
                // Check for exact match or proper directory boundary (asmdefDir + "/")
                bool isMatch = scriptDir.Equals(asmdefDir, StringComparison.OrdinalIgnoreCase) ||
                    scriptDir.StartsWith(asmdefDir + "/", StringComparison.OrdinalIgnoreCase);

                if (isMatch && asmdefDir.Length > bestMatchLength)
                {
                    bestMatch = asmdef;
                    bestMatchLength = asmdefDir.Length;
                }
            }

            return bestMatch;
        }

        /// <summary>
        /// Analyzes an assembly definition file for references to other assemblies.
        /// </summary>
        private async Task AnalyzeAsmdefReferences(
            AssetInfo info,
            AssetFile asmdef,
            Dictionary<string, AssetFile> asmdefNameToFile,
            HashSet<string> processedGuids,
            List<AssetFile> result,
            CancellationToken ct)
        {
            string path = await Assets.EnsureMaterialized(info.ToAsset(), asmdef, false, ct);
            if (path == null) return;

            string content;
            try
            {
                content = await File.ReadAllTextAsync(path);
            }
            catch
            {
                return;
            }

            // Extract references array from asmdef JSON
            Match refsMatch = AsmdefReferenceRegex.Match(content);
            if (refsMatch.Success)
            {
                string refsContent = refsMatch.Groups[1].Value;

                // Extract individual reference names (handles both GUID: and name formats)
                MatchCollection refMatches = Regex.Matches(refsContent, @"""([^""]+)""");
                foreach (Match refMatch in refMatches)
                {
                    string refName = refMatch.Groups[1].Value;

                    // Handle GUID format: "GUID:abc123..."
                    if (refName.StartsWith("GUID:"))
                    {
                        string guid = refName.Substring(5);
                        AssetFile referencedAsmdef = asmdefNameToFile.Values.FirstOrDefault(f => f.Guid == guid);
                        if (referencedAsmdef != null && !processedGuids.Contains(referencedAsmdef.Guid))
                        {
                            processedGuids.Add(referencedAsmdef.Guid);
                            result.Add(referencedAsmdef);
                        }
                    }
                    else if (asmdefNameToFile.TryGetValue(refName, out AssetFile referencedAsmdef))
                    {
                        if (referencedAsmdef.Guid != null && !processedGuids.Contains(referencedAsmdef.Guid))
                        {
                            processedGuids.Add(referencedAsmdef.Guid);
                            result.Add(referencedAsmdef);
                        }
                    }
                }
            }
        }

        private static string TryConvertBinaryDependencyToYaml(string targetFile, string extension)
        {
            if (!ShouldAttemptBinaryYamlConversion(extension)) return null;

            string sourcePath = AssetUtils.GetAssetDatabasePath(targetFile) != null
                ? AssetUtils.AddProjectRoot(targetFile)
                : targetFile;
            sourcePath = IOUtils.ToLongPath(sourcePath);
            if (!File.Exists(sourcePath)) return null;

            string directory = Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrEmpty(directory)) return null;

            string convertedPath = Path.Combine(
                directory,
                $"{Path.GetFileNameWithoutExtension(sourcePath)}.ai-yaml.{extension}");

            return AssetUtils.ConvertBinaryToYaml(sourcePath, convertedPath) && File.Exists(convertedPath)
                ? convertedPath
                : null;
        }

        /// <summary>
        /// Extracts potential type references from C# code.
        /// </summary>
        private HashSet<string> ExtractTypeReferences(string csContent)
        {
            HashSet<string> types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Remove comments and strings to avoid false positives
            string cleanContent = RemoveCommentsAndStrings(csContent);

            // 1. Inheritance/implementation
            foreach (Match m in InheritanceRegex.Matches(cleanContent))
            {
                string baseList = m.Groups[1].Value;
                foreach (string part in baseList.Split(','))
                {
                    string trimmed = part.Trim().Split('<')[0].Split('.').Last();
                    if (!string.IsNullOrEmpty(trimmed) && trimmed.Length > 0 && char.IsUpper(trimmed[0]))
                    {
                        types.Add(trimmed);
                    }
                }
            }

            // 2. Attributes
            foreach (Match m in AttributeRegex.Matches(cleanContent))
            {
                string attrName = m.Groups[1].Value;
                types.Add(attrName);
                types.Add(attrName + "Attribute");
            }

            // 3. General type references (PascalCase identifiers)
            foreach (Match m in TypeReferenceRegex.Matches(cleanContent))
            {
                string typeName = m.Groups[1].Value;
                if (!IsBuiltInType(typeName))
                {
                    types.Add(typeName);
                }
            }

            return types;
        }

        private string RemoveCommentsAndStrings(string code)
        {
            // Remove single-line comments
            code = Regex.Replace(code, @"//.*$", "", RegexOptions.Multiline);
            // Remove multi-line comments
            code = Regex.Replace(code, @"/\*[\s\S]*?\*/", "");
            // Remove string literals
            code = Regex.Replace(code, @"""[^""\\]*(?:\\.[^""\\]*)*""", "\"\"");
            // Remove verbatim strings
            code = Regex.Replace(code, @"@""[^""]*(?:""""[^""]*)*""", "\"\"");
            return code;
        }

        private static bool IsBuiltInType(string typeName)
        {
            return BuiltInTypes.Contains(typeName) ||
                typeName.Length <= 2 || // Skip short identifiers (T, U, etc.)
                char.IsLower(typeName[0]); // Skip lowercase
        }

        // for debugging purposes
        private static void ScanMetaFiles()
        {
            string[] packages = Directory.GetFiles(Paths.GetMaterializeFolder(), "*.meta", SearchOption.AllDirectories);
            for (int i = 0; i < packages.Length; i++)
            {
                string content = File.ReadAllText(packages[i]);
                List<string> guids = ExtractDependencyGuids(content, "meta");
                if (guids.Count <= 1) continue;
                string pathFile = Path.Combine(Path.GetDirectoryName(packages[i]), "pathname");
                if (!File.Exists(pathFile)) continue;

                string pathName = File.ReadAllText(pathFile);
                if (pathName.ToLowerInvariant().Contains("fbx")
                    || pathName.ToLowerInvariant().Contains("shadergraph")
                    || pathName.ToLowerInvariant().Contains("ttf")
                    || pathName.ToLowerInvariant().Contains("otf")
                    || pathName.ToLowerInvariant().Contains("cs")
                    || pathName.ToLowerInvariant().Contains("png")
                    || pathName.ToLowerInvariant().Contains("obj")
                    || pathName.ToLowerInvariant().Contains("uxml")
                    || pathName.ToLowerInvariant().Contains("js")
                    || pathName.ToLowerInvariant().Contains("uss")
                    || pathName.ToLowerInvariant().Contains("nn")
                    || pathName.ToLowerInvariant().Contains("tss")
                    || pathName.ToLowerInvariant().Contains("inputactions")
                    || pathName.ToLowerInvariant().Contains("shader")) continue;

                break;
            }
        }
    }
}
