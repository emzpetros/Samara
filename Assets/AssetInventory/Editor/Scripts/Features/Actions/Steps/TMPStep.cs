using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Automator;
using UnityEngine;

namespace AssetInventory
{
    [Serializable]
    public sealed class TMPStep : ActionStep
    {
        public TMPStep()
        {
            Key = "TMP";
            Name = "TextMeshPro";
            Description = "Install TextMeshPro addons.";
            Category = ActionCategory.Importing;
            Parameters.Add(new StepParameter
            {
                Name = "Essentials",
                Type = StepParameter.ParamType.Bool,
                DefaultValue = new ParameterValue(true)
            });
            Parameters.Add(new StepParameter
            {
                Name = "Examples",
                Type = StepParameter.ParamType.Bool,
                DefaultValue = new ParameterValue(true)
            });
        }

        public static bool AreTMPEssentialsImported()
        {
            return Directory.Exists(Path.Combine(Application.dataPath, "TextMesh Pro", "Resources"));
        }

        public static void ImportEssentials()
        {
            string packageFullPath = GetPackageFullPath();
            if (string.IsNullOrEmpty(packageFullPath)) return;

            AssetInfo essential = new AssetInfo();
            essential.AssetSource = Asset.Source.CustomPackage;
            essential.SafeName = "TMP Essential Resources";
            essential.SetLocation(packageFullPath + "/Package Resources/TMP Essential Resources.unitypackage");

            List<AssetInfo> infos = new List<AssetInfo> {essential};
            ImportUI importUI = ImportUI.ShowWindow();
            importUI.Init(infos, true, noCustomFolder: true);
        }

        public override async Task Run(List<ParameterValue> parameters)
        {
            string packageFullPath = GetPackageFullPath();
            if (string.IsNullOrEmpty(packageFullPath))
            {
                throw new Exception("TextMeshPro package not found. Please install TextMeshPro before running this action.");
            }

            List<AssetInfo> infos = new List<AssetInfo>();
            if (parameters[0].boolValue)
            {
                AssetInfo essential = new AssetInfo();
                essential.AssetSource = Asset.Source.CustomPackage;
                essential.SafeName = "TMP Essential Resources";
                essential.SetLocation(packageFullPath + "/Package Resources/TMP Essential Resources.unitypackage");
                infos.Add(essential);
            }
            if (parameters[1].boolValue)
            {
                AssetInfo samples = new AssetInfo();
                samples.AssetSource = Asset.Source.CustomPackage;
                samples.SafeName = "TMP Examples & Extras";
                samples.SetLocation(packageFullPath + "/Package Resources/TMP Examples & Extras.unitypackage");
                infos.Add(samples);
            }
            if (infos.Count == 0) return;

            bool finished = false;
            ImportUI importUI = ImportUI.ShowWindow();
            importUI.Init(infos, true, () => finished = true, true, ActionHandler.AI_ACTION_LOCK);

            while (!finished)
            {
                await Task.Yield();
            }
        }

        // taken from TMPro_PackageResourceImporter.cs as we cannot rely on the package to be installed already and don't want a dependency
        public static string GetPackageFullPath()
        {
            // Check for potential UPM package
            string packagePath = Path.GetFullPath("Packages/com.unity.textmeshpro");
            if (Directory.Exists(packagePath))
            {
                return packagePath;
            }

            // Check for TMP bundled inside ugui package (Unity 2023.2+)
            packagePath = Path.GetFullPath("Packages/com.unity.ugui");
            if (Directory.Exists(packagePath + "/Package Resources"))
            {
                return packagePath;
            }

            packagePath = Path.GetFullPath("Assets/..");
            if (Directory.Exists(packagePath))
            {
                // Search default location for development package
                if (Directory.Exists(packagePath + "/Assets/Packages/com.unity.TextMeshPro/Editor Resources"))
                {
                    return packagePath + "/Assets/Packages/com.unity.TextMeshPro";
                }

                // Search for default location of normal TextMesh Pro AssetStore package
                if (Directory.Exists(packagePath + "/Assets/TextMesh Pro/Editor Resources"))
                {
                    return packagePath + "/Assets/TextMesh Pro";
                }

                // Search for potential alternative locations in the user project
                string[] matchingPaths = Directory.GetDirectories(packagePath, "TextMesh Pro", SearchOption.AllDirectories);
                string path = ValidateLocation(matchingPaths, packagePath);
                if (path != null) return packagePath + path;
            }

            return null;
        }

        private static string ValidateLocation(string[] paths, string projectPath)
        {
            for (int i = 0; i < paths.Length; i++)
            {
                // Check if the Editor Resources folder exists.
                if (Directory.Exists(paths[i] + "/Editor Resources"))
                {
                    string folderPath = paths[i].Replace(projectPath, "");
                    folderPath = folderPath.TrimStart('\\', '/');
                    return folderPath;
                }
            }

            return null;
        }
    }
}