namespace AssetInventory
{
    public sealed class InventoryStats
    {
        public int TotalPackages;
        public int IndexedPackages;
        public int IndexablePackages;
        public int SubPackages;
        public int TotalFiles;
        public long DatabaseSize;
        public int PurchasedAssets;
        public int DeprecatedPackages;
        public int AbandonedPackages;
        public int ExcludedPackages;
        public int BackupPackages;
        public int AIPackages;
        public int CustomPackages;
        public int RegistryPackages;

        public int AllPackages => TotalPackages + SubPackages;

        public SourceBreakdown BySource;

        public sealed class SourceBreakdown
        {
            public int AssetStore;
            public int Custom;
            public int Directory;
            public int Registry;
            public int Archive;
            public int AssetManager;
        }
    }
}
