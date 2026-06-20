using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

namespace AssetInventory
{
    public sealed class CSVExport
    {
        public const string DEFAULT_FILE_NAME = "assets.csv";

        private static readonly string[] _defaultFields =
        {
            "Asset/Id",
            "Asset/AssetRating",
            "Asset/AssetSource",
            "Asset/DisplayCategory",
            "Asset/DisplayName",
            "Asset/DisplayPublisher",
            "Asset/Keywords",
            "Asset/LastRelease",
            "Asset/LatestVersion",
            "Asset/License",
            "Asset/Location",
            "Asset/PackageSource",
            "Asset/PackageTags",
            "Asset/PurchaseDate",
            "Asset/RatingCount",
            "Asset/Revision",
            "Asset/SafeCategory",
            "Asset/SafeName",
            "Asset/SafePublisher",
            "Asset/SupportedUnityVersions",
            "Asset/Version"
        };

        private static readonly Dictionary<string, PropertyInfo> _propertyCache = new Dictionary<string, PropertyInfo>();

        public static List<string> GetDefaultFields()
        {
            return _defaultFields.ToList();
        }

        public static List<string> GetAllFields()
        {
            return typeof(AssetInfo)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead)
                .Select(p => "Asset/" + p.Name)
                .OrderBy(f => f)
                .ToList();
        }

        public static void EnsureSettings(CSVExportSettings settings)
        {
            settings?.EnsureDefaults();
        }

        public Task Run(List<AssetInfo> assets, CSVExportSettings settings, string filePath)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("CSV export target file is empty.", nameof(filePath));

            settings.EnsureDefaults();

            string fullPath = Path.GetFullPath(filePath);
            string targetFolder = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(targetFolder)) Directory.CreateDirectory(targetFolder);

            File.WriteAllLines(fullPath, BuildLines(assets, settings));
            return Task.CompletedTask;
        }

        public List<string> BuildLines(List<AssetInfo> assets, CSVExportSettings settings)
        {
            settings.EnsureDefaults();

            List<string> selectedFields = (settings.selectedFields ?? GetDefaultFields())
                .Where(field => !string.IsNullOrWhiteSpace(field))
                .Distinct()
                .ToList();

            List<string> result = new List<string>();
            if (settings.addHeader)
            {
                result.Add(string.Join(settings.separator, selectedFields.Select(GetFieldName)));
            }

            IEnumerable<AssetInfo> exportAssets = (assets ?? new List<AssetInfo>())
                .Where(asset => asset != null)
                .Where(asset => asset.SafeName != Asset.NONE);

            foreach (AssetInfo asset in exportAssets)
            {
                List<object> line = new List<object>();
                foreach (string selectedField in selectedFields)
                {
                    object value = GetFieldValue(asset, selectedField);
                    line.Add(SanitizeValue(value, settings.separator));
                }

                result.Add(string.Join(settings.separator, line));
            }

            return result;
        }

        private static string GetFieldName(string selectedField)
        {
            int separatorIndex = selectedField.IndexOf('/');
            return separatorIndex >= 0 ? selectedField.Substring(separatorIndex + 1) : selectedField;
        }

        private static object GetFieldValue(AssetInfo info, string selectedField)
        {
            switch (GetFieldName(selectedField))
            {
                case "AssetLink":
                    return info.GetItemLink();

                case "PackageTags":
                    return string.Join(",", info.PackageTags.Select(tag => tag.Name));
            }

            PropertyInfo property = GetProperty(selectedField);
            if (property == null)
            {
                Debug.LogError($"Export field '{selectedField}' not found.");
                return string.Empty;
            }

            return property.GetValue(info);
        }

        private static PropertyInfo GetProperty(string selectedField)
        {
            string fieldName = GetFieldName(selectedField);
            if (string.IsNullOrWhiteSpace(fieldName)) return null;

            if (_propertyCache.TryGetValue(fieldName, out PropertyInfo property)) return property;

            property = typeof(AssetInfo).GetProperty(fieldName);
            _propertyCache[fieldName] = property;
            return property;
        }

        private static object SanitizeValue(object value, string separator)
        {
            if (value == null) return string.Empty;
            if (value is string stringValue)
            {
                stringValue = stringValue
                    .Replace(separator, ",")
                    .Replace("\n", string.Empty)
                    .Replace("\r", string.Empty);

                if (stringValue.Length > 0 && (stringValue[0] == '=' || stringValue[0] == '+' || stringValue[0] == '-' || stringValue[0] == '@'))
                {
                    stringValue = "'" + stringValue;
                }

                return stringValue;
            }

            return value;
        }
    }
}