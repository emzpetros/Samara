// reference for built-in icons: https://github.com/halak/unity-editor-icons
// new version: https://github.com/Doppelkeks/Unity-Editor-Icons/tree/2019.4

using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace ImpossibleRobert.Common
{
    /// <summary>
    /// Generic UI styles for editor tools. Can be extended by specific tools.
    /// </summary>
    public static class CommonUIStyles
    {
        public const string INDENT = "  ";
        public const int INDENT_WIDTH = 8;
        public const float BIG_BUTTON_HEIGHT = 30f;

        public static readonly Color errorColor = EditorGUIUtility.isProSkin ? new Color(1f, 0.5f, 0.5f) : Color.red;

        #region Semantic Colors
        static Color Semantic(Color dark, Color light) => EditorGUIUtility.isProSkin ? dark : light;

        // Text hierarchy
        public static Color ColorLabel => Semantic(new Color(0.90f, 0.90f, 0.90f), new Color(0.13f, 0.13f, 0.13f));
        public static Color ColorSecondaryLabel => Semantic(new Color(0.62f, 0.62f, 0.64f), new Color(0.40f, 0.40f, 0.42f));

        // Accent
        public static Color ColorAccent => Semantic(new Color(0.20f, 0.48f, 0.95f), new Color(0.16f, 0.44f, 0.93f));
        #endregion

        #region Spacing Scale (4px grid)
        public const int SpaceSM = 4;
        public const int SpaceMD = 8;
        public const int SpaceLG = 12;
        #endregion

        #region Typography Scale
        public const int FontSubheading = 13;
        #endregion

        #region UI Helpers
        private static GUIStyle _tabNormal;
        private static GUIStyle _tabSelected;

        private static void EnsureTabStyles()
        {
            if (_tabNormal != null) return;

            _tabNormal = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = FontSubheading,
                fontStyle = FontStyle.Normal,
                padding = new RectOffset(SpaceLG, SpaceLG, SpaceSM, SpaceMD),
                margin = new RectOffset(0, 0, 0, 0)
            };
            _tabNormal.normal.textColor = ColorSecondaryLabel;

            _tabSelected = new GUIStyle(_tabNormal)
            {
                fontStyle = FontStyle.Bold
            };
            _tabSelected.normal.textColor = ColorLabel;
        }

        public static int DrawTabBar(int selected, string[] labels, params GUILayoutOption[] options)
        {
            EnsureTabStyles();

            Rect barRect = EditorGUILayout.BeginHorizontal(GUILayout.Height(32));
            GUILayout.FlexibleSpace();

            for (int i = 0; i < labels.Length; i++)
            {
                GUIStyle style = i == selected ? _tabSelected : _tabNormal;
                if (GUILayout.Button(Content(labels[i]), style, GUILayout.Height(30)))
                {
                    selected = i;
                }
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (Event.current.type == EventType.Repaint)
            {
                // Accent indicator under selected tab — measure position
                float x = barRect.x;
                float totalTabWidth = 0;
                for (int i = 0; i < labels.Length; i++)
                {
                    GUIStyle measureStyle = i == selected ? _tabSelected : _tabNormal;
                    totalTabWidth += measureStyle.CalcSize(Content(labels[i])).x;
                }
                float remainingSpace = barRect.width - totalTabWidth;
                x += remainingSpace / 2f; // account for FlexibleSpace before tabs

                for (int i = 0; i < labels.Length; i++)
                {
                    GUIStyle style = i == selected ? _tabSelected : _tabNormal;
                    float tabWidth = style.CalcSize(Content(labels[i])).x;
                    if (i == selected)
                    {
                        float indicatorInset = SpaceMD;
                        Rect indicatorRect = new Rect(x + indicatorInset, barRect.yMax - 7, tabWidth - indicatorInset * 2, 3);
                        EditorGUI.DrawRect(indicatorRect, ColorAccent);
                        break;
                    }
                    x += tabWidth;
                }
            }

            return selected;
        }
        #endregion

        public static readonly GUIContent GUIText = new GUIContent();
        private static readonly GUIContent GUIImage = new GUIContent();
        private static readonly GUIContent GUITextImage = new GUIContent();

        private static GUIStyle _wrappedLinkLabel;
        public static GUIStyle wrappedLinkLabel
        {
            get
            {
                if (_wrappedLinkLabel == null)
                {
                    _wrappedLinkLabel = new GUIStyle(EditorStyles.linkLabel)
                    {
                        wordWrap = true
                    };
                }
                return _wrappedLinkLabel;
            }
        }

        private static GUIStyle _greyMiniLabel;
        public static GUIStyle greyMiniLabel
        {
            get
            {
                if (_greyMiniLabel == null)
                {
                    _greyMiniLabel = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                    {
                        alignment = TextAnchor.MiddleLeft,
                        wordWrap = true
                    };
                }
                return _greyMiniLabel;
            }
        }

        private static GUIStyle _wrappedButton;
        public static GUIStyle wrappedButton
        {
            get
            {
                if (_wrappedButton == null)
                {
                    _wrappedButton = new GUIStyle(GUI.skin.button) {wordWrap = true};
                }
                return _wrappedButton;
            }
        }

        private static GUIStyle _wrappedTextArea;
        public static GUIStyle wrappedTextArea
        {
            get
            {
                if (_wrappedTextArea == null)
                {
                    _wrappedTextArea = new GUIStyle(EditorStyles.textArea) {wordWrap = true};
                }
                return _wrappedTextArea;
            }
        }

        private static GUIStyle _richText;
        public static GUIStyle richText
        {
            get
            {
                if (_richText == null)
                {
                    _richText = new GUIStyle(EditorStyles.wordWrappedLabel) {richText = true};
                }
                return _richText;
            }
        }
        private static GUIStyle _miniLabelRight;
        public static GUIStyle miniLabelRight
        {
            get { return _miniLabelRight ?? (_miniLabelRight = new GUIStyle(EditorStyles.miniLabel) {alignment = TextAnchor.MiddleRight}); }
        }
        private static readonly Func<Rect> getVisibleRect;

        static CommonUIStyles()
        {
            // cache the visible rect getter for performance
            Type clipType = typeof (GUI).Assembly.GetType("UnityEngine.GUIClip");
            PropertyInfo prop = clipType.GetProperty("visibleRect", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo getter = prop.GetGetMethod(true);
            getVisibleRect = (Func<Rect>)Delegate.CreateDelegate(typeof (Func<Rect>), getter);
        }

        public static Rect GetCurrentVisibleRect() => getVisibleRect();

        private static GUIStyle _mainButton;
        public static GUIStyle mainButton
        {
            get
            {
                if (_mainButton == null) CreateMainButton();
                return _mainButton;
            }
        }

        private static GUIStyle _toggleButton;
        public static GUIStyle toggleButton
        {
            get
            {
                if (_toggleButton == null)
                {
                    _toggleButton = new GUIStyle(EditorStyles.miniButton);
                }
                return _toggleButton;
            }
        }

        private static GUIStyle _toggleButtonActive;
        public static GUIStyle toggleButtonActive
        {
            get
            {
                if (_toggleButtonActive == null)
                {
                    _toggleButtonActive = new GUIStyle(EditorStyles.miniButton);
                    _toggleButtonActive.normal.background = _toggleButtonActive.active.background;
                }
                return _toggleButtonActive;
            }
        }

        private static GUIStyle _selectableLabel;
        public static GUIStyle selectableLabel
        {
            get
            {
                if (_selectableLabel == null)
                {
                    _selectableLabel = new GUIStyle(EditorStyles.label)
                    {
                        wordWrap = true, margin = new RectOffset(0, 0, 0, 0), padding = new RectOffset(0, 0, 0, 0)
                    };
                }
                return _selectableLabel;
            }
        }

        private static Texture2D _bg;
        private static Texture2D _bgHover;
        private static Texture2D _bgActive;


        private static void CreateMainButton()
        {
            Color baseCol = EditorGUIUtility.isProSkin ? new Color(0.20f, 0.48f, 0.95f) : new Color(0.16f, 0.44f, 0.93f);
            Color hoverCol = Color.Lerp(baseCol, Color.white, 0.15f);
            Color activeCol = Color.Lerp(baseCol, Color.black, 0.20f);

            _bg = MakeTex(baseCol);
            _bgHover = MakeTex(hoverCol);
            _bgActive = MakeTex(activeCol);

            GUIStyle s = new GUIStyle();
            s.normal.background = _bg;
            s.normal.textColor = Color.white;
            s.hover.background = _bgHover;
            s.hover.textColor = Color.white;
            s.active.background = _bgActive;
            s.active.textColor = Color.white;
            s.focused.background = _bg;
            s.focused.textColor = Color.white;
            s.onNormal.background = _bg;
            s.onNormal.textColor = Color.white;
            s.onHover.background = _bgHover;
            s.onHover.textColor = Color.white;
            s.onActive.background = _bgActive;
            s.onActive.textColor = Color.white;
            s.onFocused.background = _bg;
            s.onFocused.textColor = Color.white;
            s.border = new RectOffset(0, 0, 0, 0);
            s.margin = new RectOffset(4, 4, 3, 4);
            s.padding = new RectOffset(3, 3, 1, 2);
            s.contentOffset = Vector2.zero;
            s.alignment = TextAnchor.MiddleCenter;
            s.fontStyle = FontStyle.Normal;
            s.imagePosition = ImagePosition.TextOnly;
            s.clipping = TextClipping.Clip;

            _mainButton = s;
        }

        private static Texture2D MakeTex(Color c)
        {
            Texture2D t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.hideFlags = HideFlags.HideAndDontSave;
            t.wrapMode = TextureWrapMode.Repeat;
            t.filterMode = FilterMode.Point;
            t.SetPixel(0, 0, c);
            t.Apply(false, true);

            return t;
        }

        public static bool MainButton(ref bool mainUsed, string text, params GUILayoutOption[] options)
        {
            return MainButton(ref mainUsed, Content(text), options);
        }

        public static bool MainButton(ref bool mainUsed, GUIContent content, params GUILayoutOption[] options)
        {
            bool result;
            if (mainUsed)
            {
                result = GUILayout.Button(content, options);
            }
            else
            {
                result = GUILayout.Button(content, mainButton, options);
            }
            mainUsed = true;

            return result;
        }

        public static Texture2D LoadTexture(string name)
        {
            string[] guids = AssetDatabase.FindAssets("t:Texture2d " + name);
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string fileName = System.IO.Path.GetFileNameWithoutExtension(path);
                if (string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                }
            }
            // Fallback: return first match if no exact match found
            if (guids.Length > 0) return AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(guids[0]));
            return null;
        }

        private static GUIStyle _whiteCenter;
        public static GUIStyle whiteCenter
        {
            get { return _whiteCenter ?? (_whiteCenter = new GUIStyle {alignment = TextAnchor.MiddleCenter, normal = new GUIStyleState {textColor = Color.white}}); }
        }
        private static GUIStyle _blackCenter;
        public static GUIStyle blackCenter
        {
            get { return _blackCenter ?? (_blackCenter = new GUIStyle {alignment = TextAnchor.MiddleCenter, normal = new GUIStyleState {textColor = Color.black}}); }
        }
        private static GUIStyle _centerLabel;
        public static GUIStyle centerLabel
        {
            get { return _centerLabel ?? (_centerLabel = new GUIStyle(GUI.skin.label) {alignment = TextAnchor.MiddleCenter}); }
        }

        private static GUIStyle _centerHeading;
        public static GUIStyle centerHeading
        {
            get { return _centerHeading ?? (_centerHeading = new GUIStyle(GUI.skin.label) {alignment = TextAnchor.MiddleCenter, fontSize = 20, fontStyle = FontStyle.Bold}); }
        }
        private static GUIStyle _centeredWhiteMiniLabel;
        public static GUIStyle centeredWhiteMiniLabel
        {
            get { return _centeredWhiteMiniLabel ?? (_centeredWhiteMiniLabel = new GUIStyle(EditorStyles.centeredGreyMiniLabel) {normal = new GUIStyleState {textColor = Color.white}}); }
        }

        private static GUIStyle _centeredGreyWrappedMiniLabel;
        public static GUIStyle centeredGreyWrappedMiniLabel
        {
            get { return _centeredGreyWrappedMiniLabel ?? (_centeredGreyWrappedMiniLabel = new GUIStyle(EditorStyles.centeredGreyMiniLabel) {wordWrap = true}); }
        }

        private static GUIStyle _rightLabel;
        public static GUIStyle rightLabel
        {
            get { return _rightLabel ?? (_rightLabel = new GUIStyle(GUI.skin.label) {alignment = TextAnchor.MiddleRight}); }
        }

        private static GUIStyle _centerLinkLabel;
        public static GUIStyle centerLinkLabel
        {
            get { return _centerLinkLabel ?? (_centerLinkLabel = new GUIStyle(EditorStyles.linkLabel) {alignment = TextAnchor.MiddleCenter}); }
        }

        private static GUIStyle _centerPopup;
        public static GUIStyle centerPopup
        {
            get { return _centerPopup ?? (_centerPopup = new GUIStyle(EditorStyles.popup) {alignment = TextAnchor.MiddleCenter}); }
        }

        public static Color GetHSPColor(Color color)
        {
            // http://alienryderflex.com/hsp.html
            return 0.299 * color.r + 0.587 * color.g + 0.114 * color.b < 0.5f ? Color.white : new Color(0.1f, 0.1f, 0.1f);
        }

        public static GUIStyle ReadableText(Color color, bool wrap = false)
        {
            GUIStyle style = new GUIStyle(EditorStyles.label);
            style.normal.textColor = GetHSPColor(color);
            style.wordWrap = wrap;
            return style;
        }

        public static GUIStyle ColoredText(Color color, bool wrapped = false)
        {
            GUIStyle style = new GUIStyle(wrapped ? EditorStyles.wordWrappedLabel : EditorStyles.label);
            style.normal.textColor = color;
            return style;
        }

        public static void DrawProgressBar(float percentage, string text, params GUILayoutOption[] options)
        {
            Rect r = EditorGUILayout.BeginVertical(options);
            EditorGUI.ProgressBar(r, percentage, text);
            GUILayout.Space(EditorGUIUtility.singleLineHeight);
            EditorGUILayout.EndVertical();
        }

        public static void DrawUILine(Color color, int thickness = 2, int padding = 10)
        {
            Rect r = EditorGUILayout.GetControlRect(GUILayout.Height(padding + thickness));
            r.height = thickness;
            r.y += padding / 2f;
            r.x -= 2;
            r.width += 6;
            EditorGUI.DrawRect(r, color);
        }


        public static GUIContent Content(string text, string tip = null)
        {
            GUIText.image = null;
            GUIText.text = text;
            GUIText.tooltip = tip;
            return GUIText;
        }

        public static GUIContent Content(Texture texture)
        {
            GUIImage.image = texture;
            GUIImage.text = null;
            GUIImage.tooltip = null;
            return GUIImage;
        }

        public static GUIContent Content(string text, Texture texture, string tip = null)
        {
            GUITextImage.image = texture;
            GUITextImage.text = " " + text; // otherwise text too close to image
            GUITextImage.tooltip = tip;
            return GUITextImage;
        }

        public static GUIContent IconContent(string name, string darkName, string tooltip = null)
        {
            if (EditorGUIUtility.isProSkin) return EditorGUIUtility.IconContent(darkName, tooltip);
            return EditorGUIUtility.IconContent(name, tooltip);
        }

        private static GUIStyle _sectionBox;
        private static Texture2D _sectionBoxBg;
        public static GUIStyle sectionBox
        {
            get
            {
                if (_sectionBox == null)
                {
                    _sectionBox = new GUIStyle(GUI.skin.box);
                    _sectionBox.padding = new RectOffset(8, 8, 8, 8);
                    _sectionBox.margin = new RectOffset(0, 0, 0, 0);

                    // Create a subtle background texture
                    Color backgroundColor = EditorGUIUtility.isProSkin
                        ? new Color(0.25f, 0.25f, 0.25f, 0.3f)
                        : new Color(0.7f, 0.7f, 0.7f, 0.3f);

                    _sectionBoxBg = new Texture2D(1, 1);
                    _sectionBoxBg.hideFlags = HideFlags.HideAndDontSave;
                    _sectionBoxBg.SetPixel(0, 0, backgroundColor);
                    _sectionBoxBg.Apply();

                    _sectionBox.normal.background = _sectionBoxBg;
                }
                return _sectionBox;
            }
        }
    }
}
