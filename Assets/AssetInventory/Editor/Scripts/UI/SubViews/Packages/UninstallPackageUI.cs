using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using ImpossibleRobert.Common;
using UnityEngine;
#pragma warning disable CS0618 // Type or member is obsolete
#if UNITY_6000_2_OR_NEWER
using BaseTreeViewState = UnityEditor.IMGUI.Controls.TreeViewState<int>;
#else
using BaseTreeViewState = UnityEditor.IMGUI.Controls.TreeViewState;
#endif

namespace AssetInventory
{
    public class UninstallPackageUI : BasicEditorUI
    {
        private AssetInfo _info;
        private string _displayName;
        private bool _fileMode;
        private Dictionary<string, List<string>> _usages;
        private bool _calculating;
        private bool _deleteEmptyFolders = true;

        private FileTreeViewControl _treeView;
        private BaseTreeViewState _treeViewState;

        // Async analysis fields
        private bool _analyzingUsages;
        private int _analysisProgress;
        private int _analysisTotal;
        private bool _cancellationRequested;
        private Dictionary<string, FileTreeElement> _pathToElementMap;

        public static UninstallPackageUI ShowWindow()
        {
            UninstallPackageUI window = GetWindow<UninstallPackageUI>("Uninstall Package");
            window.minSize = new Vector2(500, 400);
            return window;
        }

        public void Init(AssetInfo info, AssetInfo usageInfo = null)
        {
            _info = info;
            _displayName = info.GetDisplayName();
            _fileMode = false;
            titleContent = new GUIContent("Uninstall Package");
            _calculating = true;
            _usages = new Dictionary<string, List<string>>();

            AnalyzePackage(usageInfo);
        }

        public void Init(List<AssetInfo> files)
        {
            _info = files[0];
            _fileMode = true;
            _displayName = files.Count == 1 ? Path.GetFileName(files[0].GetPath(true)) : $"{files.Count:N0} Files";
            titleContent = new GUIContent("Remove from Project");
            _calculating = true;
            _usages = new Dictionary<string, List<string>>();

            AnalyzeFiles(files);
        }

        private void AnalyzePackage(AssetInfo usageInfo)
        {
            List<string> projectFiles = new List<string>();
            List<string> packageGuids = new List<string>();

            if (usageInfo?.HasChildInfos == true)
            {
                foreach (AssetInfo child in usageInfo.EnumerateChildInfos())
                {
                    if (!string.IsNullOrEmpty(child.ProjectPath))
                    {
                        projectFiles.Add(child.ProjectPath);
                        if (!string.IsNullOrEmpty(child.Guid)) packageGuids.Add(child.Guid);
                    }
                }
            }
            else
            {
                EditorUtility.DisplayDialog("Error", "Usage information not available. Please run 'Identify Used Packages' first.", "OK");
                Close();
                return;
            }

            BuildTreeFromPaths(projectFiles, packageGuids);
        }

        private void AnalyzeFiles(List<AssetInfo> files)
        {
            HashSet<string> projectFiles = new HashSet<string>();
            List<string> guids = new List<string>();

            foreach (AssetInfo file in files)
            {
                if (string.IsNullOrEmpty(file.ProjectPath)) continue;

                projectFiles.Add(file.ProjectPath);
                if (!string.IsNullOrEmpty(file.Guid)) guids.Add(file.Guid);

                // resolve forward dependencies
                string[] deps = AssetDatabase.GetDependencies(file.ProjectPath, true);
                foreach (string dep in deps)
                {
                    if (dep == file.ProjectPath) continue;
                    if (!dep.StartsWith("Assets/")) continue;

                    projectFiles.Add(dep);
                    string depGuid = AssetDatabase.AssetPathToGUID(dep);
                    if (!string.IsNullOrEmpty(depGuid)) guids.Add(depGuid);
                }
            }

            BuildTreeFromPaths(projectFiles.OrderBy(p => p).ToList(), guids);
        }

        private void BuildTreeFromPaths(List<string> projectFiles, List<string> guids)
        {
            if (_treeViewState == null) _treeViewState = new BaseTreeViewState();

            FileTreeBuilder.Result result = FileTreeBuilder.Build(projectFiles, _treeViewState);
            _treeView = result.TreeView;
            _pathToElementMap = result.PathToElementMap;

            // Mark folders that AssetDatabase recognizes
            foreach (KeyValuePair<string, FileTreeElement> kvp in _pathToElementMap)
            {
                if (!kvp.Value.IsFolder && AssetDatabase.IsValidFolder(kvp.Key))
                {
                    kvp.Value.IsFolder = true;
                }
            }

            _calculating = false;
            Repaint();

            _ = AnalyzeUsagesAsync(guids);
        }

