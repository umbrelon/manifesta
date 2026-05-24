using Manifesta.Core;
using Manifesta.Core.IR;
using MySqlConnector;

namespace Manifesta.Providers;

/// <summary>
/// Introspects a MySQL database and extracts table and view schemas.
/// Table names are returned without a schema prefix (e.g. <c>bundle</c>).
/// The schemaFilter parameter is respected; when omitted, all tables in the connected
/// database are returned.
/// </summary>
public sealed class MySqlDatabaseIntrospector : DatabaseIntrospectorBase
{
    public MySqlDatabaseIntrospector(string connectionString) : base(connectionString) { }

    public override async Task<IReadOnlyDictionary<string, int>> GetRowCountsAsync(
        string? schemaFilter = null,
        CancellationToken ct = default)
    {
        using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(ct);

        const string sql = @"
            SELECT table_name, table_rows
            FROM information_schema.tables
            WHERE table_schema = DATABASE()
              AND table_type = 'BASE TABLE'";

        using var command = new MySqlCommand(sql, connection);
        command.CommandTimeout = 30;

        var result = new Dictionary<string, int>(TableNames.Comparer);
        using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var tableName = reader.GetString(0);
            var rowCount  = Convert.ToInt32(reader.GetValue(1));
            result[tableName] = rowCount;
        }

