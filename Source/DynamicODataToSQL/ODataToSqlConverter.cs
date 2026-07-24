namespace DynamicODataToSQL;

using System;
using System.Collections.Generic;

using DynamicODataToSQL.Interfaces;

using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;

using SqlKata;
using SqlKata.Compilers;

/// <inheritdoc/>
/// <summary>
/// Initializes a new instance of the <see cref="ODataToSqlConverter"/> class.
/// </summary>
/// <param name="edmModelBuilder">edmModelBuilder.</param>
/// <param name="sqlCompiler">sqlCompiler.</param>
public class ODataToSqlConverter(IEdmModelBuilder edmModelBuilder, Compiler sqlCompiler) : IODataToSqlConverter
{
    public const string SPACESIGNREPLACEMENT = "_x0020_";

    private readonly IEdmModelBuilder _edmModelBuilder = edmModelBuilder ?? throw new ArgumentNullException(nameof(edmModelBuilder));
    private readonly Compiler _sqlCompiler = sqlCompiler ?? throw new ArgumentNullException(nameof(sqlCompiler));

    /// <inheritdoc/>
    public (string, IDictionary<string, object>) ConvertToSQL(
        string tableName,
        IDictionary<string, string> odataQuery,
        bool count = false,
        bool tryToParseDates = true)
    {
        var query = BuildSqlKataQuery(tableName, odataQuery, count, tryToParseDates);
        return CompileSqlKataQuery(query);
    }
    public Query ConvertToSQLKataQuery(
        string tableName,
        IDictionary<string, string> odataQuery,
        bool count = false,
        bool tryToParseDates = true) => BuildSqlKataQuery(tableName, odataQuery, count, tryToParseDates);



    public (string, IDictionary<string, object>) ConvertToSqlFromRawSql(
        string rawSql,
        IDictionary<string, string> odataQuery,
        bool count = false,
        bool tryToParseDates = true)
    {
        var query = BuildSqlKataQueryFromRawSql(rawSql, odataQuery, count, tryToParseDates);
        return CompileSqlKataQuery(query);
    }

    public Query ConvertToSQLKataQueryFromRawSql(
        string rawSql,
        IDictionary<string, string> odataQuery,
        bool count = false,
        bool tryToParseDates = true) => BuildSqlKataQueryFromRawSql(rawSql, odataQuery, count, tryToParseDates);


    private Query BuildSqlKataQueryFromRawSql(
        string rawSql,
        IDictionary<string, string> odataQuery,
        bool count,
        bool tryToParseDates)
    {
        if (string.IsNullOrWhiteSpace(rawSql))
        {
            throw new ArgumentNullException(nameof(rawSql));
        }

        var tableName = "RawSql";
        var query = new Query(tableName);
        query = BuildSqlKataQueryFromOdataParameters(query, tableName, odataQuery, count, tryToParseDates, false);

        query.WithRaw(tableName, rawSql);

        return query;
    }

    private Query BuildSqlKataQuery(
        string tableName,
        IDictionary<string, string> odataQuery,
        bool count,
        bool tryToParseDates)
    {
        if (string.IsNullOrWhiteSpace(tableName))
        {
            throw new ArgumentNullException(nameof(tableName));
        }

        var query = new Query(tableName);

        return BuildSqlKataQueryFromOdataParameters(query, tableName, odataQuery, count, tryToParseDates, true);
    }

