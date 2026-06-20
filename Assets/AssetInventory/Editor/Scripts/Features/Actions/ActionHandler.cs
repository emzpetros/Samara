using Automator;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImpossibleRobert.Common;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEngine;
using static AssetInventory.UpdateAction;

namespace AssetInventory
{
    /// <summary>
    /// Configuration for ActionRunner that uses AssetInventory's logging settings.
    /// </summary>
    public class AssetInventoryActionRunnerConfig : IActionRunnerConfig
    {
        public bool LogActions => AI.Config?.LogCustomActions ?? true;
    }

    public class ActionHandler
    {
        public event Action OnActionsDone;
        public event Action OnActionsInitialized;

        public const string ACTION_DEFAULT_NAME = "-Default-";
        public const string ACTION_ASSET_STORE_PURCHASES = "AssetStorePurchases";
        public const string ACTION_ASSET_STORE_DETAILS = "AssetStoreDetails";
        public const string ACTION_ASSET_STORE_CACHE_SCAN = "AssetStoreCacheScan";
        public const string ACTION_ASSET_STORE_CACHE_INDEX = "AssetStoreCacheIndex";
        public const string ACTION_SUB_PACKAGES_INDEX = "IndexSubPackages";
        public const string ACTION_SUB_ARCHIVES_INDEX = "IndexSubArchives";
        public const string ACTION_PACKAGE_CACHE_SCAN = "RegistryPackageCacheScan";
        public const string ACTION_PACKAGE_CACHE_INDEX = "RegistryPackageCacheIndex";
        public const string ACTION_FOLDERS_INDEX = "FoldersIndex";
        public const string ACTION_MEDIA_FOLDERS_INDEX = "MediaFoldersIndex";
        public const string ACTION_ARCHIVE_FOLDERS_INDEX = "ArchiveFoldersIndex";
        public const string ACTION_PACKAGE_FOLDERS_INDEX = "PackageFoldersIndex";
        public const string ACTION_DEVPACKAGE_FOLDERS_INDEX = "DevPackageFoldersIndex";
        public const string ACTION_ASSET_MANAGER_INDEX = "AssetManagerIndex";
        public const string ACTION_ASSET_MANAGER_COLLECTION_INDEX = "AssetManagerCollectionIndex";
        public const string ACTION_ASSET_STORE_DOWNLOADS = "AssetStoreDownloads";
        public const string ACTION_COLOR_INDEX = "ColorIndexer";
        public const string ACTION_BACKUP = "Backup";
        public const string ACTION_AI_CAPTIONS = "AICaptions";
        public const string ACTION_FIND_FREE = "ClaimFreeAssets";
        public const string ACTION_PREVIEWS_RECREATE = "RecreatePreviews";
        public const string ACTION_PREVIEWS_RESTORE = "RestorePreviews";
        public const string ACTION_USER = "UserAction-";

        public const string AI_ACTION_LOCK = AI.DEFINE_SYMBOL + "_ACTION_LOCK";

        private static string AI_ACTION_SETUP_DONE => AI.DEFINE_SYMBOL + "_SETUP_DONE_" + Application.dataPath.GetHashCode().ToString("X8");
        internal static bool IsProjectSetupDone => EditorPrefs.GetBool(AI_ACTION_SETUP_DONE);

        // These constants match ActionRunner's PREF_PREFIX for proper interrupt detection
        private const string AUTOMATOR_ACTION_ACTIVE = "Automator_ActionActive_";
        private const string AUTOMATOR_CURRENT_STEP = "Automator_CurrentStep_";

        // global cancellation request
        private bool _cancellationRequested;
        private CancellationTokenSource _cts;

        public bool CancellationRequested
        {
            get => _cancellationRequested;
            set
            {
                _cancellationRequested = value;
                if (value) _cts?.Cancel();
                else
                {
                    if (_cts == null || _cts.IsCancellationRequested)
                    {
                        _cts?.Dispose();
                        _cts = new CancellationTokenSource();
                    }
                }
            }
        }

