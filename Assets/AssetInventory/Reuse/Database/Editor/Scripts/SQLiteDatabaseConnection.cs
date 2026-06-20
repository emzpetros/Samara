using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using Database.Internal;
using SQLite;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Database
{
    /// <summary>
    /// SQLite implementation of IDatabaseConnection.
    /// Wraps the SQLite-Net library to provide a unified database abstraction.
    /// </summary>
    public class SQLiteDatabaseConnection : BaseDatabaseConnection
    {
        private SQLiteConnection _connection;

        public SQLiteDatabaseConnection(IDatabaseSettings settings) : base(settings)
        {
        }

        public override string DatabasePath => _connection?.DatabasePath ?? Settings.DatabasePath;

        public override bool IsInTransaction => _connection?.IsInTransaction ?? false;

        public override bool IsConnected()
        {
            return _connection != null;
        }

        public override bool TestConnection()
        {
            try
            {
                string dbPath = Settings.DatabasePath;

                // Check if file exists or can be created
                string directory = Path.GetDirectoryName(dbPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Try to open a connection
                using (SQLiteConnection testConn = new SQLiteConnection(dbPath, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create))
                {
                    testConn.ExecuteScalar<int>("SELECT 1");
                    return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"SQLite connection test failed: {e.Message}");
                return false;
            }
        }

        protected override void DoInitialize()
        {
            _connection = new SQLiteConnection(Settings.DatabasePath, SQLiteOpenFlags.Create | SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.FullMutex);
            _connection.BusyTimeout = TimeSpan.FromSeconds(10);

            // Apply SQLite-specific optimizations using settings
            string journalMode = Settings.JournalMode ?? "WAL";
            _connection.ExecuteScalar<string>($"PRAGMA journal_mode={journalMode};");
            _connection.ExecuteScalar<long>("PRAGMA mmap_size = 2000000000");
            _connection.Execute("PRAGMA temp_store = MEMORY");
            _connection.Execute("PRAGMA case_sensitive_like = false;");
            _connection.Execute("PRAGMA synchronous = NORMAL");
            _connection.Execute("PRAGMA cache_size = -20000");
            _connection.Execute("PRAGMA page_size = 8192");
        }

        protected override void DoClose()
        {
            if (_connection != null)
            {
                _connection.Close();
                _connection.Dispose();
                _connection = null;
            }
        }

        private SQLiteConnection Connection
        {
            get
            {
                EnsureConnected();
                return _connection;
            }
        }

        // Query operations
        public override ITableQuery<T> Table<T>()
        {
            return new SQLiteTableQueryWrapper<T>(Connection.Table<T>());
        }

        public override T Find<T>(object pk)
        {
            return Connection.Find<T>(pk);
        }

        public override T Find<T>(Expression<Func<T, bool>> predicate)
        {
            return Connection.Find<T>(predicate);
        }

        public override List<T> Query<T>(string query, params object[] args)
        {
            Stopwatch stopwatch = IsQueryProfilingEnabled() ? Stopwatch.StartNew() : null;
            List<T> result = Connection.Query<T>(query, args);
            LogSqliteProfile($"Query<{typeof(T).Name}>", query, args, result.Count, stopwatch);
            return result;
        }

        public override List<T> QueryScalars<T>(string query, params object[] args)
        {
            Stopwatch stopwatch = IsQueryProfilingEnabled() ? Stopwatch.StartNew() : null;
            List<T> result = Connection.QueryScalars<T>(query, args);
            LogSqliteProfile($"QueryScalars<{typeof(T).Name}>", query, args, result.Count, stopwatch);
            return result;
        }

        public override T ExecuteScalar<T>(string query, params object[] args)
        {
            Stopwatch stopwatch = IsQueryProfilingEnabled() ? Stopwatch.StartNew() : null;
            T result = Connection.ExecuteScalar<T>(query, args);
            LogSqliteProfile($"ExecuteScalar<{typeof(T).Name}>", query, args, result == null ? 0 : 1, stopwatch);
            return result;
        }

        public override int Execute(string query, params object[] args)
        {
            Stopwatch stopwatch = IsQueryProfilingEnabled() ? Stopwatch.StartNew() : null;
            int affectedRows = Connection.Execute(query, args);
            LogSqliteProfile("Execute", query, args, affectedRows, stopwatch);
            return affectedRows;
        }

        // CRUD operations
        public override int Insert(object obj)
        {
            return Connection.Insert(obj);
        }

        public override int InsertAll(IEnumerable objects, bool runInTransaction = true)
        {
            return Connection.InsertAll(objects, runInTransaction);
        }

        public override int InsertOrReplace(object obj)
        {
            return Connection.InsertOrReplace(obj);
        }

        public override int Update(object obj)
        {
            return Connection.Update(obj);
        }

        public override int UpdateAll(IEnumerable objects, bool runInTransaction = true)
        {
            return Connection.UpdateAll(objects, runInTransaction);
        }

        public override int Delete(object objectToDelete)
        {
            return Connection.Delete(objectToDelete);
        }

        public override int Delete<T>(object primaryKey)
        {
            return Connection.Delete<T>(primaryKey);
        }

        public override int DeleteAll<T>()
        {
            return Connection.DeleteAll<T>();
        }

        // Schema operations
        public override CreateTableResult CreateTable<T>(CreateFlags createFlags = CreateFlags.None)
        {
            // Convert Database.CreateFlags to SQLite.CreateFlags
            SQLite.CreateFlags sqliteFlags = (SQLite.CreateFlags)(int)createFlags;
            SQLite.CreateTableResult result = Connection.CreateTable<T>(sqliteFlags);

            // Convert SQLite.CreateTableResult to Database.CreateTableResult
            return result == SQLite.CreateTableResult.Created
                ? CreateTableResult.Created
                : CreateTableResult.Migrated;
        }

        public override int CreateIndex(string tableName, string[] columnNames, bool unique = false)
        {
            return Connection.CreateIndex(tableName, columnNames, unique);
        }

        public override int CreateIndex(string tableName, string columnName, bool unique = false)
        {
            return Connection.CreateIndex(tableName, columnName, unique);
        }

        public override List<ColumnInfo> GetTableInfo(string tableName)
        {
            List<SQLiteConnection.ColumnInfo> sqliteColumns = Connection.GetTableInfo(tableName);

            // Convert SQLiteConnection.ColumnInfo to Database.ColumnInfo
            return sqliteColumns.Select(c => new ColumnInfo(c.Name, c.notnull)).ToList();
        }

        // Transaction operations
        public override void BeginTransaction()
        {
            Connection.BeginTransaction();
        }

        public override void Commit()
        {
            Connection.Commit();
        }

        public override void Rollback()
        {
            Connection.Rollback();
        }

        // Database utilities

        public override long GetDatabaseSize()
        {
            EnsureConnected();
            string dbPath = _connection?.DatabasePath;
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
                return 0;
            return new FileInfo(dbPath).Length;
        }

        public override long Optimize()
        {
            EnsureConnected();
            string dbPath = _connection?.DatabasePath;
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
                return 0;

            long originalSize = new FileInfo(dbPath).Length;
            _connection.Execute("vacuum;");
            _connection.Execute("analyze;");
            _connection.ExecuteScalar<string>("PRAGMA optimize;");
            return originalSize - new FileInfo(dbPath).Length;
        }

        /// <summary>
        /// Creates a backup of the SQLite database at the specified path.
        /// </summary>
        /// <param name="destinationPath">Path where the backup will be created</param>
        public override void Backup(string destinationPath)
        {
            EnsureConnected();
            _connection?.Backup(destinationPath);
        }

        private static bool IsQueryProfilingEnabled()
        {
            return IsEnvironmentEnabled("ASSET_INVENTORY_PROFILE_SQLITE")
                || IsEnvironmentEnabled("ASSET_INVENTORY_PROFILE_SEARCH");
        }

        private static bool ShouldIncludeProfileSql()
        {
            return IsEnvironmentEnabled("ASSET_INVENTORY_PROFILE_SQLITE_SQL")
                || IsEnvironmentEnabled("ASSET_INVENTORY_PROFILE_SEARCH_SQL");
        }

        private static bool IsEnvironmentEnabled(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static void LogSqliteProfile(string operation, string sql, object[] args, int rows, Stopwatch stopwatch)
        {
            if (stopwatch == null) return;

            StringBuilder sb = new StringBuilder();
            sb.Append("[Database][SQLite] ");
            sb.Append(operation);
            sb.Append($": {stopwatch.ElapsedMilliseconds} ms");
            sb.Append($", rows={rows}");
            sb.Append($", args={args?.Length ?? 0}");

            if (ShouldIncludeProfileSql())
            {
                sb.AppendLine();
                sb.Append(TruncateProfileSql(sql, 4000));
            }

            Debug.Log(sb.ToString());
        }

        private static string TruncateProfileSql(string sql, int maxLength)
        {
            if (string.IsNullOrEmpty(sql)) return string.Empty;
            if (maxLength <= 0 || sql.Length <= maxLength) return sql;
            return sql.Substring(0, maxLength) + "...";
        }
    }
}