        private async Task AnalyzeUsagesAsync(List<string> packageGuids)
        {
            _analyzingUsages = true;
            _cancellationRequested = false;
            _analysisProgress = 0;

            HashSet<string> packageGuidSet = new HashSet<string>(packageGuids);

            Dictionary<string, List<string>> usages = await ProjectDependencyAnalysis.FindReferencesAsync(packageGuidSet, (current, total) =>
            {
                if (_cancellationRequested) return;
                _analysisProgress = current;
                _analysisTotal = total;
            });

            if (!_cancellationRequested)
            {
                foreach (KeyValuePair<string, List<string>> kvp in usages)
                {
                    _usages[kvp.Key] = kvp.Value;

                    if (_pathToElementMap.TryGetValue(kvp.Key, out FileTreeElement element))
                    {
                        element.Usages = kvp.Value;
                    }
                }

                FileTreeSelection.DeselectConflicting(_treeView.Model);
            }

            _analyzingUsages = false;
            Repaint();
        }



        private new void OnGUI()
        {
            if (_info == null)
            {
                Close();
                return;
            }

            EditorGUILayout.Space();
            GUILabelWithTextNoMax(_fileMode ? "File" : "Package", _displayName, 70);
            EditorGUILayout.Space();

            if (_calculating)
            {
                EditorGUILayout.LabelField(_fileMode ? "Loading files..." : "Loading package files...", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            if (_treeView == null || _treeView.Model.NumberOfDataElements <= 1) // 1 is root
            {
                EditorGUILayout.HelpBox(_fileMode ? "No matching files found in the project." : "No files from this package found in the project.", MessageType.Info);
                return;
            }

            EditorGUILayout.HelpBox("Select files to delete. Files with warning icons are used by other assets.", MessageType.Info);
            EditorGUILayout.Space();

            // Show usage analysis progress
            if (_analyzingUsages)
            {
                EditorGUILayout.BeginHorizontal();
                CommonUIStyles.DrawProgressBar((float)_analysisProgress / _analysisTotal, $"Analyzing usage: {_analysisProgress:N0}/{_analysisTotal:N0} (optional)");
                if (GUILayout.Button("Cancel", GUILayout.ExpandWidth(false), GUILayout.Height(14)))
                {
                    _cancellationRequested = true;
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.Space();
            }

            // Selection buttons
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Select All", GUILayout.ExpandWidth(false)))
            {
                FileTreeSelection.SelectAll(_treeView.Model);
            }
            if (FileTreeSelection.HasConflicting(_treeView.Model))
            {
                if (GUILayout.Button("Deselect Conflicting", GUILayout.ExpandWidth(false)))
                {
                    FileTreeSelection.DeselectConflicting(_treeView.Model);
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space();

            // Tree View
            Rect treeRect = GUILayoutUtility.GetRect(0, 10000, 0, 10000);
            _treeView.OnGUI(treeRect);

            EditorGUILayout.Space();
            _deleteEmptyFolders = EditorGUILayout.ToggleLeft("Delete Empty Folders", _deleteEmptyFolders);
            EditorGUILayout.Space();

            int countToDelete = CountSelectedFiles();
            EditorGUI.BeginDisabledGroup(countToDelete <= 0);
            if (GUILayout.Button($"Delete {countToDelete:N0} Files", CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT)))
            {
                DeleteSelectedFiles();
            }
            EditorGUI.EndDisabledGroup();
        }

        private int CountSelectedFiles()
        {
            return _treeView.Model.GetData()
                .Count(e => e.Depth >= 0 && !e.IsFolder && e.IsSelected);
        }

        private void DeleteSelectedFiles()
        {
            List<string> filesToDelete = _treeView.Model.GetData()
                .Where(e => e.Depth >= 0 && !e.IsFolder && e.IsSelected)
                .Select(e => e.Path)
                .ToList();

            if (filesToDelete.Count == 0) return;

            if (_analyzingUsages)
            {
                string message = $"Usage analysis is still running. Some files may be used by other assets.\n\nAre you sure you want to delete {filesToDelete.Count:N0} files?\nThis cannot be undone.";
                if (!EditorUtility.DisplayDialog("Confirm Delete", message, "Delete", "Cancel")) return;
            }

            // Cancel analysis if still running
            if (_analyzingUsages)
            {
                _cancellationRequested = true;
            }

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string file in filesToDelete)
                {
                    AssetDatabase.DeleteAsset(file);
                }

                if (_deleteEmptyFolders)
                {
                    CleanEmptyFolders(filesToDelete);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            AssetDatabase.Refresh();
            AI.TriggerPackageRefresh();
            Close();
        }

        private void CleanEmptyFolders(List<string> deletedFiles)
        {
            HashSet<string> directories = new HashSet<string>();
            foreach (string file in deletedFiles)
            {
                string dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir)) directories.Add(dir);
            }

            bool deleted;
            do
            {
                deleted = false;
                List<string> currentDirs = directories.ToList();
                directories.Clear();

                foreach (string dir in currentDirs)
                {
                    if (Directory.Exists(dir) && IOUtils.IsDirectoryEmpty(dir))
                    {
                        AssetDatabase.DeleteAsset(dir);
                        deleted = true;

                        string parent = Path.GetDirectoryName(dir);
                        if (!string.IsNullOrEmpty(parent) && parent.Contains("Assets"))
                        {
                            directories.Add(parent);
                        }
                    }
                }
            } while (deleted);
        }

        private void OnInspectorUpdate()
        {
            if (_analyzingUsages) Repaint();
        }

        private void OnDestroy()
        {
            if (_analyzingUsages)
            {
                _cancellationRequested = true;
            }
        }
    }
}
