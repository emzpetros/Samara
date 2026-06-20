using Automator;
using ImpossibleRobert.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace AssetInventory
{
    [Serializable]
    public sealed class FTPUploadStep : FTPActionStep
    {
        private const int FTP_PARALLEL_UPLOAD_THRESHOLD = 8;
        private const int FTP_PARALLEL_UPLOAD_COUNT = 4;

        private sealed class FTPUploadItem
        {
            public string LocalPath;
            public string RelativePath;
            public string RemotePath;
        }

        public FTPUploadStep()
        {
            Key = "FTPUpload";
            Name = "FTP Upload";
            Description = "Upload a folder to an FTP or SFTP server.";
            Category = ActionCategory.FilesAndFolders;

            // Add FTP/SFTP server connection parameter
            AddServerParameter();

            // Source folder parameter
            Parameters.Add(new StepParameter
            {
                Name = "Source",
                Description = "Local folder to upload.",
                ValueList = StepParameter.ValueType.Folder,
                DefaultValue = new ParameterValue(Paths.GetStorageFolder())
            });

            // Target directory parameter
            Parameters.Add(new StepParameter
            {
                Name = "Target",
                Description = "Remote directory path on the FTP/SFTP server (e.g., /public_html/files or /uploads).",
                DefaultValue = new ParameterValue("/")
            });
        }

        public override async Task Run(List<ParameterValue> parameters)
        {
            // Get parameters
            string connectionId = parameters[0].stringValue;
            string sourceFolder = parameters[1].stringValue;
            string targetDirectory = parameters[2].stringValue;

            // Get and validate connection
            if (!TryGetConnection(connectionId, out FTPConnection connection, out string password))
            {
                throw new Exception("Failed to get FTP connection. Check that the connection is properly configured.");
            }

            // Validate source folder
            if (string.IsNullOrEmpty(sourceFolder) || !Directory.Exists(sourceFolder))
            {
                throw new DirectoryNotFoundException($"Source folder does not exist: {sourceFolder}");
            }

            string protocolName = GetProtocolName(connection);
            Debug.Log($"Starting upload from '{sourceFolder}' to '{protocolName}://{connection.host}:{connection.port}{targetDirectory}'");

            // Perform upload based on protocol
            if (connection.protocol == FTPConnection.FTPProtocol.SFTP)
            {
                await UploadViaSFTP(connection, sourceFolder, targetDirectory, true, password);
            }
            else
            {
                await UploadViaFTP(connection, sourceFolder, targetDirectory, true, password);
            }

            Debug.Log($"{protocolName.ToUpper()} upload completed successfully.");
        }

        private async Task UploadViaFTP(FTPConnection connection, string sourceFolder, string targetDirectory, bool includeSubdirectories, string password)
        {
            await Task.Run(() =>
            {
                try
                {
                    targetDirectory = IOUtils.NormalizeRemotePath(targetDirectory);
                    HashSet<string> ensuredDirectories = new HashSet<string>(StringComparer.Ordinal);
                    string[] files = Directory.GetFiles(sourceFolder, "*.*", includeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
                    List<FTPUploadItem> uploadItems = new List<FTPUploadItem>(files.Length);
                    HashSet<string> directoriesToEnsure = new HashSet<string>(StringComparer.Ordinal) {targetDirectory};

                    foreach (string filePath in files)
                    {
                        string relativePath = IOUtils.GetRelativePath(sourceFolder, filePath).Replace("\\", "/");
                        string remoteDirectory = targetDirectory;
                        string remoteFileName = Path.GetFileName(filePath);

                        if (includeSubdirectories)
                        {
                            string subDir = Path.GetDirectoryName(relativePath);
                            if (!string.IsNullOrEmpty(subDir))
                            {
                                remoteDirectory = IOUtils.CombineRemotePath(targetDirectory, subDir);
                            }
                        }

                        remoteDirectory = IOUtils.NormalizeRemotePath(remoteDirectory);
                        uploadItems.Add(new FTPUploadItem
                        {
                            LocalPath = filePath,
                            RelativePath = relativePath,
                            RemotePath = IOUtils.CombineRemotePath(remoteDirectory, remoteFileName)
                        });

                        directoriesToEnsure.Add(remoteDirectory);
                    }

                    List<string> sortedDirectories = new List<string>(directoriesToEnsure);
                    sortedDirectories.Sort((left, right) => left.Length.CompareTo(right.Length));
                    foreach (string remoteDirectory in sortedDirectories)
                    {
                        EnsureFTPDirectory(connection, remoteDirectory, password, ensuredDirectories);
                    }

                    int uploadedCount = 0;
                    int totalFiles = uploadItems.Count;

                    void UploadSingleFile(FTPUploadItem item)
                    {
                        if (AI.Actions.CancellationRequested)
                        {
                            return;
                        }

                        try
                        {
                            FtpWebRequest request = CreateFtpRequest(connection, item.RemotePath, password, keepAlive: true);
                            request.Method = WebRequestMethods.Ftp.UploadFile;
                            request.UseBinary = true;
                            request.ServicePoint.ConnectionLimit = Math.Max(request.ServicePoint.ConnectionLimit, FTP_PARALLEL_UPLOAD_COUNT);

                            using (FileStream fileStream = File.OpenRead(item.LocalPath))
                            using (Stream requestStream = request.GetRequestStream())
                            {
                                request.ContentLength = fileStream.Length;
                                fileStream.CopyTo(requestStream);
                            }

                            using (FtpWebResponse response = (FtpWebResponse)request.GetResponse())
                            {
                                int currentCount = Interlocked.Increment(ref uploadedCount);
                                Debug.Log($"Uploaded ({currentCount}/{totalFiles}): {item.RelativePath} -> {item.RemotePath} [{response.StatusDescription.Trim()}]");
                            }
                        }
                        catch (WebException ex)
                        {
                            Debug.LogError($"Failed to upload file '{item.LocalPath}': {DescribeFtpError(ex)}");
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"Failed to upload file '{item.LocalPath}': {ex.Message}");
                        }
                    }

                    if (uploadItems.Count >= FTP_PARALLEL_UPLOAD_THRESHOLD)
                    {
                        Parallel.ForEach(uploadItems, new ParallelOptions {MaxDegreeOfParallelism = FTP_PARALLEL_UPLOAD_COUNT}, item =>
                        {
                            UploadSingleFile(item);
                        });
                    }
                    else
                    {
                        foreach (FTPUploadItem item in uploadItems)
                        {
                            if (AI.Actions.CancellationRequested)
                            {
                                break;
                            }

                            UploadSingleFile(item);
                        }
                    }

                    Debug.Log($"FTP upload completed: {uploadedCount}/{totalFiles} files uploaded.");
                }
                finally
                {
                    ResetSslValidation();
                }
            });
        }

        private void EnsureFTPDirectory(FTPConnection connection, string remotePath, string password, HashSet<string> ensuredDirectories)
        {
            remotePath = IOUtils.NormalizeRemotePath(remotePath);
            if (string.IsNullOrEmpty(remotePath) || remotePath == "/")
            {
                ensuredDirectories?.Add("/");
                return;
            }

            string currentPath = string.Empty;
            string[] parts = remotePath.Trim('/').Split(new[] {'/'}, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                currentPath = IOUtils.CombineRemotePath(currentPath, part);
                if (ensuredDirectories != null && ensuredDirectories.Contains(currentPath))
                {
                    continue;
                }

                try
                {
                    FtpWebRequest request = CreateFtpRequest(connection, currentPath, password, keepAlive: true);
                    request.Method = WebRequestMethods.Ftp.MakeDirectory;

                    using (FtpWebResponse response = (FtpWebResponse)request.GetResponse())
                    {
                        ensuredDirectories?.Add(currentPath);
                    }
                }
                catch (WebException ex)
                {
                    if (FTPDirectoryExists(connection, currentPath, password, keepAlive: true))
                    {
                        ensuredDirectories?.Add(currentPath);
                        continue;
                    }

                    throw new InvalidOperationException(
                        $"Failed to create remote directory '{currentPath}': {DescribeFtpError(ex)}",
                        ex
                    );
                }
            }
        }

        private string DescribeFtpError(WebException exception)
        {
            if (exception?.Response is FtpWebResponse response)
            {
                StringBuilder builder = new StringBuilder();
                builder.Append(response.StatusDescription?.Trim());

                if (response.StatusCode == FtpStatusCode.ActionNotTakenFileUnavailable)
                {
                    builder.Append(" This usually means the remote path is invalid, the parent folder is missing, or the FTP user cannot write there.");
                }
                else if (response.StatusCode == FtpStatusCode.ActionNotTakenFilenameNotAllowed)
                {
                    builder.Append(" The server rejected the remote path or file name.");
                }

                return builder.ToString();
            }

            return exception?.Message ?? "Unknown FTP error.";
        }

        /// <summary>
        /// Checks if a directory exists on the FTP server
        /// </summary>
        private bool FTPDirectoryExists(FTPConnection connection, string remotePath, string password, bool keepAlive = false)
        {
            try
            {
                FtpWebRequest request = CreateFtpRequest(connection, remotePath, password, keepAlive);
                request.Method = WebRequestMethods.Ftp.ListDirectory;

                using (FtpWebResponse response = (FtpWebResponse)request.GetResponse())
                {
                    return true;
                }
            }
            catch (WebException ex)
            {
                if (ex.Response is FtpWebResponse response)
                {
                    if (response.StatusCode == FtpStatusCode.ActionNotTakenFileUnavailable ||
                        response.StatusCode == FtpStatusCode.ActionNotTakenFilenameNotAllowed)
                    {
                        return false;
                    }
                }

                return false;
            }
        }

        private async Task UploadViaSFTP(FTPConnection connection, string sourceFolder, string targetDirectory, bool includeSubdirectories, string password)
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

                    // Get all files to upload
                    string[] files = Directory.GetFiles(sourceFolder, "*.*", includeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

                    int uploadedCount = 0;
                    int totalFiles = files.Length;

                    // Upload files
                    SFTPUtil.UploadDirectory(client, sourceFolder, targetDirectory, includeSubdirectories, (localFile, remoteFile) =>
                    {
                        uploadedCount++;
                        string relativePath = IOUtils.GetRelativePath(sourceFolder, localFile);
                        Debug.Log($"Uploaded ({uploadedCount}/{totalFiles}): {relativePath} -> {remoteFile}");
                    }, () => AI.Actions.CancellationRequested);

                    Debug.Log($"SFTP upload completed: {uploadedCount}/{totalFiles} files uploaded.");
                }
                catch (Exception e)
                {
                    Debug.LogError($"SFTP upload error: {e.Message}\n{e.StackTrace}");
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
    }
}
