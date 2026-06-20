using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using ImpossibleRobert.Common;
using UnityEngine;

namespace AssetInventory
{
    public sealed class AnimationsUI : BasicEditorUI
    {
        private Vector2 _scrollPos;
        private AssetInfo _info;
        private FBXData _fbxData;

        public static AnimationsUI ShowWindow()
        {
            AnimationsUI window = GetWindow<AnimationsUI>("FBX Animations");
            window.minSize = new Vector2(400, 200);

            return window;
        }

        public void Init(AssetInfo info)
        {
            _info = info;
            _fbxData = null;

            if (_info != null && !string.IsNullOrEmpty(_info.FileData))
            {
                try
                {
                    _fbxData = JsonConvert.DeserializeObject<FBXData>(_info.FileData);
                }
                catch
                {
                    // Silently ignore parsing errors
                }
            }
        }

        public override void OnGUI()
        {
            if (_info == null || _info.Id == 0)
            {
                EditorGUILayout.HelpBox("No FBX file selected.", MessageType.Warning);
                return;
            }

            EditorGUILayout.LabelField($"'{_info.FileName}' in asset '{_info.GetDisplayName()}'", EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space();

            if (_fbxData == null || _fbxData.animations == null || _fbxData.animations.Count == 0)
            {
                EditorGUILayout.HelpBox("No animation data available. The file may need to be re-indexed to extract animation information.", MessageType.Info);
                return;
            }

            // Display FBX statistics
            EditorGUILayout.BeginVertical("box");
            int labelWidth = 130;
            GUILabelWithTextNoMax("Animations", $"{_fbxData.animations.Count:N0}", labelWidth);
            if (_fbxData.meshCount > 0) GUILabelWithTextNoMax("Meshes", $"{_fbxData.meshCount:N0}", labelWidth);
            if (_fbxData.boneCount > 0) GUILabelWithTextNoMax("Bones", $"{_fbxData.boneCount:N0}", labelWidth);
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space();

            // Display animation list
            EditorGUILayout.LabelField("Animation Clips", EditorStyles.boldLabel);
            _scrollPos = GUILayout.BeginScrollView(_scrollPos, false, false, GUI.skin.horizontalScrollbar, GUI.skin.verticalScrollbar, GUILayout.ExpandWidth(true));

            float rowHeight = EditorGUIUtility.singleLineHeight + 2;
            float totalHeight = _fbxData.animations.Count * rowHeight;

            Rect totalRect = GUILayoutUtility.GetRect(0, totalHeight, GUILayout.ExpandWidth(true));

            float currentY = totalRect.y;
            float durationWidth = 80; // Fixed width for duration column
            float nameWidth = totalRect.width - durationWidth - 10; // Remaining space for name with padding

            foreach (AnimationInfo anim in _fbxData.animations.OrderBy(a => a.name))
            {
                // Animation name on the left
                Rect nameRect = new Rect(totalRect.x + 5, currentY, nameWidth, rowHeight);
                GUI.Label(nameRect, anim.name, EditorStyles.label);

                // Duration on the right
                Rect durationRect = new Rect(totalRect.x + nameWidth + 10, currentY, durationWidth, rowHeight);
                GUI.Label(durationRect, StringUtils.FormatDuration(anim.length), EditorStyles.miniLabel);

                currentY += rowHeight;
            }

            GUILayout.EndScrollView();
        }
    }
}
