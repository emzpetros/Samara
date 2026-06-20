using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Database
{
    /// <summary>
    /// Interface for table queries that works with both SQLite and MySQL.
    /// Implements IEnumerable to support LINQ extension methods.
    /// </summary>
    public interface ITableQuery<T> : IEnumerable<T> where T : new()
    {
        /// <summary>
        /// Filters the query using a predicate expression
        /// </summary>
        ITableQuery<T> Where(Expression<Func<T, bool>> predExpr);

        /// <summary>
        /// Orders results by the specified expression (ascending)
        /// </summary>
        ITableQuery<T> OrderBy<U>(Expression<Func<T, U>> orderExpr);

        /// <summary>
        /// Orders results by the specified expression (descending)
        /// </summary>
        ITableQuery<T> OrderByDescending<U>(Expression<Func<T, U>> orderExpr);

        /// <summary>
        /// Adds a secondary ordering (ascending)
        /// </summary>
        ITableQuery<T> ThenBy<U>(Expression<Func<T, U>> orderExpr);

        /// <summary>
        /// Adds a secondary ordering (descending)
        /// </summary>
        ITableQuery<T> ThenByDescending<U>(Expression<Func<T, U>> orderExpr);

        /// <summary>
        /// Limits the results to the specified count
        /// </summary>
        ITableQuery<T> Take(int n);

        /// <summary>
        /// Skips the specified number of results
        /// </summary>
        ITableQuery<T> Skip(int n);

        /// <summary>
        /// Marks the query for deferred execution
        /// </summary>
        ITableQuery<T> Deferred();

        /// <summary>
        /// Executes the query and returns results as a List
        /// </summary>
        List<T> ToList();

        /// <summary>
        /// Executes the query and returns results as an array
        /// </summary>
        T[] ToArray();

        /// <summary>
        /// Returns the count of matching rows
        /// </summary>
        int Count();

        /// <summary>
        /// Returns the first result. Throws if no results.
        /// </summary>
        T First();

        /// <summary>
        /// Returns the first result matching the predicate. Throws if no results.
        /// </summary>
        T First(Expression<Func<T, bool>> predicate);

        /// <summary>
        /// Returns the first result or default(T) if no results
        /// </summary>
        T FirstOrDefault();

        /// <summary>
        /// Returns the first result matching the predicate or default(T) if no results
        /// </summary>
        T FirstOrDefault(Expression<Func<T, bool>> predicate);

        /// <summary>
        /// Returns the element at the specified index
        /// </summary>
        T ElementAt(int index);

        /// <summary>
        /// Deletes all rows matching the current query
        /// </summary>
        int Delete();

        /// <summary>
        /// Deletes all rows matching the specified predicate
        /// </summary>
        int Delete(Expression<Func<T, bool>> predExpr);
    }
}
