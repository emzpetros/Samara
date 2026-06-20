using Automator;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Renci.SshNet.Sftp;
using UnityEngine;

namespace AssetInventory
{
    [Serializable]
    public sealed class FTPDeleteStep : FTPActionStep
    {
        public FTPDeleteStep()
        {
            Key = "FTPDelete";
            Name = "FTP Delete";
            Description = "Delete a folder from an FTP or SFTP server.";
            Category = ActionCategory.FilesAndFolders;

            // Add FTP/SFTP server connection parameter
            AddServerParameter();

            // Target directory parameter
            Parameters.Add(new StepParameter
            {
                Name = "Target",
                Description = "Remote directory path on the FTP/SFTP server to delete (e.g., /public_html/files or /uploads).",
                DefaultValue = new ParameterValue("/folder_name")
            });

            // Exclude paths parameter
            Parameters.Add(new StepParameter
            {
                Name = "Exclude",
                Description = "Comma-separated list of relative directory paths to exclude from deletion (e.g., bin,logs,bin/test). Paths are relative to the Target directory.",
                Optional = true
            });
        }

        public override async Task Run(List<ParameterValue> parameters)
        {
            // Get parameters
            string connectionId = parameters[0].stringValue;
            string targetDirectory = parameters[1].stringValue;
            string excludePaths = parameters[2].stringValue;

            // Parse and normalize exclude paths
            List<string> excludeList = new List<string>();
            if (!string.IsNullOrEmpty(excludePaths))
            {
                excludeList = excludePaths.Split(',')
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Select(p => p.Trim('/', '\\').Replace('\\', '/'))
                    .ToList();
            }

            // Get and validate connection
            if (!TryGetConnection(connectionId, out FTPConnection connection, out string password))
            {
                throw new Exception("Failed to get FTP connection. Check that the connection is properly configured.");
            }

            // Validate target directory
            if (string.IsNullOrEmpty(targetDirectory))
            {
                throw new ArgumentException("Target directory cannot be empty.");
            }

            // Prevent deleting root directory
            if (targetDirectory == "/" || targetDirectory == "\\")
            {
                throw new InvalidOperationException("Cannot delete root directory for safety reasons.");
            }

            string protocolName = GetProtocolName(connection);
            Debug.Log($"Starting deletion of '{protocolName}://{connection.host}:{connection.port}{targetDirectory}'");
            if (excludeList.Count > 0)
            {
                Debug.Log($"Excluding paths: {string.Join(", ", excludeList)}");
            }

            // Perform deletion based on protocol
            if (connection.protocol == FTPConnection.FTPProtocol.SFTP)
            {
                await DeleteViaSFTP(connection, targetDirectory, password, excludeList);
            }
            else
            {
                await DeleteViaFTP(connection, targetDirectory, password, excludeList);
            }

            Debug.Log($"{protocolName.ToUpper()} deletion completed successfully.");
        }

        private bool IsPathExcluded(string remotePath, string targetDirectory, List<string> excludeList)
        {
            if (excludeList == null || excludeList.Count == 0) return false;

            // Normalize paths for comparison (remove leading and trailing slashes, convert backslashes)
            string normalizedRemote = remotePath.Replace('\\', '/').Trim('/');
            string normalizedTarget = targetDirectory.Replace('\\', '/').Trim('/');

            // Get relative path by stripping the target directory
            string relativePath = normalizedRemote;
            if (normalizedRemote.StartsWith(normalizedTarget + "/"))
            {
                relativePath = normalizedRemote.Substring(normalizedTarget.Length + 1);
            }
            else if (normalizedRemote == normalizedTarget)
            {
                relativePath = "";
            }

            // Check if the relative path matches or starts with any excluded path
            foreach (string excludePath in excludeList)
            {
                string normalizedExclude = excludePath.Replace('\\', '/').TrimEnd('/');

                // Exact match
                if (relativePath == normalizedExclude)
                {
                    return true;
                }

                // Path is a subdirectory of excluded path
                if (relativePath.StartsWith(normalizedExclude + "/"))
                {
                    return true;
                }
            }

            return false;
        }

