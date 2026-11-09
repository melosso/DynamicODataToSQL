namespace DynamicODataToSQL.Test;

using System;
using System.Collections.Generic;

using SqlKata.Compilers;

using Xunit;

public class FilterShapeTests
{
    private static readonly SqlServerCompiler Compiler = new() { UseLegacyPagination = false };

    private static (string Sql, IDictionary<string, object> Params) Convert(string option, string value) =>
        new ODataToSqlConverter(new TestsEdmModelBuilder(), Compiler)
            .ConvertToSQL("Products", new Dictionary<string, string> { [option] = value }, false, true);

    [Theory]
    [InlineData("'Tea' eq Name", "SELECT * FROM [Products] WHERE [Products].[Name] = @p0", "Tea")]
    [InlineData("5 lt TotalInventory", "SELECT * FROM [Products] WHERE [Products].[TotalInventory] > @p0", 5)]
    [InlineData("10 ge TotalInventory", "SELECT * FROM [Products] WHERE [Products].[TotalInventory] <= @p0", 10)]
    [InlineData("null eq Name", "SELECT * FROM [Products] WHERE [Products].[Name] IS NULL", null)]
    [InlineData("Name eq Type", "SELECT * FROM [Products] WHERE [Products].[Name] = [Products].[Type]", null)]
    [InlineData("not (Name eq Type)", "SELECT * FROM [Products] WHERE NOT [Products].[Name] = [Products].[Type]", null)]
    [InlineData("not (Name eq 'Tea')", "SELECT * FROM [Products] WHERE NOT ([Products].[Name] = @p0)", "Tea")]
    public void SupportedShapeEmitsPredicate(string filter, string expectedSql, object? expectedParam)
    {
        var (sql, parameters) = Convert("filter", filter);

        Assert.Equal(expectedSql, sql, ignoreCase: true, ignoreWhiteSpaceDifferences: true);
        if (expectedParam is null)
        {
            Assert.Empty(parameters);
        }
        else
        {
            Assert.Equal(expectedParam, Assert.Single(parameters).Value);
        }
    }

    [Theory]
    [InlineData("1 eq 1")]
    [InlineData("Name eq 'x' or 1 eq 1")]
    [InlineData("not (1 eq 2)")]
    [InlineData("1 add 1 eq 2")]
    [InlineData("TotalInventory add 1 gt 100")]
    [InlineData("-TotalInventory lt 0")]
    [InlineData("not (-TotalInventory lt 0)")]
    [InlineData("length('abc') eq 3")]
    [InlineData("not (length('abc') eq 3)")]
    [InlineData("length(Name) eq 3")]
    [InlineData("trim(Name) eq 'x'")]
    [InlineData("contains('abc', Name)")]
    [InlineData("contains(Name, Type)")]
    [InlineData("Id gt 0 and not (TotalInventory sub 1 eq 0)")]
    public void UnsupportedFilterThrows(string filter) =>
        Assert.Throws<NotSupportedException>(() => Convert("filter", filter));

    [Theory]
    [InlineData("1")]
    [InlineData("length(Name)")]
    public void UnsupportedOrderByThrows(string orderby) =>
        Assert.Throws<NotSupportedException>(() => Convert("orderby", orderby));

    [Fact]
    public void NotLeavesNoNegationForLaterPredicates()
    {
        var query = new ODataToSqlConverter(new TestsEdmModelBuilder(), Compiler)
            .ConvertToSQLKataQuery("Products", new Dictionary<string, string> { ["filter"] = "not (Name eq 'Tea')" }, false, true);

        var sql = Compiler.Compile(query.Where("Products.Origin", "NL")).Sql;

        Assert.EndsWith("AND [Products].[Origin] = @p1", sql);
    }
}
