using ImpossibleRobert.Common;
using UnityEditor;
#if !USE_TUTORIALS
using UnityEditor.PackageManager;
#endif
using UnityEngine;

namespace AssetInventory
{
    public partial class IndexUI
    {
        private Vector2 _aboutScrollPos;

        private void DrawAboutTab()
        {
            _aboutScrollPos = EditorGUILayout.BeginScrollView(_aboutScrollPos);
            AboutWindow.DrawContent("AssetInventory", DrawAssetInventoryCustomSection);
            EditorGUILayout.EndScrollView();
        }

        private void DrawAssetInventoryCustomSection()
        {
#if !USE_TUTORIALS
            // Tutorials CTA
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.MaxWidth(480));
            EditorGUILayout.LabelField("Tutorials", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Integrated tutorials require the Unity Tutorials package.", EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(2);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(CommonUIStyles.Content("Install/Upgrade Tutorials Package...", "Integrated tutorials require the Unity Tutorials package installed."), GUILayout.Width(280)))
            {
                Client.Add($"com.unity.learn.iet-framework@{AI.TUTORIALS_VERSION}");
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            EditorGUILayout.Space(6);
#endif

            // Maintenance section
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (ShowAdvanced())
            {
                GUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.MaxWidth(480));
                EditorGUILayout.LabelField("Maintenance", EditorStyles.boldLabel);
                EditorGUILayout.Space(2);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Show Welcome Dialog", GUILayout.Width(180))) WelcomeWindow.ShowWindow();
                if (GUILayout.Button("Restart Setup Wizard", GUILayout.Width(180)))
                {
                    AI.Config.wizardCompleted = false;
                    AI.Config.wizardCurrentPage = 0;
                    AI.SaveConfig();
                }
                if (GUILayout.Button("Create Debug Support Report", GUILayout.Width(220))) CreateDebugReport();
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (AI.DEBUG_MODE && GUILayout.Button("Reload Lookups")) ReloadLookups();
            if (AI.DEBUG_MODE && GUILayout.Button("Get Token", GUILayout.ExpandWidth(false))) Debug.Log(CloudProjectSettings.accessToken);
            if (AI.DEBUG_MODE && GUILayout.Button("Free Memory")) Resources.UnloadUnusedAssets();
        }
    }
}
