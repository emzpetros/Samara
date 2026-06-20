using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImpossibleRobert.Common;
using UnityEngine;

namespace AssetInventory
{
    /// <summary>
    /// Normalizes target paths when importing into a case-sensitive file system.
    /// </summary>
    public static class CaseSensitivePathGuard
    {
        public sealed class PathCandidate
        {
            public string Path { get; set; }
            public bool IsDirectory { get; set; }
            public object Tag { get; set; }
        }

        public sealed class NormalizationResult
        {
            public List<PathCandidate> Paths { get; } = new List<PathCandidate>();
            public List<string> CaseCollisions { get; } = new List<string>();
            public List<string> DuplicateConflicts { get; } = new List<string>();
        }

        public static bool IsCaseSensitiveFileSystem(string path)
        {
            // Keep this intentionally simple for now: Linux editor environments are the
            // practical case-sensitive target for this import path, while Windows and the
            // common macOS setup should keep the existing fast path.
            return Application.platform == RuntimePlatform.LinuxEditor;
        }

        public static NormalizationResult NormalizePaths(IEnumerable<PathCandidate> candidates)
        {
            NormalizationResult result = new NormalizationResult();
            if (candidates == null) return result;

            List<PathCandidate> orderedCandidates = candidates
                .Where(candidate => candidate != null && !string.IsNullOrEmpty(candidate.Path))
                .ToList();
            if (orderedCandidates.Count == 0) return result;

            Dictionary<string, Dictionary<string, string>> existingEntries = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            List<(PathCandidate Candidate, string CanonicalPath)> resolved = new List<(PathCandidate Candidate, string CanonicalPath)>();

            foreach (PathCandidate candidate in orderedCandidates)
            {
                string canonicalPath = ResolveCanonicalPath(candidate, existingEntries, result.CaseCollisions);
                resolved.Add((candidate, canonicalPath));
            }

            foreach (IGrouping<string, (PathCandidate Candidate, string CanonicalPath)> group in resolved.GroupBy(entry => entry.CanonicalPath, StringComparer.OrdinalIgnoreCase))
            {
                List<(PathCandidate Candidate, string CanonicalPath)> groupedEntries = group.ToList();
                (PathCandidate Candidate, string CanonicalPath) keeper = groupedEntries[0];
                result.Paths.Add(new PathCandidate
                {
                    Path = keeper.CanonicalPath,
                    IsDirectory = keeper.Candidate.IsDirectory,
                    Tag = keeper.Candidate.Tag
                });

                if (groupedEntries.Count <= 1 || groupedEntries.All(entry => entry.Candidate.IsDirectory)) continue;

                IEnumerable<string> blocked = groupedEntries.Skip(1).Select(entry => entry.Candidate.Path);
                result.DuplicateConflicts.Add(
                    $"Multiple import targets collapse to '{group.Key}' on a case-sensitive file system. Keeping '{keeper.Candidate.Path}' and skipping: {string.Join(", ", blocked)}");
            }

            return result;
        }

        public static List<PathCandidate> AdjustPaths(
            IEnumerable<PathCandidate> candidates,
            string targetRoot,
            string collisionLabel)
        {
            List<PathCandidate> orderedCandidates = candidates?.Where(candidate => candidate != null).ToList() ?? new List<PathCandidate>();
            if (orderedCandidates.Count == 0) return orderedCandidates;
            if (!IsCaseSensitiveFileSystem(targetRoot)) return orderedCandidates;

            NormalizationResult result = NormalizePaths(orderedCandidates);

            if (result.CaseCollisions.Count > 0)
            {
                IEnumerable<string> examples = result.CaseCollisions.Take(3);
                Debug.LogWarning($"Normalized {result.CaseCollisions.Count} case-only {collisionLabel} path collision(s) on a case-sensitive file system. {string.Join(" | ", examples)}");
            }

            foreach (string conflict in result.DuplicateConflicts)
            {
                Debug.LogError(conflict);
            }

            return result.Paths;
        }