        /// <summary>
        /// Cancellation token tied to the global Stop button. Pass to async operations
        /// (HTTP calls, AI requests, etc.) so they can be interrupted immediately
        /// instead of waiting for the next polling check of <see cref="CancellationRequested"/>.
        /// </summary>
        public CancellationToken CancellationToken
        {
            get
            {
                if (_cts == null) _cts = new CancellationTokenSource();
                return _cts.Token;
            }
        }

        public bool ActionsInProgress
        {
            get
            {
                foreach (UpdateAction a in Actions)
                {
                    if ((a.IsRunning() || a.scheduled) && !a.nonBlocking) return true;
                }
                return false;
            }
        }
        public bool AnyActionsInProgress
        {
            get
            {
                foreach (UpdateAction a in Actions)
                {
                    if (a.IsRunning()) return true;
                }
                return false;
            }
        }
        public List<UpdateAction> Actions = new List<UpdateAction>();
        public List<CustomAction> UserActions = new List<CustomAction>();
        public List<ActionStep> ActionSteps = new List<ActionStep>();

        internal DateTime LastActionUpdate { get; private set; }

        private int _curState;
        private bool _initDone;

        public void Init(bool force = false)
        {
            if (_initDone && !force) return;
            _initDone = true;

            // Register Asset Inventory's config as a variable group for the VariableResolver
            // This allows $Config.xxx variables to resolve properties from AI.Config
            VariableGroupRegistry.Register("Config", () => AI.Config);

            // Configure ActionRunner to use AssetInventory context and logging
            ActionRunner.SetContextFactory(() => new AssetInventoryActionContext());
            ActionRunner.SetConfig(new AssetInventoryActionRunnerConfig());

            Actions = new List<UpdateAction>();
            EnsureStatesInitialized();

            Actions.Add(new UpdateAction {key = ACTION_ASSET_STORE_PURCHASES, name = "Fetch Asset Store Purchases", description = "Refreshes purchases from Unity Asset Store and adds these as packages (without indexing the content yet).", phase = Phase.Pre, nonBlocking = true});
            Actions.Add(new UpdateAction {key = ACTION_ASSET_STORE_DETAILS, name = "Fetch Asset Store Details", description = "Downloads metadata for packages in the index like publisher and pricing information as well as screenshots.", phase = Phase.Pre, supportsForce = true, allowParallel = true, nonBlocking = true});

            Actions.Add(new UpdateAction {key = ACTION_ASSET_STORE_CACHE_SCAN, name = "Scan Asset Store Cache", description = "Add found or changed packages to package catalog and queue without indexing the contents yet.", phase = Phase.Index, supportsForce = true, nonBlocking = true});
            Actions.Add(new UpdateAction {key = ACTION_ASSET_STORE_CACHE_INDEX, name = "Index Store Assets", description = "The main source for the asset index. Will scan the Unity Asset Store cache of already downloaded items and index these.", phase = Phase.Index});
            Actions.Add(new UpdateAction {key = ACTION_ASSET_STORE_DOWNLOADS, name = "Download & Index New Asset Store Packages", description = "Download uncached items from the Asset Store for indexing. Will delete them again afterwards if not selected otherwise below. Attention: downloading an item will revoke the right to easily return it through the Asset Store.", phase = Phase.Index});
            Actions.Add(new UpdateAction {key = ACTION_PACKAGE_CACHE_SCAN, name = "Scan Registry Package Cache", description = "Add found or changed registry packages to package catalog and queue without indexing the contents yet.", phase = Phase.Index, nonBlocking = true});
            Actions.Add(new UpdateAction {key = ACTION_PACKAGE_CACHE_INDEX, name = "Index Registry Package Cache", description = "Will index registry packages like from the Unity registry or custom registries and Github.", phase = Phase.Index});
            Actions.Add(new UpdateAction {key = ACTION_FOLDERS_INDEX, name = "Index Additional Folders", description = "Will scan all folders listed under additional folders for packages, media files and more to add to the index. Put all your texture and audio libraries as well as humble bundle, Synty and other assets there.", phase = Phase.Index});
            Actions.Add(new UpdateAction {key = ACTION_ASSET_MANAGER_INDEX, name = "Index Unity Asset Manager", description = "Activate if you have assets stored in Unity Asset Manager in the cloud to make them searchable as well.", phase = Phase.Index});

            Actions.Add(new UpdateAction {key = ACTION_COLOR_INDEX, name = "Extract Colors", description = "Will make assets searchable by color. Relies on existing preview images.", phase = Phase.Post, nonBlocking = true});
            Actions.Add(new UpdateAction {key = ACTION_BACKUP, name = "Create Backups", description = "Store downloaded assets in a separate folder", phase = Phase.Post, nonBlocking = true});
            Actions.Add(new UpdateAction {key = ACTION_AI_CAPTIONS, name = "Create AI Captions", description = "Will use AI to create an automatic caption of what is visible in each individual asset file using the existing preview images. Once indexed this will yield potentially much better search results.", phase = Phase.Post, nonBlocking = true});

            // custom actions created by user, must be created before hidden actions
            InitUserActions();

            // hidden actions, triggered manually or from other actions
            Actions.Add(new UpdateAction {key = ACTION_SUB_PACKAGES_INDEX, name = "Index Sub-Packages", phase = Phase.Index, hidden = true});
            Actions.Add(new UpdateAction {key = ACTION_SUB_ARCHIVES_INDEX, name = "Index Sub-Archives", phase = Phase.Index, hidden = true});
            Actions.Add(new UpdateAction {key = ACTION_PREVIEWS_RECREATE, name = "Recreate Previews", phase = Phase.Independent, hidden = true, nonBlocking = true});
            Actions.Add(new UpdateAction {key = ACTION_PREVIEWS_RESTORE, name = "Restore Previews", phase = Phase.Independent, hidden = true, nonBlocking = true});
            Actions.Add(new UpdateAction {key = ACTION_ASSET_MANAGER_COLLECTION_INDEX, name = "Index Unity Asset Manager Collections", phase = Phase.Index, hidden = true});
            Actions.Add(new UpdateAction {key = ACTION_PACKAGE_FOLDERS_INDEX, name = "Index Asset Packages in Folders", phase = Phase.Index, hidden = true});
            Actions.Add(new UpdateAction {key = ACTION_DEVPACKAGE_FOLDERS_INDEX, name = "Index Dev Package in Folders", phase = Phase.Index, hidden = true});
            Actions.Add(new UpdateAction {key = ACTION_ARCHIVE_FOLDERS_INDEX, name = "Index Archives in Folders", phase = Phase.Index, hidden = true});
            Actions.Add(new UpdateAction {key = ACTION_MEDIA_FOLDERS_INDEX, name = "Index Media in Folders", phase = Phase.Index, hidden = true});
            Actions.Add(new UpdateAction {key = ACTION_FIND_FREE, name = "Find Free Bundled Assets from already Purchased Assets", description = "Some asset authors, especially when purchasing bundles, grant free or cheaper access to related assets. This is not immediately visible in the store and assets can remain unclaimed. Run this action to get a list of candidates to check for free or reduced prices. Running forced will open all results in browser tabs.", phase = Phase.Independent, supportsForce = true, nonBlocking = true, hidden = true});

            AppProperty lastIndexUpdate = DBAdapter.DB.Find<AppProperty>("LastIndexUpdate");
            LastActionUpdate = lastIndexUpdate != null ? DateTime.Parse(lastIndexUpdate.Value, DateTimeFormatInfo.InvariantInfo) : DateTime.MinValue;

            OnActionsInitialized?.Invoke();
        }

