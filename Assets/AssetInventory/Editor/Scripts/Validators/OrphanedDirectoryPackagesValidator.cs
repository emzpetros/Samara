using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImpossibleRobert.Common;

namespace AssetInventory
{
    public sealed class OrphanedDirectoryPackagesValidator : Validator
    {
        private sealed class GroupingOption
        {
            public string RootPath { get; set; }
            public int PackageMode { get; set; }
            public List<string> CoveredDirectories { get; set; } = new List<string>();
            public int DirectoryCount => CoveredDirectories.Count;
        }

        public OrphanedDirectoryPackagesValidator()
        {
            Type = ValidatorType.DB;
            Speed = ValidatorSpeed.Fast;
            Name = "Orphaned Directory Packages";
            Description = "Scans for directory-type packages that are not covered by any additional folder setting.";
            FixCaption = "Add Folders";
        }

        public override async Task Validate()
        {
            CurrentState = State.Scanning;

            await Task.Yield();

            // Get all directory-type assets
            List<AssetInfo> directoryAssets = Assets.Load()
                .Where(a => a.AssetSource == Asset.Source.Directory)
                .ToList();

            DBIssues.Clear();

            // Check each directory asset to see if it's covered by any folder spec
            foreach (AssetInfo asset in directoryAssets)
            {
                if (CancellationRequested) break;

                string assetLocation = asset.GetLocation(true);
                if (string.IsNullOrEmpty(assetLocation)) continue;

                // Check if this directory is covered by any folder spec
                bool isCovered = IsDirectoryCovered(assetLocation);

                if (!isCovered)
                {
                    DBIssues.Add(asset);
                }
            }

            CurrentState = State.Completed;
        }

        public override async Task Fix()
        {
            CurrentState = State.Fixing;

            // Collect all orphaned directory paths
            List<string> orphanedPaths = new List<string>();
            foreach (AssetInfo issue in DBIssues)
            {
                if (CancellationRequested) break;

                string directoryLocation = issue.GetLocation(true);
                if (string.IsNullOrEmpty(directoryLocation)) continue;

                // Check if directory still exists
                if (!Directory.Exists(directoryLocation)) continue;

                orphanedPaths.Add(directoryLocation);
            }

            if (orphanedPaths.Count == 0)
            {
                CurrentState = State.Completed;
                return;
            }

            // Analyze grouping opportunities
            List<GroupingOption> groupingOptions = AnalyzeGroupingOpportunities(orphanedPaths);

            // Select optimal grouping strategy
            List<GroupingOption> selectedGroups = SelectOptimalGrouping(groupingOptions, orphanedPaths);

            // Create folder specs for each selected grouping
            foreach (GroupingOption group in selectedGroups)
            {
                if (CancellationRequested) break;

                // Create a new folder spec
                FolderSpec spec = new FolderSpec();
                spec.folderType = 1; // media folder type
                spec.enabled = true;
                spec.attachToPackage = true;
                spec.scanFor = 1; // scan for all files
                spec.createPreviews = true;
                spec.removeOrphans = true;
                spec.detectUnityProjects = true;
                spec.packageMode = group.PackageMode; // Set the package mode

                // Try to make the location relative if possible
                string location = Paths.MakeRelative(group.RootPath);
                spec.location = location;

                // Set relative path handling if applicable
                if (Paths.IsRel(location))
                {
                    spec.storeRelative = true;
                    spec.relativeKey = Paths.GetRelKey(location);
                }

                // Add to config
                AI.Config.folders.Add(spec);

                await Task.Yield();
            }

            // Save configuration
            AI.SaveConfig();

            // Re-validate to update the issue list
            await Validate();
        }

