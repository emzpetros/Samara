using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ImpossibleRobert.Common
{
    public sealed class AboutWindow : EditorWindow
    {
        private const int WINDOW_WIDTH = 690;
        private const int WINDOW_HEIGHT = 340;
        private const float LOGO_SIZE = 180;
        private const float GRID_LOGO_SIZE = 100;

        private string _toolId;
        private Action _customSection;
        private Vector2 _scrollPos;

        // Cached data — resolved once in Reload(), reused every frame
        private ToolCatalog _catalog;
        private ToolInfo _toolInfo;
        private string _version;
        private List<ToolInfo> _otherTools;
        private Texture2D _logo;
        private Dictionary<string, Texture2D> _gridLogos;

        public static void Show(string toolId, Action customSection = null)
        {
            AboutWindow window = GetWindow<AboutWindow>(true, "About");
            window._toolId = toolId;
            window._customSection = customSection;
            window.minSize = new Vector2(WINDOW_WIDTH, WINDOW_HEIGHT);
            window.Reload();
            window.ShowUtility();
        }

        private void Reload()
        {
            _catalog = ToolCatalog.Load();
            _toolInfo = _catalog.GetTool(_toolId);
            _version = ToolCatalog.ResolveVersion(_toolId);
            _otherTools = _catalog.GetOtherTools(_toolId);
            _logo = _toolInfo != null ? CommonUIStyles.LoadTexture(_toolInfo.logoTextureName) : null;
            _gridLogos = new Dictionary<string, Texture2D>();
            if (_otherTools != null)
            {
                foreach (ToolInfo t in _otherTools)
                {
                    _gridLogos[t.id] = CommonUIStyles.LoadTexture(t.logoTextureName);
                }
            }
        }

        private void OnGUI()
        {
            if (string.IsNullOrEmpty(_toolId))
            {
                EditorGUILayout.HelpBox("Tool ID not set. Please reopen this window.", MessageType.Warning);
                return;
            }
            if (_catalog == null || _gridLogos == null) Reload();

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            DrawContentCached(_catalog, _toolInfo, _version, _otherTools, _logo, _gridLogos, _customSection);
            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// Draws about content inline (e.g. embedded in a tab). Caches data in a static holder
        /// so repeated OnGUI calls don't re-resolve textures and versions every frame.
        /// </summary>
        public static void DrawContent(string toolId, Action customSection = null)
        {
            // Use a simple static cache keyed by toolId to avoid per-frame asset lookups
            if (_inlineCache == null || _inlineCache.ToolId != toolId)
            {
                _inlineCache = new InlineCache(toolId);
            }

            DrawContentCached(
                _inlineCache.Catalog, _inlineCache.ToolInfo, _inlineCache.Version,
                _inlineCache.OtherTools, _inlineCache.Logo, _inlineCache.GridLogos,
                customSection);
        }

        private static InlineCache _inlineCache;

        private sealed class InlineCache
        {
            public readonly string ToolId;
            public readonly ToolCatalog Catalog;
            public readonly ToolInfo ToolInfo;
            public readonly string Version;
            public readonly List<ToolInfo> OtherTools;
            public readonly Texture2D Logo;
            public readonly Dictionary<string, Texture2D> GridLogos;

            public InlineCache(string toolId)
            {
                ToolId = toolId;
                Catalog = ToolCatalog.Load();
                ToolInfo = Catalog.GetTool(toolId);
                Version = ToolCatalog.ResolveVersion(toolId);
                OtherTools = Catalog.GetOtherTools(toolId);
                Logo = ToolInfo != null ? CommonUIStyles.LoadTexture(ToolInfo.logoTextureName) : null;
                GridLogos = new Dictionary<string, Texture2D>();
                if (OtherTools != null)
                {
                    foreach (ToolInfo t in OtherTools)
                    {
                        GridLogos[t.id] = CommonUIStyles.LoadTexture(t.logoTextureName);
                    }
                }
            }
        }

        private static void DrawContentCached(
            ToolCatalog catalog, ToolInfo toolInfo, string version,
            List<ToolInfo> otherTools, Texture2D logo,
            Dictionary<string, Texture2D> gridLogos,
            Action customSection)
        {
            if (toolInfo == null)
            {
                EditorGUILayout.HelpBox("Tool not found in tools.json.", MessageType.Warning);
                return;
            }

            GUIStyle textColor = EditorGUIUtility.isProSkin ? CommonUIStyles.whiteCenter : CommonUIStyles.blackCenter;

            EditorGUILayout.Space(6);

            // Publisher heading
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField($"A tool by {catalog.publisher}", CommonUIStyles.centerHeading, GUILayout.Width(350), GUILayout.Height(50));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // Logo
            if (logo != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Box(logo, EditorStyles.centeredGreyMiniLabel, GUILayout.MaxWidth(LOGO_SIZE), GUILayout.MaxHeight(LOGO_SIZE));
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }

            // Version
            EditorGUILayout.Space(4);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField($"Version {version}", textColor, GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            EditorGUILayout.Space(6);

            // Links row
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (!string.IsNullOrEmpty(toolInfo.webLink))
            {
                if (GUILayout.Button("Online Resources", CommonUIStyles.centerLinkLabel)) Application.OpenURL(toolInfo.webLink);
                EditorGUILayout.LabelField(" | ", EditorStyles.centeredGreyMiniLabel, GUILayout.Width(10));
            }
            if (!string.IsNullOrEmpty(catalog.discordLink))
            {
                if (GUILayout.Button("Join Discord", CommonUIStyles.centerLinkLabel)) Application.OpenURL(catalog.discordLink);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            EditorGUILayout.Space(6);

            // Review CTA
            if (!string.IsNullOrEmpty(toolInfo.assetStoreLink))
            {
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.MaxWidth(480));
                EditorGUILayout.LabelField($"Enjoying {toolInfo.name}?", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("If you like this asset, please consider leaving a review on the Unity Asset Store.", EditorStyles.wordWrappedLabel);
                EditorGUILayout.Space(2);
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Write Review", GUILayout.Width(160))) Application.OpenURL(toolInfo.assetStoreLink);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                EditorGUILayout.Space(6);
            }

            // Other tools grid
            if (otherTools != null && otherTools.Count > 0)
            {
                EditorGUILayout.Space(4);
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.BeginVertical(GUILayout.MaxWidth(480));

                EditorGUILayout.LabelField("Other Tools You Might Like", CommonUIStyles.centerHeading, GUILayout.Height(30));
                EditorGUILayout.Space(4);

                DrawToolGrid(otherTools, gridLogos, catalog.discordLink);

                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(6);

            // Custom section (at the bottom)
            customSection?.Invoke();

            EditorGUILayout.Space(8);
        }

        private static void DrawToolGrid(List<ToolInfo> tools, Dictionary<string, Texture2D> logos, string discordLink)
        {
            const int columns = 2;
            const float cellPadding = 8;
            float cellWidth = (480 - cellPadding) / columns;

            for (int i = 0; i < tools.Count; i += columns)
            {
                GUILayout.BeginHorizontal();
                for (int j = 0; j < columns && i + j < tools.Count; j++)
                {
                    if (j > 0) GUILayout.Space(cellPadding);
                    ToolInfo tool = tools[i + j];
                    logos.TryGetValue(tool.id, out Texture2D tex);
                    DrawToolCell(tool, tex, cellPadding, discordLink, cellWidth);
                }
                // fill remaining cells if odd count
                if (i + columns > tools.Count && tools.Count % columns != 0)
                {
                    GUILayout.Space(cellPadding);
                    GUILayout.BeginVertical(GUILayout.Width(cellWidth));
                    GUILayout.FlexibleSpace();
                    GUILayout.EndVertical();
                }
                GUILayout.EndHorizontal();
                EditorGUILayout.Space(cellPadding);
            }
        }

        private static void DrawToolCell(ToolInfo tool, Texture2D tex, float padding, string discordLink, float cellWidth)
        {
            GUILayout.BeginVertical(CommonUIStyles.sectionBox, GUILayout.Width(cellWidth));

            GUILayout.BeginHorizontal();

            // Logo thumbnail
            if (tex != null)
            {
                GUILayout.Box(tex, GUIStyle.none, GUILayout.Width(GRID_LOGO_SIZE), GUILayout.Height(GRID_LOGO_SIZE));
            }
            else
            {
                GUILayout.Space(GRID_LOGO_SIZE);
            }

            GUILayout.Space(padding);

            // Text column — constrain width to prevent addon badge from expanding the cell
            float textWidth = cellWidth - GRID_LOGO_SIZE - padding - 12;
            GUILayout.BeginVertical(GUILayout.Width(textWidth), GUILayout.MaxWidth(textWidth));

            // Name
            string displayName = tool.isAddon ? $"{tool.name} (Add-on)" : tool.name;
            EditorGUILayout.LabelField(displayName, EditorStyles.boldLabel);

            EditorGUILayout.LabelField(tool.description, EditorStyles.wordWrappedMiniLabel);

            // Link button
            if (!string.IsNullOrEmpty(tool.assetStoreLink))
            {
                if (GUILayout.Button("View on Asset Store", EditorStyles.linkLabel)) Application.OpenURL(tool.assetStoreLink);
            }
            else if (!string.IsNullOrEmpty(discordLink))
            {
                if (GUILayout.Button("Join Beta", EditorStyles.linkLabel)) Application.OpenURL(discordLink);
            }
            else if (!string.IsNullOrEmpty(tool.webLink))
            {
                if (GUILayout.Button("Learn More", EditorStyles.linkLabel)) Application.OpenURL(tool.webLink);
            }
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }
    }
}