        private void InitUserActions()
        {
            UserActions = DBAdapter.DB.Query<CustomAction>("select * from CustomAction order by Name");
            foreach (CustomAction action in UserActions)
            {
                Actions.Add(new UpdateAction {key = ACTION_USER + action.Id, name = action.Name, description = action.Description, phase = Phase.Independent});
            }

            // Use Automator's ActionStepRegistry which discovers steps via reflection
            ActionSteps = ActionStepRegistry.Steps;

            CheckForInterruptedCustomActions();
            CheckForFirstStart();
        }

        private void CheckForFirstStart()
        {
            if (!IsProjectSetupDone)
            {
                EditorPrefs.SetBool(AI_ACTION_SETUP_DONE, true);

                List<CustomAction> startup = UserActions
                    .Where(a => a.RunMode == CustomAction.Mode.AtInstallation)
                    .ToList();
                if (startup.Count > 0)
                {
                    if (EditorUtility.DisplayDialog("Project Setup",
                            "Asset Inventory was just installed into the project and is about to run the following custom actions which were marked to be run at installation:\n\n"
                            + string.Join("\n\n", startup.Select(a => "*" + a.Name + "*\n" + a.Description)),
                            "Run", "Skip"))
                    {
                        foreach (CustomAction action in startup)
                        {
                            _ = RunUserAction(action);
                        }
                    }
                }
            }
        }

