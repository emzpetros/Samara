using System.Collections.Generic;
using System.Linq;
using ImpossibleRobert.Common;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
#pragma warning disable CS0618 // Type or member is obsolete
#if UNITY_6000_2_OR_NEWER
using BaseTreeViewState = UnityEditor.IMGUI.Controls.TreeViewState<int>;
#else
using BaseTreeViewState = UnityEditor.IMGUI.Controls.TreeViewState;
#endif

namespace AssetInventory
{
    public sealed class TagsUI : BasicEditorUI
    {
        private List<Tag> _tags;
        private string _searchTerm;
        private SearchField SearchField => _searchField = _searchField ?? new SearchField();
        private SearchField _searchField;

        private TagTreeViewControl _treeView;
        private BaseTreeViewState _treeViewState;
        private TreeModel<TagTreeElement> _treeModel;

        public static TagsUI ShowWindow()
        {
            TagsUI window = GetWindow<TagsUI>("Tag Management");
            window.minSize = new Vector2(410, 200);

            return window;
        }

        public void Init()
        {
            _tags = Tagging.LoadTags();
            InitTreeView();
        }

        private void InitTreeView()
        {
            List<TagTreeElement> treeData = Tagging.BuildTagTree();

            if (_treeViewState == null)
            {
                _treeViewState = new BaseTreeViewState();
            }

            _treeModel = new TreeModel<TagTreeElement>(treeData);
            _treeView = new TagTreeViewControl(_treeViewState, _treeModel);
            _treeView.OnRenameTag += OnRenameTag;
            _treeView.OnSetHotkey += OnSetHotkey;
            _treeView.OnDeleteTag += OnDeleteTag;
            _treeView.OnHierarchyChanged += OnHierarchyChanged;
            _treeView.OnContextMenuPopulate += OnContextMenuPopulate;
            _treeView.ExpandAll();
        }

        public void OnEnable()
        {
            Tagging.OnTagsChanged += OnTagsChanged;
        }

        public void OnDisable()
        {
            Tagging.OnTagsChanged -= OnTagsChanged;
            if (_treeView != null)
            {
                _treeView.OnRenameTag -= OnRenameTag;
                _treeView.OnSetHotkey -= OnSetHotkey;
                _treeView.OnDeleteTag -= OnDeleteTag;
                _treeView.OnHierarchyChanged -= OnHierarchyChanged;
                _treeView.OnContextMenuPopulate -= OnContextMenuPopulate;
            }
        }

        private void OnTagsChanged()
        {
            _tags = Tagging.LoadTags();
            if (_treeView != null)
            {
                List<TagTreeElement> treeData = Tagging.BuildTagTree();
                _treeView.SetData(treeData);
            }
        }

        private void OnHierarchyChanged()
        {
            // Refresh tree after drag-drop
            List<TagTreeElement> treeData = Tagging.BuildTagTree();
            _treeView?.SetData(treeData);
        }

        private Tag GetTagWithHotkey(string hotkey)
        {
            if (string.IsNullOrEmpty(hotkey)) return null;
            return _tags?.Find(t => t.Hotkey == hotkey);
        }

        private void SetHotkey(Tag tag, string newHotkey)
        {
            if (string.IsNullOrEmpty(newHotkey))
            {
                tag.Hotkey = null;
                Tagging.SaveTag(tag);
                return;
            }

            // Only allow single letter or number
            if (newHotkey.Length > 1)
            {
                newHotkey = newHotkey.Substring(0, 1);
            }
            if (!char.IsLetterOrDigit(newHotkey[0])) return;

            // If hotkey is already in use by another tag, remove it from that tag
            newHotkey = newHotkey.ToLowerInvariant();
            Tag existingTag = GetTagWithHotkey(newHotkey);
            if (existingTag != null && existingTag.Id != tag.Id)
            {
                existingTag.Hotkey = null;
                Tagging.SaveTag(existingTag);
            }

            tag.Hotkey = newHotkey;
            Tagging.SaveTag(tag);
        }

        private void OnRenameTag(Tag tag)
        {
            NameUI nameUI = new NameUI();
            nameUI.Init(tag.Name, newName => RenameTag(tag, newName));
            PopupWindow.Show(GetPopupPositionAtMouse(), nameUI);
        }

        private void OnSetHotkey(Tag tag)
        {
            NameUI nameUI = new NameUI();
            nameUI.Init(tag.Hotkey, newHotkey => SetHotkey(tag, newHotkey), true);
            PopupWindow.Show(GetPopupPositionAtMouse(), nameUI);
        }

        private void OnDeleteTag(Tag tag)
        {
            List<Tag> descendants = Tagging.GetDescendantTags(tag.Id);

            string message;
            if (descendants.Count > 0)
            {
                string childList = string.Join("\n", descendants.Select(t => $"• '{t.Name}'"));
                message = $"Are you sure you want to delete the tag '{tag.Name}'?\n\nThis will also delete the following child tags:\n{childList}\n\nThis action cannot be undone.";
            }
            else
            {
                message = $"Are you sure you want to delete the tag '{tag.Name}'? This action cannot be undone.";
            }

            if (EditorUtility.DisplayDialog("Delete Tag", message, "Delete", "Cancel"))
            {
                Tagging.DeleteTagWithDescendants(tag);
            }
        }

