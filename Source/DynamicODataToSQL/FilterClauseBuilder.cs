namespace DynamicODataToSQL;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Microsoft.OData.UriParser;

using SqlKata;

/// <summary>
/// FilterClauseBuilder
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="FilterClauseBuilder"/> class.
/// </remarks>
/// <param name="query">query.</param>
public class FilterClauseBuilder(Query query, bool tryToParseDates, ColumnNameResolver columnNameResolver) : QueryNodeVisitor<Query>
{
    private const DateTimeStyles DATETIMESTYLES = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces;
    private Query _query = query;
    private readonly bool _tryToParseDates = tryToParseDates;

    /// <inheritdoc/>
    public override Query Visit(InNode nodeIn)
    {
        if (nodeIn.Right.Kind != QueryNodeKind.CollectionConstant)
        {
            throw new NotSupportedException("Non constant collection nodes are not supported by 'in' logical operator");
        }

        var leftColumnName = columnNameResolver.GetColumnName(nodeIn.Left);
        var rightValues = GetCollectionConstantValues(nodeIn.Right as CollectionConstantNode);

        return _query.WhereIn(leftColumnName, rightValues);
    }

    /// <inheritdoc/>
    public override Query Visit(BinaryOperatorNode nodeIn)
    {
        var left = nodeIn.Left;
        if (left.Kind == QueryNodeKind.Convert)
        {
            left = (left as ConvertNode).Source;
        }
        var right = nodeIn.Right;
        if (right.Kind == QueryNodeKind.Convert)
        {
            right = (right as ConvertNode).Source;
        }

        switch (nodeIn.OperatorKind)
        {
            case BinaryOperatorKind.Or:
            case BinaryOperatorKind.And:
                _query = _query.Where(q =>
                {
                    var lb = new FilterClauseBuilder(q, _tryToParseDates, columnNameResolver);
                    var lq = left.Accept(lb);
                    if (nodeIn.OperatorKind == BinaryOperatorKind.Or)
                    {
                        lq = lq.Or();
                    }

                    var rb = new FilterClauseBuilder(lq, _tryToParseDates, columnNameResolver);
                    return right.Accept(rb);
                });
                break;

            case BinaryOperatorKind.Equal:
            case BinaryOperatorKind.NotEqual:
            case BinaryOperatorKind.GreaterThan:
            case BinaryOperatorKind.GreaterThanOrEqual:
            case BinaryOperatorKind.LessThan:
            case BinaryOperatorKind.LessThanOrEqual:
                var operatorKind = nodeIn.OperatorKind;
                if (left.Kind == QueryNodeKind.Constant && right.Kind != QueryNodeKind.Constant)
                {
                    (left, right) = (right, left);
                    operatorKind = Mirror(operatorKind);
                }

                var op = GetOperatorString(operatorKind);
                if (right.Kind == QueryNodeKind.Constant)
                {
                    var value = GetConstantValue(right);
                    _query = left.Kind == QueryNodeKind.SingleValueFunctionCall
                        ? ApplyFunction(_query, (SingleValueFunctionCallNode)left, op, value)
                        : _query.Where(columnNameResolver.GetColumnName(left), op, value);
                }
                else
                {
                    _query = _query.WhereColumns(columnNameResolver.GetColumnName(left), op, columnNameResolver.GetColumnName(right));
                }
                break;

            case BinaryOperatorKind.Add:
            case BinaryOperatorKind.Subtract:
            case BinaryOperatorKind.Multiply:
            case BinaryOperatorKind.Divide:
            case BinaryOperatorKind.Modulo:
            case BinaryOperatorKind.Has:
            default:
                throw new NotSupportedException($"Operator '{nodeIn.OperatorKind}' is not supported in $filter");
        }

        return _query;
    }