        private async void CheckForInterruptedCustomActions()
        {
            foreach (CustomAction action in UserActions)
            {
                if (EditorPrefs.GetBool(AUTOMATOR_ACTION_ACTIVE + action.Id, false))
                {
                    if (AI.Config.LogCustomActions) Debug.Log($"Found interrupted custom action '{action.Name}'. Waiting for lock removal...");
                    while (EditorPrefs.GetBool(AI_ACTION_LOCK, false))
                    {
                        await Task.Delay(500);
                    }
                    if (AI.Config.LogCustomActions) Debug.Log($"... Done. Resuming custom action '{action.Name}'");

                    _ = RunUserAction(action);

                    return; // Only resume one action at a time to avoid potential conflicts
                }
            }
        }

        private void SetDefaultStates(int idx)
        {
            List<UpdateActionState> actionStates = new List<UpdateActionState>();
            actionStates.Add(new UpdateActionState {key = ACTION_ASSET_STORE_PURCHASES, enabled = true});
            actionStates.Add(new UpdateActionState {key = ACTION_ASSET_STORE_DETAILS, enabled = true});
            actionStates.Add(new UpdateActionState {key = ACTION_ASSET_STORE_CACHE_SCAN, enabled = true});
            actionStates.Add(new UpdateActionState {key = ACTION_ASSET_STORE_CACHE_INDEX, enabled = true});
            actionStates.Add(new UpdateActionState {key = ACTION_ASSET_STORE_DOWNLOADS, enabled = true});
            actionStates.Add(new UpdateActionState {key = ACTION_PACKAGE_CACHE_SCAN, enabled = true});
            actionStates.Add(new UpdateActionState {key = ACTION_MEDIA_FOLDERS_INDEX, enabled = true});
            actionStates.Add(new UpdateActionState {key = ACTION_COLOR_INDEX, enabled = true});
            actionStates.Add(new UpdateActionState {key = ACTION_FOLDERS_INDEX, enabled = true});

            if (AI.Config.actionStates.Count == 0)
            {
                AI.Config.actionStates.Add(new UpdateActionStates {name = ACTION_DEFAULT_NAME, actions = actionStates});
            }
            else
            {
                AI.Config.actionStates[idx] = new UpdateActionStates {name = ACTION_DEFAULT_NAME, actions = actionStates};
            }
        }

        public List<UpdateAction> GetRunningActions()
        {
            return Actions.Where(a => a.progress != null && a.progress.Any(p => p.IsRunning()))
                .OrderBy(p => p.progress.Last().StartedAt)
                .ToList();
        }

        public DateTime GetFirstActionStart()
        {
            return GetRunningActions().Select(x => x.progress.First().StartedAt).Min();
        }

        public async Task RunAction(string action, bool force = false)
        {
            UpdateAction updateAction = Actions.FirstOrDefault(a => a.key == action);
            if (updateAction == null)
            {
                Debug.LogError($"Action '{action}' not found");
                return;
            }
            await RunAction(updateAction, force);
        }

        public async Task RunAction(UpdateAction action, bool force = false)
        {
            await RunActions(new List<UpdateAction> {action}, force, awaitNonBlocking: AI.Config.awaitNonBlocking);
        }

