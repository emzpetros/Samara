using System;
using System.Collections.Generic;
using System.IO;
using ImpossibleRobert.Common;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    public sealed class FolderSettingsUI : PopupWindowContent
    {
        private FolderSpec _spec;

        public void Init(FolderSpec spec)
        {
            _spec = spec;
        }

        public override void OnGUI(Rect rect)
        {
            editorWindow.maxSize = new Vector2(340, 340);
            editorWindow.minSize = editorWindow.maxSize;
            int width = 140;

            if (AI.ShowAdvanced())
            {
                RenderFolderLocationSection(width);
            }
            EditorGUILayout.Space();

            EditorGUI.BeginChangeCheck();

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(CommonUIStyles.Content("Content", "Type of content to scan for"), EditorStyles.boldLabel, GUILayout.Width(width));
            _spec.folderType = EditorGUILayout.Popup(_spec.folderType, IndexUI.FolderTypes);
            GUILayout.EndHorizontal();

            switch (_spec.folderType)
            {
                case 0: // packages
                    EditorGUI.BeginDisabledGroup(true);
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Assign Package", "Will connect indexed files to a new package with the name of the package file."), EditorStyles.boldLabel, GUILayout.Width(width));
                    EditorGUILayout.Toggle(true);
                    GUILayout.EndHorizontal();
                    EditorGUI.EndDisabledGroup();

                    RenderAssignTag(width);
                    break;

                case 1: // media
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Find", "File types to search for"), EditorStyles.boldLabel, GUILayout.Width(width));
                    _spec.scanFor = EditorGUILayout.Popup(_spec.scanFor, IndexUI.MediaTypes);
                    GUILayout.EndHorizontal();

                    if (_spec.scanFor == 7)
                    {
                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CommonUIStyles.Content("Pattern", "e.g. *.jpg;*.wav"), EditorStyles.boldLabel, GUILayout.Width(width));
                        _spec.pattern = EditorGUILayout.TextField(_spec.pattern);
                        GUILayout.EndHorizontal();
                    }

                    _spec.excludedExtensions = BasicEditorUI.GUIStringListField("Exclude Extensions", _spec.excludedExtensions, newValue => _spec.excludedExtensions = newValue, ",", "Excluded Extensions", width, "e.g. blend,max");

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Create Previews", "Recommended. Will generate previews and additional metadata but requires more time during indexing."), EditorStyles.boldLabel, GUILayout.Width(width));
                    _spec.createPreviews = EditorGUILayout.Toggle(_spec.createPreviews);
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Remove Orphans", "Recommended. Will check for deleted files and remove them from the index."), EditorStyles.boldLabel, GUILayout.Width(width));
                    _spec.removeOrphans = EditorGUILayout.Toggle(_spec.removeOrphans);
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Assign Package", $"Will connect indexed files to a new package with the name of the folder. Otherwise list them under '{Asset.NONE}'."), EditorStyles.boldLabel, GUILayout.Width(width));
                    _spec.attachToPackage = EditorGUILayout.Toggle(_spec.attachToPackage);
                    GUILayout.EndHorizontal();

                    if (_spec.attachToPackage)
                    {
                        string[] packageModeOptions = {"Root Folder", "First Level Directories", "Second Level Directories"};
                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CommonUIStyles.Content("Package Mode", "Root Folder: Creates one package based on the root folder name. First Level Directories: Creates separate packages for each direct subfolder. Second Level Directories: Creates separate packages for each second-level subdirectory."), EditorStyles.boldLabel, GUILayout.Width(width));
                        _spec.packageMode = EditorGUILayout.Popup(_spec.packageMode, packageModeOptions);
                        GUILayout.EndHorizontal();
                    }

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Detect Unity Projects", "Will check if the folder is the root of a Unity project and will only index the Assets/ folder while keeping the name of the Unity project as the package name."), EditorStyles.boldLabel, GUILayout.Width(width));
                    _spec.detectUnityProjects = EditorGUILayout.Toggle(_spec.detectUnityProjects);
                    GUILayout.EndHorizontal();

                    if (_spec.attachToPackage) RenderAssignTag(width);

                    break;

                case 2: // archive
                    EditorGUI.BeginDisabledGroup(true);
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Assign Package", "Will connect indexed files to a new package with the name of the archive."), EditorStyles.boldLabel, GUILayout.Width(width));
                    EditorGUILayout.Toggle(true);
                    GUILayout.EndHorizontal();
                    EditorGUI.EndDisabledGroup();

                    _spec.excludedExtensions = BasicEditorUI.GUIStringListField("Exclude Extensions", _spec.excludedExtensions, newValue => _spec.excludedExtensions = newValue, ",", "Excluded Extensions", width, "e.g. blend,max");

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Create Previews", "Recommended. Will generate previews and additional metadata but requires more time during indexing."), EditorStyles.boldLabel, GUILayout.Width(width));
                    _spec.createPreviews = EditorGUILayout.Toggle(_spec.createPreviews);
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Detect Unity Projects", "Will check if the folder is the root of a Unity project and will only index the Assets/ folder while keeping the name of the Unity project as the package name."), EditorStyles.boldLabel, GUILayout.Width(width));
                    _spec.detectUnityProjects = EditorGUILayout.Toggle(_spec.detectUnityProjects);
                    GUILayout.EndHorizontal();

                    RenderAssignTag(width);
                    break;

                case 3: // dev-packages
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Create Previews", "Recommended. Will generate previews and additional metadata but requires more time during indexing."), EditorStyles.boldLabel, GUILayout.Width(width));
                    _spec.createPreviews = EditorGUILayout.Toggle(_spec.createPreviews);
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Detect Unity Projects", "Will check if the folder is the root of a Unity project and will only index the Assets/ folder while keeping the name of the Unity project as the package name."), EditorStyles.boldLabel, GUILayout.Width(width));
                    _spec.detectUnityProjects = EditorGUILayout.Toggle(_spec.detectUnityProjects);
                    GUILayout.EndHorizontal();

                    RenderAssignTag(width);
                    break;
            }

            if (AI.ShowAdvanced())
            {
                _spec.excludedDirectories = BasicEditorUI.GUIStringListField("Exclude Directories", _spec.excludedDirectories, newValue => _spec.excludedDirectories = newValue, ",", "Excluded Directories", width, "Directory names to skip during scanning, separated by comma (e.g. node_modules,temp,build).");
            }

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(CommonUIStyles.Content("Check File Sizes", "Will update files upon changes in file size. Can significantly slow down the process if files are stored on slow drives or network shares."), EditorStyles.boldLabel, GUILayout.Width(width));
            _spec.checkSize = EditorGUILayout.Toggle(_spec.checkSize);
            GUILayout.EndHorizontal();

            if (AI.ShowAdvanced())
            {
                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Store Relative", "Persists file paths relative without the concrete base folder location to allow reusing the same database from different systems."), EditorStyles.boldLabel, GUILayout.Width(width));
                if (_spec.storeRelative)
                {
                    if (GUILayout.Button(CommonUIStyles.Content("Disable..."), GUILayout.ExpandWidth(false)))
                    {
                        RelativeUI relativeUI = RelativeUI.ShowWindow();
                        relativeUI.Init(_spec);
                        editorWindow.Close();
                    }
                }
                else
                {
                    if (GUILayout.Button(CommonUIStyles.Content("Enable..."), GUILayout.ExpandWidth(false)))
                    {
                        RelativeUI relativeUI = RelativeUI.ShowWindow();
                        relativeUI.Init(_spec);
                        editorWindow.Close();
                    }
                }
                GUILayout.EndHorizontal();
            }

            if (EditorGUI.EndChangeCheck()) AI.SaveConfig();
        }

        private void RenderAssignTag(int width)
        {
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(CommonUIStyles.Content("Assign Tags", "Will assign tags to all found packages. This makes it easy to filter for them later on."), EditorStyles.boldLabel, GUILayout.Width(width));
            _spec.assignTag = EditorGUILayout.Toggle(_spec.assignTag);
            GUILayout.EndHorizontal();

            if (_spec.assignTag)
            {
                _spec.tag = BasicEditorUI.GUIStringListField("Tags", _spec.tag, newValue => _spec.tag = newValue, ",", "Package Tags", width, "Tags to assign to each package, separated by comma (e.g. essential,2d).");
            }
        }

        private void RenderFolderLocationSection(int width)
        {
            string expandedPath = _spec.GetLocation(true);
            bool pathExists = !string.IsNullOrEmpty(expandedPath) && Directory.Exists(expandedPath);
            bool isRelative = Paths.IsRel(_spec.location);
            string storedPath = _spec.GetLocation(false);

            // Location Display
            GUILayout.BeginVertical("box");
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(CommonUIStyles.Content("Location", "Current folder location"), EditorStyles.boldLabel, GUILayout.Width(width));
            GUILayout.BeginVertical();

            if (!pathExists)
            {
                GUI.color = Color.red;
                EditorGUILayout.LabelField(expandedPath ?? "<Invalid Path>", EditorStyles.wordWrappedLabel);
                GUI.color = Color.white;
            }
            else
            {
                EditorGUILayout.LabelField(expandedPath, EditorStyles.wordWrappedLabel);
            }

            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            if (!pathExists)
            {
                EditorGUILayout.HelpBox("Folder not found. Use Change Location to point to the new folder location.", MessageType.Warning);
            }

            // Action Buttons
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(CommonUIStyles.Content("Actions", "Manage folder location"), EditorStyles.boldLabel, GUILayout.Width(width));
            GUILayout.BeginHorizontal();
            if (pathExists)
            {
                if (GUILayout.Button(CommonUIStyles.Content("Open", "Reveal the folder in the file browser"), GUILayout.ExpandWidth(false)))
                {
                    EditorUtility.RevealInFinder(expandedPath);
                }
                if (GUILayout.Button(CommonUIStyles.Content("Rename...", "Rename the folder on disk and update the path"), GUILayout.ExpandWidth(false)))
                {
                    OnRenameFolder();
                }
            }
            if (GUILayout.Button(CommonUIStyles.Content("Change...", "Point to a different folder location"), GUILayout.ExpandWidth(false)))
            {
                OnChangeLocation();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        private void OnRenameFolder()
        {
            string currentPath = _spec.GetLocation(true);
            if (string.IsNullOrEmpty(currentPath) || !Directory.Exists(currentPath))
            {
                EditorUtility.DisplayDialog("Invalid Path", "The folder does not exist and cannot be renamed.", "OK");
                return;
            }

            string currentFolderName = Path.GetFileName(currentPath);
            if (string.IsNullOrEmpty(currentFolderName))
            {
                // Handle root paths - can't rename root
                EditorUtility.DisplayDialog("Cannot Rename", "Root directories cannot be renamed.", "OK");
                return;
            }

            // Use NameUI to get the new folder name
            NameUI nameUI = new NameUI();
            nameUI.Init(currentFolderName, newName =>
            {
                if (currentFolderName == newName) return;
                if (string.IsNullOrWhiteSpace(newName))
                {
                    EditorUtility.DisplayDialog("Invalid Name", "Folder name cannot be empty.", "OK");
                    return;
                }

                string parentDir = Path.GetDirectoryName(currentPath);
                if (string.IsNullOrEmpty(parentDir))
                {
                    EditorUtility.DisplayDialog("Error", "Cannot determine parent directory.", "OK");
                    return;
                }

                string newPath = Path.Combine(parentDir, newName);

                // Check if target already exists
                if (Directory.Exists(newPath))
                {
                    EditorUtility.DisplayDialog("Folder Exists", $"A folder named '{newName}' already exists in that location.", "OK");
                    return;
                }

                try
                {
                    // Rename the folder on disk
                    Directory.Move(currentPath, newPath);

                    // Update database entries (packages and files) - must be done before updating folder spec
                    UpdateDatabasePaths(currentPath, newPath);

                    // Update the path in the folder spec
                    UpdateFolderLocation(newPath, false); // false = don't close popup yet

                    // Close popup after successful rename
                    editorWindow.Close();
                }
                catch (Exception e)
                {
                    EditorUtility.DisplayDialog("Rename Failed", $"Failed to rename folder: {e.Message}", "OK");
                }
            }, false, "Rename Folder");

            PopupWindow.Show(BasicEditorUI.GetPopupPositionAtMouse(), nameUI);
        }

        private void OnChangeLocation()
        {
            string currentPath = _spec.GetLocation(true);
            string defaultPath = "";

            if (!string.IsNullOrEmpty(currentPath) && Directory.Exists(currentPath))
            {
                defaultPath = currentPath;
            }

            string folder = EditorUtility.OpenFolderPanel("Select New Folder Location", defaultPath, "");
            if (string.IsNullOrEmpty(folder)) return;

            // Normalize paths for comparison
            string normalizedNewPath = Path.GetFullPath(folder).Replace("\\", "/").TrimEnd('/');
            string normalizedCurrentPath = currentPath?.Replace("\\", "/").TrimEnd('/');

            // Check if the path actually changed
            if (normalizedNewPath == normalizedCurrentPath)
            {
                // Path hasn't changed, nothing to do
                return;
            }

            // Update database entries (packages and files) - must be done before updating folder spec
            // Update even if current path doesn't exist (e.g., folder was moved/renamed externally)
            if (!string.IsNullOrEmpty(currentPath))
            {
                UpdateDatabasePaths(currentPath, normalizedNewPath);
            }

            // Update the path in the folder spec
            UpdateFolderLocation(folder);
        }

        private void UpdateFolderLocation(string newAbsolutePath, bool closePopup = true)
        {
            if (string.IsNullOrEmpty(newAbsolutePath))
            {
                EditorUtility.DisplayDialog("Invalid Path", "Please select a valid folder.", "OK");
                return;
            }

            // Make absolute and conform to OS separators
            string normalizedPath = Path.GetFullPath(newAbsolutePath);

            // Validate path exists
            if (!Directory.Exists(normalizedPath))
            {
                EditorUtility.DisplayDialog("Invalid Path", "The selected folder does not exist.", "OK");
                return;
            }

            // Update RelativeLocation entry FIRST so MakeRelative detects it as relative below
            string oldRelativeKey = _spec.relativeKey;
            bool wasUsingRelative = !string.IsNullOrEmpty(oldRelativeKey) && oldRelativeKey != "ac" && oldRelativeKey != "pc";

            if (wasUsingRelative)
            {
                string systemId = AI.GetSystemId();
                RelativeLocation relLocation = DBAdapter.DB.Find<RelativeLocation>(rl => rl.Key == oldRelativeKey && rl.System == systemId);
                if (relLocation != null)
                {
                    // Update the relative location to point to the new folder location
                    relLocation.SetLocation(normalizedPath);
                    DBAdapter.DB.Update(relLocation);
                    // Reload relative locations so MakeRelative can use the updated entry
                    Paths.LoadRelativeLocations();
                }
            }

            // Convert to relative path if possible (special case: a relative key is already defined for the folder, replace it immediately)
            // Now that we've updated the RelativeLocation entry, MakeRelative will detect it as relative
            string relativePath = Paths.MakeRelative(normalizedPath);

            // Prevent Unity asset cache folder selection (check after MakeRelative in case it was converted)
            if (relativePath.Contains(AI.ASSET_STORE_FOLDER_NAME))
            {
                EditorUtility.DisplayDialog("Attention", "You selected a custom Unity asset cache location. This should be done by setting the asset cache location above to custom.", "OK");
                return;
            }

            // Ensure no trailing slash if root folder on Windows
            if (relativePath.Length > 1 && relativePath.EndsWith("/"))
            {
                relativePath = relativePath.Substring(0, relativePath.Length - 1);
            }

            // Update location
            _spec.location = relativePath;

            // Update relative path tracking
            if (Paths.IsRel(relativePath))
            {
                _spec.storeRelative = true;
                _spec.relativeKey = Paths.GetRelKey(relativePath);
            }
            else
            {
                _spec.storeRelative = false;
                _spec.relativeKey = null;
            }

            // Save configuration
            AI.SaveConfig();

            // Reload relative locations to pick up any changes
            Paths.LoadRelativeLocations();

            // Close popup to provide feedback that operation completed
            if (closePopup)
            {
                editorWindow.Close();
            }
        }

        private void UpdateDatabasePaths(string oldPath, string newPath)
        {
            // Normalize paths for comparison (use forward slashes)
            string oldPathNormalized = oldPath.Replace("\\", "/").TrimEnd('/');
            string newPathNormalized = newPath.Replace("\\", "/").TrimEnd('/');

            // Get stored path versions (may include relative tags)
            string oldStoredPath = _spec.location;

            // Update packages/assets
            // For Root Folder mode: update packages where Location or SafeName matches the folder
            // For First/Second Level Directories mode: update all packages in subdirectories
            if (_spec.attachToPackage && (_spec.packageMode == 1 || _spec.packageMode == 2))
            {
                // First/Second Level Directories mode: update all packages in subdirectories
                // Find all packages where Location starts with oldPath
                List<Asset> affectedAssets = DBAdapter.DB.Query<Asset>(
                    "SELECT Id, Location, SafeName FROM Asset WHERE Location LIKE ? OR SafeName LIKE ?",
                    oldPathNormalized + "%", oldPathNormalized + "%");

                foreach (Asset asset in affectedAssets)
                {
                    // Only update if the path actually starts with the old path (to avoid false matches)
                    string newLocation = asset.Location;
                    string newSafeName = asset.SafeName;

                    if (!string.IsNullOrEmpty(asset.Location) && (asset.Location == oldPathNormalized || asset.Location.StartsWith(oldPathNormalized + "/")))
                    {
                        newLocation = asset.Location.Replace(oldPathNormalized, newPathNormalized);
                    }

                    if (!string.IsNullOrEmpty(asset.SafeName) && (asset.SafeName == oldPathNormalized || asset.SafeName.StartsWith(oldPathNormalized + "/")))
                    {
                        newSafeName = asset.SafeName.Replace(oldPathNormalized, newPathNormalized);
                    }

                    // Also handle stored path with relative tags if applicable
                    if (Paths.IsRel(oldStoredPath) && !string.IsNullOrEmpty(oldStoredPath))
                    {
                        string oldStoredPathNormalized = Paths.DeRel(oldStoredPath)?.Replace("\\", "/").TrimEnd('/');
                        if (!string.IsNullOrEmpty(oldStoredPathNormalized))
                        {
                            if (!string.IsNullOrEmpty(asset.Location) && (asset.Location == oldStoredPathNormalized || asset.Location.StartsWith(oldStoredPathNormalized + "/")))
                            {
                                newLocation = asset.Location.Replace(oldStoredPathNormalized, newPathNormalized);
                            }
                            if (!string.IsNullOrEmpty(asset.SafeName) && (asset.SafeName == oldStoredPathNormalized || asset.SafeName.StartsWith(oldStoredPathNormalized + "/")))
                            {
                                newSafeName = asset.SafeName.Replace(oldStoredPathNormalized, newPathNormalized);
                            }
                        }
                    }

                    DBAdapter.DB.Execute("UPDATE Asset SET Location = ?, SafeName = ? WHERE Id = ?", newLocation, newSafeName, asset.Id);
                }
            }
            else
            {
                // Root Folder mode: update packages where Location or SafeName matches the folder
                // Use LIKE to catch both exact matches and any edge cases
                List<Asset> affectedAssets = DBAdapter.DB.Query<Asset>(
                    "SELECT Id, Location, SafeName FROM Asset WHERE Location LIKE ? OR SafeName LIKE ?",
                    oldPathNormalized + "%", oldPathNormalized + "%");

                foreach (Asset asset in affectedAssets)
                {
                    // Only update if the path actually starts with the old path (to avoid false matches)
                    string newLocation = asset.Location;
                    string newSafeName = asset.SafeName;

                    if (!string.IsNullOrEmpty(asset.Location) && (asset.Location == oldPathNormalized || asset.Location.StartsWith(oldPathNormalized + "/")))
                    {
                        newLocation = asset.Location.Replace(oldPathNormalized, newPathNormalized);
                    }

                    if (!string.IsNullOrEmpty(asset.SafeName) && (asset.SafeName == oldPathNormalized || asset.SafeName.StartsWith(oldPathNormalized + "/")))
                    {
                        newSafeName = asset.SafeName.Replace(oldPathNormalized, newPathNormalized);
                    }

                    // Also handle stored path with relative tags if applicable
                    if (Paths.IsRel(oldStoredPath) && !string.IsNullOrEmpty(oldStoredPath))
                    {
                        string oldStoredPathNormalized = Paths.DeRel(oldStoredPath)?.Replace("\\", "/").TrimEnd('/');
                        if (!string.IsNullOrEmpty(oldStoredPathNormalized))
                        {
                            if (!string.IsNullOrEmpty(asset.Location) && (asset.Location == oldStoredPathNormalized || asset.Location.StartsWith(oldStoredPathNormalized + "/")))
                            {
                                newLocation = asset.Location.Replace(oldStoredPathNormalized, newPathNormalized);
                            }
                            if (!string.IsNullOrEmpty(asset.SafeName) && (asset.SafeName == oldStoredPathNormalized || asset.SafeName.StartsWith(oldStoredPathNormalized + "/")))
                            {
                                newSafeName = asset.SafeName.Replace(oldStoredPathNormalized, newPathNormalized);
                            }
                        }
                    }

                    DBAdapter.DB.Execute("UPDATE Asset SET Location = ?, SafeName = ? WHERE Id = ?", newLocation, newSafeName, asset.Id);
                }
            }

            // Update asset files (Path and SourcePath)
            // Find all files where Path or SourcePath starts with oldPath
            List<AssetFile> affectedFiles = DBAdapter.DB.Query<AssetFile>(
                "SELECT Id, Path, SourcePath FROM AssetFile WHERE Path LIKE ? OR SourcePath LIKE ?",
                oldPathNormalized + "%", oldPathNormalized + "%");

            foreach (AssetFile file in affectedFiles)
            {
                // Only update if the path actually starts with the old path (to avoid false matches)
                string newFilePath = file.Path;
                string newFileSourcePath = file.SourcePath;

                if (!string.IsNullOrEmpty(file.Path) && (file.Path == oldPathNormalized || file.Path.StartsWith(oldPathNormalized + "/")))
                {
                    newFilePath = file.Path.Replace(oldPathNormalized, newPathNormalized);
                }

                if (!string.IsNullOrEmpty(file.SourcePath) && (file.SourcePath == oldPathNormalized || file.SourcePath.StartsWith(oldPathNormalized + "/")))
                {
                    newFileSourcePath = file.SourcePath.Replace(oldPathNormalized, newPathNormalized);
                }

                // Also handle stored path with relative tags if applicable
                if (Paths.IsRel(oldStoredPath) && !string.IsNullOrEmpty(oldStoredPath))
                {
                    string oldStoredPathNormalized = Paths.DeRel(oldStoredPath)?.Replace("\\", "/").TrimEnd('/');
                    if (!string.IsNullOrEmpty(oldStoredPathNormalized))
                    {
                        if (!string.IsNullOrEmpty(file.Path) && (file.Path == oldStoredPathNormalized || file.Path.StartsWith(oldStoredPathNormalized + "/")))
                        {
                            newFilePath = file.Path.Replace(oldStoredPathNormalized, newPathNormalized);
                        }
                        if (!string.IsNullOrEmpty(file.SourcePath) && (file.SourcePath == oldStoredPathNormalized || file.SourcePath.StartsWith(oldStoredPathNormalized + "/")))
                        {
                            newFileSourcePath = file.SourcePath.Replace(oldStoredPathNormalized, newPathNormalized);
                        }
                    }
                }

                DBAdapter.DB.Execute("UPDATE AssetFile SET Path = ?, SourcePath = ? WHERE Id = ?", newFilePath, newFileSourcePath, file.Id);
            }
        }
    }
}