        private List<GroupingOption> AnalyzeGroupingOpportunities(List<string> orphanedPaths)
        {
            List<GroupingOption> opportunities = new List<GroupingOption>();

            // Normalize all paths first
            List<string> normalizedPaths = orphanedPaths.Select(p => IOUtils.NormalizePath(p)).Where(p => !string.IsNullOrEmpty(p)).ToList();

            // Strategy 1: Try to group by parent (package mode 1 - First Level)
            Dictionary<string, List<string>> byParent = new Dictionary<string, List<string>>(System.StringComparer.OrdinalIgnoreCase);
            foreach (string path in normalizedPaths)
            {
                string parent = IOUtils.GetParentPath(path);
                if (string.IsNullOrEmpty(parent)) continue;

                if (!byParent.ContainsKey(parent))
                {
                    byParent[parent] = new List<string>();
                }
                byParent[parent].Add(path);
            }

            // Check each parent - only create mode 1 grouping if ALL children are orphaned (strict requirement)
            foreach (KeyValuePair<string, List<string>> kvp in byParent)
            {
                string parentPath = kvp.Key;
                List<string> orphanedChildren = kvp.Value;

                // Get all actual child directories from filesystem
                List<string> allChildren = GetAllChildDirectories(parentPath);

                // Only create a mode 1 grouping if ALL children are orphaned
                if (allChildren.Count > 0 && orphanedChildren.Count == allChildren.Count)
                {
                    opportunities.Add(new GroupingOption
                    {
                        RootPath = parentPath,
                        PackageMode = 1, // First Level
                        CoveredDirectories = new List<string>(orphanedChildren)
                    });
                }
            }

            // Strategy 2: Try to group by grandparent (package mode 2 - Second Level)
            Dictionary<string, List<string>> byGrandparent = new Dictionary<string, List<string>>(System.StringComparer.OrdinalIgnoreCase);
            foreach (string path in normalizedPaths)
            {
                string parent = IOUtils.GetParentPath(path);
                if (string.IsNullOrEmpty(parent)) continue;

                string grandparent = IOUtils.GetParentPath(parent);
                if (string.IsNullOrEmpty(grandparent)) continue;

                if (!byGrandparent.ContainsKey(grandparent))
                {
                    byGrandparent[grandparent] = new List<string>();
                }
                byGrandparent[grandparent].Add(path);
            }

            // Check each grandparent - only create mode 2 grouping if ALL second-level descendants are orphaned
            foreach (KeyValuePair<string, List<string>> kvp in byGrandparent)
            {
                string grandparentPath = kvp.Key;
                List<string> orphanedGrandchildren = kvp.Value;

                // Get all actual second-level descendants from filesystem
                List<string> allGrandchildren = GetAllSecondLevelDescendants(grandparentPath);

                // Only create a mode 2 grouping if ALL grandchildren are orphaned
                if (allGrandchildren.Count > 0 && orphanedGrandchildren.Count == allGrandchildren.Count)
                {
                    opportunities.Add(new GroupingOption
                    {
                        RootPath = grandparentPath,
                        PackageMode = 2, // Second Level
                        CoveredDirectories = new List<string>(orphanedGrandchildren)
                    });
                }
            }

            // Strategy 3: Individual directories (package mode 0 - Root Folder)
            foreach (string path in normalizedPaths)
            {
                opportunities.Add(new GroupingOption
                {
                    RootPath = path,
                    PackageMode = 0, // Root Folder
                    CoveredDirectories = new List<string> {path}
                });
            }

            return opportunities;
        }