        public bool RegisterRunningAction(string action, ActionProgress runner, string caption = null)
        {
            UpdateAction updateAction = Actions.FirstOrDefault(a => a.key == action);
            if (updateAction == null)
            {
                Debug.LogError($"Action '{action}' not found");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(caption)) runner.WithProgress(caption);
            updateAction.progress.Add(runner);
            updateAction.MarkStarted();

            return true;
        }

        /// <summary>
        /// Helper to run an async operation with progress tracking via RegisterRunningAction, returning a result.
        /// Ensures FinishProgress is always called.
        /// </summary>
        public async Task<T> RunWithProgress<T>(string actionKey, Func<ActionProgress> factory, string caption, Func<ActionProgress, Task<T>> operation)
        {
            ActionProgress progress = factory();
            try
            {
                RegisterRunningAction(actionKey, progress, caption);
                return await operation(progress);
            }
            finally
            {
                progress.FinishProgress();
            }
        }

        /// <summary>
        /// Helper to run an async operation with progress tracking via RegisterRunningAction, without a result.
        /// Ensures FinishProgress is always called.
        /// </summary>
        public async Task RunWithProgress(string actionKey, Func<ActionProgress> factory, string caption, Func<ActionProgress, Task> operation)
        {
            ActionProgress progress = factory();
            try
            {
                RegisterRunningAction(actionKey, progress, caption);
                await operation(progress);
            }
            finally
            {
                progress.FinishProgress();
            }
        }

        /// <summary>
        /// Generic wrapper that constructs TProgress and passes it to the operation, returning TResult.
        /// </summary>
        public Task<TResult> RunWithProgress<TProgress, TResult>(string actionKey, string caption, Func<TProgress, Task<TResult>> operation)
            where TProgress : ActionProgress, new()
        {
            return RunWithProgress<TResult>(actionKey, () => new TProgress(), caption, p => operation((TProgress)p));
        }

        /// <summary>
        /// Generic wrapper that constructs TProgress and passes it to the operation, no result.
        /// </summary>
        public Task RunWithProgress<TProgress>(string actionKey, string caption, Func<TProgress, Task> operation)
            where TProgress : ActionProgress, new()
        {
            return RunWithProgress(actionKey, () => new TProgress(), caption, p => operation((TProgress)p));
        }

        public async void RunActions(bool force = false)
        {
            if (!AI.Config.quickIndexingDone)
            {
                // no downloads on very first run to show quick results
                await RunActions(Actions
                    .Where(a => IsActive(a) && a.key != ACTION_ASSET_STORE_DOWNLOADS)
                    .ToList(), force);
            }
            else
            {
                await RunActions(Actions.Where(IsActive).ToList(), force);
            }
        }

