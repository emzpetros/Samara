using System.Collections.Generic;
using ImpossibleRobert.Common;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    public sealed class FreebieUI : EditorWindow
    {
        private Vector2 _scrollPos;
        private bool _inProgress;
        private FreeAssetFinder _freeAssetFinder;
        private List<AssetDetails> _candidates;

        public static FreebieUI ShowWindow()
        {
            FreebieUI window = GetWindow<FreebieUI>("Potential Freebies");
            window.minSize = new Vector2(400, 400);

            return window;
        }

        public void OnGUI()
        {
            EditorGUILayout.HelpBox("When purchasing Asset Store packages, authors sometimes grant reduced or even free access to other packages of them. Also, some authors sell bundles. When purchasing a bundle, a linked list of other packages becomes available for free. These packages are typically listed in the description.\n\nUsing the Freebie scanner action will check all your purchased packages, if they contain any links to other assets in the description and will show these as potential candidates that you can claim.", MessageType.None);
            EditorGUILayout.Space();

            GUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("The scanner here uses a heuristic and may not find all freebies. For a definitive list, consider the free open-source browser plugin by wfthkttn which can detect all claimable freebies directly from the Asset Store.", EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button("More Info: unity-assets-freebies (codeberg.org)", CommonUIStyles.wrappedLinkLabel, GUILayout.ExpandWidth(true)))
            {
                AI.OpenURL("https://codeberg.org/wfthkttn/unity-assets-freebies");
            }
            GUILayout.EndVertical();
            EditorGUILayout.Space();

            if (_candidates != null && _candidates.Count > 0)
            {
                EditorGUILayout.LabelField($"{_candidates.Count} Potential Candidates", EditorStyles.boldLabel);

                _scrollPos = GUILayout.BeginScrollView(_scrollPos, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.ExpandWidth(true));
                bool evenRow = true;
                foreach (AssetDetails details in _candidates)
                {
                    if (details.id == null) continue;

                    Color originalColor = GUI.backgroundColor;
                    GUI.backgroundColor = evenRow ? Color.white : new Color(0.7f, 0.7f, 0.7f);

                    EditorGUILayout.BeginHorizontal("box");
                    EditorGUILayout.LabelField($"{details.name}");

                    if (GUILayout.Button("Open", GUILayout.ExpandWidth(false)))
                    {
                        string url = $"https://assetstore.unity.com/packages/slug/{details.packageId}";
                        AI.OpenStoreURL(url);

                        details.id = null;
                    }
                    EditorGUILayout.EndHorizontal();

                    GUI.backgroundColor = originalColor;
                    evenRow = !evenRow;
                }
                GUILayout.EndScrollView();
                EditorGUILayout.Space();
            }

            GUILayout.FlexibleSpace();
            if (_freeAssetFinder != null && _freeAssetFinder.IsRunning())
            {
                EditorGUILayout.BeginHorizontal();
                CommonUIStyles.DrawProgressBar((float)_freeAssetFinder.MainProgress / _freeAssetFinder.MainCount, $"Progress: {_freeAssetFinder.MainProgress}/{_freeAssetFinder.MainCount}");
                if (GUILayout.Button("Cancel", GUILayout.ExpandWidth(false), GUILayout.Height(14)))
                {
                    _freeAssetFinder.CancellationRequested = true;
                }
                EditorGUILayout.EndHorizontal();
            }

            else
            {
                if (GUILayout.Button(_inProgress ? "Analysis in progress" : "Find Candidates", CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT))) FindCandidates();
            }
        }

        private async void FindCandidates()
        {
            await AI.Actions.RunWithProgress<FreeAssetFinder>(
                ActionHandler.ACTION_FIND_FREE,
                "Finding free assets",
                async imp => { _freeAssetFinder = imp; _candidates = await imp.Run(); });
        }
    }
}