    /// <inheritdoc/>
    public override Query Visit(SingleValueFunctionCallNode nodeIn)
    {
        ArgumentNullException.ThrowIfNull(nodeIn);

        var nodes = nodeIn.Parameters.ToArray();

        var (caseSensitive, columnName) = ResolveFunctionColumn(nodes);

        switch (nodeIn.Name.ToLowerInvariant())
        {
            case "contains":
                return _query.WhereContains(columnName, (string)GetConstantValue(nodes[1]), caseSensitive);

            case "endswith":
                return _query.WhereEnds(columnName, (string)GetConstantValue(nodes[1]), caseSensitive);

            case "startswith":
                return _query.WhereStarts(columnName, (string)GetConstantValue(nodes[1]), caseSensitive);

            case "matchespattern":
                var value = ((string)GetConstantValue(nodes[1]))
                    .Replace(".*", "%");

                if (value.StartsWith("%5E", StringComparison.InvariantCulture))
                {
                    value = value.Replace("%5E", "");
                }

                if (value[^1] == '$')
                {
                    value = value[..^1];
                }

                return _query.WhereLike(columnName, value, caseSensitive);

            default:
                throw new NotSupportedException($"Function '{nodeIn.Name}' is not supported as a $filter predicate");
        }
    }

    /// <inheritdoc/>
    public override Query Visit(UnaryOperatorNode nodeIn)
    {
        if (nodeIn.OperatorKind != UnaryOperatorKind.Not)
        {
            throw new NotSupportedException($"Operator '{nodeIn.OperatorKind}' is not supported in $filter");
        }

        if (nodeIn.Operand.Kind is not (QueryNodeKind.SingleValueFunctionCall or QueryNodeKind.BinaryOperator or QueryNodeKind.In))
        {
            throw new NotSupportedException("The operand of 'not' is not supported in $filter");
        }

        // the operand emits one clause or throws, so the not flag never outlives it
        var clauses = _query.GetComponents("where").Count;
        var negated = nodeIn.Operand.Accept(new FilterClauseBuilder(_query.Not(), _tryToParseDates, columnNameResolver));
        if (negated.GetComponents("where").Count != clauses + 1)
        {
            throw new NotSupportedException("The operand of 'not' is not supported in $filter");
        }

        return _query = negated;
    }

    private Query ApplyFunction(Query query, SingleValueFunctionCallNode leftNode, string operand, object rightValue)
    {
        var nodes = leftNode.Parameters.ToArray();
        var (caseSensitive, columnName) = ResolveFunctionColumn(nodes);
        switch (leftNode.Name.ToUpperInvariant())
        {
            case "YEAR":
            case "MONTH":
            case "DAY":
            case "HOUR":
            case "MINUTE":
                query = query.WhereDatePart(leftNode.Name, columnName, operand, rightValue);
                break;
            case "DATE":
                query = query.WhereDate(columnName, operand, rightValue);
                break;
            case "TIME":
                query = query.WhereTime(columnName, operand, rightValue);
                break;
            case "TOUPPER":
            case "TOLOWER":
                query = query.WhereLike(columnName, rightValue, false);
                break;
            case "INDEXOF":
                if (rightValue.Equals(-1))
                {
                    query = query.WhereNotContains(columnName, (string)GetConstantValue(nodes[1]), caseSensitive);
                }
                else
                {
                    query = query.WhereContains(columnName, (string)GetConstantValue(nodes[1]), caseSensitive);
                }
                break;
            default:
                throw new NotSupportedException($"Function '{leftNode.Name}' is not supported in a $filter comparison");
        }

        return query;
    }

    // tolower or toupper around the first argument makes the match case insensitive
    private (bool CaseSensitive, string ColumnName) ResolveFunctionColumn(QueryNode[] nodes)
    {
        var source = nodes[0] is ConvertNode convert ? convert.Source : nodes[0];
        return source is SingleValueFunctionCallNode inner && inner.Name.ToUpperInvariant() is "TOUPPER" or "TOLOWER"
            ? (false, columnNameResolver.GetColumnName(inner.Parameters.First()))
            : (true, columnNameResolver.GetColumnName(nodes[0]));
    }