        public async Task RunActions(List<UpdateAction> actions, bool force = false, bool awaitNonBlocking = false)
        {
            CancellationRequested = false;

            AI.Init();

            // refresh registry packages in parallel
            AssetStore.GatherAllMetadata();

            actions.ForEach(a => a.scheduled = true);

            foreach (UpdateAction action in actions)
            {
                if (CancellationRequested) break;
                if (action.hidden) continue; // hidden actions must be started individually
                if (action.IsRunning()) continue;
                action.MarkStarted();

                switch (action.key)
                {
                    case ACTION_ASSET_STORE_PURCHASES:
                    {
                        AssetPurchases result = await RunWithProgress<AssetStoreImporter, AssetPurchases>(
                            ACTION_ASSET_STORE_PURCHASES,
                            "Updating purchases",
                            imp => imp.FetchOnlineAssets());
                        if (result != null) AI.TriggerPackageRefresh();
                        break;
                    }

                    case ACTION_ASSET_STORE_DETAILS:
                        if (awaitNonBlocking)
                        {
                            await FetchAssetDetails(force);
                        }
                        else
                        {
                            _ = FetchAssetDetails(force);
                        }
                        break;

                    case ACTION_ASSET_STORE_CACHE_SCAN:
                        // special handling for normal asset store assets since directory structure yields additional information
                        string assetDownloadCache = Paths.GetAssetCacheFolder();
                        if (Directory.Exists(assetDownloadCache))
                        {
                            // check if forced local update is requested after upgrading
                            AppProperty forceLocalUpdate = DBAdapter.DB.Find<AppProperty>("ForceLocalUpdate");
                            if (forceLocalUpdate != null && forceLocalUpdate.Value.ToLowerInvariant() == "true")
                            {
                                force = true;
                                DBAdapter.DB.Delete<AppProperty>("ForceLocalUpdate");
                            }

                            await RunWithProgress<UnityPackageImporter>(
                                ACTION_ASSET_STORE_CACHE_SCAN,
                                "Scanning Unity cache",
                                imp => imp.IndexRoughLocal(new FolderSpec(assetDownloadCache), true, force));
                        }
                        else
                        {
                            Debug.LogWarning($"Could not find the asset download folder: {assetDownloadCache}");
                            EditorUtility.DisplayDialog("Error",
                                $"Could not find the asset download folder: {assetDownloadCache}.\n\nEither nothing was downloaded yet through the Package Manager or you changed the Asset cache location. In the latter case, please configure the new location under Settings.",
                                "OK");
                        }
                        break;

                    case ACTION_ASSET_STORE_CACHE_INDEX:
                        await RunWithProgress<UnityPackageImporter>(
                            ACTION_ASSET_STORE_CACHE_INDEX,
                            "Indexing Unity cache",
                            imp => imp.IndexDetails());
                        break;

                    case ACTION_PACKAGE_CACHE_SCAN:
                        string packageDownloadCache = Paths.GetPackageCacheFolder();
                        if (Directory.Exists(packageDownloadCache))
                        {
                            await RunWithProgress<PackageImporter>(
                                ACTION_PACKAGE_CACHE_SCAN,
                                "Discovering registry packages",
                                imp => imp.IndexRough(packageDownloadCache, true));
                        }
                        else
                        {
                            Debug.LogWarning($"Could not find the package cache folder: {packageDownloadCache}");
                        }
                        break;

                    case ACTION_PACKAGE_CACHE_INDEX:
                        await RunWithProgress<PackageImporter>(
                            ACTION_PACKAGE_CACHE_INDEX,
                            "Indexing Unity registry cache",
                            imp => imp.IndexDetails());
                        break;

                    case ACTION_FOLDERS_INDEX:
                        await RunWithProgress<FolderImporter>(
                            ACTION_FOLDERS_INDEX,
                            "Updating additional folders",
                            imp => imp.Run(force));
                        break;

                    case ACTION_ASSET_STORE_DOWNLOADS:
                        // needs to be started as coroutine due to download triggering which cannot happen outside main thread 
                        await RunWithProgress<UnityPackageDownloadImporter>(
                            ACTION_ASSET_STORE_DOWNLOADS,
                            "Downloading and indexing assets",
                            async imp =>
                            {
                                TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
                                EditorCoroutineUtility.StartCoroutineOwnerless(imp.IndexOnline(() => tcs.SetResult(true)));
                                await tcs.Task;
                            });
                        break;

                    case ACTION_ASSET_MANAGER_INDEX:
                        await RunWithProgress<AssetManagerImporter>(
                            ACTION_ASSET_MANAGER_INDEX,
                            "Updating Asset Manager index",
                            imp => imp.Run());
                        break;

                    case ACTION_COLOR_INDEX:
                        await RunWithProgress<ColorImporter>(
                            ACTION_COLOR_INDEX,
                            "Extracting color information",
                            imp => imp.Run());
                        break;

                    case ACTION_AI_CAPTIONS:
                        await RunWithProgress<CaptionCreator>(
                            ACTION_AI_CAPTIONS,
                            "Creating AI captions",
                            imp => imp.Run());
                        break;

                    case ACTION_BACKUP:
                        await RunWithProgress<AssetBackup>(
                            ACTION_BACKUP,
                            "Backing up assets",
                            imp => imp.Run());
                        break;

                    default:
                        if (action.key.StartsWith(ACTION_USER))
                        {
                            int id = int.Parse(action.key.Split('-').Last());
                            CustomAction ca = DBAdapter.DB.Find<CustomAction>(id);
                            await RunUserAction(ca);
                        }
                        else
                        {
                            Debug.LogError($"No handler found to run action '{action.name}'.");
                        }
                        break;
                }

                // check all actions since also sub-actions might have been started
                actions.ForEach(a => a.CheckStopped());
            }
            actions.ForEach(a => a.scheduled = false);

            // set after initial wizard to capture only a small first portion
            if (!AI.Config.quickIndexingDone)
            {
                AI.Config.quickIndexingDone = true;
                AI.SaveConfig();
            }
            else
            {
                // final pass: start over once if that was the very first time indexing since after all updates are pulled the indexing might crunch additional data
                AppProperty initialIndexingDone = DBAdapter.DB.Find<AppProperty>("InitialIndexingDone");
                if (!CancellationRequested && (initialIndexingDone == null || initialIndexingDone.Value.ToLowerInvariant() != "true"))
                {
                    DBAdapter.DB.InsertOrReplace(new AppProperty("InitialIndexingDone", "true"));
                    await RunActions(actions, true);
                    return;
                }
            }

            LastActionUpdate = DateTime.Now;
            AppProperty lastUpdate = new AppProperty("LastIndexUpdate", LastActionUpdate.ToString(CultureInfo.InvariantCulture));
            DBAdapter.DB.InsertOrReplace(lastUpdate);

            OnActionsDone?.Invoke();
        }

