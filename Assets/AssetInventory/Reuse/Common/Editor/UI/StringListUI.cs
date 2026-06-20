using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace ImpossibleRobert.Common
{
    public sealed class StringListUI : PopupWindowContent
    {
        private const float WIDTH = 300f;
        private const float HEIGHT = 350f;

        private string _title;
        private string _separator;
        private Action<string> _callback;
        private List<string> _items;
        private ReorderableList _reorderableList;
        private Vector2 _scrollPosition;

        public void Init(string value, string separator, Action<string> callback, string title = null)
        {
            _separator = separator;
            _callback = callback;
            _title = title;

            _items = string.IsNullOrEmpty(value)
                ? new List<string>()
                : value.Split(new[] {separator}, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();

            _reorderableList = new ReorderableList(_items, typeof (string), true, true, true, true);
            _reorderableList.drawHeaderCallback = DrawHeader;
            _reorderableList.drawElementCallback = DrawElement;
            _reorderableList.onAddCallback = OnAdd;
            _reorderableList.onRemoveCallback = OnRemove;
        }

        private void DrawHeader(Rect rect)
        {
            EditorGUI.LabelField(rect, _title ?? "Items");
        }

        private void DrawElement(Rect rect, int index, bool isActive, bool isFocused)
        {
            if (index < 0 || index >= _items.Count) return;

            rect.y += 2;
            rect.height = EditorGUIUtility.singleLineHeight;
            _items[index] = EditorGUI.TextField(rect, _items[index]);
        }

        private void OnAdd(ReorderableList list)
        {
            _items.Add(string.Empty);
            list.index = _items.Count - 1;
        }

        private void OnRemove(ReorderableList list)
        {
            if (list.index >= 0 && list.index < _items.Count)
            {
                _items.RemoveAt(list.index);
            }
        }

        public override Vector2 GetWindowSize()
        {
            return new Vector2(WIDTH, HEIGHT);
        }

        public override void OnGUI(Rect rect)
        {
            EditorGUILayout.Space(4);

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition, GUILayout.Height(HEIGHT - 50));
            _reorderableList.DoLayoutList();
            EditorGUILayout.EndScrollView();

            GUILayout.FlexibleSpace();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("OK", CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT), GUILayout.ExpandWidth(true)))
            {
                string result = string.Join(_separator, _items.Where(s => !string.IsNullOrWhiteSpace(s)));
                _callback?.Invoke(result);
                editorWindow.Close();
            }
            if (GUILayout.Button("Cancel", GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT), GUILayout.ExpandWidth(false)))
            {
                editorWindow.Close();
            }
            GUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
        }
    }
}
