using System.Collections.Generic;
using UnityEngine;

namespace AssetInventory
{
    public sealed class SavedSearchUI : BaseSavedSearchUI<SavedSearch>
    {
        public static SavedSearchUI ShowWindow()
        {
            SavedSearchUI window = GetWindow<SavedSearchUI>("Saved Search Editor");
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

            if (!string.IsNullOrEmpty(_savedSearch.Type)) filters.Add($"Type: {_savedSearch.Type}");
            if (_savedSearch.PackageTypes != 0) filters.Add("Package Type");
            if (_savedSearch.PackageSrPs != 0) filters.Add("SRP");
            if (_savedSearch.PriceOption != 0) filters.Add("Price");
            if (_savedSearch.ImageType != 0) filters.Add("Image Type");
            if (!string.IsNullOrEmpty(_savedSearch.Package)) filters.Add($"Package: {_savedSearch.Package}");
            if (!string.IsNullOrEmpty(_savedSearch.PackageTag)) filters.Add($"Tag: {_savedSearch.PackageTag}");
            if (!string.IsNullOrEmpty(_savedSearch.FileTag)) filters.Add($"File Tag: {_savedSearch.FileTag}");
            if (!string.IsNullOrEmpty(_savedSearch.Publisher)) filters.Add($"Publisher: {_savedSearch.Publisher}");
            if (!string.IsNullOrEmpty(_savedSearch.Category)) filters.Add($"Category: {_savedSearch.Category}");
            if (!string.IsNullOrEmpty(_savedSearch.Width)) filters.Add("Width");
            if (!string.IsNullOrEmpty(_savedSearch.Height)) filters.Add("Height");
            if (!string.IsNullOrEmpty(_savedSearch.Length)) filters.Add("Length");
            if (!string.IsNullOrEmpty(_savedSearch.Size)) filters.Add("Size");
            if (_savedSearch.ColorOption != 0) filters.Add("Color");
            if (!string.IsNullOrEmpty(_savedSearch.VariableDefinitions)) filters.Add("Variables");

            return filters.Count > 0 ? string.Join(", ", filters) : "No filters";
        }

        protected override void UpdateDatabase()
        {
            DBAdapter.DB.Update(_savedSearch);
        }
    }
}
