using Database;
using ImpossibleRobert.Common;

namespace AssetInventory
{
    /// <summary>
    /// Adapter that bridges Database.IDatabaseSettings to AssetInventory's configuration.
    /// Bidirectional sync - changes via setters are persisted to the config file.
    /// </summary>
    internal class DatabaseSettingsAdapter : IDatabaseSettings
    {
        public string DatabaseType
        {
            get => AI.Config.databaseType ?? DatabaseFactory.SQLITE;
            set
            {
                if (AI.Config.databaseType != value)
                {
                    AI.Config.databaseType = value;
                    AI.SaveConfig();
                }
            }
        }

        public string DatabasePath => IOUtils.PathCombine(Paths.GetStorageFolder(), DBAdapter.DB_NAME);

        public string JournalMode
        {
            get => AI.Config.dbJournalMode ?? "WAL";
            set
            {
                if (AI.Config.dbJournalMode != value)
                {
                    AI.Config.dbJournalMode = value;
                    AI.SaveConfig();
                }
            }
        }

        public string MySqlHost
        {
            get => AI.Config.mysqlHost ?? "localhost";
            set
            {
                if (AI.Config.mysqlHost != value)
                {
                    AI.Config.mysqlHost = value;
                    AI.SaveConfig();
                }
            }
        }

        public int MySqlPort
        {
            get => AI.Config.mysqlPort > 0 ? AI.Config.mysqlPort : 3306;
            set
            {
                if (AI.Config.mysqlPort != value)
                {
                    AI.Config.mysqlPort = value;
                    AI.SaveConfig();
                }
            }
        }

        public string MySqlDatabase
        {
            get => AI.Config.mysqlDatabase ?? "";
            set
            {
                if (AI.Config.mysqlDatabase != value)
                {
                    AI.Config.mysqlDatabase = value;
                    AI.SaveConfig();
                }
            }
        }

        public string MySqlUser
        {
            get => AI.Config.mysqlUser ?? "";
            set
            {
                if (AI.Config.mysqlUser != value)
                {
                    AI.Config.mysqlUser = value;
                    AI.SaveConfig();
                }
            }
        }

        /// <summary>
        /// Gets/sets the MySQL password. Getter decrypts, setter encrypts before saving.
        /// </summary>
        public string MySqlPassword
        {
            get
            {
                if (string.IsNullOrEmpty(AI.Config.mysqlEncryptedPassword))
                    return "";

                string decrypted = EncryptionUtil.Decrypt(AI.Config.mysqlEncryptedPassword);
                return decrypted ?? "";
            }
            set
            {
                string encrypted = string.IsNullOrEmpty(value) ? "" : EncryptionUtil.Encrypt(value);
                if (AI.Config.mysqlEncryptedPassword != encrypted)
                {
                    AI.Config.mysqlEncryptedPassword = encrypted;
                    AI.SaveConfig();
                }
            }
        }

        public bool MySqlUseSSL
        {
            get => AI.Config.mysqlUseSSL;
            set
            {
                if (AI.Config.mysqlUseSSL != value)
                {
                    AI.Config.mysqlUseSSL = value;
                    AI.SaveConfig();
                }
            }
        }

        public int MySqlConnectionTimeout
        {
            get => AI.Config.mysqlConnectionTimeout > 0 ? AI.Config.mysqlConnectionTimeout : 30;
            set
            {
                if (AI.Config.mysqlConnectionTimeout != value)
                {
                    AI.Config.mysqlConnectionTimeout = value;
                    AI.SaveConfig();
                }
            }
        }

        public int MySqlUpgradeTimeout
        {
            get => AI.Config.mysqlUpgradeTimeout > 0 ? AI.Config.mysqlUpgradeTimeout : 1800;
            set
            {
                if (AI.Config.mysqlUpgradeTimeout != value)
                {
                    AI.Config.mysqlUpgradeTimeout = value;
                    AI.SaveConfig();
                }
            }
        }
    }
}
