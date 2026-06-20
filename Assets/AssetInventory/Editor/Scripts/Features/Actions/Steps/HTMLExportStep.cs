using Automator;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AssetInventory
{
    [Serializable]
    public sealed class HTMLExportStep : ActionStep
    {
        public HTMLExportStep()
        {
            Key = "HTMLExport";
            Name = "HTML Export";
            Description = "Export the full database to HTML using a template.";
            Category = ActionCategory.Actions;

            // Template parameter with lazy loading
            Parameters.Add(new StepParameter
            {
                Name = "Template",
                Description = "Template to use for the HTML export.",
                ValueList = StepParameter.ValueType.Custom,
                LazyLoadOptions = true
            });

            // Target folder parameter
            Parameters.Add(new StepParameter
            {
                Name = "Target",
                Description = "Folder where the HTML export will be saved.",
                ValueList = StepParameter.ValueType.Folder,
                DefaultValue = new ParameterValue(Paths.GetStorageFolder())
            });
        }

        public override async Task Run(List<ParameterValue> parameters)
        {
            // Get parameters
            string templateId = parameters[0].stringValue;
            string targetFolder = parameters[1].stringValue;

            // Load templates fresh for the run (don't rely on constructor field)
            List<TemplateInfo> templates = TemplateUtils.LoadTemplates();

            // Check if templates are available
            if (string.IsNullOrEmpty(templateId))
            {
                throw new Exception("No templates available for HTML export. Please ensure templates are present in the Templates folder.");
            }

            // Find template by filename
            TemplateInfo selectedTemplate = templates.FirstOrDefault(t => t.GetNameFromFile() == templateId);

            if (selectedTemplate == null)
            {
                throw new Exception($"Template with ID '{templateId}' not found. Please reconfigure this action step.");
            }

            // Skip empty separator entries
            if (string.IsNullOrWhiteSpace(selectedTemplate.name))
            {
                throw new Exception("Cannot export with an empty template name.");
            }

            // Create target folder if it doesn't exist
            Directory.CreateDirectory(targetFolder);

            // Get assets for export
            List<AssetInfo> assets = AssetInventoryActionContext.GetAssetsForStep();

            // Create export environment
            TemplateExportEnvironment env = new TemplateExportEnvironment
            {
                name = "Action Export",
                publishFolder = Path.GetFullPath(targetFolder),
                dataPath = "data/",
                imagePath = "Previews/",
                excludeImages = false,
                internalIdsOnly = false
            };

            // Get or create template export settings
            if (AI.Config.templateExportSettings == null)
            {
                AI.Config.templateExportSettings = new TemplateExportSettings();
            }

            if (AI.Config.templateExportSettings.environments == null || AI.Config.templateExportSettings.environments.Count == 0)
            {
                AI.Config.templateExportSettings.environments = new List<TemplateExportEnvironment> {env};
            }

            TemplateExport exporter = new TemplateExport();
            await exporter.Run(
                assets,
                selectedTemplate,
                templates,
                AI.Config.templateExportSettings,
                env
            );
        }

        public override List<Tuple<string, ParameterValue>> GetParamOptions(StepParameter param, List<ParameterValue> parameters)
        {
            if (param.Name == "Template")
            {
                List<Tuple<string, ParameterValue>> templateOptions = new List<Tuple<string, ParameterValue>>();
                List<TemplateInfo> templates = TemplateUtils.LoadTemplates();

                foreach (TemplateInfo template in templates)
                {
                    if (!string.IsNullOrWhiteSpace(template.name))
                    {
                        string templateId = template.GetNameFromFile();
                        templateOptions.Add(new Tuple<string, ParameterValue>(template.name, new ParameterValue(templateId)));
                    }
                }

                // Add a default option if no templates are available
                if (templateOptions.Count == 0)
                {
                    templateOptions.Add(new Tuple<string, ParameterValue>("No templates available", new ParameterValue("")));
                }

                return templateOptions;
            }
            return base.GetParamOptions(param, parameters);
        }
    }
}
