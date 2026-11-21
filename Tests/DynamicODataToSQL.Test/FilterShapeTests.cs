namespace DynamicODataToSQL.Test;

using System;
using System.Collections.Generic;
using System.Globalization;

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
    [InlineData("5 gt TotalInventory", "SELECT * FROM [Products] WHERE [Products].[TotalInventory] < @p0", 5)]
    [InlineData("5 le TotalInventory", "SELECT * FROM [Products] WHERE [Products].[TotalInventory] >= @p0", 5)]
    [InlineData("'Tea' ne Name", "SELECT * FROM [Products] WHERE [Products].[Name] <> @p0", "Tea")]
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
    [InlineData("a_x002E_b eq 'v'")]
    [InlineData("a_x003F_b eq 'v'")]
    [InlineData("_x007B_x_x002C_y_x007D_ eq 'v'")]
    [InlineData("a_x005D_b eq 'v'")]
    [InlineData("a_x005C_b eq 'v'")]
    [InlineData("a_x0000_b eq 'v'")]
    public void UnsupportedFilterThrows(string filter) =>
        Assert.Throws<NotSupportedException>(() => Convert("filter", filter));

    [Theory]
    [InlineData("1")]
    [InlineData("length(Name)")]
    [InlineData("a_x002E_b")]
    public void UnsupportedOrderByThrows(string orderby) =>
        Assert.Throws<NotSupportedException>(() => Convert("orderby", orderby));

    [Theory]
    [InlineData("aggregate(_x007D__x0029__x003B_DROP with sum as t)")]
    [InlineData("groupby((a_x003F_b))")]
    [InlineData("aggregate(x_x005D__x0029__x003B__x0020_DROP_x0020_TABLE_x0020_users_x003B__x0020__x002D__x002D_ with sum as t)")]
    [InlineData("compute(year(a_x005B_b) as yr)")]
    public void UnsupportedApplyThrows(string apply) =>
        Assert.Throws<NotSupportedException>(() => Convert("apply", apply));

    [Theory]
    [InlineData("compute(day(OrderDate) as d, hour(OrderDate) as h, minute(OrderDate) as m)", "SELECT *, day([OrderDate]) as d, DATEPART(HOUR, [OrderDate]) as h, DATEPART(MINUTE, [OrderDate]) as m FROM [Products]")]
    [InlineData("aggregate(Price with min as a, Price with max as b, Price with average as c)", "SELECT Min([Price]) AS [a], Max([Price]) AS [b], AVG([Price]) AS [c] FROM [Products]")]
    [InlineData("filter(Name eq 'Tea')/groupby((Type))", "SELECT [Type] FROM [Products] WHERE [Name] = @p0 GROUP BY [Type]")]
    public void SupportedApplyEmitsQuery(string apply, string expectedSql) =>
        Assert.Equal(expectedSql, Convert("apply", apply).Sql);

    [Theory]
    [InlineData("sqlsrv", "SELECT *, year([OrderDate]) as y, DATEPART(MINUTE, [OrderDate]) as mi FROM [Products]")]
    [InlineData("mysql", "SELECT *, year(`OrderDate`) as y, minute(`OrderDate`) as mi FROM `Products`")]
    [InlineData("postgres", "SELECT *, CAST(EXTRACT(YEAR FROM \"OrderDate\") AS INTEGER) as y, CAST(EXTRACT(MINUTE FROM \"OrderDate\") AS INTEGER) as mi FROM \"Products\"")]
    [InlineData("sqlite", "SELECT *, CAST(strftime('%Y', \"OrderDate\") AS INTEGER) as y, CAST(strftime('%M', \"OrderDate\") AS INTEGER) as mi FROM \"Products\"")]
    public void ComputeDatePartMatchesDialect(string engine, string expectedSql)
    {
        var compiler = CompilerFor(engine);
        var query = new ODataToSqlConverter(new TestsEdmModelBuilder(), compiler)
            .ConvertToSQLKataQuery("Products", new Dictionary<string, string> { ["apply"] = "compute(year(OrderDate) as y, minute(OrderDate) as mi)" });

        Assert.Equal(expectedSql, compiler.Compile(query).Sql);
    }

    [Theory]
    [InlineData("oracle")]
    [InlineData("firebird")]
    public void ComputeDatePartThrowsForUnsupportedDialect(string engine) =>
        Assert.Throws<NotSupportedException>(() => new ODataToSqlConverter(new TestsEdmModelBuilder(), CompilerFor(engine))
            .ConvertToSQLKataQuery("Products", new Dictionary<string, string> { ["apply"] = "compute(year(OrderDate) as y)" }));

    private static Compiler CompilerFor(string engine) => engine switch
    {
        "sqlsrv" => Compiler,
        "mysql" => new MySqlCompiler(),
        "postgres" => new PostgresCompiler(),
        "sqlite" => new SqliteCompiler(),
        "oracle" => new OracleCompiler(),
        "firebird" => new FirebirdCompiler(),
        _ => throw new ArgumentOutOfRangeException(nameof(engine)),
    };

    [Theory]
    [InlineData("compute(length(Name) as l)")]
    [InlineData("compute(Name as n)")]
    public void UnsupportedComputeThrows(string apply) =>
        Assert.Throws<NotSupportedException>(() => Convert("apply", apply));

    [Fact]
    public void NullArgumentsThrow()
    {
        var converter = new ODataToSqlConverter(new TestsEdmModelBuilder(), Compiler);
        var query = new Dictionary<string, string>();

        Assert.Throws<ArgumentNullException>(() => new ODataToSqlConverter(null, Compiler));
        Assert.Throws<ArgumentNullException>(() => new ODataToSqlConverter(new TestsEdmModelBuilder(), null));
        Assert.Throws<ArgumentNullException>(() => converter.ConvertToSQL(null, query));
        Assert.Throws<ArgumentNullException>(() => converter.ConvertToSqlFromRawSql(null, query));
        Assert.Throws<ArgumentNullException>(() => new EdmModelBuilder().BuildTableModel(" "));
    }

    [Theory]
    [InlineData("filter", "{0} eq 'v'", "SELECT * FROM {1} WHERE {1}.{0} = @p0")]
    [InlineData("select", "{0}", "SELECT {1}.{0} FROM {1}")]
    [InlineData("orderby", "{0}", "SELECT * FROM {1} ORDER BY {1}.{0}")]
    [InlineData("apply", "aggregate({0} with sum as t)", "SELECT Sum({0}) AS {2} FROM {1}")]
    [InlineData("apply", "aggregate({0} with countdistinct as t)", "SELECT COUNT(DISTINCT {0}) AS {2} FROM {1}")]
    [InlineData("apply", "compute(year({0}) as yr)", "SELECT *, {3} as yr FROM {1}")]
    public void EncodedColumnCharacterIsQuotedOrRejected(string option, string odataFormat, string sqlFormat)
    {
        var last = int.TryParse(Environment.GetEnvironmentVariable("DECODE_SWEEP_LAST"), out var n) ? n : 0x7E;
        Compiler[] compilers = [Compiler, new PostgresCompiler(), new MySqlCompiler()];
        var failures = new List<string>();
        foreach (var compiler in compilers)
        {
            var converter = new ODataToSqlConverter(new TestsEdmModelBuilder(), compiler);
            for (var c = 0x01; c <= last; c++)
            {
                if (c is >= 0xD800 and <= 0xDFFF)
                {
                    continue;
                }

                var column = $"a{(char)c}b";
                var odata = string.Format(CultureInfo.InvariantCulture, odataFormat, $"a_x{c:X4}_b");
                var rejected = char.IsControl((char)c) || "[]{}.?\\".Contains((char)c);
                try
                {
                    var (sql, _) = converter.ConvertToSQL("Products", new Dictionary<string, string> { [option] = odata }, false, true);
                    var wrapped = compiler.WrapValue(column);
                    var year = compiler is PostgresCompiler ? $"CAST(EXTRACT(YEAR FROM {wrapped}) AS INTEGER)" : $"year({wrapped})";
                    var expected = string.Format(CultureInfo.InvariantCulture, sqlFormat, wrapped, compiler.WrapValue("Products"), compiler.WrapValue("t"), year);
                    if (rejected || sql != expected)
                    {
                        failures.Add($"{compiler.EngineCode} U+{c:X4}: {sql}");
                    }
                }
                catch (NotSupportedException) when (rejected)
                {
                }
                catch (Exception ex)
                {
                    failures.Add($"{compiler.EngineCode} U+{c:X4}: {ex.GetType().Name}");
                }
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void NotLeavesNoNegationForLaterPredicates()
    {
        var query = new ODataToSqlConverter(new TestsEdmModelBuilder(), Compiler)
            .ConvertToSQLKataQuery("Products", new Dictionary<string, string> { ["filter"] = "not (Name eq 'Tea')" }, false, true);

        var sql = Compiler.Compile(query.Where("Products.Origin", "NL")).Sql;

        Assert.EndsWith("AND [Products].[Origin] = @p1", sql);
    }
}
