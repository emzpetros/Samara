using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AudioTool;
using ImpossibleRobert.Common;
using UnityEditor;

namespace AssetInventory
{
    public static class ProjectAssetSearch
    {
        private static int _virtualIdCounter;

        // Maps AI type groups to AssetDatabase t: filter strings
        private static readonly Dictionary<string, string> TypeGroupToUnityFilter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"Audio", "t:AudioClip"},
            {"Images", "t:Texture"},
            {"Videos", "t:VideoClip"},
            {"Prefabs", "t:Prefab"},
            {"Materials", "t:Material"},
            {"Shaders", "t:Shader"},
            {"Models", "t:Model"},
            {"Animations", "t:AnimationClip"},
            {"Fonts", "t:Font"},
            {"Scripts", "t:MonoScript"},
            {"Scenes", "t:Scene"},
            {"Documents", "t:TextAsset"}
        };

        public class Options
        {
            public string SearchPhrase = string.Empty;
            public string RawSearchType; // e.g. "Images", "Audio", "png"
            public bool IgnoreExcludedExtensions;
            public int MaxResults;
            public int CurrentPage = 1;

            // Category A metadata filters (extractable for project assets)
            public string SearchWidth = string.Empty;
            public bool CheckMaxWidth;
            public string SearchHeight = string.Empty;
            public bool CheckMaxHeight;
            public string SearchLength = string.Empty;
            public bool CheckMaxLength;
            public string SearchSize = string.Empty;
            public bool CheckMaxSize;
            public string SearchVertexCount = string.Empty;
            public bool CheckMaxVertexCount;
            public int SelectedImageType;
            public string[] ImageTypeOptions;

            public int SortField;
            public bool SortDescending;

            public static Options CreateDefault()
            {
                string[] imageTypeOptions = new List<string> {"-all-", string.Empty}
                    .Concat(TextureNameSuggester.suffixPatterns.Keys.Select(StringUtils.CamelCaseToWords))
                    .ToArray();

                return new Options
                {
                    ImageTypeOptions = imageTypeOptions
                };
            }

            public static Options FromSavedSearch(SavedSearch savedSearch)
            {
                Options options = CreateDefault();

                options.SearchPhrase = savedSearch.SearchPhrase ?? string.Empty;
                options.RawSearchType = GetRawSearchType(savedSearch.Type);
                options.SearchWidth = savedSearch.Width ?? string.Empty;
                options.CheckMaxWidth = savedSearch.CheckMaxWidth;
                options.SearchHeight = savedSearch.Height ?? string.Empty;
                options.CheckMaxHeight = savedSearch.CheckMaxHeight;
                options.SearchLength = savedSearch.Length ?? string.Empty;
                options.CheckMaxLength = savedSearch.CheckMaxLength;
                options.SearchSize = savedSearch.Size ?? string.Empty;
                options.CheckMaxSize = savedSearch.CheckMaxSize;
                options.SearchVertexCount = savedSearch.VertexCount ?? string.Empty;
                options.CheckMaxVertexCount = savedSearch.CheckMaxVertexCount;
                options.SelectedImageType = savedSearch.ImageType;
                options.CurrentPage = 1;
                options.MaxResults = 0;

                return options;
            }

            private static string GetRawSearchType(string savedType)
            {
                if (string.IsNullOrWhiteSpace(savedType)) return null;
                string[] types = Assets.LoadTypes();
                int typeIdx = UnityEngine.Mathf.Max(0, Array.FindIndex(types, s => s.Split('/').LastOrDefault() == savedType));
                return typeIdx > 0 && types.Length > typeIdx ? types[typeIdx] : null;
            }
        }

        public class Result
        {
            public List<AssetInfo> Files = new List<AssetInfo>();
            public int ResultCount;
        }

        public static Result Execute(Options opt)
        {
            Result result = new Result();
            _virtualIdCounter = -1;

            // Build AssetDatabase filter
            string filter = BuildFilter(opt);
            string[] searchFolders = new[] {"Assets"};

            string[] guids = AssetDatabase.FindAssets(filter, searchFolders);

            // Extract search terms for client-side filtering
            List<string> includeTerms = new List<string>();
            List<string> excludeTerms = new List<string>();
            ParseSearchTerms(opt.SearchPhrase, includeTerms, excludeTerms);

            // Extension filter from type selection
            string[] extensionFilter = GetExtensionFilter(opt.RawSearchType);
            HashSet<string> excludedExtensions = GetExcludedExtensionSet(opt.IgnoreExcludedExtensions);

            List<AssetInfo> files = new List<AssetInfo>();
            HashSet<string> seenGuids = new HashSet<string>();

            foreach (string guid in guids)
            {
                if (!seenGuids.Add(guid)) continue;

                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (!path.StartsWith("Assets/")) continue;

                // Skip Asset Inventory temp/preview folders
                if (path.Contains(AI.TEMP_FOLDER) || path.Contains(UnityPreviewGenerator.PREVIEW_FOLDER)) continue;

                // Skip directories
                if (AssetDatabase.IsValidFolder(path)) continue;

                string fileName = Path.GetFileName(path);
                string extension = Path.GetExtension(path);
                string type = !string.IsNullOrEmpty(extension) ? extension.Substring(1).ToLowerInvariant() : "";

                if (excludedExtensions.Contains(type)) continue;

                // Apply extension filter if type group didn't map to a Unity filter
                if (extensionFilter != null && extensionFilter.Length > 0)
                {
                    if (!extensionFilter.Contains(type)) continue;
                }

                // Apply text search terms client-side
                if (!MatchesSearchTerms(path, fileName, includeTerms, excludeTerms)) continue;

                AssetInfo info = CreateVirtualAssetInfo(guid, path, fileName, type);
                files.Add(info);
            }

            // Apply metadata filters (post-filter using cached extraction)
            if (HasMetadataFilter(opt))
            {
                files = ApplyMetadataFilters(files, opt);
            }

            // Apply sorting to match index search sort order
            files = ApplySorting(files, opt.SortField, opt.SortDescending);

            result.ResultCount = files.Count;

            if (opt.MaxResults > 0)
            {
                int skip = (opt.CurrentPage - 1) * opt.MaxResults;
                files = files.Skip(skip).Take(opt.MaxResults).ToList();
            }

            result.Files = files;
            return result;
        }

        private static AssetInfo CreateVirtualAssetInfo(string guid, string path, string fileName, string type)
        {
            AssetInfo info = new AssetInfo();
            info.Id = _virtualIdCounter--;
            info.AssetId = -1;
            info.Guid = guid;
            info.SetPath(path);
            info.FileName = fileName;
            info.Type = type;
            info.ProjectPath = path;
            info.SafeName = fileName;
            info.AssetSource = Asset.Source.CurrentProject;
            info.CurrentState = Asset.State.Done;

            return info;
        }

        public static bool HasMetadataFilter(Options opt)
        {
            return !string.IsNullOrEmpty(opt.SearchSize)
                || (AssetSearch.IsFilterApplicable("ImageType", opt.RawSearchType) && opt.SelectedImageType > 0)
                || (AssetSearch.IsFilterApplicable("Width", opt.RawSearchType) && !string.IsNullOrEmpty(opt.SearchWidth))
                || (AssetSearch.IsFilterApplicable("Height", opt.RawSearchType) && !string.IsNullOrEmpty(opt.SearchHeight))
                || (AssetSearch.IsFilterApplicable("Length", opt.RawSearchType) && !string.IsNullOrEmpty(opt.SearchLength))
                || (AssetSearch.IsFilterApplicable("VertexCount", opt.RawSearchType) && !string.IsNullOrEmpty(opt.SearchVertexCount));
        }

        private static List<AssetInfo> ApplyMetadataFilters(List<AssetInfo> files, Options opt)
        {
            long sizeKb = 0;
            int widthPx = 0;
            int heightPx = 0;
            float lengthSec = 0;
            int vertexCount = 0;
            bool filterSize = !string.IsNullOrEmpty(opt.SearchSize) && long.TryParse(opt.SearchSize, out sizeKb);
            bool filterWidth = AssetSearch.IsFilterApplicable("Width", opt.RawSearchType) && !string.IsNullOrEmpty(opt.SearchWidth) && int.TryParse(opt.SearchWidth, out widthPx);
            bool filterHeight = AssetSearch.IsFilterApplicable("Height", opt.RawSearchType) && !string.IsNullOrEmpty(opt.SearchHeight) && int.TryParse(opt.SearchHeight, out heightPx);
            bool filterLength = AssetSearch.IsFilterApplicable("Length", opt.RawSearchType) && !string.IsNullOrEmpty(opt.SearchLength) && float.TryParse(opt.SearchLength, out lengthSec);
            bool filterVertexCount = AssetSearch.IsFilterApplicable("VertexCount", opt.RawSearchType) && !string.IsNullOrEmpty(opt.SearchVertexCount) && int.TryParse(opt.SearchVertexCount, out vertexCount);
            bool filterImageType = AssetSearch.IsFilterApplicable("ImageType", opt.RawSearchType) && opt.SelectedImageType > 0 && opt.ImageTypeOptions != null && opt.SelectedImageType < opt.ImageTypeOptions.Length;

            string[] imageTypePatterns = null;
            if (filterImageType)
            {
                string key = opt.ImageTypeOptions[opt.SelectedImageType].ToLowerInvariant();
                if (TextureNameSuggester.suffixPatterns.ContainsKey(key))
                {
                    imageTypePatterns = TextureNameSuggester.suffixPatterns[key];
                }
                else
                {
                    filterImageType = false;
                }
            }

            List<AssetInfo> result = new List<AssetInfo>();
            foreach (AssetInfo info in files)
            {
                string fullPath = Path.GetFullPath(info.ProjectPath);
                if (!ProjectMetadataCache.TryGet(info.Guid, fullPath, out ProjectMetadataCache.Metadata meta))
                {
                    meta = ProjectMetadataCache.CreateEmpty(fullPath);
                }

                // File size (cheap — stat syscall)
                if (filterSize)
                {
                    if (!meta.HasSize)
                    {
                        try
                        {
                            FileInfo fi = new FileInfo(fullPath);
                            fi.Refresh();
                            meta.Size = fi.Length;
                        }
                        catch (Exception)
                        {
                            meta.Size = 0;
                        }
                    }
                    long sizeInKb = meta.Size / 1024;
                    if (opt.CheckMaxSize)
                    {
                        if (sizeInKb > sizeKb) { ProjectMetadataCache.Store(info.Guid, meta); continue; }
                    }
                    else
                    {
                        if (sizeInKb < sizeKb) { ProjectMetadataCache.Store(info.Guid, meta); continue; }
                    }
                }

                // Image type (trivial — filename suffix matching)
                if (filterImageType)
                {
                    string nameWithoutExt = Path.GetFileNameWithoutExtension(info.FileName)?.ToLowerInvariant() ?? "";
                    bool matched = false;
                    foreach (string pattern in imageTypePatterns)
                    {
                        if (string.IsNullOrEmpty(pattern))
                        {
                            // empty pattern = base name (albedo); only match if no other suffix matches
                            matched = true;
                        }
                        else if (nameWithoutExt.EndsWith(pattern))
                        {
                            matched = true;
                            break;
                        }
                    }
                    if (!matched) continue;
                }

                // Width/Height (cheap — binary header parse)
                if (filterWidth || filterHeight)
                {
                    if (!meta.HasDimensions)
                    {
                        Tuple<int, int> dim = ImageUtils.GetDimensions(fullPath, true, "." + info.Type);
                        if (dim != null)
                        {
                            meta.Width = dim.Item1;
                            meta.Height = dim.Item2;
                        }
                        else
                        {
                            meta.Width = 0;
                            meta.Height = 0;
                        }
                    }

                    if (filterWidth)
                    {
                        if (opt.CheckMaxWidth)
                        {
                            if (meta.Width > widthPx) { ProjectMetadataCache.Store(info.Guid, meta); continue; }
                        }
                        else
                        {
                            if (meta.Width < widthPx) { ProjectMetadataCache.Store(info.Guid, meta); continue; }
                        }
                    }
                    if (filterHeight)
                    {
                        if (opt.CheckMaxHeight)
                        {
                            if (meta.Height > heightPx) { ProjectMetadataCache.Store(info.Guid, meta); continue; }
                        }
                        else
                        {
                            if (meta.Height < heightPx) { ProjectMetadataCache.Store(info.Guid, meta); continue; }
                        }
                    }
                }

                // Audio length (cheap — binary header parse)
                if (filterLength)
                {
                    if (!meta.HasLength)
                    {
                        AudioHeaderReader.AudioHeaderInfo header = AudioHeaderReader.ReadHeader(fullPath);
                        meta.Length = header != null && header.Duration > 0 ? header.Duration : 0;
                    }

                    if (opt.CheckMaxLength)
                    {
                        if (meta.Length > lengthSec) { ProjectMetadataCache.Store(info.Guid, meta); continue; }
                    }
                    else
                    {
                        if (meta.Length < lengthSec) { ProjectMetadataCache.Store(info.Guid, meta); continue; }
                    }
                }

                // Vertex count (expensive — requires asset load)
                if (filterVertexCount)
                {
                    if (!meta.HasVertexCount)
                    {
                        FBXData fbxData = FBXDataExtractor.ExtractFBXData(fullPath);
                        meta.VertexCount = fbxData?.vertexCount ?? 0;
                    }

                    if (opt.CheckMaxVertexCount)
                    {
                        if (meta.VertexCount > vertexCount) { ProjectMetadataCache.Store(info.Guid, meta); continue; }
                    }
                    else
                    {
                        if (meta.VertexCount < vertexCount) { ProjectMetadataCache.Store(info.Guid, meta); continue; }
                    }
                }

                // Populate AssetInfo fields for display
                if (meta.HasSize) info.Size = meta.Size;
                if (meta.HasDimensions) { info.Width = meta.Width; info.Height = meta.Height; }
                if (meta.HasLength) info.Length = meta.Length;

                ProjectMetadataCache.Store(info.Guid, meta);
                result.Add(info);
            }

            return result;
        }

        private static List<AssetInfo> ApplySorting(List<AssetInfo> files, int sortField, bool sortDescending)
        {
            if (sortField <= 1 || files.Count <= 1) return files;

            Comparison<AssetInfo> comparison;
            switch (sortField)
            {
                case 2: comparison = (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase); break;
                case 3: comparison = (a, b) => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase); break;
                case 4: comparison = (a, b) => a.Size.CompareTo(b.Size); break;
                case 5: comparison = (a, b) => string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase); break;
                case 6: comparison = (a, b) => a.Length.CompareTo(b.Length); break;
                case 7: comparison = (a, b) => a.Width.CompareTo(b.Width); break;
                case 8: comparison = (a, b) => a.Height.CompareTo(b.Height); break;
                default: return files;
            }

            if (sortDescending)
            {
                Comparison<AssetInfo> original = comparison;
                comparison = (a, b) => original(b, a);
            }

            files.Sort(comparison);
            return files;
        }

        private static string BuildFilter(Options opt)
        {
            string filter = "";

            // Map type group to Unity type filter
            if (!string.IsNullOrEmpty(opt.RawSearchType))
            {
                string typeKey = opt.RawSearchType;

                // Handle grouped types like "Images/png" -> "Images"
                if (typeKey.Contains("/"))
                {
                    typeKey = typeKey.Substring(0, typeKey.IndexOf('/'));
                }

                if (TypeGroupToUnityFilter.TryGetValue(typeKey, out string unityFilter))
                {
                    filter = unityFilter;
                }
            }

            // Add text search phrase for AssetDatabase (basic name matching)
            // Only use simple terms - AssetDatabase doesn't support exclusion or expert syntax
            if (!string.IsNullOrEmpty(opt.SearchPhrase))
            {
                string phrase = opt.SearchPhrase.Trim();

                // Skip expert mode and special syntax - handle client-side only
                if (!phrase.StartsWith("=") && !phrase.StartsWith("~"))
                {
                    // Extract first positive term for AssetDatabase filter
                    string[] parts = phrase.Split(' ');
                    foreach (string part in parts)
                    {
                        string trimmed = part.Trim();
                        if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("-") && !trimmed.StartsWith("+")
                            && !trimmed.StartsWith("pt:") && !trimmed.StartsWith("ft:") && !trimmed.StartsWith("withall"))
                        {
                            filter += " " + trimmed;
                            break; // AssetDatabase works best with a single search term
                        }
                    }
                }
            }

            return filter.Trim();
        }

        private static void ParseSearchTerms(string phrase, List<string> includeTerms, List<string> excludeTerms)
        {
            if (string.IsNullOrEmpty(phrase)) return;

            string[] parts = phrase.Trim().Split(' ');
            foreach (string part in parts)
            {
                string trimmed = part.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                // Skip tag syntax and expert mode
                if (trimmed.StartsWith("pt:") || trimmed.StartsWith("ft:") || trimmed.StartsWith("withall")
                    || trimmed.StartsWith("=") || trimmed.StartsWith("~")) continue;

                if (trimmed.StartsWith("-"))
                {
                    string term = trimmed.Substring(1);
                    if (!string.IsNullOrEmpty(term)) excludeTerms.Add(term);
                }
                else
                {
                    string term = trimmed.StartsWith("+") ? trimmed.Substring(1) : trimmed;
                    if (!string.IsNullOrEmpty(term)) includeTerms.Add(term);
                }
            }
        }

        private static bool MatchesSearchTerms(string path, string fileName, List<string> includeTerms, List<string> excludeTerms)
        {
            if (includeTerms.Count == 0 && excludeTerms.Count == 0) return true;

            foreach (string term in excludeTerms)
            {
                if (path.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }

            foreach (string term in includeTerms)
            {
                if (path.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0
                    && fileName.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static string[] GetExtensionFilter(string rawSearchType)
        {
            if (string.IsNullOrEmpty(rawSearchType)) return null;

            string typeKey = rawSearchType;
            string specificExt = null;

            // Handle "Images/png" format
            if (typeKey.Contains("/"))
            {
                specificExt = typeKey.Substring(typeKey.IndexOf('/') + 1).ToLowerInvariant();
                typeKey = typeKey.Substring(0, typeKey.IndexOf('/'));
            }

            // If a specific extension was selected (e.g. "png"), filter to just that
            if (!string.IsNullOrEmpty(specificExt))
            {
                return new[] {specificExt};
            }

            // Always apply extension filtering for known groups;
            // Unity type filters (e.g. t:Texture) are broader than the actual extension list
            if (Enum.TryParse(typeKey, true, out AI.AssetGroup group) && AI.TypeGroups.TryGetValue(group, out string[] extensions))
            {
                return extensions;
            }

            // If this is a known group name with a Unity filter but no extension list, let AssetDatabase handle it
            if (TypeGroupToUnityFilter.ContainsKey(typeKey))
            {
                return null;
            }

            // Treat as a single extension
            return new[] {typeKey.ToLowerInvariant()};
        }

        private static HashSet<string> GetExcludedExtensionSet(bool ignoreExcludedExtensions)
        {
            string[] extensions = AssetSearch.GetConfiguredExcludedExtensions(ignoreExcludedExtensions);
            if (extensions.Length == 0) return new HashSet<string>();

            return new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resets the virtual ID counter. Call when scope changes to avoid stale IDs.
        /// </summary>
        public static void ResetIdCounter()
        {
            _virtualIdCounter = -1;
        }
    }
}
