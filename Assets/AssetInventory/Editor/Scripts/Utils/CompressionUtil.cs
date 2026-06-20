using ImpossibleRobert.Common;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;
using Unity.SharpZipLib.GZip;
using Unity.SharpZipLib.Tar;
using UnityEngine;
using CompressionType = SharpCompress.Common.CompressionType;

namespace AssetInventory
{
    public static class CompressionUtil
    {
        // more performant implementation using SharpCompress, especially on Linux
        public static void ExtractGz(string archive, string targetFolder, CancellationToken ct)
        {
            Directory.CreateDirectory(targetFolder);

            try
            {
                using FileStream stream = File.OpenRead(archive);
                ReaderOptions readerOptions = new ReaderOptions {LeaveStreamOpen = false};
                using IReader reader = ReaderFactory.Open(stream, readerOptions);
                while (reader.MoveToNextEntry())
                {
                    if (ct.IsCancellationRequested)
                    {
                        _ = IOUtils.DeleteFileOrDirectory(targetFolder);
                        break;
                    }
                    if (!reader.Entry.IsDirectory)
                    {
                        reader.WriteEntryToDirectory(targetFolder, new ExtractionOptions
                        {
                            ExtractFullPath = true,
                            Overwrite = true
                        });
                    }
                }
            }
            catch (UnauthorizedAccessException uaEx)
            {
                Debug.LogError($"Permission denied extracting '{archive}': {uaEx.Message}");
            }
            catch (ArchiveException archEx)
            {
                Debug.LogError($"Archive format error for '{archive}': {archEx.Message}");
            }
            catch (Exception e)
            {
                Debug.LogError($"Could not extract archive '{archive}'. It may be corrupted or the process was interrupted: {e.Message}");
            }
        }

        public static string ExtractGzFile(string archive, string fileName, string targetFolder, CancellationToken ct)
        {
            Stream rawStream = File.OpenRead(archive);
            GZipInputStream gzipStream = new GZipInputStream(rawStream);

            string destFile = null;

            // fileName will be ID/asset, whole folder is needed though
            string folderName = fileName.Split(new[] {'/', '\\'}, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

            try
            {
                Stream inputStream = IsZipped(archive) ? gzipStream : rawStream;

                using (TarInputStream tarStream = new TarInputStream(inputStream, Encoding.Default))
                {
                    TarEntry entry;
                    bool found = false;
                    while ((entry = tarStream.GetNextEntry()) != null)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (entry.IsDirectory) continue;
                        if (entry.Name.Contains(folderName))
                        {
                            destFile = Path.Combine(targetFolder, entry.Name);
                            string directoryName = Path.GetDirectoryName(destFile);
                            Directory.CreateDirectory(directoryName);

                            using (FileStream fileStream = File.Create(destFile))
                            {
                                tarStream.CopyEntryContents(fileStream);
                            }
                            found = true;
                        }
                        else if (found)
                        {
                            // leave the loop if the files were found and the next entry is not in the same folder
                            // assumption is the files appear consecutively
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Could not extract file from archive '{archive}'. The process was either interrupted or the file is corrupted: {e.Message}");
            }

            gzipStream.Close();
            rawStream.Close();

            return destFile;
        }

        private static bool IsZipped(string fileName)
        {
            using (FileStream fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] buffer = new byte[2];
                fs.Read(buffer, 0, buffer.Length);
                return buffer[0] == 0x1F && buffer[1] == 0x8B;
            }
        }

        public static bool IsFirstArchiveVolume(string file)
        {
            if (string.IsNullOrEmpty(file)) return true;

            string fileName = Path.GetFileName(file).ToLowerInvariant();
            if (fileName.EndsWith(".rar"))
            {
                Match match = Regex.Match(fileName, @"\.part(\d+)\.rar$");
                if (match.Success)
                {
                    int partNumber = int.Parse(match.Groups[1].Value);
                    return partNumber == 1;
                }
                return true;
            }
            return true;
        }

        public static void CompressFolder(string source, string target)
        {
            using FileStream zipStream = File.Create(target);
            WriterOptions options = new WriterOptions(CompressionType.Deflate);
            using IWriter writer = WriterFactory.Open(zipStream, ArchiveType.Zip, options);
            writer.WriteAll(source, "*", SearchOption.AllDirectories);
        }

        public static void CreateEmptyZip(string zipPath)
        {
            using FileStream zipStream = File.Create(zipPath);
            using IWriter writer = WriterFactory.Open(zipStream, ArchiveType.Zip, new WriterOptions(CompressionType.Deflate));
            // No entries added: creates an empty zip.
        }

        public static bool ExtractArchive(string archiveFile, string targetFolder, CancellationToken ct = default(CancellationToken))
        {
            Directory.CreateDirectory(targetFolder);

            try
            {
                // CRITICAL: Open archive file with FileShare.Read to allow other Unity editors to read it simultaneously
                // This prevents exclusive locking of Unity cache packages during extraction
                using (FileStream archiveStream = new FileStream(archiveFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (IArchive archive = ArchiveFactory.Open(archiveStream))
                {
                    foreach (IArchiveEntry entry in archive.Entries)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            _ = IOUtils.DeleteFileOrDirectory(targetFolder);
                            return false;
                        }
                        if (string.IsNullOrEmpty(entry.Key)) continue;

                        if (!entry.IsDirectory)
                        {
                            try
                            {
                                string fullOutputPath = Path.Combine(targetFolder, entry.Key);
                                string directoryName = Path.GetDirectoryName(fullOutputPath);
                                Directory.CreateDirectory(directoryName);

                                entry.WriteToDirectory(targetFolder, new ExtractionOptions
                                {
                                    Overwrite = true,
                                    ExtractFullPath = true
                                });
                            }
                            catch (Exception e)
                            {
                                if (e is ArgumentException || e is IOException)
                                {
                                    // can happen for paths containing : and other illegal characters
                                    Debug.LogWarning($"Could not extract file '{entry.Key}' from archive '{archiveFile}': {e.Message}");
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Could not extract archive '{archiveFile}'. The process was potentially interrupted, the file is corrupted or the path too long: {e.Message}");
                return false;
            }

            return true;
        }
    }
}
