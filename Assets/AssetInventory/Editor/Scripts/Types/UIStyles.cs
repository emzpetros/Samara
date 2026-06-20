using System;
using ImpossibleRobert.Common;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    public static class UIStyles
    {
        public enum TagStyle
        {
            Add = 0,
            Remove = 1,
            Neutral = 2,
            ColorSelect = 3
        }

        public const int BORDER_WIDTH = 30;
        public const int INSPECTOR_WIDTH = 300;
        public const int TAG_SIZE_SPACING = 20;
        public const int TAG_OUTER_MARGIN = 20;

        private static int EntryFontSize => AI.Config.fontSize;
        private static int EntryFixedHeight => EntryFontSize + 7;
        private const int TOGGLE_FIXED_WIDTH = 10;

        private static GUIStyle _searchTile;
        public static GUIStyle searchTile
        {
            get { return _searchTile ?? (_searchTile = CreateTileStyle()); }
        }

        private static GUIStyle _packageTile;
        public static GUIStyle packageTile
        {
            get { return _packageTile ?? (_packageTile = CreateTileStyle()); }
        }

        private static GUIStyle _selectedSearchTile;
        public static GUIStyle selectedSearchTile
        {
            get { return _selectedSearchTile ?? (_selectedSearchTile = CreateSelectedTileStyle()); }
        }

        private static GUIStyle _selectedPackageTile;
        public static GUIStyle selectedPackageTile
        {
            get { return _selectedPackageTile ?? (_selectedPackageTile = CreateSelectedTileStyle()); }
        }

        private static GUIStyle _toggleButtonStyleNormal;
        public static GUIStyle toggleButtonStyleNormal
        {
            get { return _toggleButtonStyleNormal ?? (_toggleButtonStyleNormal = new GUIStyle("button")); }
        }
        private static GUIStyle _toggleButtonStyleToggled;
        public static GUIStyle toggleButtonStyleToggled
        {
            get { return _toggleButtonStyleToggled ?? (_toggleButtonStyleToggled = CreateToggledStyle()); }
        }

        public static readonly GUIContent emptyTileContent = new GUIContent();
        public static readonly GUIContent selectedTileContent = new GUIContent
        {
            image = CommonUIStyles.LoadTexture("asset-inventory-selected"),
            text = string.Empty,
            tooltip = string.Empty
        };

        public static void ResetStyles()
        {
            _searchTile = null;
            _packageTile = null;
            _entryStyle = null;
            _toggleStyle = null;
        }

        private static GUIStyle _entryStyle;
        public static GUIStyle entryStyle
        {
            get { return _entryStyle ?? (_entryStyle = new GUIStyle(EditorStyles.miniLabel) {fontSize = EntryFontSize, fixedHeight = EntryFixedHeight}); }
        }

        private static GUIStyle _toggleStyle;
        public static GUIStyle toggleStyle
        {
            get { return _toggleStyle ?? (_toggleStyle = new GUIStyle(EditorStyles.toggle) {fixedWidth = TOGGLE_FIXED_WIDTH, fixedHeight = EntryFixedHeight}); }
        }

        private static GUIStyle CreateToggledStyle()
        {
            GUIStyle baseStyle = new GUIStyle("button");
            baseStyle.normal.background = baseStyle.active.background;

            return baseStyle;
        }

        private static GUIStyle CreateTileStyle()
        {
            GUIStyle baseStyle = new GUIStyle(GUI.skin.button)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = AI.Config.fontSize,
                imagePosition = ImagePosition.ImageAbove,
                wordWrap = true,
                margin = new RectOffset(AI.Config.tileMargin, AI.Config.tileMargin, AI.Config.tileMargin, AI.Config.tileMargin)
            };

            return baseStyle;
        }

        private static GUIStyle CreateSelectedTileStyle()
        {
            GUIStyle baseStyle = new GUIStyle
            {
                alignment = TextAnchor.MiddleCenter,
                imagePosition = ImagePosition.ImageOnly,
                overflow = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0)
            };

            baseStyle.normal.background = CommonUIStyles.LoadTexture("asset-inventory-transparent");

            return baseStyle;
        }

        private static GUIStyle _tag;
        public static GUIStyle tag
        {
            get
            {
                if (_tag == null)
                {
                    _tag = new GUIStyle(EditorStyles.miniButton)
                    {
                        border = new RectOffset(6, 6, 6, 6),
                        fixedHeight = EditorGUIUtility.singleLineHeight + 2,
                        alignment = TextAnchor.MiddleCenter,
                        margin = new RectOffset(2, 2, 2, 2)
                    };
                }
                return _tag;
            }
        }

        public static void DrawTag(TagInfo tagInfo, Action action = null)
        {
            DrawTag(tagInfo.Name, tagInfo.GetColor(), action, TagStyle.Remove);
        }

        public static void DrawTag(TagInfo tagInfo, Action action, float maxWidth)
        {
            DrawTag(tagInfo.Name, tagInfo.GetColor(), action, TagStyle.Remove, maxWidth);
        }

        public static float CalcTagWidth(string name, TagStyle style, float maxWidth = -1f)
        {
            float buttonWidth = style == TagStyle.Neutral ? 0f : EditorGUIUtility.singleLineHeight;
            float measuredWidth = tag.CalcSize(CommonUIStyles.Content(name)).x + buttonWidth;

            return maxWidth > 0f ? Mathf.Min(measuredWidth, maxWidth) : measuredWidth;
        }

        public static void DrawTag(string name, Color color, Action action, TagStyle style, float maxWidth = -1f)
        {
            Color oldColor = GUI.color;
            GUI.color = color;
            float tagWidth = CalcTagWidth(name, style, maxWidth);
            using (new EditorGUILayout.HorizontalScope(tag,
                       GUILayout.ExpandWidth(false), GUILayout.ExpandHeight(false),
                       GUILayout.Width(tagWidth)))
            {
                GUI.color = CommonUIStyles.GetHSPColor(color);
                GUIStyle readableText = CommonUIStyles.ReadableText(color);

                GUI.color = EditorGUIUtility.isProSkin ? Color.white : Color.black;
                readableText.normal.textColor = GUI.color;

                float buttonWidth = style == TagStyle.Neutral ? 0f : EditorGUIUtility.singleLineHeight;
                float textWidth = Mathf.Max(20f, tagWidth - buttonWidth - 8f);
                string displayName = TruncateWithEllipsis(name, readableText, textWidth);
                GUIContent tagContent = CommonUIStyles.Content(displayName, name);

                switch (style)
                {
                    case TagStyle.Add:
                        GUIContent addContent = CommonUIStyles.Content("+ " + displayName, name);
                        if (GUILayout.Button(addContent, readableText, GUILayout.Width(tagWidth), GUILayout.Height(EditorGUIUtility.singleLineHeight - 3)))
                        {
                            action?.Invoke();
                        }
                        break;

                    case TagStyle.Remove:
                        GUILayout.Label(tagContent, readableText, GUILayout.Width(textWidth));
                        GUI.color = oldColor;
                        if (GUILayout.Button(EditorGUIUtility.IconContent("TreeEditor.Trash", "|Remove Tag").image,
                                EditorStyles.label, GUILayout.Width(EditorGUIUtility.singleLineHeight),
                                GUILayout.Height(EditorGUIUtility.singleLineHeight)))
                        {
                            action?.Invoke();
                        }
                        break;

                    case TagStyle.Neutral:
                        GUILayout.Label(tagContent, readableText, GUILayout.Width(textWidth));
                        break;

                    case TagStyle.ColorSelect:
                        GUILayout.Label(tagContent, readableText, GUILayout.Width(textWidth));
                        if (GUILayout.Button(EditorGUIUtility.IconContent("TreeEditor.Trash", "|Remove Tag").image,
                                EditorStyles.label, GUILayout.Width(EditorGUIUtility.singleLineHeight),
                                GUILayout.Height(EditorGUIUtility.singleLineHeight)))
                        {
                            action?.Invoke();
                        }
                        break;
                }
            }
            GUI.color = oldColor;
        }

        private static string TruncateWithEllipsis(string text, GUIStyle style, float maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0f) return string.Empty;

            if (style.CalcSize(CommonUIStyles.Content(text)).x <= maxWidth) return text;

            const string ellipsis = "...";
            if (style.CalcSize(CommonUIStyles.Content(ellipsis)).x >= maxWidth) return ellipsis;

            int low = 0;
            int high = text.Length;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                string candidate = text.Substring(0, mid) + ellipsis;
                if (style.CalcSize(CommonUIStyles.Content(candidate)).x <= maxWidth)
                {
                    low = mid;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return text.Substring(0, low) + ellipsis;
        }

        public static void DrawTag(Rect rect, string name, Color color, TagStyle style)
        {
            float tagWidth = CalcTagWidth(name, style, rect.width);
            float tagHeight = Mathf.Min(rect.height, tag.fixedHeight);
            Rect tagRect = new Rect(rect.x, rect.y + (rect.height - tagHeight) / 2f, tagWidth, tagHeight);

            Color oldColor = GUI.color;
            GUI.color = color;
            GUI.Box(tagRect, GUIContent.none, tag);
            GUI.color = oldColor;

            float buttonWidth = style == TagStyle.Neutral ? 0f : EditorGUIUtility.singleLineHeight;
            float textWidth = Mathf.Max(8f, tagRect.width - buttonWidth - 8f);
            Rect textRect = new Rect(tagRect.x + 4f, tagRect.y, textWidth, tagRect.height);
            GUIStyle readableText = CommonUIStyles.ReadableText(color);
            readableText.normal.textColor = EditorGUIUtility.isProSkin ? Color.white : Color.black;
            readableText.alignment = TextAnchor.MiddleCenter;
            string displayName = TruncateWithEllipsis(name, readableText, textWidth);
            GUI.Label(textRect, CommonUIStyles.Content(displayName, name), readableText);
        }

        public static GUILayoutOption GetLabelMaxWidth()
        {
            return GUILayout.MaxWidth(INSPECTOR_WIDTH - 115);
        }

        public static GUIContent Content(string text, string tip, string ctrlText, string ctrlTip = null)
        {
            CommonUIStyles.GUIText.image = null;
            CommonUIStyles.GUIText.text = AI.ShowAdvanced() ? (string.IsNullOrEmpty(ctrlText) ? text : ctrlText) : text;
            CommonUIStyles.GUIText.tooltip = AI.ShowAdvanced() ? (string.IsNullOrEmpty(ctrlTip) ? tip : ctrlTip) : tip;
            return CommonUIStyles.GUIText;
        }
    }
}
