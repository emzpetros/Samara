using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;

namespace Blink.ThirdParty.EditorSceneLoader
{
    public class EditorSceneLoaderToolbar
    {
        [MainToolbarElement("Spark/Quick Scene Loader", defaultDockPosition = MainToolbarDockPosition.Left)]
        public static MainToolbarElement CreateSceneLoaderDropdown()
        {
            var sceneIcon = EditorGUIUtility.IconContent("SceneAsset Icon").image as Texture2D;
            
            MainToolbarContent content;
            if (sceneIcon != null)
            {
                content = new MainToolbarContent(sceneIcon);
            }
            else
            {
                content = new MainToolbarContent("Scene");
            }

            var dropdown = new MainToolbarDropdown(content, ShowSceneDropdown);
            
            return dropdown;
        }

        private static void ShowSceneDropdown(Rect dropdownRect)
        {
            var settings = EditorSceneLoaderSettings.Instance;
            var menu = new GenericMenu();

            if (settings.sceneEntries == null || settings.sceneEntries.Count == 0)
            {
                menu.AddItem(new GUIContent("No scenes configured"), false, OpenProjectSettings);
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Open Project Settings"), false, OpenProjectSettings);
            }
            else
            {
                foreach (var entry in settings.sceneEntries)
                {
                    if (entry.scene != null)
                    {
                        var displayName = string.IsNullOrEmpty(entry.displayName) ? entry.scene.name : entry.displayName;
                        var scenePath = AssetDatabase.GetAssetPath(entry.scene);
                        menu.AddItem(new GUIContent(displayName), false, () => LoadScene(scenePath, displayName));
                    }
                }
                
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Configure Scenes..."), false, OpenProjectSettings);
            }

            menu.DropDown(dropdownRect);
        }

        private static void LoadScene(string scenePath, string displayName)
        {
            if (string.IsNullOrEmpty(scenePath))
            {
                Debug.LogWarning("Scene path is empty or null");
                return;
            }

            if (!System.IO.File.Exists(scenePath))
            {
                Debug.LogWarning($"Scene file not found: {scenePath}");
                return;
            }

            if (EditorSceneManager.GetActiveScene().isDirty)
            {
                var result = EditorUtility.DisplayDialogComplex(
                    "Unsaved Changes",
                    "Current scene has unsaved changes. Save before loading new scene?",
                    "Save and Load",
                    "Load Without Saving",
                    "Cancel");

                switch (result)
                {
                    case 0:
                        EditorSceneManager.SaveOpenScenes();
                        break;
                    case 1:
                        break;
                    case 2:
                        return;
                }
            }

            EditorSceneManager.OpenScene(scenePath);
        }

        private static void OpenProjectSettings()
        {
            SettingsService.OpenProjectSettings("Project/Editor Scene Loader");
        }
    }
}