using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Blink.ThirdParty.EditorSceneLoader
{
    public class EditorSceneLoaderSettingsProvider : SettingsProvider
    {
        private SerializedObject serializedSettings;
        private SerializedProperty sceneEntriesProperty;

        public EditorSceneLoaderSettingsProvider(string path, SettingsScope scope = SettingsScope.Project)
            : base(path, scope) { }

        public override void OnActivate(string searchContext, VisualElement rootElement)
        {
            var settings = EditorSceneLoaderSettings.Instance;
            serializedSettings = new SerializedObject(settings);
            sceneEntriesProperty = serializedSettings.FindProperty("sceneEntries");
        }

        public override void OnGUI(string searchContext)
        {
            if (serializedSettings == null) return;

            EditorGUILayout.LabelField("Editor Scene Loader Settings", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUILayout.HelpBox(
                "Configure scenes that will appear in the Scene Loader dropdown in the toolbar. " +
                "This allows quick switching between frequently used scenes.", 
                MessageType.Info);

            EditorGUILayout.Space();

            serializedSettings.Update();

            EditorGUILayout.PropertyField(sceneEntriesProperty, new GUIContent("Scene Entries"), true);

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            
            if (GUILayout.Button("Add Scene Entry"))
            {
                var settings = EditorSceneLoaderSettings.Instance;
                settings.sceneEntries.Add(new SceneEntry());
                settings.Save();
            }

            if (GUILayout.Button("Remove Last Entry") && sceneEntriesProperty.arraySize > 0)
            {
                var settings = EditorSceneLoaderSettings.Instance;
                settings.sceneEntries.RemoveAt(settings.sceneEntries.Count - 1);
                settings.Save();
            }

            EditorGUILayout.EndHorizontal();

            if (serializedSettings.ApplyModifiedProperties())
            {
                EditorSceneLoaderSettings.Instance.Save();
            }
        }

        [SettingsProvider]
        public static SettingsProvider CreateSettingsProvider()
        {
            var provider = new EditorSceneLoaderSettingsProvider("Project/Editor Scene Loader", SettingsScope.Project)
            {
                keywords = new[] { "scene", "loader", "editor", "toolbar", "quick", "switch" }
            };
            return provider;
        }
    }
}