using ImpossibleRobert.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;

namespace AssetInventory
{
    public static class TemplateUtils
    {
        private static string _templateFolder;

        /// <summary>
        /// Loads all available templates from the Templates folder.
        /// </summary>
        /// <param name="addSeparator">If true, adds an empty separator entry between regular and sample templates.</param>
        /// <returns>List of loaded templates.</returns>
        public static List<TemplateInfo> LoadTemplates(bool addSeparator = true)
        {
            List<TemplateInfo> templates = new List<TemplateInfo>();

            // Load from default template folder
            string templateFolder = GetTemplateRootFolder();
            if (!string.IsNullOrEmpty(templateFolder))
            {
                LoadTemplatesFromFolder(templateFolder, templates);
            }

            // Load from custom template folder if specified
            if (AI.Config != null && !string.IsNullOrWhiteSpace(AI.Config.customTemplateFolder) && Directory.Exists(AI.Config.customTemplateFolder))
            {
                LoadTemplatesFromFolder(AI.Config.customTemplateFolder, templates);
            }

            templates = templates.OrderBy(t => t.isSample).ThenBy(t => t.name, StringComparer.InvariantCultureIgnoreCase).ToList();

            // Add separator for sample templates if needed
            if (addSeparator)
            {
                int idx = templates.FindIndex(t => t.isSample);
                if (idx > 0)
                {
                    TemplateInfo tmpTi = new TemplateInfo();
                    tmpTi.name = "";
                    templates.Insert(idx, tmpTi);
                }
            }

            return templates;
        }

        /// <summary>
        /// Loads templates from a specific folder and adds them to the provided list.
        /// </summary>
        /// <param name="folderPath">Path to the folder containing templates.</param>
        /// <param name="templates">List to add loaded templates to.</param>
        private static void LoadTemplatesFromFolder(string folderPath, List<TemplateInfo> templates)
        {
            IOUtils.GetFiles(folderPath, new List<string> {"*.bytes"}, SearchOption.AllDirectories).ForEach(f =>
            {
                TemplateInfo ti = new TemplateInfo();
                ti.path = f;

                // check for existing descriptor, otherwise create on the fly
                string descriptor = ti.GetDescriptorPath();
                if (File.Exists(descriptor))
                {
                    ti = JsonConvert.DeserializeObject<TemplateInfo>(File.ReadAllText(descriptor));
                    ti.path = f;
                    ti.hasDescriptor = true;
                }
                else
                {
                    ti.date = File.GetCreationTime(f);
                }
                if (string.IsNullOrWhiteSpace(ti.name)) ti.name = StringUtils.CamelCaseToWords(ti.GetNameFromFile(f));

                templates.Add(ti);
            });
        }

        /// <summary>
        /// Gets the root folder where templates are stored.
        /// </summary>
        /// <returns>Path to the template root folder.</returns>
        public static string GetTemplateRootFolder()
        {
            if (_templateFolder != null) return _templateFolder;

            _templateFolder = AssetDatabase.FindAssets("t:Folder", new[] {"Assets", "Packages"})
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => p.Replace("\\", "/").ToLowerInvariant().EndsWith("inventory/editor/templates"));

            return _templateFolder;
        }

        /// <summary>
        /// Gets the folder where new templates should be saved.
        /// Returns the custom template folder if set, otherwise the default template folder.
        /// </summary>
        /// <returns>Path to the folder for saving new templates.</returns>
        public static string GetTemplateSaveFolder()
        {
            if (AI.Config != null && !string.IsNullOrWhiteSpace(AI.Config.customTemplateFolder) && Directory.Exists(AI.Config.customTemplateFolder))
            {
                return AI.Config.customTemplateFolder;
            }
            return GetTemplateRootFolder();
        }
    }
}