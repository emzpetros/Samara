using Automator;
using ImpossibleRobert.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    public sealed class ExportUI : BasicEditorUI
    {
        private const string REMAINING_EXTENSIONS = "All the Rest";
        private const string TEMP_FOLDER = "AITemplateCache";
        private const int TILE_SIZE = 160;
        private const int TILE_HEIGHT = 130;
        private const int ICON_SIZE = 48;
        private const int TILE_SPACING = 12;

        // Polished tile styling
        private static GUIStyle _tileStyle;
        private static GUIStyle _tileTitleStyle;
        private static GUIStyle _tileDescStyle;
        private static Texture2D _tileBgNormal;
        private static Texture2D _tileBgHover;

        private string _separator = ";";
        private Vector2 _scrollPos;
        private bool _fileMode;
        private List<AssetInfo> _assets;
        private List<ED> _exportFields;
        private List<ED> _overrideFields;
        private List<ED> _exportTypes;
        private int _selectedExportOption;
        private bool _addHeader = true;
        private bool _showFields = true;
        private bool _clearTarget;
        private bool _overrideExisting;
        private List<AssetInfo> _packages;
        private int _packageCount;
        private bool _exportInProgress;
        private List<string> _exportableExtensions;
        private int _curProgress;
        private int _maxProgress;
        private ActionProgress _progress;
        private bool _autoDownload;
        private bool _flattenStructure;
        private bool _metaFiles;

        // Wizard related fields
        private bool _wizardActive = true;
        private List<ExportTypeInfo> _exportTypeInfos;
        private Vector2 _wizardScrollPos;

        private FileSystemWatcher _watcher;
        private bool _triggerExport;
        private int _selectedTemplate;
        private string _templateFolder;
        private List<TemplateInfo> _templates;
        private string[] _templateNames;
        private string _overridesFolder;
        private List<string> _overrideCandidates;

        public void OnEnable()
        {
            LoadTemplates();
            PrepareOverrides();

            EditorApplication.update += () =>
            {
                if (_triggerExport) ExportTemplate();
            };

            // Initialize export type infos with descriptions and icons
            InitExportTypeInfos();
        }

        public void OnDisable()
        {
            if (_watcher != null) StopTemplateWatcher();
        }

        private void LoadTemplates()
        {
            _templates = TemplateUtils.LoadTemplates();
            _templateFolder = TemplateUtils.GetTemplateRootFolder();
            _templateNames = _templates.Select(t => t.name).ToArray();
        }

        public static ExportUI ShowWindow()
        {
            ExportUI window = GetWindow<ExportUI>("Asset Export");
            window.minSize = new Vector2(500, 320);

            return window;
        }

        public void Init(List<AssetInfo> assets, bool fileMode = false, int exportType = 0, int[] columns = null)
        {
            _fileMode = fileMode;
            _assets = assets;
            if (!_fileMode) _assets = _assets.Where(a => a.SafeName != Asset.NONE).ToList();

            _packages = assets.GroupBy(a => a.AssetId).Select(a => a.First()).ToList(); // cast to list to make it serializable during script reloads
            _packageCount = _packages.Count;
            _wizardActive = !_fileMode; // only one type supported right now
            if (_fileMode) _flattenStructure = true;

            _exportableExtensions = AI.TypeGroups.SelectMany(tg => tg.Value).ToList();

            _selectedExportOption = exportType;
            _exportFields = new List<ED>
            {
                new ED("Asset/Id"),
                new ED("Asset/ParentId"),
                new ED("Asset/ForeignId", false, IsVisibleColumn(columns, AssetTreeViewControl.Columns.ForeignId)),
                new ED("Asset/AssetRating", true, IsVisibleColumn(columns, AssetTreeViewControl.Columns.Rating)),
                new ED("Asset/AssetSource", true, IsVisibleColumn(columns, AssetTreeViewControl.Columns.Source)),
                new ED("Asset/AssetLink", false),
                new ED("Asset/Backup", false, IsVisibleColumn(columns, AssetTreeViewControl.Columns.Backup)),
                new ED("Asset/BIRPCompatible", false, IsVisibleColumn(columns, AssetTreeViewControl.Columns.BIRP)),
                new ED("Asset/CompatibilityInfo", false),
                new ED("Asset/CurrentState", false, IsVisibleColumn(columns, AssetTreeViewControl.Columns.InternalState)),
                new ED("Asset/CurrentSubState", false),
                new ED("Asset/Description", false),
                new ED("Asset/DisplayCategory", true, IsVisibleColumn(columns, AssetTreeViewControl.Columns.Category)),
                new ED("Asset/DisplayName", true, IsVisibleColumn(columns, AssetTreeViewControl.Columns.Name)),
                new ED("Asset/DisplayPublisher", true, IsVisibleColumn(columns, AssetTreeViewControl.Columns.Publisher)),
                new ED("Asset/ETag", false),
                new ED("Asset/Exclude", false, IsVisibleColumn(columns, AssetTreeViewControl.Columns.Exclude)),
                new ED("Asset/Extract", false, IsVisibleColumn(columns, AssetTreeViewControl.Columns.Extract)),
                new ED("Asset/FirstRelease", false, IsVisibleColumn(columns, AssetTreeViewControl.Columns.ReleaseDate)),
                new ED("Asset/HDRPCompatible", false, IsVisibleColumn(columns, AssetTreeViewControl.Columns.HDRP)),
                new ED("Asset/Hotness", false, IsVisibleColumn(columns, AssetTreeViewControl.Columns.Popularity)),
                new ED("Asset/IsHidden", false),
                new ED("Asset/KeyFeatures", false),
                new ED("Asset/Keywords"),
                new ED("Asset/LastOnlineRefresh", false),
                new ED("Asset/LastRelease", true, IsVisibleColumn(columns, AssetTreeViewControl.Columns.UpdateDate)),
                new ED("Asset/LatestVersion", true, IsVisibleColumn(columns, AssetTreeViewControl.Columns.Version)),
                new ED("Asset/License", true, IsVisibleColumn(columns, AssetTreeViewControl.Columns.License)),
                new ED("Asset/LicenseLocation", false),
                new ED("Asset/Location", true, IsVisibleColumn(columns, AssetTreeViewControl.Columns.Location)),
                new ED("Asset/OriginalLocation", false),
                new ED("Asset/PackageSize", false, IsVisibleColumn(columns, AssetTreeViewControl.Columns.Size)),
                new ED("Asset/PackageSource"),
                new ED("Asset/PackageTags", true, IsVisibleColumn(columns, AssetTreeViewControl.Columns.Tags)),
                new ED("Asset/PriceEur", false, IsVisibleColumn(columns, AssetTreeViewControl.Columns.Price)),
                new ED("Asset/PriceUsd", false, IsVisibleColumn(columns, AssetTreeViewControl.Columns.Price)),
                new ED("Asset/PriceCny", false, IsVisibleColumn(columns, AssetTreeViewControl.Columns.Price)),
                new ED("Asset/PurchaseDate", true, IsVisibleColumn(columns, AssetTreeViewControl.Columns.PurchaseDate)),
                new ED("Asset/RatingCount", true, IsVisibleColumn(columns, AssetTreeViewControl.Columns.RatingCount)),
                new ED("Asset/Registry", false),
                new ED("Asset/ReleaseNotes", false),
                new ED("Asset/Repository", false),
                new ED("Asset/Revision"),
                new ED("Asset/SafeCategory"),
                new ED("Asset/SafeName"),
                new ED("Asset/SafePublisher"),
                new ED("Asset/Slug", false),
                new ED("Asset/SupportedUnityVersions", true, IsVisibleColumn(columns, AssetTreeViewControl.Columns.UnityVersions)),
                new ED("Asset/UpdateStrategy", false),
                new ED("Asset/URPCompatible", false, IsVisibleColumn(columns, AssetTreeViewControl.Columns.URP)),
                new ED("Asset/UseAI", false, IsVisibleColumn(columns, AssetTreeViewControl.Columns.AICaptions)),
                new ED("Asset/Version", true, IsVisibleColumn(columns, AssetTreeViewControl.Columns.Version))
            };
            LoadCSVSettings();

            _overrideFields = new List<ED>
            {
                new ED("Asset/AssetRating", false),
                new ED("Asset/BIRPCompatible", false),
                new ED("Asset/CompatibilityInfo", false),
                new ED("Asset/Description", false),
                new ED("Asset/DisplayCategory", false),
                new ED("Asset/DisplayName", false),
                new ED("Asset/DisplayPublisher", false),
                new ED("Asset/FirstRelease", false),
                new ED("Asset/ForeignId", false),
                new ED("Asset/HDRPCompatible", false),
                new ED("Asset/Hotness", false),
                new ED("Asset/KeyFeatures", false),
                new ED("Asset/Keywords", false),
                new ED("Asset/LastRelease", false),
                new ED("Asset/LatestVersion", false),
                new ED("Asset/License", false),
                new ED("Asset/LicenseLocation", false),
                new ED("Asset/PackageTags", false),
                new ED("Asset/PriceEur", false),
                new ED("Asset/PriceUsd", false),
                new ED("Asset/PriceCny", false),
                new ED("Asset/PurchaseDate", false),
                new ED("Asset/RatingCount", false),
                new ED("Asset/Registry", false),
                new ED("Asset/ReleaseNotes", false),
                new ED("Asset/Repository", false),
                new ED("Asset/Revision", false),
                new ED("Asset/SafeCategory", false),
                new ED("Asset/SafePublisher", false),
                new ED("Asset/Slug", false),
                new ED("Asset/SupportedUnityVersions", false),
                new ED("Asset/URPCompatible", false),
                new ED("Asset/Version", false)
            };
            _exportTypes = new List<ED>
            {
                new ED(AI.AssetGroup.Audio.ToString()),
                new ED(AI.AssetGroup.Images.ToString()),
                new ED(AI.AssetGroup.Videos.ToString()),
                new ED(AI.AssetGroup.Models.ToString()),
                new ED(AI.AssetGroup.Documents.ToString(), false),
                new ED(AI.AssetGroup.Scripts.ToString(), false),
                new ED(AI.AssetGroup.Shaders.ToString(), false),
                new ED(AI.AssetGroup.Animations.ToString(), false),
                new ED(REMAINING_EXTENSIONS, false)
            };

            // Initialize export type infos with descriptions and icons
            InitExportTypeInfos();
        }

        private void InitExportTypeInfos()
        {
            // Define export type information with descriptions and compact icons
            _exportTypeInfos = new List<ExportTypeInfo>
            {
                new ExportTypeInfo(
                    0,
                    "CSV Export",
                    "Export metadata to CSV for reports and spreadsheets.",
                    CreateCompactIcon("CSV Export", new Color(0.4f, 0.6f, 0.9f))),

                new ExportTypeInfo(
                    4,
                    "Template Export",
                    "Generate documentation or catalogs using templates.",
                    CreateCompactIcon("Template Export", new Color(0.9f, 0.4f, 0.6f))),

                new ExportTypeInfo(
                    2,
                    "Asset Export",
                    "Export actual asset files to an external folder.",
                    CreateCompactIcon("Asset Export", new Color(0.4f, 0.8f, 0.5f))),

                new ExportTypeInfo(
                    1,
                    "License Export",
                    "Generate Markdown with license info for all packages.",
                    CreateCompactIcon("License Export", new Color(0.7f, 0.5f, 0.9f))),

                new ExportTypeInfo(
                    3,
                    "Package Override",
                    "Create JSON files to customize package metadata.",
                    CreateCompactIcon("Package Override", new Color(0.9f, 0.7f, 0.4f)))
            };
        }

        /// <summary>
        /// Creates a compact 48x48 icon for an export type
        /// </summary>
        private Texture2D CreateCompactIcon(string title, Color accentColor)
        {
            int size = ICON_SIZE;
            Texture2D texture = new Texture2D(size, size);
            texture.hideFlags = HideFlags.HideAndDontSave;

            // Theme-aware colors
            bool isDark = EditorGUIUtility.isProSkin;
            Color bgColor = isDark ? new Color(0.22f, 0.22f, 0.22f) : new Color(0.85f, 0.85f, 0.85f);
            Color fgColor = isDark ? new Color(0.75f, 0.75f, 0.75f) : new Color(0.3f, 0.3f, 0.3f);
            Color accent = Color.Lerp(accentColor, fgColor, 0.3f);

            // Fill background
            Color[] pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = bgColor;
            texture.SetPixels(pixels);

            // Draw icon based on export type
            if (title.Contains("CSV"))
            {
                // Grid/table icon (3x3 cells)
                DrawFilledRect(texture, 8, 8, 32, 32, accent);
                // Grid lines
                for (int x = 8; x <= 40; x++) { texture.SetPixel(x, 18, bgColor); texture.SetPixel(x, 28, bgColor); }
                for (int y = 8; y <= 40; y++) { texture.SetPixel(18, y, bgColor); texture.SetPixel(28, y, bgColor); }
            }
            else if (title.Contains("License"))
            {
                // Document with checkmark
                DrawFilledRect(texture, 12, 6, 24, 36, accent);
                // Checkmark
                DrawLine(texture, 18, 20, 22, 16, fgColor, 2);
                DrawLine(texture, 22, 16, 30, 28, fgColor, 2);
            }
            else if (title.Contains("Asset"))
            {
                // Folder icon
                DrawFilledRect(texture, 6, 10, 36, 26, accent);
                DrawFilledRect(texture, 6, 32, 14, 6, accent); // Tab
            }
            else if (title.Contains("Override"))
            {
                // JSON curly braces
                DrawCurlyBrace(texture, 14, 8, 32, true, accent);
                DrawCurlyBrace(texture, 34, 8, 32, false, accent);
            }
            else if (title.Contains("Template"))
            {
                // Document with code brackets <>
                DrawFilledRect(texture, 10, 6, 28, 36, accent);
                // < bracket
                DrawLine(texture, 20, 18, 16, 24, fgColor, 2);
                DrawLine(texture, 16, 24, 20, 30, fgColor, 2);
                // > bracket
                DrawLine(texture, 28, 18, 32, 24, fgColor, 2);
                DrawLine(texture, 32, 24, 28, 30, fgColor, 2);
            }

            texture.Apply();
            return texture;
        }

        private void DrawFilledRect(Texture2D tex, int x, int y, int w, int h, Color color)
        {
            for (int py = y; py < y + h && py < tex.height; py++)
                for (int px = x; px < x + w && px < tex.width; px++)
                    tex.SetPixel(px, py, color);
        }

        private void DrawLine(Texture2D tex, int x1, int y1, int x2, int y2, Color color, int thickness = 1)
        {
            int dx = Mathf.Abs(x2 - x1), sx = x1 < x2 ? 1 : -1;
            int dy = -Mathf.Abs(y2 - y1), sy = y1 < y2 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                for (int t = -thickness / 2; t <= thickness / 2; t++)
                {
                    if (x1 + t >= 0 && x1 + t < tex.width) tex.SetPixel(x1 + t, y1, color);
                    if (y1 + t >= 0 && y1 + t < tex.height) tex.SetPixel(x1, y1 + t, color);
                }
                if (x1 == x2 && y1 == y2) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x1 += sx; }
                if (e2 <= dx) { err += dx; y1 += sy; }
            }
        }

        private void DrawCurlyBrace(Texture2D tex, int x, int y, int height, bool openBrace, Color color)
        {
            int dir = openBrace ? -1 : 1;
            int midY = y + height / 2;
            // Top curve
            for (int i = 0; i < 6; i++) tex.SetPixel(x + dir * (3 - Mathf.Abs(i - 3)), y + i, color);
            // Top vertical
            for (int i = 6; i < height / 2 - 3; i++) tex.SetPixel(x, y + i, color);
            // Middle point
            for (int i = -3; i <= 3; i++) tex.SetPixel(x + dir * (3 - Mathf.Abs(i)), midY + i, color);
            // Bottom vertical
            for (int i = height / 2 + 3; i < height - 6; i++) tex.SetPixel(x, y + i, color);
            // Bottom curve
            for (int i = 0; i < 6; i++) tex.SetPixel(x + dir * (3 - Mathf.Abs(i - 3)), y + height - 6 + i, color);
        }

        private void EnsureTileStyles()
        {
            if (_tileStyle != null) return;

            bool isDark = EditorGUIUtility.isProSkin;

            // Normal tile background
            Color normalBg = isDark ? new Color(0.22f, 0.22f, 0.22f) : new Color(0.78f, 0.78f, 0.78f);
            _tileBgNormal = MakeTex(normalBg);

            // Hover tile background
            Color hoverBg = isDark ? new Color(0.28f, 0.28f, 0.30f) : new Color(0.72f, 0.72f, 0.74f);
            _tileBgHover = MakeTex(hoverBg);

            _tileStyle = new GUIStyle()
            {
                normal = { background = _tileBgNormal },
                hover = { background = _tileBgHover },
                border = new RectOffset(4, 4, 4, 4),
                padding = new RectOffset(8, 8, 8, 8),
                margin = new RectOffset(0, 0, 0, 0)
            };

            _tileTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11,
                clipping = TextClipping.Clip
            };

            _tileDescStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.UpperCenter,
                wordWrap = true,
                fontSize = 10,
                normal = { textColor = isDark ? new Color(0.6f, 0.6f, 0.6f) : new Color(0.4f, 0.4f, 0.4f) }
            };
        }

        private static Texture2D MakeTex(Color color)
        {
            Texture2D tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.SetPixel(0, 0, color);
            tex.Apply(false, true);
            return tex;
        }

        private bool IsVisibleColumn(int[] columns, AssetTreeViewControl.Columns column)
        {
            return columns != null && columns.Contains((int)column);
        }

        public override void OnGUI()
        {
            if (_assets == null || _assets.Count == 0)
            {
                Close();
                return;
            }

            if (_wizardActive)
            {
                DrawWizard();
            }
            else
            {
                DrawExportOptions();
            }
        }

        private void DrawWizard()
        {
            EnsureTileStyles();

            GUILayout.Space(8);

            // Header
            GUILayout.Label("Select Export Type", CommonUIStyles.centerLabel);
            GUILayout.Space(8);

            // Create scrollview for the export type grid
            _wizardScrollPos = EditorGUILayout.BeginScrollView(_wizardScrollPos);

            // Get available width
            float availableWidth = EditorGUIUtility.currentViewWidth - 20;

            // Calculate the number of tiles per row based on width
            int tilesPerRow = Mathf.Max(1, Mathf.FloorToInt(availableWidth / (TILE_SIZE + TILE_SPACING)));

            // Calculate centered left margin
            float totalRowWidth = tilesPerRow * TILE_SIZE + (tilesPerRow - 1) * TILE_SPACING;
            float leftMargin = Mathf.Max(10, (availableWidth - totalRowWidth) / 2);

            // Start horizontal layout for centering
            GUILayout.BeginHorizontal();
            GUILayout.Space(leftMargin);

            // Draw the export type tiles in a grid
            GUILayout.BeginVertical();

            int count = 0;
            for (int i = 0; i < _exportTypeInfos.Count; i++)
            {
                if (count % tilesPerRow == 0)
                {
                    if (count > 0)
                    {
                        GUILayout.EndHorizontal();
                        GUILayout.Space(TILE_SPACING);
                    }
                    GUILayout.BeginHorizontal();
                }

                // Draw the tile
                if (DrawExportTypeTile(_exportTypeInfos[i]))
                {
                    _selectedExportOption = _exportTypeInfos[i].Index;
                    _wizardActive = false;
                }

                // Add horizontal spacing between tiles in the same row
                if ((count % tilesPerRow) < tilesPerRow - 1 && i < _exportTypeInfos.Count - 1)
                {
                    GUILayout.Space(TILE_SPACING);
                }

                count++;
            }

            // End the last row
            if (_exportTypeInfos.Count > 0)
            {
                GUILayout.EndHorizontal();
            }

            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            EditorGUILayout.EndScrollView();

            GUILayout.Space(8);
        }

        private bool DrawExportTypeTile(ExportTypeInfo info)
        {
            bool wasClicked = false;

            // Compact tile layout
            float padding = 8f;
            float innerWidth = TILE_SIZE - (padding * 2);
            float iconSize = ICON_SIZE;
            float titleHeight = 18f;
            float descriptionHeight = 36f;
            float iconTopPadding = 6f;

            float totalHeight = TILE_HEIGHT;

            // Get the rect for the entire tile
            Rect tileRect = GUILayoutUtility.GetRect(TILE_SIZE, totalHeight, GUILayout.Width(TILE_SIZE), GUILayout.ExpandWidth(false));

            // Detect hover
            bool isHovered = tileRect.Contains(Event.current.mousePosition);

            // Request repaint on mouse move for smooth hover
            if (Event.current.type == EventType.MouseMove)
            {
                GUI.changed = true;
            }

            // Draw tile background with hover effect
            if (Event.current.type == EventType.Repaint)
            {
                Texture2D bgTex = isHovered ? _tileBgHover : _tileBgNormal;
                if (bgTex != null)
                {
                    GUI.DrawTexture(tileRect, bgTex);
                }
                else
                {
                    GUI.Box(tileRect, GUIContent.none, GUI.skin.box);
                }

                // Draw subtle border
                bool isDark = EditorGUIUtility.isProSkin;
                Color borderColor = isDark ? new Color(0.15f, 0.15f, 0.15f) : new Color(0.6f, 0.6f, 0.6f);
                DrawBorder(tileRect, borderColor, 1);

                // Highlight border on hover
                if (isHovered)
                {
                    Color hoverBorder = isDark ? new Color(0.4f, 0.5f, 0.7f) : new Color(0.3f, 0.4f, 0.6f);
                    DrawBorder(tileRect, hoverBorder, 1);
                }
            }

            // Change cursor on hover
            EditorGUIUtility.AddCursorRect(tileRect, MouseCursor.Link);

            // Check for clicks on the entire tile
            if (Event.current.type == EventType.MouseDown && tileRect.Contains(Event.current.mousePosition))
            {
                wasClicked = true;
                Event.current.Use();
            }

            // Calculate inner rects - icon centered at top, then title, then description
            float iconX = tileRect.x + (TILE_SIZE - iconSize) / 2;
            float iconY = tileRect.y + padding + iconTopPadding;
            Rect iconRect = new Rect(iconX, iconY, iconSize, iconSize);

            Rect titleRect = new Rect(tileRect.x + padding, iconRect.yMax + 6f, innerWidth, titleHeight);
            Rect descriptionRect = new Rect(tileRect.x + padding, titleRect.yMax + 2f, innerWidth, descriptionHeight);

            // Draw icon
            if (info.Icon != null)
            {
                GUI.DrawTexture(iconRect, info.Icon, ScaleMode.ScaleToFit);
            }

            // Draw title (centered bold)
            GUI.Label(titleRect, info.Name, _tileTitleStyle);

            // Draw description (centered grey mini)
            GUI.Label(descriptionRect, info.Description, _tileDescStyle);

            return wasClicked;
        }

        private void DrawBorder(Rect rect, Color color, int thickness)
        {
            // Top
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, thickness), color);
            // Bottom
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
            // Left
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, thickness, rect.height), color);
            // Right
            EditorGUI.DrawRect(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
        }

        private void DrawExportOptions()
        {
            int labelWidth = 110;
            EditorGUI.BeginDisabledGroup(_exportInProgress);

            if (!_fileMode)
            {
                // Back button
                GUILayout.Space(5);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(EditorGUIUtility.IconContent("d_back@2x"), GUILayout.Width(24), GUILayout.Height(22)))
                {
                    _wizardActive = true;
                }
                EditorGUILayout.LabelField($"{_exportTypeInfos.First(e => e.Index == _selectedExportOption).Name}", EditorStyles.boldLabel);
                GUILayout.EndHorizontal();

                EditorGUILayout.Space(10);
            }

            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel, GUILayout.MaxWidth(labelWidth));
            if (_fileMode)
            {
                EditorGUILayout.LabelField($"{_assets.Count:N0} files", EditorStyles.label);
            }
            else
            {
                if (_packageCount == 1)
                {
                    EditorGUILayout.LabelField($"Custom Selection ({_assets.First().GetDisplayName()})");
                }
                else
                {
                    EditorGUILayout.LabelField($"Custom Selection ({_packageCount} packages)");
                }
            }
            GUILayout.EndHorizontal();

            switch (_selectedExportOption)
            {
                case 0:
                    EditorGUI.BeginChangeCheck();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Header Line", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    _addHeader = EditorGUILayout.Toggle(_addHeader);
                    GUILayout.EndHorizontal();

                    EditorGUILayout.Space();
                    _showFields = EditorGUILayout.BeginFoldoutHeaderGroup(_showFields, "Fields");
                    if (_showFields)
                    {
                        EditorGUILayout.Space();
                        GUILayout.BeginHorizontal();
                        if (GUILayout.Button("Select All"))
                        {
                            _exportFields.ForEach(f => f.isSelected = true);
                            SaveCSVSettings();
                        }
                        if (GUILayout.Button("Select None"))
                        {
                            _exportFields.ForEach(f => f.isSelected = false);
                            SaveCSVSettings();
                        }
                        if (GUILayout.Button("Select Default"))
                        {
                            _exportFields.ForEach(f => f.isSelected = f.isDefault);
                            SaveCSVSettings();
                        }
                        if (GUILayout.Button("Select Visible Columns"))
                        {
                            _exportFields.ForEach(f => f.isSelected = f.isVisibleColumn);
                            SaveCSVSettings();
                        }
                        GUILayout.EndHorizontal();
                        EditorGUILayout.Space();

                        _scrollPos = GUILayout.BeginScrollView(_scrollPos, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.ExpandWidth(true));
                        foreach (ED ed in _exportFields)
                        {
                            GUILayout.BeginHorizontal();
                            ed.isSelected = EditorGUILayout.Toggle(ed.isSelected, GUILayout.Width(20));
                            EditorGUILayout.LabelField(ed.field);
                            GUILayout.EndHorizontal();
                        }
                        GUILayout.EndScrollView();
                    }
                    EditorGUILayout.EndFoldoutHeaderGroup();
                    if (EditorGUI.EndChangeCheck())
                    {
                        SaveCSVSettings();
                    }
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Export...", CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT))) ExportCSV();
                    break;

                case 1:
                    EditorGUILayout.Space();
                    EditorGUILayout.HelpBox("The export will only include information about packages that actually contain license data.", MessageType.Info);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Export...", CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT))) ExportLicenses();
                    break;

                case 2:
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Clear Target", "Deletes any previously existing export for the specific package, otherwise only copies new files."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    _clearTarget = EditorGUILayout.Toggle(_clearTarget);
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Flatten", "Put all files in the target folder directly independent of the sub-folders they are contained in."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    _flattenStructure = EditorGUILayout.Toggle(_flattenStructure);
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Download", "Triggers download of package automatically in case it is not available yet in the cache."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    _autoDownload = EditorGUILayout.Toggle(_autoDownload);
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Meta Files", "Exports also meta files if they exist."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    _metaFiles = EditorGUILayout.Toggle(_metaFiles);
                    GUILayout.EndHorizontal();

                    if (!_fileMode)
                    {
                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField("File Types", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                        if (GUILayout.Button("Typical", GUILayout.ExpandWidth(false))) _exportTypes.ForEach(et => et.isSelected = et.isDefault);
                        if (GUILayout.Button("All", GUILayout.ExpandWidth(false))) _exportTypes.ForEach(et => et.isSelected = true);
                        if (GUILayout.Button("None", GUILayout.ExpandWidth(false))) _exportTypes.ForEach(et => et.isSelected = false);
                        GUILayout.EndHorizontal();

                        int typeWidth = 70;
                        for (int i = 0; i < _exportTypes.Count; i++)
                        {
                            // show always three items per row
                            if (i % 3 == 0)
                            {
                                GUILayout.BeginHorizontal();
                                GUILayout.Space(117);
                            }
                            _exportTypes[i].isSelected = EditorGUILayout.Toggle(_exportTypes[i].isSelected, GUILayout.Width(20));
                            EditorGUILayout.LabelField(_exportTypes[i].pointer, GUILayout.Width(typeWidth));
                            if (i % 3 == 2 || i == _exportTypes.Count - 1) GUILayout.EndHorizontal();
                        }
                    }

                    GUILayout.FlexibleSpace();
                    EditorGUILayout.HelpBox("Make sure you own the appropriate rights in case you intend to use assets in other contexts than Unity!", MessageType.Warning);
                    if (_exportInProgress) CommonUIStyles.DrawProgressBar((float)_curProgress / _maxProgress, $"{_curProgress}/{_maxProgress}");
                    if (GUILayout.Button(_exportInProgress ? "Export in progress..." : "Export...", CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT)))
                    {
                        ExportAssets();
                    }
                    break;

                case 3:
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Override Existing", ""), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    _overrideExisting = EditorGUILayout.Toggle(_overrideExisting);
                    GUILayout.EndHorizontal();

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Fields to override", EditorStyles.boldLabel);
                    EditorGUILayout.Space();
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Select All")) _overrideFields.ForEach(f => f.isSelected = true);
                    if (GUILayout.Button("Select None")) _overrideFields.ForEach(f => f.isSelected = false);
                    GUILayout.EndHorizontal();
                    EditorGUILayout.Space();

                    _scrollPos = GUILayout.BeginScrollView(_scrollPos, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.ExpandWidth(true));
                    foreach (ED ed in _overrideFields)
                    {
                        GUILayout.BeginHorizontal();
                        ed.isSelected = EditorGUILayout.Toggle(ed.isSelected, GUILayout.Width(20));
                        EditorGUILayout.LabelField(ed.field);
                        GUILayout.EndHorizontal();
                    }
                    GUILayout.EndScrollView();

                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(_exportInProgress ? "Export in progress..." : "Export", CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT))) ExportOverrides();
                    break;

                case 4:
                    if (_templates.Count > 0)
                    {
                        EditorGUI.BeginDisabledGroup(AI.Config.templateExportSettings.devMode);
                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField("Template", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                        if (_selectedTemplate >= _templates.Count) _selectedTemplate = 0; // in case template was deleted manually
                        EditorGUI.BeginChangeCheck();
                        _selectedTemplate = EditorGUILayout.Popup(_selectedTemplate, _templateNames);
                        if (EditorGUI.EndChangeCheck())
                        {
                            PrepareOverrides();
                        }
                        TemplateInfo curTemplate = _templates[_selectedTemplate];
                        if (ShowAdvanced())
                        {
                            if (GUILayout.Button(CommonUIStyles.Content("New...", "Create a new empty template."), GUILayout.Width(60)))
                            {
                                NameUI nameUI = new NameUI();
                                nameUI.Init("My Template", CreateTemplate);
                                PopupWindow.Show(GetPopupPositionAtMouse(), nameUI);
                            }
                            if (GUILayout.Button(CommonUIStyles.Content("Copy...", "Creates a full independent copy of the original template including all files."), GUILayout.Width(60)))
                            {
                                NameUI nameUI = new NameUI();
                                nameUI.Init("My Template", CopyTemplate);
                                PopupWindow.Show(GetPopupPositionAtMouse(), nameUI);
                            }
                            if (GUILayout.Button(CommonUIStyles.Content("Extend...", "Creates a template extension referencing the original template where the original files will selectively be replaced by the ones in this template."), GUILayout.Width(60)))
                            {
                                NameUI nameUI = new NameUI();
                                nameUI.Init("My Template", ExtendTemplate);
                                PopupWindow.Show(GetPopupPositionAtMouse(), nameUI);
                            }
                            EditorGUI.BeginDisabledGroup(curTemplate.readOnly);
                            if (GUILayout.Button(EditorGUIUtility.IconContent("TreeEditor.Trash", "|Delete template"), GUILayout.Width(30)))
                            {
                                string templateName = !string.IsNullOrWhiteSpace(curTemplate.name) 
                                    ? curTemplate.name 
                                    : curTemplate.GetNameFromFile();
                                if (!EditorUtility.DisplayDialog("Delete Template", 
                                    $"Are you sure you want to delete the template '{templateName}'? This action cannot be undone.", 
                                    "Delete", "Cancel")) return;

                                if (curTemplate.hasDescriptor) File.Delete(curTemplate.GetDescriptorPath());
                                File.Delete(curTemplate.path);
                                AssetDatabase.Refresh();
                                LoadTemplates();
                            }
                            EditorGUI.EndDisabledGroup();
                        }
                        GUILayout.EndHorizontal();
                        EditorGUI.EndDisabledGroup();

                        _scrollPos = GUILayout.BeginScrollView(_scrollPos, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.ExpandWidth(true));

                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField("Configuration", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                        AI.Config.templateExportSettings.environmentIndex = EditorGUILayout.Popup(AI.Config.templateExportSettings.environmentIndex, AI.Config.templateExportSettings.environments.Select(e => e.name).ToArray());
                        if (ShowAdvanced())
                        {
                            if (GUILayout.Button("New...", GUILayout.Width(60)))
                            {
                                NameUI nameUI = new NameUI();
                                nameUI.Init("My Config", name =>
                                {
                                    AI.Config.templateExportSettings.environments.Add(new TemplateExportEnvironment(name));
                                    AI.Config.templateExportSettings.environmentIndex = AI.Config.templateExportSettings.environments.Count - 1;
                                    AI.SaveConfig();
                                });
                                PopupWindow.Show(GetPopupPositionAtMouse(), nameUI);
                            }
                            if (AI.Config.templateExportSettings.environments.Count > 1 && GUILayout.Button(EditorGUIUtility.IconContent("TreeEditor.Trash", "|Delete configuration"), GUILayout.Width(30)))
                            {
                                AI.Config.templateExportSettings.environments.RemoveAt(AI.Config.templateExportSettings.environmentIndex);
                                AI.Config.templateExportSettings.environmentIndex--;
                                AI.SaveConfig();
                            }
                        }
                        GUILayout.EndHorizontal();

                        TemplateExportEnvironment env = AI.Config.templateExportSettings.environments[AI.Config.templateExportSettings.environmentIndex];

                        EditorGUI.BeginChangeCheck();
                        BeginIndentBlock();

                        if (curTemplate.fixedTargetFolder)
                        {
                            env.publishFolder = Path.GetDirectoryName(Paths.GetPreviewFolder());
                            DrawFolder("Target Folder", env.publishFolder, null, null, labelWidth);
                        }
                        else
                        {
                            DrawFolder("Target Folder", env.publishFolder, null, newFolder =>
                            {
                                env.publishFolder = newFolder;
                                AI.SaveConfig();
                            }, labelWidth);
                        }

                        if (ShowAdvanced())
                        {
                            if (curTemplate.needsImagePath)
                            {
                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField("Image Path", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                                env.imagePath = EditorGUILayout.TextField(env.imagePath);
                                GUILayout.EndHorizontal();
                            }

                            if (curTemplate.needsDataPath)
                            {
                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField("Data Path", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                                env.dataPath = EditorGUILayout.TextField(env.dataPath);
                                GUILayout.EndHorizontal();

                                if (curTemplate.needsImagePath)
                                {
                                    GUILayout.BeginHorizontal();
                                    EditorGUILayout.LabelField(CommonUIStyles.Content("Exclude Images", "Will not export images for the file search as that might make icons and textures available for download."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                                    env.excludeImages = EditorGUILayout.Toggle(env.excludeImages);
                                    GUILayout.EndHorizontal();
                                }
                            }
                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Internal Ids Only", "Will name all package details file as package_[id].html. Otherwise if an asset is from or linked to the Asset Store, it will use package_f[foreignId].html to create more stable links."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                            env.internalIdsOnly = EditorGUILayout.Toggle(env.internalIdsOnly);
                            GUILayout.EndHorizontal();
                        }

                        EndIndentBlock();
                        if (EditorGUI.EndChangeCheck()) AI.SaveConfig();

                        if (ShowAdvanced() || AI.Config.templateExportSettings.devMode)
                        {
                            EditorGUILayout.Space(20);
                            EditorGUI.BeginChangeCheck();
                            AI.Config.templateExportSettings.devMode = EditorGUILayout.BeginFoldoutHeaderGroup(AI.Config.templateExportSettings.devMode, "Template Development Mode");
                            if (AI.Config.templateExportSettings.devMode)
                            {
                                EditorGUILayout.HelpBox("Development mode is now active and the export will use the settings below. This allows you to create and quickly iterate on your own templates. Close section to deactivate.", MessageType.Warning);
                                EditorGUILayout.Space();

                                DrawFolder("Dev Folder", AI.Config.templateExportSettings.devFolder, null, newFolder =>
                                {
                                    AI.Config.templateExportSettings.devFolder = newFolder;
                                    AI.SaveConfig();

                                    if (!string.IsNullOrWhiteSpace(newFolder))
                                    {
                                        if (IOUtils.IsDirectoryEmpty(newFolder))
                                        {
                                            CompressionUtil.ExtractArchive(curTemplate.path, newFolder);
                                        }
                                        else
                                        {
                                            EditorUtility.DisplayDialog("Folder not empty", "The development folder is not empty. The contents of the template was not automatically extracted there.", "OK");
                                        }
                                    }
                                }, labelWidth);

                                if (!string.IsNullOrWhiteSpace(AI.Config.templateExportSettings.devFolder))
                                {
                                    GUILayout.BeginHorizontal();
                                    GUILayout.Space(labelWidth + 6);

                                    if (GUILayout.Button(CommonUIStyles.Content("Publish", "Compress development folder into a package of the same name and copy it into the templates folder."), GUILayout.ExpandWidth(false)))
                                    {
                                        PackageDevTemplate();
                                    }
                                    if (_watcher == null)
                                    {
                                        if (GUILayout.Button(CommonUIStyles.Content("Start Directory Monitoring", "Will continuously monitor the development directory for changes and trigger automatic exports.")))
                                        {
                                            StartTemplateWatcher(AI.Config.templateExportSettings.devFolder);
                                        }
                                    }
                                    else
                                    {
                                        if (GUILayout.Button("Stop Directory Monitoring")) StopTemplateWatcher();
                                    }
                                    if (!string.IsNullOrWhiteSpace(curTemplate.inheritFrom))
                                    {
                                        if (GUILayout.Button("Override File...")) ShowOverrides();
                                    }

                                    GUILayout.EndHorizontal();
                                    EditorGUILayout.Space();
                                }

                                DrawFolder("Test Folder", AI.Config.templateExportSettings.testFolder, null, newFolder =>
                                {
                                    AI.Config.templateExportSettings.testFolder = newFolder;
                                    AI.SaveConfig();
                                }, labelWidth);

                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField("Detail Pages", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                                AI.Config.templateExportSettings.maxDetailPages = EditorGUILayout.DelayedIntField(AI.Config.templateExportSettings.maxDetailPages, GUILayout.Width(50));
                                EditorGUILayout.LabelField("(0 = all)", EditorStyles.miniLabel);
                                GUILayout.EndHorizontal();

                                GUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField("Flags", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                                GUILayout.BeginVertical();
                                GUILayout.BeginHorizontal();
                                if (curTemplate.needsDataPath && !string.IsNullOrWhiteSpace(AI.Config.templateExportSettings.testFolder))
                                {
                                    AI.Config.templateExportSettings.preserveJson = EditorGUILayout.ToggleLeft(CommonUIStyles.Content("Preserve Json", "Do not export data to Json but reuse already generated Json artifacts. Will be ignored if no Json exists yet."), AI.Config.templateExportSettings.preserveJson, GUILayout.Width(110));
                                }
                                if (!string.IsNullOrWhiteSpace(AI.Config.templateExportSettings.testFolder))
                                {
                                    AI.Config.templateExportSettings.publishResult = EditorGUILayout.ToggleLeft(CommonUIStyles.Content("Publish Result", "Copy exported files from temporary to target directory."), AI.Config.templateExportSettings.publishResult, GUILayout.Width(110));
                                }
                                AI.Config.templateExportSettings.revealResult = EditorGUILayout.ToggleLeft(CommonUIStyles.Content("Open " + (Application.platform == RuntimePlatform.OSXEditor ? "Finder" : "Explorer"), "Opens file browser once the export is done."), AI.Config.templateExportSettings.revealResult, GUILayout.Width(110));
                                GUILayout.EndHorizontal();
                                if (string.IsNullOrWhiteSpace(AI.Config.templateExportSettings.testFolder))
                                {
                                    EditorGUILayout.LabelField("Setting a test folder will unlock additional flags for more convenient development.", CommonUIStyles.greyMiniLabel);
                                }
                                GUILayout.EndVertical();
                                GUILayout.EndHorizontal();

                                EditorGUILayout.Space();
                                GUILayout.BeginHorizontal();
                                if (curTemplate.hasDescriptor)
                                {
                                    if (GUILayout.Button("Open Descriptor"))
                                    {
                                        EditorUtility.RevealInFinder(curTemplate.GetDescriptorPath());
                                    }
                                }
                                else
                                {
                                    if (GUILayout.Button("Create Descriptor"))
                                    {
                                        string descriptor = curTemplate.GetDescriptorPath();
                                        File.WriteAllText(descriptor, JsonConvert.SerializeObject(curTemplate, Formatting.Indented));

                                        EditorUtility.DisplayDialog("Descriptor Created", $"Descriptor file '{descriptor}' has been created.", "OK");

                                        AssetDatabase.Refresh();
                                        LoadTemplates();
                                    }
                                }
                                if (GUILayout.Button(CommonUIStyles.Content("Start Local Server", "Starts a Python server on http://localhost:8000/ to serve the template files.")))
                                {
#if UNITY_EDITOR_OSX
                                string command = "/usr/bin/python3";
#else
                                    string command = "python";
#endif
                                    IOUtils.ExecuteCommand(command, "-m http.server 8000", env.publishFolder, false, true);
                                    AI.OpenURL("http://localhost:8000" + (!string.IsNullOrWhiteSpace(curTemplate.entryPath) ? $"/{curTemplate.entryPath}" : ""));
                                }
                                GUILayout.EndHorizontal();
                            }
                            if (EditorGUI.EndChangeCheck())
                            {
                                if (!AI.Config.templateExportSettings.devMode && _watcher != null) StopTemplateWatcher();
                                AI.SaveConfig();
                                if (AI.Config.templateExportSettings.devMode && _watcher != null) _triggerExport = true;
                            }
                            EditorGUILayout.EndFoldoutHeaderGroup();
                        }
                        GUILayout.EndScrollView();
                        GUILayout.FlexibleSpace();
                        EditorGUI.BeginDisabledGroup(_watcher != null);
                        if (GUILayout.Button((_exportInProgress ? "Export in progress..." : "Export") + (_watcher != null ? " (automatically upon changes)" : ""), CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT)))
                        {
                            ExportTemplate();
                        }
                        EditorGUI.EndDisabledGroup();
                    }
                    else
                    {
                        EditorGUILayout.Space();
                        EditorGUILayout.HelpBox("There are no templates available. Please create a template first and put it into the 'AssetInventory/Editor/Templates' folder. Normally there should be at least two default templates available, which might also hint to a broken installation.", MessageType.Warning);
                    }
                    break;
            }
            EditorGUI.EndDisabledGroup();
        }

        private async void PrepareOverrides()
        {
            _overridesFolder = IOUtils.CreateTempFolder(TEMP_FOLDER, true);
            await TemplateExport.ResolveInheritance(_templates[_selectedTemplate], _overridesFolder, _templates);
            _overrideCandidates = IOUtils.GetFiles(_overridesFolder, "", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.InvariantCultureIgnoreCase).ToList();
        }

        private void ShowOverrides()
        {
            GenericMenu menu = new GenericMenu();
            foreach (string file in _overrideCandidates)
            {
                string relPath = file.Substring(_overridesFolder.Length + 1);
                string target = Path.Combine(AI.Config.templateExportSettings.devFolder, relPath);
                if (File.Exists(target))
                {
                    menu.AddDisabledItem(new GUIContent(relPath));
                }
                else
                {
                    menu.AddItem(new GUIContent(relPath), false, () => OverrideFile(file));
                }
            }
            menu.ShowAsContext();
        }

        private void OverrideFile(string file)
        {
            string target = Path.Combine(AI.Config.templateExportSettings.devFolder, file.Substring(_overridesFolder.Length + 1));
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(file, target, true);
        }

        private void CreateTemplate(string newName)
        {
            string saveFolder = TemplateUtils.GetTemplateSaveFolder();
            string destination = Path.Combine(saveFolder, $"{newName}.zip.bytes");

            if (File.Exists(destination))
            {
                EditorUtility.DisplayDialog("Error", "A template with that name already exists.", "OK");
                return;
            }

            // create zip
            CompressionUtil.CreateEmptyZip(destination);

            AssetDatabase.Refresh();
            LoadTemplates();
        }

        private void CopyTemplate(string newName)
        {
            string saveFolder = TemplateUtils.GetTemplateSaveFolder();
            string source = _templates[_selectedTemplate].path;
            string safeName = AssetUtils.GuessSafeName(newName).Replace(" ", "");
            string destination = Path.Combine(saveFolder, $"{safeName}.zip.bytes");
            if (File.Exists(destination))
            {
                EditorUtility.DisplayDialog("Error", "A template with that name already exists.", "OK");
                return;
            }
            File.Copy(source, destination);
            if (_templates[_selectedTemplate].hasDescriptor)
            {
                string descriptor = _templates[_selectedTemplate].GetDescriptorPath();
                string newDescriptor = Path.Combine(saveFolder, $"{safeName}.json");
                File.Copy(descriptor, newDescriptor);

                // adjust descriptor
                TemplateInfo ti = JsonConvert.DeserializeObject<TemplateInfo>(File.ReadAllText(newDescriptor));
                ti.name = newName;
                ti.date = DateTime.Now;
                ti.version = 1;
                ti.readOnly = false;
                File.WriteAllText(newDescriptor, JsonConvert.SerializeObject(ti, Formatting.Indented));
            }

            AssetDatabase.Refresh();
            LoadTemplates();
        }

        private void ExtendTemplate(string newName)
        {
            string saveFolder = TemplateUtils.GetTemplateSaveFolder();
            string source = _templates[_selectedTemplate].path;
            string safeName = AssetUtils.GuessSafeName(newName).Replace(" ", "");
            string destination = Path.Combine(saveFolder, $"{safeName}.zip.bytes");
            string newDescriptor = Path.Combine(saveFolder, $"{safeName}.json");

            if (File.Exists(destination))
            {
                EditorUtility.DisplayDialog("Error", "A template with that name already exists.", "OK");
                return;
            }

            // create descriptor and copy from original
            TemplateInfo ti = new TemplateInfo();
            ti.name = newName;
            ti.inheritFrom = _templates[_selectedTemplate].GetNameFromFile();
            ti.needsDataPath = _templates[_selectedTemplate].needsDataPath;
            ti.needsImagePath = _templates[_selectedTemplate].needsImagePath;
            ti.fixedTargetFolder = _templates[_selectedTemplate].fixedTargetFolder;
            ti.entryPath = _templates[_selectedTemplate].entryPath;
            ti.parameters = _templates[_selectedTemplate].parameters;
            ti.readOnly = false;
            ti.isSample = false;
            ti.date = DateTime.Now;
            File.WriteAllText(newDescriptor, JsonConvert.SerializeObject(ti, Formatting.Indented));

            // create zip
            CompressionUtil.CreateEmptyZip(destination);

            AssetDatabase.Refresh();
            LoadTemplates();
        }

        private void PackageDevTemplate()
        {
            TemplateInfo ti = _templates[_selectedTemplate];
            string source = AI.Config.templateExportSettings.devFolder;
            string target = ti.path;

            CompressionUtil.CompressFolder(source, target);

            if (ti.hasDescriptor)
            {
                ti.date = DateTime.Now;
                File.WriteAllText(ti.GetDescriptorPath(), JsonConvert.SerializeObject(ti, Formatting.Indented));
            }

            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("Template Export", $"Template '{ti.GetNameFromFile()}' has been exported to '{target}'.", "OK");
        }

        private void StopTemplateWatcher()
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        private void StartTemplateWatcher(string path)
        {
            _watcher = new FileSystemWatcher();
            _watcher.Path = path;
            _watcher.IncludeSubdirectories = true;
            _watcher.Filter = "*.*";
            _watcher.InternalBufferSize = 65536;

            _watcher.NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.LastWrite;

            _watcher.Changed += OnChanged;
            _watcher.Created += OnCreated;
            _watcher.Deleted += OnDeleted;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += (_, args) => { Debug.LogWarning($"Template dev folder monitoring error: {args.GetException()}"); };

            _watcher.EnableRaisingEvents = true;
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            Debug.Log($"Picking up template file rename: {e.OldFullPath} -> {e.FullPath}");
            _triggerExport = true;
        }

        private void OnDeleted(object sender, FileSystemEventArgs e)
        {
            Debug.Log($"Picking up template file delete: {e.FullPath}");
            _triggerExport = true;
        }

        private void OnCreated(object sender, FileSystemEventArgs e)
        {
            Debug.Log($"Picking up template file create: {e.FullPath}");
            _triggerExport = true;
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            Debug.Log($"Picking up template file change: {e.FullPath}");
            _triggerExport = true;
        }

        private async void ExportTemplate()
        {
            if (_exportInProgress) return;
            _triggerExport = false;
            _exportInProgress = true;

            AI.AskForAffiliate();

            Debug.Log("Export");
            try
            {
                TemplateExportEnvironment env = AI.Config.templateExportSettings.environments[AI.Config.templateExportSettings.environmentIndex];

                // reload template info from disk to support easy template changes
                if (AI.Config.templateExportSettings.devMode) LoadTemplates();

                await AI.Actions.RunWithProgress<TemplateExport>(
                    ActionHandler.ACTION_SUB_PACKAGES_INDEX,
                    "Indexing sub-packages",
                    exp => exp.Run(
                        _assets,
                        _templates[_selectedTemplate],
                        _templates,
                        AI.Config.templateExportSettings,
                        env
                    ));

                if (AI.Config.templateExportSettings.revealResult) EditorUtility.RevealInFinder(env.publishFolder);
            }
            catch (Exception e)
            {
                Debug.LogError($"Exporting template failed: {e}");
            }
            _exportInProgress = false;
        }

        private async void ExportAssets()
        {
            string folder = EditorUtility.OpenFolderPanel("Select storage folder for exports", AI.Config.exportFolder2, "");
            if (string.IsNullOrEmpty(folder)) return;

            if (_clearTarget && Directory.Exists(folder)) await IOUtils.DeleteFileOrDirectory(folder);
            Directory.CreateDirectory(folder);

            AI.Config.exportFolder2 = Path.GetFullPath(folder);
            AI.SaveConfig();

            _exportInProgress = true;
            _curProgress = 0;
            _maxProgress = _packages.Count;

            foreach (AssetInfo info in _packages)
            {
                _curProgress++;
                await Task.Yield();

                if (!info.IsIndexed)
                {
                    Debug.LogError($"Skipping package '{info}' since it is not yet indexed.");
                    continue;
                }

                if (!info.IsDownloaded && !info.IsMaterialized)
                {
                    if (info.IsAbandoned)
                    {
                        Debug.LogWarning($"Package '{info}' is not locally available and also abandoned and cannot be downloaded anymore. Continuing with next package.");
                        continue;
                    }
                    if (!_autoDownload)
                    {
                        Debug.LogWarning($"Package '{info}' is not downloaded and cannot be exported. Continuing with next package.");
                        continue;
                    }
                    AI.GetObserver().Attach(info);
                    if (!info.PackageDownloader.IsDownloadSupported()) continue;

                    info.PackageDownloader.Download(true);
                    do
                    {
                        await Task.Yield();
                    } while (info.IsDownloading());
                    await Task.Delay(3000); // ensure all file operations have finished, can otherwise lead to issues
                    info.Refresh();
                    if (!info.IsDownloaded)
                    {
                        Debug.LogError($"Downloading '{info}' failed. Continuing with next package.");
                        continue;
                    }
                }

                string targetFolder = Path.Combine(folder, _flattenStructure ? "" : info.SafeName);
                Directory.CreateDirectory(targetFolder);

                // extract package
                string cachePath = Paths.GetMaterializedAssetPath(info.ToAsset());
                bool existing = Directory.Exists(cachePath);

                // gather all indexed files
                IEnumerable<AssetFile> files;
                if (_fileMode)
                {
                    // files to export are already known
                    files = _assets.Where(a => a.AssetId == info.AssetId);
                }
                else
                {
                    files = DBAdapter.DB.Query<AssetFile>("SELECT * FROM AssetFile WHERE AssetId = ?", info.AssetId).ToList();
                }
                foreach (AssetFile af in files)
                {
                    if (!_fileMode)
                    {
                        bool include = false;
                        foreach (ED type in _exportTypes)
                        {
                            if (!type.isSelected) continue;
                            if (type.pointer != REMAINING_EXTENSIONS)
                            {
                                if (Enum.TryParse(type.pointer, out AI.AssetGroup group))
                                {
                                    if (AI.TypeGroups[group].Contains(af.Type)) include = true;
                                }
                            }
                            else
                            {
                                if (!_exportableExtensions.Contains(af.Type)) include = true;
                            }
                        }
                        if (!include) continue;
                    }

                    string targetFile = Path.Combine(targetFolder, _flattenStructure ? af.FileName : af.GetPath(true));
                    string targetMeta = targetFile + ".meta";
                    if (File.Exists(targetFile) && (!_metaFiles || File.Exists(targetMeta))) continue;

                    string sourceFile = await Assets.EnsureMaterialized(info.ToAsset(), af);
                    if (string.IsNullOrEmpty(sourceFile)) continue;

                    string targetDir = Directory.GetParent(targetFile)?.ToString();
                    if (targetDir == null) continue;

                    Directory.CreateDirectory(targetDir);
                    File.Copy(sourceFile, targetFile, true);

                    if (_metaFiles)
                    {
                        string sourceMeta = sourceFile + ".meta";
                        if (File.Exists(sourceMeta)) File.Copy(sourceMeta, targetMeta, true);
                    }
                }
                if (!existing) await IOUtils.DeleteFileOrDirectory(cachePath);
            }
            _exportInProgress = false;
            EditorUtility.RevealInFinder(folder);
        }

        private async void ExportOverrides()
        {
            _exportInProgress = true;
            _curProgress = 0;
            _maxProgress = _packages.Count;

            foreach (AssetInfo info in _packages)
            {
                _curProgress++;
                if (info.AssetSource != Asset.Source.CustomPackage && info.AssetSource != Asset.Source.Archive)
                {
                    Debug.LogWarning($"Skipping package '{info}' since it is not a custom package or archive.");
                    continue;
                }
                await Task.Yield();

                string targetFile = info.GetLocation(true) + ".overrides.json";
                if (!_overrideExisting && File.Exists(targetFile)) continue;

                PackageOverrides po = new PackageOverrides();
                foreach (ED field in _overrideFields.Where(f => f.isSelected))
                {
                    switch (field.field)
                    {
                        case "PackageTags":
                            po.tags = info.PackageTags.Select(pt => pt.Name).ToArray();
                            break;

                        default:
                            if (field.FieldInfo != null)
                            {
                                FieldInfo fi = typeof (PackageOverrides).GetField(field.field.ToLowercaseFirstLetter());
                                if (fi != null)
                                {
                                    fi.SetValue(po, field.FieldInfo.GetValue(info));
                                }
                                else
                                {
                                    Debug.LogError($"Override field '{field.field}' not found.");
                                }
                            }
                            else
                            {
                                Debug.LogError($"Override source field '{field.field}' not found.");
                            }
                            break;
                    }
                }

                File.WriteAllText(targetFile, JsonConvert.SerializeObject(po, new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    NullValueHandling = NullValueHandling.Ignore,
                    DefaultValueHandling = DefaultValueHandling.Ignore
                }));
            }
            _exportInProgress = false;
        }

        private void ExportLicenses()
        {
            string file = EditorUtility.SaveFilePanel("Target file", AI.Config.exportFolder3, "ThirdParty", "md");
            if (string.IsNullOrEmpty(file)) return;

            _exportInProgress = true;

            AI.Config.exportFolder3 = Directory.GetParent(Path.GetFullPath(file))?.ToString();
            AI.SaveConfig();

            // TODO: switch to configurable templates
            List<string> result = new List<string>();
            result.Add("# Third Party Licenses");
            result.Add("");
            result.Add("The following third-party packages are included: ");
            result.Add("");

            List<AssetInfo> list = _assets.Where(a => !string.IsNullOrWhiteSpace(a.License))
                .GroupBy(a => a.GetDisplayName() + " - " + a.License)
                .Select(g => g.First())
                .OrderBy(a => a.GetDisplayName())
                .ToList();
            foreach (AssetInfo info in list)
            {
                result.Add($"## {info.GetDisplayName(true)}");
                result.Add("");
                result.Add(info.License);
                if (!string.IsNullOrWhiteSpace(info.LicenseLocation)) result.Add($"([Details]({info.LicenseLocation}))");
                result.Add("");
            }
            try
            {
                File.WriteAllLines(file, result);
                EditorUtility.RevealInFinder(file);
            }
            catch (Exception e)
            {
                Debug.LogError($"Exporting to file failed: {e}");
                EditorUtility.DisplayDialog("Export Failed", "License export failed. Most likely the target file is already opened in another application. See console for details.", "OK");
            }

            _exportInProgress = false;

        }

        private void ExportCSV()
        {
            SaveCSVSettings();

            string currentFile = AI.Config.csvExportSettings?.exportFile;
            string initialDirectory = !string.IsNullOrWhiteSpace(currentFile)
                ? Directory.GetParent(Path.GetFullPath(currentFile))?.ToString()
                : AI.Config.exportFolder;
            string initialName = !string.IsNullOrWhiteSpace(currentFile)
                ? Path.GetFileNameWithoutExtension(currentFile)
                : Path.GetFileNameWithoutExtension(CSVExport.DEFAULT_FILE_NAME);

            string file = EditorUtility.SaveFilePanel("Target file", initialDirectory, initialName, "csv");
            if (string.IsNullOrEmpty(file)) return;

            _exportInProgress = true;

            string fullPath = Path.GetFullPath(file);
            AI.Config.exportFolder = Directory.GetParent(fullPath)?.ToString();
            AI.Config.csvExportSettings.exportFile = fullPath;
            SaveCSVSettings();

            CSVExport exporter = new CSVExport();
            try
            {
                exporter.Run(_assets, AI.Config.csvExportSettings, fullPath);
                EditorUtility.RevealInFinder(fullPath);
            }
            catch (Exception e)
            {
                Debug.LogError($"Exporting to file failed: {e}");
                EditorUtility.DisplayDialog("Export Failed", "CSV export failed. Most likely the target file is already opened in another application. See console for details.", "OK");
            }

            _exportInProgress = false;
        }

        private void OnInspectorUpdate()
        {
            Repaint();
        }

        private void LoadCSVSettings()
        {
            if (AI.Config.csvExportSettings == null) AI.Config.csvExportSettings = new CSVExportSettings();
            AI.Config.csvExportSettings.EnsureDefaults();

            _separator = AI.Config.csvExportSettings.separator;
            _addHeader = AI.Config.csvExportSettings.addHeader;

            HashSet<string> selectedFields = new HashSet<string>(AI.Config.csvExportSettings.selectedFields);
            foreach (ED field in _exportFields)
            {
                field.isSelected = selectedFields.Contains(field.pointer);
            }
        }

        private void SaveCSVSettings()
        {
            if (AI.Config.csvExportSettings == null) AI.Config.csvExportSettings = new CSVExportSettings();

            AI.Config.csvExportSettings.separator = _separator;
            AI.Config.csvExportSettings.addHeader = _addHeader;
            AI.Config.csvExportSettings.selectedFields = _exportFields
                .Where(field => field.isSelected)
                .Select(field => field.pointer)
                .ToList();
            AI.Config.csvExportSettings.EnsureDefaults();
            AI.SaveConfig();
        }
    }

    /// <summary>
    /// Class to hold export type information for the wizard UI
    /// </summary>
    public class ExportTypeInfo
    {
        public int Index { get; private set; }
        public string Name { get; private set; }
        public string Description { get; private set; }
        public Texture Icon { get; private set; }

        public ExportTypeInfo(int index, string name, string description, Texture icon)
        {
            Index = index;
            Name = name;
            Description = description;
            Icon = icon;
        }
    }

    [Serializable]
    public sealed class ED
    {
        public string pointer;
        public bool isDefault;
        public bool isVisibleColumn;
        public bool isSelected;

        public string table;
        public string field;

        public PropertyInfo FieldInfo
        {
            get
            {
                if (field == null) return null;
                if (_fieldInfo == null) _fieldInfo = typeof (AssetInfo).GetProperty(field);
                return _fieldInfo;
            }
        }

        private PropertyInfo _fieldInfo;

        public ED(string pointer, bool isDefault = true, bool isVisibleColumn = false)
        {
            this.isDefault = isDefault;
            this.isVisibleColumn = isVisibleColumn;
            this.pointer = pointer;

            isSelected = isDefault;

            if (pointer.IndexOf('/') >= 0)
            {
                table = pointer.Split('/')[0];
                field = pointer.Split('/')[1];
            }
        }
    }
}
