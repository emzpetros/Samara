using System;
using System.Collections.Generic;
using System.Linq;

namespace AssetInventory
{
    public static class HierarchyBuilder
    {
        public const int PATH = 0;
        public const int CATEGORY = 1;
        public const int PUBLISHER = 2;
        public const int PACKAGE = 3;
        public const int TYPE = 4;

        private const int MAX_HIERARCHY_DEPTH = 100;

        public static List<HierarchyTreeElement> Build(List<AssetInfo> files, int hierarchyType)
        {
            List<HierarchyTreeElement> elements = new List<HierarchyTreeElement>();

            HierarchyTreeElement root = new HierarchyTreeElement("Root", -1, 0);
            elements.Add(root);

            if (files == null || files.Count == 0)
            {
                return elements;
            }

            int idCounter = 1;

            switch (hierarchyType)
            {
                case PATH:
                    BuildPathHierarchy(files, elements, ref idCounter);
                    break;
                case CATEGORY:
                    BuildCategoryHierarchy(files, elements, ref idCounter);
                    break;
                case PUBLISHER:
                    BuildPublisherHierarchy(files, elements, ref idCounter);
                    break;
                case PACKAGE:
                    BuildPackageHierarchy(files, elements, ref idCounter);
                    break;
                case TYPE:
                    BuildTypeHierarchy(files, elements, ref idCounter);
                    break;
            }

            return elements;
        }

        public static void BuildPathHierarchy(List<AssetInfo> files, List<HierarchyTreeElement> elements, ref int idCounter)
        {
            Dictionary<string, int> pathCounts = new Dictionary<string, int>();
            Dictionary<string, string> pathNames = new Dictionary<string, string>();
            HashSet<string> allPaths = new HashSet<string>();

            foreach (AssetInfo file in files)
            {
                if (string.IsNullOrEmpty(file.Path)) continue;

                string[] parts = file.Path.Split('/');
                string currentPath = "";

                for (int i = 0; i < parts.Length - 1; i++)
                {
                    string part = parts[i];
                    if (string.IsNullOrEmpty(part)) continue;

                    currentPath = string.IsNullOrEmpty(currentPath) ? part : currentPath + "/" + part;

                    if (!pathCounts.ContainsKey(currentPath))
                    {
                        pathCounts[currentPath] = 0;
                        pathNames[currentPath] = part;
                    }
                    allPaths.Add(currentPath);
                    pathCounts[currentPath]++;
                }
            }

            AddHierarchyElementsDepthFirst(elements, allPaths, pathNames, pathCounts, "Path", ref idCounter);
        }

        public static void BuildCategoryHierarchy(List<AssetInfo> files, List<HierarchyTreeElement> elements, ref int idCounter)
        {
            Dictionary<string, int> categoryCounts = new Dictionary<string, int>();
            Dictionary<string, string> categoryNames = new Dictionary<string, string>();
            HashSet<string> allCategories = new HashSet<string>();

            foreach (AssetInfo file in files)
            {
                string category = file.DisplayCategory ?? "Uncategorized";

                if (!categoryCounts.ContainsKey(category))
                {
                    categoryCounts[category] = 0;
                }
                categoryCounts[category]++;

                string[] parts = category.Split('/');
                string currentPath = "";
                for (int i = 0; i < parts.Length; i++)
                {
                    currentPath = string.IsNullOrEmpty(currentPath) ? parts[i] : currentPath + "/" + parts[i];
                    if (!categoryNames.ContainsKey(currentPath))
                    {
                        categoryNames[currentPath] = parts[i];
                    }
                    allCategories.Add(currentPath);
                }
            }

            AddHierarchyElementsDepthFirst(elements, allCategories, categoryNames, categoryCounts, "Category", ref idCounter);
        }

