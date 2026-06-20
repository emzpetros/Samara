using ImpossibleRobert.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace AssetInventory
{
    public sealed class PackageUI : BasicEditorUI
    {
        private enum Mode
        {
            NewLocation,
            New,
            Edit
        }

        private Mode _mode;
        private Vector2 _scrollPos;
        private Vector2 _scrollDescr;
        private AssetInfo _info;
        private Asset _asset;
        private Action<Asset> _onSave;

        private string _newLocation = "https://github.com/WetzoldStudios/traVRsal-sdk.git";
        private int _gitRefSource;
        private int _gitBranchIdx;
        private int _gitTagIdx;
        private int _gitPRIdx;
        private string _gitCommit;
        private string[] _gitRefSourceOptions;
        private GitHandler _git;
        private bool _initDone;

        private string[] _availablePublishers;
        private string[] _availableCategories;
        private string[] _availableUnityVersions;
        private string[] _availableLicenses;
        private Dictionary<string, string> _publisherToSafe;
        private Dictionary<string, string> _categoryToSafe;

        public static PackageUI ShowWindow()
        {
            PackageUI window = GetWindow<PackageUI>("Package Data");
            window.minSize = new Vector2(400, 500);

            return window;
        }

        public void Init(AssetInfo info, Action<Asset> onSave)
        {
            _info = info;
            _mode = info == null || info.Id == 0 ? Mode.NewLocation : Mode.Edit;

            if (_mode == Mode.Edit)
            {
                _asset = DBAdapter.DB.Find<Asset>(_info.AssetId); // load fresh from DB and store that exact copy later again
                _asset.PreviewTexture = _info.PreviewTexture;
                if (_asset.PreviewTexture == null)
                {
                    // create grey texture
                    _asset.PreviewTexture = new Texture2D(100, 100);
                    _asset.PreviewTexture.SetPixel(0, 0, Color.grey);
                    _asset.PreviewTexture.Apply();
                }
            }
            else
            {
                if (_info == null)
                {
                    _info = new AssetInfo();
                    _info.AssetSource = Asset.Source.CustomPackage;
                }
                _asset = _info.ToAsset();
            }
            _onSave = onSave;
        }

        private void InitUI()
        {
            _initDone = true;
            _gitRefSourceOptions = new[] {"HEAD", "Branch", "Tag", "Pull Request", "Commit"};

            LoadDistinctPublishers();
            LoadDistinctCategories();
            LoadDistinctUnityVersions();
            LoadDistinctLicenses();
        }

        private void LoadDistinctPublishers()
        {
            string query = "SELECT DISTINCT DisplayPublisher, SafePublisher FROM Asset WHERE DisplayPublisher IS NOT NULL AND DisplayPublisher != '' ORDER BY DisplayPublisher";
            List<Asset> results = DBAdapter.DB.Query<Asset>(query);

            _publisherToSafe = results
                .Where(a => !string.IsNullOrEmpty(a.DisplayPublisher))
                .GroupBy(a => a.DisplayPublisher)
                .ToDictionary(g => g.Key, g => g.First().SafePublisher ?? string.Empty);

            _availablePublishers = _publisherToSafe.Keys.OrderBy(k => k).ToArray();
        }

        private void LoadDistinctCategories()
        {
            string query = "SELECT DISTINCT DisplayCategory, SafeCategory FROM Asset WHERE DisplayCategory IS NOT NULL AND DisplayCategory != '' ORDER BY DisplayCategory";
            List<Asset> results = DBAdapter.DB.Query<Asset>(query);

            _categoryToSafe = results
                .Where(a => !string.IsNullOrEmpty(a.DisplayCategory))
                .GroupBy(a => a.DisplayCategory)
                .ToDictionary(g => g.Key, g => g.First().SafeCategory ?? string.Empty);

            _availableCategories = _categoryToSafe.Keys.OrderBy(k => k).ToArray();
        }

        private void LoadDistinctUnityVersions()
        {
            string query = "SELECT DISTINCT SupportedUnityVersions FROM Asset WHERE SupportedUnityVersions IS NOT NULL AND SupportedUnityVersions != ''";
            List<string> allVersions = DBAdapter.DB.QueryScalars<string>(query);

            // Split comma-separated values and get distinct individual versions
            List<string> distinctVersions = allVersions
                .SelectMany(v => v.Split(','))
                .Select(v => v.Trim())
                .Where(v => !string.IsNullOrEmpty(v))
                .Distinct()
                .ToList();

            // Sort using SemVer
            _availableUnityVersions = distinctVersions
                .OrderBy(v => new SemVer(v))
                .ToArray();
        }

        private void LoadDistinctLicenses()
        {
            string query = "SELECT DISTINCT License FROM Asset WHERE License IS NOT NULL AND License != '' ORDER BY License";
            _availableLicenses = DBAdapter.DB.QueryScalars<string>(query).ToArray();
        }

        public override void OnGUI()
        {
            if (!_initDone) InitUI();
            if (ShouldCloseOnNextGUI()) // can happen after domain reload
            {
                Close();
                return;
            }
            int labelWidth = 90;

            if (_mode == Mode.NewLocation)
            {
                EditorGUILayout.HelpBox("Step 1: Enter the Url of the Git repository.", MessageType.Info);
                EditorGUILayout.Space();

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Location", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
                _newLocation = EditorGUILayout.TextField(_newLocation);
                EditorGUI.BeginDisabledGroup(string.IsNullOrEmpty(_newLocation));
                if (GUILayout.Button("Next", GUILayout.ExpandWidth(false)))
                {
                    _git = new GitHandler(_newLocation);
                    _git.GatherRemoteInfo();
                }
                EditorGUI.EndDisabledGroup();
                GUILayout.EndHorizontal();

                if (_git != null)
                {
                    EditorGUILayout.Space();
                    if (!_git.IsValid)
                    {
                        EditorGUILayout.HelpBox($"Connection error: {_git.LastError}", MessageType.Error);
                        return;
                    }

                    EditorGUILayout.HelpBox("Step 2: Select the correct reference.", MessageType.Info);
                    EditorGUILayout.Space();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Reference", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
                    _gitRefSource = EditorGUILayout.Popup(_gitRefSource, _gitRefSourceOptions);
                    GUILayout.EndHorizontal();

                    switch (_gitRefSource)
                    {
                        case 1:
                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField("Branch", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
                            _gitBranchIdx = EditorGUILayout.Popup(_gitBranchIdx, _git.ShortBranches);
                            GUILayout.EndHorizontal();
                            break;

                        case 2:
                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField("Tag", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
                            _gitTagIdx = EditorGUILayout.Popup(_gitTagIdx, _git.ShortTags);
                            GUILayout.EndHorizontal();
                            break;

                        case 3:
                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField("Pull Request", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
                            _gitTagIdx = EditorGUILayout.Popup(_gitTagIdx, _git.ShortPRs);
                            GUILayout.EndHorizontal();
                            break;

                        case 4:
                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField("Commit Id", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
                            _gitCommit = EditorGUILayout.TextField(_gitCommit);
                            GUILayout.EndHorizontal();
                            break;
                    }
                }
                return;
            }

            labelWidth = 130;
            EditorGUILayout.HelpBox("Change the values below to update the package data. The technical names are mandatory if you want filters or selection dropdowns to work properly.", MessageType.Info);
            EditorGUILayout.Space();

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            GUILayout.BeginVertical("box");
            GUILabelWithTextNoMax("Type", StringUtils.CamelCaseToWords(_asset.AssetSource.ToString()) + (_asset.AssetSource == Asset.Source.RegistryPackage ? $" ({_asset.PackageSource})" : ""), labelWidth);

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Location", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
            GUILayout.BeginVertical();
            EditorGUILayout.LabelField(GetLocationDisplayText(), EditorStyles.wordWrappedLabel);
            if (_mode == Mode.Edit && CanChangeLocation(_asset) && GUILayout.Button("Set Location...", GUILayout.ExpandWidth(false)))
            {
                ChangeLocation();
            }
            GUILayout.EndVertical();

            switch (_mode)
            {
                case Mode.New:
                    break;

                case Mode.Edit:
                    GUILayout.FlexibleSpace();

                    GUILayout.BeginVertical();
                    int tWidth = 65;
                    GUILayout.Box(_asset.PreviewTexture, EditorStyles.centeredGreyMiniLabel, GUILayout.MaxWidth(tWidth), GUILayout.MaxHeight(tWidth));
                    if (GUILayout.Button("Change...", GUILayout.MaxWidth(tWidth))) ChangePreview();
                    EditorGUILayout.Space();
                    GUILayout.EndVertical();
                    EditorGUILayout.Space();
                    break;
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            EditorGUILayout.Space();

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(CommonUIStyles.Content("Name", "Overrides the technical name"), EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
            _asset.DisplayName = EditorGUILayout.TextField(_asset.DisplayName);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Technical Name", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
            EditorGUI.BeginDisabledGroup(true);
            _asset.SafeName = EditorGUILayout.TextField(_asset.SafeName);
            EditorGUI.EndDisabledGroup();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(CommonUIStyles.Content("Publisher", "Overrides the technical publisher name"), EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
            _asset.DisplayPublisher = EditorGUILayout.TextField(_asset.DisplayPublisher);
            if (EditorGUILayout.DropdownButton(CommonUIStyles.Content(string.Empty, "Select from existing values"), FocusType.Keyboard, GUILayout.Width(20)))
            {
                ShowDropdownMenu(_availablePublishers, value =>
                {
                    _asset.DisplayPublisher = value;
                    if (_publisherToSafe != null && _publisherToSafe.TryGetValue(value, out string safeValue))
                    {
                        _asset.SafePublisher = safeValue;
                    }
                });
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Technical Publisher", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
            _asset.SafePublisher = EditorGUILayout.TextField(_asset.SafePublisher);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(CommonUIStyles.Content("Category", "Overrides the technical category name"), EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
            _asset.DisplayCategory = EditorGUILayout.TextField(_asset.DisplayCategory);
            if (EditorGUILayout.DropdownButton(CommonUIStyles.Content(string.Empty, "Select from existing values"), FocusType.Keyboard, GUILayout.Width(20)))
            {
                ShowDropdownMenu(_availableCategories, value =>
                {
                    _asset.DisplayCategory = value;
                    if (_categoryToSafe != null && _categoryToSafe.TryGetValue(value, out string safeValue))
                    {
                        _asset.SafeCategory = safeValue;
                    }
                });
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Technical Category", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
            _asset.SafeCategory = EditorGUILayout.TextField(_asset.SafeCategory);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Version", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
            _asset.Version = EditorGUILayout.TextField(_asset.Version);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Unity Versions", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
            _asset.SupportedUnityVersions = EditorGUILayout.TextField(_asset.SupportedUnityVersions);
            if (EditorGUILayout.DropdownButton(CommonUIStyles.Content(string.Empty, "Select from existing values"), FocusType.Keyboard, GUILayout.Width(20)))
            {
                ShowHierarchicalVersionMenu(_availableUnityVersions, value => _asset.SupportedUnityVersions = value);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Render Pipelines", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
            _asset.BIRPCompatible = EditorGUILayout.ToggleLeft("BIRP", _asset.BIRPCompatible, GUILayout.Width(60));
            _asset.URPCompatible = EditorGUILayout.ToggleLeft("URP", _asset.URPCompatible, GUILayout.Width(60));
            _asset.HDRPCompatible = EditorGUILayout.ToggleLeft("HDRP", _asset.HDRPCompatible, GUILayout.Width(60));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("License", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
            _asset.License = EditorGUILayout.TextField(_asset.License);
            if (EditorGUILayout.DropdownButton(CommonUIStyles.Content(string.Empty, "Select from existing values"), FocusType.Keyboard, GUILayout.Width(20)))
            {
                ShowDropdownMenu(_availableLicenses, value => _asset.License = value);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("License Location", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
            _asset.LicenseLocation = EditorGUILayout.TextField(_asset.LicenseLocation);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Price EUR", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
            _asset.PriceEur = EditorGUILayout.FloatField(_asset.PriceEur);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Price USD", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
            _asset.PriceUsd = EditorGUILayout.FloatField(_asset.PriceUsd);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Price CNY", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
            _asset.PriceCny = EditorGUILayout.FloatField(_asset.PriceCny);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Description", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
            _scrollDescr = EditorGUILayout.BeginScrollView(_scrollDescr, GUILayout.ExpandHeight(true));
            _asset.Description = EditorGUILayout.TextArea(_asset.Description, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
            GUILayout.EndHorizontal();

            EditorGUILayout.EndScrollView();

            GUILayout.FlexibleSpace();
            if (GUILayout.Button(_mode == Mode.New ? "Create" : "Save", CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT)))
            {
                if (SaveData())
                {
                    Close();
                }
            }
        }

        internal bool ChangeLocationForTests(string selectedPath)
        {
            return TryChangeLocation(selectedPath, out _);
        }

        private void ChangeLocation()
        {
            string currentLocation = _asset.GetLocation(true);
            string selectedPath;

            if (RequiresFolderLocation(_asset))
            {
                string startFolder = Directory.Exists(currentLocation) ? currentLocation : Path.GetDirectoryName(currentLocation);
                selectedPath = EditorUtility.OpenFolderPanel("Select Package Location", startFolder ?? string.Empty, string.Empty);
            }
            else
            {
                string startFolder = string.IsNullOrWhiteSpace(currentLocation) ? string.Empty : Path.GetDirectoryName(currentLocation);
                selectedPath = EditorUtility.OpenFilePanel("Select Package Location", startFolder ?? string.Empty, string.Empty);
            }

            if (string.IsNullOrWhiteSpace(selectedPath)) return;

            if (!TryChangeLocation(selectedPath, out string errorMessage))
            {
                EditorUtility.DisplayDialog("Invalid Location", errorMessage, "OK");
            }
        }

        private bool TryChangeLocation(string selectedPath, out string errorMessage)
        {
            errorMessage = null;
            if (_asset == null)
            {
                errorMessage = "No package is loaded.";
                return false;
            }

            if (!CanChangeLocation(_asset))
            {
                errorMessage = "This package type does not support manual location changes.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                errorMessage = "Select a package location first.";
                return false;
            }

            string normalizedPath = selectedPath.Replace("\\", "/");
            if (RequiresFolderLocation(_asset))
            {
                if (!Directory.Exists(normalizedPath))
                {
                    errorMessage = "The selected package folder does not exist.";
                    return false;
                }
            }
            else if (!File.Exists(normalizedPath))
            {
                errorMessage = "The selected package file does not exist.";
                return false;
            }

            _asset.SetLocation(normalizedPath);
            if (_info != null) _info.SetLocation(_asset.Location);
            DBAdapter.DB.Update(_asset);
            _onSave?.Invoke(_asset);
            return true;
        }

        internal bool ShouldCloseOnNextGUI()
        {
            if (_mode != Mode.Edit) return false;
            if (_asset == null) return true;
            if (!string.IsNullOrEmpty(_asset.Location)) return false;

            return _asset.AssetSource != Asset.Source.AssetManager || _asset.ParentId > 0;
        }

        internal string GetLocationDisplayTextForTests()
        {
            return GetLocationDisplayText();
        }

        private string GetLocationDisplayText()
        {
            if (_asset == null) return null;
            if (!string.IsNullOrEmpty(_asset.Location)) return _asset.Location;

            return _asset.AssetSource == Asset.Source.AssetManager && _asset.ParentId <= 0 ? "-Remote-" : _asset.Location;
        }

        private static bool CanChangeLocation(Asset asset)
        {
            if (asset == null || asset.ParentId > 0) return false;

            switch (asset.AssetSource)
            {
                case Asset.Source.AssetStorePackage:
                case Asset.Source.CustomPackage:
                case Asset.Source.Archive:
                case Asset.Source.Directory:
                    return true;

                case Asset.Source.RegistryPackage:
                    return asset.PackageSource == PackageSource.Embedded
                        || asset.PackageSource == PackageSource.Local
                        || asset.PackageSource == PackageSource.LocalTarball;

                default:
                    return false;
            }
        }

        private static bool RequiresFolderLocation(Asset asset)
        {
            return asset.AssetSource == Asset.Source.Directory
                || (asset.AssetSource == Asset.Source.RegistryPackage
                    && (asset.PackageSource == PackageSource.Embedded
                        || asset.PackageSource == PackageSource.Local));
        }

        private void ChangePreview()
        {
            string assetPreviewFile = EditorUtility.OpenFilePanel("Select image", "", "png");
            if (string.IsNullOrEmpty(assetPreviewFile)) return;

            try
            {
                // load immediately
                byte[] fileData = File.ReadAllBytes(assetPreviewFile);
                Texture2D tex = new Texture2D(2, 2);
                tex.LoadImage(fileData);

                // copy file
                string targetDir = Path.Combine(Paths.GetPreviewFolder(), _asset.Id.ToString());
                string targetFile = Path.Combine(targetDir, "a-" + _asset.Id + Path.GetExtension(assetPreviewFile));
                Directory.CreateDirectory(targetDir);
                File.Copy(assetPreviewFile, targetFile, true);
                AssetUtils.RemoveFromPreviewCache(targetFile);

                // set once all critical parts are done
                _asset.PreviewTexture = tex;
                _info.PreviewTexture = tex;
            }
            catch (Exception e)
            {
                Debug.LogError("Error loading image: " + e.Message);
            }
        }

        private bool SaveData()
        {
            if (string.IsNullOrWhiteSpace(_asset.DisplayName) && string.IsNullOrWhiteSpace(_asset.SafeName))
            {
                EditorUtility.DisplayDialog("Error", "Either name or technical name must be set.", "OK");
                return false;
            }
            if ((_asset.SafeCategory != null && _asset.SafeCategory.Contains("/"))
                || (_asset.SafePublisher != null && _asset.SafePublisher.Contains("/"))
               )
            {
                EditorUtility.DisplayDialog("Error", "Safe items must not contain any forward slashes.", "OK");
                return false;
            }

            DBAdapter.DB.Update(_asset);

            _onSave?.Invoke(_asset);

            return true;
        }

        private void ShowDropdownMenu(string[] options, Action<string> onSelect)
        {
            GenericMenu menu = new GenericMenu();

            if (options == null || options.Length == 0)
            {
                menu.AddDisabledItem(new GUIContent("No existing values found"));
            }
            else
            {
                foreach (string option in options)
                {
                    string capturedOption = option;
                    menu.AddItem(new GUIContent(option), false, () =>
                    {
                        onSelect?.Invoke(capturedOption);
                    });
                }
            }

            menu.ShowAsContext();
        }

        private void ShowHierarchicalVersionMenu(string[] options, Action<string> onSelect)
        {
            GenericMenu menu = new GenericMenu();

            if (options == null || options.Length == 0)
            {
                menu.AddDisabledItem(new GUIContent("No existing values found"));
            }
            else
            {
                foreach (string option in options)
                {
                    string capturedOption = option;
                    string menuPath = BuildVersionMenuPath(option);
                    menu.AddItem(new GUIContent(menuPath), false, () =>
                    {
                        onSelect?.Invoke(capturedOption);
                    });
                }
            }

            menu.ShowAsContext();
        }

        private string BuildVersionMenuPath(string version)
        {
            string[] parts = version.Split('.');
            if (parts.Length == 0) return version;

            // Build hierarchical path: "2019/2019.4/2019.4.1"
            string path = parts[0];
            string accumulated = parts[0];

            for (int i = 1; i < parts.Length; i++)
            {
                accumulated += "." + parts[i];
                path += "/" + accumulated;
            }

            return path;
        }
    }
}
