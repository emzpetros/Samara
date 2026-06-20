using System;

namespace AssetInventory
{
    [Serializable]
    public sealed class TagTreeElement : TreeElement
    {
        public Tag Tag { get; set; }
        public int AssignmentCount { get; set; }

        public TagTreeElement()
        {
        }

        public TagTreeElement(Tag tag, int depth, int id) : base(tag?.Name ?? "Root", depth, id)
        {
            Tag = tag;
        }

        public override string ToString()
        {
            return $"Tag Tree Element '{TreeName}' ({Tag?.Id})";
        }
    }
}
