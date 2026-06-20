using Automator;
using ImpossibleRobert.Common;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Brain;
using Database;
using System.Threading.Tasks;
using UnityEditor;
#if !USE_TUTORIALS || !USE_VECTOR_GRAPHICS || !USE_VFX || (!USE_TEXTMESHPRO && !UNITY_2023_2_OR_NEWER)
using UnityEditor.PackageManager;
#endif
using UnityEditorInternal;
using UnityEngine;

namespace AssetInventory
{
    public partial class IndexUI
    {
        private Vector2 _folderScrollPos;
        private Vector2 _statsScrollPos;
        private Vector2 _settingsScrollPos;

        private bool _showMaintenance;
        private bool _showDiskSpace;
        private long _dbSize;
        private long _backupSize;
        private long _cacheSize;
        private long _persistedCacheSize;
        private long _previewSize;
        private string _captionTest = "-no caption created yet-";
        private bool _captionTestRunning;
        private bool _legacyCacheLocationFound;

        // additional folders
        private ReorderableList FolderListControl
        {
            get
            {
                if (_folderListControl == null) InitFolderControl();
                return _folderListControl;
            }
        }
        private ReorderableList _folderListControl;
        private int _selectedFolderIndex = -1;
        private int _selectedUpdateActionIndex = -1;

        // update actions
        private ReorderableList UpdateActionsControl
        {
            get
            {
                if (_updateActionsControl == null) InitUpdateActions();
                return _updateActionsControl;
            }
        }
        private ReorderableList _updateActionsControl;
        private List<UpdateAction> _updateActions;

        private bool _calculatingFolderSizes;
        private bool _cleanupInProgress;
        private DateTime _lastFolderSizeCalculation;
        private long _curOllamaProgress;
        private long _maxOllamaProgress;

        private void InitUpdateActions()
        {
            _updateActions = AI.Actions.Actions.Where(a => !a.hidden).ToList();
            _updateActionsControl = new ReorderableList(_updateActions, typeof (UpdateAction), false, true, false, false);
            _updateActionsControl.drawElementCallback = DrawUpdateActionItem;
            _updateActionsControl.drawHeaderCallback = rect =>
            {
                EditorGUI.LabelField(rect, "Actions to Perform" + (AI.Actions.AnyActionsInProgress ? $" (Started {StringUtils.GetRelativeTimeDifference(AI.Actions.GetFirstActionStart())})" : ""));
                if (GUI.Button(new Rect(rect.x + rect.width - 155, rect.y, 35, 20), "All", EditorStyles.miniButton))
                {
                    AI.Actions.SetAllActive(true);
                }
                if (GUI.Button(new Rect(rect.x + rect.width - 115, rect.y, 60, 20), "Default", EditorStyles.miniButton))
                {
                    AI.Actions.SetDefaultActive();
                }
                if (GUI.Button(new Rect(rect.x + rect.width - 50, rect.y, 50, 20), "None", EditorStyles.miniButton))
                {
                    AI.Actions.SetAllActive(false);
                }
            };
            _updateActionsControl.displayAdd = true;
            _updateActionsControl.displayRemove = true;
            _updateActionsControl.onAddCallback = _ =>
            {
                NameUI nameUI = new NameUI();
                nameUI.Init("My Action", CreateAction);
                PopupWindow.Show(GetPopupPositionAtMouse(), nameUI);
            };
            _updateActionsControl.onCanRemoveCallback = AllowRemoveAction;
            _updateActionsControl.onRemoveCallback = RemoveAction;
        }

        private bool AllowRemoveAction(ReorderableList list)
        {
            if (_selectedUpdateActionIndex < 0 || _selectedUpdateActionIndex >= AI.Actions.Actions.Count) return false;

            return AI.Actions.Actions[_selectedUpdateActionIndex].key.StartsWith(ActionHandler.ACTION_USER);
        }

        private void RemoveAction(ReorderableList list)
        {
            string key = AI.Actions.Actions[_selectedUpdateActionIndex].key;
            int id = int.Parse(key.Split('-').Last());
            CustomAction ca = DBAdapter.DB.Find<CustomAction>(id);
            if (ca == null)
            {
                Debug.LogError($"Could not find action to delete: {key}. Restarting Unity might solve this.");
                return;
            }

            if (!EditorUtility.DisplayDialog("Confirm", $"Do you really want to delete the action '{ca.Name}'?", "Yes", "No")) return;

            DBAdapter.DB.Execute("delete from CustomActionStep where ActionId=?", ca.Id);
            DBAdapter.DB.Delete(ca);

            AI.Actions.Init(true);
            InitUpdateActions();
        }

        private void CreateAction(string actionName)
        {
            CustomAction action = new CustomAction(actionName.Trim());
            DBAdapter.DB.Insert(action);

            AI.Actions.Init(true);
            InitUpdateActions();
            EditAction(action.Id);
        }

        private void EditAction(string actionKey)
        {
            int id = int.Parse(actionKey.Split('-').Last());
            EditAction(id);
        }

        private void EditAction(int id)
        {
            SqliteActionRepository repository = new SqliteActionRepository();
            ActionDefinition action = repository.GetAction(id);

            ActionEditorWindow.Edit(repository, action, RefreshActions);
        }

        private void RefreshActions()
        {
            // Reload actions from database to get updated names/descriptions
            AI.Actions.Init(true);
            InitUpdateActions();
        }

        private void OnActionsInitialized()
        {
            InitUpdateActions();
        }

        private void InitFolderControl()
        {
            _folderListControl = new ReorderableList(AI.Config.folders, typeof (FolderSpec), true, true, true, true);
            _folderListControl.drawElementCallback = DrawFoldersListItem;
            _folderListControl.drawHeaderCallback = DrawFolderListHeader;
            _folderListControl.onAddCallback = OnAddCustomFolder;
            _folderListControl.onRemoveCallback = OnRemoveCustomFolder;
        }

        private void DrawFolderListHeader(Rect rect)
        {
            EditorGUI.LabelField(rect, "Folders to Index");
            if (GUI.Button(new Rect(rect.x + rect.width - 155, rect.y, 35, 20), "All", EditorStyles.miniButton))
            {
                SetAllFoldersActive(true);
            }
            if (GUI.Button(new Rect(rect.x + rect.width - 115, rect.y, 60, 20), "Invert", EditorStyles.miniButton))
            {
                InvertFoldersActive();
            }
            if (GUI.Button(new Rect(rect.x + rect.width - 50, rect.y, 50, 20), "None", EditorStyles.miniButton))
            {
                SetAllFoldersActive(false);
            }
        }

        private void SetAllFoldersActive(bool active)
        {
            foreach (FolderSpec folder in AI.Config.folders)
            {
                folder.enabled = active;
            }

            AI.SaveConfig();
        }

        private void InvertFoldersActive()
        {
            foreach (FolderSpec folder in AI.Config.folders)
            {
                folder.enabled = !folder.enabled;
            }

            AI.SaveConfig();
        }

        private void DrawUpdateActionItem(Rect rect, int index, bool isActive, bool isFocused)
        {
            // draw alternating-row background
            if (Event.current.type == EventType.Repaint && index % 2 == 1)
            {
                // choose a tiny overlay that will darken/lighten regardless of the exact theme colors
                Color overlay = EditorGUIUtility.isProSkin
                    ? new Color(1f, 1f, 1f, 0.025f) // on dark (Pro) skin, brighten a hair
                    : new Color(0f, 0f, 0f, 0.025f); // on light skin, darken a hair

                EditorGUI.DrawRect(rect, overlay);
            }

            if (index >= AI.Actions.Actions.Count) return;

            UpdateAction action = AI.Actions.Actions[index];

            if (isFocused) _selectedUpdateActionIndex = index;

            EditorGUI.BeginChangeCheck();
            AI.Actions.SetActive(action, GUI.Toggle(new Rect(rect.x, rect.y, 20, EditorGUIUtility.singleLineHeight), AI.Actions.IsActive(action), CommonUIStyles.Content("", "Include action when updating everything"), UIStyles.toggleStyle));
            if (EditorGUI.EndChangeCheck()) AI.SaveConfig();

            GUI.Label(new Rect(rect.x + 20, rect.y, rect.width - 250, EditorGUIUtility.singleLineHeight), CommonUIStyles.Content(action.name, action.description), UIStyles.entryStyle);
            Color oldCol = GUI.backgroundColor;
            if (action.IsRunning())
            {
                GUI.backgroundColor = Color.green;
            }
            EditorGUI.BeginDisabledGroup(action.IsRunning() || action.scheduled || AI.Actions.AnyActionsInProgress);
            if (action.key.StartsWith(ActionHandler.ACTION_USER))
            {
                if (GUI.Button(new Rect(rect.x + rect.width - 65, rect.y + 1, 30, 20), EditorGUIUtility.IconContent("editicon.sml", "|Edit Action")))
                {
                    EditAction(action.key);
                }
            }
            if (action.supportsForce && ShowAdvanced() && GUI.Button(new Rect(rect.x + rect.width - 65, rect.y + 1, 30, 20), EditorGUIUtility.IconContent("d_preAudioAutoPlayOff@2x", "|Force Run Action Now")))
            {
                _ = AI.Actions.RunAction(action, true);
            }
            if (GUI.Button(new Rect(rect.x + rect.width - 30, rect.y + 1, 30, 20), EditorGUIUtility.IconContent("d_PlayButton@2x", "|Run Action Now")))
            {
                _ = AI.Actions.RunAction(action);
            }
            EditorGUI.EndDisabledGroup();
            GUI.backgroundColor = oldCol;
        }

        private void DrawFoldersListItem(Rect rect, int index, bool isActive, bool isFocused)
        {
            // draw alternating-row background
            if (Event.current.type == EventType.Repaint && index % 2 == 1)
            {
                // choose a tiny overlay that will darken/lighten regardless of the exact theme colors
                Color overlay = EditorGUIUtility.isProSkin
                    ? new Color(1f, 1f, 1f, 0.025f) // on dark (Pro) skin, brighten a hair
                    : new Color(0f, 0f, 0f, 0.025f); // on light skin, darken a hair

                EditorGUI.DrawRect(rect, overlay);
            }

            _legacyCacheLocationFound = false;
            if (index >= AI.Config.folders.Count) return;

            FolderSpec spec = AI.Config.folders[index];

            if (isFocused) _selectedFolderIndex = index;

            EditorGUI.BeginChangeCheck();
            spec.enabled = GUI.Toggle(new Rect(rect.x, rect.y, 20, EditorGUIUtility.singleLineHeight), spec.enabled, CommonUIStyles.Content("", "Rescan and update folder when running the action."), UIStyles.toggleStyle);
            if (EditorGUI.EndChangeCheck()) AI.SaveConfig();

            GUI.Label(new Rect(rect.x + 20, rect.y, rect.width - 250, EditorGUIUtility.singleLineHeight), spec.location, UIStyles.entryStyle);
            GUI.Label(new Rect(rect.x + rect.width - 230, rect.y, 170, EditorGUIUtility.singleLineHeight), FolderTypes[spec.folderType] + (spec.folderType == 1 ? " (" + MediaTypes[spec.scanFor] + ")" : ""), UIStyles.entryStyle);
            if (spec.folderType == 1)
            {
                if (GUI.Button(new Rect(rect.x + rect.width - 60, rect.y + 1, 30, 20), EditorGUIUtility.IconContent("CustomTool", "|Fine-tune which files are indexed and how packages are created")))
                {
                    FolderFineTuneUI.ShowWindow(spec);
                }
            }
            if (GUI.Button(new Rect(rect.x + rect.width - 30, rect.y + 1, 30, 20), EditorGUIUtility.IconContent("Settings", "|Folder Settings")))
            {
                FolderSettingsUI folderSettingsUI = new FolderSettingsUI();
                folderSettingsUI.Init(spec);
                PopupWindow.Show(GetPopupPositionAtMouse(), folderSettingsUI);
            }
            if (spec.location.Contains(AI.ASSET_STORE_FOLDER_NAME)) _legacyCacheLocationFound = true;
        }

        private void OnRemoveCustomFolder(ReorderableList list)
        {
            _legacyCacheLocationFound = false; // otherwise warning will not be cleared if last folder is removed
            int folderIndex = list.index >= 0 ? list.index : _selectedFolderIndex;
            if (folderIndex < 0 || folderIndex >= AI.Config.folders.Count) return;

            string folderLocation = AI.Config.folders[folderIndex].location;
            if (!EditorUtility.DisplayDialog("Remove Additional Folder",
                $"Remove this additional folder from the list?\n\n{folderLocation}\n\nThe indexed data and the folder on disk will not be deleted, just the configuration here.",
                "Remove", "Cancel")) return;

            AI.Config.folders.RemoveAt(folderIndex);
            _selectedFolderIndex = -1;
            AI.SaveConfig();
        }

