using System;
using System.Collections.Generic;

namespace AssetInventory
{
    [Serializable]
    public sealed class CSVExportSettings
    {
        public string separator = ";";
        public bool addHeader = true;
        public List<string> selectedFields;
        public string exportFile;

        public void EnsureDefaults()
        {
            if (string.IsNullOrEmpty(separator)) separator = ";";
            if (selectedFields == null) selectedFields = CSVExport.GetDefaultFields();
        }
    }
}