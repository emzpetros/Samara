using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Random = UnityEngine.Random;

namespace AssetInventory
{
    public struct SpecialTagDefinition
    {
        public string Name;
        public string Description;
        public Color Color;
        public TagAssignment.Target Target;

        public SpecialTagDefinition(string name, string description, Color color, TagAssignment.Target target)
        {
            Name = name;
            Description = description;
            Color = color;
            Target = target;
        }
    }

    public static class Tagging
    {
        public static readonly SpecialTagDefinition[] SpecialTags =
        {
            new SpecialTagDefinition("noindex", "Prevents this package from being indexed", new Color(0.9f, 0.65f, 0.2f), TagAssignment.Target.Package)
        };

        public static event Action OnTagsChanged;

        internal static IEnumerable<TagInfo> Tags
        {
            get
            {
                if (_tags == null) LoadAssignments();
                return _tags;
            }
        }
        private static List<TagInfo> _tags;

        private static Dictionary<int, List<TagInfo>> _packageTagMap;

        internal static int TagHash { get; private set; }

        public static bool AddAssignment(int targetId, string tag, TagAssignment.Target target, bool fromAssetStore = false)
        {
            Tag existingT = AddTagWithSlashHandling(tag, fromAssetStore);
            if (existingT == null) return false;

            TagAssignment existingA = DBAdapter.DB.Find<TagAssignment>(t => t.TagId == existingT.Id && t.TargetId == targetId && t.TagTarget == target);
            if (existingA != null) return false; // already added

            TagAssignment newAssignment = new TagAssignment(existingT.Id, target, targetId);
            DBAdapter.DB.Insert(newAssignment);

            return true;
        }

        public static bool AddAssignment(AssetInfo info, string tag, TagAssignment.Target target, bool byUser = false)
        {
            if (!AddAssignment(target == TagAssignment.Target.Asset ? info.Id : info.AssetId, tag, target)) return false;

            LoadAssignments(info);
            if (byUser && target == TagAssignment.Target.Asset && info.AssetSource == Asset.Source.AssetManager) AddRemoteTag(info, tag);

            return true;
        }

        public static void AddAssignments(List<AssetInfo> infos, string tag, TagAssignment.Target target, bool byUser = false)
        {
            if (infos.Count == 1 || infos.Any(info => info.AssetSource == Asset.Source.AssetManager))
            {
                // if at least one asset is from AM, we need to sync the tag changes
                infos.ForEach(info => AddAssignment(info, tag, target, byUser));
                return;
            }

            // optimized for bulk assignment without AM sync
            infos.ForEach(info =>
            {
                TagInfo tagInfo = (target == TagAssignment.Target.Asset ? info.AssetTags : info.PackageTags)?.Find(t => t.Name == tag);
                if (tagInfo != null) return;

                if (!AddAssignment(target == TagAssignment.Target.Asset ? info.Id : info.AssetId, tag, target)) return;
                info.SetTagsDirty();
            });
            LoadAssignments();
        }

        public static void RemoveAssignment(AssetInfo info, TagInfo tagInfo, bool autoReload = true, bool byUser = false)
        {
            DBAdapter.DB.Delete<TagAssignment>(tagInfo.Id);

            if (autoReload) LoadAssignments(info);
            if (byUser && tagInfo.TagTarget == TagAssignment.Target.Asset && info.AssetSource == Asset.Source.AssetManager) RemoveRemoteTag(info, tagInfo.Name);
        }

        public static void RemoveAssetAssignments(List<AssetInfo> infos, string name, bool byUser)
        {
            if (infos == null) return;
            infos.ForEach(info =>
            {
                TagInfo tagInfo = info.AssetTags?.Find(t => t.Name == name);
                if (tagInfo == null) return;
                RemoveAssignment(info, tagInfo, false, byUser);
                info.AssetTags.RemoveAll(t => t.Name == name);
                info.SetTagsDirty();
            });
            LoadAssignments();
        }

        public static void RemovePackageAssignments(List<AssetInfo> infos, string name, bool byUser)
        {
            if (infos == null) return;
            infos.ForEach(info =>
            {
                TagInfo tagInfo = info.PackageTags?.Find(t => t.Name == name);
                if (tagInfo == null) return;
                RemoveAssignment(info, tagInfo, false, byUser);
                info.PackageTags.RemoveAll(t => t.Name == name);
                info.SetTagsDirty();
            });
            LoadAssignments();
        }

