using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Text.RegularExpressions;
using Database.Internal;
using MySqlConnector;
using SQLite;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Database
{
    /// <summary>
    /// MySQL implementation of IDatabaseConnection using MySqlConnector.
    /// Full MySQL support with expression tree to SQL translation.
    /// </summary>
    public class MySQLDatabaseConnection : BaseDatabaseConnection
    {
        private sealed class ExistingColumnSchema
        {
            public string Name { get; set; }
            public string DataType { get; set; }
            public long? CharacterMaximumLength { get; set; }
            public string IsNullable { get; set; }
            public string CollationName { get; set; }
        }

        private MySqlConnection _connection;
        private readonly string _connectionString;
        private readonly string _databaseName;
        private bool _inTransaction;
        private MySqlTransaction _currentTransaction;

        private static readonly Dictionary<string, TableMapping> _mappings = new Dictionary<string, TableMapping>();
        private static readonly Dictionary<string, Dictionary<string, TableMapping.Column>> _columnLookupCache = new Dictionary<string, Dictionary<string, TableMapping.Column>>();

        public MySQLDatabaseConnection(IDatabaseSettings settings) : base(settings)
        {
            MySqlConnectionStringBuilder builder = new MySqlConnectionStringBuilder
            {
                Server = settings.MySqlHost ?? "localhost",
                Port = (uint)(settings.MySqlPort > 0 ? settings.MySqlPort : 3306),
                Database = settings.MySqlDatabase ?? "",
                UserID = settings.MySqlUser ?? "",
                Password = settings.MySqlPassword ?? "",
                SslMode = settings.MySqlUseSSL ? MySqlSslMode.Required : MySqlSslMode.None,
                ConnectionTimeout = (uint)(settings.MySqlConnectionTimeout > 0 ? settings.MySqlConnectionTimeout : 30),
                DefaultCommandTimeout = (uint)(settings.MySqlConnectionTimeout > 0 ? settings.MySqlConnectionTimeout : 30),
                AllowUserVariables = true,
                AllowLoadLocalInfile = false,
                ConvertZeroDateTime = true,
                Pooling = true,
                MinimumPoolSize = 2,
                MaximumPoolSize = 10,
                ConnectionLifeTime = 300,
                UseCompression = false
            };

            _connectionString = builder.ConnectionString;
            _databaseName = settings.MySqlDatabase ?? "";
        }

        public override string DatabasePath => _connection?.Database ?? _databaseName;

        public override bool IsInTransaction => _inTransaction;

        public override bool IsConnected()
        {
            return _connection != null && _connection.State == ConnectionState.Open;
        }

        public override bool TestConnection()
        {
            try
            {
                using (MySqlConnection testConn = new MySqlConnection(_connectionString))
                {
                    testConn.Open();
                    using (MySqlCommand cmd = new MySqlCommand("SELECT 1", testConn))
                    {
                        cmd.ExecuteScalar();
                    }
                    return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"MySQL connection test failed: {e.Message}");
                return false;
            }
        }

        protected override void DoInitialize()
        {
            if (_connection != null)
            {
                try
                {
                    if (_connection.State != ConnectionState.Closed)
                    {
                        _connection.Close();
                    }
                }
                catch
                {
                    // Recreate the connection below.
                }

                _connection.Dispose();
                _connection = null;
            }

            _connection = new MySqlConnection(_connectionString);
            _connection.Open();
            EnsureDatabaseExists();
        }

        protected override void DoClose()
        {
            if (_inTransaction)
            {
                Rollback();
            }

            if (_connection != null)
            {
                if (_connection.State == ConnectionState.Open)
                {
                    _connection.Close();
                }
                _connection.Dispose();
                _connection = null;
            }
        }

        private void EnsureDatabaseExists()
        {
            if (string.IsNullOrEmpty(_databaseName)) return;

            try
            {
                MySqlConnectionStringBuilder builder = new MySqlConnectionStringBuilder(_connectionString);
                builder.Database = "";
                using (MySqlConnection tempConn = new MySqlConnection(builder.ConnectionString))
                {
                    tempConn.Open();
                    using (MySqlCommand cmd = new MySqlCommand($"CREATE DATABASE IF NOT EXISTS `{_databaseName}`", tempConn))
                    {
                        cmd.ExecuteNonQuery();
                    }
                }

                _connection.ChangeDatabase(_databaseName);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Could not create database '{_databaseName}': {e.Message}");
            }
        }

        // Query operations

        public override ITableQuery<T> Table<T>()
        {
            EnsureConnected();
            return new MySQLTableQuery<T>(this);
        }

        public override T Find<T>(object pk)
        {
            EnsureConnected();
            TableMapping mapping = GetMapping<T>();
            TableMapping.Column pkColumn = mapping.PK ?? mapping.Columns.FirstOrDefault();
            if (pkColumn == null)
                throw new Exception($"No primary key found for type {typeof (T).Name}");

            string sql = $"SELECT * FROM `{mapping.TableName}` WHERE `{pkColumn.Name}` = @pk LIMIT 1";

            using (MySqlCommand cmd = new MySqlCommand(sql, _connection, _currentTransaction))
            {
                cmd.Parameters.Add(new MySqlParameter("@pk", pk));

                using (MySqlDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        T obj = new T();
                        MapReaderToObject(reader, obj, mapping);
                        reader.Close();
                        return obj;
                    }
                    reader.Close();
                }
            }

            return default(T);
        }

        public override T Find<T>(Expression<Func<T, bool>> predicate)
        {
            EnsureConnected();
            ITableQuery<T> query = Table<T>().Where(predicate);
            return query.FirstOrDefault();
        }

        public override List<T> Query<T>(string query, params object[] args)
        {
            EnsureConnected();
            List<T> result = new List<T>();
            TableMapping mapping = GetMapping<T>();
            string mysqlQuery = ConvertSqliteToMySQL(query, args);
            Stopwatch stopwatch = IsQueryProfilingEnabled() ? Stopwatch.StartNew() : null;
            long readerOpenMs = 0;

            using (MySqlCommand cmd = new MySqlCommand(mysqlQuery, _connection, _currentTransaction))
            {
                if (CommandTimeout > 0) cmd.CommandTimeout = CommandTimeout;
                AddParameters(cmd, args);
                using (MySqlDataReader reader = cmd.ExecuteReader())
                {
                    readerOpenMs = stopwatch?.ElapsedMilliseconds ?? 0;
                    while (reader.Read())
                    {
                        T obj = new T();
                        MapReaderToObject(reader, obj, mapping);
                        result.Add(obj);
                    }
                    reader.Close();
                }
            }

            LogMySqlProfile($"Query<{typeof(T).Name}>", mysqlQuery, args, result.Count, stopwatch, readerOpenMs);
            return result;
        }

        public override List<T> QueryScalars<T>(string query, params object[] args)
        {
            EnsureConnected();
            List<T> result = new List<T>();
            string mysqlQuery = ConvertSqliteToMySQL(query, args);
            Stopwatch stopwatch = IsQueryProfilingEnabled() ? Stopwatch.StartNew() : null;
            long readerOpenMs = 0;

            using (MySqlCommand cmd = new MySqlCommand(mysqlQuery, _connection, _currentTransaction))
            {
                if (CommandTimeout > 0) cmd.CommandTimeout = CommandTimeout;
                AddParameters(cmd, args);
                using (MySqlDataReader reader = cmd.ExecuteReader())
                {
                    readerOpenMs = stopwatch?.ElapsedMilliseconds ?? 0;
                    while (reader.Read())
                    {
                        if (reader.FieldCount > 0 && !reader.IsDBNull(0))
                        {
                            object value = reader.GetValue(0);
                            result.Add((T)Convert.ChangeType(value, typeof (T)));
                        }
                    }
                }
            }

            LogMySqlProfile($"QueryScalars<{typeof(T).Name}>", mysqlQuery, args, result.Count, stopwatch, readerOpenMs);
            return result;
        }

        public override T ExecuteScalar<T>(string query, params object[] args)
        {
            EnsureConnected();
            string mysqlQuery = ConvertSqliteToMySQL(query, args);
            Stopwatch stopwatch = IsQueryProfilingEnabled() ? Stopwatch.StartNew() : null;
            using (MySqlCommand cmd = new MySqlCommand(mysqlQuery, _connection, _currentTransaction))
            {
                if (CommandTimeout > 0) cmd.CommandTimeout = CommandTimeout;
                AddParameters(cmd, args);
                object result = cmd.ExecuteScalar();
                LogMySqlProfile($"ExecuteScalar<{typeof(T).Name}>", mysqlQuery, args, result == null || result == DBNull.Value ? 0 : 1, stopwatch);
                if (result == null || result == DBNull.Value) return default(T);
                return (T)Convert.ChangeType(result, typeof (T));
            }
        }

        public override int Execute(string query, params object[] args)
        {
            EnsureConnected();
            string mysqlQuery = ConvertSqliteToMySQL(query, args);
            Stopwatch stopwatch = IsQueryProfilingEnabled() ? Stopwatch.StartNew() : null;
            using (MySqlCommand cmd = new MySqlCommand(mysqlQuery, _connection, _currentTransaction))
            {
                if (CommandTimeout > 0) cmd.CommandTimeout = CommandTimeout;
                AddParameters(cmd, args);
                int affectedRows = cmd.ExecuteNonQuery();
                LogMySqlProfile("Execute", mysqlQuery, args, affectedRows, stopwatch);
                return affectedRows;
            }
        }

        // CRUD operations

        public override int Insert(object obj)
        {
            EnsureConnected();
            if (obj == null) return 0;
            return InsertInternal(obj, "");
        }

        public override int InsertAll(IEnumerable objects, bool runInTransaction = true)
        {
            EnsureConnected();

            List<object> objectList = objects.Cast<object>().ToList();
            if (objectList.Count == 0) return 0;

            int count = 0;

            if (runInTransaction)
            {
                RunInTransaction(() =>
                {
                    count = InsertAllBatch(objectList);
                });
            }
            else
            {
                count = InsertAllBatch(objectList);
            }

            return count;
        }

        private int InsertAllBatch(List<object> objects)
        {
            if (objects.Count == 0) return 0;

            Dictionary<Type, List<object>> objectsByType = objects
                .GroupBy(o => o.GetType())
                .ToDictionary(g => g.Key, g => g.ToList());

            int totalCount = 0;

            foreach (KeyValuePair<Type, List<object>> group in objectsByType)
            {
                Type type = group.Key;
                List<object> batch = group.Value;

                TableMapping mapping = GetMapping(type);
                TableMapping.Column[] insertColumns = mapping.Columns.Where(c => !c.IsAutoInc).ToArray();

                if (insertColumns.Length == 0) continue;

                int batchSize = 1000;
                for (int i = 0; i < batch.Count; i += batchSize)
                {
                    List<object> currentBatch = batch.Skip(i).Take(batchSize).ToList();
                    totalCount += InsertBatch(mapping, insertColumns, currentBatch);
                }
            }

            return totalCount;
        }

        private int InsertBatch(TableMapping mapping, TableMapping.Column[] insertColumns, List<object> objects)
        {
            if (objects.Count == 0) return 0;

            string columnNames = string.Join(", ", insertColumns.Select(c => $"`{c.Name}`"));

            StringBuilder sqlBuilder = new StringBuilder();
            sqlBuilder.Append($"INSERT INTO `{mapping.TableName}` ({columnNames}) VALUES ");

            List<string> valueGroups = new List<string>();
            List<MySqlParameter> parameters = new List<MySqlParameter>();
            int paramIndex = 0;

            foreach (object obj in objects)
            {
                List<string> paramNames = new List<string>();
                foreach (TableMapping.Column col in insertColumns)
                {
                    string paramName = $"@p{paramIndex}";
                    paramNames.Add(paramName);
                    object value = col.GetValue(obj);
                    parameters.Add(new MySqlParameter(paramName, value ?? DBNull.Value));
                    paramIndex++;
                }
                valueGroups.Add($"({string.Join(", ", paramNames)})");
            }

            sqlBuilder.Append(string.Join(", ", valueGroups));

            using (MySqlCommand cmd = new MySqlCommand(sqlBuilder.ToString(), _connection, _currentTransaction))
            {
                cmd.Parameters.AddRange(parameters.ToArray());
                return cmd.ExecuteNonQuery();
            }
        }

        public override int InsertOrReplace(object obj)
        {
            EnsureConnected();
            if (obj == null) return 0;
            return InsertOrReplaceMySQL(obj);
        }

        public override int Update(object obj)
        {
            EnsureConnected();
            if (obj == null) return 0;
            TableMapping mapping = GetMapping(obj.GetType());
            TableMapping.Column pkColumn = mapping.PK ?? mapping.Columns.FirstOrDefault();
            if (pkColumn == null)
                throw new Exception($"No primary key found for type {obj.GetType().Name}");

            object pkValue = pkColumn.GetValue(obj);
            if (pkValue == null) return 0;

            TableMapping.Column[] columns = mapping.Columns.Where(c => c != pkColumn).ToArray();
            string setClause = string.Join(", ", columns.Select(c => $"`{c.Name}` = @{c.Name}"));
            string sql = $"UPDATE `{mapping.TableName}` SET {setClause} WHERE `{pkColumn.Name}` = @pk";

            using (MySqlCommand cmd = new MySqlCommand(sql, _connection, _currentTransaction))
            {
                foreach (TableMapping.Column col in columns)
                {
                    object value = col.GetValue(obj);
                    cmd.Parameters.Add(new MySqlParameter($"@{col.Name}", value ?? DBNull.Value));
                }
                cmd.Parameters.Add(new MySqlParameter("@pk", pkValue));

                return cmd.ExecuteNonQuery();
            }
        }

        public override int UpdateAll(IEnumerable objects, bool runInTransaction = true)
        {
            EnsureConnected();

            List<object> objectList = objects.Cast<object>().ToList();
            if (objectList.Count == 0) return 0;

            int count = 0;

            if (runInTransaction)
            {
                RunInTransaction(() =>
                {
                    count = UpdateAllBatch(objectList);
                });
            }
            else
            {
                count = UpdateAllBatch(objectList);
            }

            return count;
        }

        private int UpdateAllBatch(List<object> objects)
        {
            if (objects.Count == 0) return 0;

            Dictionary<Type, List<object>> objectsByType = objects
                .GroupBy(o => o.GetType())
                .ToDictionary(g => g.Key, g => g.ToList());

            int totalCount = 0;

            foreach (KeyValuePair<Type, List<object>> group in objectsByType)
            {
                Type type = group.Key;
                List<object> batch = group.Value;

                TableMapping mapping = GetMapping(type);
                TableMapping.Column pkColumn = mapping.PK ?? mapping.Columns.FirstOrDefault();
                if (pkColumn == null) continue;

                TableMapping.Column[] updateColumns = mapping.Columns.Where(c => c != pkColumn).ToArray();
                if (updateColumns.Length == 0) continue;

                string setClause = string.Join(", ", updateColumns.Select(c => $"`{c.Name}` = @{c.Name}"));
                string sql = $"UPDATE `{mapping.TableName}` SET {setClause} WHERE `{pkColumn.Name}` = @pk";

                using (MySqlCommand cmd = new MySqlCommand(sql, _connection, _currentTransaction))
                {
                    foreach (TableMapping.Column col in updateColumns)
                    {
                        cmd.Parameters.Add(new MySqlParameter($"@{col.Name}", null));
                    }
                    cmd.Parameters.Add(new MySqlParameter("@pk", null));

                    cmd.Prepare();

                    foreach (object obj in batch)
                    {
                        object pkValue = pkColumn.GetValue(obj);
                        if (pkValue == null) continue;

                        foreach (TableMapping.Column col in updateColumns)
                        {
                            object value = col.GetValue(obj);
                            cmd.Parameters[$"@{col.Name}"].Value = value ?? DBNull.Value;
                        }
                        cmd.Parameters["@pk"].Value = pkValue;

                        totalCount += cmd.ExecuteNonQuery();
                    }
                }
            }

            return totalCount;
        }

        public override int Delete(object objectToDelete)
        {
            EnsureConnected();
            if (objectToDelete == null) return 0;
            TableMapping mapping = GetMapping(objectToDelete.GetType());
            TableMapping.Column pkColumn = mapping.PK ?? mapping.Columns.FirstOrDefault();
            if (pkColumn == null)
                throw new Exception($"No primary key found for type {objectToDelete.GetType().Name}");

            object pkValue = pkColumn.GetValue(objectToDelete);
            if (pkValue == null) return 0;

            return DeleteInternal(objectToDelete.GetType(), pkValue);
        }

        public override int Delete<T>(object primaryKey)
        {
            EnsureConnected();
            return DeleteInternal(typeof (T), primaryKey);
        }

        private int DeleteInternal(Type type, object primaryKey)
        {
            TableMapping mapping = GetMapping(type);
            TableMapping.Column pkColumn = mapping.PK ?? mapping.Columns.FirstOrDefault();
            if (pkColumn == null)
                throw new Exception($"No primary key found for type {type.Name}");

            string sql = $"DELETE FROM `{mapping.TableName}` WHERE `{pkColumn.Name}` = @pk";
            using (MySqlCommand cmd = new MySqlCommand(sql, _connection, _currentTransaction))
            {
                cmd.Parameters.Add(new MySqlParameter("@pk", primaryKey));
                return cmd.ExecuteNonQuery();
            }
        }

        public override int DeleteAll<T>()
        {
            EnsureConnected();
            TableMapping mapping = GetMapping<T>();
            string sql = $"DELETE FROM `{mapping.TableName}`";
            return Execute(sql);
        }

        // Schema operations

        public override CreateTableResult CreateTable<T>(CreateFlags createFlags)
        {
            EnsureConnected();
            return CreateTableInternal(typeof (T), (SQLite.CreateFlags)(int)createFlags);
        }

        private CreateTableResult CreateTableInternal(Type type, SQLite.CreateFlags createFlags)
        {
            EnsureConnected();
            TableMapping mapping = GetMapping(type, createFlags);

            if (mapping.Columns.Length == 0)
            {
                throw new Exception($"Cannot create a table without columns (does '{type.FullName}' have public properties?)");
            }

            try
            {
                List<ColumnInfo> existingCols = GetTableInfo(mapping.TableName);
                CreateTableResult result = existingCols.Count == 0 ? CreateTableResult.Created : CreateTableResult.Migrated;

                if (existingCols.Count == 0)
                {
                    string sql = GenerateCreateTableSQL(mapping);
                    Execute(sql);
                    CreateIndexesForMapping(mapping);
                }
                else
                {
                    MigrateTable(mapping, existingCols);
                    CreateIndexesForMapping(mapping);
                }

                return result;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to create/migrate table '{mapping.TableName}' for type {type.Name}: {ex.Message}");
                throw;
            }
        }

        public override int CreateIndex(string tableName, string[] columnNames, bool unique = false)
        {
            EnsureConnected();
            string indexName = $"idx_{tableName}_{string.Join("_", columnNames)}";
            string uniqueStr = unique ? "UNIQUE" : "";

            Dictionary<string, string> columnTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string sql = "SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS " +
                    "WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table";
                using (MySqlCommand cmd = new MySqlCommand(sql, _connection, _currentTransaction))
                {
                    cmd.Parameters.AddWithValue("@schema", _databaseName);
                    cmd.Parameters.AddWithValue("@table", tableName);
                    using (MySqlDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string colName = reader.GetString(0);
                            string dataType = reader.GetString(1);
                            long? maxLength = reader.IsDBNull(2) ? null : reader.GetInt64(2);
                            columnTypes[colName] = maxLength.HasValue ? $"{dataType}:{maxLength.Value}" : dataType;
                        }
                    }
                }
            }
            catch
            {
                // Continue if query fails
            }

            List<string> columnDefs = new List<string>();
            foreach (string colName in columnNames)
            {
                string colDef = $"`{colName}`";
                bool needsKeyLength = false;

                if (columnTypes.TryGetValue(colName, out string dataType))
                {
                    string[] parts = dataType.Split(':');
                    string upperType = parts[0].ToUpperInvariant();
                    long maxLength = parts.Length > 1 && long.TryParse(parts[1], out long parsedLength) ? parsedLength : 0;
                    if (upperType.Contains("TEXT") || upperType.Contains("BLOB") || (upperType.Contains("CHAR") && maxLength > 255))
                    {
                        needsKeyLength = true;
                    }
                }
                else
                {
                    needsKeyLength = true;
                }

                if (needsKeyLength)
                {
                    colDef = $"`{colName}`(255)";
                }

                columnDefs.Add(colDef);
            }

            string columns = string.Join(", ", columnDefs);

            bool indexExists = false;
            try
            {
                int existing = ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM information_schema.statistics " +
                    "WHERE table_schema = ? AND table_name = ? AND index_name = ?",
                    _databaseName, tableName, indexName);
                indexExists = existing > 0;
            }
            catch
            {
                // Continue if check fails
            }

            if (!indexExists)
            {
                string createSql = $"CREATE {uniqueStr} INDEX `{indexName}` ON `{tableName}` ({columns})";
                return Execute(createSql);
            }
            return 0;
        }

        public override int CreateIndex(string tableName, string columnName, bool unique = false)
        {
            return CreateIndex(tableName, new[] {columnName}, unique);
        }

        public override List<ColumnInfo> GetTableInfo(string tableName)
        {
            EnsureConnected();
            List<ColumnInfo> result = new List<ColumnInfo>();

            string sql = "SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT, COLUMN_KEY, EXTRA " +
                "FROM INFORMATION_SCHEMA.COLUMNS " +
                "WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table";

            using (MySqlCommand cmd = new MySqlCommand(sql, _connection, _currentTransaction))
            {
                cmd.Parameters.AddWithValue("@schema", _databaseName);
                cmd.Parameters.AddWithValue("@table", tableName);
                using (MySqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new ColumnInfo(
                            reader.GetString(0),
                            reader.GetString(2) == "NO" ? 1 : 0
                        ));
                    }
                }
            }

            return result;
        }

        // Transaction operations

        public override void BeginTransaction()
        {
            EnsureConnected();
            if (_inTransaction)
                throw new InvalidOperationException("Already in a transaction");
            _currentTransaction = _connection.BeginTransaction();
            _inTransaction = true;
        }

        public override void Commit()
        {
            if (!_inTransaction) return;
            _currentTransaction?.Commit();
            _currentTransaction?.Dispose();
            _currentTransaction = null;
            _inTransaction = false;
        }

        public override void Rollback()
        {
            if (!_inTransaction) return;
            _currentTransaction?.Rollback();
            _currentTransaction?.Dispose();
            _currentTransaction = null;
            _inTransaction = false;
        }

        // Internal helpers exposed for MySQLTableQuery

        internal TableMapping GetMapping<T>(SQLite.CreateFlags createFlags = SQLite.CreateFlags.None)
        {
            return GetMapping(typeof (T), createFlags);
        }

        private TableMapping GetMapping(Type type, SQLite.CreateFlags createFlags = SQLite.CreateFlags.None)
        {
            string key = $"{type.FullName}_{createFlags}";
            if (!_mappings.TryGetValue(key, out TableMapping mapping))
            {
                using (SQLiteConnection sqliteConn = new SQLiteConnection(":memory:"))
                {
                    mapping = sqliteConn.GetMapping(type, createFlags);
                    _mappings[key] = mapping;
                }
            }
            return mapping;
        }

        // Private helper methods

        private void AddParameters(MySqlCommand cmd, object[] args)
        {
            if (args == null) return;

            for (int i = 0; i < args.Length; i++)
            {
                cmd.Parameters.Add(new MySqlParameter($"@p{i}", args[i] ?? DBNull.Value));
            }
        }

        private string ConvertSqliteToMySQL(string sql, object[] args)
        {
            if (string.IsNullOrEmpty(sql)) return sql;

            if (sql.IndexOf('?') >= 0)
            {
                int paramIndex = 0;
                StringBuilder result = new StringBuilder(sql.Length + Math.Max(0, (args?.Length ?? 0) * 2));
                for (int i = 0; i < sql.Length; i++)
                {
                    if (sql[i] == '?' && paramIndex < (args?.Length ?? 0))
                    {
                        result.Append($"@p{paramIndex}");
                        paramIndex++;
                    }
                    else
                    {
                        result.Append(sql[i]);
                    }
                }
                sql = result.ToString();
            }

            if (sql.IndexOf('[') >= 0)
            {
                sql = Regex.Replace(sql, @"\[([^\]]+)\]", "`$1`");
            }

            if (sql.IndexOf("COLLATE", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                sql = Regex.Replace(sql, @"\bCOLLATE\s+NOCASE\b", "COLLATE utf8mb4_general_ci", RegexOptions.IgnoreCase);
            }

            return sql;
        }

        private static bool IsQueryProfilingEnabled()
        {
            return IsEnvironmentEnabled("ASSET_INVENTORY_PROFILE_MYSQL")
                || IsEnvironmentEnabled("ASSET_INVENTORY_PROFILE_SEARCH");
        }

        private static bool ShouldIncludeProfileSql()
        {
            return IsEnvironmentEnabled("ASSET_INVENTORY_PROFILE_MYSQL_SQL")
                || IsEnvironmentEnabled("ASSET_INVENTORY_PROFILE_SEARCH_SQL");
        }

        private static bool IsEnvironmentEnabled(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static void LogMySqlProfile(string operation, string sql, object[] args, int rows, Stopwatch stopwatch, long readerOpenMs = -1)
        {
            if (stopwatch == null) return;

            long totalMs = stopwatch.ElapsedMilliseconds;
            StringBuilder sb = new StringBuilder();
            sb.Append("[Database][MySQL] ");
            sb.Append(operation);
            sb.Append($": {totalMs} ms");
            if (readerOpenMs >= 0) sb.Append($", reader={readerOpenMs} ms");
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

        private Dictionary<string, TableMapping.Column> GetColumnLookup(TableMapping mapping)
        {
            string key = mapping.TableName;
            if (_columnLookupCache.TryGetValue(key, out Dictionary<string, TableMapping.Column> lookup))
            {
                return lookup;
            }

            lookup = mapping.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
            _columnLookupCache[key] = lookup;
            return lookup;
        }

        private int InsertInternal(object obj, string extra)
        {
            TableMapping mapping = GetMapping(obj.GetType());
            TableMapping.Column[] insertColumns = mapping.Columns
                .Where(c => !c.IsAutoInc || c.GetValue(obj) != null && !c.GetValue(obj).Equals(0))
                .ToArray();
            string columnNames = string.Join(", ", insertColumns.Select(c => $"`{c.Name}`"));
            string parameterNames = string.Join(", ", insertColumns.Select(c => $"@{c.Name}"));

            string sql = $"INSERT {extra} INTO `{mapping.TableName}` ({columnNames}) VALUES ({parameterNames})";

            using (MySqlCommand cmd = new MySqlCommand(sql, _connection, _currentTransaction))
            {
                foreach (TableMapping.Column col in insertColumns)
                {
                    object value = col.GetValue(obj);
                    cmd.Parameters.Add(new MySqlParameter($"@{col.Name}", value ?? DBNull.Value));
                }

                cmd.ExecuteNonQuery();

                TableMapping.Column pkColumn = mapping.PK;
                if (pkColumn != null && pkColumn.IsAutoInc)
                {
                    cmd.CommandText = "SELECT LAST_INSERT_ID()";
                    object newId = cmd.ExecuteScalar();
                    if (newId != null)
                    {
                        pkColumn.SetValue(obj, Convert.ChangeType(newId, pkColumn.ColumnType));
                    }
                }

                return 1;
            }
        }

        private int InsertOrReplaceMySQL(object obj)
        {
            TableMapping mapping = GetMapping(obj.GetType());
            TableMapping.Column[] insertColumns = mapping.Columns
                .Where(c => !c.IsAutoInc || c.GetValue(obj) != null && !c.GetValue(obj).Equals(0))
                .ToArray();
            string columnNames = string.Join(", ", insertColumns.Select(c => $"`{c.Name}`"));
            string parameterNames = string.Join(", ", insertColumns.Select(c => $"@{c.Name}"));

            string updateClause = string.Join(", ", insertColumns.Where(c => c != mapping.PK).Select(c => $"`{c.Name}` = VALUES(`{c.Name}`)"));

            string sql = $"INSERT INTO `{mapping.TableName}` ({columnNames}) VALUES ({parameterNames}) " +
                $"ON DUPLICATE KEY UPDATE {updateClause}";

            using (MySqlCommand cmd = new MySqlCommand(sql, _connection, _currentTransaction))
            {
                foreach (TableMapping.Column col in insertColumns)
                {
                    object value = col.GetValue(obj);
                    cmd.Parameters.Add(new MySqlParameter($"@{col.Name}", value ?? DBNull.Value));
                }

                return cmd.ExecuteNonQuery();
            }
        }

        private void MapReaderToObject<T>(MySqlDataReader reader, T obj, TableMapping mapping)
        {
            Dictionary<string, TableMapping.Column> columnLookup = GetColumnLookup(mapping);

            for (int i = 0; i < reader.FieldCount; i++)
            {
                string columnName = reader.GetName(i);
                if (columnLookup.TryGetValue(columnName, out TableMapping.Column column) && !reader.IsDBNull(i))
                {
                    object value = reader.GetValue(i);
                    column.SetValue(obj, ConvertValue(value, column.ColumnType));
                }
            }
        }

        private object ConvertValue(object value, Type targetType)
        {
            if (value == null || value == DBNull.Value) return null;

            if (targetType.IsEnum)
            {
                return Enum.ToObject(targetType, Convert.ToInt32(value));
            }

            if (targetType == typeof (DateTime))
            {
                if (value is DateTime dt) return dt;
                if (value is string str) return DateTime.Parse(str);
                return Convert.ToDateTime(value);
            }

            return Convert.ChangeType(value, Nullable.GetUnderlyingType(targetType) ?? targetType);
        }

        private string GenerateCreateTableSQL(TableMapping mapping)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append($"CREATE TABLE IF NOT EXISTS `{mapping.TableName}` (");

            List<string> columnDefs = new List<string>();
            foreach (TableMapping.Column col in mapping.Columns)
            {
                string def = GenerateColumnDefinition(col, mapping);
                columnDefs.Add(def);
            }

            if (mapping.PK != null)
            {
                bool hasPrimaryKey = columnDefs.Any(d => d.Contains("PRIMARY KEY"));
                if (!hasPrimaryKey)
                {
                    Type pkType = mapping.PK.ColumnType;
                    pkType = Nullable.GetUnderlyingType(pkType) ?? pkType;

                    string pkColumn = pkType == typeof (string)
                        ? $"`{mapping.PK.Name}`({GetPreferredStringLength(mapping.TableName, mapping.PK) ?? 255})"
                        : $"`{mapping.PK.Name}`";

                    columnDefs.Add($"PRIMARY KEY ({pkColumn})");
                }
            }

            sb.Append(string.Join(", ", columnDefs));
            sb.Append(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci");

            return sb.ToString();
        }

        private string GenerateColumnDefinition(TableMapping.Column col, TableMapping mapping)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append($"`{col.Name}` ");

            string sqlType = GetColumnSqlType(mapping, col);
            sb.Append(sqlType);

            if (col.IsAutoInc && col == mapping.PK)
            {
                sb.Append(" AUTO_INCREMENT");
            }

            if (!col.IsNullable)
            {
                sb.Append(" NOT NULL");
            }

            if (col.Collation == "NOCASE")
            {
                sb.Append(" COLLATE utf8mb4_general_ci");
            }

            return sb.ToString();
        }

        private string GetColumnSqlType(TableMapping mapping, TableMapping.Column col)
        {
            Type type = Nullable.GetUnderlyingType(col.ColumnType) ?? col.ColumnType;

            if (type == typeof (string))
            {
                int? preferredLength = GetPreferredStringLength(mapping.TableName, col);
                if (preferredLength.HasValue)
                {
                    return $"VARCHAR({preferredLength.Value})";
                }

                return "TEXT";
            }

            return MapTypeToMySQL(type, col.IsAutoInc);
        }

        private string MapTypeToMySQL(Type type, bool isAutoInc)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;

            if (type == typeof (int)) return "INT";
            if (type == typeof (long)) return "BIGINT";
            if (type == typeof (float)) return "FLOAT";
            if (type == typeof (double)) return "DOUBLE";
            if (type == typeof (bool)) return "TINYINT(1)";
            if (type == typeof (DateTime)) return "DATETIME";
            if (type == typeof (string)) return "TEXT";
            if (type.IsEnum) return "INT";

            return "TEXT";
        }

        private void CreateIndexesForMapping(TableMapping mapping)
        {
            Dictionary<string, IndexInfo> indexes = new Dictionary<string, IndexInfo>();
            Dictionary<string, TableMapping.Column> columnMap = new Dictionary<string, TableMapping.Column>(StringComparer.OrdinalIgnoreCase);

            foreach (TableMapping.Column col in mapping.Columns)
            {
                columnMap[col.Name] = col;
            }

            foreach (TableMapping.Column col in mapping.Columns)
            {
                foreach (IndexedAttribute idx in col.Indices)
                {
                    string indexName = idx.Name ?? $"{mapping.TableName}_{col.Name}";
                    if (!indexes.ContainsKey(indexName))
                    {
                        indexes[indexName] = new IndexInfo
                        {
                            IndexName = indexName,
                            TableName = mapping.TableName,
                            Unique = idx.Unique,
                            Columns = new List<IndexedColumn>()
                        };
                    }
                    indexes[indexName].Columns.Add(new IndexedColumn {Order = idx.Order, ColumnName = col.Name});
                }
            }

            foreach (IndexInfo index in indexes.Values)
            {
                string[] columnNames = index.Columns.OrderBy(i => i.Order).Select(i => i.ColumnName).ToArray();
                CreateIndexWithTypes(index.TableName, columnNames, index.Unique, columnMap);
            }
        }

        private int CreateIndexWithTypes(string tableName, string[] columnNames, bool unique, Dictionary<string, TableMapping.Column> columnMap)
        {
            EnsureConnected();
            string indexName = $"idx_{tableName}_{string.Join("_", columnNames)}";
            string uniqueStr = unique ? "UNIQUE" : "";

            List<string> columnDefs = new List<string>();
            foreach (string colName in columnNames)
            {
                string colDef = $"`{colName}`";

                if (columnMap.TryGetValue(colName, out TableMapping.Column col))
                {
                    Type columnType = col.ColumnType;
                    columnType = Nullable.GetUnderlyingType(columnType) ?? columnType;

                    if (columnType == typeof (string))
                    {
                        int? preferredLength = GetPreferredStringLength(tableName, col);
                        if (!preferredLength.HasValue || preferredLength.Value > 255)
                        {
                            colDef = $"`{colName}`(255)";
                        }
                    }
                }
                else
                {
                    try
                    {
                        string sql = "SELECT DATA_TYPE, CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS " +
                            "WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table AND COLUMN_NAME = @column";
                        using (MySqlCommand cmd = new MySqlCommand(sql, _connection, _currentTransaction))
                        {
                            cmd.Parameters.AddWithValue("@schema", _databaseName);
                            cmd.Parameters.AddWithValue("@table", tableName);
                            cmd.Parameters.AddWithValue("@column", colName);
                            using (MySqlDataReader reader = cmd.ExecuteReader())
                            {
                                if (reader.Read())
                                {
                                    string dataType = reader.GetString(0).ToUpperInvariant();
                                    long? maxLength = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                                    if (dataType.Contains("TEXT") || dataType.Contains("BLOB") || (dataType.Contains("CHAR") && maxLength.GetValueOrDefault() > 255))
                                    {
                                        colDef = $"`{colName}`(255)";
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        colDef = $"`{colName}`(255)";
                    }
                }

                columnDefs.Add(colDef);
            }

            string columns = string.Join(", ", columnDefs);

            bool indexExists = false;
            try
            {
                int existing = ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM information_schema.statistics " +
                    "WHERE table_schema = ? AND table_name = ? AND index_name = ?",
                    _databaseName, tableName, indexName);
                indexExists = existing > 0;
            }
            catch
            {
                // Continue
            }

            if (!indexExists)
            {
                try
                {
                    string createSql = $"CREATE {uniqueStr} INDEX `{indexName}` ON `{tableName}` ({columns})";
                    return Execute(createSql);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Failed to create index `{indexName}` on table `{tableName}` with columns ({columns}): {ex.Message}");
                    throw;
                }
            }
            return 0;
        }

        private void MigrateTable(TableMapping mapping, List<ColumnInfo> existingCols)
        {
            HashSet<string> existingColumnNames = existingCols.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, ExistingColumnSchema> existingSchemas = GetExistingColumnSchemas(mapping.TableName);

            foreach (TableMapping.Column col in mapping.Columns)
            {
                if (!existingColumnNames.Contains(col.Name))
                {
                    string alterSql = $"ALTER TABLE `{mapping.TableName}` ADD COLUMN {GenerateColumnDefinition(col, mapping)}";
                    Execute(alterSql);
                    continue;
                }

                if (RequiresColumnMigration(mapping, col, existingSchemas))
                {
                    string alterSql = $"ALTER TABLE `{mapping.TableName}` MODIFY COLUMN {GenerateColumnDefinition(col, mapping)}";
                    Execute(alterSql);
                }
            }
        }

        private Dictionary<string, ExistingColumnSchema> GetExistingColumnSchemas(string tableName)
        {
            Dictionary<string, ExistingColumnSchema> result = new Dictionary<string, ExistingColumnSchema>(StringComparer.OrdinalIgnoreCase);

            string sql = "SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE, COLLATION_NAME " +
                "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table";

            using (MySqlCommand cmd = new MySqlCommand(sql, _connection, _currentTransaction))
            {
                cmd.Parameters.AddWithValue("@schema", _databaseName);
                cmd.Parameters.AddWithValue("@table", tableName);

                using (MySqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        ExistingColumnSchema schema = new ExistingColumnSchema
                        {
                            Name = reader.GetString(0),
                            DataType = reader.GetString(1),
                            CharacterMaximumLength = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                            IsNullable = reader.GetString(3),
                            CollationName = reader.IsDBNull(4) ? null : reader.GetString(4)
                        };

                        result[schema.Name] = schema;
                    }
                }
            }

            return result;
        }

        private bool RequiresColumnMigration(TableMapping mapping, TableMapping.Column col, Dictionary<string, ExistingColumnSchema> existingSchemas)
        {
            if (!existingSchemas.TryGetValue(col.Name, out ExistingColumnSchema existing))
            {
                return false;
            }

            Type columnType = Nullable.GetUnderlyingType(col.ColumnType) ?? col.ColumnType;
            if (columnType == typeof (string))
            {
                int? preferredLength = GetPreferredStringLength(mapping.TableName, col);
                if (preferredLength.HasValue)
                {
                    if (!existing.DataType.Equals("varchar", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (existing.CharacterMaximumLength.GetValueOrDefault() != preferredLength.Value)
                    {
                        return true;
                    }

                    if (col.Collation == "NOCASE")
                    {
                        return !string.Equals(existing.CollationName, "utf8mb4_general_ci", StringComparison.OrdinalIgnoreCase);
                    }

                    return false;
                }

                if (existing.DataType.IndexOf("text", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return true;
                }

                if (col.Collation == "NOCASE")
                {
                    return !string.Equals(existing.CollationName, "utf8mb4_general_ci", StringComparison.OrdinalIgnoreCase);
                }

                return false;
            }

            if (col.Collation == "NOCASE")
            {
                return !string.Equals(existing.CollationName, "utf8mb4_general_ci", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private int? GetPreferredStringLength(string tableName, TableMapping.Column col)
        {
            if (col.MaxStringLength.HasValue)
            {
                return col.MaxStringLength.Value;
            }

            switch (tableName)
            {
                case "AssetFile":
                    if (col.Name.Equals("Path", StringComparison.OrdinalIgnoreCase)) return 2048;
                    if (col.Name.Equals("Guid", StringComparison.OrdinalIgnoreCase)) return 255;
                    if (col.Name.Equals("FileName", StringComparison.OrdinalIgnoreCase)) return 255;
                    if (col.Name.Equals("FileVersion", StringComparison.OrdinalIgnoreCase)) return 255;
                    if (col.Name.Equals("FileStatus", StringComparison.OrdinalIgnoreCase)) return 255;
                    if (col.Name.Equals("Type", StringComparison.OrdinalIgnoreCase)) return 255;
                    break;
                case "Asset":
                    if (col.Name.Equals("Location", StringComparison.OrdinalIgnoreCase)) return 2048;
                    if (col.Name.Equals("DisplayName", StringComparison.OrdinalIgnoreCase)) return 255;
                    if (col.Name.Equals("SafeName", StringComparison.OrdinalIgnoreCase)) return 255;
                    if (col.Name.Equals("DisplayPublisher", StringComparison.OrdinalIgnoreCase)) return 255;
                    if (col.Name.Equals("SafePublisher", StringComparison.OrdinalIgnoreCase)) return 255;
                    if (col.Name.Equals("DisplayCategory", StringComparison.OrdinalIgnoreCase)) return 255;
                    if (col.Name.Equals("SafeCategory", StringComparison.OrdinalIgnoreCase)) return 255;
                    if (col.Name.Equals("Slug", StringComparison.OrdinalIgnoreCase)) return 255;
                    if (col.Name.Equals("Version", StringComparison.OrdinalIgnoreCase)) return 50;
                    if (col.Name.Equals("LatestVersion", StringComparison.OrdinalIgnoreCase)) return 50;
                    if (col.Name.Equals("License", StringComparison.OrdinalIgnoreCase)) return 50;
                    if (col.Name.Equals("ETag", StringComparison.OrdinalIgnoreCase)) return 50;
                    break;
            }

            return null;
        }

        // Database utilities

        public override long GetDatabaseSize()
        {
            EnsureConnected();
            try
            {
                object result = ExecuteScalar<object>(
                    "SELECT COALESCE(SUM(data_length + index_length), 0) " +
                    "FROM information_schema.tables " +
                    "WHERE table_schema = ?",
                    _databaseName);
                if (result == null) return 0;
                return Convert.ToInt64(result);
            }
            catch
            {
                return 0;
            }
        }

        public override long Optimize()
        {
            EnsureConnected();
            try
            {
                List<string> tables = QueryScalars<string>(
                    "SELECT table_name FROM information_schema.tables WHERE table_schema = ?",
                    _databaseName);

                foreach (string table in tables)
                {
                    Execute($"OPTIMIZE TABLE `{table}`");
                }
                return 0; // MySQL doesn't easily report size reduction
            }
            catch
            {
                return 0;
            }
        }

        public override void Backup(string destinationPath)
        {
            throw new NotSupportedException("MySQL backup via API is not supported. Use mysqldump or MySQL Workbench for backups.");
        }

        // Helper classes

        private class IndexInfo
        {
            public string IndexName { get; set; }
            public string TableName { get; set; }
            public bool Unique { get; set; }
            public List<IndexedColumn> Columns { get; set; }
        }

        private class IndexedColumn
        {
            public int Order { get; set; }
            public string ColumnName { get; set; }
        }
    }
}
