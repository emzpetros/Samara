using System;
using SQLite;

namespace AssetInventory
{
    [Serializable]
    public class SavedPackageSearch
    {
        [PrimaryKey, AutoIncrement] public int Id { get; set; }
        public string Name { get; set; }
        public string Icon { get; set; }
        public string Color { get; set; }

        public string SearchPhrase { get; set; }
        public int PackagesListing { get; set; }
        public int SRPs { get; set; }
        public int Deprecation { get; set; }
        public int Maintenance { get; set; }
        public int PriceOption { get; set; }
        public float Price { get; set; }
        public string PackageTag { get; set; }
        public string Publisher { get; set; }
        public string Category { get; set; }
        public int UpdateDateOption { get; set; }
        public string UpdateBeforeDate { get; set; } // "yyyy-MM-dd" format
        public string UpdateAfterDate { get; set; }  // "yyyy-MM-dd" format
        public int PurchaseDateOption { get; set; }
        public string PurchaseBeforeDate { get; set; } // "yyyy-MM-dd" format
        public string PurchaseAfterDate { get; set; }  // "yyyy-MM-dd" format
        public int PackageSizeOption { get; set; }
        public float PackageSizeMB { get; set; }
        public int UnityVersionOption { get; set; }
    }
}