        private static async void AddRemoteTag(AssetInfo info, string tagName)
        {
#if USE_ASSET_MANAGER && USE_CLOUD_IDENTITY
            // sync online with AM
            CloudAssetManagement cam = await AI.GetCloudAssetManagement();
            await cam.AddTags(info.ToAsset(), info, new List<string> {tagName});
#else
            Debug.LogWarning("Tag changes will not be synced back to Unity Cloud since this project does not have the Cloud Asset dependencies installed (see Settings).");
            await Task.Yield();
#endif
        }

        private static async void RemoveRemoteTag(AssetInfo info, string tagName)
        {
#if USE_ASSET_MANAGER && USE_CLOUD_IDENTITY
            // sync online with AM
            CloudAssetManagement cam = await AI.GetCloudAssetManagement();
            await cam.RemoveTags(info.ToAsset(), info, new List<string> {tagName});
#else
            Debug.LogWarning("Tag changes will not be synced back to Unity Cloud since this project does not have the Cloud Asset dependencies installed (see Settings).");
            await Task.Yield();
#endif
        }

        internal static void LoadAssignments(AssetInfo info = null, bool triggerEvents = true)
        {
            string dataQuery = "SELECT *, TagAssignment.Id as Id from TagAssignment inner join Tag on Tag.Id = TagAssignment.TagId order by TagTarget, TargetId";
            _tags = DBAdapter.DB.Query<TagInfo>($"{dataQuery}").ToList();
            TagHash = Random.Range(0, int.MaxValue);

            // Build fast lookup dictionary for package tags
            _packageTagMap = _tags
                .Where(t => t.TagTarget == TagAssignment.Target.Package)
                .GroupBy(t => t.TargetId)
                .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Name).ToList());

