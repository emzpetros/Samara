using ImpossibleRobert.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    public sealed class PackageDeletionUI : BasicEditorUI
    {
        private enum DeletionMode
        {
            DatabaseOnly = 0,
            FileSystemOnly = 1,
            Both = 2,
            ForgetContent = 3
        }

        private AssetInfo _info;
        private List<AssetInfo> _bulkInfos;
        private Action _onComplete;
        private DeletionMode _selectedMode = DeletionMode.DatabaseOnly;
        private bool _canDeleteFromFileSystem;
        private int _deletableFromFileSystemCount;
        private long _totalSize;

        private bool IsBulkMode => _bulkInfos != null && _bulkInfos.Count > 0;

        public static PackageDeletionUI ShowWindow(bool isBulk = false)
        {
            PackageDeletionUI window = GetWindow<PackageDeletionUI>(isBulk ? "Delete Packages" : "Delete Package");
            window.maxSize = new Vector2(500, 400);
            window.minSize = window.maxSize;

            return window;
        }

        public void Init(AssetInfo info, Action onComplete = null)
        {
            _info = info;
            _bulkInfos = null;
            _onComplete = onComplete;

            // Determine available options based on package type and state
            _canDeleteFromFileSystem = CanDeleteFromFileSystem(info);
            _deletableFromFileSystemCount = _canDeleteFromFileSystem ? 1 : 0;

            // Set default selection
            _selectedMode = DeletionMode.DatabaseOnly;
        }

        public void Init(List<AssetInfo> infos, Action onComplete = null)
        {
            _info = null;
            _bulkInfos = infos;
            _onComplete = onComplete;

            // Determine available options based on package types and states
            _deletableFromFileSystemCount = infos.Count(CanDeleteFromFileSystem);
            _canDeleteFromFileSystem = _deletableFromFileSystemCount > 0;
            _totalSize = infos.Where(i => i.ParentId <= 0).Sum(i => i.PackageSize);

            // Set default selection
            _selectedMode = DeletionMode.DatabaseOnly;
        }

        private static bool CanDeleteFromFileSystem(AssetInfo info)
        {
            return info.ParentId <= 0 && info.IsDownloaded && info.SafeName != Asset.NONE
                && info.AssetSource != Asset.Source.RegistryPackage && info.AssetSource != Asset.Source.AssetManager
                && info.AssetSource != Asset.Source.Directory;
        }

        public override void OnGUI()
        {
            if (_info == null && !IsBulkMode)
            {
                EditorGUILayout.HelpBox("No package selected.", MessageType.Error);
                return;
            }

            EditorGUILayout.Space(10);

            // Package information
            GUILayout.BeginVertical("box");
            if (IsBulkMode)
            {
                EditorGUILayout.LabelField("Bulk Selection", EditorStyles.boldLabel);
                EditorGUILayout.Space(5);

                int labelWidth = 140;
                int packageCount = _bulkInfos.Count(i => i.ParentId <= 0);
                int subPackageCount = _bulkInfos.Count - packageCount;

                GUILabelWithTextNoMax("Selected Packages", $"{packageCount:N0}", labelWidth);
                if (subPackageCount > 0)
                {
                    GUILabelWithTextNoMax("Sub-Packages", $"{subPackageCount:N0}", labelWidth);
                }
                if (_canDeleteFromFileSystem)
                {
                    GUILabelWithTextNoMax("Deletable from Disk", $"{_deletableFromFileSystemCount:N0}", labelWidth);
                }
                GUILabelWithTextNoMax("Total Size", EditorUtility.FormatBytes(_totalSize), labelWidth);
            }
            else
            {
                EditorGUILayout.LabelField("Package Information", EditorStyles.boldLabel);
                EditorGUILayout.Space(5);

                int labelWidth = 80;

                GUILabelWithTextNoMax("Name", _info.GetDisplayName(), labelWidth);
                GUILabelWithTextNoMax("Type", StringUtils.CamelCaseToWords(_info.AssetSource.ToString()), labelWidth);
                if (!string.IsNullOrEmpty(_info.Version))
                {
                    GUILabelWithTextNoMax("Version", _info.Version, labelWidth);
                }
                if (_info.IsDownloaded && !string.IsNullOrEmpty(_info.GetLocation(true)))
                {
                    GUILabelWithTextNoMax("Location", _info.GetLocation(true), labelWidth, null, true);
                }
            }
            GUILayout.EndVertical();

            EditorGUILayout.Space(15);

            // Deletion options
            EditorGUILayout.LabelField("Deletion Options", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            // Database only option (always available)
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(_selectedMode == DeletionMode.DatabaseOnly, GUIContent.none, EditorStyles.radioButton, GUILayout.Width(15)))
            {
                _selectedMode = DeletionMode.DatabaseOnly;
            }
            if (GUILayout.Button("Delete from Index", EditorStyles.label))
            {
                _selectedMode = DeletionMode.DatabaseOnly;
            }
            GUILayout.EndHorizontal();

            // File system only option (conditionally available)
            GUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(!_canDeleteFromFileSystem);
            if (GUILayout.Toggle(_selectedMode == DeletionMode.FileSystemOnly, GUIContent.none, EditorStyles.radioButton, GUILayout.Width(15)))
            {
                if (_canDeleteFromFileSystem) _selectedMode = DeletionMode.FileSystemOnly;
            }
            if (GUILayout.Button("Delete from File System", EditorStyles.label))
            {
                if (_canDeleteFromFileSystem) _selectedMode = DeletionMode.FileSystemOnly;
            }
            EditorGUI.EndDisabledGroup();
            GUILayout.EndHorizontal();

            // Both option (conditionally available)
            GUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(!_canDeleteFromFileSystem);
            if (GUILayout.Toggle(_selectedMode == DeletionMode.Both, GUIContent.none, EditorStyles.radioButton, GUILayout.Width(15)))
            {
                if (_canDeleteFromFileSystem) _selectedMode = DeletionMode.Both;
            }
            if (GUILayout.Button("Delete from Index and File System", EditorStyles.label))
            {
                if (_canDeleteFromFileSystem) _selectedMode = DeletionMode.Both;
            }
            EditorGUI.EndDisabledGroup();
            GUILayout.EndHorizontal();

            // Forget Content option (always available)
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(_selectedMode == DeletionMode.ForgetContent, GUIContent.none, EditorStyles.radioButton, GUILayout.Width(15)))
            {
                _selectedMode = DeletionMode.ForgetContent;
            }
            if (GUILayout.Button("Forget Indexed Content", EditorStyles.label))
            {
                _selectedMode = DeletionMode.ForgetContent;
            }
            GUILayout.EndHorizontal();

            EditorGUILayout.Space(10);

            // Show appropriate warning message based on selected mode
            string packageText = IsBulkMode ? "packages" : "package";
            string fileText = IsBulkMode ? "files" : "file";
            string entryText = IsBulkMode ? "entries" : "entry";
            switch (_selectedMode)
            {
                case DeletionMode.DatabaseOnly:
                    EditorGUILayout.HelpBox($"The {packageText} will be removed from the index only. The {fileText} will remain in the cache and the {packageText} will reappear after the next index update.", MessageType.Warning);
                    break;
                case DeletionMode.FileSystemOnly:
                    EditorGUILayout.HelpBox($"The {packageText} will be removed from the file system. The index {entryText} will remain and be marked as not downloaded.", MessageType.Info);
                    break;
                case DeletionMode.Both:
                    EditorGUILayout.HelpBox($"The {packageText} will be permanently removed from both the index and the file system.", MessageType.Warning);
                    break;
                case DeletionMode.ForgetContent:
                    EditorGUILayout.HelpBox($"All indexed files and previews will be removed. The {packageText} will remain registered but in an unindexed state, ready to be indexed fresh.", MessageType.Info);
                    break;
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.Space(10);

            // Action buttons
            if (GUILayout.Button("Delete", CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT)))
            {
                PerformDeletion();
            }

            EditorGUILayout.Space(5);
        }

        private void PerformDeletion()
        {
            List<AssetInfo> targets = IsBulkMode ? _bulkInfos : new List<AssetInfo> {_info};

            foreach (AssetInfo info in targets)
            {
                switch (_selectedMode)
                {
                    case DeletionMode.DatabaseOnly:
                        // Delete from database only
                        Assets.RemovePackage(info, false);
                        break;

                    case DeletionMode.FileSystemOnly:
                        // Delete from file system only
                        if (CanDeleteFromFileSystem(info) && File.Exists(info.GetLocation(true)))
                        {
                            File.Delete(info.GetLocation(true));
                            info.SetLocation(null);
                            info.PackageSize = 0;
                            info.CurrentState = Asset.State.New;
                            info.Refresh();
                            DBAdapter.DB.Execute("update Asset set Location=null, PackageSize=0, CurrentState=? where Id=?", Asset.State.New, info.AssetId);
                        }
                        break;

                    case DeletionMode.Both:
                        // Delete from both database and file system
                        Assets.RemovePackage(info, CanDeleteFromFileSystem(info));
                        break;

                    case DeletionMode.ForgetContent:
                        // Remove indexed content only (files + previews)
                        Assets.ForgetPackage(info, false, true);
                        break;
                }
            }

            _onComplete?.Invoke();
            Close();
        }
    }
}
