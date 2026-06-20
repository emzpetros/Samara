using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace Blink.ThirdParty.EditorSceneLoader
{
    [Serializable]
    public class SceneEntry
    {
        [SerializeField] public string displayName;
        [SerializeField] public SceneAsset scene;

        public SceneEntry()
        {
            displayName = "";
            scene = null;
        }

        public SceneEntry(string displayName, SceneAsset scene)
        {
            this.displayName = displayName;
            this.scene = scene;
        }
    }

    public class EditorSceneLoaderSettings : ScriptableObject
    {
        [SerializeField] 
        public List<SceneEntry> sceneEntries = new List<SceneEntry>();

        private static EditorSceneLoaderSettings instance;
        
        public static EditorSceneLoaderSettings Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = CreateInstance<EditorSceneLoaderSettings>();
                    LoadOrCreateSettings();
                }
                return instance;
            }
        }

        private static void LoadOrCreateSettings()
        {
            var guids = AssetDatabase.FindAssets("t:EditorSceneLoaderSettings");
            EditorSceneLoaderSettings settings = null;
            
            if (guids.Length > 0)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                settings = AssetDatabase.LoadAssetAtPath<EditorSceneLoaderSettings>(path);
            }
            
            if (settings == null)
            {
                settings = CreateInstance<EditorSceneLoaderSettings>();
                settings.sceneEntries = new List<SceneEntry>();
                
                var scriptPath = AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(settings));
                if (string.IsNullOrEmpty(scriptPath))
                {
                    var monoScripts = AssetDatabase.FindAssets("t:MonoScript EditorSceneLoaderSettings");
                    if (monoScripts.Length > 0)
                    {
                        scriptPath = AssetDatabase.GUIDToAssetPath(monoScripts[0]);
                    }
                }
                
                string settingsDirectory;
                if (!string.IsNullOrEmpty(scriptPath))
                {
                    settingsDirectory = System.IO.Path.GetDirectoryName(scriptPath);
                }
                else
                {
                    settingsDirectory = "Assets/Blink/Spark/Templates/StarterKit/Tools/EditorSceneLoader/Editor";
                }
                
                if (!AssetDatabase.IsValidFolder(settingsDirectory))
                {
                    var parts = settingsDirectory.Split('/');
                    string currentPath = parts[0];
                    
                    for (int i = 1; i < parts.Length; i++)
                    {
                        string nextPath = currentPath + "/" + parts[i];
                        if (!AssetDatabase.IsValidFolder(nextPath))
                        {
                            AssetDatabase.CreateFolder(currentPath, parts[i]);
                        }
                        currentPath = nextPath;
                    }
                }
                
                var settingsPath = settingsDirectory + "/EditorSceneLoaderSettings.asset";
                AssetDatabase.CreateAsset(settings, settingsPath);
                AssetDatabase.SaveAssets();
            }
            instance = settings;
        }

        public void Save()
        {
            EditorUtility.SetDirty(this);
            AssetDatabase.SaveAssets();
        }
    }
}