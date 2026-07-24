namespace DynamicODataToSQL;

using System.Collections.Generic;
using System.Linq;

using Microsoft.OData.Edm;

using SqlKata;

public class JoinClauseBuilder(IEnumerable<IEdmNavigationProperty> navigationProperties)
{
    public Query BuildJoinClause(Query queryIn, IEdmModel model, string tableName)
    {
        // TODO -- I would like to think there's a better solution to this but it doesn't seem like Entity models link back to their entity set
        // so this is the simplest option I see now. 
        var entitySetLookup = model.EntityContainer.EntitySets().ToDictionary(es => es.EntityType().Name);
        foreach (var navigationProperty in navigationProperties)
        {
            // I would like to get rid of the as cast.
            var lookupTable = navigationProperty.Type.Definition as EdmEntityType;
            var entitySet = entitySetLookup[lookupTable.Name];

            queryIn = queryIn.Join($"{entitySet.Name} as {navigationProperty.Name}", j =>
            {
                foreach (var pair in navigationProperty.ReferentialConstraint.PropertyPairs)
                {
                    j.On($"{navigationProperty.Name}.{pair.PrincipalProperty.Name}", $"{tableName}.{pair.DependentProperty.Name}");
                }
                return j;
            });
        }
        return queryIn;
    }
}
