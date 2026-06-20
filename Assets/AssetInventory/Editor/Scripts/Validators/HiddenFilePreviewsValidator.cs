using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImpossibleRobert.Common;

namespace AssetInventory
{
    public sealed class HiddenFilePreviewsValidator : Validator
    {
        public int HiddenFileCount { get; private set; }

        public override string ResultText => CurrentState == State.Completed
            ? $"{HiddenFileCount:N0} hidden files, {IssueCount:N0} with generated previews"
            : null;

        public HiddenFilePreviewsValidator()
        {
            Type = ValidatorType.DB;
            Speed = ValidatorSpeed.Fast;
            Name = "Hidden File Previews";
            Description = "Counts hidden files and finds generated preview cache files that are no longer needed for them.";
            FixCaption = "Delete Previews";
        }

        public override async Task Validate()
        {
            CurrentState = State.Scanning;
            HiddenFileCount = DBAdapter.DB.ExecuteScalar<int>("select count(*) from AssetFile where Hidden = 1");
            DBIssues = await GatherHiddenFilesWithGeneratedPreviews();
            CurrentState = State.Completed;
        }

        public override async Task Fix()
        {
            CurrentState = State.Fixing;
            await DeleteGeneratedPreviews(DBIssues.ToList());
            await Validate();
        }

        private async Task<List<AssetInfo>> GatherHiddenFilesWithGeneratedPreviews()
        {
            string query = @"
                select *, AssetFile.Id as Id
                from AssetFile
                inner join Asset on Asset.Id = AssetFile.AssetId
                where AssetFile.Hidden = 1
                and (
                    AssetFile.PreviewState = ?
                    or AssetFile.PreviewState = ?
                    or AssetFile.PreviewState = ?
                )";

            List<AssetInfo> files = DBAdapter.DB.Query<AssetInfo>(
                query,
                AssetFile.PreviewOptions.Provided,
                AssetFile.PreviewOptions.Custom,
                AssetFile.PreviewOptions.Redo).ToList();

            List<AssetInfo> result = new List<AssetInfo>();
            int progress = 0;
            int count = files.Count;
            int progressId = MetaProgress.Start("Checking hidden file previews");

            foreach (AssetInfo file in files)
            {
                progress++;
                MetaProgress.Report(progressId, progress, count, file.FileName);
                if (CancellationRequested) break;
                if (progress % 5000 == 0) await Task.Yield();

                if (GetExistingGeneratedPreviewFiles(file).Count > 0) result.Add(file);
            }

            MetaProgress.Remove(progressId);
            return result;
        }

        private async Task DeleteGeneratedPreviews(List<AssetInfo> files)
        {
            int progress = 0;
            int count = files.Count;
            int progressId = MetaProgress.Start("Deleting hidden file previews");

            foreach (AssetInfo file in files)
            {
                progress++;
                MetaProgress.Report(progressId, progress, count, file.FileName);
                if (CancellationRequested) break;
                if (progress % 50 == 0) await Task.Yield();

                List<string> previewFiles = GetExistingGeneratedPreviewFiles(file);
                if (previewFiles.Count == 0) continue;

                bool deleted = false;
                foreach (string previewFile in previewFiles)
                {
                    deleted |= PreviewManager.TryDeletePreviewFile(previewFile);
                }
                if (!deleted) continue;

                DBAdapter.DB.Execute("update AssetFile set PreviewState = ?, Hue = ? where Id = ?", AssetFile.PreviewOptions.None, -1f, file.Id);
            }

            MetaProgress.Remove(progressId);
        }

        private static List<string> GetExistingGeneratedPreviewFiles(AssetFile file)
        {
            List<string> result = new List<string>();
            string previewFolder = Paths.GetPreviewFolder(createOnDemand: false);
            if (string.IsNullOrWhiteSpace(previewFolder)) return result;

            AddIfGeneratedPreviewExists(result, previewFolder, file.AssetId, $"af-{file.Id}.png");
            AddIfGeneratedPreviewExists(result, previewFolder, file.AssetId, $"afa-{file.Id}.png");

            return result;
        }

        private static void AddIfGeneratedPreviewExists(List<string> result, string previewFolder, int assetId, string fileName)
        {
            string previewFile = Path.Combine(previewFolder, assetId.ToString(), fileName);
            string longPath = IOUtils.ToLongPath(previewFile);
            if (File.Exists(longPath)) result.Add(longPath);
        }
    }
}
