using System.Data.Common;
using System.Text.Json;
using Manifesta.Core.IR;

namespace Manifesta.Core;

/// <summary>
/// Shared implementation for all database-flavour reference data capturers.
/// Subclasses supply the connection factory and identifier quoting; this class
/// owns the query-execute-read-size-check pipeline and JSON serialisation.
/// </summary>
public abstract class ReferenceDataCapturerBase : IReferenceDataCapturer
{
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>> CaptureAsync(
        TableDefinition table,
        int maxSizeKb,
        CancellationToken ct = default)
    {
        var pkCols = table.PrimaryKey;

        var orderBy = pkCols.Count > 0
            ? string.Join(", ", pkCols.Select(c => QuoteColumn(c)))
            : (string?)null;

        var quotedTable = QuoteTable(table.Name);
        var sql = orderBy is null
            ? $"SELECT * FROM {quotedTable}"
            : $"SELECT * FROM {quotedTable} ORDER BY {orderBy}";

        var rows = new List<IReadOnlyDictionary<string, JsonElement>>();

        using var connection = CreateConnection();
        await connection.OpenAsync(ct);

        using var command = connection.CreateCommand();
        command.CommandText    = sql;
        command.CommandTimeout = 30;

        using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, JsonElement>(reader.FieldCount, StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var colName = reader.GetName(i);
                var value   = reader.IsDBNull(i) ? null : reader.GetValue(i);
                row[colName] = ToJsonElement(value);
            }
            rows.Add(row);
        }

        var serialized = JsonSerializer.Serialize(rows);
        if (serialized.Length > maxSizeKb * 1024)
            return [];

        return rows;
    }

    protected abstract DbConnection CreateConnection();

    /// <summary>Quotes a column identifier for ORDER BY (e.g. <c>[col]</c>, <c>`col`</c>, <c>"col"</c>).</summary>
    protected abstract string QuoteColumn(string column);

    /// <summary>Quotes a (possibly schema-qualified) table name for FROM (e.g. <c>[dbo].[Foo]</c>).</summary>
    protected abstract string QuoteTable(string tableName);

    // Union of all type mappings across SQL Server, MySQL, and Postgres.
    public static JsonElement ToJsonElement(object? value) => value switch
    {
        null or DBNull   => JsonSerializer.SerializeToElement<object?>(null),
        bool v           => JsonSerializer.SerializeToElement(v),
        sbyte v          => JsonSerializer.SerializeToElement((int)v),
        byte v           => JsonSerializer.SerializeToElement((int)v),
        short v          => JsonSerializer.SerializeToElement((int)v),
        ushort v         => JsonSerializer.SerializeToElement((int)v),
        int v            => JsonSerializer.SerializeToElement(v),
        uint v           => JsonSerializer.SerializeToElement((long)v),
        long v           => JsonSerializer.SerializeToElement(v),
        ulong v          => JsonSerializer.SerializeToElement(v.ToString()),
        float v          => JsonSerializer.SerializeToElement(v),
        double v         => JsonSerializer.SerializeToElement(v),
        decimal v        => JsonSerializer.SerializeToElement(v),
        string v         => JsonSerializer.SerializeToElement(v),
        Guid v           => JsonSerializer.SerializeToElement(v.ToString()),
        DateTime v       => JsonSerializer.SerializeToElement(v.ToString("O")),
        DateTimeOffset v => JsonSerializer.SerializeToElement(v.ToString("O")),
        byte[] v         => JsonSerializer.SerializeToElement(Convert.ToBase64String(v)),
        _                => JsonSerializer.SerializeToElement(value.ToString()),
    };
}
