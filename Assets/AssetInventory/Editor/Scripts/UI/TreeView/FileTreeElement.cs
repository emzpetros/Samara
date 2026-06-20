using System;
using System.Collections.Generic;

namespace AssetInventory
{
    [Serializable]
    public class FileTreeElement : TreeElement
    {
        public string Path;
        public bool IsFolder;
        public bool IsSelected = true;
        public bool IsAutoExcluded;
        public bool IsAutoIncluded;
        public List<string> Usages;

        public FileTreeElement(string name, int depth, int id) : base(name, depth, id)
        {
        }
    }
}

