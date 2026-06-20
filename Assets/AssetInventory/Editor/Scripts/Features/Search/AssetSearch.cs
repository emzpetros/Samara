using ImpossibleRobert.Common;
using Database;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MySqlConnector;
using UnityEditor;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace AssetInventory
{
    public static class AssetSearch
    {
        private const string PACKAGE_TAG_JOIN_CLAUSE = "inner join TagAssignment as tap on (Asset.Id = tap.TargetId and tap.TagTarget = 0)";
        private const string FILE_TAG_JOIN_CLAUSE = "inner join TagAssignment as taf on (AssetFile.Id = taf.TargetId and taf.TagTarget = 1)";
        private const string SQLITE_PATH_INDEX_SOURCE = "AssetFile INDEXED BY AssetFile_Path";

        public static class Diagnostics
        {
            public static bool Enabled { get; set; }
            public static bool IncludeSql { get; set; }
            public static int SqlPreviewLength { get; set; } = 4000;

            internal static bool IsEnabled => Enabled || IsEnvironmentEnabled("ASSET_INVENTORY_PROFILE_SEARCH");
            internal static bool ShouldIncludeSql => IncludeSql || IsEnvironmentEnabled("ASSET_INVENTORY_PROFILE_SEARCH_SQL");

            private static bool IsEnvironmentEnabled(string name)
            {
                string value = Environment.GetEnvironmentVariable(name);
                return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool SearchFieldCanBeNull(string field)
        {
            return field == "AssetFile.AICaption" || field == "Asset.DisplayName";
        }

        private static string BuildLikeCondition(string field, string escape)
        {
            return SearchFieldCanBeNull(field) ? $"({field} IS NOT NULL AND {field} like ? {escape})" : $"{field} like ? {escape}";
        }

        private static string BuildNotLikeCondition(string field, string escape)
        {
            return SearchFieldCanBeNull(field) ? $"({field} IS NULL OR {field} not like ? {escape})" : $"{field} not like ? {escape}";
        }

        private static bool SortUsesTextCollation(int sortField)
        {
            switch (sortField)
            {
                case 2:
                case 3:
                case 5:
                case 10:
                    return true;
                default:
                    return false;
            }
        }

        private static string GetAssetFileSource(Options opt)
        {
            if (AI.Config?.databaseType == DatabaseFactory.MYSQL
                && AI.Config.sortField == 2
                && opt.SelectedAsset <= 0
                && opt.SelectedAssetId <= 0)
            {
                return "AssetFile FORCE INDEX(idx_AssetFile_Path)";
            }

            return "AssetFile";
        }

        private static bool CanUseSQLitePathIndexDataQuery(Options opt)
        {
            return AI.Config?.databaseType == DatabaseFactory.SQLITE
                && AI.Config.sortField == 2
                && opt.SelectedAsset <= 0
                && opt.SelectedAssetId <= 0;
        }

        private static bool CanUseSQLitePathIndexCountQuery(Options opt)
        {
            return CanUseSQLitePathIndexDataQuery(opt)
                && opt.RawSearchType == null
                && opt.SelectedPackageTag <= 0
                && opt.SelectedFileTag <= 0
                && opt.SelectedPublisher <= 0
                && opt.SelectedCategory <= 0
                && opt.SelectedColorOption <= 0
                && opt.SelectedPriceOption <= 0
                && opt.SelectedImageType <= 0
                && opt.SelectedPreviewFilter == 0
                && opt.SelectedHiddenFilter == 0
                && opt.SelectedPackageTypes <= 1
                && opt.SelectedPackageSRPs <= 1
                && string.IsNullOrWhiteSpace(opt.SearchWidth)
                && string.IsNullOrWhiteSpace(opt.SearchHeight)
                && string.IsNullOrWhiteSpace(opt.SearchLength)
                && string.IsNullOrWhiteSpace(opt.SearchSize)
                && string.IsNullOrWhiteSpace(opt.SearchVertexCount);
        }

        public class Options
        {
            public string SearchPhrase = string.Empty;
            public int SelectedPackageSRPs = 0;
            public int SelectedPriceOption = 0;
            public float SearchPrice = 0f;
            public string SearchWidth = string.Empty;
            public bool CheckMaxWidth = false;
            public string SearchHeight = string.Empty;
            public bool CheckMaxHeight = false;
            public string SearchLength = string.Empty;
            public bool CheckMaxLength = false;
            public string SearchSize = string.Empty;
            public bool CheckMaxSize = false;
            public string SearchVertexCount = string.Empty;
            public bool CheckMaxVertexCount = false;
            public int SelectedPackageTag = 0;
            public int SelectedFileTag = 0;
            public int SelectedPackageTypes = 0;
            public int SelectedPublisher = 0;
            public int SelectedAsset = 0;
            public int SelectedAssetId = 0;
            public int SelectedCategory = 0;
            public int SelectedColorOption = 0;
            public UnityEngine.Color SelectedColor = UnityEngine.Color.white;
            public int SelectedImageType = 0;
            public int SelectedPreviewFilter = 0; // 0=both, 2=has preview, 3=no preview
            public int SelectedHiddenFilter = 0; // 0=hide hidden, 1=show all, 2=only hidden
            public string RawSearchType = null; // precomputed caller value or null
            public bool IgnoreExcludedExtensions = false;
            public int CurrentPage = 1;
            public int MaxResults = 0; // 0 disables limit
            public InMemoryMode InMemory = InMemoryMode.None;

            // data references as input
            public Dictionary<string, string> SearchVariables = null; // variable name → current value
            public List<AssetInfo> AllAssets = new List<AssetInfo>(); // required for ResolveParents
            public string[] TagNames = Array.Empty<string>();
            public List<Tag> Tags = new List<Tag>();
            public string[] AssetNames = Array.Empty<string>();
            public string[] PublisherNames = Array.Empty<string>();
            public string[] CategoryNames = Array.Empty<string>();
            public string[] ImageTypeOptions = Array.Empty<string>();

            /// <summary>
            /// Creates a default Options object with all data loaded from the database.
            /// </summary>
            public static Options CreateDefault()
            {
                List<AssetInfo> allAssets = Assets.Load().ToList();
                List<Tag> tags = DBAdapter.DB.Table<Tag>().ToList();
                string[] tagNames = Assets.ExtractTagNames(tags);
                string[] publisherNames = Assets.ExtractPublisherNames(allAssets);
                string[] categoryNames = Assets.ExtractCategoryNames(allAssets);
                string[] assetNames = Assets.ExtractAssetNames(allAssets, true);
                string[] types = Assets.LoadTypes();
                string[] imageTypeOptions = new List<string> {"-all-", string.Empty}.Concat(TextureNameSuggester.suffixPatterns.Keys.Select(StringUtils.CamelCaseToWords)).ToArray();

                Options options = new Options
                {
                    AllAssets = allAssets,
                    Tags = tags,
                    TagNames = tagNames,
                    PublisherNames = publisherNames,
                    CategoryNames = categoryNames,
                    AssetNames = assetNames,
                    ImageTypeOptions = imageTypeOptions
                };

                return options;
            }

            /// <summary>
            /// Creates an Options object from a SavedSearch.
            /// </summary>
            public static Options FromSavedSearch(SavedSearch savedSearch)
            {
                Options options = CreateDefault();

                // Parse variable definitions if present
                Dictionary<string, string> searchVariables = null;
                if (!string.IsNullOrEmpty(savedSearch.VariableDefinitions))
                {
                    searchVariables = DeserializeSearchVariables(savedSearch.VariableDefinitions);
                }

                // Map saved search properties to options
                options.SearchPhrase = savedSearch.SearchPhrase ?? string.Empty;
                options.SearchVariables = searchVariables;
                options.SelectedPackageSRPs = savedSearch.PackageSrPs;
                options.SelectedPriceOption = savedSearch.PriceOption;
                options.SearchPrice = savedSearch.Price;
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
                options.SelectedPackageTag = FindIndexByValue(options.TagNames, savedSearch.PackageTag, splitPath: false);
                options.SelectedFileTag = FindIndexByValue(options.TagNames, savedSearch.FileTag, splitPath: false);
                options.SelectedPackageTypes = savedSearch.PackageTypes;
                options.SelectedPublisher = FindIndexByValue(options.PublisherNames, savedSearch.Publisher, splitPath: true);
                options.SelectedAsset = FindIndexByValue(options.AssetNames, savedSearch.Package, splitPath: true);
                options.SelectedCategory = FindIndexByValue(options.CategoryNames, savedSearch.Category, splitPath: false);
                options.SelectedColorOption = savedSearch.ColorOption;
                options.SelectedColor = ImageUtils.FromHex(savedSearch.SearchColor);
                options.SelectedImageType = savedSearch.ImageType;
                options.SelectedPreviewFilter = 0; // Default to both
                options.SelectedHiddenFilter = savedSearch.Hidden;
                options.RawSearchType = GetRawSearchType(savedSearch.Type, Assets.LoadTypes());
                options.IgnoreExcludedExtensions = false;
                options.CurrentPage = 1;
                options.MaxResults = 0; // No limit
                options.InMemory = InMemoryMode.None;

                return options;
            }

            private static int FindIndexByValue(string[] items, string value, bool splitPath = false)
            {
                if (string.IsNullOrWhiteSpace(value)) return 0;

                // Extract ID from value if it has brackets
                string valueId = null;
                int valueBracketStart = value.LastIndexOf('[');
                if (valueBracketStart > 0)
                {
                    valueId = value.Substring(valueBracketStart + 1, value.Length - valueBracketStart - 2);
                }

                return UnityEngine.Mathf.Max(0, Array.FindIndex(items, s =>
                {
                    string itemToCheck = splitPath ? s.Split('/').LastOrDefault() : s;

                    // If we have an ID, try to match by ID
                    if (valueId != null)
                    {
                        int itemBracketStart = itemToCheck.LastIndexOf('[');
                        if (itemBracketStart > 0)
                        {
                            string itemId = itemToCheck.Substring(itemBracketStart + 1, itemToCheck.Length - itemBracketStart - 2);
                            return itemId == valueId;
                        }
                    }

                    // Otherwise fall back to exact string match
                    return itemToCheck == value;
                }));
            }

            private static string GetRawSearchType(string savedType, string[] types)
            {
                if (string.IsNullOrWhiteSpace(savedType)) return null;
                int typeIdx = UnityEngine.Mathf.Max(0, Array.FindIndex(types, s => s.Split('/').LastOrDefault() == savedType));
                return typeIdx > 0 && types.Length > typeIdx ? types[typeIdx] : null;
            }

            private static Dictionary<string, string> DeserializeSearchVariables(string variableDefinitions)
            {
                // Simple JSON deserialization - matches the pattern used in IndexUI+Search.cs
                Dictionary<string, string> result = new Dictionary<string, string>();
                if (string.IsNullOrEmpty(variableDefinitions)) return result;

                try
                {
                    // The VariableDefinitions is stored as JSON, parse it
                    // For now, return empty dict - variables will be resolved at search time if needed
                    // This matches the behavior where variables are optional
                    return result;
                }
                catch
                {
                    return result;
                }
            }
        }

        public enum InMemoryMode
        {
            None,
            Init,
            Active
        }

        public class Result
        {
            public List<AssetInfo> Files = new List<AssetInfo>();
            public int ResultCount;
            public int OriginalResultCount;
            public string Error;
            public InMemoryMode InMemory;
        }

        public static Result Execute(Options opt)
        {
            bool profile = Diagnostics.IsEnabled;
            Stopwatch stopwatch = profile ? Stopwatch.StartNew() : null;
            long lastProfileMs = 0;
            List<string> profileSteps = profile ? new List<string>() : null;

            AI.Init();
            Result result = new Result {InMemory = opt.InMemory};

            QueryResult qr = BuildQuery(opt);
            RecordProfileStep(profileSteps, stopwatch, ref lastProfileMs, "BuildQuery", $"args={qr.Args?.Count ?? 0}");
            if (qr.Error != null)
            {
                result.Error = qr.Error;
                WriteSearchProfile(opt, qr, result, profileSteps, stopwatch);
                return result;
            }

            string countQuery = qr.FastCountQuery ?? qr.CountQuery;
            string dataQuery = GetInitialDataQuery(qr);
            List<object> args = qr.Args;

            if (opt.MaxResults > 0 && opt.InMemory == InMemoryMode.None)
            {
                int offset = Math.Max(0, (opt.CurrentPage - 1) * opt.MaxResults);
                dataQuery += $" limit {opt.MaxResults} offset {offset}";
            }

            try
            {
                result.Error = null;

                if (opt.MaxResults > 0 && opt.InMemory == InMemoryMode.None)
                {
                    if (ShouldRunSQLitePathCountFirst(qr))
                    {
                        result.ResultCount = DBAdapter.DB.ExecuteScalar<int>($"{countQuery}", args.ToArray());
                        RecordProfileStep(profileSteps, stopwatch, ref lastProfileMs, "CountQuery", $"count={result.ResultCount}");
                        result.OriginalResultCount = result.ResultCount;

                        int offset = Math.Max(0, (opt.CurrentPage - 1) * opt.MaxResults);
                        if (result.ResultCount <= offset)
                        {
                            RecordProfileStep(profileSteps, stopwatch, ref lastProfileMs, "DataQuery", "skipped");
                        }
                        else
                        {
                            bool sortInMemory;
                            dataQuery = SelectSQLitePathDataQuery(qr, opt, result.ResultCount, out sortInMemory);
                            dataQuery = AddPaging(dataQuery, opt);
                            result.Files = DBAdapter.DB.Query<AssetInfo>($"{dataQuery}", args.ToArray());
                            if (sortInMemory) SortSmallPathResultPage(result.Files);
                            RecordProfileStep(profileSteps, stopwatch, ref lastProfileMs, "DataQuery", $"rows={result.Files.Count}");
                        }
                    }
                    else
                    {
                        // Run data first; if the page is not full we can derive the total without a second scan.
                        result.Files = DBAdapter.DB.Query<AssetInfo>($"{dataQuery}", args.ToArray());
                        RecordProfileStep(profileSteps, stopwatch, ref lastProfileMs, "DataQuery", $"rows={result.Files.Count}");

                        int offset = Math.Max(0, (opt.CurrentPage - 1) * opt.MaxResults);
                        if (result.Files.Count < opt.MaxResults)
                        {
                            result.ResultCount = offset + result.Files.Count;
                            RecordProfileStep(profileSteps, stopwatch, ref lastProfileMs, "CountQuery", "skipped");
                        }
                        else
                        {
                            result.ResultCount = DBAdapter.DB.ExecuteScalar<int>($"{countQuery}", args.ToArray());
                            RecordProfileStep(profileSteps, stopwatch, ref lastProfileMs, "CountQuery", $"count={result.ResultCount}");
                        }
                        result.OriginalResultCount = result.ResultCount;
                    }
                }
                else
                {
                    result.ResultCount = DBAdapter.DB.ExecuteScalar<int>($"{countQuery}", args.ToArray());
                    RecordProfileStep(profileSteps, stopwatch, ref lastProfileMs, "CountQuery", $"count={result.ResultCount}");
                    result.OriginalResultCount = result.ResultCount;

                    if (opt.MaxResults > 0 && opt.InMemory != InMemoryMode.None && result.ResultCount > AI.Config.maxInMemoryResults)
                    {
                        result.InMemory = InMemoryMode.None;
                        int offset = Math.Max(0, (opt.CurrentPage - 1) * opt.MaxResults);
                        dataQuery += $" limit {opt.MaxResults} offset {offset}";
                        EditorUtility.DisplayDialog("Search Result Limit Exceeded",
                            $"There are more than {AI.Config.maxInMemoryResults:N0} search results (configured in search settings). In-Memory mode was therefore disabled again.",
                            "OK");
                    }

                    result.Files = DBAdapter.DB.Query<AssetInfo>($"{dataQuery}", args.ToArray());
                    RecordProfileStep(profileSteps, stopwatch, ref lastProfileMs, "DataQuery", $"rows={result.Files.Count}");
                }
            }
            catch (SQLite.SQLiteException e)
            {
                result.Error = e.Message;
                RecordProfileStep(profileSteps, stopwatch, ref lastProfileMs, "SQLiteException", e.Message);
            }
            catch (MySqlException e)
            {
                result.Error = e.Message;
                RecordProfileStep(profileSteps, stopwatch, ref lastProfileMs, "MySqlException", e.Message);
            }

            Assets.ResolveParents(result.Files, opt.AllAssets);
            RecordProfileStep(profileSteps, stopwatch, ref lastProfileMs, "ResolveParents", $"rows={result.Files.Count}, allAssets={opt.AllAssets?.Count ?? 0}");
            WriteSearchProfile(opt, qr, result, profileSteps, stopwatch);
            return result;
        }

        private static string GetInitialDataQuery(QueryResult qr)
        {
            return qr.FastCountQuery != null && qr.FastDataQuery != null ? qr.FastDataQuery : qr.DataQuery;
        }

        private static bool ShouldRunSQLitePathCountFirst(QueryResult qr)
        {
            return qr.FastCountQuery == null && qr.FastDataQuery != null;
        }

        private static string SelectSQLitePathDataQuery(QueryResult qr, Options opt, int resultCount, out bool sortInMemory)
        {
            sortInMemory = false;

            if (opt.CurrentPage == 1 && resultCount <= opt.MaxResults && !string.IsNullOrEmpty(qr.UnorderedDataQuery))
            {
                sortInMemory = true;
                return qr.UnorderedDataQuery;
            }

            if (resultCount >= opt.MaxResults * 4 && !string.IsNullOrEmpty(qr.FastDataQuery)) return qr.FastDataQuery;
            return qr.DataQuery;
        }

        private static string AddPaging(string query, Options opt)
        {
            if (opt.MaxResults <= 0) return query;

            int offset = Math.Max(0, (opt.CurrentPage - 1) * opt.MaxResults);
            return $"{query} limit {opt.MaxResults} offset {offset}";
        }

        private static void SortSmallPathResultPage(List<AssetInfo> files)
        {
            if (files == null || files.Count <= 1 || AI.Config.sortField != 2) return;

            files.Sort((a, b) =>
            {
                string aPath = a?.Path ?? string.Empty;
                string bPath = b?.Path ?? string.Empty;
                int result = string.Compare(aPath, bPath, StringComparison.OrdinalIgnoreCase);
                if (AI.Config.sortDescending) result = -result;
                if (result != 0) return result;
                return string.Compare(aPath, bPath, StringComparison.Ordinal);
            });
        }

        private static void RecordProfileStep(List<string> profileSteps, Stopwatch stopwatch, ref long lastProfileMs, string label, string details = null)
        {
            if (profileSteps == null || stopwatch == null) return;

            long elapsedMs = stopwatch.ElapsedMilliseconds;
            long stepMs = elapsedMs - lastProfileMs;
            lastProfileMs = elapsedMs;

            profileSteps.Add(string.IsNullOrEmpty(details)
                ? $"{label}: {stepMs} ms"
                : $"{label}: {stepMs} ms ({details})");
        }

        private static void WriteSearchProfile(Options opt, QueryResult qr, Result result, List<string> profileSteps, Stopwatch stopwatch)
        {
            if (profileSteps == null || stopwatch == null) return;

            StringBuilder sb = new StringBuilder();
            sb.Append("[Asset Inventory] Search profile");
            sb.Append($" db={AI.Config?.databaseType ?? "unknown"}");
            sb.Append($", phrase='{TruncateProfileText(opt.SearchPhrase, 120)}'");
            sb.Append($", type='{opt.RawSearchType ?? "all"}'");
            sb.Append($", page={opt.CurrentPage}");
            sb.Append($", maxResults={opt.MaxResults}");
            sb.Append($", resultCount={result.ResultCount}");
            sb.Append($", returned={result.Files.Count}");
            if (!string.IsNullOrEmpty(result.Error)) sb.Append($", error='{TruncateProfileText(result.Error, 200)}'");
            sb.Append($", total={stopwatch.ElapsedMilliseconds} ms");

            foreach (string step in profileSteps)
            {
                sb.AppendLine();
                sb.Append("  - ");
                sb.Append(step);
            }

            if (Diagnostics.ShouldIncludeSql && qr != null)
            {
                sb.AppendLine();
                sb.Append("  Count SQL: ");
                sb.Append(TruncateProfileText(qr.CountQuery, Diagnostics.SqlPreviewLength));
                sb.AppendLine();
                sb.Append("  Data SQL: ");
                sb.Append(TruncateProfileText(qr.DataQuery, Diagnostics.SqlPreviewLength));
            }

            UnityEngine.Debug.Log(sb.ToString());
        }

        private static string TruncateProfileText(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (maxLength <= 0 || value.Length <= maxLength) return value;
            return value.Substring(0, maxLength) + "...";
        }

        public static HashSet<string> FindIndexedGuids(Options opt, List<string> guids)
        {
            HashSet<string> result = new HashSet<string>();
            if (guids == null || guids.Count == 0) return result;

            AI.Init();
            QueryResult qr = BuildQuery(opt);
            if (qr.Error != null) return result;

            string whereConnector = qr.BaseQuery.Contains("where ") ? "and" : "where";

            const int batchSize = 500;
            for (int i = 0; i < guids.Count; i += batchSize)
            {
                List<string> batch = guids.GetRange(i, Math.Min(batchSize, guids.Count - i));
                List<object> args = new List<object>(qr.Args);
                string placeholders = string.Join(",", batch.Select(_ => "?"));
                args.AddRange(batch.Cast<object>());

                string query = $"select distinct AssetFile.Guid {qr.BaseQuery} {whereConnector} AssetFile.Guid in ({placeholders})";

                try
                {
                    List<AssetInfo> rows = DBAdapter.DB.Query<AssetInfo>(query, args.ToArray());
                    foreach (AssetInfo row in rows)
                    {
                        if (!string.IsNullOrEmpty(row.Guid)) result.Add(row.Guid);
                    }
                }
                catch (Exception)
                {
                    // Fall back: treat batch as non-indexed
                }
            }

            return result;
        }

        public class QueryResult
        {
            public string BaseQuery;
            public string CountQuery;
            public string DataQuery;
            public string FastCountQuery;
            public string FastDataQuery;
            public string UnorderedDataQuery;
            public List<object> Args;
            public string Error;
        }

        public static QueryResult BuildQuery(Options opt)
        {
            List<string> wheres = new List<string>();
            List<object> args = new List<object>();
            string packageTagJoin = "";
            string fileTagJoin = "";
            string computedFields = "";
            string lastWhere = null;
            string phrase = opt.SearchPhrase ?? string.Empty;

            // Substitute search variables before processing
            if (opt.SearchVariables != null && opt.SearchVariables.Count > 0)
            {
                try
                {
                    phrase = VariableResolver.ReplaceVariables(phrase, opt.SearchVariables);
                }
                catch (Exception ex)
                {
                    return new QueryResult {Error = $"Variable substitution error: {ex.Message}"};
                }
            }

            wheres.Add("Asset.Exclude=0");

            // hidden files filter
            switch (opt.SelectedHiddenFilter)
            {
                case 0:
                    wheres.Add("(AssetFile.Hidden IS NULL OR AssetFile.Hidden <> 1)");
                    break;
                case 2:
                    wheres.Add("AssetFile.Hidden=1");
                    break;
            }

            List<string> withAllPT = new List<string>();
            phrase = StringUtils.ExtractTokens(phrase, "withallpt", withAllPT);
            List<string> withAnyPT = new List<string>();
            phrase = StringUtils.ExtractTokens(phrase, new[] {"withanypt", "pt"}, withAnyPT);
            List<string> withNonePT = new List<string>();
            phrase = StringUtils.ExtractTokens(phrase, new[] {"withnonept", "withnopt"}, withNonePT);

            List<string> withAllFT = new List<string>();
            phrase = StringUtils.ExtractTokens(phrase, "withallft", withAllFT);
            List<string> withAnyFT = new List<string>();
            phrase = StringUtils.ExtractTokens(phrase, new[] {"withanyft", "ft"}, withAnyFT);
            List<string> withNoneFT = new List<string>();
            phrase = StringUtils.ExtractTokens(phrase, new[] {"withnoneft", "withnoft"}, withNoneFT);

            List<string> withAllPTTags = StringUtils.FlattenCommaSeparated(withAllPT);
            if (withAllPTTags.Count > 0)
            {
                foreach (string tag in withAllPTTags)
                {
                    wheres.Add("exists (select tap2.Id from TagAssignment as tap2 where Asset.Id = tap2.TargetId and tap2.TagTarget = 0 and tap2.TagId = ?)");
                    args.Add(opt.Tags.FirstOrDefault(t => t.Name.ToLowerInvariant() == tag.ToLowerInvariant())?.Id);
                }
            }
            List<string> withAnyPTTags = StringUtils.FlattenCommaSeparated(withAnyPT);
            if (withAnyPTTags.Count > 0)
            {
                List<string> conditions = new List<string>();
                foreach (string tag in withAnyPTTags)
                {
                    conditions.Add("tap.TagId = ?");
                    args.Add(opt.Tags.FirstOrDefault(t => t.Name.ToLowerInvariant() == tag.ToLowerInvariant())?.Id);
                }
                wheres.Add("(" + string.Join(" OR ", conditions) + ")");
                packageTagJoin = PACKAGE_TAG_JOIN_CLAUSE;
            }
            List<string> withNonePTTags = StringUtils.FlattenCommaSeparated(withNonePT);
            if (withNonePTTags.Count > 0)
            {
                List<string> paramCount = new List<string>();
                foreach (string tag in withNonePTTags)
                {
                    paramCount.Add("?");
                    args.Add(opt.Tags.FirstOrDefault(t => t.Name.ToLowerInvariant() == tag.ToLowerInvariant())?.Id);
                }
                wheres.Add("not exists (select tap2.Id from TagAssignment as tap2 where Asset.Id = tap2.TargetId and tap2.TagTarget = 0 and tap2.TagId in (" + string.Join(",", paramCount) + "))");
            }

            List<string> withAllFTTags = StringUtils.FlattenCommaSeparated(withAllFT);
            if (withAllFTTags.Count > 0)
            {
                foreach (string tag in withAllFTTags)
                {
                    wheres.Add("exists (select taf2.Id from TagAssignment as taf2 where AssetFile.Id = taf2.TargetId and taf2.TagTarget = 1 and taf2.TagId = ?)");
                    args.Add(opt.Tags.FirstOrDefault(t => t.Name.ToLowerInvariant() == tag.ToLowerInvariant())?.Id);
                }
            }
            List<string> withAnyFTTags = StringUtils.FlattenCommaSeparated(withAnyFT);
            if (withAnyFTTags.Count > 0)
            {
                List<string> conditions = new List<string>();
                foreach (string tag in withAnyFTTags)
                {
                    conditions.Add("taf.TagId = ?");
                    args.Add(opt.Tags.FirstOrDefault(t => t.Name.ToLowerInvariant() == tag.ToLowerInvariant())?.Id);
                }
                wheres.Add("(" + string.Join(" OR ", conditions) + ")");
                fileTagJoin = FILE_TAG_JOIN_CLAUSE;
            }
            List<string> withNoneFTTags = StringUtils.FlattenCommaSeparated(withNoneFT);
            if (withNoneFTTags.Count > 0)
            {
                List<string> paramCount = new List<string>();
                foreach (string tag in withNoneFTTags)
                {
                    paramCount.Add("?");
                    args.Add(opt.Tags.FirstOrDefault(t => t.Name.ToLowerInvariant() == tag.ToLowerInvariant())?.Id);
                }
                wheres.Add("not exists (select taf2.Id from TagAssignment as taf2 where AssetFile.Id = taf2.TargetId and taf2.TagTarget = 1 and taf2.TagId in (" + string.Join(",", paramCount) + "))");
            }

            // inline tags: packages
            List<string> parsedPackageTags = new List<string>();
            phrase = StringUtils.ExtractTokens(phrase, "pt", parsedPackageTags);
            if (parsedPackageTags.Count > 0)
            {
                List<string> conditions = new List<string>();
                foreach (string tag in parsedPackageTags)
                {
                    conditions.Add("tap.TagId = ?");
                    args.Add(opt.Tags.FirstOrDefault(t => t.Name.ToLowerInvariant() == tag.ToLowerInvariant())?.Id);
                }
                wheres.Add("(" + string.Join(" OR ", conditions) + ")");
                packageTagJoin = PACKAGE_TAG_JOIN_CLAUSE;
            }

            // inline tags: files
            List<string> parsedFileTags = new List<string>();
            phrase = StringUtils.ExtractTokens(phrase, "ft", parsedFileTags);
            if (parsedFileTags.Count > 0)
            {
                List<string> conditions = new List<string>();
                foreach (string tag in parsedFileTags)
                {
                    conditions.Add("taf.TagId = ?");
                    args.Add(opt.Tags.FirstOrDefault(t => t.Name.ToLowerInvariant() == tag.ToLowerInvariant())?.Id);
                }
                wheres.Add("(" + string.Join(" OR ", conditions) + ")");
                fileTagJoin = FILE_TAG_JOIN_CLAUSE;
            }

            // SRP filters
            switch (opt.SelectedPackageSRPs)
            {
                case 0:
                    break;

                case 1: // auto-detect
                    if (AI.Config.excludeIncompatibleSRPs && opt.SelectedAsset <= 0)
                    {
                        bool isURP = AssetUtils.IsOnURP();
                        bool isHDRP = AssetUtils.IsOnHDRP();
                        bool isBIRP = !isURP && !isHDRP;

                        if (isBIRP)
                        {
                            wheres.Add("(Asset.BIRPCompatible = 1 OR (Asset.URPCompatible = 0 AND Asset.HDRPCompatible = 0))");
                        }
                        else if (isURP)
                        {
                            wheres.Add("(Asset.BIRPCompatible = 1 OR Asset.URPCompatible = 1 OR (Asset.URPCompatible = 0 AND Asset.HDRPCompatible = 0))");
                        }
                        else if (isHDRP)
                        {
                            wheres.Add("(Asset.BIRPCompatible = 1 OR Asset.HDRPCompatible = 1 OR (Asset.URPCompatible = 0 AND Asset.HDRPCompatible = 0))");
                        }
                    }
                    break;

                case 3:
                    wheres.Add("Asset.BIRPCompatible=1");
                    break;

                case 4:
                    wheres.Add("Asset.URPCompatible=1");
                    break;

                case 5:
                    wheres.Add("Asset.HDRPCompatible=1");
                    break;
            }

            // Price filters
            if (opt.SelectedPriceOption > 0)
            {
                string priceField;
                switch (AI.Config.currency)
                {
                    case 0:
                        priceField = "Asset.PriceEur";
                        break;
                    case 1:
                        priceField = "Asset.PriceUsd";
                        break;
                    case 2:
                        priceField = "Asset.PriceCny";
                        break;
                    default:
                        priceField = "Asset.PriceEur";
                        break;
                }

                switch (opt.SelectedPriceOption)
                {
                    case 1: // -Free-
                        wheres.Add($"{priceField} = 0");
                        break;

                    case 2: // -Paid-
                        wheres.Add($"{priceField} > 0");
                        break;

                    case 4: // < smaller than
                        if (opt.SearchPrice > 0)
                        {
                            wheres.Add($"{priceField} < ?");
                            args.Add(opt.SearchPrice);
                        }
                        break;

                    case 5: // > greater than
                        if (opt.SearchPrice > 0)
                        {
                            wheres.Add($"{priceField} > ?");
                            args.Add(opt.SearchPrice);
                        }
                        break;
                }
            }

            // numeric filters first
            if (IsFilterApplicable("Width", opt.RawSearchType) && !string.IsNullOrWhiteSpace(opt.SearchWidth) && int.TryParse(opt.SearchWidth, out int width) && width > 0)
            {
                string comp = opt.CheckMaxWidth ? "<=" : ">=";
                wheres.Add($"AssetFile.Width > 0 and AssetFile.Width {comp} ?");
                args.Add(width);
            }
            if (IsFilterApplicable("Height", opt.RawSearchType) && !string.IsNullOrWhiteSpace(opt.SearchHeight) && int.TryParse(opt.SearchHeight, out int height) && height > 0)
            {
                string comp = opt.CheckMaxHeight ? "<=" : ">=";
                wheres.Add($"AssetFile.Height > 0 and AssetFile.Height {comp} ?");
                args.Add(height);
            }
            if (IsFilterApplicable("Length", opt.RawSearchType) && !string.IsNullOrWhiteSpace(opt.SearchLength) && float.TryParse(opt.SearchLength, out float length) && length > 0)
            {
                string comp = opt.CheckMaxLength ? "<=" : ">=";
                wheres.Add($"AssetFile.Length > 0 and AssetFile.Length {comp} ?");
                args.Add(length);
            }
            if (!string.IsNullOrWhiteSpace(opt.SearchSize) && int.TryParse(opt.SearchSize, out int size) && size > 0)
            {
                string comp = opt.CheckMaxSize ? "<=" : ">=";
                wheres.Add($"AssetFile.Size > 0 and AssetFile.Size {comp} ?");
                args.Add(size * 1024);
            }
            if (IsFilterApplicable("VertexCount", opt.RawSearchType) && !string.IsNullOrWhiteSpace(opt.SearchVertexCount) && int.TryParse(opt.SearchVertexCount, out int vertexCount) && vertexCount > 0)
            {
                string comp = opt.CheckMaxVertexCount ? "<=" : ">=";
                wheres.Add($"AssetFile.FileData IS NOT NULL and CAST(json_extract(AssetFile.FileData, '$.vertexCount') AS INTEGER) > 0 and CAST(json_extract(AssetFile.FileData, '$.vertexCount') AS INTEGER) {comp} ?");
                args.Add(vertexCount);
            }

            // dropdown tags (ignored if inline tags are present)
            bool anyInlinePT = parsedPackageTags.Count > 0 || withAllPTTags.Count > 0 || withAnyPTTags.Count > 0 || withNonePTTags.Count > 0;
            if (!anyInlinePT)
            {
                if (opt.SelectedPackageTag == 1)
                {
                    wheres.Add("not exists (select tap.Id from TagAssignment as tap where Asset.Id = tap.TargetId and tap.TagTarget = 0)");
                }
                else if (opt.SelectedPackageTag > 1 && opt.TagNames.Length > opt.SelectedPackageTag)
                {
                    string tagName = opt.TagNames[opt.SelectedPackageTag];
                    Tag selectedTag = opt.Tags.FirstOrDefault(t => t.Name == tagName);
                    int? tagId = selectedTag?.Id;
                    if (tagId.HasValue)
                    {
                        HashSet<int> descendantIds = Tagging.GetDescendantTagIds(tagId.Value);
                        List<string> placeholders = new List<string>();
                        foreach (int id in descendantIds)
                        {
                            placeholders.Add("?");
                            args.Add(id);
                        }
                        wheres.Add($"tap.TagId IN ({string.Join(",", placeholders)})");
                    }
                    packageTagJoin = PACKAGE_TAG_JOIN_CLAUSE;
                }
            }
            bool anyInlineFT = parsedFileTags.Count > 0 || withAllFTTags.Count > 0 || withAnyFTTags.Count > 0 || withNoneFTTags.Count > 0;
            if (!anyInlineFT)
            {
                if (opt.SelectedFileTag == 1)
                {
                    wheres.Add("not exists (select taf.Id from TagAssignment as taf where AssetFile.Id = taf.TargetId and taf.TagTarget = 1)");
                }
                else if (opt.SelectedFileTag > 1 && opt.TagNames.Length > opt.SelectedFileTag)
                {
                    string tagName = opt.TagNames[opt.SelectedFileTag];
                    Tag selectedTag = opt.Tags.FirstOrDefault(t => t.Name == tagName);
                    int? tagId = selectedTag?.Id;
                    if (tagId.HasValue)
                    {
                        HashSet<int> descendantIds = Tagging.GetDescendantTagIds(tagId.Value);
                        List<string> placeholders = new List<string>();
                        foreach (int id in descendantIds)
                        {
                            placeholders.Add("?");
                            args.Add(id);
                        }
                        wheres.Add($"taf.TagId IN ({string.Join(",", placeholders)})");
                    }
                    fileTagJoin = FILE_TAG_JOIN_CLAUSE;
                }
            }

            // package types
            switch (opt.SelectedPackageTypes)
            {
                case 1:
                    wheres.Add("Asset.AssetSource != ?");
                    args.Add(Asset.Source.RegistryPackage);
                    break;
                case 2:
                    wheres.Add("Asset.AssetSource = ?");
                    args.Add(Asset.Source.AssetStorePackage);
                    break;
                case 3:
                    wheres.Add("Asset.AssetSource = ?");
                    args.Add(Asset.Source.RegistryPackage);
                    break;
                case 4:
                    wheres.Add("Asset.AssetSource = ?");
                    args.Add(Asset.Source.CustomPackage);
                    break;
                case 5:
                    wheres.Add("Asset.AssetSource = ?");
                    args.Add(Asset.Source.Directory);
                    break;
                case 6:
                    wheres.Add("Asset.AssetSource = ?");
                    args.Add(Asset.Source.Archive);
                    break;
                case 7:
                    wheres.Add("Asset.AssetSource = ?");
                    args.Add(Asset.Source.AssetManager);
                    break;
            }

            // publisher filter
            if (opt.SelectedPublisher > 0 && opt.PublisherNames.Length > opt.SelectedPublisher)
            {
                string[] arr = opt.PublisherNames[opt.SelectedPublisher].Split('/');
                string publisher = arr[arr.Length - 1];
                wheres.Add("Asset.SafePublisher = ?");
                args.Add($"{publisher}");
            }

            // asset filter
            if (opt.SelectedAsset > 0 && opt.AssetNames.Length > opt.SelectedAsset)
            {
                string[] arr = opt.AssetNames[opt.SelectedAsset].Split('/');
                string asset = arr[arr.Length - 1];
                if (asset.LastIndexOf('[') > 0)
                {
                    string assetId = asset.Substring(asset.LastIndexOf('[') + 1);
                    assetId = assetId.Substring(0, assetId.Length - 1);
                    if (AI.Config.searchSubPackages)
                    {
                        wheres.Add("(Asset.Id = ? or Asset.ParentId = ?)");
                        args.Add(int.Parse(assetId));
                        args.Add(int.Parse(assetId));
                    }
                    else
                    {
                        wheres.Add("Asset.Id = ?");
                        args.Add(int.Parse(assetId));
                    }
                }
                else
                {
                    wheres.Add("Asset.SafeName = ?");
                    args.Add($"{asset}");
                }
            }

            // direct asset ID filter (programmatic API)
            if (opt.SelectedAssetId > 0)
            {
                if (AI.Config.searchSubPackages)
                {
                    wheres.Add("(Asset.Id = ? or Asset.ParentId = ?)");
                    args.Add(opt.SelectedAssetId);
                    args.Add(opt.SelectedAssetId);
                }
                else
                {
                    wheres.Add("Asset.Id = ?");
                    args.Add(opt.SelectedAssetId);
                }
            }

            // category filter
            if (opt.SelectedCategory > 0 && opt.CategoryNames.Length > opt.SelectedCategory)
            {
                wheres.Add("Asset.DisplayCategory = ?");
                args.Add(opt.CategoryNames[opt.SelectedCategory]);
            }

            // color range
            if (opt.SelectedColorOption > 0)
            {
                wheres.Add("AssetFile.Hue >= ?");
                wheres.Add("AssetFile.Hue <= ?");
                args.Add(opt.SelectedColor.ToHue() - AI.Config.hueRange / 2f);
                args.Add(opt.SelectedColor.ToHue() + AI.Config.hueRange / 2f);
            }

            // image type
            if (IsFilterApplicable("ImageType", opt.RawSearchType) && opt.SelectedImageType > 0)
            {
                computedFields = ", CASE WHEN INSTR(AssetFile.FileName, '.') > 0 THEN Lower(SUBSTRING(AssetFile.FileName, 1, INSTR(AssetFile.FileName, '.') - 1)) ELSE Lower(AssetFile.FileName) END AS FileNameWithoutExtension";
                string[] patterns = TextureNameSuggester.suffixPatterns[opt.ImageTypeOptions[opt.SelectedImageType].ToLowerInvariant()];
                List<string> patternWheres = new List<string>();
                foreach (string pattern in patterns)
                {
                    if (string.IsNullOrWhiteSpace(pattern)) continue;
                    patternWheres.Add("FileNameWithoutExtension like ? ESCAPE '\\\'");
                    args.Add("%" + pattern.Replace("_", "\\_"));
                }
                wheres.Add("(" + string.Join(" or ", patternWheres) + ")");
            }

            // text search
            if (!string.IsNullOrWhiteSpace(opt.SearchPhrase))
            {
                List<string> searchFields = new List<string>();
                switch (AI.Config.searchField)
                {
                    case 0: searchFields.Add("AssetFile.Path"); break;
                    case 1: searchFields.Add("AssetFile.FileName"); break;
                }
                if (AI.Config.searchAICaptions && AI.Actions.CreateAICaptions) searchFields.Add("AssetFile.AICaption");
                if (AI.Config.searchPackageNames) searchFields.Add("Asset.DisplayName");

                // check for sqlite escaping requirements
                string escape = "";
                if (phrase.Contains("_"))
                {
                    if (!phrase.StartsWith("=")) phrase = phrase.Replace("_", "\\_");
                    escape = "ESCAPE '\\\'";
                }

                if (phrase.StartsWith("=")) // expert mode
                {
                    if (phrase.Length > 1)
                    {
                        phrase = StringUtils.EscapeSQL(phrase);
                        lastWhere = phrase.Substring(1);
                    }
                }
                else if (phrase.StartsWith("~")) // exact mode
                {
                    string term = phrase.Substring(1);
                    List<string> conditions = new List<string>();
                    searchFields.ForEach(s =>
                    {
                        conditions.Add(BuildLikeCondition(s, escape));
                        args.Add($"%{term}%");
                    });
                    wheres.Add("(" + string.Join(" OR ", conditions) + ")");
                }
                else
                {
                    string[] fuzzyWords = phrase
                        .Split(' ')
                        .Select(s => s.Trim())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToArray();
                    foreach (string fuzzyWord in fuzzyWords)
                    {
                        if (fuzzyWord.StartsWith("-"))
                        {
                            List<string> conditions = new List<string>();
                            searchFields.ForEach(s =>
                            {
                                conditions.Add(BuildNotLikeCondition(s, escape));
                                args.Add($"%{fuzzyWord.Substring(1)}%");
                            });
                            wheres.Add("(" + string.Join(" AND ", conditions) + ")");
                        }
                        else
                        {
                            string term = fuzzyWord;
                            if (term.StartsWith("+")) term = term.Substring(1);
                            List<string> conditions = new List<string>();
                            searchFields.ForEach(s =>
                            {
                                conditions.Add(BuildLikeCondition(s, escape));
                                args.Add($"%{term}%");
                            });
                            wheres.Add("(" + string.Join(" OR ", conditions) + ")");
                        }
                    }
                }
            }

            // type filtering based on raw type
            string rawType = opt.RawSearchType;
            if (rawType != null)
            {
                string[] type = rawType.Split('/');
                if (type.Length > 1)
                {
                    wheres.Add("AssetFile.Type = ?");
                    args.Add(type.Last());
                }
                else if (Enum.TryParse(rawType, out AI.AssetGroup assetGroup))
                {
                    // Special handling for Animations group: include both .anim files and .fbx files with animations
                    if (assetGroup == AI.AssetGroup.Animations)
                    {
                        wheres.Add("(AssetFile.Type = ? OR (AssetFile.Type = ? AND AssetFile.Length > 0))");
                        args.Add("anim");
                        args.Add("fbx");
                    }
                    else if (AI.TypeGroups.TryGetValue(assetGroup, out string[] group))
                    {
                        // optimize SQL slightly for cases where only one type is checked
                        if (group.Length == 1)
                        {
                            wheres.Add("AssetFile.Type = ?");
                            args.Add(group[0]);
                        }
                        else
                        {
                            // sqlite does not support binding lists, parameters must be spelled out
                            List<string> paramCount = new List<string>();
                            foreach (string t in group)
                            {
                                paramCount.Add("?");
                                args.Add(t);
                            }
                            wheres.Add("AssetFile.Type in (" + string.Join(",", paramCount) + ")");
                        }
                    }
                }
            }

            // excluded extensions
            string[] excludedExtensions = GetConfiguredExcludedExtensions(opt.IgnoreExcludedExtensions);
            if (excludedExtensions.Length > 0)
            {
                List<string> paramCount = new List<string>();
                foreach (string ext in excludedExtensions)
                {
                    paramCount.Add("?");
                    args.Add(ext);
                }
                wheres.Add("AssetFile.Type not in (" + string.Join(",", paramCount) + ")");
            }

            // preview filter (has preview / no preview)
            switch (opt.SelectedPreviewFilter)
            {
                case 2: // has preview
                    // skip "has preview" filter for types that never generate previews
                    bool isNonPreviewableType = rawType != null
                        && Enum.TryParse(rawType, out AI.AssetGroup previewGroup)
                        && ((previewGroup is AI.AssetGroup.Scripts or AI.AssetGroup.Libraries or AI.AssetGroup.Documents or AI.AssetGroup.Shaders)
                            || (previewGroup is AI.AssetGroup.Scenes && !AI.Config.generateScenePreviews));
                    if (!isNonPreviewableType)
                    {
                        wheres.Add("AssetFile.PreviewState in (1, 2, 3)"); // Provided, Redo, Custom
                    }
                    break;
                case 3: // no preview
                    wheres.Add("AssetFile.PreviewState not in (1, 2, 3)"); // not Provided, Redo, Custom
                    break;
            }

            // ordering, can only be done on DB side since post-processing results would only work on the paged results which is incorrect
            string orderBy = "order by ";
            switch (AI.Config.sortField)
            {
                case 2: orderBy += "AssetFile.Path"; break;
                case 3: orderBy += "AssetFile.FileName"; break;
                case 4: orderBy += "AssetFile.Size"; break;
                case 5: orderBy += "AssetFile.Type"; break;
                case 6: orderBy += "AssetFile.Length"; break;
                case 7: orderBy += "AssetFile.Width"; break;
                case 8: orderBy += "AssetFile.Height"; break;
                case 9:
                    orderBy += "AssetFile.Hue";
                    wheres.Add("AssetFile.Hue >=0");
                    break;
                case 10: orderBy += "Asset.DisplayCategory"; break;
                case 11: orderBy += "Asset.LastRelease"; break;
                case 12: orderBy += "Asset.AssetRating"; break;
                case 13: orderBy += "Asset.RatingCount"; break;
                default: orderBy = ""; break;
            }
            if (!string.IsNullOrEmpty(orderBy))
            {
                if (SortUsesTextCollation(AI.Config.sortField)) orderBy += " COLLATE NOCASE";
                if (AI.Config.sortDescending) orderBy += " desc";
                orderBy += ", AssetFile.Path"; // always sort by path in case of equality of first level sorting
            }
            if (!string.IsNullOrEmpty(lastWhere)) wheres.Add(lastWhere);

            string where = wheres.Count > 0 ? "where " + string.Join(" and ", wheres) : "";
            string assetFileSource = GetAssetFileSource(opt);
            string baseQuery = $"from {assetFileSource} inner join Asset on Asset.Id = AssetFile.AssetId {packageTagJoin} {fileTagJoin} {where}";
            string countQuery = $"select count(*){computedFields} {baseQuery}";
            string dataQuery = $"select *, AssetFile.Id as Id{computedFields} {baseQuery} {orderBy}";
            string sqlitePathBaseQuery = CanUseSQLitePathIndexDataQuery(opt)
                ? $"from {SQLITE_PATH_INDEX_SOURCE} inner join Asset on Asset.Id = AssetFile.AssetId {packageTagJoin} {fileTagJoin} {where}"
                : null;
            string fastCountQuery = CanUseSQLitePathIndexCountQuery(opt) ? $"select count(*){computedFields} {sqlitePathBaseQuery}" : null;
            string fastDataQuery = sqlitePathBaseQuery != null ? $"select *, AssetFile.Id as Id{computedFields} {sqlitePathBaseQuery} {orderBy}" : null;
            string unorderedDataQuery = sqlitePathBaseQuery != null ? $"select *, AssetFile.Id as Id{computedFields} {baseQuery}" : null;

            return new QueryResult
            {
                BaseQuery = baseQuery,
                CountQuery = countQuery,
                DataQuery = dataQuery,
                FastCountQuery = fastCountQuery,
                FastDataQuery = fastDataQuery,
                UnorderedDataQuery = unorderedDataQuery,
                Args = args
            };
        }

        public static bool IsFilterApplicable(string filterName, string rawSearchType)
        {
            string searchType = rawSearchType;
            if (searchType == null) return true;
            if (AI.FilterRestriction.TryGetValue(filterName, out string[] restrictions))
            {
                return restrictions.Contains(searchType);
            }
            return true;
        }

        internal static string[] GetConfiguredExcludedExtensions(bool ignoreExcludedExtensions)
        {
            if (ignoreExcludedExtensions
                || !AI.Config.excludeExtensions
                || AI.Config.searchType != 0
                || string.IsNullOrWhiteSpace(AI.Config.excludedExtensions))
            {
                return Array.Empty<string>();
            }

            return AI.Config.excludedExtensions
                .Replace("\n", "")
                .Split(';')
                .Select(ext => ext.Trim())
                .Where(ext => !string.IsNullOrEmpty(ext))
                .ToArray();
        }
    }
}
