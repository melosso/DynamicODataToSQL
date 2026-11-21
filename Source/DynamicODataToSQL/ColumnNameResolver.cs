namespace DynamicODataToSQL;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;

using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;

using SqlKata.Compilers;

public class ColumnNameResolver(Compiler compiler, string tableName, IEdmModel model, bool useNamespacing)
{
    private IEdmEntityType EntityType { get; } = model.EntityContainer.FindEntitySet(tableName).EntityType;

    public HashSet<IEdmNavigationProperty> NavigationProperties { get; } = [];

    /// <param name="wrap">if true returned value will be wrapped in opening and closing column identifier</param>
    public string GetColumnName(QueryNode node, bool wrap = false)
    {
        var table = tableName;
        var column = string.Empty;
        if (node.Kind == QueryNodeKind.Convert)
        {
            node = (node as ConvertNode).Source;
        }

        if (node.Kind == QueryNodeKind.SingleValuePropertyAccess)
        {
            // I would like to get rid of the is/as casts. Are there some first class
            // interface members I can use?
            // Additionally, is this the place where I can maybe dereference the entity set linked
            // to by the lookup property?
            if (node is SingleValuePropertyAccessNode singleValueProperty
                && singleValueProperty.Source is SingleNavigationNode navigationNode)
            {
                column = (node as SingleValuePropertyAccessNode).Property.Name.Trim();
                table = navigationNode.NavigationProperty.Name;

                NavigationProperties.Add(navigationNode.NavigationProperty);
            }
            else
            {
                column = (node as SingleValuePropertyAccessNode).Property.Name.Trim();
            }
        }

        if (node.Kind == QueryNodeKind.SingleValueOpenPropertyAccess)
        {
            column = (node as SingleValueOpenPropertyAccessNode).Name.Trim();
        }

        if (column.Length == 0)
        {
            throw new NotSupportedException($"Expected a property, found '{node.Kind}'");
        }

        // Decode before wrapping so a decoded closing quote (e.g. _x005D_) is escaped by WrapValue.
        table = XmlConvert.DecodeName(table);
        column = DecodeColumnName(column);
        if (wrap)
        {
            table = compiler.WrapValue(table);
            column = compiler.WrapValue(column);
        }

        return useNamespacing ? $"{table}.{column}" : column;
    }

    public string GetColumnName(string propertyName)
    {
        if (useNamespacing)
        {
            return $"{XmlConvert.DecodeName(tableName)}.{DecodeColumnName(propertyName)}";
        }
        else
        {
            return DecodeColumnName(propertyName);
        }
    }

    public string[] GetColumnNames()
    {
        var names = new List<string>();
        foreach (var property in EntityType.DeclaredProperties)
        {
            if (property is EdmStructuralProperty structuralProperty)
            {
                if (useNamespacing)
                {
                    names.Add($"{tableName}.{DecodeColumnName(structuralProperty.Name)}");
                }
                else
                {
                    names.Add(DecodeColumnName(structuralProperty.Name));
                }
            }
        }
        return [.. names];
    }

    public string GetColumnName(string navigationPropertyName, string propertyName)
    {
        if (!useNamespacing)
        {
            throw new InvalidOperationException("Navigation properties require namespacing.");
        }

        var navigationProperty = EntityType.DeclaredProperties.FirstOrDefault(p => p.Name == navigationPropertyName);
        if (navigationProperty == null || navigationProperty is not IEdmNavigationProperty edmNavigationProperty)
        {
            throw new InvalidOperationException("Name is not a navigation property.");
        }
        NavigationProperties.Add(edmNavigationProperty);

        var property = (navigationProperty.Type.Definition as IEdmEntityType).DeclaredProperties.FirstOrDefault(p => p.Name == propertyName);
        if (property is not EdmStructuralProperty structuralProperty)
        {
            throw new InvalidOperationException("Only structural properties may be selected.");
        }
        return $"{navigationPropertyName}.{DecodeColumnName(structuralProperty.Name)}";
    }

    public string[] GetColumnNames(string navigationPropertyName)
    {
        if (!useNamespacing)
        {
            throw new InvalidOperationException("Navigation properties require namespacing.");
        }

        var navigationProperty = EntityType.DeclaredProperties.FirstOrDefault(p => p.Name == navigationPropertyName);
        if (navigationProperty == null || navigationProperty is not IEdmNavigationProperty edmNavigationProperty)
        {
            throw new InvalidOperationException("Name is not a navigation property.");
        }
        NavigationProperties.Add(edmNavigationProperty);

        var names = new List<string>();
        foreach (var property in (navigationProperty.Type.Definition as IEdmEntityType).DeclaredProperties)
        {
            if (property is EdmStructuralProperty structuralProperty)
            {
                names.Add($"{navigationPropertyName}.{DecodeColumnName(structuralProperty.Name)}");
            }
        }
        return [.. names];
    }

    // OData names escape non-identifier characters as _xHHHH_ (e.g. _x0020_ for a space).
    private static string DecodeColumnName(string name)
    {
        var decoded = XmlConvert.DecodeName(name);
        // SqlKata treats these as syntax even inside quoted identifiers: '.' splits names, '?' is a
        // parameter placeholder, and '{' '}' become unescaped quote characters in raw SQL.
        return decoded.IndexOfAny(['.', '?', '{', '}']) >= 0
            ? throw new NotSupportedException($"Column name '{decoded}' must not contain '.', '?', '{{' or '}}'")
            : decoded;
    }
}
