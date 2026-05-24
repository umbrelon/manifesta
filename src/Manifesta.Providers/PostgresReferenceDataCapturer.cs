using System.Data.Common;
using Manifesta.Core;
using Npgsql;

namespace Manifesta.Providers;

/// <summary>
/// Captures reference-table row data from a PostgreSQL database.
/// Table names are schema-qualified (e.g. <c>public.bundle</c>); double-quote quoting is used.
/// </summary>
public sealed class PostgresReferenceDataCapturer : ReferenceDataCapturerBase
{
    private readonly string _connectionString;

    public PostgresReferenceDataCapturer(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    protected override DbConnection CreateConnection() => new NpgsqlConnection(_connectionString);

    protected override string QuoteColumn(string column) => $"\"{column}\"";

    protected override string QuoteTable(string tableName)
    {
        var dot = tableName.IndexOf('.');
        return dot >= 0
            ? $"\"{tableName[..dot]}\".\"{tableName[(dot + 1)..]}\""
            : $"\"{tableName}\"";
    }
}