        private async Task DeleteViaFTP(FTPConnection connection, string targetDirectory, string password, List<string> excludeList)
        {
            await Task.Run(() =>
            {
                try
                {
                    // Recursively delete directory contents
                    DeleteFTPDirectoryRecursive(connection, targetDirectory, password, excludeList, targetDirectory);
                    Debug.Log($"FTP deletion completed for: {targetDirectory}");
                }
                finally
                {
                    ResetSslValidation();
                }
            });
        }

        private void DeleteFTPDirectoryRecursive(FTPConnection connection, string remotePath, string password, List<string> excludeList, string targetDirectory)
        {
            try
            {
                if (AI.Actions.CancellationRequested) return;

                // List directory contents
                FtpWebRequest listRequest = CreateFtpRequest(connection, remotePath, password);
                listRequest.Method = WebRequestMethods.Ftp.ListDirectoryDetails;

                List<string> directories = new List<string>();
                List<string> files = new List<string>();

                using (FtpWebResponse listResponse = (FtpWebResponse)listRequest.GetResponse())
                using (StreamReader reader = new StreamReader(listResponse.GetResponseStream()))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        // Parse directory listing (Unix-style)
                        // Format: drwxr-xr-x 2 user group 4096 Jan 1 12:00 dirname
                        string[] parts = line.Split(new[] {' '}, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 9) continue;

                        string name = string.Join(" ", parts.Skip(8));
                        if (name == "." || name == "..") continue;

                        string itemPath = remotePath.TrimEnd('/') + "/" + name;

                        // Check if it's a directory (starts with 'd')
                        if (line.StartsWith("d"))
                        {
                            directories.Add(itemPath);
                        }
                        else
                        {
                            files.Add(itemPath);
                        }
                    }
                }

                // Track if any children were skipped
                bool hasSkippedChildren = false;

                // Delete files first
                foreach (string filePath in files)
                {
                    if (AI.Actions.CancellationRequested) return;

                    // Check if file should be excluded
                    if (IsPathExcluded(filePath, targetDirectory, excludeList))
                    {
                        Debug.Log($"Skipping excluded file: {filePath}");
                        hasSkippedChildren = true;
                        continue;
                    }

                    DeleteFTPFile(connection, filePath, password);
                }

                // Recursively delete subdirectories
                foreach (string dirPath in directories)
                {
                    if (AI.Actions.CancellationRequested) return;

                    // Check if directory should be excluded
                    if (IsPathExcluded(dirPath, targetDirectory, excludeList))
                    {
                        Debug.Log($"Skipping excluded directory: {dirPath}");
                        hasSkippedChildren = true;
                        continue;
                    }

                    DeleteFTPDirectoryRecursive(connection, dirPath, password, excludeList, targetDirectory);
                }

                // Delete the directory itself only if:
                // 1. It's not excluded
                // 2. No children were skipped (directory is empty or all contents were deleted)
                if (!IsPathExcluded(remotePath, targetDirectory, excludeList) && !hasSkippedChildren)
                {
                    DeleteFTPDirectory(connection, remotePath, password);
                }
                else if (hasSkippedChildren)
                {
                    Debug.Log($"Skipping deletion of directory '{remotePath}' because it contains excluded items.");
                }
            }
            catch (WebException ex)
            {
                if (ex.Response is FtpWebResponse response)
                {
                    Debug.LogError($"Failed to delete directory '{remotePath}': {response.StatusDescription}");
                }
                else
                {
                    Debug.LogError($"Failed to delete directory '{remotePath}': {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to delete directory '{remotePath}': {ex.Message}");
            }
        }

