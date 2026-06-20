using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ImpossibleRobert.Common;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEditor.PackageManager;
using UnityEngine;
using static AssetInventory.AssetTreeViewControl;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
#if UNITY_6000_2_OR_NEWER
using BaseTreeViewState = UnityEditor.IMGUI.Controls.TreeViewState<int>;
#else
using BaseTreeViewState = UnityEditor.IMGUI.Controls.TreeViewState;
#endif

namespace AssetInventory
{
    public partial class IndexUI
    {
        private bool _usageCalculationInProgress;
        private bool _usageCalculationDone;
        private AssetUsage _usageCalculation;
        private Vector2 _reportScrollPos;

        private List<AssetInfo> _assetUsage;
        private Dictionary<int, AssetInfo> _usedPackages;
        private List<AssetInfo> _paidPackages;
        private List<AssetInfo> _identifiedFiles;
        private List<AssetInfo> _selectedReportEntries;
        private List<string> _licenses;
        private AssetInfo _selectedReportEntry;
        private AssetInfo _selectedReportFile;

        private long _reportTreeSubPackageCount;
        private long _reportTreeSelectionSize;
        private readonly Dictionary<string, Tuple<int, Color>> _reportBulkTags = new Dictionary<string, Tuple<int, Color>>();
        private HashSet<int> _reportPackageTreeIds = new HashSet<int>();

        [SerializeField] private MultiColumnHeaderState reportMchState;
        private TreeViewWithTreeModel<AssetInfo> ReportTreeView
        {
            get
            {
#pragma warning disable CS0618 // Type or member is obsolete
                if (_reportTreeViewState == null) _reportTreeViewState = new BaseTreeViewState();
#pragma warning restore CS0618 // Type or member is obsolete

                if (_reportTreeView == null)
                {
                    // Calculate available width dynamically (accounting for inspector width)
                    float availableWidth = position.width - UIStyles.INSPECTOR_WIDTH - 40; // 40 for margins
                    if (availableWidth < 570) availableWidth = 570; // minimum width

                    bool hadSerializedState = reportMchState != null;
                    MultiColumnHeaderState headerState = CreateDefaultMultiColumnHeaderState(availableWidth);
                    headerState.visibleColumns = new[] {(int)Columns.Name, (int)Columns.FileCount, (int)Columns.License, (int)Columns.Version};
                    headerState = GetUsableMultiColumnHeaderState(reportMchState, headerState);
                    RestoreColumnWidths(headerState, AI.Config.reportTreeColumnWidths);
                    reportMchState = headerState;

                    MultiColumnHeader mch = new MultiColumnHeader(headerState);
                    mch.canSort = false;
                    mch.height = MultiColumnHeader.DefaultGUI.minimumHeight;
                    if (!hadSerializedState && AI.Config.reportTreeColumnWidths == null) mch.ResizeToFit();

                    _reportTreeView = new AssetTreeViewControl(_reportTreeViewState, mch, ReportTreeModel);
                    _reportTreeView.OnSelectionChanged += OnReportTreeSelectionChanged;
                    _reportTreeView.OnDoubleClickedItem += OnReportTreeDoubleClicked;
                    _reportTreeView.Reload();

                    // Clear selection state after domain reload
                    _selectedReportEntry = null;
                    _selectedReportFile = null;
                    _selectedReportEntries?.Clear();
                }
                return _reportTreeView;
            }
        }
        private TreeViewWithTreeModel<AssetInfo> _reportTreeView;
#pragma warning disable CS0618 // Type or member is obsolete
        private BaseTreeViewState _reportTreeViewState;
#pragma warning restore CS0618 // Type or member is obsolete

        private TreeModel<AssetInfo> ReportTreeModel
        {
            get
            {
                if (_reportTreeModel == null) _reportTreeModel = new TreeModel<AssetInfo>(new List<AssetInfo> {new AssetInfo().WithTreeData("Root", depth: -1)});
                return _reportTreeModel;
            }
        }
        private TreeModel<AssetInfo> _reportTreeModel;