    private Query BuildSqlKataQueryFromOdataParameters(Query query, string modelName, IDictionary<string, string> odataQuery, bool count, bool tryToParseDates, bool allowNavigationProperties)
    {
        var (parser, model) = GetParser(modelName, odataQuery);

        var applyClause = parser.ParseApply();
        var filterClause = parser.ParseFilter();
        var top = parser.ParseTop();
        var skip = parser.ParseSkip();
        var orderbyClause = parser.ParseOrderBy();
        var selectClause = parser.ParseSelectAndExpand();

        var useNamespacing = allowNavigationProperties && applyClause == null;
        var columnNameResolver = new ColumnNameResolver(_sqlCompiler, modelName, model, useNamespacing);

        if (applyClause != null)
        {
            query = new ApplyClauseBuilder(_sqlCompiler, columnNameResolver).BuildApplyClause(query, applyClause, tryToParseDates);
            if (filterClause != null || selectClause != null)
            {
                query = new Query().From(query, "apply");
            }
        }

        if (filterClause != null)
        {
            query = filterClause.Expression.Accept(new FilterClauseBuilder(query, tryToParseDates, columnNameResolver));
        }

        if (count)
        {
            query = query.AsCount();

            query = new JoinClauseBuilder(columnNameResolver.NavigationProperties).BuildJoinClause(query, model, modelName);
        }
        else
        {
            if (top.HasValue)
            {
                query = query.Take(Convert.ToInt32(top.Value));
            }

            if (skip.HasValue)
            {
                query = query.Skip(Convert.ToInt32(skip.Value));
            }

            if (orderbyClause != null)
            {
                query = BuildOrderByClause(query, orderbyClause, columnNameResolver);
            }

            if (selectClause != null)
            {
                query = BuildSelectClause(query, selectClause, columnNameResolver);
            }

            query = new JoinClauseBuilder(columnNameResolver.NavigationProperties).BuildJoinClause(query, model, modelName);
        }

        return query;
    }
    private (ODataQueryOptionParser, IEdmModel) GetParser(string name, IDictionary<string, string> odataQuery)
    {
        var result = _edmModelBuilder.BuildTableModel(name);
        var model = result.Item1;
        var entityType = result.Item2;
        var entitySet = result.Item3;
        var parser = new ODataQueryOptionParser(model, entityType, entitySet, odataQuery);
        parser.Resolver.EnableCaseInsensitive = true;
        parser.Resolver.EnableNoDollarQueryOptions = true;
        return (parser, model);
    }

    private (string, IDictionary<string, object>) CompileSqlKataQuery(Query query)
    {
        var sqlResult = _sqlCompiler.Compile(query);
        return (sqlResult.Sql, sqlResult.NamedBindings);
    }

    private static Query BuildOrderByClause(Query query, OrderByClause orderbyClause, ColumnNameResolver columnNameResolver)
    {
        while (orderbyClause != null)
        {
            var direction = orderbyClause.Direction;
            var expressionName = columnNameResolver.GetColumnName(orderbyClause.Expression);
            if (expressionName is not null)
            {
                if (direction == OrderByDirection.Ascending)
                {
                    query = query.OrderBy(expressionName);
                }
                else
                {
                    query = query.OrderByDesc(expressionName);
                }
            }

            orderbyClause = orderbyClause.ThenBy;
        }

        return query;
    }

    private static Query BuildSelectClause(Query query, SelectExpandClause selectClause, ColumnNameResolver columnNameResolver)
    {
        if (selectClause.AllSelected)
        {
            foreach (var property in columnNameResolver.GetColumnNames())
            {
                query = query.Select(property);
            }
        }
        // If AllSelected is true, then only EXPAND clauses will be in this collection so we don't need to worry about duplicate selections.
        foreach (var selectItem in selectClause.SelectedItems)
        {
            if (selectItem is PathSelectItem path)
            {
                var property = columnNameResolver.GetColumnName(path.SelectedPath.FirstSegment.Identifier);
                query = query.Select(property);
            }
            else if (selectItem is ExpandedNavigationSelectItem expanded)
            {
                if (expanded.SelectAndExpand.AllSelected)
                {
                    foreach (var property in columnNameResolver.GetColumnNames(expanded.NavigationSource.Name))
                    {
                        query = query.Select($"{property} AS {property}");
                    }
                }
                else
                {
                    foreach (var expandedSelection in expanded.SelectAndExpand.SelectedItems)
                    {
                        if (expandedSelection is PathSelectItem expandedPath)
                        {
                            var property = columnNameResolver.GetColumnName(expanded.NavigationSource.Name, expandedPath.SelectedPath.FirstSegment.Identifier);
                            query = query.Select($"{property} AS {property}");
                        }
                    }
                }
            }
        }

        return query;
    }
}
