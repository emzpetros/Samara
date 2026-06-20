#if ASSET_INVENTORY_MCP
using Unity.AI.MCP.Editor.Helpers;
using Unity.AI.MCP.Editor.ToolRegistry;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    public static class SceneTools
    {
        public class AddToSceneParams
        {
            [McpDescription("Project-relative path of prefab/model, e.g. 'Assets/ThirdParty/MyPrefab.prefab'. From importAsset result.", Required = true)]
            public string ProjectPath { get; set; }

            [McpDescription("World X position. Defaults to scene view pivot.")]
            public float PositionX { get; set; } = float.NaN;

            [McpDescription("World Y position. Defaults to scene view pivot.")]
            public float PositionY { get; set; } = float.NaN;

            [McpDescription("World Z position. Defaults to scene view pivot.")]
            public float PositionZ { get; set; } = float.NaN;

            [McpDescription("Name of existing GameObject to parent under.")]
            public string ParentGameObject { get; set; }
        }

        [McpTool("AssetInventory_addToScene",
            "Instantiate an imported prefab/model into the active scene with optional position and parenting.",
            Groups = new[] {"Asset Inventory/Import"})]
        public static object AddToScene(AddToSceneParams parameters)
        {
            if (string.IsNullOrEmpty(parameters.ProjectPath))
            {
                return Response.Error("ProjectPath is required.");
            }

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(parameters.ProjectPath);
            if (prefab == null)
            {
                return Response.Error($"Could not load prefab or model at '{parameters.ProjectPath}'. Ensure the file exists and is a prefab or model.");
            }

            // Determine parent
            Transform parentTransform = null;
            if (!string.IsNullOrEmpty(parameters.ParentGameObject))
            {
                GameObject parent = GameObject.Find(parameters.ParentGameObject);
                if (parent == null)
                {
                    return Response.Error($"Parent GameObject '{parameters.ParentGameObject}' not found in the scene.");
                }
                parentTransform = parent.transform;
            }

            // When no explicit parent is requested, clear the selection so AssetUtils.AddToScene
            // does not auto-parent under the previously instantiated object.
            if (parentTransform == null)
            {
                Selection.activeGameObject = null;
            }

            bool hasPosition = !float.IsNaN(parameters.PositionX) || !float.IsNaN(parameters.PositionY) || !float.IsNaN(parameters.PositionZ);
            if (hasPosition)
            {
                float x = float.IsNaN(parameters.PositionX) ? 0f : parameters.PositionX;
                float y = float.IsNaN(parameters.PositionY) ? 0f : parameters.PositionY;
                float z = float.IsNaN(parameters.PositionZ) ? 0f : parameters.PositionZ;
                AssetUtils.AddToScene(parameters.ProjectPath, new Vector3(x, y, z), parentTransform);
            }
            else if (parentTransform != null)
            {
                AssetUtils.AddToScene(parameters.ProjectPath, Vector3.zero, parentTransform);
            }
            else
            {
                AssetUtils.AddToScene(parameters.ProjectPath);
            }

            GameObject instance = Selection.activeGameObject;
            string instanceName = instance != null ? instance.name : prefab.name;

            return Response.Success(
                $"'{instanceName}' added to the scene at {(hasPosition ? $"({parameters.PositionX}, {parameters.PositionY}, {parameters.PositionZ})" : "scene view pivot")}.",
                new { gameObjectName = instanceName });
        }
    }
}
#endif
