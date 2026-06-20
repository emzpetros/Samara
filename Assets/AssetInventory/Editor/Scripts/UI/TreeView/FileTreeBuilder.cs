using System.Collections.Generic;
using System.Linq;
using UnityEditor.IMGUI.Controls;
#pragma warning disable CS0618 // Type or member is obsolete
#if UNITY_6000_2_OR_NEWER
using BaseTreeViewState = UnityEditor.IMGUI.Controls.TreeViewState<int>;
#else
using BaseTreeViewState = UnityEditor.IMGUI.Controls.TreeViewState;
#endif

namespace AssetInventory
{
    internal static class FileTreeBuilder
    {
        public struct Result
        {
            public FileTreeViewControl TreeView;
            public Dictionary<string, FileTreeElement> PathToElementMap;
        }

        public static Result Build(List<string> paths, BaseTreeViewState state)
        {
            List<FileTreeElement> treeElements = new List<FileTreeElement>();
            FileTreeElement root = new FileTreeElement("Root", -1, 0);
            treeElements.Add(root);

            Dictionary<string, FileTreeElement> pathToElementMap = new Dictionary<string, FileTreeElement>();
            int idCounter = 1;

            foreach (string path in paths.OrderBy(p => p))
            {
                string[] parts = path.Split('/');
                string currentPath = "";

                for (int i = 0; i < parts.Length; i++)
                {
                    string part = parts[i];
                    if (string.IsNullOrEmpty(currentPath)) currentPath = part;
                    else currentPath += "/" + part;

                    if (!pathToElementMap.TryGetValue(currentPath, out FileTreeElement node))
                    {
                        bool isFolder = i < parts.Length - 1;
                        int depth = i;

                        node = new FileTreeElement(part, depth, idCounter++)
                        {
                            Path = currentPath,
                            IsFolder = isFolder
                        };

                        pathToElementMap[currentPath] = node;
                        treeElements.Add(node);
                    }
                }
            }

            FileTreeElement rootElement = TreeElementUtility.ListToTree(treeElements);
            SortTree(rootElement);
            TreeElementUtility.TreeToList(rootElement, treeElements);

            TreeModel<FileTreeElement> model = new TreeModel<FileTreeElement>(treeElements);
            FileTreeViewControl treeView = new FileTreeViewControl(state, model);
            treeView.ExpandAll();

            return new Result
            {
                TreeView = treeView,
                PathToElementMap = pathToElementMap
            };
        }

        public static Result Build(List<AssetFile> files, BaseTreeViewState state)
        {
            List<string> paths = new List<string>(files.Count);
            foreach (AssetFile file in files)
            {
                if (!string.IsNullOrEmpty(file.Path)) paths.Add(file.Path);
            }
            return Build(paths, state);
        }

        public static void SortTree(TreeElement node)
        {
            if (node.Children != null && node.Children.Count > 0)
            {
                node.Children = node.Children
                    .OrderByDescending(c => c is FileTreeElement fte && fte.IsFolder)
                    .ThenBy(c => c.TreeName)
                    .ToList();

                foreach (TreeElement child in node.Children)
                {
                    SortTree(child);
                }
            }
        }
    }
}
