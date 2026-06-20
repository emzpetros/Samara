namespace Database
{
    /// <summary>
    /// Factory for creating database connections.
    /// Simple API for creating connections based on settings.
    /// </summary>
    public static class DatabaseFactory
    {
        /// <summary>
        /// SQLite database type identifier
        /// </summary>
        public const string SQLITE = "SQLite";

        /// <summary>
        /// MySQL database type identifier
        /// </summary>
        public const string MYSQL = "MySQL";

        /// <summary>
        /// Creates and initializes a database connection based on the provided settings.
        /// </summary>
        /// <param name="settings">Database configuration settings</param>
        /// <returns>An initialized database connection ready for use</returns>
        public static IDatabaseConnection CreateConnection(IDatabaseSettings settings)
        {
            IDatabaseConnection connection = settings.DatabaseType == MYSQL
                ? new MySQLDatabaseConnection(settings)
                : new SQLiteDatabaseConnection(settings);

            connection.Initialize();
            return connection;
        }

        /// <summary>
        /// Creates a SQLite connection with the specified path.
        /// </summary>
        /// <param name="databasePath">Path to the SQLite database file</param>
        /// <param name="journalMode">SQLite journal mode (default: WAL)</param>
        /// <returns>An initialized SQLite database connection</returns>
        public static IDatabaseConnection CreateSQLiteConnection(string databasePath, string journalMode = "WAL")
        {
            DatabaseSettings settings = DatabaseSettings.ForSQLite(databasePath, journalMode);
            return CreateConnection(settings);
        }

        /// <summary>
        /// Creates a MySQL connection with the specified parameters.
        /// </summary>
        public static IDatabaseConnection CreateMySQLConnection(
            string host,
            int port,
            string database,
            string user,
            string password,
            bool useSSL = false,
            int connectionTimeout = 30)
        {
            DatabaseSettings settings = DatabaseSettings.ForMySQL(host, port, database, user, password, useSSL, connectionTimeout);
            return CreateConnection(settings);
        }

        /// <summary>
        /// Tests if a connection can be established with the given settings.
        /// Does not keep the connection open.
        /// </summary>
        /// <param name="settings">Database configuration settings</param>
        /// <returns>True if connection test succeeded</returns>
        public static bool TestConnection(IDatabaseSettings settings)
        {
            IDatabaseConnection connection = settings.DatabaseType == MYSQL
                ? new MySQLDatabaseConnection(settings)
                : new SQLiteDatabaseConnection(settings);

            return connection.TestConnection();
        }
    }
}