        public override void OnGUI()
        {
            if (Event.current.isKey && Event.current.keyCode == KeyCode.Return && !string.IsNullOrWhiteSpace(_searchTerm))
            {
                Tagging.AddTagWithSlashHandling(_searchTerm);
                _searchTerm = "";
            }
            _searchTerm = SearchField.OnGUI(_searchTerm, GUILayout.ExpandWidth(true));

            if (_tags == null || _tags.Count == 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox("No tags created yet. Use the textfield above to create the first tag.", MessageType.Info);
                return;
            }

            // Initialize tree view if needed
            if (_treeView == null)
            {
                InitTreeView();
            }

            // Apply search filter to tree view
            _treeView.searchString = _searchTerm;

            EditorGUILayout.Space();

            // TreeView
            Rect treeRect = GUILayoutUtility.GetRect(0, 10000, 0, position.height - 100);
            _treeView.OnGUI(treeRect);

            if (!string.IsNullOrWhiteSpace(_searchTerm))
            {
                EditorGUILayout.HelpBox("Press RETURN to create a new tag", MessageType.Info);
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("Drag and drop tags to create parent/child relationships.", MessageType.Info);

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Delete All"))
            {
                if (EditorUtility.DisplayDialog("Delete All Tags", "Are you sure you want to delete all tags? This action cannot be undone.", "Delete", "Cancel"))
                {
                    _tags.ForEach(Tagging.DeleteTag);
                }
            }
        }

        private void OnContextMenuPopulate(GenericMenu menu, IReadOnlyList<TagTreeElement> selection, int clickedIndex)
        {
            // Get the clicked tag
            Tag clickedTag = clickedIndex >= 0 && clickedIndex < selection.Count ? selection[clickedIndex]?.Tag : null;

            // Add "Split into hierarchy..." option for individual tags containing "/"
            if (clickedTag != null && clickedTag.Name.Contains("/"))
            {
                menu.AddItem(new GUIContent("Split into hierarchy..."), false, () => ConvertSingleTagToHierarchy(clickedTag));
            }

            // Add bulk conversion option if there are any tags with "/"
            List<Tag> slashTags = Tagging.GetTagsWithSlash();
            if (slashTags.Count > 0)
            {
                if (clickedTag != null && clickedTag.Name.Contains("/"))
                {
                    menu.AddSeparator("");
                }
                menu.AddItem(new GUIContent($"Convert all {slashTags.Count} slash tags to hierarchy..."), false, ConvertAllTagsToHierarchy);
            }
        }

        private void ConvertSingleTagToHierarchy(Tag tag)
        {
            // Check for conflicts first
            List<(string segment, Tag existingTag, int? currentParentId, int? newParentId)> conflicts = Tagging.CheckConversionConflicts(tag);

            if (conflicts.Count > 0)
            {
                // Build warning message
                string conflictList = string.Join("\n", conflicts.Select(c =>
                {
                    string currentParent = c.currentParentId.HasValue
                        ? _tags?.Find(t => t.Id == c.currentParentId.Value)?.Name ?? $"ID:{c.currentParentId}"
                        : "none";
                    string newParent = c.newParentId.HasValue
                        ? _tags?.Find(t => t.Id == c.newParentId.Value)?.Name ?? $"ID:{c.newParentId}"
                        : "root";
                    return $"• '{c.segment}' (current parent: {currentParent} → new parent: {newParent})";
                }));

                bool proceed = EditorUtility.DisplayDialog(
                    "Reparenting Warning",
                    $"Converting '{tag.Name}' to hierarchy would reparent the following existing tags:\n\n{conflictList}\n\nDo you want to proceed?",
                    "Proceed",
                    "Cancel");

                if (!proceed) return;

                // Force reparent
                if (Tagging.ConvertSlashTagToHierarchy(tag, true))
                {
                    EditorUtility.DisplayDialog("Conversion Complete", $"Tag '{tag.Name}' has been converted to a hierarchy.", "OK");
                }
            }
            else
            {
                if (Tagging.ConvertSlashTagToHierarchy(tag, false))
                {
                    EditorUtility.DisplayDialog("Conversion Complete", $"Tag '{tag.Name}' has been converted to a hierarchy.", "OK");
                }
            }
        }

        private void ConvertAllTagsToHierarchy()
        {
            (int convertedCount, List<(Tag tag, List<(string segment, Tag existingTag, int? currentParentId, int? newParentId)> conflicts)> skipped) result = Tagging.ConvertAllSlashTagsToHierarchy();

            if (result.skipped.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "Conversion Complete",
                    $"All {result.convertedCount} slash tags have been converted to hierarchies.",
                    "OK");
            }
            else
            {
                string skippedList = string.Join("\n", result.skipped.Select(s => $"• '{s.tag.Name}'"));
                EditorUtility.DisplayDialog(
                    "Conversion Complete",
                    $"Converted {result.convertedCount} tags to hierarchies.\n\n" +
                    $"Skipped {result.skipped.Count} tags due to reparenting conflicts:\n{skippedList}\n\n" +
                    "Use right-click → 'Split into hierarchy...' on individual tags to review and force conversion.",
                    "OK");
            }
        }

        private void RenameTag(Tag tag, string newName)
        {
            if (string.IsNullOrEmpty(newName) || tag.Name == newName) return;

            Tag existingTag = DBAdapter.DB.Find<Tag>(t => t.Id != tag.Id && t.Name.ToLower() == newName.ToLower());
            if (existingTag != null)
            {
                EditorUtility.DisplayDialog("Error", "A tag with that name already exists (and merging tags is not yet supported).", "OK");
                return;
            }

            Tagging.RenameTag(tag, newName);
        }
    }
}
