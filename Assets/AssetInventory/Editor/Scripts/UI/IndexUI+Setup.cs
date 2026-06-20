using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
#if !USE_VECTOR_GRAPHICS || !USE_TUTORIALS || !USE_SHADER_GRAPH || (!USE_TEXTMESHPRO && !UNITY_2023_2_OR_NEWER)
using UnityEditor.PackageManager;
#endif
using UnityEngine;
using System.IO;
using ImpossibleRobert.Common;
using System;
using System.Linq;
using Brain;

namespace AssetInventory
{
    public partial class IndexUI
    {
        private const float WIZARD_STEP_LIST_WIDTH = 200f;

        private List<IWizardPage> _wizardPages;
        private Vector2 _wizardScrollPosition;

        private void DrawSetupView()
        {
            InitializeWizardPages();
            DrawEmbeddedWizard();
        }

        private void InitializeWizardPages()
        {
            if (_wizardPages == null)
            {
                _wizardPages = new List<IWizardPage>
                {
                    new SetupWizardIntroPage(),
                    new SetupWizardDownloadPage(),
                    new SetupWizardLocationsPage(),
                    new SetupWizardPreviewPage(),
                    new SetupWizardAIPage(),
                    new SetupWizardUIPage(),
                    new SetupWizardCompletionPage()
                };

                // Load current page from config
                if (AI.Config.wizardCurrentPage >= _wizardPages.Count)
                {
                    AI.Config.wizardCurrentPage = 0;
                }

                NavigateToWizardPage(AI.Config.wizardCurrentPage);
            }
        }

        private void DrawEmbeddedWizard()
        {
            GUILayout.BeginHorizontal();

            // Step list panel
            DrawWizardStepList();

            // Main content area
            GUILayout.BeginVertical();
            DrawWizardContent();
            DrawWizardFooter();
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
        }

        private void DrawWizardStepList()
        {
            GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(WIZARD_STEP_LIST_WIDTH), GUILayout.ExpandHeight(true));

            EditorGUILayout.LabelField("Setup Steps", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            for (int i = 0; i < _wizardPages.Count; i++)
            {
                IWizardPage page = _wizardPages[i];
                bool isCurrentPage = i == AI.Config.wizardCurrentPage;
                bool isCompleted = page.IsCompleted;
                bool isClickable = i <= AI.Config.wizardCurrentPage || isCompleted;

                // Determine the style and icon
                GUIStyle stepStyle = isCurrentPage ? EditorStyles.boldLabel : EditorStyles.label;
                string stepIcon = isCompleted ? "☑" : "☐"; // Checked box vs empty box
                string stepText = $"{stepIcon} {page.Title}";

                // Draw step button
                EditorGUI.BeginDisabledGroup(!isClickable);
                if (GUILayout.Button(stepText, stepStyle, GUILayout.Height(25)))
                {
                    if (isClickable)
                    {
                        NavigateToWizardPage(i);
                    }
                }
                EditorGUI.EndDisabledGroup();
            }

            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Box(Logo, EditorStyles.centeredGreyMiniLabel, GUILayout.MaxWidth(150), GUILayout.MaxHeight(150), GUILayout.MinHeight(50), GUILayout.MinWidth(50));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            EditorGUILayout.Space();

            GUILayout.EndVertical();
        }

        private void DrawWizardContent()
        {
            if (_wizardPages == null || _wizardPages.Count == 0 || AI.Config.wizardCurrentPage >= _wizardPages.Count) return;

            _wizardScrollPosition = GUILayout.BeginScrollView(_wizardScrollPosition);

            IWizardPage currentPage = _wizardPages[AI.Config.wizardCurrentPage];

            // Call OnEnter when page becomes active
            if (Event.current.type == EventType.Layout)
            {
                currentPage.OnEnter();
            }

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField(currentPage.Title, EditorStyles.largeLabel);
            EditorGUILayout.Space(5);

            if (!string.IsNullOrEmpty(currentPage.Description))
            {
                EditorGUILayout.LabelField(currentPage.Description, EditorStyles.wordWrappedLabel);
                EditorGUILayout.Space(10);
            }

            // Draw the page content
            currentPage.Draw();

            GUILayout.EndScrollView();
        }

        private void DrawWizardFooter()
        {
            GUILayout.BeginHorizontal();

            // Back button
            EditorGUI.BeginDisabledGroup(AI.Config.wizardCurrentPage <= 0);
            if (GUILayout.Button("Back", GUILayout.Width(60), GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT)))
            {
                NavigateToWizardPage(AI.Config.wizardCurrentPage - 1);
            }
            EditorGUI.EndDisabledGroup();

            GUILayout.FlexibleSpace();

