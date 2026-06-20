using System.Collections.Generic;
using UnityEngine;

namespace AssetInventory
{
    public sealed class SavedPackageSearchUI : BaseSavedSearchUI<SavedPackageSearch>
    {
        public static SavedPackageSearchUI ShowWindow()
        {
            SavedPackageSearchUI window = GetWindow<SavedPackageSearchUI>("Saved Package Search Editor");
            window.minSize = new Vector2(400, 150);
            return window;
        }

        protected override string GetName() => _savedSearch.Name;
        protected override void SetName(string searchName) => _savedSearch.Name = searchName;
        protected override string GetIcon() => _savedSearch.Icon;
        protected override void SetIcon(string icon) => _savedSearch.Icon = icon;
        protected override string GetColor() => _savedSearch.Color;
        protected override void SetColor(string color) => _savedSearch.Color = color;
        protected override string GetSearchPhrase() => _savedSearch.SearchPhrase;

        protected override string GetSearchDetails()
        {
            // Build a summary of active filters
            List<string> filters = new List<string>();

            if (_savedSearch.PackagesListing != 1) filters.Add("Package Type");
            if (_savedSearch.SRPs != 0) filters.Add("SRP");
            if (_savedSearch.Deprecation != 0) filters.Add("Deprecation");
            if (_savedSearch.Maintenance != 0) filters.Add("Maintenance");
            if (_savedSearch.PriceOption != 0) filters.Add("Price");
            if (!string.IsNullOrEmpty(_savedSearch.PackageTag)) filters.Add($"Tag: {_savedSearch.PackageTag}");
            if (!string.IsNullOrEmpty(_savedSearch.Publisher)) filters.Add($"Publisher: {_savedSearch.Publisher}");
            if (!string.IsNullOrEmpty(_savedSearch.Category)) filters.Add($"Category: {_savedSearch.Category}");
            if (_savedSearch.UpdateDateOption != 0) filters.Add("Update Date");
            if (_savedSearch.PackageSizeOption != 0) filters.Add("Size");
            if (_savedSearch.UnityVersionOption != 0) filters.Add("Unity Version");

            return filters.Count > 0 ? string.Join(", ", filters) : "No filters";
        }

        protected override void UpdateDatabase()
        {
            DBAdapter.DB.Update(_savedSearch);
        }
    }
}
