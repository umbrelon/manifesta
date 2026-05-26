using Manifesta.Core;
using Manifesta.Core.IR;
using Microsoft.Data.Sqlite;

namespace Manifesta.Providers;

/// <summary>
/// Introspects a SQLite database and extracts table and view schemas.
/// Table names are returned without a schema prefix (e.g. <c>orders</c>).
/// The schemaFilter parameter is not supported for SQLite and is silently ignored.
/// </summary>
public sealed class SqliteDatabaseIntrospector : DatabaseIntrospectorBase
{
    public SqliteDatabaseIntrospector(string connectionString) : base(connectionString) { }

    public override async Task<IReadOnlyDictionary<string, int>> GetRowCountsAsync(
        string? schemaFilter = null,
        CancellationToken ct = default)
    {
        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);

        var tableNames = await GetObjectNamesAsync(connection, "table", ct);
        var result = new Dictionary<string, int>(TableNames.Comparer);

        foreach (var tableName in tableNames)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {QuoteIdentifier(tableName)}";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            result[tableName] = count;
        }

        return result;
    }

    protected override async Task<IReadOnlyList<TableDefinition>> IntrospectInternalAsync(
        string? tableTypesOnly,
        string? schemaFilter,
        CancellationToken ct)
    {
        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);

        bool includeTables = tableTypesOnly is null or "BASE TABLE";
        bool includeViews  = tableTypesOnly is null or "VIEW";

        var result = new List<TableDefinition>();

        if (includeTables)
        {
            var tableNames = await GetObjectNamesAsync(connection, "table", ct);
            foreach (var name in tableNames)
                result.Add(await BuildTableDefinitionAsync(connection, name, ct));
        }

        if (includeViews)
        {
            var viewNames = await GetObjectNamesAsync(connection, "view", ct);
            foreach (var name in viewNames)
                result.Add(await BuildViewDefinitionAsync(connection, name, ct));
        }

        return result.OrderBy(t => t.Name).ToList();
    }

    private static async Task<List<string>> GetObjectNamesAsync(
        SqliteConnection connection,
        string type,
        CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = type == "table"
            ? "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name"
            : "SELECT name FROM sqlite_master WHERE type='view' ORDER BY name";

        var names = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            names.Add(reader.GetString(0));
        return names;
    }

    private static async Task<TableDefinition> BuildTableDefinitionAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken ct)
    {
        var (fields, primaryKey)         = await LoadColumnsAsync(connection, tableName, ct);
        var foreignKeys                  = await LoadForeignKeysAsync(connection, tableName, ct);
        var (indexes, uniqueConstraints) = await LoadIndexesAsync(connection, tableName, ct);

        return new TableDefinition
        {
            Name              = tableName,
            Fields            = fields,
            PrimaryKey        = primaryKey,
            ForeignKeys       = foreignKeys,
            Indexes           = indexes,
            CheckConstraints  = [],
            UniqueConstraints = uniqueConstraints,
        };
    }

    private static async Task<TableDefinition> BuildViewDefinitionAsync(
        SqliteConnection connection,
        string viewName,
        CancellationToken ct)
    {
        var (fields, _) = await LoadColumnsAsync(connection, viewName, ct);

        return new TableDefinition
        {
            Name   = viewName,
            Fields = fields,
        };
    }

    private static async Task<(List<FieldDefinition> Fields, List<string> PrimaryKey)> LoadColumnsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        // table_xinfo available since SQLite 3.37.0 (Nov 2021); Microsoft.Data.Sqlite bundles >= 3.37.
        // Columns: cid, name, type, notnull, dflt_value, pk, hidden
        // hidden: 0=normal, 2=virtual generated, 3=stored generated
        cmd.CommandText = $"PRAGMA table_xinfo({QuoteIdentifier(tableName)})";

        var fields        = new List<FieldDefinition>();
        var primaryKeyMap = new SortedDictionary<int, string>(); // pk order → column name

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name    = reader.GetString(1);
            var type    = reader.GetString(2);
            var notnull = reader.GetInt32(3);
            var dflt    = reader.IsDBNull(4) ? (string?)null : reader.GetString(4);
            var pk      = reader.GetInt32(5); // 0 = not part of PK, >0 = PK column order
            var hidden  = reader.GetInt32(6);

            fields.Add(new FieldDefinition
            {
                Name        = name,
                Type        = string.IsNullOrWhiteSpace(type) ? "TEXT" : type,
                Nullable    = notnull == 0,
                Default     = string.IsNullOrWhiteSpace(dflt) ? null : dflt,
                IsComputed  = hidden is 2 or 3,
                IsPersisted = hidden == 3,
            });

            if (pk > 0)
                primaryKeyMap[pk] = name;
        }

        return (fields, [.. primaryKeyMap.Values]);
    }

    private static async Task<List<ForeignKey>> LoadForeignKeysAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        // Columns: id, seq, table, from, to, on_update, on_delete, match
        // 'to' is NULL when the FK implicitly references the PK of the target table.
        cmd.CommandText = $"PRAGMA foreign_key_list({QuoteIdentifier(tableName)})";

        var foreignKeys = new List<ForeignKey>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var targetTable = reader.GetString(2);
            var sourceCol   = reader.GetString(3);
            var targetColRaw = reader.IsDBNull(4) ? null : reader.GetString(4);
            var targetCol   = string.IsNullOrEmpty(targetColRaw) ? sourceCol : targetColRaw;
            var onDelete    = reader.GetString(6);

            foreignKeys.Add(new ForeignKey
            {
                SourceField   = sourceCol,
                TargetTable   = targetTable,
                TargetField   = targetCol,
                CascadeDelete = onDelete.Equals("CASCADE", StringComparison.OrdinalIgnoreCase),
            });
        }

        return foreignKeys;
    }

    private static async Task<(List<IndexDefinition> Indexes, List<UniqueConstraint> UniqueConstraints)> LoadIndexesAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken ct)
    {
        using var listCmd = connection.CreateCommand();
        // Columns: seq, name, unique, origin, partial
        // origin: 'c'=user-created, 'u'=UNIQUE constraint, 'pk'=PRIMARY KEY
        listCmd.CommandText = $"PRAGMA index_list({QuoteIdentifier(tableName)})";

        var entries = new List<(string Name, bool IsUnique, string Origin)>();
        using (var reader = await listCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                entries.Add((reader.GetString(1), reader.GetInt32(2) == 1, reader.GetString(3)));
        }

        var indexes           = new List<IndexDefinition>();
        var uniqueConstraints = new List<UniqueConstraint>();

        foreach (var (name, isUnique, origin) in entries)
        {
            // Skip the implicit rowid index that backs INTEGER PRIMARY KEY.
            if (origin == "pk")
                continue;

            var columns = await LoadIndexColumnsAsync(connection, name, ct);

            if (isUnique)
                uniqueConstraints.Add(new UniqueConstraint { Name = name, Columns = columns });
            else
                indexes.Add(new IndexDefinition { Name = name, Columns = columns, IsUnique = false });
        }

        return (indexes, uniqueConstraints);
    }

    private static async Task<List<string>> LoadIndexColumnsAsync(
        SqliteConnection connection,
        string indexName,
        CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        // Columns: seqno, cid, name
        cmd.CommandText = $"PRAGMA index_info({QuoteIdentifier(indexName)})";

        var columns = new SortedDictionary<int, string>(); // seqno → column name
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            columns[reader.GetInt32(0)] = reader.GetString(2);

        return [.. columns.Values];
    }

    private static string QuoteIdentifier(string name) => $"\"{name.Replace("\"", "\"\"")}\"";
}
