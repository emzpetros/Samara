using System;
using System.Collections.Generic;
using System.Linq;
using ImpossibleRobert.Common;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    public sealed class UnityPackageVersionSelectionUI : PopupWindowContent
    {
        private AssetInfo _info;
        private Vector2 _scrollPos;
        private Action<string> _callback;
        private Dictionary<int, List<BackupInfo>> _backupState;
        private List<BackupInfo> _availableVersions;

        public void Init(AssetInfo info, Dictionary<int, List<BackupInfo>> backupState, Action<string> callback)
        {
            _info = info;
            _callback = callback;
            _backupState = backupState;

            if (_backupState != null && _info != null && _info.ForeignId > 0)
            {
                if (_backupState.TryGetValue(_info.ForeignId, out List<BackupInfo> versions))
                {
                    _availableVersions = versions.ToList();
                }
                else
                {
                    _availableVersions = new List<BackupInfo>();
                }
            }
            else
            {
                _availableVersions = new List<BackupInfo>();
            }
        }

        public override Vector2 GetWindowSize()
        {
            return new Vector2(250, 200); 
        }

        public override void OnGUI(Rect rect)
        {
            if (_info == null)
            {
                EditorGUILayout.HelpBox("No package selected.", MessageType.Warning);
                return;
            }

            if (_availableVersions == null || _availableVersions.Count == 0)
            {
                EditorGUILayout.HelpBox("No backup versions available for this package.", MessageType.Info);
                EditorGUILayout.Space();
                string defaultVersion = _info.GetVersion(true);
                if (!string.IsNullOrWhiteSpace(defaultVersion))
                {
                    if (GUILayout.Button(CommonUIStyles.Content($"Use Current Version ({defaultVersion})")))
                    {
                        _callback?.Invoke(null);
                        editorWindow.Close();
                    }
                }
                return;
            }

            string currentSelectedVersion = _info.ForcedUnityPackageVersion;
            string currentVersion = _info.GetVersion(true);

            EditorGUILayout.LabelField("Available Backup Versions", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            _scrollPos = GUILayout.BeginScrollView(_scrollPos, false, false);
            Color oldCol = GUI.backgroundColor;

            foreach (BackupInfo backupInfo in _availableVersions)
            {
                bool isCurrent = backupInfo.version == currentSelectedVersion;
                bool isDefault = string.IsNullOrWhiteSpace(currentSelectedVersion) && backupInfo.version == currentVersion;

                if (isCurrent || isDefault)
                {
                    GUI.backgroundColor = Color.green;
                }

                GUILayout.BeginHorizontal();
                EditorGUI.BeginDisabledGroup(isCurrent);
                if (GUILayout.Button(CommonUIStyles.Content(backupInfo.version, isCurrent ? "Currently selected" : "Select this version"), CommonUIStyles.wrappedButton, GUILayout.Width(150)))
                {
                    _callback?.Invoke(backupInfo.version);
                    editorWindow.Close();
                }
                EditorGUI.EndDisabledGroup();

                if (isCurrent)
                {
                    GUILayout.Label("(selected)", EditorStyles.miniLabel);
                }
                else if (isDefault)
                {
                    GUILayout.Label("(current)", EditorStyles.miniLabel);
                }

                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                GUI.backgroundColor = oldCol;
            }

            GUILayout.EndScrollView();

            EditorGUILayout.Space();
            if (!string.IsNullOrWhiteSpace(currentSelectedVersion) && currentSelectedVersion != currentVersion)
            {
                if (GUILayout.Button(CommonUIStyles.Content("Clear Selection (use default)")))
                {
                    _callback?.Invoke(null);
                    editorWindow.Close();
                }
            }
        }
    }
}
