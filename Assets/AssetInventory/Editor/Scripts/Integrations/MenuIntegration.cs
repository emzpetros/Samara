using ImpossibleRobert.Common;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    public class MenuIntegration : EditorWindow
    {
#if !ASSET_INVENTORY_HIDE_AI
        [MenuItem("Assets/Asset Inventory", priority = 9000)]
#endif
#if !ASSET_INVENTORY_HIDE_TOOLS_MENU
        [MenuItem("Tools/Asset Inventory/Asset Inventory", false, 600)]
#endif
#if UNITY_6000_1_OR_NEWER
        [MenuItem("Window/Package Management/Asset Inventory")]
#else
        [MenuItem("Window/Asset Inventory", priority = 1500)]
#endif
        public static void ShowWindow()
        {
            IndexUI window = GetWindow<IndexUI>("Asset Inventory");
            window.minSize = new Vector2(650, 300);
        }

#if !ASSET_INVENTORY_HIDE_TOOLS_MENU
        [MenuItem("Tools/Asset Inventory/Welcome...", false, 699)]
#endif
        public static void ShowWelcome()
        {
            WelcomeWindow.ShowWindow();
        }

#if !ASSET_INVENTORY_HIDE_TOOLS_MENU
        [MenuItem("Tools/Asset Inventory/About...", false, 700)]
#endif
        public static void ShowAbout()
        {
            AboutWindow.Show("AssetInventory");
        }
    }
}