        public async Task RunUserAction(CustomAction ca)
        {
            UpdateAction action = Actions.FirstOrDefault(a => a.key == ACTION_USER + ca.Id);
            if (action == null)
            {
                Debug.LogError($"Could not find a registered custom action '{ca.Name}'.");
                return;
            }

            // Use ActionRunner from Automator with SQLite repository
            SqliteActionRepository repository = new SqliteActionRepository();
            await RunWithProgress(
                ACTION_USER + ca.Id,
                () => new ActionRunner(repository),
                $"Running custom action: {ca.Name}",
                runner => ((ActionRunner)runner).RunAction(ca.Id));
        }

        public async void Reindex(AssetInfo info)
        {
            CancellationRequested = false;

            switch (info.AssetSource)
            {
                case Asset.Source.AssetStorePackage:
                case Asset.Source.CustomPackage:
                    await RunWithProgress<UnityPackageImporter>(
                        ACTION_ASSET_STORE_CACHE_INDEX,
                        "Indexing package",
                        imp => imp.IndexDetails(info.Id));
                    break;

                case Asset.Source.RegistryPackage:
                    await RunWithProgress<PackageImporter>(
                        ACTION_PACKAGE_CACHE_INDEX,
                        "Indexing registry package",
                        imp => imp.IndexDetails(info.Id));
                    break;

                case Asset.Source.Archive:
                    await RunWithProgress<ArchiveImporter>(
                        ACTION_ARCHIVE_FOLDERS_INDEX,
                        "Reindexing archive",
                        imp => imp.IndexDetails(info.ToAsset()));
                    break;

                case Asset.Source.AssetManager:
                    await RunWithProgress<AssetManagerImporter>(
                        ACTION_ASSET_MANAGER_INDEX,
                        "Updating Single Asset Manager entry",
                        imp => imp.Run(info.ToAsset()));
                    break;

                case Asset.Source.Directory:
                    FolderSpec spec = AI.Config.folders.FirstOrDefault(f => f.location == info.Location && f.folderType == info.GetFolderSpecType());
                    if (spec != null)
                    {
                        await RunWithProgress<MediaImporter>(
                            ACTION_MEDIA_FOLDERS_INDEX,
                            "Updating files index",
                            imp => imp.Index(spec));
                    }
                    break;

                default:
                    Debug.LogError($"Unsupported asset source of '{info.GetDisplayName()}' for index refresh: {info.AssetSource}");
                    break;
            }

            OnActionsDone?.Invoke();
        }

