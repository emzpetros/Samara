#if UNITY_6000_3_OR_NEWER
using ImpossibleRobert.Common;
using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;

namespace AssetInventory
{
    public static class ToolbarIntegration
    {
        private static Texture2D _icon32;
        private static Texture2D ToolbarIcon
        {
            get
            {
                if (_icon32 == null) _icon32 = CommonUIStyles.LoadTexture("asset-inventory-icon32");
                return _icon32;
            }
        }

        [MainToolbarElement("Asset Inventory/Open Asset Inventory", defaultDockPosition = MainToolbarDockPosition.Left)]
        public static MainToolbarElement OpenToolButton()
        {
            MainToolbarContent content = new MainToolbarContent(ToolbarIcon)
            {
                tooltip = "Open Asset Inventory"
            };

            return new MainToolbarButton(content, OnOpenToolButtonClicked);
        }

        private static void OnOpenToolButtonClicked()
        {
            IndexUI window = EditorWindow.GetWindow<IndexUI>("Asset Inventory");
            window.minSize = new Vector2(650, 300);
        }

        [MainToolbarElement("Asset Inventory/Add To Scene", defaultDockPosition = MainToolbarDockPosition.Left)]
        public static MainToolbarElement AddAssetToSceneButton()
        {
            MainToolbarContent content = new MainToolbarContent(ToolbarIcon)
            {
                text = "Add To Scene",
                tooltip = "Open Asset Inventory to pick an asset and add it to the scene"
            };

            return new MainToolbarButton(content, OnAddSceneButtonClicked);
        }

        private static void OnAddSceneButtonClicked()
        {
            ResultPickerUI.Show(OnAssetPicked, "Prefabs");
        }

        private static void OnAssetPicked(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath)) return;

            AssetUtils.AddToScene(projectPath);
        }
    }
}
#endif