            info?.SetTagsDirty();
            if (triggerEvents) OnTagsChanged?.Invoke();
        }

        public static List<TagInfo> GetAssetTags(int assetFileId)
        {
            return Tags?.Where(t => t.TagTarget == TagAssignment.Target.Asset && t.TargetId == assetFileId)
                .OrderBy(t => t.Name).ToList();
        }

        public static List<TagInfo> GetPackageTags(int assetId)
        {
            if (_packageTagMap == null) LoadAssignments();
            return _packageTagMap.TryGetValue(assetId, out List<TagInfo> tags) ? tags : new List<TagInfo>();
        }

        public static void SaveTag(Tag tag)
        {
            DBAdapter.DB.Update(tag);
            LoadAssignments();
        }

        public static Tag AddTag(string name, bool fromAssetStore = false)
        {
            name = name.Trim();
            if (string.IsNullOrWhiteSpace(name)) return null;

            Tag tag = DBAdapter.DB.Find<Tag>(t => t.Name.ToLower() == name.ToLower());
            if (tag == null)
            {
                tag = new Tag(name);
                tag.FromAssetStore = fromAssetStore;
                DBAdapter.DB.Insert(tag);

                OnTagsChanged?.Invoke();
            }
            else if (!tag.FromAssetStore && fromAssetStore)
            {
                tag.FromAssetStore = true;
                DBAdapter.DB.Update(tag); // don't trigger changed event in such cases, this is just for bookkeeping
            }

            return tag;
        }

        public static void RenameTag(Tag tag, string newName)
        {
            newName = newName.Trim();
            if (string.IsNullOrWhiteSpace(newName)) return;

            tag.Name = newName;
            DBAdapter.DB.Update(tag);
            LoadAssignments();
        }

        public static void DeleteTag(Tag tag)
        {
            DBAdapter.DB.Execute("DELETE from TagAssignment where TagId=?", tag.Id);
            DBAdapter.DB.Delete<Tag>(tag.Id);
            LoadAssignments();
        }

        public static void DeleteTagWithDescendants(Tag tag)
        {
            HashSet<int> descendantIds = GetDescendantTagIds(tag.Id);
            foreach (int tagId in descendantIds)
            {
                DBAdapter.DB.Execute("DELETE from TagAssignment where TagId=?", tagId);
                DBAdapter.DB.Delete<Tag>(tagId);
            }
            LoadAssignments();
        }

        public static List<Tag> GetDescendantTags(int tagId)
        {
            List<Tag> allTags = LoadTags();
            List<Tag> descendants = new List<Tag>();
            AddDescendantTagsRecursive(descendants, allTags, tagId);
            return descendants;
        }

        private static void AddDescendantTagsRecursive(List<Tag> result, List<Tag> allTags, int parentId)
        {
            foreach (Tag child in allTags.Where(t => t.ParentId == parentId))
            {
                result.Add(child);
                AddDescendantTagsRecursive(result, allTags, child.Id);
            }
        }

        public static List<Tag> LoadTags()
        {
            return DBAdapter.DB.Table<Tag>().AsEnumerable().OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static HashSet<int> GetDescendantTagIds(int tagId)
        {
            HashSet<int> result = new HashSet<int> { tagId };
            List<Tag> allTags = LoadTags();
            AddDescendantsRecursive(result, allTags, tagId);
            return result;
        }

        private static void AddDescendantsRecursive(HashSet<int> result, List<Tag> allTags, int parentId)
        {
            foreach (Tag child in allTags.Where(t => t.ParentId == parentId))
            {
                result.Add(child.Id);
                AddDescendantsRecursive(result, allTags, child.Id);
            }
        }

        public static List<TagTreeElement> BuildTagTree()
        {
            List<Tag> tags = LoadTags();
            List<TagTreeElement> elements = new List<TagTreeElement>();

            // Root element (required by TreeModel, depth = -1)
            TagTreeElement root = new TagTreeElement(null, -1, 0);
            elements.Add(root);

            // Build hierarchy using depth-first traversal starting from root tags (ParentId == null)
            AddTagChildrenRecursive(elements, tags, null, 0);

            return elements;
        }

        private static void AddTagChildrenRecursive(List<TagTreeElement> elements, List<Tag> allTags, int? parentId, int depth)
        {
            IEnumerable<Tag> children = allTags.Where(t => t.ParentId == parentId).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase);
            foreach (Tag tag in children)
            {
                elements.Add(new TagTreeElement(tag, depth, tag.Id));
                AddTagChildrenRecursive(elements, allTags, tag.Id, depth + 1);
            }
        }

        /// <summary>
        /// Adds a tag with slash handling based on the global setting.
        /// When CreateHierarchy: splits on "/" and creates parent chain with proper ParentId relationships.
        /// When TakeAsIs: creates tag with "/" in the name literally.
        /// </summary>
        public static Tag AddTagWithSlashHandling(string name, bool fromAssetStore = false)
        {
            name = name.Trim();
            if (string.IsNullOrWhiteSpace(name)) return null;

            if (AI.Config.tagSlashHandling == TagSlashHandling.TakeAsIs || !name.Contains("/"))
            {
                return AddTag(name, fromAssetStore);
            }

            // CreateHierarchy mode: split and create parent chain
            string[] segments = name.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return null;

            Tag parentTag = null;
            Tag lastTag = null;

            foreach (string segment in segments)
            {
                string trimmedSegment = segment.Trim();
                if (string.IsNullOrWhiteSpace(trimmedSegment)) continue;

                // Find or create tag with this name at the current parent level
                Tag existingTag = DBAdapter.DB.Find<Tag>(t => t.Name.ToLower() == trimmedSegment.ToLower());

                if (existingTag == null)
                {
                    // Create new tag with parent reference
                    existingTag = new Tag(trimmedSegment);
                    existingTag.FromAssetStore = fromAssetStore;
                    existingTag.ParentId = parentTag?.Id;
                    DBAdapter.DB.Insert(existingTag);
                    OnTagsChanged?.Invoke();
                }
                else
                {
                    // Tag exists - update parent if it doesn't have one yet
                    if (existingTag.ParentId == null && parentTag != null)
                    {
                        existingTag.ParentId = parentTag.Id;
                        DBAdapter.DB.Update(existingTag);
                    }
                    if (!existingTag.FromAssetStore && fromAssetStore)
                    {
                        existingTag.FromAssetStore = true;
                        DBAdapter.DB.Update(existingTag);
                    }
                }

                parentTag = existingTag;
                lastTag = existingTag;
            }

            return lastTag; // Return the leaf tag for assignment
        }

        /// <summary>
        /// Returns all tags that contain "/" in their name.
        /// </summary>
        public static List<Tag> GetTagsWithSlash()
        {
            return DBAdapter.DB.Table<Tag>().Where(t => t.Name.Contains("/")).ToList();
        }

        /// <summary>
        /// Checks if converting a slash tag to hierarchy would cause reparenting conflicts.
        /// Returns a list of tags that would be reparented (already exist with a different parent).
        /// </summary>
        public static List<(string segment, Tag existingTag, int? currentParentId, int? newParentId)> CheckConversionConflicts(Tag tag)
        {
            List<(string segment, Tag existingTag, int? currentParentId, int? newParentId)> conflicts = new List<(string segment, Tag existingTag, int? currentParentId, int? newParentId)>();

            if (!tag.Name.Contains("/")) return conflicts;

            string[] segments = tag.Name.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length <= 1) return conflicts;

            int? expectedParentId = null;

            foreach (string segment in segments)
            {
                string trimmedSegment = segment.Trim();
                if (string.IsNullOrWhiteSpace(trimmedSegment)) continue;

                Tag existingTag = DBAdapter.DB.Find<Tag>(t => t.Name.ToLower() == trimmedSegment.ToLower() && t.Id != tag.Id);

                if (existingTag != null && existingTag.ParentId != expectedParentId)
                {
                    // This tag exists but would need to be reparented
                    conflicts.Add((trimmedSegment, existingTag, existingTag.ParentId, expectedParentId));
                }

                expectedParentId = existingTag?.Id;
            }

            return conflicts;
        }

        /// <summary>
        /// Converts a tag containing "/" into a proper hierarchy.
        /// Splits on "/", creates/reuses tags with proper ParentId chain,
        /// reassigns all TagAssignment records to the leaf tag, and deletes the original "/" tag.
        /// </summary>
        /// <param name="tag">The tag containing "/" to convert</param>
        /// <param name="forceReparent">If true, will reparent existing tags even if they have a different parent</param>
        /// <returns>True if conversion succeeded, false otherwise</returns>
        public static bool ConvertSlashTagToHierarchy(Tag tag, bool forceReparent = false)
        {
            if (!tag.Name.Contains("/")) return false;

            string[] segments = tag.Name.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length <= 1) return false;

            // Check for conflicts if not forcing reparent
            if (!forceReparent)
            {
                List<(string segment, Tag existingTag, int? currentParentId, int? newParentId)> conflicts = CheckConversionConflicts(tag);
                if (conflicts.Count > 0) return false;
            }

            Tag parentTag = null;
            Tag leafTag = null;

            foreach (string segment in segments)
            {
                string trimmedSegment = segment.Trim();
                if (string.IsNullOrWhiteSpace(trimmedSegment)) continue;

                // Find existing tag with this name (excluding the original slash tag)
                Tag existingTag = DBAdapter.DB.Find<Tag>(t => t.Name.ToLower() == trimmedSegment.ToLower() && t.Id != tag.Id);

                if (existingTag == null)
                {
                    // Create new tag with parent reference
                    existingTag = new Tag(trimmedSegment);
                    existingTag.FromAssetStore = tag.FromAssetStore;
                    existingTag.ParentId = parentTag?.Id;
                    DBAdapter.DB.Insert(existingTag);
                }
                else if (forceReparent || existingTag.ParentId == null)
                {
                    // Update parent if forcing or if tag doesn't have a parent yet
                    existingTag.ParentId = parentTag?.Id;
                    DBAdapter.DB.Update(existingTag);
                }

                parentTag = existingTag;
                leafTag = existingTag;
            }

            if (leafTag == null) return false;

            // Reassign all TagAssignment records from old tag to leaf tag
            DBAdapter.DB.Execute("UPDATE TagAssignment SET TagId = ? WHERE TagId = ?", leafTag.Id, tag.Id);

            // Delete the original "/" tag
            DBAdapter.DB.Delete<Tag>(tag.Id);

            LoadAssignments();
            return true;
        }

        /// <summary>
        /// Converts all tags containing "/" to hierarchy structure.
        /// Auto-converts those without conflicts and returns list of skipped tags.
        /// </summary>
        /// <returns>Tuple of (converted count, list of skipped tags with their conflict reasons)</returns>
        public static (int convertedCount, List<(Tag tag, List<(string segment, Tag existingTag, int? currentParentId, int? newParentId)> conflicts)> skipped) ConvertAllSlashTagsToHierarchy()
        {
            List<Tag> slashTags = GetTagsWithSlash();
            int convertedCount = 0;
            List<(Tag tag, List<(string segment, Tag existingTag, int? currentParentId, int? newParentId)> conflicts)> skipped = new List<(Tag tag, List<(string segment, Tag existingTag, int? currentParentId, int? newParentId)> conflicts)>();

            foreach (Tag tag in slashTags)
            {
                List<(string segment, Tag existingTag, int? currentParentId, int? newParentId)> conflicts = CheckConversionConflicts(tag);
                if (conflicts.Count > 0)
                {
                    skipped.Add((tag, conflicts));
                    continue;
                }

                if (ConvertSlashTagToHierarchy(tag, false))
                {
                    convertedCount++;
                }
            }

            if (convertedCount > 0)
            {
                LoadAssignments();
            }

            return (convertedCount, skipped);
        }
    }
}
