using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using SQLite;
using UnityEngine;

namespace Database.Internal
{
    /// <summary>
    /// MySQL-native TableQuery that executes queries directly against MySQL.
    /// Translates LINQ expressions to MySQL SQL for efficient query execution.
    /// </summary>
    internal class MySQLTableQuery<T> : ITableQuery<T> where T : new()
    {
        private readonly MySQLDatabaseConnection _mysqlConnection;
        private readonly List<Expression<Func<T, bool>>> _whereExpressions;
        private string _compiledWhereClause;
        private List<object> _whereParameters;
        private List<(string ColumnName, bool Ascending)> _orderBys;
        private int? _limit;
        private int? _offset;
        private bool _deferred;

        public MySQLTableQuery(MySQLDatabaseConnection connection)
        {
            _mysqlConnection = connection;
            _whereExpressions = new List<Expression<Func<T, bool>>>();
            _orderBys = new List<(string ColumnName, bool Ascending)>();
            _deferred = false;
        }

        private MySQLTableQuery(
            MySQLDatabaseConnection connection,
            List<Expression<Func<T, bool>>> whereExpressions,
            List<(string ColumnName, bool Ascending)> orderBys,
            int? limit,
            int? offset,
            bool deferred)
        {
            _mysqlConnection = connection;
            _whereExpressions = new List<Expression<Func<T, bool>>>(whereExpressions);
            _orderBys = orderBys != null ? new List<(string ColumnName, bool Ascending)>(orderBys) : new List<(string ColumnName, bool Ascending)>();
            _limit = limit;
            _offset = offset;
            _deferred = deferred;
        }

        private void CompileWhereClause()
        {
            if (_compiledWhereClause != null) return; // Already compiled

            if (_whereExpressions.Count == 0)
            {
                _compiledWhereClause = "";
                _whereParameters = new List<object>();
                return;
            }

            StringBuilder whereBuilder = new StringBuilder();
            _whereParameters = new List<object>();

            whereBuilder.Append(" WHERE ");
            List<string> conditions = new List<string>();

            TableMapping mapping = _mysqlConnection.GetMapping<T>();

            foreach (Expression<Func<T, bool>> expr in _whereExpressions)
            {
                string condition = TranslateExpression(expr.Body, mapping, _whereParameters);
                if (!string.IsNullOrEmpty(condition))
                {
                    conditions.Add(condition);
                }
            }

            whereBuilder.Append(string.Join(" AND ", conditions));
            _compiledWhereClause = whereBuilder.ToString();
        }

        private string BuildOrderBy()
        {
            if (_orderBys == null || _orderBys.Count == 0)
            {
                return "";
            }

            StringBuilder builder = new StringBuilder();
            builder.Append(" ORDER BY ");
            List<string> orderClauses = new List<string>();
            foreach ((string ColumnName, bool Ascending) orderBy in _orderBys)
            {
                string direction = orderBy.Ascending ? "ASC" : "DESC";
                orderClauses.Add($"`{orderBy.ColumnName}` {direction}");
            }
            builder.Append(string.Join(", ", orderClauses));
            return builder.ToString();
        }

        private string BuildOrderLimitOffset()
        {
            StringBuilder builder = new StringBuilder();

            // Add ORDER BY clause
            builder.Append(BuildOrderBy());

            // Add LIMIT clause
            if (_limit.HasValue)
            {
                builder.Append($" LIMIT {_limit.Value}");
            }

            // Add OFFSET clause
            if (_offset.HasValue)
            {
                // MySQL requires LIMIT when using OFFSET
                if (!_limit.HasValue)
                {
                    builder.Append(" LIMIT 18446744073709551615"); // Max unsigned bigint
                }
                builder.Append($" OFFSET {_offset.Value}");
            }

            return builder.ToString();
        }

        private string GetNullableColumnFromMethodCall(Expression expression)
        {
            // Check if this is a method call on a column (e.g., a.Location.ToLower())
            if (expression is MethodCallExpression methodCall &&
                methodCall.Method.DeclaringType == typeof(string) &&
                methodCall.Object is MemberExpression memberExpr &&
                memberExpr.Expression is ParameterExpression)
            {
                // Return the column name for null checking
                return $"`{memberExpr.Member.Name}`";
            }
            return null;
        }

