using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AssetInventory
{
    /// <summary>
    /// Provides lookup functionality for assets that have been migrated to the China-only Asset Store.
    /// These assets are still available for download but will be deprecated and receive no more updates.
    /// The data is lazy-loaded from a JSON file only when first queried.
    /// </summary>
    public static class ChinaBasedAssets
    {
        private const string DATA_FILE = "ChinaBasedAssets.json";
        private const string DATA_FILE_GUID = "ab6b8a5962015490184d9f7ff08e4493";
        private const string KEY_SEPARATOR = "|||";

        private static HashSet<string> _affectedAssets;

        /// <summary>
        /// Returns whether the given asset is affected by the China Store migration.
        /// </summary>
        public static bool IsAffected(AssetInfo info)
        {
            if (info == null) return false;

            EnsureLoaded();
            if (_affectedAssets == null || _affectedAssets.Count == 0) return false;

            string key = BuildKey(info.GetDisplayName(), info.GetDisplayPublisher());
            return _affectedAssets.Contains(key);
        }

        /// <summary>
        /// Returns the number of entries in the China-based assets list.
        /// </summary>
        public static int Count
        {
            get
            {
                EnsureLoaded();
                return _affectedAssets?.Count ?? 0;
            }
        }

        /// <summary>
        /// Forces a reload of the data file on next access.
        /// </summary>
        internal static void ClearCache()
        {
            _affectedAssets = null;
        }

        private static void EnsureLoaded()
        {
            if (_affectedAssets != null) return;

            _affectedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string filePath = FindDataFile();
            if (string.IsNullOrEmpty(filePath))
            {
                Debug.LogWarning($"Asset Inventory: Could not locate {DATA_FILE}. China Store filter will not work.");
                return;
            }

            try
            {
                string json = File.ReadAllText(filePath);
                List<Entry> entries = JsonConvert.DeserializeObject<List<Entry>>(json);
                if (entries != null)
                {
                    foreach (Entry entry in entries)
                    {
                        string key = BuildKey(entry.a, entry.p);
                        _affectedAssets.Add(key);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Asset Inventory: Failed to load {DATA_FILE}: {e.Message}");
            }
        }

        private static string BuildKey(string assetName, string publisher)
        {
            return (assetName ?? string.Empty) + KEY_SEPARATOR + (publisher ?? string.Empty);
        }

        private static string FindDataFile()
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(DATA_FILE_GUID);
            if (!string.IsNullOrEmpty(assetPath))
            {
                string fullPath = AssetUtils.AddProjectRoot(assetPath);
                if (File.Exists(fullPath)) return fullPath;
            }
            return null;
        }

        [Serializable]
        private class Entry
        {
            public string a; // asset name
            public string p; // publisher
        }
    }
}
