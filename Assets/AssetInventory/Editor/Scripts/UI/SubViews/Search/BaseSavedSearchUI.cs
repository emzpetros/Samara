using System;
using ImpossibleRobert.Common;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    public abstract class BaseSavedSearchUI<T> : BasicEditorUI where T : class
    {
        protected T _savedSearch;
        private Action<T> _onSave;

        public void Init(T savedSearch, Action<T> onSave = null)
        {
            _savedSearch = savedSearch;
            _onSave = onSave;
        }

        protected abstract string GetName();
        protected abstract void SetName(string searchName);
        protected abstract string GetIcon();
        protected abstract void SetIcon(string icon);
        protected abstract string GetColor();
        protected abstract void SetColor(string color);
        protected abstract string GetSearchPhrase();
        protected abstract string GetSearchDetails();
        protected abstract void UpdateDatabase();

        public override void OnGUI()
        {
            int labelWidth = 100;

            if (_savedSearch == null)
            {
                Close();
                return;
            }

            GUILayout.BeginVertical("box");

            // Display search phrase (read-only)
            string searchPhrase = GetSearchPhrase();
            if (!string.IsNullOrEmpty(searchPhrase))
            {
                GUILabelWithTextNoMax("Search Phrase", searchPhrase, labelWidth, null, true);
            }

            // Display search details if available
            string details = GetSearchDetails();
            if (!string.IsNullOrEmpty(details))
            {
                GUILabelWithTextNoMax("Filters", details, labelWidth, null, true);
            }

            GUILayout.EndVertical();
            EditorGUILayout.Space();

            // Name field
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Name", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
            SetName(EditorGUILayout.TextField(GetName()));
            GUILayout.EndHorizontal();

            // Color picker
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Color", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
            Color currentColor = Color.white;
            string colorStr = GetColor();
            if (!string.IsNullOrEmpty(colorStr))
            {
                ColorUtility.TryParseHtmlString("#" + colorStr, out currentColor);
            }
            EditorGUI.BeginChangeCheck();
            Color newColor = EditorGUILayout.ColorField(GUIContent.none, currentColor, false, false, false, GUILayout.Width(20));
            if (EditorGUI.EndChangeCheck())
            {
                SetColor(ColorUtility.ToHtmlStringRGB(newColor));
            }
            GUILayout.EndHorizontal();

            // Icon selection
            GUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Icon", EditorStyles.boldLabel, GUILayout.Width(labelWidth));

            string icon = GetIcon();
            if (!string.IsNullOrEmpty(icon))
            {
                GUIContent iconContent = EditorGUIUtility.IconContent(icon);
                GUILayout.Label(iconContent, GUILayout.Width(24), GUILayout.Height(24));
            }
            else
            {
                GUILayout.Label("-No Icon-", GUILayout.ExpandWidth(true));
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Select...", GUILayout.ExpandWidth(false)))
            {
                IconSelectionUI iconSelectionUI = new IconSelectionUI();
                iconSelectionUI.Init(iconName => SetIcon(iconName));
                PopupWindow.Show(GetPopupPositionAtMouse(), iconSelectionUI);
            }
            if (!string.IsNullOrEmpty(icon) && GUILayout.Button("Clear", GUILayout.ExpandWidth(false)))
            {
                SetIcon(null);
            }
            GUILayout.EndHorizontal();

            // Action Buttons
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Update", CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT)))
            {
                if (string.IsNullOrWhiteSpace(GetName()) && string.IsNullOrWhiteSpace(GetIcon()))
                {
                    EditorUtility.DisplayDialog("Invalid Name", "Please enter a name or set an icon for the saved search.", "OK");
                    return;
                }

                UpdateDatabase();
                _onSave?.Invoke(_savedSearch);

                Close();
            }
        }
    }
}
