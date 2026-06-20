using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Database
{
    /// <summary>
    /// Database abstraction interface that supports both SQLite and MySQL.
    /// Unified API for database operations.
    /// </summary>
    public interface IDatabaseConnection : IDisposable
    {
        // Query operations

        /// <summary>
        /// Returns a query builder for the specified table type
        /// </summary>
        ITableQuery<T> Table<T>() where T : new();

        /// <summary>
        /// Finds an entity by primary key
        /// </summary>
        T Find<T>(object pk) where T : new();

        /// <summary>
        /// Finds the first entity matching the predicate
        /// </summary>
        T Find<T>(Expression<Func<T, bool>> predicate) where T : new();

        /// <summary>
        /// Executes a raw SQL query and maps results to the specified type
        /// </summary>
        List<T> Query<T>(string query, params object[] args) where T : new();

        /// <summary>
        /// Executes a SQL query returning scalar values
        /// </summary>
        List<T> QueryScalars<T>(string query, params object[] args);

        /// <summary>
        /// Executes a SQL query returning a single scalar value
        /// </summary>
        T ExecuteScalar<T>(string query, params object[] args);

        /// <summary>
        /// Executes a non-query SQL statement
        /// </summary>
        int Execute(string query, params object[] args);

        // CRUD operations

        /// <summary>
        /// Inserts an object into the database
        /// </summary>
        int Insert(object obj);

        /// <summary>
        /// Inserts multiple objects into the database
        /// </summary>
        int InsertAll(IEnumerable objects, bool runInTransaction = true);

        /// <summary>
        /// Inserts or replaces an object (upsert)
        /// </summary>
        int InsertOrReplace(object obj);

        /// <summary>
        /// Updates an object in the database
        /// </summary>
        int Update(object obj);

        /// <summary>
        /// Updates multiple objects in the database
        /// </summary>
        int UpdateAll(IEnumerable objects, bool runInTransaction = true);

        /// <summary>
        /// Deletes an object from the database
        /// </summary>
        int Delete(object objectToDelete);

        /// <summary>
        /// Deletes an object by primary key
        /// </summary>
        int Delete<T>(object primaryKey);

        /// <summary>
        /// Deletes all rows from the specified table
        /// </summary>
        int DeleteAll<T>();

        // Schema operations

        /// <summary>
        /// Creates a table for the specified type if it doesn't exist
        /// </summary>
        CreateTableResult CreateTable<T>(CreateFlags createFlags = CreateFlags.None);

        /// <summary>
        /// Creates an index on the specified columns
        /// </summary>
        int CreateIndex(string tableName, string[] columnNames, bool unique = false);

        /// <summary>
        /// Creates an index on a single column
        /// </summary>
        int CreateIndex(string tableName, string columnName, bool unique = false);

        /// <summary>
        /// Gets information about the columns in a table
        /// </summary>
        List<ColumnInfo> GetTableInfo(string tableName);

        // Transaction operations

        /// <summary>
        /// Begins a new transaction
        /// </summary>
        void BeginTransaction();

        /// <summary>
        /// Commits the current transaction
        /// </summary>
        void Commit();

        /// <summary>
        /// Rolls back the current transaction
        /// </summary>
        void Rollback();

        /// <summary>
        /// Executes an action within a transaction
        /// </summary>
        void RunInTransaction(Action action);

        // Connection management

        /// <summary>
        /// Initializes the connection. Call before first use.
        /// </summary>
        void Initialize();

        /// <summary>
        /// Closes the database connection
        /// </summary>
        void Close();

        /// <summary>
        /// Checks if the connection is currently open
        /// </summary>
        bool IsConnected();

        /// <summary>
        /// Tests if a connection can be established
        /// </summary>
        bool TestConnection();

        // Database utilities

        /// <summary>
        /// Gets the size of the database in bytes.
        /// For SQLite: returns file size. For MySQL: queries information_schema.
        /// </summary>
        long GetDatabaseSize();

        /// <summary>
        /// Optimizes the database (vacuum, analyze, etc.).
        /// Returns the number of bytes saved (SQLite) or 0 (MySQL).
        /// </summary>
        long Optimize();

        /// <summary>
        /// Checks if a column exists in a table.
        /// </summary>
        bool ColumnExists(string tableName, string columnName);

        /// <summary>
        /// Creates a backup of the database at the specified path.
        /// Supported for SQLite. MySQL implementations may throw NotSupportedException.
        /// </summary>
        void Backup(string destinationPath);

        // Properties

        /// <summary>
        /// Gets the database path (for SQLite) or connection info
        /// </summary>
        string DatabasePath { get; }

        /// <summary>
        /// Indicates if currently within a transaction
        /// </summary>
        bool IsInTransaction { get; }

        /// <summary>
        /// Per-command timeout override in seconds. 0 means use connection default.
        /// </summary>
        int CommandTimeout { get; set; }
    }
}
