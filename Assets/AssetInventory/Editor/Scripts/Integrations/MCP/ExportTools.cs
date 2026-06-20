#if ASSET_INVENTORY_MCP
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Unity.AI.MCP.Editor.Helpers;
using Unity.AI.MCP.Editor.ToolRegistry;

namespace AssetInventory
{
    public static class ExportTools
    {
        #region Export CSV

        public class ExportCSVParams
        {
            [McpDescription("Output file path for the CSV.", Required = true)]
            public string FilePath { get; set; }

            [McpDescription("Search phrase to filter exported packages. Supports query operators: " +
                "space-separated words are ANDed, '-word' excludes matches, '~phrase' for exact matching.")]
            public string SearchQuery { get; set; }

            [McpDescription("JSON array of package IDs to export, e.g. [3, 5, 305].")]
            public string PackageIds { get; set; }

            [McpDescription("JSON array of field names, e.g. [\"Asset/DisplayName\",\"Asset/Version\"]. Use listExportFields for options.")]
            public string Fields { get; set; }

            [McpDescription("Column separator.", Default = ";")]
            public string Separator { get; set; } = ";";

            [McpDescription("Include header row.", Default = true)]
            public bool AddHeader { get; set; } = true;
        }

        [McpTool("AssetInventory_exportCSV",
            "Export package data to CSV with configurable fields and filters.",
            Groups = new[] {"Asset Inventory/Export"})]
        public static async Task<object> ExportCSV(ExportCSVParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            if (string.IsNullOrEmpty(parameters.FilePath))
            {
                return Response.Error("FilePath is required.");
            }

            HashSet<int> packageIdFilter = null;
            if (!string.IsNullOrEmpty(parameters.PackageIds))
            {
                try
                {
                    List<int> ids = Newtonsoft.Json.JsonConvert.DeserializeObject<List<int>>(parameters.PackageIds);
                    if (ids != null && ids.Count > 0) packageIdFilter = new HashSet<int>(ids);
                }
                catch (Newtonsoft.Json.JsonException e)
                {
                    return Response.Error($"Invalid PackageIds JSON array: {e.Message}");
                }
            }

            List<AssetInfo> assets = Assets.LoadPackagesForExport(parameters.SearchQuery, packageIdFilter);

            CSVExportSettings settings = new CSVExportSettings
            {
                separator = !string.IsNullOrEmpty(parameters.Separator) ? parameters.Separator : ";",
                addHeader = parameters.AddHeader
            };

            if (!string.IsNullOrEmpty(parameters.Fields))
            {
                try
                {
                    settings.selectedFields = Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(parameters.Fields);
                }
                catch (Newtonsoft.Json.JsonException e)
                {
                    return Response.Error($"Invalid Fields JSON array: {e.Message}");
                }
            }

            CSVExport.EnsureSettings(settings);

            string fullPath = Path.GetFullPath(parameters.FilePath);
            CSVExport exporter = new CSVExport();
            await exporter.Run(assets, settings, fullPath);

            return Response.Success($"Exported {assets.Count} packages to '{fullPath}'.", new
            {
                filePath = fullPath,
                packageCount = assets.Count
            });
        }

        #endregion

        #region Export HTML

        public class ExportHTMLParams
        {
            [McpDescription("Template name. Use listExportTemplates for options.", Required = true)]
            public string TemplateName { get; set; }

            [McpDescription("Output folder path.", Required = true)]
            public string OutputFolder { get; set; }

            [McpDescription("Search phrase to filter exported packages. Supports query operators: " +
                "space-separated words are ANDed, '-word' excludes matches, '~phrase' for exact matching.")]
            public string SearchQuery { get; set; }
        }

        [McpTool("AssetInventory_exportHTML",
            "Export package data to HTML using a template.",
            Groups = new[] {"Asset Inventory/Export"})]
        public static async Task<object> ExportHTML(ExportHTMLParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            if (string.IsNullOrEmpty(parameters.TemplateName))
            {
                return Response.Error("TemplateName is required.");
            }

            if (string.IsNullOrEmpty(parameters.OutputFolder))
            {
                return Response.Error("OutputFolder is required.");
            }

            List<TemplateInfo> templates = TemplateUtils.LoadTemplates();
            TemplateInfo selectedTemplate = templates.FirstOrDefault(t => t.GetNameFromFile() == parameters.TemplateName);
            if (selectedTemplate == null)
            {
                selectedTemplate = templates.FirstOrDefault(t => t.name != null && t.name.Equals(parameters.TemplateName, System.StringComparison.OrdinalIgnoreCase));
            }

            if (selectedTemplate == null)
            {
                return Response.Error($"Template '{parameters.TemplateName}' not found. Use AssetInventory_listExportTemplates to see available templates.");
            }

            List<AssetInfo> assets = Assets.LoadPackagesForExport(parameters.SearchQuery);

            string outputFolder = Path.GetFullPath(parameters.OutputFolder);

            Directory.CreateDirectory(outputFolder);

            TemplateExportEnvironment env = new TemplateExportEnvironment
            {
                name = "MCP Export",
                publishFolder = outputFolder,
                dataPath = "data/",
                imagePath = "Previews/",
                excludeImages = false,
                internalIdsOnly = false
            };

            if (AI.Config.templateExportSettings == null)
            {
                AI.Config.templateExportSettings = new TemplateExportSettings();
            }

            TemplateExport exporter = new TemplateExport();
            await exporter.Run(assets, selectedTemplate, templates, AI.Config.templateExportSettings, env);

            return Response.Success($"Exported {assets.Count} packages to '{outputFolder}' using template '{selectedTemplate.name}'.", new
            {
                outputFolder,
                packageCount = assets.Count,
                templateName = selectedTemplate.name
            });
        }

        #endregion

        #region List Export Fields

        public class ListExportFieldsParams
        {
        }

        [McpTool("AssetInventory_listExportFields",
            "List available field names for CSV export. Fields use 'Asset/PropertyName' format.",
            Groups = new[] {"Asset Inventory/Export"})]
        public static object ListExportFields(ListExportFieldsParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            List<string> allFields = CSVExport.GetAllFields();
            List<string> defaultFields = CSVExport.GetDefaultFields();
            System.Collections.Generic.HashSet<string> defaultSet = new System.Collections.Generic.HashSet<string>(defaultFields);

            return Response.Success($"Found {allFields.Count} export fields ({defaultFields.Count} defaults).", new
            {
                fields = allFields.Select(f => new
                {
                    name = f,
                    isDefault = defaultSet.Contains(f)
                }).ToArray()
            });
        }

        #endregion

        #region List Export Templates

        public class ListExportTemplatesParams
        {
        }

        [McpTool("AssetInventory_listExportTemplates",
            "List available HTML export templates.",
            Groups = new[] {"Asset Inventory/Export"})]
        public static object ListExportTemplates(ListExportTemplatesParams parameters)
        {
            object initError = McpResultHelper.EnsureInit();
            if (initError != null) return initError;

            List<TemplateInfo> templates = TemplateUtils.LoadTemplates();

            var filteredTemplates = templates.Where(t => !string.IsNullOrWhiteSpace(t.name)).ToList();
            return Response.Success($"Found {filteredTemplates.Count} export templates.", new
            {
                templates = filteredTemplates.Select(t => new
                    {
                        id = t.GetNameFromFile(),
                        name = t.name,
                        description = t.description,
                        isSample = t.isSample,
                        version = t.version
                    }).ToArray()
            });
        }

        #endregion
    }
}
#endif
