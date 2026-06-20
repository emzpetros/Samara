using Automator;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace AssetInventory
{
    [Serializable]
    public sealed class CSVExportStep : ActionStep
    {
        public CSVExportStep()
        {
            Key = "CSVExport";
            Name = "CSV Export";
            Description = "Export assets to CSV using the current CSV export settings.";
            Category = ActionCategory.Actions;

            Parameters.Add(new StepParameter
            {
                Name = "Target File",
                Description = "Full file path where the CSV export will be saved.",
                DefaultValue = new ParameterValue(GetDefaultTargetFile())
            });
        }

        public override async Task Run(List<ParameterValue> parameters)
        {
            string targetFile = parameters[0].stringValue;
            if (string.IsNullOrWhiteSpace(targetFile))
            {
                throw new Exception("CSV export target file is empty. Please configure a valid file path.");
            }

            List<AssetInfo> assets = AssetInventoryActionContext.GetAssetsForStep();

            if (AI.Config.csvExportSettings == null) AI.Config.csvExportSettings = new CSVExportSettings();
            CSVExport.EnsureSettings(AI.Config.csvExportSettings);

            string fullPath = Path.GetFullPath(targetFile);
            AI.Config.csvExportSettings.exportFile = fullPath;
            AI.Config.exportFolder = Path.GetDirectoryName(fullPath);
            AI.SaveConfig();

            CSVExport exporter = new CSVExport();
            await exporter.Run(assets, AI.Config.csvExportSettings, fullPath);
        }

        private static string GetDefaultTargetFile()
        {
            string folder = AI.Config?.exportFolder;
            if (string.IsNullOrWhiteSpace(folder)) folder = Paths.GetStorageFolder();
            return Path.Combine(folder, CSVExport.DEFAULT_FILE_NAME);
        }
    }
}