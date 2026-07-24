namespace DynamicODataToSQL.Test;

using System.Collections.Generic;

using Microsoft.OData.Edm;

using JoinKey = (string dependent, string principal);
using PropertyMapping = (string navigationPropertyName, string targetEntitySetName);

/// <inheritdoc/>
/// <summary>
/// EdmModelBuilder with known test tables properties.
/// </summary>
public class TestsEdmModelBuilder : EdmModelBuilder
{
    private readonly Dictionary<string, Dictionary<string, EdmPrimitiveTypeKind>> _knownTableProperties;
    private readonly Dictionary<string, Dictionary<PropertyMapping, List<JoinKey>>> _knownTableNavigationProperties;

    public TestsEdmModelBuilder()
    {
        _knownTableProperties = new Dictionary<string, Dictionary<string, EdmPrimitiveTypeKind>>
        {
            ["Products"] = new Dictionary<string, EdmPrimitiveTypeKind>
            {
                ["Id"] = EdmPrimitiveTypeKind.Int32,
                ["Name"] = EdmPrimitiveTypeKind.String,
                ["Type"] = EdmPrimitiveTypeKind.String,
                ["TotalInventory"] = EdmPrimitiveTypeKind.Int32,
                ["TimeCreated"] = EdmPrimitiveTypeKind.DateTimeOffset,
                ["Origin"] = EdmPrimitiveTypeKind.String,
                ["Spaced Column"] = EdmPrimitiveTypeKind.String
            },
            ["Orders"] = new Dictionary<string, EdmPrimitiveTypeKind>
            {
                ["OrderId"] = EdmPrimitiveTypeKind.Int32,
                ["TotalAmount"] = EdmPrimitiveTypeKind.Double,
                ["Country"] = EdmPrimitiveTypeKind.String,
                ["Amount"] = EdmPrimitiveTypeKind.Double,
                ["OrderDate"] = EdmPrimitiveTypeKind.DateTimeOffset,
                ["value"] = EdmPrimitiveTypeKind.Double,
                ["ProductId"] = EdmPrimitiveTypeKind.Int32
            }
        };
        _knownTableNavigationProperties = new Dictionary<string, Dictionary<PropertyMapping, List<JoinKey>>>
        {
            ["Orders"] = new Dictionary<PropertyMapping, List<JoinKey>>
            {
                [("Product", "Products")] = [("ProductId", "Id")]
            }
        };
    }

    protected override void AddProperties(EdmEntityType entityType)
    {
        var tableName = entityType.Name;

        if (_knownTableProperties.TryGetValue(tableName, out var value))
        {
            foreach (var column in value)
            {
                entityType.AddStructuralProperty(column.Key, column.Value);
            }
        }
    }

    protected override void AddNavigationProperties(EdmEntityType rootType, EdmModel edmModel)
    {
        if (_knownTableNavigationProperties.TryGetValue(rootType.Name, out var navigationProperties))
        {
            var additionalEntities = new Dictionary<string, EdmEntitySet>();

            foreach (var (navigationProperty, joinKeys) in navigationProperties)
            {
                if (!additionalEntities.TryGetValue(navigationProperty.targetEntitySetName, out var lookupEntitySet))
                {
                    var container = edmModel.EntityContainer;
                    var entityType = new EdmEntityType(container.Namespace, navigationProperty.targetEntitySetName, null, false, true);
                    AddProperties(entityType);
                    edmModel.AddElement(entityType);

                    if (container is EdmEntityContainer edmEntityContainer)
                    {
                        lookupEntitySet = edmEntityContainer.AddEntitySet(navigationProperty.targetEntitySetName, entityType);
                        additionalEntities.Add(navigationProperty.targetEntitySetName, lookupEntitySet);
                    }
                }

                var targetEntity = lookupEntitySet.EntityType();
                var navigationPropertyInfo = new EdmNavigationPropertyInfo
                {
                    Name = navigationProperty.navigationPropertyName,
                    TargetMultiplicity = EdmMultiplicity.One,
                    Target = targetEntity,
                    PrincipalProperties = [.. ResolveJoinProperties(targetEntity, joinKeys.Select(j => j.principal))],
                    DependentProperties = [.. ResolveJoinProperties(rootType, joinKeys.Select(j => j.dependent))],
                };
                rootType.AddUnidirectionalNavigation(navigationPropertyInfo);
            }
        }
    }

    private static IEnumerable<IEdmStructuralProperty> ResolveJoinProperties(IEdmEntityType edmEntityType, IEnumerable<string> propertyNames) => edmEntityType.StructuralProperties().Where(p => propertyNames.Contains(p.Name));
}
