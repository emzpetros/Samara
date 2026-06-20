using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImpossibleRobert.Common;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    public sealed class FolderFineTuneUI : EditorWindow
    {
        private struct PackagePreview
        {
            public string Name;
            public int FileCount;
        }

        private FolderSpec _original;
        private FolderSpec _draft;
        private Vector2 _scrollPos;

        private bool _scanning;
        private List<PackagePreview> _packages = new List<PackagePreview>();
        private int _totalFiles;
        private double _lastChangeTime;
        private bool _needsRescan;
        private CancellationTokenSource _cts;
        private string _scanError;
        private string _resolvedPath;

        private static readonly string[] PackageModeOptions = {"Root Folder", "First Level Directories", "Second Level Directories"};
        private static readonly string[] PackageModeDescriptions =
        {
            "All files are grouped into a single package named after the root folder.",
            "Each direct subfolder becomes its own package.",
            "Each second-level subfolder becomes its own package."
        };

        public static FolderFineTuneUI ShowWindow(FolderSpec spec)
        {
            FolderFineTuneUI window = GetWindow<FolderFineTuneUI>("Fine-Tune Media Folder");
            window.minSize = new Vector2(520, 480);
            window.Init(spec);
            window.Show();

            return window;
        }

        private void Init(FolderSpec spec)
        {
            _original = spec;
            _draft = new FolderSpec(spec);
            _resolvedPath = _draft.GetLocation(true);
            _lastChangeTime = 0;
            _needsRescan = true;
        }

        private void OnDestroy()
        {
            CancelScan();
        }

        public void OnGUI()
        {
            if (_draft == null)
            {
                EditorGUILayout.HelpBox("No folder selected.", MessageType.Warning);
                return;
            }

            // handle debounced rescan
            if (_needsRescan && EditorApplication.timeSinceStartup - _lastChangeTime > 0.3)
            {
                _needsRescan = false;
                StartScan();
            }

            EditorGUILayout.Space(6);

            // header
            GUILayout.BeginVertical("box");
            EditorGUILayout.LabelField(_resolvedPath ?? _draft.location, EditorStyles.boldLabel);
            GUILayout.EndVertical();

            EditorGUILayout.Space(8);

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            DrawFileFilterSection();
            EditorGUILayout.Space(12);
            DrawPackageModeSection();
            EditorGUILayout.Space(12);
            DrawPreviewSection();

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4);
            DrawFooter();
            EditorGUILayout.Space(4);
        }

        private void DrawFileFilterSection()
        {
            EditorGUILayout.LabelField("Which files to index", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            EditorGUI.BeginChangeCheck();

            int labelWidth = 130;

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(CommonUIStyles.Content("File Types", "Determines which files are scanned and added to the index."), GUILayout.Width(labelWidth));
            _draft.scanFor = EditorGUILayout.Popup(_draft.scanFor, IndexUI.MediaTypes);
            GUILayout.EndHorizontal();

            // contextual hint indented under dropdown
            switch (_draft.scanFor)
            {
                case 0:
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(labelWidth + 4);
                    EditorGUILayout.LabelField("Only audio, image and 3D model files will be indexed.", EditorStyles.miniLabel);
                    GUILayout.EndHorizontal();
                    break;

                case 1:
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(labelWidth + 4);
                    EditorGUILayout.LabelField("Every file in the folder will be indexed regardless of type.", EditorStyles.miniLabel);
                    GUILayout.EndHorizontal();
                    break;

                case 3:
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(labelWidth + 4);
                    EditorGUILayout.LabelField("Only audio files (wav, mp3, ogg, flac, ...).", EditorStyles.miniLabel);
                    GUILayout.EndHorizontal();
                    break;

                case 4:
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(labelWidth + 4);
                    EditorGUILayout.LabelField("Only image files (png, jpg, psd, tga, ...).", EditorStyles.miniLabel);
                    GUILayout.EndHorizontal();
                    break;

                case 5:
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(labelWidth + 4);
                    EditorGUILayout.LabelField("Only 3D model files (fbx, obj, blend, ...).", EditorStyles.miniLabel);
                    GUILayout.EndHorizontal();
                    break;
            }

            if (_draft.scanFor == 7)
            {
                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Pattern", "e.g. *.jpg;*.wav"), GUILayout.Width(labelWidth));
                _draft.pattern = EditorGUILayout.TextField(_draft.pattern);
                GUILayout.EndHorizontal();
            }

            _draft.excludedExtensions = BasicEditorUI.GUIStringListField("Exclude Extensions", _draft.excludedExtensions, newValue => _draft.excludedExtensions = newValue, ",", "Excluded Extensions", labelWidth, "e.g. blend,max");

            if (EditorGUI.EndChangeCheck()) MarkChanged();
        }

        private void DrawPackageModeSection()
        {
            EditorGUILayout.LabelField("How packages are created", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            EditorGUI.BeginChangeCheck();

            int labelWidth = 130;

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(CommonUIStyles.Content("Create Packages", "Groups indexed files into packages so they can be found and managed more easily."), GUILayout.Width(labelWidth));
            _draft.attachToPackage = EditorGUILayout.Toggle(_draft.attachToPackage);
            GUILayout.EndHorizontal();

            if (_draft.attachToPackage)
            {
                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Package Mode", "Controls how the folder structure maps to packages."), GUILayout.Width(labelWidth));
                _draft.packageMode = EditorGUILayout.Popup(_draft.packageMode, PackageModeOptions);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Space(labelWidth + 4);
                EditorGUILayout.LabelField(PackageModeDescriptions[_draft.packageMode], EditorStyles.miniLabel);
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(labelWidth + 4);
                EditorGUILayout.LabelField($"Files will be listed under the generic '{Asset.NONE}' entry without any package grouping.", EditorStyles.wordWrappedMiniLabel);
                GUILayout.EndHorizontal();
            }

            if (EditorGUI.EndChangeCheck()) MarkChanged();
        }

        private void DrawPreviewSection()
        {
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            if (_scanning)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField("Scanning folder...", EditorStyles.centeredGreyMiniLabel);
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();

                Repaint();
                return;
            }

            if (!string.IsNullOrEmpty(_scanError))
            {
                EditorGUILayout.HelpBox(_scanError, MessageType.Warning);
                return;
            }

            if (_packages.Count == 0 && _totalFiles == 0)
            {
                EditorGUILayout.HelpBox("No matching files found with the current settings.", MessageType.Warning);
                return;
            }

            // summary
            if (_draft.attachToPackage)
            {
                string packageWord = _packages.Count == 1 ? "package" : "packages";
                string fileWord = _totalFiles == 1 ? "file" : "files";
                EditorGUILayout.LabelField($"{_packages.Count:N0} {packageWord}  ·  {_totalFiles:N0} {fileWord} total");
            }
            else
            {
                string fileWord = _totalFiles == 1 ? "file" : "files";
                EditorGUILayout.LabelField($"{_totalFiles:N0} {fileWord} will be indexed without package grouping.");
            }

            if (_draft.attachToPackage && _packages.Count > 0)
            {
                EditorGUILayout.Space(4);

                // package list with alternating row backgrounds and padding
                int maxVisible = 200;
                int shown = Math.Min(_packages.Count, maxVisible);

                GUILayout.BeginHorizontal();
                GUILayout.Space(8);
                GUILayout.BeginVertical();

                for (int i = 0; i < shown; i++)
                {
                    PackagePreview pkg = _packages[i];

                    Rect rowRect = EditorGUILayout.BeginHorizontal(GUILayout.Height(20));
                    if (Event.current.type == EventType.Repaint && i % 2 == 1)
                    {
                        Color overlay = EditorGUIUtility.isProSkin
                            ? new Color(1f, 1f, 1f, 0.03f)
                            : new Color(0f, 0f, 0f, 0.04f);
                        EditorGUI.DrawRect(rowRect, overlay);
                    }

                    EditorGUILayout.LabelField(pkg.Name, GUILayout.ExpandWidth(true));
                    EditorGUILayout.LabelField($"{pkg.FileCount:N0} files", CommonUIStyles.miniLabelRight, GUILayout.Width(80));
                    GUILayout.Space(8);
                    EditorGUILayout.EndHorizontal();
                }

                if (_packages.Count > maxVisible)
                {
                    EditorGUILayout.LabelField($"... and {_packages.Count - maxVisible:N0} more", EditorStyles.centeredGreyMiniLabel);
                }

                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
            }
        }

        private void DrawFooter()
        {
            GUILayout.BeginHorizontal();

            EditorGUI.BeginDisabledGroup(_scanning);
            if (GUILayout.Button("Apply", CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT)))
            {
                ApplyChanges();
            }
            EditorGUI.EndDisabledGroup();

            if (GUILayout.Button("Cancel", GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT)))
            {
                Close();
            }

            GUILayout.EndHorizontal();
        }

        private void ApplyChanges()
        {
            _original.scanFor = _draft.scanFor;
            _original.pattern = _draft.pattern;
            _original.excludedExtensions = _draft.excludedExtensions;
            _original.attachToPackage = _draft.attachToPackage;
            _original.packageMode = _draft.packageMode;

            AI.SaveConfig();
            Close();
        }

        private void MarkChanged()
        {
            _lastChangeTime = EditorApplication.timeSinceStartup;
            _needsRescan = true;
            Repaint();
        }

        private void CancelScan()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
        }

        private void StartScan()
        {
            CancelScan();

            string fullLocation = _resolvedPath;
            if (string.IsNullOrEmpty(fullLocation) || !Directory.Exists(fullLocation))
            {
                _scanError = "Folder not found.";
                _packages.Clear();
                _totalFiles = 0;
                Repaint();
                return;
            }

            _scanning = true;
            _scanError = null;
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            // capture draft values for background thread
            int scanFor = _draft.scanFor;
            string pattern = _draft.pattern;
            string excludedExt = _draft.excludedExtensions;
            string excludedDirs = _draft.excludedDirectories;
            bool attachToPackage = _draft.attachToPackage;
            int packageMode = _draft.packageMode;
            bool detectUnity = _draft.detectUnityProjects;

            Task.Run(() => ComputePreview(fullLocation, scanFor, pattern, excludedExt, excludedDirs, attachToPackage, packageMode, detectUnity, token), token)
                .ContinueWith(task =>
                {
                    EditorApplication.delayCall += () =>
                    {
                        if (task.IsCanceled) return;

                        if (task.IsFaulted)
                        {
                            _scanError = "Scan failed: " + task.Exception?.InnerException?.Message;
                            _packages.Clear();
                            _totalFiles = 0;
                        }
                        else
                        {
                            (List<PackagePreview> packages, int total) = task.Result;
                            _packages = packages;
                            _totalFiles = total;
                            _scanError = null;
                        }
                        _scanning = false;
                        Repaint();
                    };
                });

            Repaint();
        }

        private static (List<PackagePreview>, int) ComputePreview(
            string fullLocation, int scanFor, string pattern, string excludedExt,
            string excludedDirs, bool attachToPackage, int packageMode, bool detectUnity,
            CancellationToken token)
        {
            List<string> searchPatterns = new List<string>();
            List<AI.AssetGroup> types = new List<AI.AssetGroup>();

            switch (scanFor)
            {
                case 0:
                    types.AddRange(new[] {AI.AssetGroup.Audio, AI.AssetGroup.Images, AI.AssetGroup.Models});
                    break;
                case 1:
                    searchPatterns.Add("*.*");
                    break;
                case 3:
                    types.Add(AI.AssetGroup.Audio);
                    break;
                case 4:
                    types.Add(AI.AssetGroup.Images);
                    break;
                case 5:
                    types.Add(AI.AssetGroup.Models);
                    break;
                case 7:
                    if (!string.IsNullOrWhiteSpace(pattern)) searchPatterns.AddRange(pattern.Split(';'));
                    break;
            }

            types.ForEach(t => searchPatterns.AddRange(AI.TypeGroups[t].Select(ext => $"*.{ext}")));

            string[] exclExt = StringUtils.Split(excludedExt, new[] {';', ','});
            string[] exclDirArr = StringUtils.Split(excludedDirs, new[] {';', ','});

            bool treatAsUnityProject = detectUnity && AssetUtils.IsUnityProject(fullLocation);
            string scanPath = treatAsUnityProject ? Path.Combine(fullLocation, "Assets") : fullLocation;

            if (!Directory.Exists(scanPath)) return (new List<PackagePreview>(), 0);

            token.ThrowIfCancellationRequested();

            if (!attachToPackage)
            {
                int count = CountFiles(scanPath, searchPatterns, exclExt, exclDirArr, scanPath, token);
                return (new List<PackagePreview>(), count);
            }

            List<PackagePreview> packages = new List<PackagePreview>();
            int totalFiles = 0;

            if (packageMode == 0)
            {
                // Root Folder: single package
                string name = Path.GetFileName(fullLocation);
                if (treatAsUnityProject) name = Path.GetFileName(fullLocation);
                int count = CountFiles(scanPath, searchPatterns, exclExt, exclDirArr, scanPath, token);
                packages.Add(new PackagePreview {Name = name, FileCount = count});
                totalFiles = count;
            }
            else
            {
                // First Level or Second Level
                IEnumerable<string> targetDirs;

                string[] firstLevelDirs = Directory.GetDirectories(scanPath)
                    .Where(d =>
                    {
                        string rel = d.Substring(scanPath.Length + 1).Replace("\\", "/");
                        return !AssetImporter.IsIgnoredPath(rel, true) && !AssetImporter.IsExcludedDirectory(rel, exclDirArr, false);
                    })
                    .ToArray();

                if (packageMode == 1)
                {
                    targetDirs = firstLevelDirs;
                }
                else
                {
                    // Second Level: get subdirectories of first-level directories
                    targetDirs = firstLevelDirs
                        .SelectMany(firstLevel =>
                        {
                            if (!Directory.Exists(firstLevel)) return Enumerable.Empty<string>();
                            return Directory.GetDirectories(firstLevel)
                                .Where(d =>
                                {
                                    string rel = d.Substring(scanPath.Length + 1).Replace("\\", "/");
                                    return !AssetImporter.IsIgnoredPath(rel, true) && !AssetImporter.IsExcludedDirectory(rel, exclDirArr, false);
                                });
                        });
                }

                foreach (string dir in targetDirs)
                {
                    token.ThrowIfCancellationRequested();

                    string name = Path.GetFileName(dir);
                    int count = CountFiles(dir, searchPatterns, exclExt, exclDirArr, dir, token);
                    if (count > 0)
                    {
                        packages.Add(new PackagePreview {Name = name, FileCount = count});
                        totalFiles += count;
                    }
                }

                packages.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            }

            return (packages, totalFiles);
        }

        private static int CountFiles(string path, List<string> searchPatterns, string[] excludedExtensions, string[] excludedDirectories, string basePath, CancellationToken token)
        {
            if (!Directory.Exists(path)) return 0;

            token.ThrowIfCancellationRequested();

            IEnumerable<string> files;
            try
            {
                files = IOUtils.GetFiles(path, searchPatterns, SearchOption.AllDirectories, allowParallel: false);
            }
            catch (Exception)
            {
                return 0;
            }

            int count = 0;
            foreach (string file in files)
            {
                if (count % 500 == 0) token.ThrowIfCancellationRequested();

                string type = IOUtils.GetExtensionWithoutDot(file).ToLowerInvariant();
                if (type == "meta") continue;
                if (excludedExtensions != null && excludedExtensions.Contains(type)) continue;
                if (AssetImporter.IsExcludedDirectory(file, excludedDirectories)) continue;

                string relPath = file.Substring(basePath.Length + 1).Replace("\\", "/");
                if (AssetImporter.IsIgnoredPath(relPath, false)) continue;

                count++;
            }
            return count;
        }
    }
}