        private void DeleteFTPFile(FTPConnection connection, string remotePath, string password)
        {
            try
            {
                FtpWebRequest request = CreateFtpRequest(connection, remotePath, password);
                request.Method = WebRequestMethods.Ftp.DeleteFile;

                using (FtpWebResponse response = (FtpWebResponse)request.GetResponse())
                {
                    Debug.Log($"Deleted file: {remotePath} [{response.StatusDescription.Trim()}]");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to delete file '{remotePath}': {ex.Message}");
            }
        }

        private void DeleteFTPDirectory(FTPConnection connection, string remotePath, string password)
        {
            try
            {
                FtpWebRequest request = CreateFtpRequest(connection, remotePath, password);
                request.Method = WebRequestMethods.Ftp.RemoveDirectory;

                using (FtpWebResponse response = (FtpWebResponse)request.GetResponse())
                {
                    Debug.Log($"Deleted directory: {remotePath} [{response.StatusDescription.Trim()}]");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to delete directory '{remotePath}': {ex.Message}");
            }
        }

        private async Task DeleteViaSFTP(FTPConnection connection, string targetDirectory, string password, List<string> excludeList)
        {
            await Task.Run(() =>
            {
                Renci.SshNet.SftpClient client = null;

                try
                {
                    // Connect to SFTP server
                    client = SFTPUtil.ConnectSFTP(connection, password);

                    if (!client.IsConnected)
                    {
                        Debug.LogError("Failed to connect to SFTP server");
                        return;
                    }

                    Debug.Log($"Connected to SFTP server: {connection.host}:{connection.port}");

                    // Delete directory recursively
                    DeleteSFTPDirectoryRecursive(client, targetDirectory, excludeList, targetDirectory);

                    Debug.Log($"SFTP deletion completed for: {targetDirectory}");
                }
                catch (Exception e)
                {
                    Debug.LogError($"SFTP deletion error: {e.Message}\n{e.StackTrace}");
                    throw;
                }
                finally
                {
                    // Disconnect and cleanup
                    if (client != null)
                    {
                        if (client.IsConnected)
                        {
                            client.Disconnect();
                        }
                        client.Dispose();
                    }
                }
            });
        }

        private void DeleteSFTPDirectoryRecursive(Renci.SshNet.SftpClient client, string remotePath, List<string> excludeList, string targetDirectory)
        {
            try
            {
                if (AI.Actions.CancellationRequested) return;

                // List directory contents - this is more reliable than Exists() for complex paths
                // If the path doesn't exist, ListDirectory will throw SftpPathNotFoundException
                IEnumerable<ISftpFile> items;
                try
                {
                    items = client.ListDirectory(remotePath);
                }
                catch (Renci.SshNet.Common.SftpPathNotFoundException)
                {
                    // Path doesn't exist
                    Debug.LogWarning($"Path does not exist: {remotePath}");
                    return;
                }

                // Track if any children were skipped
                bool hasSkippedChildren = false;

                foreach (ISftpFile item in items)
                {
                    if (AI.Actions.CancellationRequested) return;

                    if (item.Name == "." || item.Name == "..") continue;

                    // Check if path should be excluded
                    if (IsPathExcluded(item.FullName, targetDirectory, excludeList))
                    {
                        Debug.Log($"Skipping excluded path: {item.FullName}");
                        hasSkippedChildren = true;
                        continue;
                    }

                    if (item.IsDirectory)
                    {
                        // Recursively delete subdirectory
                        DeleteSFTPDirectoryRecursive(client, item.FullName, excludeList, targetDirectory);
                    }
                    else
                    {
                        // Delete file
                        client.DeleteFile(item.FullName);
                        Debug.Log($"Deleted file: {item.FullName}");
                    }
                }

                // Delete the directory itself only if:
                // 1. It's not excluded
                // 2. No children were skipped (directory is empty or all contents were deleted)
                if (!IsPathExcluded(remotePath, targetDirectory, excludeList) && !hasSkippedChildren)
                {
                    client.DeleteDirectory(remotePath);
                    Debug.Log($"Deleted directory: {remotePath}");
                }
                else if (hasSkippedChildren)
                {
                    Debug.Log($"Skipping deletion of directory '{remotePath}' because it contains excluded items.");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to delete '{remotePath}': {e.Message}");
                throw;
            }
        }
    }
}
