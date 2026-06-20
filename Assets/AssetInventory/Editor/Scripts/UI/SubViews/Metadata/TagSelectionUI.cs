using System;
using System.Collections.Generic;
using System.Linq;
using ImpossibleRobert.Common;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace AssetInventory
{
    public sealed class TagSelectionUI : PopupWindowContent
    {
        private List<AssetInfo> _assetInfo;
        private List<Tag> _tags;
        private string _newTag;
        private Vector2 _scrollPos;
        private bool _firstRunDone;
        private SearchField SearchField => _searchField = _searchField ?? new SearchField();
        private SearchField _searchField;
        private TagAssignment.Target _target;
        private Action _onChange;
        private GUIStyle _specialTagDescriptionStyle;

        public void Init(TagAssignment.Target target, Action onChange = null)
        {
            _target = target;
            _onChange = onChange;
            _tags = Tagging.LoadTags();
            _specialTagDescriptionStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                wordWrap = true,
                normal = {textColor = Color.gray}
            };
        }

        public override Vector2 GetWindowSize()
        {
            return new Vector2(220, AI.Config.tagListHeight);
        }

        public void SetAssets(List<AssetInfo> infos)
        {
            _assetInfo = infos;
        }

        public override void OnGUI(Rect rect)
        {
            if (_assetInfo == null) return;
            if (Event.current.isKey && Event.current.keyCode == KeyCode.Return && !string.IsNullOrWhiteSpace(_newTag))
            {
                Tagging.AddAssignments(_assetInfo, _newTag, _target, true);
                _newTag = "";
            }
            GUILayout.BeginHorizontal();
            _newTag = SearchField.OnGUI(_newTag, GUILayout.ExpandWidth(true));
            if (GUILayout.Button(EditorGUIUtility.IconContent("Settings", "|Manage Tags").image, EditorStyles.label))
            {
                TagsUI tagsUI = TagsUI.ShowWindow();
                tagsUI.Init();
            }
            GUILayout.EndHorizontal();
            _scrollPos = GUILayout.BeginScrollView(_scrollPos, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.ExpandWidth(true));
            int shownItems = 0;

            // User tags section
            if (_tags != null)
            {
                foreach (Tag tag in _tags)
                {
                    // skip tags that are handled in the special tags section
                    if (Tagging.SpecialTags.Any(st => st.Target == _target && string.Equals(st.Name, tag.Name, StringComparison.OrdinalIgnoreCase))) continue;

                    // don't show already added tags (for case of only one item selected, otherwise assigning it to all)
                    switch (_target)
                    {
                        case TagAssignment.Target.Package:
                            if (_assetInfo.Count == 1 && _assetInfo[0].PackageTags.Any(t => t.TagId == tag.Id)) continue;
                            break;

                        case TagAssignment.Target.Asset:
                            if (_assetInfo.Count == 1 && _assetInfo[0].AssetTags.Any(t => t.TagId == tag.Id)) continue;
                            break;
                    }
                    if (!string.IsNullOrWhiteSpace(_newTag) && !tag.Name.ToLowerInvariant().Contains(_newTag.ToLowerInvariant())) continue;
                    shownItems++;

                    GUILayout.BeginHorizontal();
                    GUILayout.Space(8);
                    UIStyles.DrawTag(tag.Name, tag.GetColor(), () =>
                    {
                        Tagging.AddAssignments(_assetInfo, tag.Name, _target, true);
                        _onChange?.Invoke();
                    }, UIStyles.TagStyle.Add);
                    if (!string.IsNullOrWhiteSpace(tag.Hotkey))
                    {
                        EditorGUILayout.LabelField($"Alt+{tag.Hotkey}", CommonUIStyles.greyMiniLabel);
                    }
                    GUILayout.EndHorizontal();
                }
            }

            // Special tags section
            int specialShown = 0;
            foreach (SpecialTagDefinition specialTag in Tagging.SpecialTags)
            {
                if (specialTag.Target != _target) continue;

                // don't show already added special tags
                bool alreadyAssigned = false;
                if (_assetInfo.Count == 1)
                {
                    List<TagInfo> tags = _target == TagAssignment.Target.Package ? _assetInfo[0].PackageTags : _assetInfo[0].AssetTags;
                    alreadyAssigned = tags != null && tags.Any(t => string.Equals(t.Name, specialTag.Name, StringComparison.OrdinalIgnoreCase));
                }
                if (alreadyAssigned) continue;

                if (!string.IsNullOrWhiteSpace(_newTag) && !specialTag.Name.ToLowerInvariant().Contains(_newTag.ToLowerInvariant())) continue;

                if (specialShown == 0)
                {
                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField("Special Tags", EditorStyles.miniLabel);
                }
                specialShown++;
                shownItems++;

                string tagName = specialTag.Name;
                GUILayout.BeginHorizontal();
                GUILayout.Space(8);
                UIStyles.DrawTag(tagName, specialTag.Color, () =>
                {
                    Tagging.AddAssignments(_assetInfo, tagName, _target, true);
                    _onChange?.Invoke();
                }, UIStyles.TagStyle.Add);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Space(12);
                EditorGUILayout.LabelField(specialTag.Description, _specialTagDescriptionStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();
            }

            if (shownItems == 0)
            {
                if (_tags == null || _tags.Count == 0)
                {
                    EditorGUILayout.HelpBox("No tags created yet. Use the textfield above to create the first tag.", MessageType.Info);
                }
                else if (string.IsNullOrWhiteSpace(_newTag))
                {
                    EditorGUILayout.HelpBox("All existing tags were assigned already. Use the textfield above to create additional tags.", MessageType.Info);
                }
                else
                {
                    EditorGUILayout.HelpBox("Press RETURN to create a new tag", MessageType.Info);
                }
            }
            GUILayout.EndScrollView();
            if (!_firstRunDone)
            {
                SearchField.SetFocus();
                _firstRunDone = true;
            }
        }
    }
}