            // Buttons
            if (AI.Config.wizardCurrentPage == 0)
            {
                if (GUILayout.Button("Skip", GUILayout.Width(60), GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT)))
                {
                    CompleteWizard();
                }
            }
            if (AI.Config.wizardCurrentPage < _wizardPages.Count - 1)
            {
                EditorGUI.BeginDisabledGroup(!_wizardPages[AI.Config.wizardCurrentPage].CanProceed);
                if (GUILayout.Button("Next", CommonUIStyles.mainButton, GUILayout.Width(60), GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT)))
                {
                    NavigateToWizardPage(AI.Config.wizardCurrentPage + 1);
                }
                EditorGUI.EndDisabledGroup();
            }
            else
            {
                EditorGUI.BeginDisabledGroup(!_wizardPages[AI.Config.wizardCurrentPage].CanProceed);
                if (GUILayout.Button("Finish", CommonUIStyles.mainButton, GUILayout.Width(60), GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT)))
                {
                    CompleteWizard();
                }
                EditorGUI.EndDisabledGroup();
            }
            EditorGUILayout.Space();
            GUILayout.EndHorizontal();
            EditorGUILayout.Space();
        }

        private void NavigateToWizardPage(int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= _wizardPages.Count) return;

            // Call OnExit for current page
            if (AI.Config.wizardCurrentPage < _wizardPages.Count)
            {
                _wizardPages[AI.Config.wizardCurrentPage].OnExit();
            }

            // set all pages completed up to selected one
            for (int i = 0; i < pageIndex; i++)
            {
                _wizardPages[i].IsCompleted = true;
            }

            AI.Config.wizardCurrentPage = pageIndex;
            SaveWizardState();
            Repaint();
        }

        private void SaveWizardState()
        {
            AI.SaveConfig();
        }

        private void CompleteWizard()
        {
            AI.Config.wizardCompleted = true;
            AI.SaveConfig();
            Repaint();
        }
    }

    public class SetupWizardIntroPage : WizardPage
    {
        public override string Title => "Welcome to Asset Inventory";
        public override string Description => "Asset Inventory is a powerful tool for managing and finding your Unity assets. This setup wizard will guide you through the essential configuration options. All settings can be changed later in the Settings tab.";

        private static readonly Texture2D _sample = CommonUIStyles.LoadTexture("asset-inventory-sample");

        public override void Draw()
        {
            EditorGUILayout.LabelField("Once complete you will be able to search through all your assets as shown below", EditorStyles.boldLabel);

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Box(_sample, EditorStyles.label, GUILayout.MinWidth(250), GUILayout.MaxWidth(750), GUILayout.MinHeight(200), GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }
    }

    public class SetupWizardDownloadPage : WizardPage
    {
        public override string Title => "Download Settings";
        public override string Description => "Asset Inventory can automatically download your purchased assets from the Asset Store for indexing. This ensures all your assets are properly catalogued.";

        public override void Draw()
        {
            EditorGUI.BeginChangeCheck();

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(CommonUIStyles.Content("Download Assets for Indexing", "Automatically download uncached items from the Asset Store for indexing. Will delete them again afterwards if not selected otherwise below. Attention: downloading an item will revoke the right to easily return it through the Asset Store."), EditorStyles.boldLabel, GUILayout.Width(220));
            AI.Actions.DownloadAssets = EditorGUILayout.Toggle(AI.Actions.DownloadAssets, GUILayout.MaxWidth(20));
            GUILayout.EndHorizontal();

            if (AI.Actions.DownloadAssets)
            {
                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Keep Downloaded Assets", "Will not delete automatically downloaded assets after indexing but keep them in the cache instead."), EditorStyles.boldLabel, GUILayout.Width(220));
                AI.Config.keepAutoDownloads = EditorGUILayout.Toggle(AI.Config.keepAutoDownloads, GUILayout.MaxWidth(20));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Limit Package Size", "Will not automatically download packages larger than specified."), EditorStyles.boldLabel, GUILayout.Width(220));
                AI.Config.limitAutoDownloads = EditorGUILayout.Toggle(AI.Config.limitAutoDownloads, GUILayout.MaxWidth(20));
                if (AI.Config.limitAutoDownloads)
                {
                    GUILayout.Label("to", GUILayout.ExpandWidth(false));
                    AI.Config.downloadLimit = EditorGUILayout.DelayedIntField(AI.Config.downloadLimit, GUILayout.Width(50));
                    GUILayout.Label("Mb", GUILayout.ExpandWidth(false));
                }
                GUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(20);
            EditorGUILayout.HelpBox("Note: If you plan to return freshly purchased assets you should deactivate this option since downloading will disallow returns. Also, downloading assets will use bandwidth. Consider your internet connection when enabling this feature in case you are not on an unlimited plan.", MessageType.Warning);

            if (EditorGUI.EndChangeCheck())
            {
                AI.SaveConfig();
            }
        }
    }

    public class SetupWizardLocationsPage : WizardPage
    {
        public override string Title => "Storage Location";
        public override string Description => "Specify where to store the database, cache and backups. While the database will only be a couple of hundred megabytes usually, the cache and backups can easily grow beyond 50-100Gb. The more space you reserve the more performant the tool can operate.";

        internal readonly struct StorageRequirementSample
        {
            public readonly string InventorySize;
            public readonly string Database;
            public readonly string Previews;
            public readonly string ExtractedCache;
            public readonly string SuggestedFree;

            public StorageRequirementSample(string inventorySize, string database, string previews, string extractedCache, string suggestedFree)
            {
                InventorySize = inventorySize;
                Database = database;
                Previews = previews;
                ExtractedCache = extractedCache;
                SuggestedFree = suggestedFree;
            }
        }

        internal static readonly StorageRequirementSample[] StorageRequirementSamples =
        {
            new StorageRequirementSample("100 packages / 25,000 files", "~100 MB", "~1-3 GB", "~5-15 GB", "20+ GB"),
            new StorageRequirementSample("500 packages / 150,000 files", "~500 MB", "~5-15 GB", "~25-75 GB", "100+ GB"),
            new StorageRequirementSample("2,000 packages / 750,000 files", "~2.5 GB", "~25-75 GB", "~100-300 GB", "350+ GB")
        };

        private List<DriveInfo> _drives;
        private Vector2 _driveScrollPosition;

        public override void OnEnter()
        {
            base.OnEnter();
            RefreshDriveInfo();
        }

        private void RefreshDriveInfo()
        {
            _drives = new List<DriveInfo>();
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    if (IsDriveSuitableForStorage(drive))
                    {
                        _drives.Add(drive);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error getting drive information: {e.Message}");
            }
        }

        private bool IsDriveSuitableForStorage(DriveInfo drive)
        {
            if (!drive.IsReady) return false;

            // Filter out unsuitable drive types
            switch (drive.DriveType)
            {
                case DriveType.Fixed:
                    // Fixed drives are good
                    break;
                case DriveType.Removable:
                    // Removable drives are not suitable for database storage
                    return false;
                case DriveType.Network:
                    // Network drives can be slow and unreliable for database operations
                    return false;
                case DriveType.CDRom:
                case DriveType.NoRootDirectory:
                    return false;
                case DriveType.Unknown:
#if UNITY_EDITOR_LINUX
                    // On Linux, local ext4/btrfs/xfs drives often report DriveType.Unknown
                    // instead of Fixed. Allow them and filter by format/path below.
                    break;
#else
                    return false;
#endif
            }

            // Filter out drives with very little space (less than 1GB)
            try
            {
                if (drive.AvailableFreeSpace < 1024L * 1024 * 1024) return false;
            }
            catch (Exception)
            {
                return false;
            }

#if UNITY_EDITOR_LINUX
            string drivePath = drive.RootDirectory.FullName;

            // Skip pseudo-filesystem mount points
            if (drivePath.StartsWith("/proc", StringComparison.Ordinal) ||
                drivePath.StartsWith("/sys", StringComparison.Ordinal) ||
                drivePath.StartsWith("/dev", StringComparison.Ordinal) ||
                drivePath.StartsWith("/run", StringComparison.Ordinal) ||
                drivePath.StartsWith("/snap", StringComparison.Ordinal) ||
                drivePath.StartsWith("/boot", StringComparison.Ordinal))
                return false;

            // Skip by filesystem format
            try
            {
                string format = drive.DriveFormat?.ToLowerInvariant();
                if (format == "tmpfs" || format == "devtmpfs" || format == "proc" ||
                    format == "sysfs" || format == "cgroup" || format == "cgroup2" ||
                    format == "overlay" || format == "squashfs" || format == "ramfs")
                    return false;
            }
            catch (IOException)
            {
                return false;
            }
#endif

            // On macOS, filter out system volumes and temporary mounts
#if UNITY_EDITOR_OSX
            string drivePath = drive.RootDirectory.FullName.ToLowerInvariant();

            // Skip system volumes
            if (drivePath.Contains("/system/") || drivePath.Contains("/volumes/system")) return false;

            // Skip temporary mounts and network volumes
            if (drivePath.Contains("/volumes/.timemachine") ||
                drivePath.Contains("/volumes/.spotlight") ||
                drivePath.Contains("/volumes/.fseventsd") ||
                drivePath.Contains("/volumes/.trashes") ||
                drivePath.Contains("/volumes/.mobilebackups"))
                return false;

            // Skip network drives (usually mounted under /volumes with network paths)
            if (drivePath.Contains("//") || drivePath.Contains("smb://") || drivePath.Contains("afp://"))
                return false;

            // Skip drives that are likely temporary or system-related
            if (drive.VolumeLabel.ToLowerInvariant().Contains("time machine") ||
                drive.VolumeLabel.ToLowerInvariant().Contains("backup") ||
                drive.VolumeLabel.ToLowerInvariant().Contains("system") ||
                drive.VolumeLabel.ToLowerInvariant().Contains("recovery"))
                return false;
#endif

            // On Windows, filter out some system drives
#if UNITY_EDITOR_WIN
            string drivePath = drive.RootDirectory.FullName.ToLowerInvariant();

            // Skip drives that are likely system recovery or temporary
            if (drive.VolumeLabel.ToLowerInvariant().Contains("recovery") ||
                drive.VolumeLabel.ToLowerInvariant().Contains("system") ||
                drive.VolumeLabel.ToLowerInvariant().Contains("temp"))
                return false;
#endif

            return true;
        }

        public override void Draw()
        {
            // Current database location
            string currentDbPath = Paths.GetStorageFolder();
            string currentDbFolder = Path.GetDirectoryName(currentDbPath);

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Current Location:", EditorStyles.boldLabel, GUILayout.Width(120));
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField(currentDbPath, GUILayout.ExpandWidth(true));
            EditorGUI.EndDisabledGroup();
            if (GUILayout.Button("Change...", GUILayout.ExpandWidth(false))) SetDatabaseLocation();
            GUILayout.EndHorizontal();

            DrawStorageEstimateOverview();

            // Drive space overview
            EditorGUILayout.Space(20);
            EditorGUILayout.LabelField("Free Disk Space", EditorStyles.boldLabel);

            if (_drives != null && _drives.Count > 0)
            {
                foreach (DriveInfo drive in _drives)
                {
                    DrawDriveInfo(drive, currentDbFolder);
                }
            }
            else
            {
                EditorGUILayout.HelpBox("No drives found or accessible.", MessageType.Warning);
            }
        }

        private void SetDatabaseLocation()
        {
            string targetFolder = EditorUtility.OpenFolderPanel("Select folder for database and cache", Paths.GetStorageFolder(), "");
            if (string.IsNullOrEmpty(targetFolder)) return;

            // check if same folder selected
            if (IOUtils.IsSameDirectory(targetFolder, Paths.GetStorageFolder())) return;

            // disallow selecting a drive/root directory (e.g., C:\, D:\, E:, or /)
            if (IOUtils.IsRootPath(targetFolder))
            {
                EditorUtility.DisplayDialog("Invalid Folder", "Please select a subfolder, not a drive root.", "OK");
                return;
            }

            // check for existing database
            if (File.Exists(Path.Combine(targetFolder, DBAdapter.DB_NAME)))
            {
                if (EditorUtility.DisplayDialog("Use Existing?", "The target folder contains a database. Switch to this one? Otherwise please select an empty directory.", "Switch", "Cancel"))
                {
                    AI.SwitchDatabase(targetFolder);
                }

                return;
            }

            AI.SwitchDatabase(targetFolder);
            AssetStore.GatherAllMetadata();
            AssetStore.GatherProjectMetadata();
        }

        private void DrawDriveInfo(DriveInfo drive, string currentDbFolder)
        {
            bool isCurrentDrive = currentDbFolder.StartsWith(drive.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase);

            // Calculate free space percentage
            long freeSpace = drive.AvailableFreeSpace;

            // Draw drive info
            GUILayout.BeginVertical("box");

            GUILayout.BeginHorizontal();
            string driveLabel = drive.VolumeLabel;
            if (string.IsNullOrEmpty(driveLabel)) driveLabel = drive.Name;

            if (isCurrentDrive)
            {
                driveLabel += " (Current)";
                EditorGUILayout.LabelField(driveLabel, EditorStyles.boldLabel);
            }
            else
            {
                EditorGUILayout.LabelField(driveLabel, EditorStyles.label);
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(EditorUtility.FormatBytes(freeSpace), EditorStyles.boldLabel, GUILayout.Width(100));
            GUILayout.EndHorizontal();

            // Simple progress bar
            DrawSimpleStorageBar(freeSpace, isCurrentDrive);

            GUILayout.EndVertical();
            EditorGUILayout.Space(5);
        }

        private void DrawSimpleStorageBar(long freeSpace, bool isCurrentDrive)
        {
            Rect rect = EditorGUILayout.GetControlRect(false, 8);

            // Draw background
            EditorGUI.DrawRect(rect, new Color(0.3f, 0.3f, 0.3f));

            // Calculate color based on free space in GB
            long freeSpaceGb = freeSpace / (1024 * 1024 * 1024);
            Color freeColor;
            if (freeSpaceGb > 200)
            {
                freeColor = Color.green;
            }
            else if (freeSpaceGb > 100)
            {
                freeColor = Color.yellow;
            }
            else
            {
                freeColor = Color.red;
            }

            // Draw free space bar (using a simple full-width bar for visual indication)
            EditorGUI.DrawRect(rect, freeColor);
        }

        private static void DrawStorageEstimateOverview()
        {
            EditorGUILayout.Space(15);
            EditorGUILayout.LabelField("Estimated Space Needed", EditorStyles.boldLabel);

            GUILayout.BeginVertical("box");
            foreach (StorageRequirementSample sample in StorageRequirementSamples)
            {
                EditorGUILayout.LabelField(
                    $"{sample.InventorySize}: database {sample.Database}, previews {sample.Previews}, extracted cache {sample.ExtractedCache}, suggested free space {sample.SuggestedFree}.",
                    EditorStyles.wordWrappedLabel);
            }
            GUILayout.EndVertical();

            EditorGUILayout.HelpBox("These are approximate planning numbers. The extracted cache varies most because it depends on package size, accessed packages, kept downloads and preview settings.", MessageType.Info);
        }

        public override bool CanProceed => true;
    }

    public class SetupWizardPreviewPage : WizardPage
    {
        public override string Title => "Display Settings";
        public override string Description => "Configure how Asset Inventory handles preview images and visual content.";

        public override void Draw()
        {
            EditorGUI.BeginChangeCheck();

            // Previews Section
            EditorGUILayout.LabelField("Previews", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);
            
            EditorGUILayout.HelpBox("Previews are extracted from the packages. These are rather small (usually 128x128px). Activating the upscaling will provide larger previews for image files at the cost of additional storage space.", MessageType.Info);
            EditorGUILayout.Space();

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(CommonUIStyles.Content("Upscale Preview Images", "Resize preview images to make them fill a bigger area of the tiles."), EditorStyles.boldLabel, GUILayout.Width(220));
            AI.Config.upscalePreviews = EditorGUILayout.Toggle(AI.Config.upscalePreviews, GUILayout.MaxWidth(20));
            GUILayout.EndHorizontal();

            if (AI.Config.upscalePreviews)
            {
                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content(AI.Config.upscaleLossless ? "Target Size" : "Minimum Size", "Minimum size the preview image should have. Bigger images are not changed."), EditorStyles.boldLabel, GUILayout.Width(220));
                AI.Config.upscaleSize = EditorGUILayout.DelayedIntField(AI.Config.upscaleSize, GUILayout.Width(100));
                EditorGUILayout.LabelField("px", EditorStyles.miniLabel, GUILayout.Width(30));
                GUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(10);
            EditorGUILayout.HelpBox("Sometimes a package does not contain preview images or just generic icons. Automatically creating missing previews after indexing provides visual thumbnails for all assets, which is very beneficial for browsing. However, this can add significant time to the indexing process. If you want a quick overview first, you can turn this off and recreate missing previews later on demand.", MessageType.Info);
            EditorGUILayout.Space(5);

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(CommonUIStyles.Content("Create Previews When Missing", "Automatically generate previews for assets after indexing completes. Recommended for a complete visual experience, but increases indexing time."), EditorStyles.boldLabel, GUILayout.Width(220));
            AI.Config.recreatePreviewsAfterIndexing = EditorGUILayout.Toggle(AI.Config.recreatePreviewsAfterIndexing, GUILayout.MaxWidth(20));
            GUILayout.EndHorizontal();

#if !USE_VECTOR_GRAPHICS
            EditorGUILayout.Space(10);
            EditorGUILayout.HelpBox("In order to see previews for SVG graphics, the 'com.unity.vectorgraphics' package needs to be installed.", MessageType.Warning);
            if (GUILayout.Button("Install Vector Graphics Package"))
            {
                Client.Add("com.unity.vectorgraphics");
            }
#endif
#if !USE_SHADER_GRAPH
            EditorGUILayout.Space(10);
            EditorGUILayout.HelpBox("In order to see previews for Shader Graph assets, the 'com.unity.shadergraph' package needs to be installed.", MessageType.Warning);
            if (GUILayout.Button("Install Shader Graph Package"))
            {
                Client.Add("com.unity.shadergraph");
            }
#endif
#if !USE_TEXTMESHPRO && !UNITY_2023_2_OR_NEWER
            EditorGUILayout.Space(10);
            EditorGUILayout.HelpBox("In order to see previews for TextMeshPro assets, the 'com.unity.textmeshpro' package needs to be installed.", MessageType.Warning);
            if (GUILayout.Button("Install TextMeshPro Package"))
            {
                Client.Add("com.unity.textmeshpro");
            }
#endif
#if USE_TEXTMESHPRO || UNITY_2023_2_OR_NEWER
            if (!TMPStep.AreTMPEssentialsImported())
            {
                EditorGUILayout.Space(10);
                EditorGUILayout.HelpBox("TextMeshPro Essentials need to be imported for full text rendering support. This will add default fonts, shaders, and settings required by TextMeshPro.", MessageType.Warning);
                if (GUILayout.Button("Import TMP Essentials"))
                {
                    TMPStep.ImportEssentials();
                }
            }
#endif

            // Others Section
            EditorGUILayout.Space(20);
            EditorGUILayout.LabelField("Other Settings", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(CommonUIStyles.Content("Preferred Currency", "Currency to show asset prices in."), EditorStyles.boldLabel, GUILayout.Width(220));
            AI.Config.currency = EditorGUILayout.Popup(AI.Config.currency, new[] {"EUR", "USD", "CNY"}, GUILayout.Width(70));
            GUILayout.EndHorizontal();

            if (EditorGUI.EndChangeCheck())
            {
                AI.SaveConfig();
            }
        }
    }

    public class SetupWizardAIPage : WizardPage
    {
        public override string Title => "AI Features (Optional)";
        public override string Description => "Asset Inventory includes AI-powered features that can automatically generate captions for many items. This allows you to search for an 'ambulance' even though the file might be called 'car1.prefab' but showing the ambulance.";

        private static readonly string[] _backendLabels = {"Ollama (recommended)", "LM Studio"};
        private static readonly int[] _backendValues = {1, 2};

        private string _connectionStatus;
        private MessageType _connectionMessageType;
        private int _testGeneration;
        private bool _tested;
        private long _downloadCurrent;
        private long _downloadTotal;

        public override void OnEnter()
        {
            base.OnEnter();
            if (!_tested && AI.Actions.CreateAICaptions) RunAutoTest();
        }

        public override void Draw()
        {
            EditorGUI.BeginChangeCheck();

            AI.Actions.CreateAICaptions = EditorGUILayout.ToggleLeft("Activate AI Captions", AI.Actions.CreateAICaptions);

            if (AI.Actions.CreateAICaptions)
            {
                EditorGUILayout.Space(10);
                EditorGUILayout.LabelField("AI Backend", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox("Choose the local AI backend. Both are free and run entirely on your machine. Ollama is easier to set up. LM Studio offers a graphical interface for managing models.", MessageType.Info);

                int selectedIndex = Array.IndexOf(_backendValues, AI.Config.aiBackend);
                if (selectedIndex < 0) selectedIndex = 0;
                EditorGUI.BeginChangeCheck();
                selectedIndex = GUILayout.SelectionGrid(selectedIndex, _backendLabels, 2);
                if (EditorGUI.EndChangeCheck())
                {
                    AI.Config.aiBackend = _backendValues[selectedIndex];
                    AI.SaveConfig();
                    RunAutoTest();
                }

                EditorGUILayout.Space(10);

                if (AI.Config.aiBackend == 1) DrawOllamaSetup();
                else if (AI.Config.aiBackend == 2) DrawLMStudioSetup();

                EditorGUILayout.Space(5);

                if (!string.IsNullOrEmpty(_connectionStatus))
                {
                    EditorGUILayout.HelpBox(_connectionStatus, _connectionMessageType);
                }

                EditorGUILayout.Space(5);
                EditorGUILayout.HelpBox("AI captions are created per package. Since this will take time, not all packages are enabled for AI captions per default. As a subsequent step go to the Packages tab and activate AI for those packages you want.", MessageType.Info);
            }

            if (EditorGUI.EndChangeCheck())
            {
                AI.SaveConfig();
            }
        }

        private void DrawOllamaSetup()
        {
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Service URL", GUILayout.Width(80));
            EditorGUI.BeginChangeCheck();
            AI.Config.ollamaServiceUrl = EditorGUILayout.DelayedTextField(AI.Config.ollamaServiceUrl, GUILayout.Width(250));
            if (EditorGUI.EndChangeCheck())
            {
                Intelligence.RefreshOllama();
                AI.SaveConfig();
                RunAutoTest();
            }
            GUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Model", GUILayout.Width(80));
            EditorGUI.BeginChangeCheck();
            AI.Config.ollamaModel = EditorGUILayout.DelayedTextField(AI.Config.ollamaModel, GUILayout.Width(150)).Trim();
            if (EditorGUI.EndChangeCheck())
            {
                AI.SaveConfig();
                RunAutoTest();
            }
            if (EditorGUILayout.DropdownButton(CommonUIStyles.Content("Suggested"), FocusType.Keyboard, CommonUIStyles.centerPopup, GUILayout.ExpandWidth(false)))
            {
                GenericMenu menu = new GenericMenu();
                foreach (string model in Intelligence.SuggestedOllamaModels)
                {
                    menu.AddItem(new GUIContent(model), false, () =>
                    {
                        AI.Config.ollamaModel = model.Split(' ')[0];
                        AI.SaveConfig();
                        RunAutoTest();
                    });
                }
                menu.ShowAsContext();
            }
            GUILayout.EndHorizontal();

            if (!string.IsNullOrWhiteSpace(AI.Config.ollamaModel) && Intelligence.IsOllamaInstalled && !Intelligence.OllamaModelDownloaded(AI.Config.ollamaModel))
            {
                EditorGUILayout.Space(3);
                if (Intelligence.DownloadingModel)
                {
                    GUILayout.BeginHorizontal();
                    CommonUIStyles.DrawProgressBar(
                        (float)_downloadCurrent / _downloadTotal,
                        $"{EditorUtility.FormatBytes(_downloadCurrent)}/{EditorUtility.FormatBytes(_downloadTotal)}",
                        GUILayout.MaxWidth(250));
                    if (GUILayout.Button("Cancel", GUILayout.ExpandWidth(false))) Intelligence.OllamaDownloadToken?.Cancel();
                    GUILayout.EndHorizontal();
                }
                else
                {
                    if (GUILayout.Button("Download Model", GUILayout.ExpandWidth(false))) DownloadOllamaModel();
                }
            }

            if (!Intelligence.IsOllamaInstalled && _connectionMessageType != MessageType.None)
            {
                EditorGUILayout.Space(3);
                if (GUILayout.Button("Ollama Website", CommonUIStyles.wrappedLinkLabel, GUILayout.ExpandWidth(false)))
                {
                    AI.OpenURL(Intelligence.OLLAMA_WEBSITE);
                }
            }
        }

        private void DrawLMStudioSetup()
        {
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Service URL", GUILayout.Width(80));
            EditorGUI.BeginChangeCheck();
            AI.Config.lmStudioServiceUrl = EditorGUILayout.DelayedTextField(AI.Config.lmStudioServiceUrl ?? Intelligence.LMSTUDIO_SERVICE_URL, GUILayout.Width(250));
            if (EditorGUI.EndChangeCheck())
            {
                Intelligence.RefreshLMStudio();
                AI.SaveConfig();
                RunAutoTest();
            }
            GUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Model", GUILayout.Width(80));
            EditorGUI.BeginChangeCheck();
            AI.Config.lmStudioModel = EditorGUILayout.DelayedTextField(AI.Config.lmStudioModel ?? string.Empty, GUILayout.Width(250)).Trim();
            if (EditorGUI.EndChangeCheck())
            {
                AI.SaveConfig();
                RunAutoTest();
            }
            if (EditorGUILayout.DropdownButton(CommonUIStyles.Content("Installed"), FocusType.Keyboard, CommonUIStyles.centerPopup, GUILayout.ExpandWidth(false)))
            {
                IEnumerable<LMStudioModel> models = Intelligence.LMStudioModels;
                GenericMenu menu = new GenericMenu();
                if (models != null)
                {
                    foreach (LMStudioModel model in models.Where(m =>
                        !string.IsNullOrEmpty(m.type) &&
                        (m.type.ToLowerInvariant() == "vlm" || m.type.ToLowerInvariant().Contains("vision"))))
                    {
                        menu.AddItem(new GUIContent(model.id), false, () =>
                        {
                            AI.Config.lmStudioModel = model.id;
                            AI.SaveConfig();
                            RunAutoTest();
                        });
                    }
                }
                if (menu.GetItemCount() == 0)
                {
                    menu.AddDisabledItem(new GUIContent("No vision models found"));
                }
                menu.ShowAsContext();
            }
            GUILayout.EndHorizontal();

            if (!Intelligence.IsLMStudioInstalled && _connectionMessageType != MessageType.None)
            {
                EditorGUILayout.Space(3);
                if (GUILayout.Button("LM Studio Website", CommonUIStyles.wrappedLinkLabel, GUILayout.ExpandWidth(false)))
                {
                    AI.OpenURL(Intelligence.LMSTUDIO_WEBSITE);
                }
            }
        }

        private void RunAutoTest()
        {
            _tested = true;
            _connectionStatus = null;
            _testGeneration++;

            if (AI.Config.aiBackend == 1) TestOllamaConnection(_testGeneration);
            else if (AI.Config.aiBackend == 2) TestLMStudioConnection(_testGeneration);
        }

        private async void TestOllamaConnection(int generation)
        {
            _connectionStatus = "Connecting to Ollama...";
            _connectionMessageType = MessageType.None;

            try
            {
                Intelligence.RefreshOllama();
                bool running = await Intelligence.IsOllamaRunningAsync();
                if (generation != _testGeneration) return;

                if (running)
                {
                    string version = await Intelligence.GetOllamaVersionAsync();
                    IEnumerable<ModelInfo> models = await Intelligence.ListOllamaModelsAsync();
                    if (generation != _testGeneration) return;
                    int modelCount = models?.Count() ?? 0;

                    _connectionStatus = $"Connected to Ollama v{version}. {modelCount} model{(modelCount != 1 ? "s" : "")} available.";
                    _connectionMessageType = MessageType.Info;

                    if (modelCount == 0)
                    {
                        _connectionStatus += " Download a model first (e.g. qwen2.5vl:7b).";
                        _connectionMessageType = MessageType.Warning;
                    }
                    else if (!string.IsNullOrWhiteSpace(AI.Config.ollamaModel) && !Intelligence.OllamaModelDownloaded(AI.Config.ollamaModel))
                    {
                        _connectionStatus += $" Model '{AI.Config.ollamaModel}' is not downloaded yet.";
                        _connectionMessageType = MessageType.Warning;
                    }
                }
                else
                {
                    _connectionStatus = "Ollama is not running. Start the Ollama application and try again.";
                    _connectionMessageType = MessageType.Error;
                }
            }
            catch (Exception e)
            {
                if (generation != _testGeneration) return;
                _connectionStatus = $"Connection failed: {e.Message}";
                _connectionMessageType = MessageType.Error;
            }
        }

        private async void TestLMStudioConnection(int generation)
        {
            _connectionStatus = "Connecting to LM Studio...";
            _connectionMessageType = MessageType.None;

            try
            {
                Intelligence.RefreshLMStudio();
                await System.Threading.Tasks.Task.Delay(1500);
                if (generation != _testGeneration) return;

                if (Intelligence.IsLMStudioInstalled)
                {
                    string version = Intelligence.LMStudioVersion;
                    IEnumerable<LMStudioModel> models = Intelligence.LMStudioModels;
                    int visionCount = models?.Count(m =>
                        !string.IsNullOrEmpty(m.type) &&
                        (m.type.ToLowerInvariant() == "vlm" || m.type.ToLowerInvariant().Contains("vision"))) ?? 0;

                    _connectionStatus = $"Connected to LM Studio{(version != null ? $" v{version}" : "")}. {visionCount} vision model{(visionCount != 1 ? "s" : "")} available.";
                    _connectionMessageType = MessageType.Info;

                    if (visionCount == 0)
                    {
                        _connectionStatus += " Load a vision model (VLM) in LM Studio first.";
                        _connectionMessageType = MessageType.Warning;
                    }
                }
                else
                {
                    _connectionStatus = "LM Studio is not running or the local server is not started. Enable it in LM Studio and try again.";
                    _connectionMessageType = MessageType.Error;
                }
            }
            catch (Exception e)
            {
                if (generation != _testGeneration) return;
                _connectionStatus = $"Connection failed: {e.Message}";
                _connectionMessageType = MessageType.Error;
            }
        }

        private void DownloadOllamaModel()
        {
            _downloadCurrent = 0;
            _downloadTotal = 0;
            string modelName = AI.Config.ollamaModel;
            Task.Run(async () =>
            {
                await Intelligence.PullOllamaModelAsync(modelName, status =>
                {
                    _downloadCurrent = status.Completed;
                    _downloadTotal = status.Total;
                });
                RunAutoTest();
            });
        }
    }

    public class SetupWizardUIPage : WizardPage
    {
        public override string Title => "Advanced Features";
        public override string Description => "How to show advanced features.";

        private static readonly Texture2D _sample = CommonUIStyles.LoadTexture("asset-inventory-sample2");


        public override void Draw()
        {
            EditorGUILayout.HelpBox("To simplify everyday usage, Asset Inventory will only show a subset of the features.", MessageType.Info);
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("To switch between easy and advanced mode, use the eye icon in the top upper right corner or temporarily hold down the CTRL key.", EditorStyles.wordWrappedLabel);

            EditorGUILayout.Space();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Box(_sample, EditorStyles.label, GUILayout.MinWidth(250), GUILayout.MaxWidth(650), GUILayout.MinHeight(200), GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            EditorGUILayout.Space();
        }
    }

    public class SetupWizardCompletionPage : WizardPage
    {
        public override string Title => "Setup Done";
        public override string Description => "All steps completed!";

        private int _assetsFileCount = -1;

        public override void OnEnter()
        {
            base.OnEnter();
            CountAssetsFiles();
            if (!AI.Actions.AnyActionsInProgress)
            {
                AI.Config.quickIndexingDone = false;
                AI.Actions.RunActions();
            }
        }

        private void CountAssetsFiles()
        {
            try
            {
                string assetsPath = Application.dataPath;
                if (Directory.Exists(assetsPath))
                {
                    _assetsFileCount = IOUtils.GetFilesSafe(assetsPath, "*.*", SearchOption.AllDirectories).Count();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Could not count files in Assets directory: {e.Message}");
                _assetsFileCount = -1;
            }
        }

        public override void Draw()
        {
            EditorGUILayout.HelpBox("You have successfully configured Asset Inventory. The tool is now ready to use in all your Unity projects.", MessageType.Info);
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("What's Next:", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("1. Indexing of a part of your assets has already been triggered in the background", CommonUIStyles.ColoredText(Color.green, true));
            EditorGUILayout.LabelField("2. Update your index regularly by clicking 'Run Actions' on the Settings tab", EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("3. Check out the tutorials for advanced features", EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("4. Have a great new asset management experience!", EditorStyles.wordWrappedLabel);
#if !USE_TUTORIALS
            EditorGUILayout.Space();
            if (GUILayout.Button("Install Getting-Started Tutorials", GUILayout.ExpandWidth(false)))
            {
                Client.Add($"com.unity.learn.iet-framework@{AI.TUTORIALS_VERSION}");
            }
#endif
            // Show warning if there are more than 100 files in Assets directory
            if (_assetsFileCount > 1500)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(
                    $"Your project contains quite many files already which might slow down the initial indexing. " +
                    "For optimal performance, it is recommended to run Asset Inventory in an empty project first. " +
                    "This will significantly improve indexing speed." +
                    "The database is shared between projects.",
                    MessageType.Warning);
            }
        }

        public override bool CanProceed => true;
    }
}