        public async Task FetchAssetDetails(bool forceUpdate = false, int assetId = 0, bool skipEvents = false)
        {
            Func<AssetStoreImporter, Task> work = async imp =>
            {
                // set skipEvents if update changed significant data, like version or name
                if (assetId > 0)
                {
                    if (await imp.FetchAssetsDetails(forceUpdate, assetId, forceUpdate)) skipEvents = false;
                }
                else
                {
                    if (forceUpdate)
                    {
                        if (await imp.FetchAssetsDetails(forceUpdate, 0, forceUpdate)) skipEvents = false;
                    }
                    else
                    {
                        List<Asset> itemsToUpdate = await imp.FetchAssetUpdates(forceUpdate);
                        if (!CancellationRequested)
                        {
                            if (itemsToUpdate != null)
                            {
                                if (await imp.FetchAssetsDetails(itemsToUpdate, true, true)) skipEvents = false;
                            }
                            else
                            {
                                Debug.Log("New method for fetching asset details did not work, falling back to full scan.");
                                if (await imp.FetchAssetsDetails(forceUpdate, 0, forceUpdate)) skipEvents = false;
                            }
                        }
                    }
                }
            };

            await RunWithProgress<AssetStoreImporter>(
                ACTION_ASSET_STORE_DETAILS,
                "Updating package details",
                imp => work(imp));

            // skip in optional update scenarios like when user selects something in the tree to avoid hick-ups 
            if (!skipEvents) AI.TriggerPackageRefresh();
        }

        public bool IsActive(string actionKey)
        {
            return IsActive(Actions.FirstOrDefault(a => a.key == actionKey));
        }

        public bool IsActive(UpdateAction action)
        {
            if (action == null) return false;

            EnsureStatesInitialized();

            UpdateActionState state = AI.Config.actionStates[_curState]?.actions?.FirstOrDefault(x => x.key == action.key);
            return state?.enabled ?? false;
        }

        private void EnsureStatesInitialized()
        {
            if (AI.Config.actionStates == null || AI.Config.actionStates.Count == 0)
            {
                AI.Config.actionStates = new List<UpdateActionStates>();
                SetDefaultStates(0);
            }
            AI.Config.actionStates[0].name = ACTION_DEFAULT_NAME;
        }

        public void SetAllActive(bool enabled)
        {
            foreach (UpdateAction action in Actions)
            {
                SetActive(action, enabled);
            }
        }

        public void SetActive(string actionKey, bool enabled)
        {
            SetActive(Actions.FirstOrDefault(a => a.key == actionKey), enabled);
        }

        public void SetActive(UpdateAction action, bool enabled)
        {
            if (action == null) return;

            UpdateActionState state = AI.Config.actionStates[_curState].actions.FirstOrDefault(x => x.key == action.key);
            if (state == null)
            {
                AI.Config.actionStates[_curState].actions.Add(new UpdateActionState {key = action.key, enabled = enabled});
            }
            else
            {
                state.enabled = enabled;
            }
        }

        public void SetDefaultActive()
        {
            SetDefaultStates(_curState);
        }

        public void CancelAll()
        {
            CancellationRequested = true;

            GetRunningActions().ForEach(a => a.progress?.ForEach(p => p.Cancel()));
        }

        public bool CreateBackups
        {
            get => IsActive(ACTION_BACKUP);
            set => SetActive(ACTION_BACKUP, value);
        }

        public bool CreateAICaptions
        {
            get => IsActive(ACTION_AI_CAPTIONS);
            set => SetActive(ACTION_AI_CAPTIONS, value);
        }

        public bool ExtractColors
        {
            get => IsActive(ACTION_COLOR_INDEX);
            set => SetActive(ACTION_COLOR_INDEX, value);
        }

        public bool DownloadAssets
        {
            get => IsActive(ACTION_ASSET_STORE_DOWNLOADS);
            set => SetActive(ACTION_ASSET_STORE_DOWNLOADS, value);
        }

        public bool IndexPackageCache
        {
            get => IsActive(ACTION_PACKAGE_CACHE_INDEX);
            set => SetActive(ACTION_PACKAGE_CACHE_INDEX, value);
        }

        public bool IndexAssetManager
        {
            get => IsActive(ACTION_ASSET_MANAGER_INDEX);
            set => SetActive(ACTION_ASSET_MANAGER_INDEX, value);
        }
    }
}
