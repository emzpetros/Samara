using System;
using Brain;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    public sealed class AdvancedSearchUI : PopupWindowContent
    {
        private static Action<string, string> _onSearchSelection;
        private string _phrase = "Images with at least 1000 pixels in width but only if they contain the word 'nature'";

        public override Vector2 GetWindowSize()
        {
            return new Vector2(350, 260);
        }

        public void Init(Action<string, string> onSearchSelection)
        {
            _onSearchSelection = onSearchSelection;
        }

        public override void OnGUI(Rect rect)
        {
            EditorGUILayout.LabelField("Simple Searches", EditorStyles.largeLabel);
            ShowSample("'Car' prefabs", "car", "Prefabs");
            ShowSample("'Books' but not 'book shelves' or 'bookmarks'", "book -shelf -mark", "Prefabs");
            ShowSample("Search for an exact phrase", "~book shelf");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Advanced Searches", EditorStyles.largeLabel);
            ShowSample("Results from free packages only", "=AssetFile.FileName like \"%TEXT%\" and Asset.PriceEur = 0");
            ShowSample("Audio files between 10-20 seconds in length", "=AssetFile.Length >= 10 and AssetFile.Length <= 20", "Audio");
            ShowSample("Files with an AI caption available", "=AssetFile.AICaption is not null");
            ShowSample("Previews scheduled for recreation", "=AssetFile.PreviewState=2 OR AssetFile.PreviewState=6");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Describe in English (AI, Experimental)", EditorStyles.largeLabel);

            if (Intelligence.IsOllamaInstalled)
            {
                EditorGUILayout.BeginHorizontal();
                _phrase = EditorGUILayout.TextField(_phrase);
                if (GUILayout.Button("Set", GUILayout.ExpandWidth(false)))
                {
                    CreateAISearch(_phrase);
                }
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.HelpBox("AI search requires Ollama to be installed and active.", MessageType.Info);
            }
        }

        private async void CreateAISearch(string phrase)
        {
            string systemPrompt = GetSearchSyntaxPrompt();
            string response = await Intelligence.ChatAsync(systemPrompt, phrase);
            _onSearchSelection?.Invoke(response, null);
        }

        private string GetSearchSyntaxPrompt()
        {
            string[] guids = AssetDatabase.FindAssets("SearchSyntax t:TextAsset");
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (assetPath.EndsWith("SearchSyntax.md"))
                {
                    return System.IO.File.ReadAllText(assetPath);
                }
            }

            return "Search syntax documentation not found.";
        }

        private void ShowSample(string text, string searchPhrase, string searchType = null)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(text, EditorStyles.wordWrappedLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Set"))
            {
                _onSearchSelection?.Invoke(searchPhrase, searchType);
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}