        private string TranslateExpression(Expression expression, TableMapping mapping, List<object> parameters)
        {
            // Handle binary expressions (==, !=, <, >, etc.)
            if (expression is BinaryExpression binary)
            {
                // OPTIMIZATION: Detect symmetric ToLower() comparisons for index usage
                // When both sides use ToLower(), strip them and rely on MySQL's case-insensitive collation
                if ((binary.NodeType == ExpressionType.Equal || binary.NodeType == ExpressionType.NotEqual) &&
                    binary.Left is MethodCallExpression leftMethod &&
                    binary.Right is MethodCallExpression rightMethod &&
                    leftMethod.Method.Name == "ToLower" &&
                    rightMethod.Method.Name == "ToLower" &&
                    leftMethod.Arguments.Count == 0 &&
                    rightMethod.Arguments.Count == 0)
                {
                    string left = TranslateExpression(leftMethod.Object, mapping, parameters);
                    string right = TranslateExpression(rightMethod.Object, mapping, parameters);
                    string op = binary.NodeType == ExpressionType.Equal ? "=" : "!=";
                    return $"{left} {op} {right}";
                }

                // Handle case where left is ToLower() and right is a constant
                if ((binary.NodeType == ExpressionType.Equal || binary.NodeType == ExpressionType.NotEqual) &&
                    binary.Left is MethodCallExpression leftToLowerMethod &&
                    leftToLowerMethod.Method.Name == "ToLower" &&
                    leftToLowerMethod.Arguments.Count == 0 &&
                    (binary.Right is ConstantExpression ||
                     (binary.Right is MemberExpression rightMember && rightMember.Expression is ConstantExpression)))
                {
                    string left = TranslateExpression(leftToLowerMethod.Object, mapping, parameters);
                    string right = TranslateExpression(binary.Right, mapping, parameters);
                    string op = binary.NodeType == ExpressionType.Equal ? "=" : "!=";
                    return $"{left} {op} {right}";
                }

                string nullCheckColumn = GetNullableColumnFromMethodCall(binary.Left);

                string leftDefault = TranslateExpression(binary.Left, mapping, parameters);
                string rightDefault = TranslateExpression(binary.Right, mapping, parameters);

                string opDefault = binary.NodeType switch
                {
                    ExpressionType.Equal => "=",
                    ExpressionType.NotEqual => "!=",
                    ExpressionType.GreaterThan => ">",
                    ExpressionType.GreaterThanOrEqual => ">=",
                    ExpressionType.LessThan => "<",
                    ExpressionType.LessThanOrEqual => "<=",
                    ExpressionType.AndAlso => "AND",
                    ExpressionType.OrElse => "OR",
                    _ => throw new NotSupportedException($"Binary operator {binary.NodeType} not supported")
                };

                if (opDefault == "AND" || opDefault == "OR")
                {
                    return $"({leftDefault} {opDefault} {rightDefault})";
                }

                string comparison = $"{leftDefault} {opDefault} {rightDefault}";
                if (!string.IsNullOrEmpty(nullCheckColumn))
                {
                    return $"({nullCheckColumn} IS NOT NULL AND {comparison})";
                }

                return comparison;
            }

            // Handle member access (e.g., a.PropertyName)
            if (expression is MemberExpression member)
            {
                if (member.Expression is ParameterExpression)
                {
                    return $"`{member.Member.Name}`";
                }

                object value = Expression.Lambda(expression).Compile().DynamicInvoke();
                parameters.Add(value);
                return $"@p{parameters.Count - 1}";
            }

            // Handle constant values
            if (expression is ConstantExpression constant)
            {
                parameters.Add(constant.Value);
                return $"@p{parameters.Count - 1}";
            }

            // Handle unary expressions (NOT, conversions)
            if (expression is UnaryExpression unary)
            {
                if (unary.NodeType == ExpressionType.Convert)
                {
                    return TranslateExpression(unary.Operand, mapping, parameters);
                }
                else if (unary.NodeType == ExpressionType.Not)
                {
                    string operand = TranslateExpression(unary.Operand, mapping, parameters);
                    return $"NOT {operand}";
                }
            }

            // Handle method calls (StartsWith, Contains, etc.)
            if (expression is MethodCallExpression methodCall)
            {
                // String instance methods
                if (methodCall.Method.DeclaringType == typeof(string) && !methodCall.Method.IsStatic)
                {
                    bool isColumnReference = methodCall.Object is MemberExpression memberExpr
                        && memberExpr.Expression is ParameterExpression;

                    if (!isColumnReference)
                    {
                        try
                        {
                            object value = Expression.Lambda(methodCall).Compile().DynamicInvoke();
                            parameters.Add(value);
                            return $"@p{parameters.Count - 1}";
                        }
                        catch
                        {
                            // Fall through
                        }
                    }

                    string columnName = TranslateExpression(methodCall.Object, mapping, parameters);

                    switch (methodCall.Method.Name)
                    {
                        case "StartsWith":
                            if (methodCall.Arguments.Count >= 1)
                            {
                                object value = Expression.Lambda(methodCall.Arguments[0]).Compile().DynamicInvoke();
                                parameters.Add($"{value}%");
                                return $"{columnName} LIKE @p{parameters.Count - 1}";
                            }
                            break;

                        case "EndsWith":
                            if (methodCall.Arguments.Count >= 1)
                            {
                                object value = Expression.Lambda(methodCall.Arguments[0]).Compile().DynamicInvoke();
                                parameters.Add($"%{value}");
                                return $"{columnName} LIKE @p{parameters.Count - 1}";
                            }
                            break;

                        case "Contains":
                            if (methodCall.Arguments.Count >= 1)
                            {
                                object value = Expression.Lambda(methodCall.Arguments[0]).Compile().DynamicInvoke();
                                parameters.Add($"%{value}%");
                                return $"{columnName} LIKE @p{parameters.Count - 1}";
                            }
                            break;

                        case "ToLower":
                            return $"LOWER({columnName})";

                        case "ToUpper":
                            return $"UPPER({columnName})";

                        case "Replace":
                            if (methodCall.Arguments.Count == 2)
                            {
                                object oldValue = Expression.Lambda(methodCall.Arguments[0]).Compile().DynamicInvoke();
                                object newValue = Expression.Lambda(methodCall.Arguments[1]).Compile().DynamicInvoke();
                                parameters.Add(oldValue);
                                string oldParam = $"@p{parameters.Count - 1}";
                                parameters.Add(newValue);
                                string newParam = $"@p{parameters.Count - 1}";
                                return $"REPLACE({columnName}, {oldParam}, {newParam})";
                            }
                            break;

                        case "Equals":
                            if (methodCall.Arguments.Count >= 1)
                            {
                                object value = Expression.Lambda(methodCall.Arguments[0]).Compile().DynamicInvoke();
                                parameters.Add(value);
                                return $"({columnName} = @p{parameters.Count - 1})";
                            }
                            break;
                    }
                }

                // Static string methods
                if (methodCall.Method.DeclaringType == typeof(string) && methodCall.Method.IsStatic)
                {
                    if (methodCall.Method.Name == "IsNullOrEmpty" && methodCall.Arguments.Count == 1)
                    {
                        string columnName = TranslateExpression(methodCall.Arguments[0], mapping, parameters);
                        return $"({columnName} IS NULL OR {columnName} = '')";
                    }
                }

                // Custom Like method
                if (methodCall.Method.Name == "Like" && methodCall.Arguments.Count == 2)
                {
                    string column = TranslateExpression(methodCall.Arguments[0], mapping, parameters);
                    object pattern = Expression.Lambda(methodCall.Arguments[1]).Compile().DynamicInvoke();
                    parameters.Add(pattern);
                    return $"({column} LIKE @p{parameters.Count - 1})";
                }

                // List.Contains - for "IN" queries
                if (methodCall.Method.Name == "Contains" && methodCall.Object != null)
                {
                    object listObj = Expression.Lambda(methodCall.Object).Compile().DynamicInvoke();
                    if (listObj is IEnumerable enumerable)
                    {
                        List<object> values = new List<object>();
                        foreach (object item in enumerable)
                        {
                            values.Add(item);
                        }

                        if (values.Count == 0)
                        {
                            return "1=0"; // No values, always false
                        }

                        string columnName = TranslateExpression(methodCall.Arguments[0], mapping, parameters);
                        List<string> placeholders = new List<string>();
                        foreach (object value in values)
                        {
                            parameters.Add(value);
                            placeholders.Add($"@p{parameters.Count - 1}");
                        }
                        return $"{columnName} IN ({string.Join(", ", placeholders)})";
                    }
                }
            }

            // For anything else, try to evaluate it
            try
            {
                object value = Expression.Lambda(expression).Compile().DynamicInvoke();
                parameters.Add(value);
                return $"@p{parameters.Count - 1}";
            }
            catch
            {
                throw new NotSupportedException($"Expression type {expression.NodeType} not supported in WHERE clause");
            }
        }