        private static string ResolveCanonicalPath(
            PathCandidate candidate,
            Dictionary<string, Dictionary<string, string>> existingEntries,
            List<string> caseCollisions)
        {
            string normalizedPath = NormalizePath(candidate.Path);
            string root = NormalizeRoot(Path.GetPathRoot(normalizedPath));
            string relativePath = normalizedPath.Substring(root.Length);
            string[] segments = relativePath.Split(new[] {'/'}, StringSplitOptions.RemoveEmptyEntries);

            string currentPath = root;
            string originalPath = normalizedPath;

            for (int index = 0; index < segments.Length; index++)
            {
                bool isLastSegment = index == segments.Length - 1;
                bool representsDirectory = candidate.IsDirectory || !isLastSegment;
                string segment = segments[index];
                Dictionary<string, string> childNames = GetChildNames(currentPath, existingEntries);

                if (!childNames.TryGetValue(segment, out string canonicalSegment))
                {
                    canonicalSegment = segment;
                    childNames[segment] = segment;
                }
                else if (!string.Equals(canonicalSegment, segment, StringComparison.Ordinal))
                {
                    string candidateParent = TrimTrailingSlash(currentPath);
                    string normalizedParent = string.IsNullOrEmpty(candidateParent) ? root : candidateParent;
                    caseCollisions.Add($"'{originalPath}' -> '{CombineNormalized(normalizedParent, canonicalSegment, segments, index)}'");
                }

                currentPath = CombineNormalized(currentPath, canonicalSegment);

                if (representsDirectory)
                {
                    GetChildNames(currentPath, existingEntries);
                }
            }

            return currentPath;
        }

        private static Dictionary<string, string> GetChildNames(
            string parentPath,
            Dictionary<string, Dictionary<string, string>> existingEntries)
        {
            string cacheKey = NormalizePath(parentPath);
            if (existingEntries.TryGetValue(cacheKey, out Dictionary<string, string> cached)) return cached;

            Dictionary<string, string> childNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string filesystemPath = IOUtils.ToShortPath(parentPath);
            if (Directory.Exists(filesystemPath))
            {
                try
                {
                    foreach (string entry in Directory.EnumerateFileSystemEntries(filesystemPath))
                    {
                        string name = Path.GetFileName(entry);
                        if (string.IsNullOrEmpty(name) || childNames.ContainsKey(name)) continue;
                        childNames[name] = name;
                    }
                }
                catch (IOException)
                {
                    // Ignore and fall back to batch-local canonicalization only.
                }
                catch (UnauthorizedAccessException)
                {
                    // Ignore and fall back to batch-local canonicalization only.
                }
            }

            existingEntries[cacheKey] = childNames;
            return childNames;
        }

        private static string NormalizePath(string path)
        {
            string normalized = IOUtils.ToShortPath(path)?.Replace("\\", "/");
            if (string.IsNullOrEmpty(normalized)) return normalized;

            string root = NormalizeRoot(Path.GetPathRoot(normalized));
            string remainder = normalized.Substring(root.Length).Trim('/');
            return string.IsNullOrEmpty(remainder) ? root : root + remainder;
        }

        private static string NormalizeRoot(string root)
        {
            string normalizedRoot = IOUtils.ToShortPath(root)?.Replace("\\", "/") ?? string.Empty;
            if (string.IsNullOrEmpty(normalizedRoot)) return string.Empty;
            return normalizedRoot.EndsWith("/") ? normalizedRoot : normalizedRoot + "/";
        }

        private static string CombineNormalized(string basePath, string segment)
        {
            if (string.IsNullOrEmpty(basePath)) return segment;
            return TrimTrailingSlash(basePath) + "/" + segment;
        }

        private static string CombineNormalized(string parentPath, string segment, string[] segments, int index)
        {
            string resolved = CombineNormalized(parentPath, segment);
            for (int remainingIndex = index + 1; remainingIndex < segments.Length; remainingIndex++)
            {
                resolved = CombineNormalized(resolved, segments[remainingIndex]);
            }
            return resolved;
        }

        private static string TrimTrailingSlash(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.Length <= 1) return path;
            return path.EndsWith("/") ? path.TrimEnd('/') : path;
        }
    }
}