using Manifesta.Core;
using Manifesta.Core.IR;

namespace Manifesta.Providers;

/// <summary>
/// Database introspector factory for the OSS edition.
/// Supports MySQL, PostgreSQL, and SQLite; throws <see cref="NotSupportedException"/> for SQL Server
/// (SQL Server introspection requires the full edition of Manifesta).
/// </summary>
public sealed class OssDatabaseIntrospectorFactory : IDatabaseIntrospectorFactory
{
    public IDatabaseIntrospector Create(DbProvider provider, string connectionString) =>
        provider switch
        {
            DbProvider.MySql     => new MySqlDatabaseIntrospector(connectionString),
            DbProvider.Postgres  => new PostgresDatabaseIntrospector(connectionString),
            DbProvider.Sqlite    => new SqliteDatabaseIntrospector(connectionString),
            DbProvider.SqlServer => throw new NotSupportedException(
                "SQL Server introspection requires the full edition of Manifesta. " +
                "See https://github.com/umbrelon/manifesta-enterprise"),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };

    public IReferenceDataCapturer CreateCapturer(DbProvider provider, string connectionString) =>
        provider switch
        {
            DbProvider.MySql     => new MySqlReferenceDataCapturer(connectionString),
            DbProvider.Postgres  => new PostgresReferenceDataCapturer(connectionString),
            DbProvider.Sqlite    => new SqliteReferenceDataCapturer(connectionString),
            DbProvider.SqlServer => throw new NotSupportedException(
                "SQL Server reference data capture requires the full edition of Manifesta. " +
                "See https://github.com/umbrelon/manifesta-enterprise"),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };

    public async Task<IReadOnlyList<TableDefinition>> EnrichWithReferenceDataAsync(
        IReadOnlyList<TableDefinition> tables,
        IReferenceDataCapturer capturer,
        string? schemaFilter,
        ReferenceTableConfig config,
        IDatabaseIntrospector introspector,
        GlobalOptions globals,
        CancellationToken ct)
    {
        IReadOnlyDictionary<string, int> rowCounts;
        try
        {
            rowCounts = await introspector.GetRowCountsAsync(schemaFilter, ct);
        }
        catch (Exception ex)
        {
            OutputFormatter.WriteVerbose(
                $"Warning: could not retrieve row counts — skipping reference detection: {ex.Message}", globals);
            return tables;
        }

        var referenceNames = ReferenceTableDetector.Detect(
            tables.Select(t => t.Name),
            rowCounts,
            config);

        if (referenceNames.Count == 0)
            return tables;

        OutputFormatter.WriteVerbose(
            $"Detected {referenceNames.Count} reference table(s): {string.Join(", ", referenceNames)}", globals);

        var enriched = new List<TableDefinition>(tables.Count);

        foreach (var table in tables)
        {
            if (!referenceNames.Contains(table.Name))
            {
                enriched.Add(table);
                continue;
            }

            IReadOnlyList<IReadOnlyDictionary<string, System.Text.Json.JsonElement>> data;
            try
            {
                data = await capturer.CaptureAsync(table, config.Heuristics.MaxSizeKb, ct);
            }
            catch (Exception ex)
            {
                OutputFormatter.WriteVerbose(
                    $"Warning: could not capture data for {table.Name}: {ex.Message}", globals);
                enriched.Add(table with { IsReferenceTable = true });
                continue;
            }

            enriched.Add(table with { IsReferenceTable = true, Data = data });
            OutputFormatter.WriteVerbose($"Captured {data.Count} row(s) for reference table: {table.Name}", globals);
        }

        return enriched;
    }
}
