using Database;
using ImpossibleRobert.Common;
using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace AssetInventory
{
    public static class DBAdapter
    {
        public const string DB_NAME = "AssetInventory.db";

        private static IDatabaseSettings _settings;

        public static IDatabaseConnection DB
        {
            get
            {
                if (_db == null) InitDB();
                return _db;
            }
        }

        public static string DBError { get; private set; }
        private static IDatabaseConnection _db;

        private static void InitDB()
        {
            try
            {
                DBError = null;

                // Create settings adapter that bridges to AI.Config
                _settings = new DatabaseSettingsAdapter();

                // Use DatabaseFactory to create the appropriate connection
                _db = DatabaseFactory.CreateConnection(_settings);

                // Use elevated timeout during table creation/migration (important for MySQL with large databases)
                _db.CommandTimeout = _settings.MySqlUpgradeTimeout;

                CreateSchema(_db);

                // Reset to default timeout for normal operations
                _db.CommandTimeout = 0;
            }
            catch (Exception e)
            {
                DBError = e.Message;
                Debug.LogError($"Error opening database: {DBError}");
                _db = null;
            }
        }

        public static void CreateSchema(IDatabaseConnection db)
        {
            // Create all tables
            db.CreateTable<Asset>();
            db.CreateTable<AssetFile>();
            db.CreateTable<AssetMedia>();
            db.CreateTable<AppProperty>();
            db.CreateTable<CustomAction>();
            db.CreateTable<CustomActionStep>();
            db.CreateTable<MetadataDefinition>();
            db.CreateTable<MetadataAssignment>();
            db.CreateTable<SavedSearch>();
            db.CreateTable<SavedPackageSearch>();
            db.CreateTable<Tag>();
            db.CreateTable<TagAssignment>();
            db.CreateTable<RelativeLocation>();
            db.CreateTable<SystemData>();
            db.CreateTable<Workspace>();
            db.CreateTable<WorkspaceSearch>();

            // Create indexes
            db.CreateIndex("AssetFile", new[] {"Type", "PreviewState", "Path"});
            db.CreateIndex("AssetFile", new[] {"Guid", "PreviewState"}); // For project window preview lookups
            db.CreateIndex("AssetFile", "Path");
            db.CreateIndex("AssetFile", "FileName");
            db.CreateIndex("AssetFile", new[] {"AssetId", "Size"}); // Covering index for Assets.Load() GROUP BY query
            db.CreateIndex("Asset", new[] {"Exclude", "AssetSource"});
            if (UsesSQLiteNoCaseLocationIndex(db))
            {
                db.Execute("CREATE INDEX IF NOT EXISTS idx_asset_fid_pid_loc ON Asset (ForeignId, ParentId, Location COLLATE NOCASE)");
            }
            else
            {
                db.CreateIndex("Asset", new[] {"ForeignId", "ParentId", "Location"});
            }
        }

        private static bool UsesSQLiteNoCaseLocationIndex(IDatabaseConnection db)
        {
            if (db is SQLiteDatabaseConnection) return true;
            return string.Equals(AI.Config?.databaseType, DatabaseFactory.SQLITE, StringComparison.OrdinalIgnoreCase);
        }

        internal static void SetConnection(IDatabaseConnection connection)
        {
            _db?.Dispose();
            _db = connection;
        }

        internal static void ResetConnection()
        {
            _db = null;
        }

        public static long GetDBSize()
        {
            return _db?.GetDatabaseSize() ?? 0;
        }

        public static bool ColumnExists(string tableName, string columnName)
        {
            return _db?.ColumnExists(tableName, columnName) ?? false;
        }

        public static long Optimize()
        {
            return _db?.Optimize() ?? 0;
        }

        public static string GetDBPath()
        {
            return IOUtils.PathCombine(Paths.GetStorageFolder(), DB_NAME);
        }

        public static bool IsDBOpen()
        {
            return _db != null && _db.IsConnected();
        }

        public static void Close()
        {
            if (_db == null) return;
            _db.Close();
            _db.Dispose();
            _db = null;
        }

        public static bool DeleteDB()
        {
            if (IsDBOpen()) Close();

            // Only delete SQLite database file
            if (AI.Config?.databaseType != DatabaseFactory.MYSQL)
            {
                try
                {
                    File.Delete(GetDBPath());
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false; // MySQL databases should be deleted through MySQL tools
        }

        public static bool IsBackingUp { get; private set; }

        public static void BackupDatabase(bool skipIntervalCheck = false)
        {
            _ = BackupDatabaseAsync(skipIntervalCheck);
        }

        public static async Task BackupDatabaseAsync(bool skipIntervalCheck = false)
        {
            if (IsBackingUp) return;

            try
            {
                // Check if database backups are enabled
                if (AI.Config == null || !AI.Config.enableDatabaseBackup) return;

                // Only support SQLite backups
                if (AI.Config.databaseType == DatabaseFactory.MYSQL) return;

                // Check if database is available
                if (_db == null || !IsDBOpen()) return;

                // Check if database file exists
                string dbPath = GetDBPath();
                if (!File.Exists(dbPath)) return;

                // Check if enough time has passed since last backup (unless skipping interval check)
                DateTime now = DateTime.Now;
                if (!skipIntervalCheck && AI.Config.lastDatabaseBackup != DateTime.MinValue)
                {
                    double daysSinceLastBackup = (now - AI.Config.lastDatabaseBackup).TotalDays;
                    if (daysSinceLastBackup < AI.Config.databaseBackupInterval)
                    {
                        return;
                    }
                }

                IsBackingUp = true;
                int progressId = MetaProgress.Start("Database Backup");

                // Capture values needed inside Task.Run
                string backupFolder = Paths.GetBackupFolder();
                string configPath = Paths.GetConfigLocation();
                int backupsToKeep = AI.Config.databaseBackupsToKeep;

                try
                {
                    await Task.Run(() =>
                    {
                        Directory.CreateDirectory(backupFolder);

                        // Create timestamped backup filename
                        string timestamp = now.ToString("yyyy-MM-dd_HH-mm-ss");
                        string backupFileName = $"AssetInventory_{timestamp}.db";
                        string backupPath = IOUtils.PathCombine(backupFolder, backupFileName);

                        // Perform backup
                        try
                        {
                            _db.Backup(backupPath);
                            Debug.Log($"Database backup created: {backupFileName}");
                        }
                        catch (NotSupportedException)
                        {
                            // Fallback to file copy for databases that don't support backup API
                            File.Copy(dbPath, backupPath, true);
                            Debug.Log($"Database backup created (file copy): {backupFileName}");
                        }

                        // Backup config file with same timestamp
                        BackupConfigInternal(backupFolder, timestamp, configPath);

                        // Clean up old backups
                        CleanupOldBackupsInternal(backupFolder, backupsToKeep);
                    });

                    // Update last backup timestamp on main thread
                    AI.Config.lastDatabaseBackup = now;
                    AI.SaveConfig();
                }
                finally
                {
                    MetaProgress.Remove(progressId);
                    IsBackingUp = false;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error creating database backup: {e.Message}");
            }
        }

        private static void BackupConfigInternal(string backupFolder, string timestamp, string configPath)
        {
            try
            {
                if (!File.Exists(configPath)) return;

                string backupFileName = $"AssetInventoryConfig_{timestamp}.json";
                string backupPath = IOUtils.PathCombine(backupFolder, backupFileName);

                File.Copy(configPath, backupPath, true);
                Debug.Log($"Config backup created: {backupFileName}");
            }
            catch (Exception e)
            {
                Debug.LogError($"Error creating config backup: {e.Message}");
            }
        }

        private static void CleanupOldBackupsInternal(string backupFolder, int backupsToKeep)
        {
            try
            {
                if (backupsToKeep <= 0) return;

                // Clean up database backups
                CleanupBackupFiles(backupFolder, "AssetInventory_*.db", "database", backupsToKeep);

                // Clean up config backups
                CleanupBackupFiles(backupFolder, "AssetInventoryConfig_*.json", "config", backupsToKeep);
            }
            catch (Exception e)
            {
                Debug.LogError($"Error cleaning up old backups: {e.Message}");
            }
        }

        private static void CleanupBackupFiles(string backupFolder, string pattern, string backupType, int backupsToKeep)
        {
            string[] backupFiles = Directory.GetFiles(backupFolder, pattern);

            if (backupFiles.Length <= backupsToKeep) return;

            // Sort by creation time (newest first)
            Array.Sort(backupFiles, (a, b) =>
            {
                DateTime timeA = File.GetCreationTime(a);
                DateTime timeB = File.GetCreationTime(b);
                return timeB.CompareTo(timeA);
            });

            // Delete oldest backups
            for (int i = backupsToKeep; i < backupFiles.Length; i++)
            {
                try
                {
                    File.Delete(backupFiles[i]);
                    Debug.Log($"Deleted old {backupType} backup: {Path.GetFileName(backupFiles[i])}");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Could not delete old backup {backupFiles[i]}: {e.Message}");
                }
            }
        }
    }
}
