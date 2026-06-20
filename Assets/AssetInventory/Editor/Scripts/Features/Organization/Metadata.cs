using System;
using System.Collections.Generic;
using System.Linq;

namespace AssetInventory
{
    public static class Metadata
    {
        public static event Action OnDefinitionsChanged;

        internal static IEnumerable<MetadataInfo> Metadatas
        {
            get
            {
                if (_metas == null) LoadAssignments();
                return _metas;
            }
        }
        private static List<MetadataInfo> _metas;

        internal static int MetadataHash { get; private set; }

        public static List<MetadataDefinition> LoadDefinitions()
        {
            List<MetadataDefinition> defs = DBAdapter.DB.Table<MetadataDefinition>().AsEnumerable().OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            defs = EnsurePredefinedField(defs, MetadataDefinition.FIELD_COMMENTS, MetadataDefinition.DataType.BigText);
            defs = EnsurePredefinedField(defs, MetadataDefinition.FIELD_HIDE, MetadataDefinition.DataType.List);
            defs = EnsurePredefinedField(defs, MetadataDefinition.FIELD_MAX_BACKUPS, MetadataDefinition.DataType.Number);
            return defs;
        }

        private static List<MetadataDefinition> EnsurePredefinedField(List<MetadataDefinition> defs, string name, MetadataDefinition.DataType type)
        {
            MetadataDefinition existing = defs.FirstOrDefault(d => d.Name == name);
            if (existing != null)
            {
                if (existing.Type != type)
                {
                    existing.Type = type;
                    DBAdapter.DB.Update(existing);
                }
                return defs;
            }

            MetadataDefinition def = new MetadataDefinition(name);
            def.Type = type;
            DBAdapter.DB.Insert(def);
            defs.Add(def);
            defs = defs.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return defs;
        }

        public static MetadataDefinition AddDefinition(MetadataDefinition def)
        {
            def.Name = def.Name.Trim();
            if (string.IsNullOrWhiteSpace(def.Name)) return null;

            if (def.Id > 0)
            {
                DBAdapter.DB.Update(def);
            }
            else
            {
                DBAdapter.DB.Insert(def);
            }
            OnDefinitionsChanged?.Invoke();

            return def;
        }

        public static void DeleteDefinition(MetadataDefinition def)
        {
            if (def.IsPredefined) return;

            DBAdapter.DB.Execute("DELETE from MetadataAssignment where MetadataId=?", def.Id);
            DBAdapter.DB.Delete<MetadataDefinition>(def.Id);

            OnDefinitionsChanged?.Invoke();
        }

        public static bool AddAssignment(int targetId, int id, MetadataAssignment.Target target, bool fromAssetStore = false)
        {
            MetadataAssignment existingA = DBAdapter.DB.Find<MetadataAssignment>(t => t.MetadataId == id && t.TargetId == targetId && t.MetadataTarget == target);
            if (existingA != null) return false; // already added

            MetadataAssignment newAssignment = new MetadataAssignment(id, target, targetId);
            DBAdapter.DB.Insert(newAssignment);

            return true;
        }

        public static bool AddAssignment(AssetInfo info, int id, MetadataAssignment.Target target, bool byUser = false)
        {
            if (!AddAssignment(target == MetadataAssignment.Target.Asset ? info.Id : info.AssetId, id, target)) return false;

            LoadAssignments(info);

            return true;
        }

        public static void RemoveAssignment(AssetInfo info, MetadataInfo metadataInfo, bool autoReload = true, bool byUser = false)
        {
            DBAdapter.DB.Delete<MetadataAssignment>(metadataInfo.Id);

            if (autoReload) LoadAssignments(info);
        }

        internal static void LoadAssignments(AssetInfo info = null, bool triggerEvents = true)
        {
            string dataQuery = "SELECT *, MetadataAssignment.Id as Id, MetadataDefinition.Id as DefinitionId from MetadataAssignment inner join MetadataDefinition on MetadataDefinition.Id = MetadataAssignment.MetadataId order by MetadataTarget, TargetId, MetadataAssignment.Id";
            _metas = DBAdapter.DB.Query<MetadataInfo>($"{dataQuery}").ToList();

            MetadataHash = UnityEngine.Random.Range(0, int.MaxValue);

            info?.SetMetadataDirty();
            if (triggerEvents) OnDefinitionsChanged?.Invoke();
        }

        public static List<MetadataInfo> GetPackageMetadata(int assetId)
        {
            return Metadatas?.Where(t => t.MetadataTarget == MetadataAssignment.Target.Package && t.TargetId == assetId)
                .OrderBy(t => t.Id).ToList();
        }

        public static Dictionary<int, List<string>> GetHidePatterns()
        {
            Dictionary<int, List<string>> result = new Dictionary<int, List<string>>();

            foreach (KeyValuePair<int, HideRuleSet> kvp in GetHideRuleSets())
            {
                if (kvp.Value.Mode == HideRuleMode.Hide && kvp.Value.Rules.Count > 0)
                {
                    result[kvp.Key] = new List<string>(kvp.Value.Rules);
                }
            }
            return result;
        }

        internal static Dictionary<int, HideRuleSet> GetHideRuleSets()
        {
            Dictionary<int, HideRuleSet> result = new Dictionary<int, HideRuleSet>();
            if (Metadatas == null) return result;

            foreach (MetadataInfo meta in Metadatas)
            {
                if (meta.Name != MetadataDefinition.FIELD_HIDE) continue;
                if (meta.MetadataTarget != MetadataAssignment.Target.Package) continue;
                if (string.IsNullOrWhiteSpace(meta.StringValue)) continue;

                HideRuleSet ruleSet = HideRuleSet.Parse(meta.StringValue);
                if (ruleSet.Mode == HideRuleMode.Hide && ruleSet.Rules.Count == 0) continue;

                result[meta.TargetId] = ruleSet;
            }
            return result;
        }

        internal static bool TryGetPackageMaxBackups(int assetId, out int maxBackups)
        {
            maxBackups = 0;
            MetadataInfo meta = Metadatas?.FirstOrDefault(t =>
                t.MetadataTarget == MetadataAssignment.Target.Package &&
                t.TargetId == assetId &&
                t.Name == MetadataDefinition.FIELD_MAX_BACKUPS);
            if (meta == null) return false;

            maxBackups = Math.Max(0, meta.IntValue);
            return true;
        }
    }
}