        private List<GroupingOption> SelectOptimalGrouping(List<GroupingOption> opportunities, List<string> orphanedPaths)
        {
            List<GroupingOption> selected = new List<GroupingOption>();
            HashSet<string> coveredPaths = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            // Normalize orphaned paths for comparison
            HashSet<string> normalizedOrphanedPaths = new HashSet<string>(
                orphanedPaths.Select(p => IOUtils.NormalizePath(p)).Where(p => !string.IsNullOrEmpty(p)),
                System.StringComparer.OrdinalIgnoreCase
            );

            // Use greedy set cover algorithm with preference for lower package modes
            while (coveredPaths.Count < normalizedOrphanedPaths.Count)
            {
                GroupingOption best = null;
                int bestNewlyCovered = 0;

                foreach (GroupingOption option in opportunities)
                {
                    // Count how many new directories this option would cover
                    int newlyCovered = option.CoveredDirectories.Count(d => !coveredPaths.Contains(IOUtils.NormalizePath(d)));

                    if (newlyCovered == 0) continue;

                    // Prefer this option if:
                    // 1. It covers more directories, OR
                    // 2. It covers the same number but has lower package mode (simpler)
                    if (best == null ||
                        newlyCovered > bestNewlyCovered ||
                        (newlyCovered == bestNewlyCovered && option.PackageMode < best.PackageMode))
                    {
                        best = option;
                        bestNewlyCovered = newlyCovered;
                    }
                }

                if (best == null) break; // No more options

                // Add the best option
                selected.Add(best);

                // Mark directories as covered
                foreach (string dir in best.CoveredDirectories)
                {
                    string normalized = IOUtils.NormalizePath(dir);
                    if (!string.IsNullOrEmpty(normalized))
                    {
                        coveredPaths.Add(normalized);
                    }
                }
            }

            return selected;
        }

        private List<string> GetAllChildDirectories(string parentPath)
        {
            List<string> children = new List<string>();

            if (string.IsNullOrEmpty(parentPath) || !Directory.Exists(parentPath))
            {
                return children;
            }

            try
            {
                string[] directories = Directory.GetDirectories(parentPath);
                foreach (string dir in directories)
                {
                    // Normalize and check if it should be ignored
                    string normalizedDir = IOUtils.NormalizePath(dir);
                    string relativePath = normalizedDir.Substring(IOUtils.NormalizePath(parentPath).Length + 1);

                    // Skip ignored paths using AssetImporter's logic
                    if (!AssetImporter.IsIgnoredPath(relativePath, false))
                    {
                        children.Add(normalizedDir);
                    }
                }
            }
            catch
            {
                // If directory enumeration fails, return empty list
            }

            return children;
        }

        private List<string> GetAllSecondLevelDescendants(string grandparentPath)
        {
            List<string> grandchildren = new List<string>();

            if (string.IsNullOrEmpty(grandparentPath) || !Directory.Exists(grandparentPath))
            {
                return grandchildren;
            }

            try
            {
                // Get all first-level children
                List<string> children = GetAllChildDirectories(grandparentPath);

                // For each child, get its children (the grandchildren)
                foreach (string child in children)
                {
                    List<string> childrenOfChild = GetAllChildDirectories(child);
                    grandchildren.AddRange(childrenOfChild);
                }
            }
            catch
            {
                // If directory enumeration fails, return empty list
            }

            return grandchildren;
        }

        private bool IsDirectoryCovered(string directoryPath)
        {
            if (string.IsNullOrEmpty(directoryPath)) return false;

            // Normalize the directory path
            string normalizedDirPath = IOUtils.NormalizePath(directoryPath);
            if (string.IsNullOrEmpty(normalizedDirPath)) return false;

            // Check against all folder specs
            foreach (FolderSpec spec in AI.Config.folders)
            {
                string specLocation = spec.GetLocation(true);
                if (string.IsNullOrEmpty(specLocation)) continue;

                // Normalize the folder spec path
                string normalizedSpecPath = IOUtils.NormalizePath(specLocation);
                if (string.IsNullOrEmpty(normalizedSpecPath)) return false;

                // Check coverage based on package mode
                switch (spec.packageMode)
                {
                    case 0: // Root Folder - exact match
                        if (string.Equals(normalizedDirPath, normalizedSpecPath, System.StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                        break;

                    case 1: // First Level Directories - check if directory is a direct child
                        if (IOUtils.IsDirectChildPath(normalizedDirPath, normalizedSpecPath))
                        {
                            return true;
                        }
                        break;

                    case 2: // Second Level Directories - check if directory is a grandchild
                        if (IOUtils.IsGrandchildPath(normalizedDirPath, normalizedSpecPath))
                        {
                            return true;
                        }
                        break;
                }
            }

            return false;
        }
    }
}