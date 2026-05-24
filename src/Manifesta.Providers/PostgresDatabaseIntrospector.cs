using System.Collections.Frozen;
using Manifesta.Core;
using Manifesta.Core.IR;
using Npgsql;

namespace Manifesta.Providers;

/// <summary>
/// Introspects a PostgreSQL database and extracts table and view schemas.
/// Table names are returned with schema prefix (e.g. <c>public.bundle</c>).
/// The schemaFilter parameter is respected; when omitted, all non-system schemas are returned.
/// </summary>
public sealed class PostgresDatabaseIntrospector : DatabaseIntrospectorBase
{
    public PostgresDatabaseIntrospector(string connectionString) : base(connectionString) { }

    public override async Task<IReadOnlyDictionary<string, int>> GetRowCountsAsync(
        string? schemaFilter = null,
        CancellationToken ct = default)
    {
        var allowedSchemas = ParseSchemaFilter(schemaFilter);

        using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);

        const string sql = @"
            SELECT
                n.nspname                       AS schema_name,
                c.relname                       AS table_name,
                GREATEST(0, c.reltuples)::bigint AS row_count
            FROM pg_class c
            JOIN pg_namespace n ON c.relnamespace = n.oid
            WHERE c.relkind = 'r'
              AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')";

        using var command = new NpgsqlCommand(sql, connection);
        command.CommandTimeout = 30;

        var result = new Dictionary<string, int>(TableNames.Comparer);
        using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var schema    = reader.GetString(0);
            var tableName = reader.GetString(1);
            var rowCount  = Convert.ToInt32(reader.GetValue(2));

            if (allowedSchemas != null && !allowedSchemas.Contains(schema))
                continue;