        public static void BuildPublisherHierarchy(List<AssetInfo> files, List<HierarchyTreeElement> elements, ref int idCounter)
        {
            Dictionary<string, int> publisherCounts = new Dictionary<string, int>();

            foreach (AssetInfo file in files)
            {
                string publisher = file.DisplayPublisher ?? "Unknown";
                if (!publisherCounts.ContainsKey(publisher))
                {
                    publisherCounts[publisher] = 0;
                }
                publisherCounts[publisher]++;
            }

            foreach (KeyValuePair<string, int> kvp in publisherCounts.OrderByDescending(x => x.Value))
            {
                elements.Add(new HierarchyTreeElement(kvp.Key, 0, idCounter++, "Publisher", kvp.Key, kvp.Key, kvp.Value));
            }
        }

        public static void BuildPackageHierarchy(List<AssetInfo> files, List<HierarchyTreeElement> elements, ref int idCounter)
        {
            Dictionary<string, int> packageCounts = new Dictionary<string, int>();

            foreach (AssetInfo file in files)
            {
                string package = file.DisplayName ?? file.SafeName ?? "Unknown";
                if (!packageCounts.ContainsKey(package))
                {
                    packageCounts[package] = 0;
                }
                packageCounts[package]++;
            }

            foreach (KeyValuePair<string, int> kvp in packageCounts.OrderByDescending(x => x.Value))
            {
                elements.Add(new HierarchyTreeElement(kvp.Key, 0, idCounter++, "Package", kvp.Key, kvp.Key, kvp.Value));
            }
        }

        public static void BuildTypeHierarchy(List<AssetInfo> files, List<HierarchyTreeElement> elements, ref int idCounter)
        {
            Dictionary<string, int> typeCounts = new Dictionary<string, int>();

            foreach (AssetInfo file in files)
            {
                string type = file.Type ?? "Unknown";
                if (!typeCounts.ContainsKey(type))
                {
                    typeCounts[type] = 0;
                }
                typeCounts[type]++;
            }

            foreach (KeyValuePair<string, int> kvp in typeCounts.OrderByDescending(x => x.Value))
            {
                elements.Add(new HierarchyTreeElement(kvp.Key, 0, idCounter++, "Type", kvp.Key, kvp.Key, kvp.Value));
            }
        }

        public static void AddHierarchyElementsDepthFirst(List<HierarchyTreeElement> elements, HashSet<string> allPaths,
            Dictionary<string, string> pathNames, Dictionary<string, int> pathCounts, string filterKey, ref int idCounter)
        {
            Dictionary<string, List<string>> childrenMap = new Dictionary<string, List<string>>();
            childrenMap[""] = new List<string>();

            foreach (string path in allPaths)
            {
                int lastSlash = path.LastIndexOf('/');
                string parent = lastSlash > 0 ? path.Substring(0, lastSlash) : "";

                if (!childrenMap.ContainsKey(parent))
                {
                    childrenMap[parent] = new List<string>();
                }
                childrenMap[parent].Add(path);
            }

            foreach (List<string> children in childrenMap.Values)
            {
                children.Sort(StringComparer.OrdinalIgnoreCase);
            }

            TraverseHierarchy(elements, "", 0, childrenMap, pathNames, pathCounts, filterKey, ref idCounter);
        }

        public static void TraverseHierarchy(List<HierarchyTreeElement> elements, string parentPath, int depth,
            Dictionary<string, List<string>> childrenMap, Dictionary<string, string> pathNames,
            Dictionary<string, int> pathCounts, string filterKey, ref int idCounter)
        {
            if (depth > MAX_HIERARCHY_DEPTH) return;
            if (!childrenMap.ContainsKey(parentPath)) return;

            foreach (string path in childrenMap[parentPath])
            {
                string name = pathNames.ContainsKey(path) ? pathNames[path] : path;
                int count = pathCounts.ContainsKey(path) ? pathCounts[path] : 0;

                if (filterKey == "Category")
                {
                    count = pathCounts.Where(kvp => kvp.Key == path || kvp.Key.StartsWith(path + "/")).Sum(kvp => kvp.Value);
                }

                elements.Add(new HierarchyTreeElement(name, depth, idCounter++, filterKey, path, path, count));

                TraverseHierarchy(elements, path, depth + 1, childrenMap, pathNames, pathCounts, filterKey, ref idCounter);
            }
        }
    }
}