        // ITableQuery<T> implementation

        public List<T> ToList()
        {
            try
            {
                CompileWhereClause();
                TableMapping mapping = _mysqlConnection.GetMapping<T>();
                string orderLimitOffset = BuildOrderLimitOffset();
                string sql = $"SELECT * FROM `{mapping.TableName}`{_compiledWhereClause}{orderLimitOffset}";
                return _mysqlConnection.Query<T>(sql, _whereParameters.ToArray());
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MySQLTableQuery] Error executing ToList() on table {typeof(T).Name}: {ex.Message}");
                throw;
            }
        }

        public T[] ToArray()
        {
            return ToList().ToArray();
        }

        public int Count()
        {
            try
            {
                CompileWhereClause();
                TableMapping mapping = _mysqlConnection.GetMapping<T>();
                string sql = $"SELECT COUNT(*) FROM `{mapping.TableName}`{_compiledWhereClause}";
                return _mysqlConnection.ExecuteScalar<int>(sql, _whereParameters.ToArray());
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MySQLTableQuery] Error executing Count() on table {typeof(T).Name}: {ex.Message}");
                throw;
            }
        }

        public T First()
        {
            try
            {
                CompileWhereClause();
                TableMapping mapping = _mysqlConnection.GetMapping<T>();
                string orderClause = BuildOrderBy();
                string sql = $"SELECT * FROM `{mapping.TableName}`{_compiledWhereClause}{orderClause} LIMIT 1";
                List<T> results = _mysqlConnection.Query<T>(sql, _whereParameters.ToArray());
                if (results.Count == 0)
                    throw new InvalidOperationException("Sequence contains no elements");
                return results[0];
            }
            catch (Exception ex) when (!(ex is InvalidOperationException))
            {
                Debug.LogError($"[MySQLTableQuery] Error executing First() on table {typeof(T).Name}: {ex.Message}");
                throw;
            }
        }

        public T First(Expression<Func<T, bool>> predicate)
        {
            return Where(predicate).First();
        }

        public T FirstOrDefault()
        {
            try
            {
                CompileWhereClause();
                TableMapping mapping = _mysqlConnection.GetMapping<T>();
                string orderClause = BuildOrderBy();
                string sql = $"SELECT * FROM `{mapping.TableName}`{_compiledWhereClause}{orderClause} LIMIT 1";
                List<T> results = _mysqlConnection.Query<T>(sql, _whereParameters.ToArray());
                return results.FirstOrDefault();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MySQLTableQuery] Error executing FirstOrDefault() on table {typeof(T).Name}: {ex.Message}");
                throw;
            }
        }

        public T FirstOrDefault(Expression<Func<T, bool>> predicate)
        {
            return Where(predicate).FirstOrDefault();
        }

        public T ElementAt(int index)
        {
            CompileWhereClause();
            TableMapping mapping = _mysqlConnection.GetMapping<T>();
            string orderClause = BuildOrderBy();
            string sql = $"SELECT * FROM `{mapping.TableName}`{_compiledWhereClause}{orderClause} LIMIT 1 OFFSET {index}";
            List<T> results = _mysqlConnection.Query<T>(sql, _whereParameters.ToArray());
            if (results.Count == 0)
                throw new ArgumentOutOfRangeException(nameof(index));
            return results[0];
        }

        public ITableQuery<T> Where(Expression<Func<T, bool>> predExpr)
        {
            List<Expression<Func<T, bool>>> newExpressions = new List<Expression<Func<T, bool>>>(_whereExpressions);
            newExpressions.Add(predExpr);
            return new MySQLTableQuery<T>(_mysqlConnection, newExpressions, _orderBys, _limit, _offset, _deferred);
        }

        public ITableQuery<T> OrderBy<U>(Expression<Func<T, U>> orderExpr)
        {
            return AddOrderBy(orderExpr, true);
        }

        public ITableQuery<T> OrderByDescending<U>(Expression<Func<T, U>> orderExpr)
        {
            return AddOrderBy(orderExpr, false);
        }

        public ITableQuery<T> ThenBy<U>(Expression<Func<T, U>> orderExpr)
        {
            return AddOrderBy(orderExpr, true);
        }

        public ITableQuery<T> ThenByDescending<U>(Expression<Func<T, U>> orderExpr)
        {
            return AddOrderBy(orderExpr, false);
        }

        private ITableQuery<T> AddOrderBy<U>(Expression<Func<T, U>> orderExpr, bool ascending)
        {
            if (orderExpr.NodeType != ExpressionType.Lambda)
            {
                throw new NotSupportedException("Order By must be a lambda expression");
            }

            LambdaExpression lambda = (LambdaExpression)orderExpr;
            MemberExpression memberExpr = null;

            if (lambda.Body is UnaryExpression unary && unary.NodeType == ExpressionType.Convert)
            {
                memberExpr = unary.Operand as MemberExpression;
            }
            else
            {
                memberExpr = lambda.Body as MemberExpression;
            }

            if (memberExpr == null || !(memberExpr.Expression is ParameterExpression))
            {
                throw new NotSupportedException("Order By expression must be a member access");
            }

            TableMapping mapping = _mysqlConnection.GetMapping<T>();
            TableMapping.Column column = mapping.FindColumnWithPropertyName(memberExpr.Member.Name);

            if (column == null)
            {
                throw new NotSupportedException($"Column not found for property: {memberExpr.Member.Name}");
            }

            List<(string ColumnName, bool Ascending)> newOrderBys = new List<(string ColumnName, bool Ascending)>(_orderBys);
            newOrderBys.Add((column.Name, ascending));

            return new MySQLTableQuery<T>(_mysqlConnection, _whereExpressions, newOrderBys, _limit, _offset, _deferred);
        }

        public ITableQuery<T> Take(int n)
        {
            return new MySQLTableQuery<T>(_mysqlConnection, _whereExpressions, _orderBys, n, _offset, _deferred);
        }

        public ITableQuery<T> Skip(int n)
        {
            return new MySQLTableQuery<T>(_mysqlConnection, _whereExpressions, _orderBys, _limit, n, _deferred);
        }

        public ITableQuery<T> Deferred()
        {
            return new MySQLTableQuery<T>(_mysqlConnection, _whereExpressions, _orderBys, _limit, _offset, true);
        }

        public int Delete()
        {
            return Delete(null);
        }

        public int Delete(Expression<Func<T, bool>> predExpr)
        {
            if (_limit.HasValue || _offset.HasValue)
            {
                throw new InvalidOperationException("Cannot delete with LIMIT or OFFSET");
            }

            List<Expression<Func<T, bool>>> deleteExpressions = new List<Expression<Func<T, bool>>>(_whereExpressions);
            if (predExpr != null)
            {
                deleteExpressions.Add(predExpr);
            }

            if (deleteExpressions.Count == 0)
            {
                throw new InvalidOperationException("Cannot delete without WHERE clause - use DeleteAll on connection");
            }

            try
            {
                List<object> deleteParameters = new List<object>();
                StringBuilder whereBuilder = new StringBuilder();
                List<string> conditions = new List<string>();

                TableMapping mapping = _mysqlConnection.GetMapping<T>();

                foreach (Expression<Func<T, bool>> expr in deleteExpressions)
                {
                    string condition = TranslateExpression(expr.Body, mapping, deleteParameters);
                    if (!string.IsNullOrEmpty(condition))
                    {
                        conditions.Add(condition);
                    }
                }

                if (conditions.Count > 0)
                {
                    whereBuilder.Append(" WHERE ");
                    whereBuilder.Append(string.Join(" AND ", conditions));
                }

                string sql = $"DELETE FROM `{mapping.TableName}`{whereBuilder}";
                return _mysqlConnection.Execute(sql, deleteParameters.ToArray());
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MySQLTableQuery] Error executing Delete() on table {typeof(T).Name}: {ex.Message}");
                throw;
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            return ToList().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