            result[$"{schema}.{tableName}"] = rowCount;
        }

        return result;
    }

    protected override async Task<IReadOnlyList<TableDefinition>> IntrospectInternalAsync(
        string? tableTypesOnly,
        string? schemaFilter,
        CancellationToken ct)
    {
        var allowedSchemas = ParseSchemaFilter(schemaFilter);

        using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);

        var tables            = await LoadTablesAndViews(connection, tableTypesOnly, allowedSchemas, ct);
        var primaryKeys       = await LoadPrimaryKeys(connection, allowedSchemas, ct);
        var foreignKeys       = await LoadForeignKeys(connection, allowedSchemas, ct);
        var computedColumns   = await LoadComputedColumns(connection, allowedSchemas, ct);
        var indexes           = await LoadIndexes(connection, allowedSchemas, ct);
        var checkConstraints  = await LoadCheckConstraints(connection, allowedSchemas, ct);
        var uniqueConstraints = await LoadUniqueConstraints(connection, allowedSchemas, ct);

        return CombineSchemaAware(tables, primaryKeys, foreignKeys, computedColumns, indexes, checkConstraints, uniqueConstraints);
    }

    private static async Task<List<SchemaTableInfo>> LoadTablesAndViews(
        NpgsqlConnection   connection,
        string?            tableTypesOnly,
        FrozenSet<string>? allowedSchemas,
        CancellationToken  ct)
    {
        var tables = new List<SchemaTableInfo>();

        var tableTypeFilter = tableTypesOnly switch
        {
            null => "IN ('BASE TABLE', 'VIEW')",
            _    => $"= '{tableTypesOnly}'"
        };

        var sql = $@"
            SELECT
                t.table_schema,
                t.table_name,
                c.column_name,
                c.data_type,
                c.udt_name,
                c.character_maximum_length,
                c.numeric_precision,
                c.numeric_scale,
                CASE WHEN c.is_nullable = 'YES' THEN true ELSE false END AS is_nullable,
                c.column_default
            FROM information_schema.tables t
            JOIN information_schema.columns c
                ON t.table_catalog = c.table_catalog
               AND t.table_schema  = c.table_schema
               AND t.table_name    = c.table_name
            WHERE t.table_type {tableTypeFilter}
              AND t.table_schema NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
            ORDER BY t.table_schema, t.table_name, c.ordinal_position";

        using var command = new NpgsqlCommand(sql, connection);
        command.CommandTimeout = 30;

        using var reader = await command.ExecuteReaderAsync(ct);

        SchemaTableInfo? currentTable = null;

        while (await reader.ReadAsync(ct))
        {
            var schema           = reader.GetString(0);
            var tableName        = reader.GetString(1);
            var columnName       = reader.GetString(2);
            var dataType         = reader.GetString(3);
            var udtName          = reader.GetString(4);
            var charMaxLength    = reader.IsDBNull(5) ? (int?)null : Convert.ToInt32(reader.GetValue(5));
            var numericPrecision = reader.IsDBNull(6) ? (int?)null : Convert.ToInt32(reader.GetValue(6));
            var numericScale     = reader.IsDBNull(7) ? (int?)null : Convert.ToInt32(reader.GetValue(7));
            var isNullable       = Convert.ToBoolean(reader.GetValue(8));
            var rawDefault       = reader.IsDBNull(9) ? null : reader.GetString(9);

            if (allowedSchemas != null && !allowedSchemas.Contains(schema))
                continue;

            if (currentTable == null ||
                !schema.Equals(currentTable.Schema, StringComparison.OrdinalIgnoreCase) ||
                !tableName.Equals(currentTable.TableName, StringComparison.OrdinalIgnoreCase))
            {
                if (currentTable != null)
                    tables.Add(currentTable);

                currentTable = new SchemaTableInfo { Schema = schema, TableName = tableName, Fields = [] };
            }

            currentTable.Fields.Add(new FieldDefinition
            {
                Name     = columnName,
                Type     = BuildTypeString(dataType, udtName, charMaxLength, numericPrecision, numericScale),
                Nullable = isNullable,
                Default  = string.IsNullOrWhiteSpace(rawDefault) ? null : rawDefault,
            });
        }

        if (currentTable != null)
            tables.Add(currentTable);

        return tables;
    }

    private static async Task<List<SchemaPrimaryKeyInfo>> LoadPrimaryKeys(
        NpgsqlConnection   connection,
        FrozenSet<string>? allowedSchemas,
        CancellationToken  ct)
    {
        const string sql = @"
            SELECT kcu.table_schema, kcu.table_name, kcu.column_name
            FROM information_schema.key_column_usage kcu
            JOIN information_schema.table_constraints tc
                ON kcu.constraint_catalog = tc.constraint_catalog
               AND kcu.constraint_schema  = tc.constraint_schema
               AND kcu.constraint_name    = tc.constraint_name
            WHERE tc.constraint_type = 'PRIMARY KEY'
              AND kcu.table_schema NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
            ORDER BY kcu.table_schema, kcu.table_name, kcu.ordinal_position";

        using var command = new NpgsqlCommand(sql, connection);
        command.CommandTimeout = 30;

        var result = new List<SchemaPrimaryKeyInfo>();
        using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var schema = reader.GetString(0);

            if (allowedSchemas != null && !allowedSchemas.Contains(schema))
                continue;

            result.Add(new SchemaPrimaryKeyInfo(schema, reader.GetString(1), reader.GetString(2)));
        }

        return result;
    }

    private static async Task<List<SchemaForeignKeyInfo>> LoadForeignKeys(
        NpgsqlConnection   connection,
        FrozenSet<string>? allowedSchemas,
        CancellationToken  ct)
    {
        const string sql = @"
            SELECT
                kcu1.table_schema AS source_schema,
                kcu1.table_name   AS source_table,
                kcu1.column_name  AS source_column,
                kcu2.table_schema AS target_schema,
                kcu2.table_name   AS target_table,
                kcu2.column_name  AS target_column,
                rc.delete_rule
            FROM information_schema.referential_constraints rc
            JOIN information_schema.key_column_usage kcu1
                ON rc.constraint_catalog = kcu1.constraint_catalog
               AND rc.constraint_schema  = kcu1.constraint_schema
               AND rc.constraint_name    = kcu1.constraint_name
            JOIN information_schema.key_column_usage kcu2
                ON rc.unique_constraint_catalog = kcu2.constraint_catalog
               AND rc.unique_constraint_schema  = kcu2.constraint_schema
               AND rc.unique_constraint_name    = kcu2.constraint_name
               AND kcu1.ordinal_position        = kcu2.ordinal_position
            ORDER BY rc.constraint_name, kcu1.ordinal_position";

        using var command = new NpgsqlCommand(sql, connection);
        command.CommandTimeout = 30;

        var result = new List<SchemaForeignKeyInfo>();
        using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var sourceSchema = reader.GetString(0);

            if (allowedSchemas != null && !allowedSchemas.Contains(sourceSchema))
                continue;

            result.Add(new SchemaForeignKeyInfo(
                SourceSchema:     sourceSchema,
                SourceTableName:  reader.GetString(1),
                SourceColumnName: reader.GetString(2),
                TargetSchema:     reader.GetString(3),
                TargetTableName:  reader.GetString(4),
                TargetColumnName: reader.GetString(5),
                CascadeDelete:    reader.GetString(6).Equals("CASCADE", StringComparison.OrdinalIgnoreCase)));
        }

        return result;
    }

    private static async Task<List<SchemaComputedColumnInfo>> LoadComputedColumns(
        NpgsqlConnection   connection,
        FrozenSet<string>? allowedSchemas,
        CancellationToken  ct)
    {
        // PostgreSQL only supports STORED generated columns (no VIRTUAL).
        const string sql = @"
            SELECT table_schema, table_name, column_name, generation_expression
            FROM information_schema.columns
            WHERE is_generated = 'ALWAYS'
              AND table_schema NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
            ORDER BY table_schema, table_name, ordinal_position";

        using var command = new NpgsqlCommand(sql, connection);
        command.CommandTimeout = 30;

        var result = new List<SchemaComputedColumnInfo>();
        using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var schema = reader.GetString(0);

            if (allowedSchemas != null && !allowedSchemas.Contains(schema))
                continue;

            result.Add(new SchemaComputedColumnInfo(
                Schema:     schema,
                TableName:  reader.GetString(1),
                ColumnName: reader.GetString(2),
                Definition: reader.IsDBNull(3) ? "" : reader.GetString(3),
                IsPersisted: true)); // PostgreSQL only supports STORED generated columns
        }

        return result;
    }

    /// <summary>
    /// Loads non-PK, non-unique-constraint indexes from pg_index.
    /// Unique constraint indexes (those backing a UNIQUE constraint in pg_constraint) are excluded
    /// — they are returned by <see cref="LoadUniqueConstraints"/> instead.
    /// Expression-based (functional) indexes whose key columns cannot be resolved to simple attribute
    /// names are silently skipped.
    /// </summary>
    private static async Task<List<SchemaIndexInfo>> LoadIndexes(
        NpgsqlConnection   connection,
        FrozenSet<string>? allowedSchemas,
        CancellationToken  ct)
    {
        // indnkeyatts (PostgreSQL 11+) separates key columns from INCLUDE columns.
        // Key columns: ordinal position <= indnkeyatts.
        // Included columns: ordinal position > indnkeyatts.
        // Expression columns have attnum = 0 and are excluded from both lists.
        const string sql = @"
            SELECT
                n.nspname                                                      AS schema_name,
                t.relname                                                      AS table_name,
                i.relname                                                      AS index_name,
                ix.indisunique                                                 AS is_unique,
                ix.indisclustered                                              AS is_clustered,
                (ix.indpred IS NOT NULL)                                       AS is_filtered,
                pg_get_expr(ix.indpred, ix.indrelid)                         AS filter_expression,
                (
                    SELECT string_agg(a.attname, ',' ORDER BY k.ord)
                    FROM   unnest(ix.indkey::int[]) WITH ORDINALITY AS k(key, ord)
                    JOIN   pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.key AND a.attnum > 0
                    WHERE  k.ord <= ix.indnkeyatts
                )                                                              AS key_columns,
                (
                    SELECT string_agg(a.attname, ',' ORDER BY k.ord)
                    FROM   unnest(ix.indkey::int[]) WITH ORDINALITY AS k(key, ord)
                    JOIN   pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.key AND a.attnum > 0
                    WHERE  k.ord > ix.indnkeyatts
                )                                                              AS included_columns
            FROM  pg_index     ix
            JOIN  pg_class      t ON t.oid = ix.indrelid
            JOIN  pg_class      i ON i.oid = ix.indexrelid
            JOIN  pg_namespace  n ON n.oid = t.relnamespace
            WHERE NOT ix.indisprimary
              AND t.relkind = 'r'
              AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
              AND NOT EXISTS (
                    SELECT 1 FROM pg_constraint c
                    WHERE  c.conindid = ix.indexrelid AND c.contype = 'u'
              )
            ORDER BY n.nspname, t.relname, i.relname";

        using var command = new NpgsqlCommand(sql, connection);
        command.CommandTimeout = 30;

        var result = new List<SchemaIndexInfo>();
        using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var schema = reader.GetString(0);
            if (allowedSchemas != null && !allowedSchemas.Contains(schema))
                continue;

            // key_columns is NULL when every key column is an expression (functional index);
            // skip those rather than producing an IndexDefinition with an empty Columns list.
            if (reader.IsDBNull(7))
                continue;

            var keyColumns = reader.GetString(7);
            if (string.IsNullOrWhiteSpace(keyColumns))
                continue;

            result.Add(new SchemaIndexInfo(
                Schema:           schema,
                TableName:        reader.GetString(1),
                IndexName:        reader.GetString(2),
                Columns:          keyColumns,
                IsUnique:         reader.GetBoolean(3),
                IsClustered:      reader.GetBoolean(4),
                IsFiltered:       reader.GetBoolean(5),
                FilterExpression: reader.IsDBNull(6) ? null : reader.GetString(6),
                IncludedColumns:  reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return result;
    }

    /// <summary>
    /// Loads CHECK constraints from pg_constraint (contype = 'c').
    /// For single-column constraints (<c>conkey</c> has exactly one entry) the column name is
    /// resolved; for multi-column / table-level constraints <c>ColumnName</c> is <c>null</c>.
    /// </summary>
    private static async Task<List<SchemaCheckConstraintInfo>> LoadCheckConstraints(
        NpgsqlConnection   connection,
        FrozenSet<string>? allowedSchemas,
        CancellationToken  ct)
    {
        const string sql = @"
            SELECT
                n.nspname                                     AS schema_name,
                t.relname                                     AS table_name,
                c.conname                                     AS constraint_name,
                pg_get_expr(c.conbin, c.conrelid)            AS expression,
                CASE
                    WHEN c.conkey IS NOT NULL AND array_length(c.conkey, 1) = 1
                    THEN (SELECT a.attname
                          FROM   pg_attribute a
                          WHERE  a.attrelid = c.conrelid AND a.attnum = c.conkey[1])
                    ELSE NULL
                END                                           AS column_name
            FROM  pg_constraint  c
            JOIN  pg_class        t ON t.oid = c.conrelid
            JOIN  pg_namespace    n ON n.oid = t.relnamespace
            WHERE c.contype = 'c'
              AND t.relkind = 'r'
              AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
            ORDER BY n.nspname, t.relname, c.conname";

        using var command = new NpgsqlCommand(sql, connection);
        command.CommandTimeout = 30;

        var result = new List<SchemaCheckConstraintInfo>();
        using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var schema = reader.GetString(0);
            if (allowedSchemas != null && !allowedSchemas.Contains(schema))
                continue;

            result.Add(new SchemaCheckConstraintInfo(
                Schema:         schema,
                TableName:      reader.GetString(1),
                ConstraintName: reader.GetString(2),
                Expression:     reader.IsDBNull(3) ? "" : reader.GetString(3),
                ColumnName:     reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return result;
    }

    /// <summary>
    /// Loads UNIQUE constraints from pg_constraint (contype = 'u').
    /// The backing index for each unique constraint is excluded from <see cref="LoadIndexes"/>,
    /// so there is no double-counting.
    /// </summary>
    private static async Task<List<SchemaUniqueConstraintInfo>> LoadUniqueConstraints(
        NpgsqlConnection   connection,
        FrozenSet<string>? allowedSchemas,
        CancellationToken  ct)
    {
        const string sql = @"
            SELECT
                n.nspname    AS schema_name,
                t.relname    AS table_name,
                c.conname    AS constraint_name,
                (
                    SELECT string_agg(a.attname, ',' ORDER BY k.ord)
                    FROM   unnest(c.conkey) WITH ORDINALITY AS k(key, ord)
                    JOIN   pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.key
                )            AS columns
            FROM  pg_constraint  c
            JOIN  pg_class        t ON t.oid = c.conrelid
            JOIN  pg_namespace    n ON n.oid = t.relnamespace
            WHERE c.contype = 'u'
              AND t.relkind = 'r'
              AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
            ORDER BY n.nspname, t.relname, c.conname";

        using var command = new NpgsqlCommand(sql, connection);
        command.CommandTimeout = 30;

        var result = new List<SchemaUniqueConstraintInfo>();
        using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var schema = reader.GetString(0);
            if (allowedSchemas != null && !allowedSchemas.Contains(schema))
                continue;

            result.Add(new SchemaUniqueConstraintInfo(
                Schema:         schema,
                TableName:      reader.GetString(1),
                ConstraintName: reader.GetString(2),
                Columns:        reader.IsDBNull(3) ? "" : reader.GetString(3)));
        }

        return result;
    }

    private static string BuildTypeString(
        string dataType,
        string udtName,
        int? charMaxLength,
        int? numericPrecision,
        int? numericScale)
    {
        return dataType.ToLowerInvariant() switch
        {
            "character varying" => charMaxLength.HasValue ? $"varchar({charMaxLength})" : "varchar",
            "character"         => charMaxLength.HasValue ? $"char({charMaxLength})"    : "char",
            "numeric" or "decimal" => (numericPrecision.HasValue, numericScale.HasValue) switch
            {
                (true, true)  => $"{dataType}({numericPrecision},{numericScale})",
                (true, false) => $"{dataType}({numericPrecision})",
                _             => dataType
            },
            // USER-DEFINED covers jsonb, json, custom enum/composite types; ARRAY covers array types.
            "user-defined" or "array" => udtName,
            _                         => dataType
        };
    }
}