        private void DrawReportingTab()
        {
            int assetUsageCount = _assetUsage?.Count ?? 0;
            int identifiedFilesCount = _identifiedFiles?.Count ?? 0;
            int identifiedPackagesCount = _usedPackages?.Count ?? 0;
            int paidPackagesCount = _paidPackages?.Count ?? 0;
            string licenses = _licenses != null && _licenses.Count > 0 ? string.Join(", ", _licenses) : "n/a";

            int labelWidth = 130;

            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();

            UIBlock("reporting.hints.intro", () =>
            {
                EditorGUILayout.HelpBox("Reporting will try to identify used packages inside the current project using guids. Results for assets imported with Unity 2023+ will be 100% correct since Unity introduced origin tracking. Otherwise results might only be correct for the package but not for the version. Also, if package authors have shared files between projects this can result in multiple package candidates.", MessageType.Info);
                EditorGUILayout.Space();
            });

            UIBlock("reporting.overview", () =>
            {
                GUILayout.BeginHorizontal();
                GUILayout.BeginVertical();
                GUILabelWithText("Project files", $"{assetUsageCount:N0}", labelWidth);
                if (assetUsageCount > 0)
                {
                    GUILabelWithText("Identified packages", $"{identifiedPackagesCount:N0}", labelWidth);
                    GUILabelWithText("Identified files", $"{identifiedFilesCount:N0}" + " (" + Mathf.RoundToInt((float)identifiedFilesCount / assetUsageCount * 100f) + "%)", labelWidth);
                }
                else
                {
                    GUILabelWithText("Identified packages", "None", labelWidth);
                    GUILabelWithText("Identified files", "None", labelWidth);
                }
                GUILayout.EndVertical();
                GUILayout.BeginVertical();
                GUILabelWithText("Paid packages", $"{paidPackagesCount:N0}", labelWidth);
                GUILabelWithText("Used Licenses", $"{licenses}", labelWidth, null, true);
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
            });

            if (_usedPackages != null && _usedPackages.Count > 0)
            {
                EditorGUILayout.Space();

                GUILayout.BeginVertical(GUILayout.ExpandHeight(true));
                ReportTreeView.OnGUI(GUILayoutUtility.GetRect(0, 0, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true)));
                GUILayout.EndVertical();
            }
            else
            {
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField("Analyze the current project first to see results.", EditorStyles.centeredGreyMiniLabel);
                GUILayout.FlexibleSpace();
            }
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();

            GUILayout.BeginVertical(GUILayout.Width(UIStyles.INSPECTOR_WIDTH));
            GUILayout.BeginVertical("Actions", "window", GUILayout.Width(UIStyles.INSPECTOR_WIDTH));
            EditorGUILayout.Space();

            if (_usageCalculationInProgress)
            {
                EditorGUI.BeginDisabledGroup(_usageCalculation.CancellationRequested);
                if (GUILayout.Button("Stop Identification")) _usageCalculation.CancellationRequested = true;
                EditorGUI.EndDisabledGroup();

                EditorGUILayout.LabelField("Identification Progress", EditorStyles.boldLabel);
                CommonUIStyles.DrawProgressBar(_usageCalculation.MainProgress / (float)_usageCalculation.MainCount, $"{_usageCalculation.MainProgress}/{_usageCalculation.MainCount}");
                EditorGUILayout.LabelField(_usageCalculation.CurrentMain);
                EditorGUILayout.Space();
            }
            else
            {
                if (GUILayout.Button("Identify Used Packages", CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT)))
                {
                    CalculateAssetUsage();
                }
            }
            UIBlock("reporting.actions.export", () =>
            {
                if (GUILayout.Button("Export Data..."))
                {
                    ExportUI exportUI = ExportUI.ShowWindow();

                    List<AssetInfo> exportList;
                    if (_selectedReportEntries != null && _selectedReportEntries.Count > 1)
                    {
                        exportList = _selectedReportEntries;
                    }
                    else
                    {
                        // filter only for meaningful assets, since this is the overall database export
                        exportList = _assets.Where(a => a.AssetSource == Asset.Source.AssetStorePackage ||
                                a.AssetSource == Asset.Source.CustomPackage ||
                                a.AssetSource == Asset.Source.RegistryPackage)
                            .ToList();
                    }

                    exportUI.Init(exportList, false, 1, reportMchState?.visibleColumns);
                }
            });
            UIBlock("reporting.actions.freebies", () =>
            {
                if (GUILayout.Button("Find Freebies...")) FreebieUI.ShowWindow();
            });
            GUILayout.EndVertical();
            EditorGUILayout.Space();