    private static bool ConvertToDateTimeUTC(string dateTimeString, out DateTime? dateTime)
    {
        if (DateTime.TryParse(dateTimeString, CultureInfo.InvariantCulture.DateTimeFormat, DATETIMESTYLES, out var dateTimeValue))
        {
            dateTime = dateTimeValue;
            return true;
        }
        else
        {
            dateTime = null;
            return false;
        }
    }

    private object GetConstantValue(QueryNode node)
    {
        if (node.Kind == QueryNodeKind.Convert)
        {
            return GetConstantValue((node as ConvertNode).Source);
        }
        else if (node.Kind == QueryNodeKind.Constant)
        {
            var value = (node as ConstantNode).Value;
            if (value is string)
            {
                var trimedValue = value.ToString();
                if (_tryToParseDates && ConvertToDateTimeUTC(trimedValue, out var dateTime))
                {
                    return dateTime.Value;
                }

                return trimedValue;
            }
            else if (value is DateOnly date)
            {
                return date.ToDateTime(TimeOnly.MinValue);
            }
            else if (value is TimeOnly timeOfDay)
            {
                return timeOfDay.ToTimeSpan();
            }
            else
            {
                return value;
            }
        }
        else if (node.Kind == QueryNodeKind.CollectionConstant)
        {
            return GetCollectionConstantValues(node as CollectionConstantNode);
        }

        throw new NotSupportedException("A $filter function argument must be a literal");
    }

    private IEnumerable<object> GetCollectionConstantValues(CollectionConstantNode node) =>
        node.Collection.Select(n =>
        {
            // The OData library parser will take empty strings and replace them with quoted text strings, specifically
            // in collection filter clauses:
            // eg. $filter=Name in ('', 'Value')
            // That means that you can never check for a text value in a collection where the collection should include
            // an empty string, unless you special case it here. This does not seem to be an issue for general filters:
            // eg. $filter=Name eq ''
            // So we can focus this workaround to just processing collection constant values.
            if (n.Kind == QueryNodeKind.Constant && n.Value is string nodeValue && nodeValue == @"""""")
            {
                return string.Empty;
            }
            else
            {
                return GetConstantValue(n);
            }
        });

    private static BinaryOperatorKind Mirror(BinaryOperatorKind kind) => kind switch
    {
        BinaryOperatorKind.GreaterThan => BinaryOperatorKind.LessThan,
        BinaryOperatorKind.GreaterThanOrEqual => BinaryOperatorKind.LessThanOrEqual,
        BinaryOperatorKind.LessThan => BinaryOperatorKind.GreaterThan,
        BinaryOperatorKind.LessThanOrEqual => BinaryOperatorKind.GreaterThanOrEqual,
        BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual => kind,
        BinaryOperatorKind.Or or BinaryOperatorKind.And or BinaryOperatorKind.Add or BinaryOperatorKind.Subtract
            or BinaryOperatorKind.Multiply or BinaryOperatorKind.Divide or BinaryOperatorKind.Modulo or BinaryOperatorKind.Has
            => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only comparison operators mirror"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only comparison operators mirror"),
    };

    private static string GetOperatorString(BinaryOperatorKind operatorKind) => operatorKind switch
    {
        BinaryOperatorKind.Equal => "=",
        BinaryOperatorKind.NotEqual => "<>",
        BinaryOperatorKind.GreaterThan => ">",
        BinaryOperatorKind.GreaterThanOrEqual => ">=",
        BinaryOperatorKind.LessThan => "<",
        BinaryOperatorKind.LessThanOrEqual => "<=",
        BinaryOperatorKind.Or => "or",
        BinaryOperatorKind.And => "and",
        BinaryOperatorKind.Add => "+",
        BinaryOperatorKind.Subtract => "-",
        BinaryOperatorKind.Multiply => "*",
        BinaryOperatorKind.Divide => "/",
        BinaryOperatorKind.Modulo => "%",
        BinaryOperatorKind.Has => throw new NotImplementedException(),
        _ => string.Empty,
    };
}
