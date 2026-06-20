using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using SQLite;

namespace Database.Internal
{
    /// <summary>
    /// Wrapper for SQLite TableQuery that implements ITableQuery interface
    /// </summary>
    internal class SQLiteTableQueryWrapper<T> : ITableQuery<T> where T : new()
    {
        private readonly TableQuery<T> _query;

        public SQLiteTableQueryWrapper(TableQuery<T> query)
        {
            _query = query;
        }

        public ITableQuery<T> Where(Expression<Func<T, bool>> predExpr)
        {
            return new SQLiteTableQueryWrapper<T>(_query.Where(predExpr));
        }

        public ITableQuery<T> OrderBy<U>(Expression<Func<T, U>> orderExpr)
        {
            return new SQLiteTableQueryWrapper<T>(_query.OrderBy(orderExpr));
        }

        public ITableQuery<T> OrderByDescending<U>(Expression<Func<T, U>> orderExpr)
        {
            return new SQLiteTableQueryWrapper<T>(_query.OrderByDescending(orderExpr));
        }

        public ITableQuery<T> ThenBy<U>(Expression<Func<T, U>> orderExpr)
        {
            return new SQLiteTableQueryWrapper<T>(_query.ThenBy(orderExpr));
        }

        public ITableQuery<T> ThenByDescending<U>(Expression<Func<T, U>> orderExpr)
        {
            return new SQLiteTableQueryWrapper<T>(_query.ThenByDescending(orderExpr));
        }

        public ITableQuery<T> Take(int n)
        {
            return new SQLiteTableQueryWrapper<T>(_query.Take(n));
        }

        public ITableQuery<T> Skip(int n)
        {
            return new SQLiteTableQueryWrapper<T>(_query.Skip(n));
        }

        public ITableQuery<T> Deferred()
        {
            return new SQLiteTableQueryWrapper<T>(_query.Deferred());
        }

        public List<T> ToList()
        {
            return _query.ToList();
        }

        public T[] ToArray()
        {
            return _query.ToArray();
        }

        public int Count()
        {
            return _query.Count();
        }

        public T First()
        {
            return _query.First();
        }

        public T FirstOrDefault()
        {
            return _query.FirstOrDefault();
        }

        public T FirstOrDefault(Expression<Func<T, bool>> predicate)
        {
            return _query.Where(predicate).FirstOrDefault();
        }

        public T First(Expression<Func<T, bool>> predicate)
        {
            return _query.Where(predicate).First();
        }

        public T ElementAt(int index)
        {
            return _query.ElementAt(index);
        }

        public int Delete()
        {
            return _query.Delete();
        }

        public int Delete(Expression<Func<T, bool>> predExpr)
        {
            return _query.Delete(predExpr);
        }

        // IEnumerable<T> implementation for LINQ support
        public IEnumerator<T> GetEnumerator()
        {
            return _query.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
