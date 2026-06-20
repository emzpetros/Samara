using ImpossibleRobert.Common;
using System;
using System.IO;
using Renci.SshNet;
using Renci.SshNet.Common;
using UnityEngine;

namespace AssetInventory
{
    public static class SFTPUtil
    {
        /// <summary>
        /// Creates and connects an SFTP client based on the connection configuration
        /// </summary>
        public static SftpClient ConnectSFTP(FTPConnection connection, string password)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof (connection));
            }

            if (string.IsNullOrEmpty(connection.host))
            {
                throw new ArgumentException("Host cannot be empty", nameof (connection));
            }

            if (string.IsNullOrEmpty(connection.username))
            {
                throw new ArgumentException("Username cannot be empty", nameof (connection));
            }

            if (string.IsNullOrEmpty(password))
            {
                throw new ArgumentException("Password is required for SFTP authentication");
            }

            // Create connection info with password authentication
            ConnectionInfo connectionInfo = new ConnectionInfo(
                connection.host,
                connection.port,
                connection.username,
                new PasswordAuthenticationMethod(connection.username, password)
            );

            // Create and connect the SFTP client
            SftpClient client = new SftpClient(connectionInfo);

            try
            {
                client.Connect();
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Uploads a directory recursively to the SFTP server
        /// </summary>
        public static void UploadDirectory(SftpClient client, string localPath, string remotePath, bool recursive, Action<string, string> onFileUploaded = null, Func<bool> cancellationRequested = null)
        {
            if (client == null || !client.IsConnected)
            {
                throw new InvalidOperationException("SFTP client is not connected");
            }

            if (!Directory.Exists(localPath))
            {
                throw new DirectoryNotFoundException($"Local directory not found: {localPath}");
            }

            // Normalize remote path
            remotePath = IOUtils.NormalizeRemotePath(remotePath);

            // Create remote directory if it doesn't exist
            CreateRemoteDirectory(client, remotePath);

            // Upload files in current directory
            string[] files = Directory.GetFiles(localPath);
            foreach (string filePath in files)
            {
                // Check for cancellation
                if (cancellationRequested != null && cancellationRequested()) return;

                string fileName = Path.GetFileName(filePath);
                string remoteFilePath = remotePath.TrimEnd('/') + "/" + fileName;

                try
                {
                    using (FileStream fileStream = File.OpenRead(filePath))
                    {
                        client.UploadFile(fileStream, remoteFilePath, true);
                    }

                    onFileUploaded?.Invoke(filePath, remoteFilePath);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Failed to upload file '{filePath}': {ex.Message}");
                    throw;
                }
            }

            // Upload subdirectories if recursive
            if (recursive)
            {
                string[] directories = Directory.GetDirectories(localPath);
                foreach (string dirPath in directories)
                {
                    // Check for cancellation
                    if (cancellationRequested != null && cancellationRequested())
                    {
                        Debug.Log("SFTP upload cancelled by user");
                        return;
                    }

                    string dirName = Path.GetFileName(dirPath);
                    string remoteSubDir = remotePath.TrimEnd('/') + "/" + dirName;

                    UploadDirectory(client, dirPath, remoteSubDir, true, onFileUploaded, cancellationRequested);
                }
            }
        }

        /// <summary>
        /// Creates a directory on the remote server, including parent directories
        /// </summary>
        public static void CreateRemoteDirectory(SftpClient client, string path)
        {
            if (client == null || !client.IsConnected)
            {
                throw new InvalidOperationException("SFTP client is not connected");
            }

            path = IOUtils.NormalizeRemotePath(path);

            if (string.IsNullOrEmpty(path) || path == "/")
            {
                return; // Root directory always exists
            }

            // Check if directory already exists
            try
            {
                if (client.Exists(path))
                {
                    return;
                }
            }
            catch
            {
                // Directory doesn't exist or any error occurred (permission, network, etc.)
                // We'll proceed with creation - if it already exists, CreateDirectory will handle it
            }

            // Create parent directories first
            string parentPath = IOUtils.GetRemoteParentPath(path);
            if (!string.IsNullOrEmpty(parentPath) && parentPath != "/")
            {
                CreateRemoteDirectory(client, parentPath);
            }

            // Create this directory
            try
            {
                client.CreateDirectory(path);
            }
            catch (SftpPermissionDeniedException ex)
            {
                // Some SFTP servers return permission denied when directory already exists
                // Check if directory actually exists before throwing
                try
                {
                    if (client.Exists(path))
                    {
                        // Directory exists, treat as success
                        return;
                    }
                }
                catch (SftpPermissionDeniedException)
                {
                    // Exists check also returned permission denied
                    // Since we got permission denied on create, this likely means the directory exists
                    // (some servers return permission denied for both operations when directory exists)
                    // Treat as success and proceed
                    return;
                }
                catch
                {
                    // Exists check failed with other exception, but we got permission denied on create
                    // This could mean directory exists but we can't verify - assume success to avoid false errors
                    return;
                }
                
                // Exists check returned false - directory doesn't exist and we can't create it
                // This is a real permission issue
                Debug.LogError($"Permission denied creating directory '{path}': {ex.Message}");
                throw;
            }
            catch (SshException ex)
            {
                // Directory might already exist (race condition in some cases)
                try
                {
                    if (client.Exists(path))
                    {
                        // Directory exists, treat as success
                        return;
                    }
                }
                catch (SftpPermissionDeniedException)
                {
                    // Exists check returned permission denied
                    // Since creation failed with SshException, and we can't verify existence,
                    // this might be a permission issue, but could also mean directory exists
                    // Be conservative and assume directory might exist - don't throw
                    return;
                }
                catch
                {
                    // Exists check failed with other exception
                    // Can't verify if directory exists, but creation failed
                    // This is likely a real error - throw the original exception
                }
                
                // Exists check returned false or failed - directory doesn't exist and creation failed
                Debug.LogError($"Failed to create directory '{path}': {ex.Message}");
                throw;
            }
        }


        /// <summary>
        /// Tests an SFTP connection
        /// </summary>
        public static bool TestConnection(FTPConnection connection, string password, out string errorMessage)
        {
            errorMessage = null;

            try
            {
                using (SftpClient client = ConnectSFTP(connection, password))
                {
                    if (client.IsConnected)
                    {
                        // Try to list the home directory to verify connection works
                        client.ListDirectory(".");
                        client.Disconnect();
                        return true;
                    }

                    errorMessage = "Failed to connect";
                    return false;
                }
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }
    }
}
