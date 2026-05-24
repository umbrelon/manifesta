using System.Data.Common;
using Manifesta.Core;
using MySqlConnector;

namespace Manifesta.Providers;

/// <summary>
/// Captures reference-table row data from a MySQL database.
/// Table names are bare (no schema prefix); backtick quoting is used.
/// </summary>
public sealed class MySqlReferenceDataCapturer : ReferenceDataCapturerBase
{
    private readonly string _connectionString;

    public MySqlReferenceDataCapturer(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    protected override DbConnection CreateConnection() => new MySqlConnection(_connectionString);

    protected override string QuoteColumn(string column) => $"`{column}`";

    protected override string QuoteTable(string tableName) => $"`{tableName}`";
}
