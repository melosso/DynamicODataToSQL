namespace DynamicODataSampleService.Controllers;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Dapper;

using DynamicODataSampleService.Models;

using DynamicODataToSQL;

using Flurl;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

using Newtonsoft.Json.Linq;

[ApiController]
[Route("[controller]")]
public class TablesController : ControllerBase
{
    private readonly IODataToSqlConverter _oDataToSqlConverter;
    private readonly string _connectionString;

    public TablesController(IODataToSqlConverter oDataToSqlConverter,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _oDataToSqlConverter = oDataToSqlConverter ?? throw new ArgumentNullException(nameof(oDataToSqlConverter));
        _connectionString = configuration.GetConnectionString("Sql");
    }

    [HttpGet("{tableName}", Name = "QueryRecords")]
    public async Task<IActionResult> QueryAsync(string tableName,
        [FromQuery(Name = "$select")] string select,
        [FromQuery(Name = "$filter")] string filter,
        [FromQuery(Name = "$orderby")] string orderby,
        [FromQuery(Name = "$top")] int top = 10,
        [FromQuery(Name = "$skip")] int skip = 0)
    {
        var query = _oDataToSqlConverter.ConvertToSQL(tableName,
                new Dictionary<string, string>
                {
                    { "select", select },
                    { "filter", filter },
                    { "orderby", orderby },
                    { "top", (top + 1).ToString(null,CultureInfo.InvariantCulture) },
                    { "skip", skip.ToString(null,CultureInfo.InvariantCulture) }
                }
            );
        IEnumerable<dynamic> rows = null;
        await using var conn = new SqlConnection(_connectionString);
        rows = (await conn.QueryAsync(query.Item1, query.Item2).ConfigureAwait(false))?.ToList();

        ODataQueryResult result = null;
        if (rows == null)
        {
            return new JsonResult(result);
        }

        var isLastPage = rows.Count() <= top;
        result = new ODataQueryResult
        {
            Count = isLastPage ? rows.Count() : rows.Count() - 1,
            Value = rows.Take(top),
            NextLink = isLastPage ? null : BuildNextLink(tableName, @select, filter, @orderby, top, skip)
        };

        var serializerSettings = new JsonSerializerOptions
        {
            Converters = { new DynamicConverter() },
        };
        return new JsonResult(result, serializerSettings);
    }

    private string BuildNextLink(string tableName,
        string select,
        string filter,
        string orderby,
        int top,
        int skip
        )
    {
        var nextLink = Url.Link("QueryRecords", new { tableName });
        nextLink = nextLink
            .SetQueryParam("select", select)
            .SetQueryParam("filter", filter)
            .SetQueryParam("orderBy", orderby)
            .SetQueryParam("top", top)
            .SetQueryParam("skip", skip + top);

        return nextLink;
    }
}

/// <summary>
/// This converter is one way you can unpack a query that was constructed with support for $expand.
/// </summary>
public class DynamicConverter : System.Text.Json.Serialization.JsonConverter<dynamic>
{
    public override JObject Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotImplementedException();

    public override void Write(Utf8JsonWriter writer, dynamic value, JsonSerializerOptions options)
    {
        var root = new JObject();

        foreach (var kvp in value)
        {
            var parts = kvp.Key.Split('.');
            var current = root;

            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];

                if (i == parts.Length - 1)
                {
                    // Last part → set value
                    if (kvp.Value is DateTime dt)
                    {
                        // Ensure DateTime is treated as UTC
                        dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                        current[part] = JToken.FromObject(dt);
                    }
                    else
                    {
                        current[part] = kvp.Value != null ? JToken.FromObject(kvp.Value) : JValue.CreateNull();
                    }
                }
                else
                {
                    // Intermediate part → ensure JObject exists
                    if (current[part] == null || current[part].Type != JTokenType.Object)
                    {
                        current[part] = new JObject();
                    }
                    current = (JObject)current[part];
                }
            }
        }

        writer.WriteRawValue(root.ToString(Newtonsoft.Json.Formatting.None));
    }
}
