using System;
using System.Collections.Generic;
using System.Linq;

namespace AssetInventory
{
    internal enum HideRuleMode
    {
        Hide = 0,
        Include = 1
    }

    internal sealed class HideRuleSet
    {
        public HideRuleMode Mode { get; }
        public List<string> Rules { get; }

        public HideRuleSet(HideRuleMode mode, IEnumerable<string> rules)
        {
            Mode = mode;
            Rules = NormalizeRules(rules);
        }

        public static HideRuleSet Parse(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return new HideRuleSet(HideRuleMode.Hide, null);
            }

            string trimmedValue = rawValue.Trim();
            HideRuleMode mode = trimmedValue.StartsWith("!", StringComparison.Ordinal) ? HideRuleMode.Include : HideRuleMode.Hide;
            string ruleValue = mode == HideRuleMode.Include ? trimmedValue.Substring(1) : trimmedValue;
            return new HideRuleSet(mode, SplitRules(ruleValue));
        }

        public static string Serialize(HideRuleMode mode, IEnumerable<string> rules)
        {
            string serializedRules = string.Join(";", NormalizeRules(rules));
            if (mode == HideRuleMode.Include)
            {
                return "!" + serializedRules;
            }
            return serializedRules;
        }

        public bool Matches(string path)
        {
            foreach (string rule in Rules)
            {
                if (MatchesPattern(path, rule)) return true;
            }
            return false;
        }

        internal static List<string> NormalizeRules(IEnumerable<string> rules)
        {
            if (rules == null) return new List<string>();

            List<string> normalizedRules = rules
                .Select(rule => rule?.Trim())
                .Where(rule => !string.IsNullOrEmpty(rule))
                .ToList();

            List<string> result = new List<string>();
            for (int i = 0; i < normalizedRules.Count; i++)
            {
                string rule = normalizedRules[i];
                if (result.Contains(rule)) continue;

                bool coveredByFolderRule = false;
                for (int j = 0; j < normalizedRules.Count; j++)
                {
                    if (i == j) continue;
                    if (IsCoveredByFolderRule(rule, normalizedRules[j]))
                    {
                        coveredByFolderRule = true;
                        break;
                    }
                }

                if (!coveredByFolderRule)
                {
                    result.Add(rule);
                }
            }

            return result;
        }

        internal static List<string> SplitRules(string ruleValue)
        {
            if (string.IsNullOrWhiteSpace(ruleValue)) return new List<string>();

            string[] patterns = ruleValue.Split(';');
            return NormalizeRules(patterns);
        }

        internal static bool MatchesPattern(string path, string pattern)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrWhiteSpace(pattern)) return false;

            string trimmedPattern = pattern.Trim();
            if (trimmedPattern.StartsWith("*", StringComparison.Ordinal))
            {
                string ext = trimmedPattern.TrimStart('*').TrimStart('.');
                if (string.IsNullOrEmpty(ext)) return false;

                string fileExt = System.IO.Path.GetExtension(path);
                if (!string.IsNullOrEmpty(fileExt)) fileExt = fileExt.TrimStart('.');
                return string.Equals(fileExt, ext, StringComparison.OrdinalIgnoreCase);
            }

            if (trimmedPattern.Contains("/"))
            {
                if (trimmedPattern.EndsWith("/", StringComparison.Ordinal)
                    && string.Equals(path.TrimEnd('/'), trimmedPattern.TrimEnd('/'), StringComparison.Ordinal))
                {
                    return true;
                }

                return path.Contains(trimmedPattern);
            }

            return path.Contains("/" + trimmedPattern + "/") || path.StartsWith(trimmedPattern + "/");
        }

        private static bool IsCoveredByFolderRule(string rule, string folderRule)
        {
            if (string.IsNullOrEmpty(rule) || string.IsNullOrEmpty(folderRule)) return false;
            if (rule.StartsWith("*", StringComparison.Ordinal)) return false;
            if (!folderRule.EndsWith("/", StringComparison.Ordinal)) return false;

            return rule.StartsWith(folderRule, StringComparison.Ordinal);
        }
    }
}
