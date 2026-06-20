using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using UnityEngine;

namespace Database
{
    /// <summary>
    /// Abstract base class for database connections providing common functionality.
    /// Concrete implementations should override the abstract methods and provide
    /// database-specific behavior.
    /// </summary>
    public abstract class BaseDatabaseConnection : IDatabaseConnection
    {
        /// <summary>
        /// The settings used to configure this connection
        /// </summary>
        protected IDatabaseSettings Settings { get; }

        /// <summary>
        /// Whether the connection has been initialized
        /// </summary>
        protected bool IsInitialized { get; private set; }

        protected BaseDatabaseConnection(IDatabaseSettings settings)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof (settings));
        }

        /// <summary>
        /// Ensures the connection is initialized before performing operations
        /// </summary>
        protected virtual void EnsureConnected()
        {
            if (!IsInitialized || !IsConnected())
            {
                Initialize();
            }
        }

        /// <summary>
        /// Initializes the database connection
        /// </summary>
        public void Initialize()
        {
            if (IsInitialized && IsConnected()) return;

            try
            {
                DoInitialize();
                IsInitialized = true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to initialize database connection: {e.Message}");
                throw;
            }
        }

        /// <summary>
        /// Closes the database connection
        /// </summary>
        public void Close()
        {
            if (!IsInitialized) return;

            try
            {
                DoClose();
            }
            finally
            {
                IsInitialized = false;
            }
        }

        /// <summary>
        /// Disposes of the connection
        /// </summary>
        public void Dispose()
        {
            Close();
        }

        /// <summary>
        /// Executes an action within a transaction, with automatic commit/rollback
        /// </summary>
        public void RunInTransaction(Action action)
        {
            EnsureConnected();

            bool wasInTransaction = IsInTransaction;
            if (!wasInTransaction)
            {
                BeginTransaction();
            }

            try
            {
                action();
                if (!wasInTransaction)
                {
                    Commit();
                }
            }
            catch
            {
                if (!wasInTransaction)
                {
                    Rollback();
                }
                throw;
            }
        }

        // Abstract methods to be implemented by concrete classes

        /// <summary>
        /// Performs the actual initialization. Called by Initialize().
        /// </summary>
        protected abstract void DoInitialize();

        /// <summary>
        /// Performs the actual close. Called by Close().
        /// </summary>
        protected abstract void DoClose();

        // Interface implementation - abstract methods

        public abstract ITableQuery<T> Table<T>() where T : new();
        public abstract T Find<T>(object pk) where T : new();
        public abstract T Find<T>(Expression<Func<T, bool>> predicate) where T : new();
        public abstract List<T> Query<T>(string query, params object[] args) where T : new();
        public abstract List<T> QueryScalars<T>(string query, params object[] args);
        public abstract T ExecuteScalar<T>(string query, params object[] args);
        public abstract int Execute(string query, params object[] args);

        public abstract int Insert(object obj);
        public abstract int InsertAll(IEnumerable objects, bool runInTransaction = true);
        public abstract int InsertOrReplace(object obj);
        public abstract int Update(object obj);
        public abstract int UpdateAll(IEnumerable objects, bool runInTransaction = true);
        public abstract int Delete(object objectToDelete);
        public abstract int Delete<T>(object primaryKey);
        public abstract int DeleteAll<T>();

        public abstract CreateTableResult CreateTable<T>(CreateFlags createFlags = CreateFlags.None);
        public abstract int CreateIndex(string tableName, string[] columnNames, bool unique = false);
        public abstract int CreateIndex(string tableName, string columnName, bool unique = false);
        public abstract List<ColumnInfo> GetTableInfo(string tableName);

        public abstract void BeginTransaction();
        public abstract void Commit();
        public abstract void Rollback();

        public abstract bool IsConnected();
        public abstract bool TestConnection();

        public abstract long GetDatabaseSize();
        public abstract long Optimize();
        public abstract void Backup(string destinationPath);

        /// <summary>
        /// Default implementation using GetTableInfo. Can be overridden for optimization.
        /// </summary>
        public virtual bool ColumnExists(string tableName, string columnName)
        {
            EnsureConnected();
            List<ColumnInfo> cols = GetTableInfo(tableName);
            return cols.Any(c => c.Name == columnName);
        }

        public abstract string DatabasePath { get; }
        public abstract bool IsInTransaction { get; }

        /// <summary>
        /// Per-command timeout override in seconds. 0 means use connection default.
        /// </summary>
        public virtual int CommandTimeout { get; set; }
    }
}