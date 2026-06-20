using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    public sealed class WhereUsedUI : BasicEditorUI
    {
        private Vector2 _scrollPos;
        private string _assetPath;
        private string _assetName;
        private List<string> _resultPaths;
        private bool _calculated;
        private bool _calculating;
        private int _analysisProgress;
        private int _analysisTotal;

        public static WhereUsedUI ShowWindow()
        {
            WhereUsedUI window = GetWindow<WhereUsedUI>("Asset References");
            window.minSize = new Vector2(400, 200);
            return window;
        }

        public void Init(string assetPath)
        {
            _assetPath = assetPath;
            _assetName = Path.GetFileName(assetPath);
            _calculated = false;
            _calculating = false;
            _resultPaths = null;

            CalculateReverseReferencesAsync();
        }

        private async void CalculateReverseReferencesAsync()
        {
            _calculating = true;
            _analysisProgress = 0;
            _analysisTotal = 0;
            Repaint();

            List<string> refs = await ProjectDependencyAnalysis.FindReferencesAsync(_assetPath, (current, total) =>
            {
                _analysisProgress = current;
                _analysisTotal = total;
                Repaint();
            });

            _resultPaths = refs.OrderBy(p => p).ToList();
            _calculated = true;
            _calculating = false;
            Repaint();
        }

        public override void OnGUI()
        {
            EditorGUILayout.Space();

            EditorGUILayout.LabelField($"Assets referencing: {_assetName}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(_assetPath, EditorStyles.miniLabel);
            EditorGUILayout.Space();

            if (_calculating)
            {
                if (_analysisTotal > 0)
                {
                    float progress = (float)_analysisProgress / _analysisTotal;
                    Rect rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
                    EditorGUI.ProgressBar(rect, progress, $"Scanning... {_analysisProgress:N0}/{_analysisTotal:N0}");
                }
                else
                {
                    EditorGUILayout.HelpBox("Calculating...", MessageType.Info);
                }
                return;
            }

            if (!_calculated || _resultPaths == null)
            {
                EditorGUILayout.HelpBox("No data available.", MessageType.Warning);
                return;
            }

            if (_resultPaths.Count == 0)
            {
                EditorGUILayout.HelpBox("No assets reference this file.", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField($"{_resultPaths.Count} reference(s) found:", EditorStyles.miniBoldLabel);
            EditorGUILayout.Space();

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            foreach (string path in _resultPaths)
            {
                GUILayout.BeginHorizontal();

                Texture icon = AssetDatabase.GetCachedIcon(path);
                if (icon != null)
                {
                    GUILayout.Label(icon, GUILayout.Width(16), GUILayout.Height(16));
                }

                if (GUILayout.Button(path, EditorStyles.linkLabel))
                {
                    Object obj = AssetDatabase.LoadAssetAtPath<Object>(path);
                    if (obj != null)
                    {
                        EditorGUIUtility.PingObject(obj);
                        Selection.activeObject = obj;
                    }
                }

                GUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
