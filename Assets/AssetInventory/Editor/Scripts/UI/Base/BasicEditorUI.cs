using System;
using System.IO;
using ImpossibleRobert.Common;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    public abstract class BasicEditorUI : EditorWindow
    {
        public static Texture2D Logo
        {
            get
            {
                if (_logo == null) _logo = CommonUIStyles.LoadTexture("AssetInventory");
                return _logo;
            }
        }
        private static Texture2D _logo;

        public virtual void OnGUI()
        {
            EditorGUILayout.Space();
        }

        protected static void GUILabelWithText(string label, string text, int width = 95, string tooltip = null, bool wrappable = false, bool selectable = false)
        {
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(CommonUIStyles.Content(label, string.IsNullOrWhiteSpace(tooltip) ? label : tooltip), EditorStyles.boldLabel, GUILayout.Width(width));
            if (selectable)
            {
                EditorGUILayout.SelectableLabel(text, wrappable ? EditorStyles.wordWrappedLabel : EditorStyles.label, GUILayout.MaxWidth(UIStyles.INSPECTOR_WIDTH - width - 20), GUILayout.ExpandWidth(false), GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
            else
            {
                EditorGUILayout.LabelField(CommonUIStyles.Content(text, string.IsNullOrWhiteSpace(tooltip) ? text : tooltip), wrappable ? EditorStyles.wordWrappedLabel : EditorStyles.label, GUILayout.MaxWidth(UIStyles.INSPECTOR_WIDTH - width - 20), GUILayout.ExpandWidth(false));
            }
            GUILayout.EndHorizontal();
        }

        protected static void GUILabelWithTextNoMax(string label, string text, int width = 95, string tooltip = null, bool wrappable = false, bool selectable = false)
        {
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(CommonUIStyles.Content(label, string.IsNullOrWhiteSpace(tooltip) ? label : tooltip), EditorStyles.boldLabel, GUILayout.Width(width));
            if (selectable)
            {
                EditorGUILayout.SelectableLabel(text, wrappable ? EditorStyles.wordWrappedLabel : EditorStyles.label, GUILayout.ExpandWidth(true), GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
            else
            {
                EditorGUILayout.LabelField(CommonUIStyles.Content(text, string.IsNullOrWhiteSpace(tooltip) ? text : tooltip), wrappable ? EditorStyles.wordWrappedLabel : EditorStyles.label, GUILayout.ExpandWidth(true));
            }
            GUILayout.EndHorizontal();
        }

        protected static void UILine(string key, Action content, bool alwaysShow = false)
        {
            UIBlock(key, content, alwaysShow, true);
        }

        protected static void UISection(string group, string key, Action content)
        {
            if (AI.UICustomizationMode)
            {
                UISection section = AI.Config.GetSection(group);

                Color oldCol = GUI.backgroundColor;
                GUI.backgroundColor = Color.yellow;
                GUILayout.BeginVertical("box");
                GUI.backgroundColor = oldCol;
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();

                Color oldCol2 = GUI.backgroundColor;
                GUI.backgroundColor = EditorGUIUtility.isProSkin ? Color.black : Color.white;
                if (!section.IsFirst(key) && GUILayout.Button("Up", EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                {
                    section.MoveUp(key);
                    AI.SaveConfig();
                }
                if (!section.IsLast(key) && GUILayout.Button("Down", EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                {
                    section.MoveDown(key);
                    AI.SaveConfig();
                }
                GUI.backgroundColor = oldCol2;
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                content();
                GUILayout.EndVertical();
            }
            else
            {
                content();
            }
        }

        protected static void UIBlock2(string key, Action content, bool alwaysShow = false)
        {
            UIBlock(key, content, alwaysShow, false, false);
        }

        protected static void UIBlock(string key, Action content, bool alwaysShow = false, bool horizontal = false, bool expand = true)
        {
            AssetInventorySettings c = AI.Config;
            bool isAdvanced = c.advancedUI.Contains(key);

            if (AI.UICustomizationMode)
            {
                Color oldCol = GUI.backgroundColor;
                GUI.backgroundColor = isAdvanced ? Color.red : Color.green;
                GUILayout.BeginVertical("box");
                GUI.backgroundColor = oldCol;
                if (horizontal)
                {
                    GUILayout.BeginHorizontal();
                    content();
                }
                else
                {
                    content();
                    GUILayout.BeginHorizontal();
                    if (expand) GUILayout.FlexibleSpace();
                }
                Color oldCol2 = GUI.backgroundColor;
                GUI.backgroundColor = EditorGUIUtility.isProSkin ? Color.black : Color.white;
                if (GUILayout.Button(isAdvanced ? "Show" : "Hide", EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                {
                    if (isAdvanced)
                    {
                        c.advancedUI.Remove(key);
                    }
                    else
                    {
                        c.advancedUI.Add(key);
                    }
                    AI.SaveConfig();
                }
                GUI.backgroundColor = oldCol2;
                EditorGUILayout.Space(horizontal ? 1 : 12);
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }
            else
            {
                if (!alwaysShow && isAdvanced && !ShowAdvanced()) return;

                content();
            }
        }

        /// <summary>
        /// Displays a text field with an edit button that opens a StringListUI popup for managing comma/semicolon-separated values.
        /// </summary>
        /// <param name="label">The label to display</param>
        /// <param name="value">Current comma/semicolon-separated value</param>
        /// <param name="setValue">Callback to update the value</param>
        /// <param name="separator">Separator character (e.g., "," or ";")</param>
        /// <param name="title">Title for the popup window</param>
        /// <param name="labelWidth">Width of the label (optional)</param>
        /// <param name="tooltip">Tooltip text (optional)</param>
        /// <returns>The updated value</returns>
        public static string GUIStringListField(string label, string value, Action<string> setValue, string separator = ",", string title = "Items", int labelWidth = 140, string tooltip = null)
        {
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(CommonUIStyles.Content(label, tooltip ?? label), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
            string newValue = EditorGUILayout.TextField(value);
            if (GUILayout.Button(EditorGUIUtility.IconContent("editicon.sml", "|Edit list"), GUILayout.Width(24)))
            {
                StringListUI listUI = new StringListUI();
                listUI.Init(value, separator, result =>
                {
                    setValue?.Invoke(result);
                }, title);
                PopupWindow.Show(GetPopupPositionAtMouse(), listUI);
            }
            GUILayout.EndHorizontal();
            return newValue;
        }

        protected void DrawFolder(string caption, string curFolder, string defaultFolder, Action<string> newFolder, int labelWidth, string prompt = "Select folder", Func<string, bool> validator = null, bool leaveOpen = false)
        {
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(caption, EditorStyles.boldLabel, GUILayout.Width(labelWidth));
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField(string.IsNullOrWhiteSpace(curFolder) ? "[Default]" + (defaultFolder != null ? $" {defaultFolder}" : "") : curFolder, GUILayout.ExpandWidth(true));
            EditorGUI.EndDisabledGroup();
            if (newFolder != null)
            {
                if (GUILayout.Button("Select...", GUILayout.ExpandWidth(false)))
                {
                    SelectFolder(curFolder, newFolder, prompt, validator);
                }
                if (!string.IsNullOrWhiteSpace(curFolder))
                {
                    if (GUILayout.Button("Clear", GUILayout.ExpandWidth(false)))
                    {
                        newFolder.Invoke(null);
                        AI.SaveConfig();
                    }
                }
            }
            string usedFolder = curFolder != null ? curFolder : defaultFolder;
            if (!string.IsNullOrWhiteSpace(usedFolder))
            {
                if (GUILayout.Button("Open", GUILayout.ExpandWidth(false))) EditorUtility.RevealInFinder(usedFolder);
            }
            if (!leaveOpen) GUILayout.EndHorizontal();
        }

        private void SelectFolder(string oldValue, Action<string> newValue, string prompt = "Select folder", Func<string, bool> validator = null)
        {
            string folder = EditorUtility.OpenFolderPanel(prompt, oldValue, "");
            if (!string.IsNullOrEmpty(folder))
            {
                string fullPath = Path.GetFullPath(folder);

                if (validator != null && !validator(fullPath)) return;

                newValue?.Invoke(fullPath);
                AI.SaveConfig();
            }
        }

        protected static void BeginIndentBlock(int widthOverride = 0)
        {
            GUILayout.BeginHorizontal();
            EditorGUILayout.Space(widthOverride > 0 ? widthOverride : CommonUIStyles.INDENT_WIDTH, false);
            GUILayout.BeginVertical();
        }

        protected static void EndIndentBlock(bool autoSpace = true)
        {
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            if (autoSpace) EditorGUILayout.Space();
        }

        /// <summary>
        /// Gets a popup position at the current mouse location.
        /// </summary>
        public static Rect GetPopupPositionAtMouse()
        {
            return new Rect(Event.current.mousePosition.x, Event.current.mousePosition.y, 0, 0);
        }

        // shortcut
        protected static bool ShowAdvanced() => AI.ShowAdvanced();
    }
}