        return result;
    }

    protected override async Task<IReadOnlyList<TableDefinition>> IntrospectInternalAsync(
        string? tableTypesOnly,
        string? schemaFilter,
        CancellationToken ct)
    {
        using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(ct);

        var tables            = await LoadTablesAndViews(connection, tableTypesOnly, ct);
        var primaryKeys       = await LoadPrimaryKeys(connection, ct);
        var foreignKeys       = await LoadForeignKeys(connection, ct);
        var computedColumns   = await LoadComputedColumns(connection, ct);
        var indexes           = await LoadIndexes(connection, ct);
        var checkConstraints  = await LoadCheckConstraints(connection, ct);
        var uniqueConstraints = await LoadUniqueConstraints(connection, ct);

        return CombineFlat(tables, primaryKeys, foreignKeys, computedColumns, indexes, checkConstraints, uniqueConstraints);
    }

    private static async Task<List<(string TableName, List<FieldDefinition> Fields)>> LoadTablesAndViews(
        MySqlConnection connection,
        string?         tableTypesOnly,
        CancellationToken ct)
    {
        var result = new List<(string, List<FieldDefinition>)>();

        var tableTypeFilter = tableTypesOnly switch
        {
            null          => "IN ('BASE TABLE', 'VIEW')",
            "BASE TABLE"  => "= 'BASE TABLE'",
            "VIEW"        => "= 'VIEW'",
            _             => $"= '{tableTypesOnly}'"
        };

        var sql = $@"
            SELECT
                t.table_name,
                c.column_name,
                c.data_type,
                c.character_maximum_length,
                c.numeric_precision,
                c.numeric_scale,
                CASE WHEN c.is_nullable = 'YES' THEN 1 ELSE 0 END AS is_nullable,
                c.column_default
            FROM information_schema.tables t
            JOIN information_schema.columns c
                ON t.table_schema = c.table_schema
               AND t.table_name   = c.table_name
            WHERE t.table_schema = DATABASE()
              AND t.table_type {tableTypeFilter}
            ORDER BY t.table_name, c.ordinal_position";

        using var command = new MySqlCommand(sql, connection);
        command.CommandTimeout = 30;

        using var reader = await command.ExecuteReaderAsync(ct);

        string?              currentTableName = null;
        List<FieldDefinition>? currentFields  = null;

        while (await reader.ReadAsync(ct))
        {
            var tableName        = reader.GetString(0);
            var columnName       = reader.GetString(1);
            var dataType         = reader.GetString(2);
            var charMaxLength    = reader.IsDBNull(3) ? (int?)null : Convert.ToInt32(reader.GetValue(3));
            var numericPrecision = reader.IsDBNull(4) ? (int?)null : Convert.ToInt32(reader.GetValue(4));
            var numericScale     = reader.IsDBNull(5) ? (int?)null : Convert.ToInt32(reader.GetValue(5));
            var isNullable       = Convert.ToBoolean(reader.GetValue(6));
            var rawDefault       = reader.IsDBNull(7) ? null : reader.GetString(7);

            if (currentTableName == null || !tableName.Equals(currentTableName, StringComparison.OrdinalIgnoreCase))
            {
                if (currentTableName != null && currentFields != null)
                    result.Add((currentTableName, currentFields));

                currentTableName = tableName;
                currentFields    = [];
            }

            currentFields!.Add(new FieldDefinition
            {
                Name     = columnName,
                Type     = BuildTypeString(dataType, charMaxLength, numericPrecision, numericScale),
                Nullable = isNullable,
                Default  = string.IsNullOrWhiteSpace(rawDefault) ? null : rawDefault,
            });
        }

        if (currentTableName != null && currentFields != null)
            result.Add((currentTableName, currentFields));

        return result;
    }

    private static async Task<Dictionary<string, List<string>>> LoadPrimaryKeys(
        MySqlConnection connection,
        CancellationToken ct)
    {
        const string sql = @"
            SELECT table_name, column_name
            FROM information_schema.key_column_usage
            WHERE table_schema    = DATABASE()
              AND constraint_name = 'PRIMARY'
            ORDER BY table_name, ordinal_position";

        using var command = new MySqlCommand(sql, connection);
        command.CommandTimeout = 30;

        var result = new Dictionary<string, List<string>>(TableNames.Comparer);
        using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var tableName = reader.GetString(0);
            var column    = reader.GetString(1);

            if (!result.TryGetValue(tableName, out var cols))
                result[tableName] = cols = [];

            cols.Add(column);
        }

        return result;
    }

    private static async Task<Dictionary<string, List<(string Source, string TargetTable, string TargetColumn, bool CascadeDelete)>>> LoadForeignKeys(
        MySqlConnection connection,
        CancellationToken ct)
    {
        const string sql = @"
            SELECT
                kcu.table_name,
                kcu.column_name,
                kcu.referenced_table_name,
                kcu.referenced_column_name,
                rc.delete_rule
            FROM information_schema.key_column_usage kcu
            JOIN information_schema.referential_constraints rc
                ON kcu.constraint_schema = rc.constraint_schema
               AND kcu.constraint_name   = rc.constraint_name
            WHERE kcu.table_schema      = DATABASE()
              AND kcu.referenced_table_name IS NOT NULL
            ORDER BY kcu.table_name, kcu.constraint_name, kcu.ordinal_position";

        using var command = new MySqlCommand(sql, connection);
        command.CommandTimeout = 30;

        var result = new Dictionary<string, List<(string, string, string, bool)>>(TableNames.Comparer);
        using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var tableName   = reader.GetString(0);
            var sourceCol   = reader.GetString(1);
            var targetTable = reader.GetString(2);
            var targetCol   = reader.GetString(3);
            var cascade     = reader.GetString(4).Equals("CASCADE", StringComparison.OrdinalIgnoreCase);

            if (!result.TryGetValue(tableName, out var fks))
                result[tableName] = fks = [];

            fks.Add((sourceCol, targetTable, targetCol, cascade));
        }

        return result;
    }

    private static async Task<Dictionary<string, List<(string Column, string Expression, bool IsPersisted)>>> LoadComputedColumns(
        MySqlConnection connection,
        CancellationToken ct)
    {
        const string sql = @"
            SELECT table_name, column_name, generation_expression, extra
            FROM information_schema.columns
            WHERE table_schema      = DATABASE()
              AND generation_expression IS NOT NULL
            ORDER BY table_name, ordinal_position";

        using var command = new MySqlCommand(sql, connection);
        command.CommandTimeout = 30;

        var result = new Dictionary<string, List<(string, string, bool)>>(TableNames.Comparer);
        using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var tableName  = reader.GetString(0);
            var column     = reader.GetString(1);
            var expression = reader.GetString(2);
            var extra      = reader.GetString(3);
            var isPersisted = extra.Contains("STORED", StringComparison.OrdinalIgnoreCase);

            if (!result.TryGetValue(tableName, out var cols))
                result[tableName] = cols = [];

            cols.Add((column, expression, isPersisted));
        }

        return result;
    }

    private static async Task<Dictionary<string, List<(string Name, string Columns, bool IsUnique)>>> LoadIndexes(
        MySqlConnection connection,
        CancellationToken ct)
    {
        // seq_in_index = 1 for the first column in each index.
        // We exclude PRIMARY index (it appears in key_column_usage).
        // We also exclude unique indexes (those back a UNIQUE constraint); they appear in LoadUniqueConstraints.
        const string sql = @"
            SELECT table_name, index_name, GROUP_CONCAT(column_name ORDER BY seq_in_index), non_unique
            FROM information_schema.statistics
            WHERE table_schema = DATABASE()
              AND index_name  != 'PRIMARY'
              AND non_unique   = 1
            GROUP BY table_name, index_name, non_unique
            ORDER BY table_name, index_name";

        using var command = new MySqlCommand(sql, connection);
        command.CommandTimeout = 30;

        var result = new Dictionary<string, List<(string, string, bool)>>(TableNames.Comparer);
        using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var tableName = reader.GetString(0);
            var indexName = reader.GetString(1);
            var columns   = reader.GetString(2);
            var isUnique  = Convert.ToInt32(reader.GetValue(3)) == 0;

            if (!result.TryGetValue(tableName, out var idxs))
                result[tableName] = idxs = [];

            idxs.Add((indexName, columns, isUnique));
        }

        return result;
    }

    private static async Task<Dictionary<string, List<(string Name, string Expression, string? Column)>>> LoadCheckConstraints(
        MySqlConnection connection,
        CancellationToken ct)
    {
        // CHECK constraints only in MySQL 8.0.16+
        const string sql = @"
            SELECT tc.table_name, cc.constraint_name, cc.check_clause
            FROM information_schema.table_constraints tc
            JOIN information_schema.check_constraints cc
                ON tc.constraint_catalog = cc.constraint_catalog
               AND tc.constraint_schema  = cc.constraint_schema
               AND tc.constraint_name    = cc.constraint_name
            WHERE tc.table_schema = DATABASE()
              AND tc.constraint_type = 'CHECK'
            ORDER BY tc.table_name, cc.constraint_name";

        using var command = new MySqlCommand(sql, connection);
        command.CommandTimeout = 30;

        var result = new Dictionary<string, List<(string, string, string?)>>(TableNames.Comparer);
        using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var tableName  = reader.GetString(0);
            var name       = reader.GetString(1);
            var expression = reader.GetString(2);

            if (!result.TryGetValue(tableName, out var checks))
                result[tableName] = checks = [];

            checks.Add((name, expression, null));
        }

        return result;
    }

    private static async Task<Dictionary<string, List<(string Name, string Columns)>>> LoadUniqueConstraints(
        MySqlConnection connection,
        CancellationToken ct)
    {
        const string sql = @"
            SELECT table_name, index_name, GROUP_CONCAT(column_name ORDER BY seq_in_index)
            FROM information_schema.statistics
            WHERE table_schema = DATABASE()
              AND index_name  != 'PRIMARY'
              AND non_unique   = 0
            GROUP BY table_name, index_name
            ORDER BY table_name, index_name";

        using var command = new MySqlCommand(sql, connection);
        command.CommandTimeout = 30;

        var result = new Dictionary<string, List<(string, string)>>(TableNames.Comparer);
        using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var tableName = reader.GetString(0);
            var name      = reader.GetString(1);
            var columns   = reader.GetString(2);

            if (!result.TryGetValue(tableName, out var ucs))
                result[tableName] = ucs = [];

            ucs.Add((name, columns));
        }

        return result;
    }

    // ── Flat combiner (no schema prefix) ─────────────────────────────────────

    private static IReadOnlyList<TableDefinition> CombineFlat(
        List<(string TableName, List<FieldDefinition> Fields)>                         tables,
        Dictionary<string, List<string>>                                               primaryKeys,
        Dictionary<string, List<(string Source, string TargetTable, string TargetColumn, bool CascadeDelete)>> foreignKeys,
        Dictionary<string, List<(string Column, string Expression, bool IsPersisted)>> computedColumns,
        Dictionary<string, List<(string Name, string Columns, bool IsUnique)>>         indexes,
        Dictionary<string, List<(string Name, string Expression, string? Column)>>     checkConstraints,
        Dictionary<string, List<(string Name, string Columns)>>                        uniqueConstraints)
    {
        return tables
            .Select(t =>
            {
                var tablePks = primaryKeys.TryGetValue(t.TableName, out var pks) ? pks : [];

                var tableFks = foreignKeys.TryGetValue(t.TableName, out var fks)
                    ? fks.Select(fk => new ForeignKey
                    {
                        SourceField   = fk.Source,
                        TargetTable   = fk.TargetTable,
                        TargetField   = fk.TargetColumn,
                        CascadeDelete = fk.CascadeDelete
                    }).ToList()
                    : (List<ForeignKey>)[];

                var computed = computedColumns.TryGetValue(t.TableName, out var cc)
                    ? cc.ToDictionary(c => c.Column, c => c, StringComparer.OrdinalIgnoreCase)
                    : null;

                var fields = t.Fields.Select(f =>
                {
                    if (computed != null && computed.TryGetValue(f.Name, out var ccInfo))
                        return f with
                        {
                            IsComputed         = true,
                            ComputedExpression = ccInfo.Expression,
                            IsPersisted        = ccInfo.IsPersisted,
                        };
                    return f;
                }).ToList();

                var tableIndexes = indexes.TryGetValue(t.TableName, out var idxList)
                    ? idxList.Select(i => new IndexDefinition
                    {
                        Name    = i.Name,
                        Columns = i.Columns.Split(',', StringSplitOptions.TrimEntries).ToList(),
                        IsUnique = i.IsUnique,
                    }).ToList()
                    : (List<IndexDefinition>)[];

                var tableChecks = checkConstraints.TryGetValue(t.TableName, out var chkList)
                    ? chkList.Select(c => new CheckConstraint
                    {
                        Name       = c.Name,
                        Expression = c.Expression,
                        Column     = c.Column,
                    }).ToList()
                    : (List<CheckConstraint>)[];

                var tableUniques = uniqueConstraints.TryGetValue(t.TableName, out var ucList)
                    ? ucList.Select(u => new UniqueConstraint
                    {
                        Name    = u.Name,
                        Columns = u.Columns.Split(',', StringSplitOptions.TrimEntries).ToList(),
                    }).ToList()
                    : (List<UniqueConstraint>)[];

                return new TableDefinition
                {
                    Name              = t.TableName,
                    Fields            = fields,
                    PrimaryKey        = tablePks,
                    ForeignKeys       = tableFks,
                    Indexes           = tableIndexes,
                    CheckConstraints  = tableChecks,
                    UniqueConstraints = tableUniques,
                };
            })
            .OrderBy(t => t.Name)
            .ToList();
    }

    private static string BuildTypeString(
        string dataType,
        int?   charMaxLength,
        int?   numericPrecision,
        int?   numericScale)
    {
        return dataType.ToLowerInvariant() switch
        {
            "varchar" or "char" or "binary" or "varbinary"
                => charMaxLength.HasValue ? $"{dataType}({charMaxLength})" : dataType,
            "decimal" or "numeric"
                => (numericPrecision.HasValue, numericScale.HasValue) switch
                {
                    (true, true)  => $"{dataType}({numericPrecision},{numericScale})",
                    (true, false) => $"{dataType}({numericPrecision})",
                    _             => dataType
                },
            _ => dataType
        };
    }
}