        private void OnAddCustomFolder(ReorderableList list)
        {
            string folder = EditorUtility.OpenFolderPanel("Select folder to index", "", "");
            if (string.IsNullOrEmpty(folder)) return;

            // make absolute and conform to OS separators
            folder = Path.GetFullPath(folder);

            // special case: a relative key is already defined for the folder to be added, replace it immediately
            folder = Paths.MakeRelative(folder);

            // don't allow adding Unity asset cache folders manually 
            if (folder.Contains(AI.ASSET_STORE_FOLDER_NAME))
            {
                EditorUtility.DisplayDialog("Attention", "You selected a custom Unity asset cache location. This should be done by setting the asset cache location above to custom.", "OK");
                return;
            }

            // ensure no trailing slash if root folder on Windows
            if (folder.Length > 1 && folder.EndsWith("/")) folder = folder.Substring(0, folder.Length - 1);

            FolderWizardUI wizardUI = FolderWizardUI.ShowWindow();
            wizardUI.Init(folder);
        }

        private void DrawSettingsTab()
        {
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();
            _folderScrollPos = GUILayout.BeginScrollView(_folderScrollPos, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.ExpandWidth(true));

            int labelWidth = 225;
            int cbWidth = 20;

            // invisible spacer to ensure settings are legible if all are collapsed
            EditorGUILayout.LabelField("", GUILayout.Width(labelWidth), GUILayout.Height(1));

            // actions
            BeginIndentBlock();
            UpdateActionsControl.DoLayoutList();

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            EndIndentBlock();
            EditorGUILayout.Space();

            // settings
            EditorGUI.BeginChangeCheck();
            AI.Config.showIndexingSettings = EditorGUILayout.BeginFoldoutHeaderGroup(AI.Config.showIndexingSettings, "Indexing");
            if (EditorGUI.EndChangeCheck()) AI.SaveConfig();

            if (AI.Config.showIndexingSettings)
            {
                BeginIndentBlock();
                UIBlock("settings.locationintro", () =>
                {
                    EditorGUILayout.HelpBox("Unity stores downloads in two cache folders: one for Assets and one for content from the Unity package registry. These Unity cache folders will be your main indexing locations. Specify custom locations in the Additional Folders list below.", MessageType.Info);
                });

                EditorGUI.BeginChangeCheck();

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Asset Cache Location", "How to determine where Unity stores downloaded asset packages"), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                AI.Config.assetCacheLocationType = EditorGUILayout.Popup(AI.Config.assetCacheLocationType, _assetCacheLocationOptions, GUILayout.Width(200));
                GUILayout.EndHorizontal();

                switch (AI.Config.assetCacheLocationType)
                {
                    case 0:
                        UIBlock("settings.actions.openassetcache", () =>
                        {
                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField("", GUILayout.Width(labelWidth));
                            if (GUILayout.Button("Open", GUILayout.ExpandWidth(false))) EditorUtility.RevealInFinder(Paths.GetAssetCacheFolder());
                            EditorGUILayout.LabelField(CommonUIStyles.Content(Paths.GetAssetCacheFolder(), Paths.GetAssetCacheFolder()), EditorStyles.wordWrappedLabel);
                            GUILayout.FlexibleSpace();
                            GUILayout.EndHorizontal();
                        });

#if UNITY_2022_1_OR_NEWER
                        // show hint if Unity is not self-reporting the cache location
                        if (string.IsNullOrWhiteSpace(AssetStore.GetAssetCacheFolder()))
                        {
                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField("", GUILayout.Width(labelWidth));
                            EditorGUILayout.HelpBox("If you defined a custom location for your cache folder different from the one above, either set the 'ASSETSTORE_CACHE_PATH' environment variable or select 'Custom' and enter the path there. Unity does not expose the location yet for other tools.", MessageType.Info);
                            GUILayout.EndHorizontal();
                        }
#endif
                        break;

                    case 1:
                        DrawFolder("", AI.Config.assetCacheLocation, Paths.GetAssetCacheFolder(), newFolder =>
                        {
                            AI.Config.assetCacheLocation = newFolder;
                            Paths.ClearCaches();
                            AI.GetObserver().SetPath(Paths.GetAssetCacheFolder());
                            Paths.LoadRelativeLocations();
                        }, labelWidth, "Select asset cache folder of Unity (ending with 'Asset Store-5.x')", validate =>
                        {
                            if (Path.GetFileName(validate).ToLowerInvariant() != AI.ASSET_STORE_FOLDER_NAME.ToLowerInvariant())
                            {
                                EditorUtility.DisplayDialog("Error", $"Not a valid Unity asset cache folder. It should point to a folder ending with '{AI.ASSET_STORE_FOLDER_NAME}'", "OK");
                                return false;
                            }
                            return true;
                        });
                        UIBlock("settings.customlocationwarning", () =>
                        {
                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField("", GUILayout.Width(labelWidth));
                            EditorGUILayout.HelpBox("Setting a custom location should only be done if the automatic detection did not work and Unity actually stores the packages it downloads in a different place. Otherwise this will lead to an inconsistent experience. Downloads will always happen to the folder that is managed by Unity, not the one selected here.", MessageType.Warning);
                            GUILayout.EndHorizontal();
                        });
                        break;
                }

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Package Cache Location", "How to determine where Unity stores downloaded registry packages"), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                AI.Config.packageCacheLocationType = EditorGUILayout.Popup(AI.Config.packageCacheLocationType, _assetCacheLocationOptions, GUILayout.Width(200));
                GUILayout.EndHorizontal();

                switch (AI.Config.packageCacheLocationType)
                {
                    case 0:
                        UIBlock("settings.actions.openpackagecache", () =>
                        {
                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField("", GUILayout.Width(labelWidth));
                            if (GUILayout.Button("Open", GUILayout.ExpandWidth(false))) EditorUtility.RevealInFinder(Paths.GetPackageCacheFolder());
                            EditorGUILayout.LabelField(CommonUIStyles.Content(Paths.GetPackageCacheFolder(), Paths.GetPackageCacheFolder()), EditorStyles.wordWrappedLabel);
                            GUILayout.FlexibleSpace();
                            GUILayout.EndHorizontal();
                        });
                        break;

                    case 1:
                        DrawFolder("", AI.Config.packageCacheLocation, Paths.GetAssetCacheFolder(), newFolder => AI.Config.packageCacheLocation = newFolder, labelWidth);
                        break;
                }

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Index Sub-Packages", "Will scan packages for other .unitypackage files and also index these. Recommended, as it is the basis for SRP support since SRP packages are sub-packages inside other packages."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                AI.Config.indexSubPackages = EditorGUILayout.Toggle(AI.Config.indexSubPackages, GUILayout.MaxWidth(cbWidth));
                GUILayout.EndHorizontal();

                EditorGUILayout.LabelField(CommonUIStyles.Content("Download Settings"), EditorStyles.boldLabel);

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content($"{CommonUIStyles.INDENT}Keep Downloaded Assets", "Will not delete automatically downloaded assets after indexing but keep them in the cache instead."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                AI.Config.keepAutoDownloads = EditorGUILayout.Toggle(AI.Config.keepAutoDownloads, GUILayout.MaxWidth(cbWidth));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content($"{CommonUIStyles.INDENT}Limit Package Size", "Will not automatically download packages larger than specified."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                AI.Config.limitAutoDownloads = EditorGUILayout.Toggle(AI.Config.limitAutoDownloads, GUILayout.Width(15));

                if (AI.Config.limitAutoDownloads)
                {
                    GUILayout.Label("to", GUILayout.ExpandWidth(false));
                    AI.Config.downloadLimit = EditorGUILayout.DelayedIntField(AI.Config.downloadLimit, GUILayout.Width(40));
                    GUILayout.Label("Mb", GUILayout.ExpandWidth(false));
                }
                GUILayout.EndHorizontal();

                if (ShowAdvanced())
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Extract Full Metadata", "Will extract dimensions from images and length from audio files to make these searchable at the cost of a slower indexing process."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.gatherExtendedMetadata = EditorGUILayout.Toggle(AI.Config.gatherExtendedMetadata, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Index Asset Package Contents", "Will extract asset packages (.unitypackage) and make their contents searchable. This is the foundation for the search. Deactivate only if you are solely interested in package metadata."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.indexAssetPackageContents = EditorGUILayout.Toggle(AI.Config.indexAssetPackageContents, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Exclude Hidden Packages", "Will activate the exclude flag for packages that have been hidden by the user on the Asset Store."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.excludeHidden = EditorGUILayout.Toggle(AI.Config.excludeHidden, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Remove Unresolveable Files", "Will automatically clean-up the database if a file cannot be found in the materialized package anymore but is still in the database."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.removeUnresolveableDBFiles = EditorGUILayout.Toggle(AI.Config.removeUnresolveableDBFiles, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Auto-Schedule for Reindexing", "Will automatically schedule reindexing of packages when unresolveable files are encountered (can happen for packages that were indexed before version 3.6)."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.markUnresolveableForReindexing = EditorGUILayout.Toggle(AI.Config.markUnresolveableForReindexing, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Directory Package Media Count", "Number of media entries to create for directory packages from evenly-spaced previews. Set to 0 to disable."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.directoryPackageMediaCount = EditorGUILayout.DelayedIntField(AI.Config.directoryPackageMediaCount, GUILayout.Width(50));
                    GUILayout.EndHorizontal();

                    EditorGUILayout.LabelField(CommonUIStyles.Content("Defaults for New Packages"), EditorStyles.boldLabel);

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content($"{CommonUIStyles.INDENT}Keep Cached", "Will set the Keep Cached flag on newly discovered assets. This will cause them to remain in the cache after indexing making the next access fast."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.extractByDefault = EditorGUILayout.Toggle(AI.Config.extractByDefault, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content($"{CommonUIStyles.INDENT}Backup", "Will mark newly discovered packages to be backed up automatically. Otherwise you need to select packages manually which will save a lot of disk space potentially."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.backupByDefault = EditorGUILayout.Toggle(AI.Config.backupByDefault, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content($"{CommonUIStyles.INDENT}AI Captions", "Will set the AI Caption flag on newly discovered assets. This will cause AI captions to be created for these when the caption action is run."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.captionByDefault = EditorGUILayout.Toggle(AI.Config.captionByDefault, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content($"{CommonUIStyles.INDENT}Exclude", "Will mark new assets as excluded causing them to not be shown in the normal packages list and not being processed further."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.excludeByDefault = EditorGUILayout.Toggle(AI.Config.excludeByDefault, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Tag Slash Handling", "How to handle tags containing '/' characters (e.g. from Asset Store). 'Take As Is' keeps the slash in the tag name. 'Create Hierarchy' splits on '/' and creates parent/child tag relationships."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.tagSlashHandling = (TagSlashHandling)EditorGUILayout.EnumPopup(AI.Config.tagSlashHandling, GUILayout.Width(150));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Pause indexing regularly", "Will pause all hard disk activity regularly to allow the disk to cool down."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.useCooldown = EditorGUILayout.Toggle(AI.Config.useCooldown, GUILayout.Width(15));

                    if (AI.Config.useCooldown)
                    {
                        GUILayout.Label("every", GUILayout.ExpandWidth(false));
                        AI.Config.cooldownInterval = EditorGUILayout.DelayedIntField(AI.Config.cooldownInterval, GUILayout.Width(30));
                        GUILayout.Label("minutes for", GUILayout.ExpandWidth(false));
                        AI.Config.cooldownDuration = EditorGUILayout.DelayedIntField(AI.Config.cooldownDuration, GUILayout.Width(30));
                        GUILayout.Label("seconds", GUILayout.ExpandWidth(false));
                    }
                    GUILayout.EndHorizontal();
                }

                if (EditorGUI.EndChangeCheck())
                {
                    Paths.ClearCaches();
                    AI.SaveConfig();
                    Paths.LoadRelativeLocations();
                    _requireLookupUpdate = ChangeImpact.Write;
                }
                EndIndentBlock();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // additional folders
            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            AI.Config.showFolderSettings = EditorGUILayout.BeginFoldoutHeaderGroup(AI.Config.showFolderSettings, "Additional Folders");
            if (EditorGUI.EndChangeCheck()) AI.SaveConfig();

            if (AI.Config.showFolderSettings)
            {
                BeginIndentBlock();

                UIBlock("settings.foldersintro", () =>
                {
                    EditorGUILayout.HelpBox("Use Additional Folders to scan for Unity Packages downloaded from somewhere else than the Asset Store or for any arbitrary media files like your model or sound library you want to access.", MessageType.Info);
                });

                EditorGUILayout.Space();
                FolderListControl.DoLayoutList();

                if (_legacyCacheLocationFound)
                {
                    EditorGUILayout.HelpBox("You have selected a custom asset cache location as an additional folder. This should be done using the Asset Cache Location UI above in this new version.", MessageType.Warning);
                }

                // relative locations
                if (AI.UserRelativeLocations.Count > 0)
                {
                    EditorGUILayout.LabelField("Relative Location Mappings", EditorStyles.boldLabel);
                    GUILayout.BeginVertical(EditorStyles.helpBox);
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Key", EditorStyles.boldLabel, GUILayout.Width(200));
                    EditorGUILayout.LabelField("Location", EditorStyles.boldLabel);
                    GUILayout.EndHorizontal();
                    foreach (RelativeLocation location in AI.UserRelativeLocations)
                    {
                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(location.Key, GUILayout.Width(200));

                        string otherSystems = "Mappings on other systems:\n\n";
                        string otherLocs = string.Join("\n", location.otherLocations);
                        otherSystems += string.IsNullOrWhiteSpace(otherLocs) ? "-None-" : otherLocs;

                        if (string.IsNullOrWhiteSpace(location.Location))
                        {
                            EditorGUILayout.LabelField(CommonUIStyles.Content("-Not yet connected-", otherSystems));

                            // TODO: add ability to force delete relative mapping in case it is not used in additional folders anymore
                        }
                        else
                        {
                            EditorGUILayout.LabelField(CommonUIStyles.Content(location.Location, otherSystems));
                            if (string.IsNullOrWhiteSpace(otherLocs))
                            {
                                if (ShowAdvanced())
                                {
                                    if (GUILayout.Button(EditorGUIUtility.IconContent("TreeEditor.Trash", "|Delete mapping"), GUILayout.Width(30)))
                                    {
                                        if (EditorUtility.DisplayDialog("Confirmation", "Are you sure you want to delete this mapping? This will remove it from the database and the tool will no longer be able to access the folder.", "Yes", "Cancel"))
                                        {
                                            DBAdapter.DB.Delete(location);
                                            Paths.LoadRelativeLocations();
                                        }
                                    }
                                }
                                else
                                {
                                    EditorGUI.BeginDisabledGroup(true);
                                    GUILayout.Button(EditorGUIUtility.IconContent("TreeEditor.Trash", "|Cannot delete only remaining mapping"), GUILayout.Width(30));
                                    EditorGUI.EndDisabledGroup();
                                }
                            }
                            else
                            {
                                if (GUILayout.Button(EditorGUIUtility.IconContent("TreeEditor.Trash", "|Delete mapping"), GUILayout.Width(30)))
                                {
                                    DBAdapter.DB.Delete(location);
                                    Paths.LoadRelativeLocations();
                                }
                            }
                        }
                        if (GUILayout.Button(CommonUIStyles.Content("...", "Select folder"), GUILayout.Width(30)))
                        {
                            SelectRelativeFolderMapping(location);
                        }
                        GUILayout.EndHorizontal();
                    }
                    GUILayout.EndVertical();
                }
                EndIndentBlock();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // Asset Manager
            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            AI.Config.showAMSettings = EditorGUILayout.BeginFoldoutHeaderGroup(AI.Config.showAMSettings, "Unity Asset Manager");
            if (EditorGUI.EndChangeCheck()) AI.SaveConfig();

            if (AI.Config.showAMSettings)
            {
                BeginIndentBlock();
                DrawAssetManager();
                EndIndentBlock();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // importing
            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            AI.Config.showImportSettings = EditorGUILayout.BeginFoldoutHeaderGroup(AI.Config.showImportSettings, "Import");
            if (EditorGUI.EndChangeCheck()) AI.SaveConfig();

            if (AI.Config.showImportSettings)
            {
                BeginIndentBlock();

                EditorGUI.BeginChangeCheck();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Adapt to Render Pipeline", "Will automatically adapt materials to the current render pipeline upon import."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));

                EditorGUI.indentLevel++;
                UIBlock("settings.srpintro", () =>
                {
                    EditorGUILayout.HelpBox("There is extensive support for SRPs (scriptable render pipelines). Two mechanisms exist: if a package brings its own SRP support packages, dependencies will automatically be used from these which fit to the current project. If these do not exist, the tool can automatically convert materials to the current render pipeline upon import.", MessageType.Info);
                });

#if USE_URP
                AI.Config.useUnityPipelineConverter = EditorGUILayout.Toggle(
                    CommonUIStyles.Content("Unity Converter", "Use Unity's built-in Render Pipeline Converter to persistently convert materialized assets. Only supports BIRP → URP."),
                    AI.Config.useUnityPipelineConverter);
                EditorGUILayout.LabelField("Acts project-wide. When active it takes precedence and the custom converter will be the fallback. Enable if you find visual artifacts and please report them so the custom converter can be improved.", CommonUIStyles.greyMiniLabel);
#endif
                AI.Config.useCustomPipelineConverter = EditorGUILayout.Toggle(
                    CommonUIStyles.Content("Custom Converter", "Converts only the actual imported assets. Supports BIRP → URP and BIRP → HDRP."),
                    AI.Config.useCustomPipelineConverter);
                EditorGUILayout.LabelField("Acts only on the actual imported assets and is much faster than the Unity converter.", CommonUIStyles.greyMiniLabel);
                EditorGUI.indentLevel--;

                UIBlock("settings.importstructureintro", () =>
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.HelpBox("You can always drag & drop assets from the search into a folder of your choice in the project view. What can be configured is the behavior when using the Import button or double-clicking an asset.", MessageType.Info);
                });

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Structure", "Structure to materialize the imported files in"), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                AI.Config.importStructure = EditorGUILayout.Popup(AI.Config.importStructure, _importStructureOptions, GUILayout.Width(300));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Destination", "Target folder for imported files"), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                AI.Config.importDestination = EditorGUILayout.Popup(AI.Config.importDestination, _importDestinationOptions, GUILayout.Width(300));
                GUILayout.EndHorizontal();

                if (AI.Config.importDestination == 2)
                {
                    DrawFolder("Target Folder", AI.Config.importFolder, "/Assets", newFolder =>
                    {
                        // store only part relative to /Assets
                        AI.Config.importFolder = newFolder?.Substring(Path.GetDirectoryName(Application.dataPath).Length + 1);
                    }, labelWidth, "Select folder for imports", validate =>
                    {
                        if (!validate.Replace("\\", "/").ToLowerInvariant().StartsWith(Application.dataPath.Replace("\\", "/").ToLowerInvariant()))
                        {
                            EditorUtility.DisplayDialog("Error", "Folder must be inside current project", "OK");
                            return false;
                        }
                        return true;
                    });
                }

                if (AI.ShowAdvanced())
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Reorganize on Reimport", "When reimporting files that already exist in the project, move them to the target import structure first instead of overwriting at their current location. Preserves Unity references."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.reorganizeOnReimport = EditorGUILayout.Toggle(AI.Config.reorganizeOnReimport, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    if (AI.Config.reorganizeOnReimport)
                    {
                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CommonUIStyles.Content($"{CommonUIStyles.INDENT}Delete Empty Folders", "Delete folders that become empty after files are moved during reorganization."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                        AI.Config.deleteEmptyFoldersOnReorganize = EditorGUILayout.Toggle(AI.Config.deleteEmptyFoldersOnReorganize, GUILayout.MaxWidth(cbWidth));
                        GUILayout.EndHorizontal();
                    }

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Calculate FBX Dependencies", "Will scan FBX files for embedded texture references. This is recommended for maximum compatibility but can reduce performance of dependency calculation and preview generation."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.scanFBXDependencies = EditorGUILayout.Toggle(AI.Config.scanFBXDependencies, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Cross-Package Dependencies", "If referenced GUIDs cannot be found in the current package, the tool will scan the whole database if a match can be found somewhere else. Some asset authors rely on having multiple packs installed, e.g. level assembly packs."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.allowCrossPackageDependencies = EditorGUILayout.Toggle(AI.Config.allowCrossPackageDependencies, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Remove LODs", "Will remove LOD groups from imported prefabs and only keep the first one."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.removeLODs = EditorGUILayout.Toggle(AI.Config.removeLODs, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Skip ProjectSettings", "When importing packages in automatic/unattended mode, skip files in the ProjectSettings folder to prevent accidentally overwriting project settings."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.skipProjectSettings = EditorGUILayout.Toggle(AI.Config.skipProjectSettings, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();
                }

                if (EditorGUI.EndChangeCheck()) AI.SaveConfig();
                EndIndentBlock();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // preview images
            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            AI.Config.showPreviewSettings = EditorGUILayout.BeginFoldoutHeaderGroup(AI.Config.showPreviewSettings, "Previews");
            if (EditorGUI.EndChangeCheck()) AI.SaveConfig();

            if (AI.Config.showPreviewSettings)
            {
                BeginIndentBlock();
                EditorGUI.BeginChangeCheck();

                if (ShowAdvanced())
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Use Provided Preview Images", "Will extract preview images from the package if it provides previews already. Will require a moderate amount of space if there are many files. The provided previews are usually rather bad quality and max 128px but a good first start to save time."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.extractPreviews = EditorGUILayout.Toggle(AI.Config.extractPreviews, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Use Small Image Files Directly", "Will not create a separate preview file in the Previews folder if an image file is in an additional folder with dimensions fitting to the preview size. Recommended to speed up the preview pipeline and reduce storage size."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.directMediaPreviews = EditorGUILayout.Toggle(AI.Config.directMediaPreviews, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Use Fallback-Icons as Previews", "Will show generic icons in case a file preview is missing instead of an empty tile."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.showIconsForMissingPreviews = EditorGUILayout.Toggle(AI.Config.showIconsForMissingPreviews, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();
                }

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Verify Previews", "Will check preview images if they indeed contain a preview or just Unity default icons. Highly recommended but will slow down indexing and preview recreation."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                AI.Config.verifyPreviews = EditorGUILayout.Toggle(AI.Config.verifyPreviews, GUILayout.MaxWidth(cbWidth));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Recreate Previews After Indexing", "Will run the preview recreation automatically once a package is indexed in case previews are missing or erroneous. Recommended, especially in combination with preview verification."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                AI.Config.recreatePreviewsAfterIndexing = EditorGUILayout.Toggle(AI.Config.recreatePreviewsAfterIndexing, GUILayout.MaxWidth(cbWidth));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Download Missing Packages", "Will automatically temporarily download packages for which previews are missing."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                AI.Config.downloadPackagesForPreviews = EditorGUILayout.Toggle(AI.Config.downloadPackagesForPreviews, GUILayout.MaxWidth(cbWidth));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Override Icons in Project Window", "Displays your custom preview icons for prefabs directly in Unity's Project window, overriding the ones Unity would show."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                AI.Config.overrideProjectPreviews = EditorGUILayout.Toggle(AI.Config.overrideProjectPreviews, GUILayout.MaxWidth(cbWidth));
                GUILayout.EndHorizontal();

                if (AI.Config.overrideProjectPreviews)
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content($"{CommonUIStyles.INDENT}Play Animations", "Play animated previews when available instead of showing static previews."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.playProjectWindowAnimations = EditorGUILayout.Toggle(AI.Config.playProjectWindowAnimations, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();
                }

                if (ShowAdvanced())
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Confirm Preview Rescheduling", "Show a confirmation dialog before scheduling preview recreation in the Preview Wizard."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.confirmPreviewRescheduling = EditorGUILayout.Toggle(AI.Config.confirmPreviewRescheduling, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Parallel Processing", "Number of previews to process simultaneously. Higher values can speed up preview generation but may use more memory and CPU. Set to 1 for sequential processing."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.parallelPreviewBatchSize = EditorGUILayout.DelayedIntField(AI.Config.parallelPreviewBatchSize, GUILayout.Width(50));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Bulk Preview Threshold", "When more than the number of files here from one page are requested for previews, the whole package will be materialized instead of each file one by one. This usually results in major performance improvements."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.bulkPreviewThreshold = EditorGUILayout.DelayedIntField(AI.Config.bulkPreviewThreshold, GUILayout.Width(50));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Wait Time", "Minimum time in seconds to wait for Unity's preview generation before giving up. Lower values speed up indexing but may skip some previews. Unity's preview system can be slow and unreliable."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.minPreviewWait = EditorGUILayout.DelayedFloatField(AI.Config.minPreviewWait, GUILayout.Width(50));
                    EditorGUILayout.LabelField("seconds");
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Exclude Extensions", "File extensions or type groups in curly braces (e.g. {audio}, {images}, {models}) that should be skipped when creating preview images during media and archive indexing. Type groups automatically expand to all registered extensions for that group."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.excludePreviewExtensions = EditorGUILayout.Toggle(AI.Config.excludePreviewExtensions, GUILayout.Width(16));
                    if (AI.Config.excludePreviewExtensions)
                    {
                        AI.Config.excludedPreviewExtensions = EditorGUILayout.DelayedTextField(AI.Config.excludedPreviewExtensions, GUILayout.Width(300));
                        if (GUILayout.Button(EditorGUIUtility.IconContent("editicon.sml", "|Edit list"), GUILayout.Width(24)))
                        {
                            StringListUI listUI = new StringListUI();
                            listUI.Init(AI.Config.excludedPreviewExtensions, ",", result =>
                            {
                                AI.Config.excludedPreviewExtensions = result;
                                AI.SaveConfig();
                            }, "Excluded Preview Extensions");
                            PopupWindow.Show(GetPopupPositionAtMouse(), listUI);
                        }
                    }
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Keep Cached on Audio Playback", "Will set the 'Keep Cached' flag on a package to true when previewing an audio clip from it to ensure audio plays back smoothly without waiting for extraction first in the future."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.keepExtractedOnAudio = EditorGUILayout.Toggle(AI.Config.keepExtractedOnAudio, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();
                }

                // Custom Preview Pipeline
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Custom Preview Pipeline", EditorStyles.boldLabel);
                UIBlock("settings.customprevintro", () =>
                {
                    EditorGUILayout.HelpBox("Unity will only create previews for prefabs containing 3D models. If prefabs contain UI, particles or other visual effects, no preview will be shown. Also, the size is limited to 128 by 128px. The custom pipeline provides better previews for all prefab types with configurable lighting, camera angles, and super-sampling. UI, VFX, and Particle prefabs always use the custom pipeline automatically.", MessageType.Info);
                });

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Configure Custom Previews...", GUILayout.Width(labelWidth)))
                {
                    CustomPreviewSettingsUI.ShowWindow();
                }
                GUILayout.EndHorizontal();

#if UNITY_EDITOR_WIN && !NET_4_6
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox("Your 'Editor Assembly Compatibility Level' is set to '.NET Standard' in the Player Settings. This will cause the tool to use an alternative image processing library which is slower on Windows. If you do not have a specific need for .NET Standard it is recommended to switch to .NET Framework.", MessageType.Warning);
#endif
#if !USE_VECTOR_GRAPHICS
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox("In order to see previews for SVG graphics, the 'com.unity.vectorgraphics' needs to be installed.", MessageType.Warning);
                if (GUILayout.Button("Install Vector Graphics Package"))
                {
                    Client.Add("com.unity.vectorgraphics");
                }
#endif
#if !USE_SHADER_GRAPH
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox("In order to see previews for Shader Graph assets, the 'com.unity.shadergraph' package needs to be installed.", MessageType.Warning);
                if (GUILayout.Button("Install Shader Graph Package"))
                {
                    Client.Add("com.unity.shadergraph");
                }
#endif
#if !USE_VFX && UNITY_6000_0_OR_NEWER
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox("In order to generate previews for Visual Effect Graph assets, the 'com.unity.visualeffectgraph' package needs to be installed.", MessageType.Warning);
                if (GUILayout.Button("Install Visual Effects Graph Package"))
                {
                    Client.Add("com.unity.visualeffectgraph");
                }
#endif
#if !USE_TEXTMESHPRO && !UNITY_2023_2_OR_NEWER
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox("In order to see previews for TextMeshPro assets, the 'com.unity.textmeshpro' package needs to be installed.", MessageType.Warning);
                if (GUILayout.Button("Install TextMeshPro Package"))
                {
                    Client.Add("com.unity.textmeshpro");
                }
#endif
#if USE_TEXTMESHPRO || UNITY_2023_2_OR_NEWER
                if (!TMPStep.AreTMPEssentialsImported())
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.HelpBox("TextMeshPro Essentials need to be imported for full text rendering support. This will add default fonts, shaders, and settings required by TextMeshPro.", MessageType.Warning);
                    if (GUILayout.Button("Import TMP Essentials"))
                    {
                        TMPStep.ImportEssentials();
                    }
                }
#endif

                if (EditorGUI.EndChangeCheck())
                {
                    AI.SaveConfig();
                    _requireSearchUpdate = true;
                }
                EndIndentBlock();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // backup
            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            AI.Config.showBackupSettings = EditorGUILayout.BeginFoldoutHeaderGroup(AI.Config.showBackupSettings, "Backup");
            if (EditorGUI.EndChangeCheck()) AI.SaveConfig();

            if (AI.Config.showBackupSettings)
            {
                BeginIndentBlock();
                EditorGUILayout.HelpBox("Automatically create backups of your asset purchases. Unity does not store old versions and assets get regularly deprecated. Backups will allow you to go back to previous versions easily. Backups will be done at the end of each update cycle.", MessageType.Info);
                EditorGUI.BeginChangeCheck();

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Activated Packages"), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                EditorGUILayout.LabelField(CommonUIStyles.Content($"{_stats?.BackupPackages} (set per package in Packages view)"), EditorStyles.wordWrappedLabel);
                if (ShowAdvanced())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Show")) ShowPackageMaintenance(PackageSearch.MaintenanceOption.MarkedForBackup);
                }
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Override Patch Versions", "Will remove all but the latest patch version of an asset inside the same minor version (e.g. 5.4.3 instead of 5.4.2)"), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                AI.Config.onlyLatestPatchVersion = EditorGUILayout.Toggle(AI.Config.onlyLatestPatchVersion, GUILayout.MaxWidth(cbWidth));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Backups per Asset", "Number of versions to keep per asset"), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                AI.Config.backupsPerAsset = EditorGUILayout.DelayedIntField(AI.Config.backupsPerAsset, GUILayout.Width(50));
                GUILayout.EndHorizontal();

                DrawFolder("Storage Folder", AI.Config.backupFolder, Paths.GetBackupFolder(false), newFolder => AI.Config.backupFolder = newFolder, labelWidth);

                EditorGUILayout.Space();
                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Enable Database & Config Backups", "Automatically create backups of the database and current configuration file at the configured interval."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                AI.Config.enableDatabaseBackup = EditorGUILayout.Toggle(AI.Config.enableDatabaseBackup, GUILayout.MaxWidth(cbWidth));
                GUILayout.EndHorizontal();

                if (AI.Config.enableDatabaseBackup)
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content($"{CommonUIStyles.INDENT}Backup Interval (days)", "Number of days between automatic database backups."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.databaseBackupInterval = EditorGUILayout.DelayedIntField(AI.Config.databaseBackupInterval, GUILayout.Width(50));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content($"{CommonUIStyles.INDENT}Number of Backups to Keep", "Maximum number of database backups to retain. Older backups will be automatically deleted."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.databaseBackupsToKeep = EditorGUILayout.DelayedIntField(AI.Config.databaseBackupsToKeep, GUILayout.Width(50));
                    EditorGUI.BeginDisabledGroup(DBAdapter.IsBackingUp);
                    if (GUILayout.Button(DBAdapter.IsBackingUp ? "Backing Up..." : "Backup Now", GUILayout.ExpandWidth(false)))
                    {
                        _ = DBAdapter.BackupDatabaseAsync(skipIntervalCheck: true);
                    }
                    EditorGUI.EndDisabledGroup();
                    GUILayout.EndHorizontal();
                }

                if (EditorGUI.EndChangeCheck()) AI.SaveConfig();
                EndIndentBlock();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // AI
            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            AI.Config.showAISettings = EditorGUILayout.BeginFoldoutHeaderGroup(AI.Config.showAISettings, "Artificial Intelligence");
            if (EditorGUI.EndChangeCheck()) AI.SaveConfig();

            if (AI.Config.showAISettings)
            {
                BeginIndentBlock();
                EditorGUI.BeginChangeCheck();

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Activated Packages"), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                EditorGUILayout.LabelField(CommonUIStyles.Content($"{_stats?.AIPackages} (set per package in Packages view)"), EditorStyles.wordWrappedLabel);
                if (ShowAdvanced())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Show")) ShowPackageMaintenance(PackageSearch.MaintenanceOption.MarkedForAI);
                }
                GUILayout.EndHorizontal();

                EditorGUILayout.LabelField("Create Captions for", EditorStyles.boldLabel, GUILayout.Width(labelWidth));

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content($"{CommonUIStyles.INDENT}Prefabs", "Will create captions for prefabs."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                AI.Config.aiForPrefabs = EditorGUILayout.Toggle(AI.Config.aiForPrefabs, GUILayout.MaxWidth(cbWidth));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content($"{CommonUIStyles.INDENT}Images", "Will create captions for image files."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                AI.Config.aiForImages = EditorGUILayout.Toggle(AI.Config.aiForImages, GUILayout.MaxWidth(cbWidth));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content($"{CommonUIStyles.INDENT}Models", "Will create captions for model files."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                AI.Config.aiForModels = EditorGUILayout.Toggle(AI.Config.aiForModels, GUILayout.MaxWidth(cbWidth));
                GUILayout.EndHorizontal();

                if (ShowAdvanced())
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Log Created Captions", "Will print finished captions to the console."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.logAICaptions = EditorGUILayout.Toggle(AI.Config.logAICaptions, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Max Caption Length", "Some models can generate extremely long captions in boundary conditions. This setting caps the max length to preserve memory and display quality."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.aiMaxCaptionLength = EditorGUILayout.DelayedIntField(AI.Config.aiMaxCaptionLength, GUILayout.Width(50));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Pause Between Calculations", "AI inference requires significant resources and will bring a system to full load. Running constantly can lead to system crashes. Feel free to experiment with lower pauses."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.aiPause = EditorGUILayout.DelayedFloatField(AI.Config.aiPause, GUILayout.Width(50));
                    EditorGUILayout.LabelField("seconds", EditorStyles.miniLabel);
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Request Timeout", "Cancel an individual AI request after this many seconds (e.g. when a model takes too long to load). Set to 0 to disable. The global Stop button always works regardless of this setting."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.aiTimeout = Mathf.Max(0, EditorGUILayout.DelayedIntField(AI.Config.aiTimeout, GUILayout.Width(50)));
                    EditorGUILayout.LabelField(AI.Config.aiTimeout == 0 ? "seconds (off)" : "seconds", EditorStyles.miniLabel);
                    GUILayout.EndHorizontal();
                }

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Backend", "The technology to use for AI."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                AI.Config.aiBackend = EditorGUILayout.Popup(AI.Config.aiBackend, _aiBackendOptions, GUILayout.Width(100));
                GUILayout.EndHorizontal();

                bool showTestImage = true;
                BeginIndentBlock();
                switch (AI.Config.aiBackend)
                {
                    case 0:
                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CommonUIStyles.Content("Installation", "The model to be used for captioning. Local models are free of charge, but require a potent computer and graphics card."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                        GUILayout.BeginVertical();
                        EditorGUILayout.HelpBox("This backend requires installing the Blip-Caption tool. It is free of charge and the guide can be found under the GitHub link below (Python, pipx, blip).", MessageType.Info);
                        if (GUILayout.Button("Salesforce Blip through Blip-Caption tool (local, free)", CommonUIStyles.wrappedLinkLabel, GUILayout.ExpandWidth(true)))
                        {
                            AI.OpenURL("https://github.com/simonw/blip-caption");
                        }
                        GUILayout.EndVertical();
                        GUILayout.EndHorizontal();

                        if (ShowAdvanced())
                        {
                            DrawFolder("Blip Folder", AI.Config.blipPath, null, newFolder => AI.Config.blipPath = newFolder, labelWidth);
                        }

                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CommonUIStyles.Content("Model", "The variant of the model that should be used."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                        AI.Config.blipType = EditorGUILayout.Popup(AI.Config.blipType, _blipOptions, GUILayout.Width(100));
                        GUILayout.EndHorizontal();

                        if (ShowAdvanced())
                        {
                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Ignore empty results", "Will not stop the captioning process when encountering empty captions which typically means the tooling is not properly set up."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                            AI.Config.aiContinueOnEmpty = EditorGUILayout.Toggle(AI.Config.aiContinueOnEmpty, GUILayout.MaxWidth(cbWidth));
                            GUILayout.EndHorizontal();

                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Use GPU", "Activate GPU acceleration if your system supports it. Otherwise only the CPU will be used. GPU support requires a patched blip version supporting GPU usage, see pull request 8."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                            AI.Config.blipUseGPU = EditorGUILayout.Toggle(AI.Config.blipUseGPU, GUILayout.MaxWidth(cbWidth));
                            GUILayout.EndHorizontal();
                        }

                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CommonUIStyles.Content("Batch Size", "Number of files that are captioned by the model at once."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                        AI.Config.blipChunkSize = EditorGUILayout.DelayedIntField(AI.Config.blipChunkSize, GUILayout.Width(50));
                        GUILayout.EndHorizontal();
                        break;

                    case 1:
                        if (ShowAdvanced())
                        {
                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Service URL", "The URL of the Ollama service."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                            EditorGUI.BeginChangeCheck();
                            AI.Config.ollamaServiceUrl = EditorGUILayout.DelayedTextField(AI.Config.ollamaServiceUrl, GUILayout.Width(250));
                            bool urlChanged = EditorGUI.EndChangeCheck();
                            if (GUILayout.Button("Reset", GUILayout.ExpandWidth(false)))
                            {
                                AI.Config.ollamaServiceUrl = Intelligence.OLLAMA_SERVICE_URL;
                                urlChanged = true;
                            }
                            if (urlChanged) Intelligence.RefreshOllama();
                            GUILayout.EndHorizontal();
                        }

                        if (Intelligence.IsOllamaInstalled)
                        {
                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Model", "The model to use. Must be listed in the Ollama library and support vision input and analysis."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                            GUILayout.BeginVertical();
                            GUILayout.BeginHorizontal();
                            AI.Config.ollamaModel = EditorGUILayout.DelayedTextField(AI.Config.ollamaModel, GUILayout.Width(150)).Trim();
                            if (!string.IsNullOrWhiteSpace(AI.Config.ollamaModel) && !Intelligence.OllamaModelDownloaded(AI.Config.ollamaModel))
                            {
                                EditorGUI.BeginDisabledGroup(Intelligence.DownloadingModel);
                                if (GUILayout.Button("Download Model", GUILayout.ExpandWidth(false))) DownloadOllamaModel();
                                EditorGUI.EndDisabledGroup();
                            }
                            if (EditorGUILayout.DropdownButton(CommonUIStyles.Content("Installed"), FocusType.Keyboard, CommonUIStyles.centerPopup, GUILayout.ExpandWidth(false))) ShowInstalledOllamaModels();
                            if (EditorGUILayout.DropdownButton(CommonUIStyles.Content("Suggested"), FocusType.Keyboard, CommonUIStyles.centerPopup, GUILayout.ExpandWidth(false))) ShowSuggestedOllamaModels();
                            if (ShowAdvanced() && Intelligence.OllamaModelDownloaded(AI.Config.ollamaModel))
                            {
                                if (GUILayout.Button(EditorGUIUtility.IconContent("TreeEditor.Trash", "|Delete model"), GUILayout.Width(30)))
                                {
                                    DeleteOllamaModel();
                                }
                            }
                            GUILayout.EndHorizontal();
                            if (Intelligence.DownloadingModel)
                            {
                                GUILayout.BeginHorizontal();
                                GUILayout.Space(3);
                                CommonUIStyles.DrawProgressBar(
                                    (float)_curOllamaProgress / _maxOllamaProgress,
                                    $"{EditorUtility.FormatBytes(_curOllamaProgress)}/{EditorUtility.FormatBytes(_maxOllamaProgress)}",
                                    GUILayout.MaxWidth(150));
                                if (GUILayout.Button("Cancel", GUILayout.ExpandWidth(false))) Intelligence.OllamaDownloadToken?.Cancel();
                                GUILayout.EndHorizontal();
                            }
                            ModelInfo model = Intelligence.OllamaModels?.FirstOrDefault(m => m.Name == AI.Config.ollamaModel);
                            if (model != null && (model.Size / 1024 / 1024) + 2000 > SystemInfo.graphicsMemorySize) // add some buffer for system usage
                            {
                                EditorGUILayout.HelpBox($"The model probably requires more VRAM than your system has ({model.Size / 1024 / 1024:N0}Mb vs {SystemInfo.graphicsMemorySize:N0}Mb). This will lead to much slower performance.", MessageType.Warning);
                            }
                            if (GUILayout.Button("Model Catalog", CommonUIStyles.wrappedLinkLabel, GUILayout.ExpandWidth(false)))
                            {
                                AI.OpenURL(Intelligence.OLLAMA_LIBRARY);
                            }
                            GUILayout.EndVertical();
                            GUILayout.EndHorizontal();

                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Batch Size", "Number of requests to send to Ollama in parallel (1 = sequential, 2-4 recommended). Match this to the OLLAMA_NUM_PARALLEL environment variable on the Ollama server (default 4); requests beyond that just queue server-side."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                            AI.Config.ollamaParallelRequests = EditorGUILayout.DelayedIntField(AI.Config.ollamaParallelRequests, GUILayout.Width(50));
                            GUILayout.EndHorizontal();
                        }
                        else
                        {
                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Installation", ""), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                            GUILayout.BeginVertical();
                            EditorGUILayout.HelpBox("Ollama is not installed or active. Start it first and retry.", MessageType.Error);
                            GUILayout.BeginHorizontal();
                            if (GUILayout.Button("Refresh", GUILayout.ExpandWidth(false))) Intelligence.RefreshOllama();
                            if (GUILayout.Button("Ollama Website", CommonUIStyles.wrappedLinkLabel, GUILayout.ExpandWidth(false)))
                            {
                                AI.OpenURL(Intelligence.OLLAMA_WEBSITE);
                            }
                            GUILayout.EndHorizontal();
                            GUILayout.EndVertical();
                            GUILayout.EndHorizontal();
                        }
                        break;

                    case 2:
                        if (ShowAdvanced())
                        {
                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Service URL", "The URL of the LM Studio service."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                            EditorGUI.BeginChangeCheck();
                            AI.Config.lmStudioServiceUrl = EditorGUILayout.DelayedTextField(AI.Config.lmStudioServiceUrl ?? Intelligence.LMSTUDIO_SERVICE_URL, GUILayout.Width(250));
                            bool urlChanged = EditorGUI.EndChangeCheck();
                            if (GUILayout.Button("Reset", GUILayout.ExpandWidth(false)))
                            {
                                AI.Config.lmStudioServiceUrl = Intelligence.LMSTUDIO_SERVICE_URL;
                                urlChanged = true;
                            }
                            if (urlChanged) Intelligence.RefreshLMStudio();
                            GUILayout.EndHorizontal();
                        }

                        if (Intelligence.IsLMStudioInstalled)
                        {
                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Model", "The model to use. Must be installed in LM Studio and support vision input (VLM). Models must be in GGUF format."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                            GUILayout.BeginVertical();
                            GUILayout.BeginHorizontal();
                            AI.Config.lmStudioModel = EditorGUILayout.DelayedTextField(AI.Config.lmStudioModel ?? string.Empty, GUILayout.Width(250)).Trim();
                            if (EditorGUILayout.DropdownButton(CommonUIStyles.Content("Installed"), FocusType.Keyboard, CommonUIStyles.centerPopup, GUILayout.ExpandWidth(false))) ShowInstalledLMStudioModels();
                            GUILayout.EndHorizontal();
                            // Always reserve space for HelpBox to avoid layout mismatch between passes
                            if (Intelligence.LoadingLMStudioModels)
                            {
                                EditorGUILayout.HelpBox("Loading models...", MessageType.Info);
                            }
                            else
                            {
                                // Reserve same space when not loading to maintain consistent layout
                                EditorGUILayout.Space();
                            }
                            LMStudioModel lmStudioModel = Intelligence.LMStudioModels?.FirstOrDefault(m =>
                                !string.IsNullOrEmpty(AI.Config.lmStudioModel) &&
                                (m.id == AI.Config.lmStudioModel || m.id.Contains(AI.Config.lmStudioModel)));
                            if (ShowAdvanced())
                            {
                                if (lmStudioModel != null && !string.IsNullOrEmpty(lmStudioModel.state))
                                {
                                    string stateText = lmStudioModel.state == "loaded" ? "Loaded" : "Not loaded";
                                    EditorGUILayout.HelpBox($"Model state: {stateText}", MessageType.Info);
                                }
                                if (GUILayout.Button("LM Studio Website", EditorStyles.linkLabel, GUILayout.ExpandWidth(false)))
                                {
                                    AI.OpenURL(Intelligence.LMSTUDIO_WEBSITE);
                                }
                            }
                            GUILayout.EndVertical();
                            GUILayout.EndHorizontal();

                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Batch Size", "Number of requests to send in parallel (1 = sequential, 2-4 recommended for better GPU utilization). LMStudio will queue requests internally."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                            AI.Config.lmStudioParallelRequests = EditorGUILayout.DelayedIntField(AI.Config.lmStudioParallelRequests, GUILayout.Width(50));
                            GUILayout.EndHorizontal();
                        }
                        else
                        {
                            GUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(CommonUIStyles.Content("Installation", ""), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                            GUILayout.BeginVertical();
                            EditorGUILayout.HelpBox("LM Studio is not installed or the server is not running. Start LM Studio and enable the local server first, then retry.", MessageType.Error);
                            GUILayout.BeginHorizontal();
                            if (GUILayout.Button("Refresh", GUILayout.ExpandWidth(false))) Intelligence.RefreshLMStudio();
                            if (GUILayout.Button("LM Studio Website", EditorStyles.linkLabel, GUILayout.ExpandWidth(false)))
                            {
                                AI.OpenURL(Intelligence.LMSTUDIO_WEBSITE);
                            }
                            GUILayout.EndHorizontal();
                            GUILayout.EndVertical();
                            GUILayout.EndHorizontal();
                        }
                        break;
                }
                EndIndentBlock();

                if (showTestImage)
                {
                    EditorGUILayout.Space();
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Test Image", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    GUILayout.BeginVertical(GUILayout.Width(120));
                    GUILayout.Box(Logo, EditorStyles.centeredGreyMiniLabel, GUILayout.Width(100), GUILayout.MaxHeight(100));
                    EditorGUI.BeginDisabledGroup(_captionTestRunning);
                    if (GUILayout.Button("Create Caption", GUILayout.ExpandWidth(false))) TestCaptioning();
                    if (AI.Config.aiBackend == 1) // Ollama
                    {
                        EditorGUI.BeginDisabledGroup(!Intelligence.IsOllamaInstalled || Intelligence.LoadingModels);
                        if (GUILayout.Button("Model Tester...", GUILayout.ExpandWidth(false)))
                        {
                            ModelTesterUI.ShowWindow(GetTestImageFolder(), AI.Config.aiCustomPrompt, OnModelTesterPromptChanged);
                        }
                        EditorGUI.EndDisabledGroup();
                    }
                    else if (AI.Config.aiBackend == 2) // LMStudio
                    {
                        EditorGUI.BeginDisabledGroup(!Intelligence.IsLMStudioInstalled || Intelligence.LoadingLMStudioModels);
                        if (GUILayout.Button("Model Tester...", GUILayout.ExpandWidth(false)))
                        {
                            ModelTesterUI.ShowWindow(GetTestImageFolder(), AI.Config.aiCustomPrompt, OnModelTesterPromptChanged);
                        }
                        EditorGUI.EndDisabledGroup();
                    }
                    EditorGUI.EndDisabledGroup();
                    GUILayout.EndVertical();
                    EditorGUILayout.LabelField(_captionTest, EditorStyles.wordWrappedLabel);
                    GUILayout.EndHorizontal();
                }
                if (EditorGUI.EndChangeCheck()) AI.SaveConfig();
                EndIndentBlock();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // UI
            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            AI.Config.showUISettings = EditorGUILayout.BeginFoldoutHeaderGroup(AI.Config.showUISettings, "UI Integration");
            if (EditorGUI.EndChangeCheck()) AI.SaveConfig();

            if (AI.Config.showUISettings)
            {
                BeginIndentBlock();

                EditorGUILayout.LabelField("'Assets' Menu", EditorStyles.largeLabel);

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Show Asset Inventory"), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                if (EditorUtils.HasDefine(AI.DEFINE_SYMBOL_HIDE_AI))
                {
                    if (GUILayout.Button("Enable", GUILayout.ExpandWidth(false))) EditorUtils.RemoveDefine(AI.DEFINE_SYMBOL_HIDE_AI);
                }
                else
                {
                    if (GUILayout.Button("Disable", GUILayout.ExpandWidth(false))) EditorUtils.AddDefine(AI.DEFINE_SYMBOL_HIDE_AI);
                }
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Show Asset Browser"), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                if (EditorUtils.HasDefine(AI.DEFINE_SYMBOL_HIDE_BROWSER))
                {
                    if (GUILayout.Button("Enable", GUILayout.ExpandWidth(false))) EditorUtils.RemoveDefine(AI.DEFINE_SYMBOL_HIDE_BROWSER);
                }
                else
                {
                    if (GUILayout.Button("Disable", GUILayout.ExpandWidth(false))) EditorUtils.AddDefine(AI.DEFINE_SYMBOL_HIDE_BROWSER);
                }
                GUILayout.EndHorizontal();

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("'Tools' Menu", EditorStyles.largeLabel);

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Show Asset Inventory"), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                if (EditorUtils.HasDefine(AI.DEFINE_SYMBOL_HIDE_TOOLS_MENU))
                {
                    if (GUILayout.Button("Enable", GUILayout.ExpandWidth(false))) EditorUtils.RemoveDefine(AI.DEFINE_SYMBOL_HIDE_TOOLS_MENU);
                }
                else
                {
                    if (GUILayout.Button("Disable", GUILayout.ExpandWidth(false))) EditorUtils.AddDefine(AI.DEFINE_SYMBOL_HIDE_TOOLS_MENU);
                }
                GUILayout.EndHorizontal();

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Editor Windows", EditorStyles.largeLabel);

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Project Window Toolbar"), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                if (EditorUtils.HasDefine(AI.DEFINE_SYMBOL_HIDE_PROJECT_TOOLBAR))
                {
                    if (GUILayout.Button("Enable", GUILayout.ExpandWidth(false))) EditorUtils.RemoveDefine(AI.DEFINE_SYMBOL_HIDE_PROJECT_TOOLBAR);
                }
                else
                {
                    if (GUILayout.Button("Disable", GUILayout.ExpandWidth(false))) EditorUtils.AddDefine(AI.DEFINE_SYMBOL_HIDE_PROJECT_TOOLBAR);
                }
                GUILayout.EndHorizontal();

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Browser", EditorStyles.largeLabel);

                EditorGUI.BeginChangeCheck();
                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Open Links With", "Select which browser to use when opening URLs from Asset Inventory."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                AI.Config.browserType = EditorGUILayout.Popup(AI.Config.browserType, _browserTypeOptions, GUILayout.Width(200));
                GUILayout.EndHorizontal();

                if (AI.Config.browserType == 1)
                {
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Browser Application", "Full path to the browser executable to use for opening links."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.customBrowserPath = EditorGUILayout.TextField(AI.Config.customBrowserPath, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("Browse...", GUILayout.ExpandWidth(false)))
                    {
#if UNITY_EDITOR_OSX
                        string path = EditorUtility.OpenFilePanel("Select Browser Application", "/Applications", "app");
#elif UNITY_EDITOR_LINUX
                        string path = EditorUtility.OpenFilePanel("Select Browser Application", "/usr/bin", "");
#else
                        string path = EditorUtility.OpenFilePanel("Select Browser Application", "C:\\Program Files", "exe");
#endif
                        if (!string.IsNullOrEmpty(path))
                        {
                            AI.Config.customBrowserPath = path;
                            AI.SaveConfig();
                        }
                    }
                    GUILayout.EndHorizontal();
                }
                if (EditorGUI.EndChangeCheck()) AI.SaveConfig();

                EndIndentBlock();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // locations
            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            AI.Config.showLocationSettings = EditorGUILayout.BeginFoldoutHeaderGroup(AI.Config.showLocationSettings, "Locations");
            if (EditorGUI.EndChangeCheck()) AI.SaveConfig();

            if (AI.Config.showLocationSettings)
            {
                BeginIndentBlock();
                EditorGUILayout.HelpBox("Per default all folders reside at the database location. Especially the cache, backup and preview folders can become quite large. You can move those to a different location with more space if needed. The database itself should be on the fastest available drive. If you change the locations, make sure to move the former contents along in case you want to keep the data.", MessageType.Info);
                EditorGUI.BeginChangeCheck();

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Database", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                EditorGUI.BeginDisabledGroup(true);
                string dbType = AI.Config?.databaseType ?? DatabaseFactory.SQLITE;
                string dbInfo = dbType == DatabaseFactory.MYSQL ? $"{dbType} - {AI.Config?.mysqlHost ?? "localhost"}" : $"{dbType} - {Paths.GetStorageFolder()}";
                EditorGUILayout.TextField(dbInfo, GUILayout.ExpandWidth(true));
                EditorGUI.EndDisabledGroup();
                EditorGUI.BeginDisabledGroup(AI.Actions.AnyActionsInProgress);
                if (dbType == DatabaseFactory.SQLITE && GUILayout.Button("Browse...", GUILayout.ExpandWidth(false)))
                {
                    SetDatabaseLocation();
                }
                if (GUILayout.Button("Configure...", GUILayout.ExpandWidth(false)))
                {
                    DatabaseConfigurationUI.ShowWindow();
                }
                EditorGUI.EndDisabledGroup();
                GUILayout.EndHorizontal();

                DrawFolder("Backups", AI.Config.backupFolder, Paths.GetBackupFolder(false), newFolder => AI.Config.backupFolder = newFolder, labelWidth);
                DrawFolder("Previews", AI.Config.previewFolder, Paths.GetPreviewFolder(null, true), newFolder =>
                {
                    AI.Config.previewFolder = newFolder;
                    Paths.RefreshPreviewCache();
                }, labelWidth);
                DrawFolder("Cache", AI.Config.cacheFolder, Paths.GetMaterializeFolder(), newFolder => AI.Config.cacheFolder = newFolder, labelWidth);

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(CommonUIStyles.Content("Limit Cache Size", "Flag if to regularly scan the cache folder and remove old items until the size limit is reached again. Only items that are not marked as 'Keep Extracted' will be removed."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                AI.Config.limitCacheSize = EditorGUILayout.Toggle(AI.Config.limitCacheSize, GUILayout.MaxWidth(cbWidth));
                if (AI.Config.limitCacheSize)
                {
                    AI.Config.cacheLimit = EditorGUILayout.DelayedIntField(AI.Config.cacheLimit, GUILayout.Width(50));
                    EditorGUILayout.LabelField("Gb", EditorStyles.miniLabel, GUILayout.Width(20));
                    EditorGUI.BeginDisabledGroup(AI.CacheLimiter.IsRunning);
                    if (GUILayout.Button(AI.CacheLimiter.IsRunning ? "Calculating..." : "Run Check", GUILayout.ExpandWidth(false)))
                    {
                        _ = AI.CacheLimiter.CheckAndClean();
                    }
                    if (AI.CacheLimiter.CurrentSize > 0)
                    {
                        EditorGUILayout.LabelField($"Current Size: {EditorUtility.FormatBytes(AI.CacheLimiter.CurrentSize)}", EditorStyles.miniLabel);
                    }
                    else if (AI.CacheLimiter.CurrentSize > AI.CacheLimiter.GetLimit())
                    {
                        EditorGUILayout.LabelField($"The current cache size with {EditorUtility.FormatBytes(AI.CacheLimiter.CurrentSize)} exceeds the limit due to persistent cache entries ('Keep Cached' setting per package) that will not be cleaned up.", EditorStyles.wordWrappedMiniLabel);
                    }
                    EditorGUI.EndDisabledGroup();
                    GUILayout.FlexibleSpace();
                }
                GUILayout.EndHorizontal();
                EditorGUILayout.Space();

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Configuration", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                GUILayout.BeginVertical();
                GUILayout.BeginHorizontal();
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.TextField(AI.UsedConfigLocation, GUILayout.ExpandWidth(true));
                EditorGUI.EndDisabledGroup();
                if (GUILayout.Button("Open", GUILayout.ExpandWidth(false))) EditorUtility.RevealInFinder(AI.UsedConfigLocation);
                GUILayout.EndHorizontal();
                EditorGUILayout.HelpBox("To change, either copy the json file into your project to use a project-specific configuration or use the 'ASSETINVENTORY_CONFIG_PATH' environment variable to define a new global location (see documentation).", MessageType.Info);
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();

                if (EditorGUI.EndChangeCheck())
                {
                    AI.SaveConfig();
                    AI.CacheLimiter.Enabled = AI.Config.limitCacheSize;
                    AI.CacheLimiter.SetLimit(AI.Config.cacheLimit);
                }
                EditorGUILayout.Space();

                DrawFolder("Custom HTML Templates", AI.Config.customTemplateFolder, TemplateUtils.GetTemplateRootFolder(), newFolder => AI.Config.customTemplateFolder = newFolder, labelWidth);

                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("FTP/SFTP Connections", EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                if (GUILayout.Button("Configure...", GUILayout.ExpandWidth(false)))
                {
                    FTPAdminUI.ShowWindow();
                }
                GUILayout.EndHorizontal();

                EndIndentBlock();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // advanced
            if (AI.Config.showAdvancedSettings || ShowAdvanced())
            {
                EditorGUILayout.Space();
                EditorGUI.BeginChangeCheck();
                AI.Config.showAdvancedSettings = EditorGUILayout.BeginFoldoutHeaderGroup(AI.Config.showAdvancedSettings, "Advanced");
                if (EditorGUI.EndChangeCheck()) AI.SaveConfig();

                if (AI.Config.showAdvancedSettings)
                {
                    BeginIndentBlock();

                    EditorGUI.BeginChangeCheck();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Hide Advanced behind CTRL", "Will show only the main features in the UI permanently and hide all the rest until CTRL is held down."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.hideAdvanced = EditorGUILayout.Toggle(AI.Config.hideAdvanced, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Color Closed Tag Filter Field", "Tint the selected Package Tag/File Tag filter field with the tag color. The dropdown list itself stays colored either way."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.colorTagFilterClosedField = EditorGUILayout.Toggle(AI.Config.colorTagFilterClosedField, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Use Affiliate Links", "Will support the further development of the tool by allowing the usage of affiliate links whenever opening Asset Store pages."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.useAffiliateLinks = EditorGUILayout.Toggle(AI.Config.useAffiliateLinks, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Fetch Original Price", "Per default the current, potentially discounted price will be shown. If active, only the non-discounted is considered."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.showOriginalPrice = EditorGUILayout.Toggle(AI.Config.showOriginalPrice, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Propose Save Scene for Previews", "Will prompt to save the current scene if it is untitled/unsaved before creating preview scenes. This can block execution with a modal popup during preview recreation."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.proposeSaveSceneDialog = EditorGUILayout.Toggle(AI.Config.proposeSaveSceneDialog, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Initialize on Domain Reload", "Restores the previous behavior of initializing Asset Inventory after scripts reload even when the main window is closed. This keeps closed-window integrations like Project window previews available, but can slow down domain reloads when the database is remote or unavailable."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.forceInitOnDomainReload = EditorGUILayout.Toggle(AI.Config.forceInitOnDomainReload, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Concurrent Requests to Unity API", "Max number of requests that should be send at the same time to the Unity backend."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.maxConcurrentUnityRequests = EditorGUILayout.DelayedIntField(AI.Config.maxConcurrentUnityRequests, GUILayout.Width(50));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Online Metadata Refresh Cycle", "Number of days after which all metadata from the Asset Store should be refreshed to gather update information, new descriptions etc."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.assetStoreRefreshCycle = EditorGUILayout.DelayedIntField(AI.Config.assetStoreRefreshCycle, GUILayout.Width(50));
                    EditorGUILayout.LabelField("days");
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Preview Image Load Chunk Size", "Number of preview images to load in parallel."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.previewChunkSize = EditorGUILayout.DelayedIntField(AI.Config.previewChunkSize, GUILayout.Width(50));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Package State Refresh Speed", "Number of packages to gather update information for in the background per cycle."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.observationSpeed = EditorGUILayout.DelayedIntField(AI.Config.observationSpeed, GUILayout.Width(50));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Reporting Batch Size", "Amount of GUIDs that will be processed in a single request. Balance between performance and UI responsiveness."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.reportingBatchSize = EditorGUILayout.DelayedIntField(AI.Config.reportingBatchSize, GUILayout.Width(50));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Resolve Multiple Candidates", "When multiple origin candidates are found for a file, automatically pick the latest indexed version as best guess. The version will not be shown in bold to indicate it is an estimate."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.reportingAutoResolve = EditorGUILayout.Toggle(AI.Config.reportingAutoResolve, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Extract Single Audio Files", "Will only extract single audio files for preview and not the full archive. Advantage is less space requirements for caching but each preview will potentially again need to go through the full archive to extract, leading to more waiting time."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.extractSingleFiles = EditorGUILayout.Toggle(AI.Config.extractSingleFiles, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Scan OBJ Material Dependencies", "Will analyze meta files of OBJ model assets for external material dependencies. When enabled, materials referenced by name in model importers will be resolved and tracked as dependencies. Usually does not make sense as it will detect too many dependencies. Experimental."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.scanOBJMaterialDependencies = EditorGUILayout.Toggle(AI.Config.scanOBJMaterialDependencies, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Updates For Indirect Dependencies", "Will show updates for packages even if they are indirect dependencies."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.showIndirectPackageUpdates = EditorGUILayout.Toggle(AI.Config.showIndirectPackageUpdates, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Updates For Custom Packages", "Will show custom packages in the list of available updates even though they cannot be updated automatically."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.showCustomPackageUpdates = EditorGUILayout.Toggle(AI.Config.showCustomPackageUpdates, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Enlarge Grid Tiles", "Will make grid tiles use all the available space and only snap to a different size if the tile size allows it."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.enlargeTiles = EditorGUILayout.Toggle(AI.Config.enlargeTiles, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    EditorGUI.BeginChangeCheck();
                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Font Size", "Font size for grids."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.fontSize = EditorGUILayout.IntSlider(AI.Config.fontSize, 8, 20, GUILayout.Width(200));
                    GUILayout.EndHorizontal();
                    if (EditorGUI.EndChangeCheck())
                    {
                        UIStyles.ResetStyles();
                        AI.SaveConfig();
                        _requireAssetTreeRebuild = true;
                        _requireSearchUpdate = true;
                    }

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Auto-Refresh Purchases", "Will update Asset Store purchases automatically at first start of the tool."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.autoRefreshPurchases = EditorGUILayout.Toggle(AI.Config.autoRefreshPurchases, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    if (AI.Config.autoRefreshPurchases)
                    {
                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CommonUIStyles.Content($"{CommonUIStyles.INDENT}Refresh Period", "Number of hours after which purchases from the Asset Store should be refreshed."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                        AI.Config.purchasesRefreshPeriod = EditorGUILayout.DelayedIntField(AI.Config.purchasesRefreshPeriod, GUILayout.Width(50));
                        EditorGUILayout.LabelField("hours");
                        GUILayout.EndHorizontal();
                    }

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Auto-Refresh Metadata", "Will update the package metadata in the background when selecting a package to ensure the displayed information is up-to-date."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.autoRefreshMetadata = EditorGUILayout.Toggle(AI.Config.autoRefreshMetadata, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    if (AI.Config.autoRefreshMetadata)
                    {
                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CommonUIStyles.Content($"{CommonUIStyles.INDENT}Max Age", "Maximum age in hours after which the metadata is loaded again."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                        AI.Config.metadataTimeout = EditorGUILayout.DelayedIntField(AI.Config.metadataTimeout, GUILayout.Width(50));
                        EditorGUILayout.LabelField("hours");
                        GUILayout.EndHorizontal();
                    }

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Auto-Stop Cache Observer", "Will stop the cache observer after no new events came in for the specified time. This will save around 10% CPU background consumption. The only drawback will be that downloads started from the package manager will not be immediately be picked up by the tool anymore but only upon reselection."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.autoStopObservation = EditorGUILayout.Toggle(AI.Config.autoStopObservation, GUILayout.MaxWidth(cbWidth));
                    EditorGUILayout.LabelField(AI.IsObserverActive() ? "currently active" : "currently inactive", AI.IsObserverActive() ? EditorStyles.miniLabel : CommonUIStyles.greyMiniLabel);
                    GUILayout.EndHorizontal();

                    if (AI.Config.autoStopObservation)
                    {
                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CommonUIStyles.Content($"{CommonUIStyles.INDENT}Timeout", "Time in seconds of no incoming file events after which the observer will be shut down."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                        AI.Config.observationTimeout = EditorGUILayout.DelayedIntField(AI.Config.observationTimeout, GUILayout.Width(50));
                        EditorGUILayout.LabelField("seconds");
                        GUILayout.EndHorizontal();
                    }

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Tag Selection Window Height", "Height of the tag list window when selecting 'Add Tag...'"), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.tagListHeight = EditorGUILayout.DelayedIntField(AI.Config.tagListHeight, GUILayout.Width(50));
                    EditorGUILayout.LabelField("px");
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("No Package Text Below", "Don't show text for packages in grid mode when the tile size is below the value."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.noPackageTileTextBelow = EditorGUILayout.DelayedIntField(AI.Config.noPackageTileTextBelow, GUILayout.Width(50));
                    EditorGUILayout.LabelField("tile size");
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Sync Detail Fetching", "Will fetch asset details synchronously before continuing with other index actions. Ensures new packages are picked up for download on the first run."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.awaitNonBlocking = EditorGUILayout.Toggle(AI.Config.awaitNonBlocking, GUILayout.MaxWidth(cbWidth));
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(CommonUIStyles.Content("Exception Logging", "Will specify which errors should be logged to the console."), EditorStyles.boldLabel, GUILayout.Width(labelWidth));
                    AI.Config.logAreas = EditorGUILayout.MaskField(AI.Config.logAreas, _logOptions, GUILayout.MaxWidth(200));
                    GUILayout.EndHorizontal();

                    if (EditorGUI.EndChangeCheck())
                    {
                        AI.SaveConfig();
                        _requireAssetTreeRebuild = true;
                        if (!AI.Config.autoStopObservation) AI.StartCacheObserver();
                    }
                    EndIndentBlock();
                }
                EditorGUILayout.EndFoldoutHeaderGroup();
            }

            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            EditorGUILayout.Space();

            GUILayout.BeginVertical();
            EditorGUILayout.Space();
            GUILayout.BeginVertical("Update", "window", GUILayout.Width(UIStyles.INSPECTOR_WIDTH), GUILayout.ExpandHeight(false));
            UIBlock("settings.updateintro", () =>
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Ensure to regularly update the index and to fetch the newest updates from the Asset Store.", EditorStyles.wordWrappedLabel);
            });
            EditorGUILayout.Space();

            if (_usageCalculationInProgress)
            {
                EditorGUILayout.LabelField("Usage calculation in progress...", EditorStyles.boldLabel);
                EditorGUILayout.Space();
                EditorGUILayout.LabelField(_usageCalculation.CurrentMain);
            }
            else
            {
                if (AI.Actions.AnyActionsInProgress)
                {
                    EditorGUI.BeginDisabledGroup(AI.Actions.CancellationRequested);
                    if (GUILayout.Button("Stop Actions"))
                    {
                        AI.Actions.CancelAll();
                    }
                    EditorGUI.EndDisabledGroup();

                    // status
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Currently Running", EditorStyles.largeLabel);

                    List<UpdateAction> actions = AI.Actions.GetRunningActions();
                    foreach (UpdateAction action in actions)
                    {
                        foreach (ActionProgress progress in action.progress)
                        {
                            if (!progress.IsRunning()) continue;

                            EditorGUILayout.Space();

                            EditorGUILayout.LabelField(action.name, EditorStyles.boldLabel);
                            if (progress == null) continue;

                            CommonUIStyles.DrawProgressBar(progress.MainProgress / (float)progress.MainCount, $"{progress.MainProgress:N0}/{progress.MainCount:N0} - {progress.CurrentMain}");

                            if (!string.IsNullOrWhiteSpace(progress.CurrentSub))
                            {
                                CommonUIStyles.DrawProgressBar(progress.SubProgress / (float)progress.SubCount, $"{progress.SubProgress:N0}/{progress.SubCount:N0} - {progress.CurrentSub}");
                            }
                        }
                    }

                    EditorGUILayout.Space();
                    UIBlock("settings.hints.indexinterruption", () =>
                    {
                        EditorGUILayout.LabelField("Indexing can be interrupted and resumed at any time. Many warnings and errors can appear in the console during the process which is usually normal.", CommonUIStyles.centeredGreyWrappedMiniLabel);
                    });
                }
                else
                {
                    if (GUILayout.Button(CommonUIStyles.Content("Run Actions", "Run all enabled actions in one go and perform all necessary updates."), CommonUIStyles.mainButton, GUILayout.Height(CommonUIStyles.BIG_BUTTON_HEIGHT)))
                    {
                        PerformFullUpdate();
                    }
                    if (AI.Actions.LastActionUpdate != DateTime.MinValue)
                    {
                        UIBlock("settings.lastupdate", () =>
                        {
                            EditorGUILayout.Space();
                            EditorGUILayout.LabelField($"Last updated {StringUtils.GetRelativeTimeDifference(AI.Actions.LastActionUpdate)}", EditorStyles.centeredGreyMiniLabel);
                        });
                    }
                }
            }
            GUILayout.EndVertical();

            EditorGUILayout.Space();
            GUILayout.BeginVertical("Statistics", "window", GUILayout.Width(UIStyles.INSPECTOR_WIDTH));
            EditorGUILayout.Space();
            int labelWidth2 = 130;
            _statsScrollPos = GUILayout.BeginScrollView(_statsScrollPos, false, false);
            UIBlock("settings.statistics", () =>
            {
                DrawPackageStats(false);
                GUILabelWithText("Database Size", EditorUtility.FormatBytes(_dbSize), labelWidth2);
            });

            if (_stats != null && _stats.IndexedPackages < _stats.IndexablePackages && !AI.Actions.AnyActionsInProgress) // && !AI.Config.downloadAssets)
            {
                UIBlock("settings.hints.indexremaining", () =>
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.HelpBox("To index the remaining assets, download them first. You can multi-select packages in the Packages view to start a bulk download.", MessageType.Info);
                });
            }

            UIBlock("settings.diskspace", () =>
            {
                EditorGUILayout.Space();
                _showDiskSpace = EditorGUILayout.Foldout(_showDiskSpace, "Used Disk Space", true);
                if (_showDiskSpace)
                {
                    if (_lastFolderSizeCalculation != DateTime.MinValue)
                    {
                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CommonUIStyles.Content("Previews", "Size of folder containing asset preview images."), EditorStyles.boldLabel, GUILayout.Width(120));
                        EditorGUILayout.LabelField(EditorUtility.FormatBytes(_previewSize), GUILayout.Width(80));
                        GUILayout.EndHorizontal();

                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CommonUIStyles.Content("Cache", "Size of folder containing temporary cache. Can be deleted at any time."), EditorStyles.boldLabel, GUILayout.Width(120));
                        EditorGUILayout.LabelField(EditorUtility.FormatBytes(_cacheSize), GUILayout.Width(80));
                        GUILayout.EndHorizontal();

                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CommonUIStyles.Content("Persistent Cache", "Size of extracted packages in cache that are marked 'extracted' and not automatically removed."), EditorStyles.boldLabel, GUILayout.Width(120));
                        EditorGUILayout.LabelField(EditorUtility.FormatBytes(_persistedCacheSize), GUILayout.Width(80));
                        GUILayout.EndHorizontal();

                        GUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(CommonUIStyles.Content("Backups", "Size of folder containing asset preview images."), EditorStyles.boldLabel, GUILayout.Width(120));
                        EditorGUILayout.LabelField(EditorUtility.FormatBytes(_backupSize), GUILayout.Width(80));
                        GUILayout.EndHorizontal();

                        EditorGUILayout.LabelField("last updated " + _lastFolderSizeCalculation.ToShortTimeString(), EditorStyles.centeredGreyMiniLabel);
                    }
                    else
                    {
                        EditorGUILayout.LabelField("Not calculated yet....", EditorStyles.centeredGreyMiniLabel);
                    }
                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    EditorGUI.BeginDisabledGroup(_calculatingFolderSizes);
                    if (GUILayout.Button(_calculatingFolderSizes ? "Calculating..." : "Refresh", EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                    {
                        CalcFolderSizes();
                    }
                    EditorGUI.EndDisabledGroup();
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();
                }
            });

            EditorGUILayout.Space();
            _showMaintenance = EditorGUILayout.Foldout(_showMaintenance, "Maintenance", true);
            if (_showMaintenance)
            {
                EditorGUI.BeginDisabledGroup(AI.Actions.AnyActionsInProgress);
                UIBlock("settings.actions.maintenance", () =>
                {
                    if (GUILayout.Button("Maintenance Wizard..."))
                    {
                        MaintenanceUI.ShowWindow();
                    }
                });
                UIBlock("settings.actions.recreatepreviews", () =>
                {
                    if (GUILayout.Button("Previews Wizard..."))
                    {
                        PreviewWizardUI previewsUI = PreviewWizardUI.ShowWindow();
                        previewsUI.Init(null, _assets);
                    }
                });
                UIBlock("settings.actions.clearcache", () =>
                {
                    EditorGUILayout.Space();
                    EditorGUI.BeginDisabledGroup(Paths.ClearCacheInProgress);
                    if (GUILayout.Button(CommonUIStyles.Content("Clear Cache", "Will delete the 'Extracted' folder used for speeding up asset access. It will be recreated automatically when needed.")))
                    {
                        Paths.ClearCache(() => UpdateStatistics(true));
                    }
                    EditorGUI.EndDisabledGroup();
                });
                UIBlock("settings.actions.cleardb", () =>
                {
                    if (GUILayout.Button(CommonUIStyles.Content("Clear Database", "Will reset the database to its initial empty state. ALL data in the index will be lost.")))
                    {
                        if (EditorUtility.DisplayDialog("Confirm", "This will reset the database to its initial empty state. ALL data in the index will be lost.", "Proceed", "Cancel"))
                        {
                            if (DBAdapter.DeleteDB())
                            {
                                AssetUtils.ClearCache();

                                // delete previews and extracted cache since they will be incompatible due to different Ids
                                _ = IOUtils.DeleteFileOrDirectory(Paths.GetPreviewFolder());
                                _ = IOUtils.DeleteFileOrDirectory(Paths.GetMaterializeFolder());
                            }
                            else
                            {
                                EditorUtility.DisplayDialog("Error", "Database seems to be in use by another program and could not be cleared.", "OK");
                            }
                            UpdateStatistics(true);
                            _assets = new List<AssetInfo>();
                            _requireAssetTreeRebuild = true;
                        }
                    }
                });

                GUILayout.BeginHorizontal();
                UIBlock("settings.actions.resetconfig", () =>
                {
                    if (GUILayout.Button(CommonUIStyles.Content("Reset Configuration", "Will reset the configuration to default values, also deleting all Additional Folder configurations.")))
                    {
                        AI.ResetConfig();
                    }
                });
                UIBlock("settings.actions.resetuiconfig", () =>
                {
                    if (GUILayout.Button(CommonUIStyles.Content("Reset UI Customization", "Will reset the visibility of UI elements to initial default values.")))
                    {
                        AI.ResetUICustomization();
                    }
                });
                GUILayout.EndHorizontal();
                EditorGUILayout.Space();

                EditorGUI.BeginDisabledGroup(_cleanupInProgress);
                UIBlock("settings.actions.optimizedb", () =>
                {
                    if (GUILayout.Button("Optimize Database")) OptimizeDatabase();
                });
                EditorGUI.EndDisabledGroup();
                if (DBAdapter.IsDBOpen())
                {
                    UIBlock("settings.actions.closedb", () =>
                    {
                        if (GUILayout.Button(CommonUIStyles.Content("Close Database", "Will allow to safely copy the database in the file system. Database will be reopened automatically upon activity.")))
                        {
                            DBAdapter.Close();
                        }
                    });
                }

                EditorGUI.EndDisabledGroup();
            }

            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        private static string GetTestImageFolder(string folderName = "Test")
        {
            string[] inventoryGuids = AssetDatabase.FindAssets("AssetInventory t:Folder");
            foreach (string guid in inventoryGuids)
            {
                string invPath = AssetDatabase.GUIDToAssetPath(guid);
                string testFolder = $"{invPath}/Editor/Images/{folderName}";
                if (AssetDatabase.IsValidFolder(testFolder))
                {
                    string projectRoot = Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length);
                    return Path.Combine(projectRoot, testFolder).Replace("\\", "/");
                }
            }
            return null;
        }

        private void DeleteOllamaModel()
        {
            if (!EditorUtility.DisplayDialog("Confirm Delete", $"Are you sure you want to delete the Ollama model '{AI.Config.ollamaModel}'?", "Delete", "Cancel"))
            {
                return;
            }
            _ = Intelligence.DeleteOllamaModelAsync(AI.Config.ollamaModel);
        }

        private void DownloadOllamaModel()
        {
            _curOllamaProgress = 0;
            Task.Run(() => Intelligence.PullOllamaModelAsync(AI.Config.ollamaModel, status =>
            {
                _curOllamaProgress = status.Completed;
                _maxOllamaProgress = status.Total;
            }));
        }

        private void ShowInstalledOllamaModels()
        {
            IEnumerable<ModelInfo> models = Intelligence.OllamaModels;

            GenericMenu menu = new GenericMenu();
            if (models != null)
            {
                foreach (ModelInfo model in models.OrderBy(m => m.Name, StringComparer.InvariantCultureIgnoreCase))
                {
                    menu.AddItem(new GUIContent($"{model.Name} ({EditorUtility.FormatBytes(model.Size)}, {model.ParameterSize})"), false, () =>
                    {
                        AI.Config.ollamaModel = model.Name.Split(' ')[0];
                    });
                }
                menu.AddItem(GUIContent.none, false, () => {});
                menu.AddItem(new GUIContent("Refresh"), false, Intelligence.RefreshOllama);
            }
            else
            {
                if (Intelligence.LoadingModels)
                {
                    menu.AddDisabledItem(new GUIContent("Loading models..."));
                }
                else
                {
                    menu.AddDisabledItem(new GUIContent("Models could not be loaded"));
                }
            }
            menu.ShowAsContext();
        }

        private void ShowSuggestedOllamaModels()
        {
            GenericMenu menu = new GenericMenu();
            foreach (string model in Intelligence.SuggestedOllamaModels)
            {
                menu.AddItem(new GUIContent(model), false, () =>
                {
                    AI.Config.ollamaModel = model.Split(' ')[0];
                });
            }
            menu.ShowAsContext();
        }

        private void ShowInstalledLMStudioModels()
        {
            IEnumerable<LMStudioModel> models = Intelligence.LMStudioModels;

            GenericMenu menu = new GenericMenu();
            if (models != null)
            {
                // Filter to only show vision-enabled models (VLM type)
                IEnumerable<LMStudioModel> visionModels = models.Where(m =>
                    !string.IsNullOrEmpty(m.type) &&
                    (m.type.ToLowerInvariant() == "vlm" || m.type.ToLowerInvariant().Contains("vision")));

                if (visionModels.Any())
                {
                    foreach (LMStudioModel model in visionModels.OrderBy(m => m.id, StringComparer.InvariantCultureIgnoreCase))
                    {
                        string stateText = !string.IsNullOrEmpty(model.state) ? $" ({model.state})" : "";
                        string typeText = !string.IsNullOrEmpty(model.type) ? $" [{model.type}]" : "";
                        menu.AddItem(new GUIContent($"{model.id}{typeText}{stateText}"), false, () =>
                        {
                            AI.Config.lmStudioModel = model.id;
                        });
                    }
                    menu.AddItem(GUIContent.none, false, () => {});
                    menu.AddItem(new GUIContent("Refresh"), false, Intelligence.RefreshLMStudio);
                }
                else
                {
                    menu.AddDisabledItem(new GUIContent("No vision models found"));
                    menu.AddItem(new GUIContent("Refresh"), false, Intelligence.RefreshLMStudio);
                }
            }
            else
            {
                if (Intelligence.LoadingLMStudioModels)
                {
                    menu.AddDisabledItem(new GUIContent("Loading models..."));
                }
                else
                {
                    menu.AddDisabledItem(new GUIContent("No models found"));
                    menu.AddItem(new GUIContent("Refresh"), false, Intelligence.RefreshLMStudio);
                }
            }
            menu.ShowAsContext();
        }

        private void OnModelTesterPromptChanged(string prompt)
        {
            AI.Config.aiCustomPrompt = string.IsNullOrEmpty(prompt) ? null : prompt;
            AI.SaveConfig();
        }

        private async void TestCaptioning()
        {
            _captionTestRunning = true;
            _captionTest = "Running...";
            string path = AssetDatabase.GUIDToAssetPath(AssetDatabase.FindAssets("t:Texture2D AssetInventory").FirstOrDefault());
            string absolutePath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
            string modelName = null;
            if (AI.Config.aiBackend == 1)
            {
                modelName = AI.Config.ollamaModel;
            }
            else if (AI.Config.aiBackend == 2)
            {
                modelName = AI.Config.lmStudioModel;
            }
            List<CaptionResult> captionResult = await CaptionCreator.CaptionImage(new List<string> {absolutePath}, modelName, AI.Actions.CancellationToken);
            _captionTest = captionResult?.FirstOrDefault()?.caption;
            if (string.IsNullOrWhiteSpace(_captionTest))
            {
                _captionTest = "-Failed to create caption. Check tooling.-";
            }
            else
            {
                _captionTest = $"\"{_captionTest}\"";
            }
            _captionTestRunning = false;
        }

        private void OptimizeDatabase(bool initOnly = false)
        {
            if (!initOnly)
            {
                long savings = DBAdapter.Optimize();
                UpdateStatistics(true);
                EditorUtility.DisplayDialog("Success", $"Database was optimized. Size reduction: {EditorUtility.FormatBytes(savings)}\n\nMake sure to also delete your Library folder every now and then, especially after long indexing runs, to ensure Unity's asset database only contains what you really need for maximum performance.", "OK");
            }

            AppProperty lastOpt = new AppProperty("LastOptimization", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            DBAdapter.DB.InsertOrReplace(lastOpt);
        }

        private void SelectRelativeFolderMapping(RelativeLocation location)
        {
            string folder = EditorUtility.OpenFolderPanel("Select folder to map to", location.Location, "");
            if (!string.IsNullOrEmpty(folder))
            {
                location.SetLocation(Path.GetFullPath(folder));
                if (location.Id > 0)
                {
                    DBAdapter.DB.Execute("UPDATE RelativeLocation SET Location = ? WHERE Id = ?", location.Location, location.Id);
                }
                else
                {
                    DBAdapter.DB.Insert(location);
                }
                Paths.LoadRelativeLocations();
            }
        }

        private async void CalcFolderSizes()
        {
            if (_calculatingFolderSizes) return;
            _calculatingFolderSizes = true;
            _lastFolderSizeCalculation = DateTime.Now;

            _backupSize = await Paths.GetBackupFolderSize();
            _cacheSize = await Paths.GetCacheFolderSize();
            _persistedCacheSize = await Paths.GetPersistedCacheSize();
            _previewSize = await Paths.GetPreviewFolderSize();

            _calculatingFolderSizes = false;
        }

        private void PerformFullUpdate()
        {
            AI.Actions.RunActions();
        }

        private void SetDatabaseLocation()
        {
            string targetFolder = EditorUtility.OpenFolderPanel("Select folder for database and cache", Paths.GetStorageFolder(), "");
            if (string.IsNullOrEmpty(targetFolder)) return;

            // check if same folder selected
            if (IOUtils.IsSameDirectory(targetFolder, Paths.GetStorageFolder())) return;

            // disallow selecting a drive/root directory (e.g., C:\, D:\, E:, or /)
            if (IOUtils.IsRootPath(targetFolder))
            {
                EditorUtility.DisplayDialog("Invalid Folder", "Please select a subfolder, not a drive root.", "OK");
                return;
            }

            // check for existing database
            if (File.Exists(Path.Combine(targetFolder, DBAdapter.DB_NAME)))
            {
                if (EditorUtility.DisplayDialog("Use Existing?", "The target folder contains a database. Switch to this one? Otherwise please select an empty directory.", "Switch", "Cancel"))
                {
                    AI.SwitchDatabase(targetFolder);
                    ReloadLookups();
                    PerformSearch();
                }

                return;
            }

            if (EditorUtility.DisplayDialog("Keep Old Database", "Should a new database be created or the current one moved?", "New", "Move..."))
            {
                AI.SwitchDatabase(targetFolder);
                ReloadLookups();
                PerformSearch();
                AssetStore.GatherAllMetadata();
                AssetStore.GatherProjectMetadata();
                return;
            }

            // show dedicated UI since the process is more complex now
            DBLocationUI relocateUI = DBLocationUI.ShowWindow();
            relocateUI.Init(targetFolder);
        }

        private IEnumerator UpdateStatisticsDelayed()
        {
            yield return null;
            UpdateStatistics(false);
        }

        private void UpdateStatistics(bool force)
        {
            if (!force && _assets != null && _tags != null && _dbSize > 0)
            {
                // check if assets were already correctly initialized since this method is also used for initial bootstrapping
                if (_assets.Any(a => a.PackageDownloader == null || (a.ParentId > 0 && a.ParentInfo == null)))
                {
                    Assets.InitAssets(_assets);
                }
                return;
            }

            if (AI.DEBUG_MODE) Debug.LogWarning("Update Statistics");
            if (Application.isPlaying) return;

            _assets = Assets.Load();
            _tags = Tagging.LoadTags();

            _stats = Assets.GetInventoryStats();

            if (AI.Config.tab == 3)
            {
                _dbSize = DBAdapter.GetDBSize();
            }
        }
    }
}
