using System;

namespace Database
{
    /// <summary>
    /// Interface for database configuration settings.
    /// </summary>
    public interface IDatabaseSettings
    {
        /// <summary>
        /// The database type: "SQLite" or "MySQL"
        /// </summary>
        string DatabaseType { get; set; }

        // SQLite settings

        /// <summary>
        /// Full path to the SQLite database file
        /// </summary>
        string DatabasePath { get; }

        /// <summary>
        /// SQLite journal mode (e.g., "WAL", "DELETE", "MEMORY")
        /// </summary>
        string JournalMode { get; set; }

        // MySQL settings

        /// <summary>
        /// MySQL server hostname
        /// </summary>
        string MySqlHost { get; set; }

        /// <summary>
        /// MySQL server port (default: 3306)
        /// </summary>
        int MySqlPort { get; set; }

        /// <summary>
        /// MySQL database name
        /// </summary>
        string MySqlDatabase { get; set; }

        /// <summary>
        /// MySQL username
        /// </summary>
        string MySqlUser { get; set; }

        /// <summary>
        /// MySQL password (decrypted). The consumer is responsible for encryption/decryption.
        /// </summary>
        string MySqlPassword { get; set; }

        /// <summary>
        /// Whether to use SSL for MySQL connections
        /// </summary>
        bool MySqlUseSSL { get; set; }

        /// <summary>
        /// MySQL connection timeout in seconds
        /// </summary>
        int MySqlConnectionTimeout { get; set; }

        /// <summary>
        /// MySQL command execution timeout in seconds during DB upgrades/migrations
        /// </summary>
        int MySqlUpgradeTimeout { get; set; }
    }

    /// <summary>
    /// Default IDatabaseSettings implementation.
    /// </summary>
    [Serializable]
    public class DatabaseSettings : IDatabaseSettings
    {
        // Database type
        private string _databaseType = DatabaseFactory.SQLITE;

        // SQLite settings
        private string _databasePath = "database.db";
        private string _journalMode = "WAL";

        // MySQL settings
        private string _mySqlHost = "localhost";
        private int _mySqlPort = 3306;
        private string _mySqlDatabase = "";
        private string _mySqlUser = "";
        private string _mySqlPassword = "";
        private bool _mySqlUseSSL;
        private int _mySqlConnectionTimeout = 30;
        private int _mySqlUpgradeTimeout = 1800;

        public string DatabaseType
        {
            get => _databaseType;
            set => _databaseType = value ?? DatabaseFactory.SQLITE;
        }

        public string DatabasePath
        {
            get => _databasePath;
            set => _databasePath = value;
        }

        public string JournalMode
        {
            get => _journalMode;
            set => _journalMode = value ?? "WAL";
        }

        public string MySqlHost
        {
            get => _mySqlHost;
            set => _mySqlHost = value ?? "localhost";
        }

        public int MySqlPort
        {
            get => _mySqlPort;
            set => _mySqlPort = value > 0 ? value : 3306;
        }

        public string MySqlDatabase
        {
            get => _mySqlDatabase;
            set => _mySqlDatabase = value ?? "";
        }

        public string MySqlUser
        {
            get => _mySqlUser;
            set => _mySqlUser = value ?? "";
        }

        public string MySqlPassword
        {
            get => _mySqlPassword;
            set => _mySqlPassword = value ?? "";
        }

        public bool MySqlUseSSL
        {
            get => _mySqlUseSSL;
            set => _mySqlUseSSL = value;
        }

        public int MySqlConnectionTimeout
        {
            get => _mySqlConnectionTimeout;
            set => _mySqlConnectionTimeout = value > 0 ? value : 30;
        }

        public int MySqlUpgradeTimeout
        {
            get => _mySqlUpgradeTimeout;
            set => _mySqlUpgradeTimeout = value > 0 ? value : 1800;
        }

        /// <summary>
        /// Creates settings configured for SQLite with the specified database path
        /// </summary>
        public static DatabaseSettings ForSQLite(string databasePath, string journalMode = "WAL")
        {
            return new DatabaseSettings
            {
                DatabaseType = DatabaseFactory.SQLITE,
                DatabasePath = databasePath,
                JournalMode = journalMode
            };
        }

        /// <summary>
        /// Creates settings configured for MySQL with the specified connection parameters
        /// </summary>
        public static DatabaseSettings ForMySQL(
            string host,
            int port,
            string database,
            string user,
            string password,
            bool useSSL = false,
            int connectionTimeout = 30)
        {
            return new DatabaseSettings
            {
                DatabaseType = DatabaseFactory.MYSQL,
                MySqlHost = host,
                MySqlPort = port,
                MySqlDatabase = database,
                MySqlUser = user,
                MySqlPassword = password,
                MySqlUseSSL = useSSL,
                MySqlConnectionTimeout = connectionTimeout
            };
        }
    }
}