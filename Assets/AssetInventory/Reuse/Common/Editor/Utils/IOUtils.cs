using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace ImpossibleRobert.Common
{
    public static class IOUtils
    {
        private const string LONG_PATH_PREFIX = @"\\?\";
        private const string LONG_PATH_UNC_PREFIX = @"\\?\UNC\";

        // Cached drive list to avoid expensive DriveInfo.GetDrives() calls,
        // especially on Linux where it enumerates dozens of pseudo-filesystems
        private static readonly TimedCache<DriveInfo[]> _driveCache = new TimedCache<DriveInfo[]>();
        private static readonly TimeSpan _driveCacheTTL = TimeSpan.FromSeconds(30);

#if UNITY_EDITOR_LINUX
        private static readonly HashSet<string> _linuxPseudoFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "proc", "sysfs", "tmpfs", "devtmpfs", "devpts", "cgroup", "cgroup2",
            "pstore", "securityfs", "debugfs", "tracefs", "configfs", "fusectl",
            "hugetlbfs", "mqueue", "binfmt_misc", "autofs", "efivarfs", "bpf",
            "overlay", "squashfs", "ramfs", "rpc_pipefs", "nfsd", "nsfs", "fuse"
        };

        private static readonly string[] _linuxPseudoPrefixes =
        {
            "/proc", "/sys", "/dev", "/run", "/snap"
        };
#endif

        private static DriveInfo[] GetCachedDrives()
        {
            if (_driveCache.TryGetValue(out DriveInfo[] cached))
            {
                return cached;
            }

            List<DriveInfo> filtered = new List<DriveInfo>();
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!drive.IsReady) continue;

#if UNITY_EDITOR_LINUX
                        string root = drive.RootDirectory.FullName;

                        // skip pseudo-filesystem mount points
                        bool isPseudo = false;
                        for (int i = 0; i < _linuxPseudoPrefixes.Length; i++)
                        {
                            if (root.StartsWith(_linuxPseudoPrefixes[i], StringComparison.Ordinal))
                            {
                                isPseudo = true;
                                break;
                            }
                        }
                        if (isPseudo) continue;

                        // skip by filesystem format (DriveFormat maps to fs type on Linux)
                        try
                        {
                            if (_linuxPseudoFormats.Contains(drive.DriveFormat)) continue;
                        }
                        catch (IOException)
                        {
                            // DriveFormat can throw for inaccessible mounts
                        }
#endif
                        filtered.Add(drive);
                    }
                    catch (Exception)
                    {
                        // individual drive access can fail on Linux for inaccessible mounts
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Error enumerating drives: {e.Message}");
                if (_driveCache.TryGetValue(out DriveInfo[] fallback)) return fallback;
            }

            DriveInfo[] result = filtered.ToArray();
            _driveCache.SetValue(result, _driveCacheTTL);
            return result;
        }

        public static void InvalidateDriveCache()
        {
            _driveCache.Clear();
        }

        public static DriveInfo GetDriveInfoForPath(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return null;
            folderPath = ToShortPath(folderPath);

            folderPath = Path.GetFullPath(folderPath);

            // UNC paths (\\server\share or //server/share) are not enumerated by DriveInfo.GetDrives()
            if (folderPath.StartsWith(@"\\", StringComparison.Ordinal) ||
                folderPath.StartsWith("//", StringComparison.Ordinal))
            {
                return null;
            }

            DriveInfo bestMatch = null;

            foreach (DriveInfo drive in GetCachedDrives())
            {
                string root = drive.RootDirectory.FullName;
                if (folderPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    if (bestMatch == null || root.Length > bestMatch.RootDirectory.FullName.Length)
                    {
                        bestMatch = drive;
                    }
                }
            }
            if (bestMatch == null) Debug.LogError($"No drive found for the given path: {folderPath}");

            return bestMatch;
        }

        public static bool IsNetworkDrive(string folderPath)
        {
            DriveInfo drive = GetDriveInfoForPath(folderPath);
            if (drive == null) return false;

#if UNITY_EDITOR_LINUX
            // On Linux, network filesystems (NFS, CIFS/SMB) may report DriveType.Unknown
            // rather than DriveType.Network. Check DriveFormat as a fallback.
            if (drive.DriveType == DriveType.Network) return true;
            try
            {
                string format = drive.DriveFormat;
                return string.Equals(format, "nfs", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(format, "nfs4", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(format, "cifs", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(format, "smb", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(format, "smbfs", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(format, "sshfs", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(format, "9p", StringComparison.OrdinalIgnoreCase);
            }
            catch (IOException)
            {
                return false;
            }
#else
            return drive.DriveType == DriveType.Network;
#endif
        }

        public static bool IsSameDrive(string path1, string path2)
        {
            DriveInfo drive1 = GetDriveInfoForPath(path1);
            DriveInfo drive2 = GetDriveInfoForPath(path2);
            if (drive1 == null || drive2 == null) return false;
            return string.Equals(drive1.Name, drive2.Name, StringComparison.OrdinalIgnoreCase);
        }

        public static long GetFreeSpace(string folderPath)
        {
            try
            {
                DriveInfo drive = GetDriveInfoForPath(folderPath);
                if (drive == null) return -1;
                return drive.AvailableFreeSpace;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        public static string NormalizeRelative(string path)
        {
            string[] parts = path.Split(new[] {'/', '\\'}, StringSplitOptions.RemoveEmptyEntries);
            Stack<string> stack = new Stack<string>();
            foreach (string part in parts)
            {
                if (part == "..")
                {
                    stack.Pop();
                }
                else if (part != ".")
                {
                    stack.Push(part);
                }
            }
            return string.Join("/", stack.Reverse());
        }

        public static string ToLongPath(string path)
        {
            if (path == null) return null;

#if UNITY_EDITOR_WIN
            // see https://learn.microsoft.com/en-us/answers/questions/240603/c-app-long-path-support-on-windows-10-post-1607-ne
            path = path.Replace("/", "\\"); // in case later concatenations added /
            if (path.StartsWith(LONG_PATH_PREFIX, StringComparison.Ordinal)) return path;
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                string withoutSlashes = path.Substring(2);
                return $"{LONG_PATH_UNC_PREFIX}{withoutSlashes}";
            }
            
            // Convert relative paths to absolute before adding long path prefix
            // Windows requires absolute paths for \\?\ prefix
            if (!Path.IsPathRooted(path))
            {
                path = Path.GetFullPath(path);
            }
            
            return $"{LONG_PATH_PREFIX}{path}";
#else
            return path;
#endif
        }

        public static string ToShortPath(string path)
        {
#if UNITY_EDITOR_WIN
            if (path == null) return null;

            // handle UNC long-path prefix \\?\UNC\server\share\�
            if (path.StartsWith(LONG_PATH_UNC_PREFIX, StringComparison.Ordinal))
            {
                string withoutUncPrefix = path.Substring(LONG_PATH_UNC_PREFIX.Length);
                string uncPath = @"\\" + withoutUncPrefix;
                return uncPath; // UNC paths must use backslashes
            }
            return path.Replace(LONG_PATH_PREFIX, string.Empty).Replace("\\", "/");
#else
            return path;
#endif
        }

        public static bool PathContainsInvalidChars(string path)
        {
            return !string.IsNullOrEmpty(path) && path.IndexOfAny(Path.GetInvalidPathChars()) >= 0;
        }

        public static string RemoveInvalidChars(string path)
        {
            return string.Concat(path.Split(Path.GetInvalidFileNameChars()));
        }

        public static string MakeProjectRelative(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            string normalizedPath = path.Replace("\\", "/");
            if (normalizedPath == "Assets" || normalizedPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return normalizedPath;
            }

            string dataPath = CollapseLeadingSlashes(Application.dataPath.Replace("\\", "/"));
            string collapsedPath = CollapseLeadingSlashes(normalizedPath);

            StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (string.Equals(collapsedPath, dataPath, comparison))
            {
                return "Assets";
            }

            if (collapsedPath.Length > dataPath.Length &&
                collapsedPath[dataPath.Length] == '/' &&
                collapsedPath.StartsWith(dataPath, comparison))
            {
                return "Assets" + collapsedPath.Substring(dataPath.Length);
            }

            return normalizedPath;
        }

        private static string CollapseLeadingSlashes(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 2) return path;

            int start = 0;
            while (start < path.Length - 1 && path[start] == '/' && path[start + 1] == '/')
            {
                start++;
            }

            return start > 0 ? path.Substring(start) : path;
        }

        public static string CreateTempFolder()
        {
            string tempDirectoryPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tempDirectoryPath);

            return tempDirectoryPath;
        }

        public static string CreateTempFolder(string name, bool deleteIfExists = false)
        {
            string tempDirectoryPath = Path.Combine(Path.GetTempPath(), name);
            if (deleteIfExists && Directory.Exists(tempDirectoryPath)) Directory.Delete(tempDirectoryPath, true);
            if (!Directory.Exists(tempDirectoryPath)) Directory.CreateDirectory(tempDirectoryPath);

            return tempDirectoryPath;
        }

        public static async Task<List<string>> FindMatchesInBinaryFile(string filePath, IList<string> searchStrings, int bufferSize = 1 << 20)
        {
            int count = searchStrings.Count;
            byte[][] patterns = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                patterns[i] = Encoding.UTF8.GetBytes(searchStrings[i]);
            }

            AhoCorasick automaton = new AhoCorasick(patterns);
            HashSet<int> foundIds = new HashSet<int>();
            AhoCorasick.Node state = automaton.Root;

            byte[] buffer = new byte[bufferSize];

            using (FileStream fs = new FileStream(
                       filePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       bufferSize,
                       useAsync: true))
            {
                int bytesRead;
                while (foundIds.Count < count && (bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    automaton.Scan(buffer, bytesRead, foundIds, ref state);
                }

                List<string> result = new List<string>(foundIds.Count);
                foreach (int id in foundIds)
                {
                    result.Add(searchStrings[id]);
                }
                return result;
            }
        }

        private static bool ContainsPattern(byte[] data, int dataLen, byte[] pattern)
        {
            int patLen = pattern.Length;
            int end = dataLen - patLen;

            for (int i = 0; i <= end; i++)
            {
                bool match = true;
                for (int j = 0; j < patLen; j++)
                {
                    if (data[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return true;
            }

            return false;
        }

        public static string GetExtensionWithoutDot(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            string ext = Path.GetExtension(path);
            return string.IsNullOrEmpty(ext) ? string.Empty : ext.TrimStart('.');
        }

        public static string GetFileName(string path, bool returnOriginalOnError = true, bool quiet = true)
        {
            try
            {
                return Path.GetFileName(path);
            }
            catch (Exception e)
            {
                if (!quiet) Debug.LogError($"Illegal characters in path '{path}': {e}");
                return returnOriginalOnError ? path : null;
            }
        }

        public static string ReadFirstLine(string path)
        {
            string result = null;
            try
            {
                using (StreamReader reader = new StreamReader(ToLongPath(path)))
                {
                    result = reader.ReadLine();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error reading file '{path}': {e.Message}");
            }

            return result;
        }

        public static async Task<bool> TryCopyFile(string sourceFileName, string destFileName, bool overwrite, int retries = 5)
        {
            while (retries >= 0)
            {
                try
                {
                    File.Copy(sourceFileName, destFileName, overwrite);
                    return true;
                }
                catch (Exception e)
                {
                    string directoryName = Path.GetDirectoryName(destFileName);
                    if (!Directory.Exists(directoryName)) Directory.CreateDirectory(directoryName);

                    retries--;
                    if (retries >= 0)
                    {
                        await Task.Delay(500);
                    }
                    else
                    {
                        Debug.LogError($"Could not copy file '{sourceFileName}' to '{destFileName}': {e.Message}");
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Reads all text from a file using FileShare.Read to avoid locking the file.
        /// Required for Unity cache files that may be accessed by multiple editors.
        /// </summary>
        public static string ReadAllTextWithShare(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (StreamReader reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }

        /// <summary>
        /// Reads all bytes from a file using FileShare.Read to avoid locking the file.
        /// Required for Unity cache files that may be accessed by multiple editors.
        /// </summary>
        public static byte[] ReadAllBytesWithShare(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] buffer = new byte[stream.Length];
                stream.Read(buffer, 0, buffer.Length);
                return buffer;
            }
        }

        /// <summary>
        /// Reads all lines from a file using FileShare.Read to avoid locking the file.
        /// Required for Unity cache files that may be accessed by multiple editors.
        /// </summary>
        public static string[] ReadAllLines(string path)
        {
            List<string> lines = new List<string>();
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (StreamReader reader = new StreamReader(stream))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lines.Add(line);
                }
            }
            return lines.ToArray();
        }

        /// <summary>
        /// Reads all lines from a file using FileShare.Read to avoid locking the file (async version).
        /// Required for Unity cache files that may be accessed by multiple editors.
        /// </summary>
        public static async Task<string[]> ReadAllLinesAsync(string path)
        {
            List<string> lines = new List<string>();
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            using (StreamReader reader = new StreamReader(stream))
            {
                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    lines.Add(line);
                }
            }
            return lines.ToArray();
        }

        /// <summary>
        /// Reads all lines from a file with retry logic to handle file locking issues during parallel operations.
        /// Uses FileShare.ReadWrite to allow concurrent access, particularly when Unity is auto-creating meta files.
        /// </summary>
        public static async Task<string[]> TryReadAllLinesAsync(string path, int retries = 5)
        {
            string longPath = ToLongPath(path);
            while (retries >= 0)
            {
                try
                {
                    List<string> lines = new List<string>();
                    using (FileStream stream = new FileStream(longPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true))
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        string line;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            lines.Add(line);
                        }
                    }
                    return lines.ToArray();
                }
                catch (IOException e)
                {
                    // Often due to file locks from parallel operations or Unity auto-creating meta files; wait and retry
                    retries--;
                    if (retries >= 0)
                    {
                        await Task.Delay(500);
                    }
                    else
                    {
                        Debug.LogError($"Could not read file '{path}': {e.Message}");
                        return Array.Empty<string>();
                    }
                }
                catch (Exception e)
                {
                    retries--;
                    if (retries >= 0)
                    {
                        await Task.Delay(500);
                    }
                    else
                    {
                        Debug.LogError($"Could not read file '{path}': {e.Message}");
                        return Array.Empty<string>();
                    }
                }
            }

            return Array.Empty<string>();
        }

        /// <summary>
        /// Copies a file using FileShare.Read to avoid locking the source file.
        /// Required for Unity cache files that may be accessed by multiple editors.
        /// </summary>
        public static async Task<bool> CopyFileWithShare(string sourceFileName, string destFileName, bool overwrite, int retries = 5)
        {
            while (retries >= 0)
            {
                try
                {
                    // Ensure destination directory exists
                    string directoryName = Path.GetDirectoryName(destFileName);
                    if (!Directory.Exists(directoryName)) Directory.CreateDirectory(directoryName);

                    // Check if destination exists and we're not overwriting
                    if (File.Exists(destFileName) && !overwrite)
                    {
                        throw new IOException($"File already exists: {destFileName}");
                    }

                    // Copy file using FileStreams with FileShare.Read to avoid locking
                    using (FileStream sourceStream = new FileStream(sourceFileName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
                    using (FileStream destStream = new FileStream(destFileName, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        await sourceStream.CopyToAsync(destStream);
                    }

                    // Copy file attributes
                    File.SetAttributes(destFileName, File.GetAttributes(sourceFileName));

                    return true;
                }
                catch (Exception e)
                {
                    retries--;
                    if (retries >= 0)
                    {
                        await Task.Delay(500);
                    }
                    else
                    {
                        Debug.LogError($"Could not copy file '{sourceFileName}' to '{destFileName}': {e.Message}");
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Writes all lines to a file using FileShare.ReadWrite to allow concurrent access.
        /// Implements retry logic to handle file locking issues during parallel operations.
        /// </summary>
        public static async Task<bool> TryWriteAllLinesAsync(string path, IEnumerable<string> lines, int retries = 5)
        {
            string longPath = ToLongPath(path);
            while (retries >= 0)
            {
                try
                {
                    // Ensure destination directory exists
                    string directoryName = Path.GetDirectoryName(longPath);
                    if (!Directory.Exists(directoryName)) Directory.CreateDirectory(directoryName);

                    // Write file using FileStream with FileShare.ReadWrite to allow concurrent access
                    using (FileStream stream = new FileStream(longPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 81920, useAsync: true))
                    using (StreamWriter writer = new StreamWriter(stream, Encoding.UTF8))
                    {
                        foreach (string line in lines)
                        {
                            await writer.WriteLineAsync(line);
                        }
                    }

                    return true;
                }
                catch (IOException e)
                {
                    // Often due to file locks from parallel operations; wait and retry
                    retries--;
                    if (retries >= 0)
                    {
                        await Task.Delay(500);
                    }
                    else
                    {
                        Debug.LogError($"Could not write file '{path}': {e.Message}");
                    }
                }
                catch (Exception e)
                {
                    retries--;
                    if (retries >= 0)
                    {
                        await Task.Delay(500);
                    }
                    else
                    {
                        Debug.LogError($"Could not write file '{path}': {e.Message}");
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Writes all lines to a file using FileShare.ReadWrite to allow concurrent access.
        /// Implements retry logic to handle file locking issues during parallel operations.
        /// </summary>
        public static bool TryWriteAllLines(string path, IEnumerable<string> lines, int retries = 5)
        {
            string longPath = ToLongPath(path);
            while (retries >= 0)
            {
                try
                {
                    // Ensure destination directory exists
                    string directoryName = Path.GetDirectoryName(longPath);
                    if (!Directory.Exists(directoryName)) Directory.CreateDirectory(directoryName);

                    // Write file using FileStream with FileShare.ReadWrite to allow concurrent access
                    using (FileStream stream = new FileStream(longPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 81920))
                    using (StreamWriter writer = new StreamWriter(stream, Encoding.UTF8))
                    {
                        foreach (string line in lines)
                        {
                            writer.WriteLine(line);
                        }
                    }

                    return true;
                }
                catch (IOException e)
                {
                    // Often due to file locks from parallel operations; wait and retry
                    retries--;
                    if (retries >= 0)
                    {
                        Thread.Sleep(500);
                    }
                    else
                    {
                        Debug.LogError($"Could not write file '{path}': {e.Message}");
                    }
                }
                catch (Exception e)
                {
                    retries--;
                    if (retries >= 0)
                    {
                        Thread.Sleep(500);
                    }
                    else
                    {
                        Debug.LogError($"Could not write file '{path}': {e.Message}");
                    }
                }
            }

            return false;
        }

        public static bool TryDeleteFile(string path)
        {
            // adjust attributes to ensure deletion
            try { File.SetAttributes(path, FileAttributes.Normal); }
            catch
            { /* ignore */
            }

            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Attempts to delete a file with retry logic to handle file locking issues.
        /// Useful when Unity may have auto-created and locked a meta file.
        /// </summary>
        public static async Task<bool> TryDeleteFileAsync(string path, int retries = 5)
        {
            if (!File.Exists(path)) return true;

            while (retries >= 0)
            {
                // adjust attributes to ensure deletion
                try { File.SetAttributes(path, FileAttributes.Normal); }
                catch
                { /* ignore */
                }

                try
                {
                    File.Delete(path);
                    return true;
                }
                catch (Exception)
                {
                    retries--;
                    if (retries >= 0)
                    {
                        await Task.Delay(200);
                    }
                }
            }

            return false;
        }

        public static async Task<bool> DeleteFileOrDirectory(string path, int retries = 3)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;

            string targetPath = ToLongPath(path);

            while (retries >= 0)
            {
                try
                {
                    // Delete file
                    if (File.Exists(targetPath))
                    {
                        try { File.SetAttributes(targetPath, FileAttributes.Normal); }
                        catch
                        { /* ignore */
                        }
                        File.Delete(targetPath);
                        return true;
                    }

                    // Delete directory (recursive)
                    if (Directory.Exists(targetPath))
                    {
                        ClearReadOnlyAttributes(targetPath);
                        Directory.Delete(targetPath, true);
                        return true;
                    }

                    // Path already gone
                    return true;
                }
                catch (UnauthorizedAccessException)
                {
                    // Clear attributes and retry
                    try
                    {
                        if (Directory.Exists(targetPath)) ClearReadOnlyAttributes(targetPath);
                        if (File.Exists(targetPath)) File.SetAttributes(targetPath, FileAttributes.Normal);
                    }
                    catch
                    { /* best effort */
                    }

                    retries--;
                    if (retries >= 0) await Task.Delay(500);
                }
                catch (IOException)
                {
                    // Often due to file locks; wait and retry
                    retries--;
                    if (retries >= 0) await Task.Delay(500);
                }
                catch
                {
                    // Do not swallow unexpected exceptions endlessly
                    break;
                }
            }

            return !File.Exists(targetPath) && !Directory.Exists(targetPath);
        }

        private static void ClearReadOnlyAttributes(string directoryPath)
        {
            // Clear file attributes first
            try
            {
                foreach (string file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); }
                    catch
                    { /* ignore */
                    }
                }
            }
            catch
            { /* ignore */
            }

            // Clear directory attributes (including the root)
            try
            {
                foreach (string dir in Directory.EnumerateDirectories(directoryPath, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(dir, FileAttributes.Normal); }
                    catch
                    { /* ignore */
                    }
                }
            }
            catch
            { /* ignore */
            }

            try { File.SetAttributes(directoryPath, FileAttributes.Normal); }
            catch
            { /* ignore */
            }
        }

        // Regex version
        public static IEnumerable<string> GetFiles(string path, string searchPatternExpression = "", SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            Regex reSearchPattern = new Regex(searchPatternExpression, RegexOptions.IgnoreCase);
            return Directory.EnumerateFiles(path, "*", searchOption)
                .Where(file => reSearchPattern.IsMatch(Path.GetExtension(file)));
        }

        // Takes multiple patterns and executes in parallel
        public static IEnumerable<string> GetFiles(string path, IEnumerable<string> searchPatterns, SearchOption searchOption = SearchOption.TopDirectoryOnly, bool allowParallel = true)
        {
            if (path == null) return Enumerable.Empty<string>();

            if (allowParallel)
            {
                return searchPatterns.AsParallel()
                    .SelectMany(searchPattern => Directory.EnumerateFiles(path, searchPattern, searchOption));
            }
            else
            {
                return searchPatterns
                    .SelectMany(searchPattern => Directory.EnumerateFiles(path, searchPattern, searchOption));
            }
        }

        public static IEnumerable<string> GetFilesSafe(string rootPath, string searchPattern, SearchOption searchOption = SearchOption.AllDirectories)
        {
            Queue<string> dirs = new Queue<string>();
            dirs.Enqueue(rootPath);

            while (dirs.Count > 0)
            {
                string currentDir = dirs.Dequeue();
                string[] subDirs;
                string[] files;

                // Try to get files in the current directory
                try
                {
                    files = Directory.GetFiles(currentDir, searchPattern);
                }
                catch (Exception)
                {
                    // Skip this directory if access is denied
                    // Skip if the directory is not found
                    // Skip if timeout happens
                    continue;
                }

                foreach (string file in files)
                {
                    yield return file;
                }

                if (searchOption == SearchOption.TopDirectoryOnly) continue;

                // Try to get subdirectories
                try
                {
                    subDirs = Directory.GetDirectories(currentDir);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (string subDir in subDirs)
                {
                    dirs.Enqueue(subDir);
                }
            }
        }

        public static bool IsDirectoryEmpty(string path)
        {
            if (path == null) return true;
            return !Directory.EnumerateFileSystemEntries(path).Any();
        }

        public static bool IsSameDirectory(string path1, string path2)
        {
            DirectoryInfo di1 = new DirectoryInfo(path1);
            DirectoryInfo di2 = new DirectoryInfo(path2);

            return string.Equals(di1.FullName, di2.FullName, StringComparison.OrdinalIgnoreCase);
        }

        public static void CopyDirectory(string sourceDir, string destDir, bool includeSubDirs = true)
        {
            DirectoryInfo dir = new DirectoryInfo(sourceDir);
            DirectoryInfo[] dirs = dir.GetDirectories();
            Directory.CreateDirectory(destDir);

            FileInfo[] files = dir.GetFiles();
            foreach (FileInfo file in files)
            {
                string tempPath = Path.Combine(destDir, file.Name);
                file.CopyTo(tempPath, true);
            }

            if (includeSubDirs)
            {
                foreach (DirectoryInfo subDir in dirs)
                {
                    string tempPath = Path.Combine(destDir, subDir.Name);
                    CopyDirectory(subDir.FullName, tempPath, includeSubDirs);
                }
            }
        }

        // RW: cache folder sizes keyed by full path; reused only while folder's LastWriteTime is unchanged AND the entry is still fresh.
        private struct FolderSizeEntry
        {
            public DateTime LastWriteUtc;
            public long Size;
            public DateTime CachedAt;
        }
        private static readonly ConcurrentDictionary<string, FolderSizeEntry> s_folderSizeCache = new ConcurrentDictionary<string, FolderSizeEntry>(StringComparer.OrdinalIgnoreCase);
        private const int FOLDER_SIZE_CACHE_TTL_SECONDS = 60;

        public static void InvalidateFolderSizeCache(string folder = null)
        {
            if (string.IsNullOrEmpty(folder)) s_folderSizeCache.Clear();
            else s_folderSizeCache.TryRemove(folder, out _);
        }

        public static async Task<long> GetFolderSize(string folder, bool async = true)
        {
            if (!Directory.Exists(folder)) return 0;
            DirectoryInfo dirInfo = new DirectoryInfo(folder);

            // memoization: skip walk if cache is fresh and folder's LastWriteTime hasn't changed
            DateTime nowUtc = DateTime.UtcNow;
            DateTime lastWriteUtc;
            try { lastWriteUtc = dirInfo.LastWriteTimeUtc; }
            catch { lastWriteUtc = DateTime.MinValue; }
            if (s_folderSizeCache.TryGetValue(folder, out FolderSizeEntry cached)
                && cached.LastWriteUtc == lastWriteUtc
                && (nowUtc - cached.CachedAt).TotalSeconds < FOLDER_SIZE_CACHE_TTL_SECONDS)
            {
                return cached.Size;
            }

            try
            {
                long size;
                if (async)
                {
                    // FIXME: this can crash Unity
                    size = await Task.Run(() => dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length));
                }
                else
                {
                    size = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
                }
                s_folderSizeCache[folder] = new FolderSizeEntry { LastWriteUtc = lastWriteUtc, Size = size, CachedAt = nowUtc };
                return size;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Returns a combined path with unified slashes
        /// </summary>
        /// <returns></returns>
        public static string PathCombine(params string[] path)
        {
            return Path.GetFullPath(Path.Combine(path));
        }

        /// <summary>
        /// Determines whether the provided path is a filesystem root.
        /// Supports Windows drive roots (e.g., C:\, D:\), bare drive letters (e.g., E:),
        /// and Unix-style roots (/). UNC roots are treated as roots as well.
        /// </summary>
        public static bool IsRootPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            try
            {
                // Normalize to a full path when possible
                string fullPath = Path.GetFullPath(path);
                string rootPath = Path.GetPathRoot(fullPath);

                bool isAtRoot = !string.IsNullOrEmpty(rootPath)
                    && string.Equals(fullPath.TrimEnd('\\', '/'), rootPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

                // Also catch bare drive letters like "E:" (without trailing slash)
                string normalizedRaw = path.Replace('/', '\\').TrimEnd('\\', '/');
                bool isDriveLetterOnly = normalizedRaw.Length == 2 && normalizedRaw[1] == ':'
                    && char.IsLetter(normalizedRaw[0]);

                return isAtRoot || isDriveLetterOnly;
            }
            catch
            {
                // If Path.GetFullPath throws (illegal chars), treat as not root
                return false;
            }
        }

        /// <summary>
        /// Gets the relative path from one path to another.
        /// For Unity 2021.2+, uses the built-in Path.GetRelativePath.
        /// For older versions, provides a custom implementation.
        /// </summary>
        /// <param name="relativeTo">The source path the result should be relative to</param>
        /// <param name="path">The destination path</param>
        /// <returns>The relative path, or the original path if it can't be made relative</returns>
        public static string GetRelativePath(string relativeTo, string path)
        {
            return Path.GetRelativePath(relativeTo, path);
        }

        /// <summary>
        /// Normalizes a remote path (FTP/SFTP) to use forward slashes.
        /// Returns "/" for empty or null paths.
        /// </summary>
        public static string NormalizeRemotePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "/";
            }

            path = path.Replace("\\", "/");

            // Collapse consecutive slashes
            while (path.Contains("//"))
            {
                path = path.Replace("//", "/");
            }

            // Remove trailing slash (unless root)
            if (path.Length > 1) path = path.TrimEnd('/');

            // Ensure leading slash
            if (!path.StartsWith("/")) path = "/" + path;

            return path;
        }

        /// <summary>
        /// Combines remote path segments into a single normalized remote path.
        /// </summary>
        public static string CombineRemotePath(params string[] segments)
        {
            if (segments == null || segments.Length == 0) return "/";

            string combined = string.Join("/", segments.Where(s => !string.IsNullOrEmpty(s)));
            return NormalizeRemotePath(combined);
        }

        /// <summary>
        /// Gets the parent path of a given remote path (FTP/SFTP).
        /// Returns null for root path, "/" for paths with no parent directory.
        /// </summary>
        public static string GetRemoteParentPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
            {
                return null;
            }

            path = path.TrimEnd('/');
            int lastSlash = path.LastIndexOf('/');

            if (lastSlash <= 0)
            {
                return "/";
            }

            return path.Substring(0, lastSlash);
        }

        #region Path Hierarchy Utilities

        /// <summary>
        /// Normalizes a filesystem path to use forward slashes and removes trailing slashes.
        /// Converts to absolute full path for consistent comparison.
        /// </summary>
        /// <param name="path">The path to normalize</param>
        /// <returns>Normalized path with forward slashes and no trailing slash, or null/empty if input is null/empty</returns>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            try
            {
                // Get full path to normalize
                string fullPath = Path.GetFullPath(path);

                // Normalize slashes to forward slashes
                fullPath = fullPath.Replace("\\", "/");

                // Remove trailing slashes for consistent comparison
                fullPath = fullPath.TrimEnd('/');

                return fullPath;
            }
            catch
            {
                // If path is invalid, return as-is with basic normalization
                return path.Replace("\\", "/").TrimEnd('/');
            }
        }

        /// <summary>
        /// Gets the parent directory path of the specified path.
        /// </summary>
        /// <param name="path">The path to get the parent of</param>
        /// <returns>The parent directory path, or null if the path is a root or invalid</returns>
        public static string GetParentPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            try
            {
                // Check if already at root
                if (IsRootPath(path)) return null;

                // Get parent directory
                string parent = Path.GetDirectoryName(path);
                
                if (string.IsNullOrEmpty(parent)) return null;

                // Normalize the result
                return NormalizePath(parent);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Calculates the depth of a path (number of directory levels from root).
        /// For example: "C:/" = 0, "C:/folder" = 1, "C:/folder/subfolder" = 2
        /// </summary>
        /// <param name="path">The path to calculate depth for</param>
        /// <returns>The depth level, or 0 for root paths or invalid paths</returns>
        public static int GetPathDepth(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;

            try
            {
                string normalized = NormalizePath(path);
                if (string.IsNullOrEmpty(normalized)) return 0;

                // Check if root
                if (IsRootPath(normalized)) return 0;

                // Get the root to calculate relative depth
                string root = Path.GetPathRoot(normalized);
                if (string.IsNullOrEmpty(root)) return 0;

                // Normalize root
                root = root.Replace("\\", "/").TrimEnd('/');

                // Remove root from path to get relative portion
                string relativePart = normalized;
                if (normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    relativePart = normalized.Substring(root.Length).TrimStart('/');
                }

                if (string.IsNullOrEmpty(relativePart)) return 0;

                // Count forward slashes in relative part + 1
                return relativePart.Split('/').Length;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Determines whether a path is a child (at any level) of another path.
        /// </summary>
        /// <param name="childPath">The potential child path</param>
        /// <param name="parentPath">The potential parent path</param>
        /// <returns>True if childPath is a descendant of parentPath (but not equal to it)</returns>
        public static bool IsChildPath(string childPath, string parentPath)
        {
            if (string.IsNullOrEmpty(childPath) || string.IsNullOrEmpty(parentPath)) return false;

            try
            {
                string normalizedChild = NormalizePath(childPath);
                string normalizedParent = NormalizePath(parentPath);

                if (string.IsNullOrEmpty(normalizedChild) || string.IsNullOrEmpty(normalizedParent)) return false;

                // Cannot be child of itself
                if (string.Equals(normalizedChild, normalizedParent, StringComparison.OrdinalIgnoreCase)) return false;

                // Check if child starts with parent path followed by a separator
                return normalizedChild.StartsWith(normalizedParent + "/", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Determines whether a path is a direct child (first-level subdirectory) of another path.
        /// For example: "C:/folder/child" is a direct child of "C:/folder", 
        /// but "C:/folder/child/grandchild" is not.
        /// </summary>
        /// <param name="childPath">The potential child path</param>
        /// <param name="parentPath">The potential parent path</param>
        /// <returns>True if childPath is exactly one level below parentPath</returns>
        public static bool IsDirectChildPath(string childPath, string parentPath)
        {
            // First check if it's a child at all
            if (!IsChildPath(childPath, parentPath)) return false;

            try
            {
                int childDepth = GetPathDepth(childPath);
                int parentDepth = GetPathDepth(parentPath);

                // Direct child means exactly one level deeper
                return (childDepth - parentDepth) == 1;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Determines whether a path is a grandchild (second-level subdirectory) of another path.
        /// For example: "C:/folder/child/grandchild" is a grandchild of "C:/folder",
        /// but "C:/folder/child" is not.
        /// </summary>
        /// <param name="grandchildPath">The potential grandchild path</param>
        /// <param name="grandparentPath">The potential grandparent path</param>
        /// <returns>True if grandchildPath is exactly two levels below grandparentPath</returns>
        public static bool IsGrandchildPath(string grandchildPath, string grandparentPath)
        {
            // First check if it's a child at all
            if (!IsChildPath(grandchildPath, grandparentPath)) return false;

            try
            {
                int grandchildDepth = GetPathDepth(grandchildPath);
                int grandparentDepth = GetPathDepth(grandparentPath);

                // Grandchild means exactly two levels deeper
                return (grandchildDepth - grandparentDepth) == 2;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        public static string ExecuteCommand(string command, string arguments, string workingDirectory = "", bool waitForExit = true, bool createWindow = false)
        {
            ProcessStartInfo processStartInfo = new ProcessStartInfo(command, arguments)
            {
                RedirectStandardOutput = !createWindow,
                UseShellExecute = createWindow,
                CreateNoWindow = !createWindow,
                WorkingDirectory = workingDirectory
            };

            try
            {
                using (Process process = new Process {StartInfo = processStartInfo})
                {
                    process.Start();
                    string result = null;
                    if (!createWindow) result = process.StandardOutput.ReadToEnd();
                    if (waitForExit) process.WaitForExit();
                    return result;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error executing command '{command}': {e.Message}");
                return null;
            }
        }

        public static async Task<bool> DownloadFile(Uri uri, string targetFile)
        {
            UnityWebRequest request = UnityWebRequest.Get(uri);
            UnityWebRequestAsyncOperation operation = request.SendWebRequest();

            while (!operation.isDone)
            {
                await Task.Yield();
            }

            if (request.result == UnityWebRequest.Result.ConnectionError || request.result == UnityWebRequest.Result.ProtocolError)
            {
                Debug.LogError(request.error);
                return false;
            }

            byte[] data = request.downloadHandler.data;
            await File.WriteAllBytesAsync(targetFile, data);

            return true;
        }
    }
}
