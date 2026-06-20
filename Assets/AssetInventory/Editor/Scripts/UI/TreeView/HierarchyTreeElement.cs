using System;

namespace AssetInventory
{
    [Serializable]
    public sealed class HierarchyTreeElement : TreeElement
    {
        public string FilterKey { get; set; }
        public string FilterValue { get; set; }
        public int MatchCount { get; set; }
        public string FullPath { get; set; }

        public HierarchyTreeElement()
        {
        }

        public HierarchyTreeElement(string name, int depth, int id) : base(name, depth, id)
        {
        }

        public HierarchyTreeElement(string name, int depth, int id, string filterKey, string filterValue, string fullPath, int matchCount) : base(name, depth, id)
        {
            FilterKey = filterKey;
            FilterValue = filterValue;
            FullPath = fullPath;
            MatchCount = matchCount;
        }
    }
}
