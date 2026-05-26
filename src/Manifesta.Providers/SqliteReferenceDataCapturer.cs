using System.Data.Common;
using Manifesta.Core;
using Microsoft.Data.Sqlite;

namespace Manifesta.Providers;

/// <summary>
/// Captures reference-table row data from a SQLite database.
/// Table names are bare (no schema prefix); double-quote identifier quoting is used.
/// </summary>
public sealed class SqliteReferenceDataCapturer : ReferenceDataCapturerBase
{
    private readonly string _connectionString;

    public SqliteReferenceDataCapturer(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    protected override DbConnection CreateConnection() => new SqliteConnection(_connectionString);

    protected override string QuoteColumn(string column) => $"\"{column.Replace("\"", "\"\"")}\"";

    protected override string QuoteTable(string tableName) => $"\"{tableName.Replace("\"", "\"\"")}\"";
}
