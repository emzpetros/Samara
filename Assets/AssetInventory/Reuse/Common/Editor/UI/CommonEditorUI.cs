using UnityEditor;
using UnityEngine;

namespace ImpossibleRobert.Common
{
    /// <summary>
    /// Base class for editor windows with common utility methods.
    /// </summary>
    public abstract class CommonEditorUI : EditorWindow
    {
        /// <summary>
        /// Draws a label with text value side by side.
        /// </summary>
        /// <param name="label">The label text</param>
        /// <param name="value">The value text</param>
        /// <param name="labelWidth">Width of the label</param>
        /// <param name="maxWidth">Maximum width for the value</param>
        protected void GUILabelWithText(string label, string value, int labelWidth = 85, int maxWidth = 500)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel, GUILayout.Width(labelWidth));
            EditorGUILayout.LabelField(value, GUILayout.MaxWidth(maxWidth));
            EditorGUILayout.EndHorizontal();
        }
    }
}