            _reportScrollPos = GUILayout.BeginScrollView(_reportScrollPos, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.ExpandWidth(true));
            if (_selectedReportFile != null)
            {
                DrawFileInfo(_selectedReportFile, true);
                EditorGUILayout.Space();
            }
            else if (_selectedReportEntry != null)
            {
                DrawPackageInfo(_selectedReportEntry, true);
                EditorGUILayout.Space();
            }
            else if (_selectedReportEntries != null && _selectedReportEntries.Count > 0)
            {
                DrawBulkPackageActions(_selectedReportEntries, _reportTreeSubPackageCount, _reportBulkTags, _reportTreeSelectionSize, -1, -1, false);
                EditorGUILayout.Space();
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        private async void CalculateAssetUsage()
        {
            if (_usageCalculationInProgress) return;
            if (_assets == null) return;
            _usageCalculationInProgress = true;

            bool packageDataAvailable = false;
            try
            {
                List<AssetInfo> allAssets = _assets.Where(asset => asset != null).ToList();

                _usageCalculation = new AssetUsage();
                _assetUsage = await _usageCalculation.Calculate() ?? new List<AssetInfo>();
                _assetUsage = _assetUsage.Where(asset => asset != null).ToList();

                _identifiedFiles = _assetUsage.Where(info => info.CurrentState != Asset.State.Unknown).ToList();

                // add installed packages (sync path avoids async wait)
                Dictionary<string, PackageInfo> packageCollection = AssetStore.GetProjectPackagesSync();
                if (packageCollection != null)
                {
                    packageDataAvailable = true;
                    int unmatchedCount = 0;
                    foreach (PackageInfo packageInfo in packageCollection.Values.Where(info => info != null))
                    {
                        if (packageInfo.source == PackageSource.BuiltIn) continue;

                        AssetInfo matchedAsset = allAssets.FirstOrDefault(info => info.SafeName == packageInfo.name);
                        if (matchedAsset == null)
                        {
                            // Debug.Log($"Registry package '{packageInfo.name}' is not yet indexed, information will be incomplete.");
                            matchedAsset = new AssetInfo();
                            matchedAsset.AssetSource = Asset.Source.RegistryPackage;
                            matchedAsset.SafeName = packageInfo.name;
                            matchedAsset.DisplayName = packageInfo.displayName;
                            matchedAsset.Version = packageInfo.version;
                            matchedAsset.Id = int.MaxValue - unmatchedCount;
                            matchedAsset.AssetId = int.MaxValue - unmatchedCount;
                            unmatchedCount++;
                        }
                        _assetUsage.Add(matchedAsset);
                    }
                }
                Assets.ResolveParents(_assetUsage, allAssets);

                _usedPackages = _assetUsage.GroupBy(a => a.AssetId).Select(a => a.First()).ToDictionary(a => a.AssetId, a => a);

                // Restore correct DisplayName for sub-packages whose names were
                // overwritten by the origin overlay (which reports the parent's name)
                foreach (AssetInfo package in _usedPackages.Values)
                {
                    if (package.ParentId > 0)
                    {
                        AssetInfo indexed = allAssets.FirstOrDefault(a => a.AssetId == package.AssetId);
                        if (indexed != null)
                        {
                            if (!string.IsNullOrEmpty(indexed.DisplayName)) package.DisplayName = indexed.DisplayName;
                            if (!string.IsNullOrEmpty(indexed.SafeName)) package.SafeName = indexed.SafeName;
                        }
                    }
                }

                // Ensure parents of identified sub-packages are present in the
                // usage data so that the tree hierarchy is always complete.
                // Clone parents to avoid mutating the shared _assets objects
                // (whose ChildInfos contains grid-context sub-packages).
                List<AssetInfo> parentsToAdd = new List<AssetInfo>();
                foreach (AssetInfo package in _usedPackages.Values)
                {
                    if (package.ParentId > 0 && !_usedPackages.ContainsKey(package.ParentId))
                    {
                        AssetInfo parent = allAssets.FirstOrDefault(a => a.AssetId == package.ParentId);
                        if (parent != null && !parentsToAdd.Any(p => p.AssetId == parent.AssetId))
                        {
                            AssetInfo parentClone = new AssetInfo(parent.ToAsset());
                            parentClone.ResetChildInfos();
                            parentsToAdd.Add(parentClone);
                        }
                    }
                }
                foreach (AssetInfo parentClone in parentsToAdd)
                {
                    _assetUsage.Add(parentClone);
                    _usedPackages[parentClone.AssetId] = parentClone;
                }
                if (parentsToAdd.Count > 0)
                {
                    Assets.ResolveParents(parentsToAdd, allAssets);
                }

                // Enrich used packages with identified files
                foreach (AssetInfo file in _identifiedFiles)
                {
                    if (_usedPackages.TryGetValue(file.AssetId, out AssetInfo package))
                    {
                        package.AddChildInfo(file);
                    }
                }

                // Find unidentified files (files under Assets/ not matched to any package)
                HashSet<string> identifiedGuids = new HashSet<string>(_identifiedFiles.Where(a => !string.IsNullOrEmpty(a.Guid)).Select(a => a.Guid));
                string[] allGuids = AssetDatabase.FindAssets("", new[] {"Assets"});
                List<AssetInfo> unidentifiedFiles = new List<AssetInfo>();

                foreach (string guid in allGuids)
                {
                    if (!identifiedGuids.Contains(guid))
                    {
                        string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                        if (!string.IsNullOrEmpty(assetPath) && !AssetDatabase.IsValidFolder(assetPath)
                            && !assetPath.Contains(AI.TEMP_FOLDER) && !assetPath.Contains(UnityPreviewGenerator.PREVIEW_FOLDER))
                        {
                            AssetInfo unidentifiedFile = new AssetInfo();
                            unidentifiedFile.Guid = guid;
                            unidentifiedFile.Path = assetPath;
                            unidentifiedFile.FileName = Path.GetFileName(assetPath);
                            unidentifiedFile.Id = guid.GetHashCode();
                            unidentifiedFiles.Add(unidentifiedFile);
                        }
                    }
                }

                // Create artificial "-Unidentified-" package if there are unidentified files
                if (unidentifiedFiles.Count > 0)
                {
                    AssetInfo unidentifiedPackage = new AssetInfo();
                    unidentifiedPackage.DisplayName = "-Unidentified-";
                    unidentifiedPackage.SafeName = "-Unidentified-";
                    unidentifiedPackage.AssetSource = Asset.Source.CustomPackage;
                    unidentifiedPackage.Id = -1;
                    unidentifiedPackage.AssetId = -1;
                    unidentifiedPackage.ChildInfos = unidentifiedFiles;
                    _assetUsage.Add(unidentifiedPackage);
                    _usedPackages[-1] = unidentifiedPackage;
                }

                _paidPackages = _usedPackages.Where(a => a.Value.GetPrice() > 0).Select(a => a.Value).ToList();
                _licenses = new List<string> {"Standard Unity Asset Store EULA"};
                _licenses.AddRange(_usedPackages.Values.Where(info => info != null && !string.IsNullOrWhiteSpace(info.License)).Select(info => info.License).Distinct());
            }
            catch (Exception e)
            {
                Debug.LogError($"Could not calculate asset usage: {e.Message}");
            }

            _requireReportTreeRebuild = true;
            _requireAssetTreeRebuild = true;
            _usageCalculationInProgress = false;
            // Only mark as done if package data was available, otherwise allow retry when packages become available
            _usageCalculationDone = packageDataAvailable;
        }

        private void CalculateAssetUsageAutomatically()
        {
            if (!SearchScopeModel.ShouldAutoCalculateAssetUsage(GetConfiguredSearchScope())) return;
            CalculateAssetUsage();
        }

        private void CreateReportTree()
        {
            _requireReportTreeRebuild = false;
            List<AssetInfo> data = new List<AssetInfo>();
            AssetInfo root = new AssetInfo().WithTreeData("Root", depth: -1);
            data.Add(root);

            _reportPackageTreeIds.Clear();

            if (_assetUsage != null)
            {
                // apply filters
                IEnumerable<AssetInfo> filteredAssets = _assetUsage.GroupBy(a => a.AssetId).Select(a => a.First()).Where(a => !string.IsNullOrEmpty(a.GetDisplayName()));

                IOrderedEnumerable<AssetInfo> orderedAssets = filteredAssets.OrderBy(a => a.GetDisplayName(), StringComparer.OrdinalIgnoreCase);

                // First pass: add all packages without file hierarchies so that
                // ReorderSubPackages can safely rearrange them without orphaning
                // file/folder nodes or matching file nodes via AssetId lookups.
                foreach (AssetInfo package in orderedAssets)
                {
                    AI.GetObserver().Attach(package);

                    // Store identified file count in FileCount for display in column
                    package.FileCount = package.ChildInfoCount;
                    data.Add(package.WithTreeData(package.GetDisplayName(), package.AssetId, depth: 0));
                    _reportPackageTreeIds.Add(package.AssetId);
                }

                // re-add parents to sub-packages if they were filtered out
                ReAddMissingParents(orderedAssets, data);

                // track any re-added parent TreeIds
                foreach (AssetInfo item in data)
                {
                    if (item.Depth >= 0 && !_reportPackageTreeIds.Contains(item.TreeId))
                    {
                        _reportPackageTreeIds.Add(item.TreeId);
                    }
                }

                // reorder sub-packages
                ReorderSubPackages(data);

                // Second pass: insert file hierarchies under each package using its
                // final depth. Iterate backwards so earlier indices stay stable.
                int folderIdCounter = -100; // Negative IDs for folder nodes to avoid conflicts
                int fileIdCounter = int.MaxValue / 2; // Counts down; uses middle range to avoid collision with
                                                      // both normal package AssetIds (low) and unmatched registry
                                                      // packages (near int.MaxValue)
                for (int packageIdx = data.Count - 1; packageIdx >= 1; packageIdx--)
                {
                    AssetInfo package = data[packageIdx];
                    if (package.AssetSource == Asset.Source.RegistryPackage) continue;
                    if (!package.HasChildInfos) continue;

                    int baseDepth = package.Depth;

                    // Build folder structure from file paths
                    List<AssetInfo> fileHierarchy = BuildReportFileHierarchy(package.EnumerateChildInfos(), baseDepth, ref folderIdCounter, ref fileIdCounter);

                    data.InsertRange(packageIdx + 1, fileHierarchy);
                }
            }

            ReportTreeModel.SetData(data, true);
            ReportTreeView.Reload();

            // Collapse all items by default
            ReportTreeView.CollapseAll();

            // Clear selection after tree rebuild
            ReportTreeView.SetSelection(new List<int>());
            OnReportTreeSelectionChanged(new List<int>());

            _textureLoading3?.Cancel();
            _textureLoading3?.Dispose();
            _textureLoading3 = new CancellationTokenSource();
            AssetUtils.LoadTextures(data, _textureLoading3.Token);
        }

        internal static List<AssetInfo> BuildReportFileHierarchy(IEnumerable<AssetInfo> files, int baseDepth, ref int folderIdCounter, ref int fileIdCounter)
        {
            ReportFolderNode root = new ReportFolderNode(string.Empty);
            foreach (AssetInfo file in files.OrderBy(GetReportFilePath, StringComparer.OrdinalIgnoreCase))
            {
                AddReportFile(root, file);
            }

            List<AssetInfo> result = new List<AssetInfo>();
            AppendReportFolder(root, result, baseDepth, ref folderIdCounter, ref fileIdCounter);
            return result;
        }

        private static void AddReportFile(ReportFolderNode root, AssetInfo file)
        {
            string[] parts = GetReportPathParts(file);
            ReportFolderNode current = root;

            for (int i = 0; i < parts.Length - 1; i++)
            {
                string folderName = parts[i];
                if (!current.Folders.TryGetValue(folderName, out ReportFolderNode folder))
                {
                    folder = new ReportFolderNode(folderName);
                    current.Folders.Add(folderName, folder);
                }

                folder.FileCount++;
                current = folder;
            }

            string fileName = parts.Length > 0 ? parts[parts.Length - 1] : GetReportFilePath(file);
            current.Files.Add(new ReportFileNode(file, fileName));
        }

        private static void AppendReportFolder(ReportFolderNode folder, List<AssetInfo> result, int depth, ref int folderIdCounter, ref int fileIdCounter)
        {
            foreach (ReportFolderNode childFolder in folder.Folders.Values)
            {
                folderIdCounter--;
                string folderName = childFolder.FileCount > 0 ? $"{childFolder.Name} ({childFolder.FileCount:N0})" : childFolder.Name;
                result.Add(new AssetInfo().WithTreeData(folderName, folderIdCounter, depth: depth + 1));
                AppendReportFolder(childFolder, result, depth + 1, ref folderIdCounter, ref fileIdCounter);
            }

            folder.Files.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
            foreach (ReportFileNode file in folder.Files)
            {
                fileIdCounter--;
                AssetInfo fileNode = new AssetInfo(file.Info).WithTreeData(file.Name, fileIdCounter, depth: depth + 1);
                fileNode.FileCount = 0;
                result.Add(fileNode);
            }
        }

        private static string[] GetReportPathParts(AssetInfo file)
        {
            string filePath = GetReportFilePath(file).Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(filePath)) return new[] {"Unknown"};
            return filePath.Split(new[] {'/'}, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string GetReportFilePath(AssetInfo file)
        {
            if (!string.IsNullOrEmpty(file.ProjectPath)) return file.ProjectPath;
            if (!string.IsNullOrEmpty(file.Path)) return file.Path;
            if (!string.IsNullOrEmpty(file.FileName)) return file.FileName;
            return "Unknown";
        }

        private sealed class ReportFolderNode
        {
            public readonly string Name;
            public readonly SortedDictionary<string, ReportFolderNode> Folders = new SortedDictionary<string, ReportFolderNode>(StringComparer.OrdinalIgnoreCase);
            public readonly List<ReportFileNode> Files = new List<ReportFileNode>();
            public int FileCount;

            public ReportFolderNode(string name)
            {
                Name = name;
            }
        }

        private sealed class ReportFileNode
        {
            public readonly AssetInfo Info;
            public readonly string Name;

            public ReportFileNode(AssetInfo info, string name)
            {
                Info = info;
                Name = name;
            }
        }

        private void OnReportTreeDoubleClicked(int id)
        {
            if (id <= 0) return;

            AssetInfo info = ReportTreeModel.Find(id);
            string searchPhrase = null;

            // If this is a file (not a package), find the parent package and use filename as search phrase
            if (info != null && !_reportPackageTreeIds.Contains(id))
            {
                // Extract filename for search phrase
                searchPhrase = info.FileName;
                if (string.IsNullOrEmpty(searchPhrase) && !string.IsNullOrEmpty(info.Path))
                {
                    searchPhrase = System.IO.Path.GetFileName(info.Path);
                }

                // Files have AssetId pointing to the parent package
                // Packages are stored with AssetId as their tree id
                info = ReportTreeModel.Find(info.AssetId);
            }

            OpenInSearch(info, true, true, searchPhrase);
        }

        private void OnReportTreeSelectionChanged(IList<int> ids)
        {
            _selectedReportEntry = null;
            _selectedReportFile = null;
            _selectedReportEntries = _selectedReportEntries ?? new List<AssetInfo>();
            _selectedReportEntries.Clear();

            if (ids.Count == 1 && ids[0] > 0)
            {
                AssetInfo selected = ReportTreeModel.Find(ids[0]);
                if (selected != null)
                {
                    if (_reportPackageTreeIds.Contains(ids[0]))
                    {
                        // Package or sub-package selected
                        _selectedReportEntry = selected;
                        _selectedReportEntry.Refresh();
                    }
                    else
                    {
                        // File selected
                        _selectedReportFile = selected;
                        _selectedReportFile.Refresh();
                        _selectedReportFile.CheckIfInProject();
                        _selectedReportFile.IsMaterialized = Assets.IsMaterialized(_selectedReportFile.ToAsset(), _selectedReportFile);
                        CalcDependenciesOnDemand(_selectedReportFile);
                        if (AI.Config.pingSelected && _selectedReportFile.InProject) PingAsset(_selectedReportFile);
                    }
                }
            }

            // load all selected items but count each only once
            HashSet<int> seen = new HashSet<int>();
            foreach (int id in ids)
            {
                GatherTreeChildren(id, _selectedReportEntries, seen, ReportTreeModel);
            }
            // Filter to only include actual packages (any depth), not folders or file nodes
            _selectedReportEntries = _selectedReportEntries.Where(a => _reportPackageTreeIds.Contains(a.TreeId)).ToList();

            _reportBulkTags.Clear();
            _selectedReportEntries.ForEach(info => info.PackageTags?.ForEach(t =>
            {
                if (!_reportBulkTags.ContainsKey(t.Name)) _reportBulkTags.Add(t.Name, new Tuple<int, Color>(0, t.GetColor()));
                _reportBulkTags[t.Name] = new Tuple<int, Color>(_reportBulkTags[t.Name].Item1 + 1, _reportBulkTags[t.Name].Item2);
            }));

            _reportTreeSubPackageCount = _selectedReportEntries.Count(a => a.ParentId > 0);
            _reportTreeSelectionSize = _selectedReportEntries.Sum(a => a.PackageSize);
        }
    }
}
