using System.Collections.Frozen;
using Manifesta.Core.IR;

namespace Manifesta.Core;

/// <summary>
/// Abstract base for all database introspectors.
/// Provides the three public <see cref="IDatabaseIntrospector"/> delegation methods,
/// the <see cref="ParseSchemaFilter"/> helper, and shared record types + combining
/// logic used by schema-aware providers (SQL Server, Postgres).
/// </summary>
public abstract class DatabaseIntrospectorBase : IDatabaseIntrospector
{
    protected readonly string ConnectionString;

    protected DatabaseIntrospectorBase(string connectionString)
        => ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));

    public Task<IReadOnlyList<TableDefinition>> IntrospectAsync(
        string? schemaFilter = null, CancellationToken cancellationToken = default)
        => IntrospectInternalAsync(null, schemaFilter, cancellationToken);

    public Task<IReadOnlyList<TableDefinition>> IntrospectTablesOnlyAsync(
        string? schemaFilter = null, CancellationToken cancellationToken = default)
        => IntrospectInternalAsync("BASE TABLE", schemaFilter, cancellationToken);

    public Task<IReadOnlyList<TableDefinition>> IntrospectViewsOnlyAsync(
        string? schemaFilter = null, CancellationToken cancellationToken = default)
        => IntrospectInternalAsync("VIEW", schemaFilter, cancellationToken);

    public abstract Task<IReadOnlyDictionary<string, int>> GetRowCountsAsync(
        string? schemaFilter = null, CancellationToken cancellationToken = default);

    protected abstract Task<IReadOnlyList<TableDefinition>> IntrospectInternalAsync(
        string? tableTypesOnly, string? schemaFilter, CancellationToken ct);

    protected static FrozenSet<string>? ParseSchemaFilter(string? schemaFilter) =>
        string.IsNullOrWhiteSpace(schemaFilter)
            ? null
            : schemaFilter.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToFrozenSet();

    // ── Shared types for schema-aware providers (SQL Server, Postgres) ────────

    protected sealed class SchemaTableInfo
    {
        public required string Schema    { get; init; }
        public required string TableName { get; init; }
        public required List<FieldDefinition> Fields { get; init; }
    }

    protected sealed record SchemaPrimaryKeyInfo(string Schema, string TableName, string ColumnName);

    protected sealed record SchemaForeignKeyInfo(
        string SourceSchema,
        string SourceTableName,
        string SourceColumnName,
        string TargetSchema,
        string TargetTableName,
        string TargetColumnName,
        bool   CascadeDelete);

    protected sealed record SchemaComputedColumnInfo(
        string Schema,
        string TableName,
        string ColumnName,
        string Definition,
        bool   IsPersisted);

    protected sealed record SchemaIndexInfo(
        string Schema,
        string TableName,
        string IndexName,
        string Columns,
        bool   IsUnique,
        bool   IsClustered,
        bool   IsFiltered,
        string? FilterExpression,
        string? IncludedColumns);

    protected sealed record SchemaCheckConstraintInfo(
        string Schema,
        string TableName,
        string ConstraintName,
        string Expression,
        string? ColumnName);

    protected sealed record SchemaUniqueConstraintInfo(
        string Schema,
        string TableName,
        string ConstraintName,
        string Columns);

    protected static IReadOnlyList<TableDefinition> CombineSchemaAware(
        List<SchemaTableInfo>               tables,
        List<SchemaPrimaryKeyInfo>          primaryKeys,
        List<SchemaForeignKeyInfo>          foreignKeys,
        List<SchemaComputedColumnInfo>      computedColumns,
        List<SchemaIndexInfo>?              indexes           = null,
        List<SchemaCheckConstraintInfo>?    checkConstraints  = null,
        List<SchemaUniqueConstraintInfo>?   uniqueConstraints = null)
    {
        var pksByTable = primaryKeys
            .GroupBy(pk => (pk.Schema, pk.TableName))
            .ToDictionary(g => g.Key, g => g.Select(pk => pk.ColumnName).ToList());

        var fksByTable = foreignKeys
            .GroupBy(fk => (fk.SourceSchema, fk.SourceTableName))
            .ToDictionary(g => g.Key, g => g.ToList());

        var computedByKey = computedColumns
            .ToDictionary(
                cc => (cc.Schema, cc.TableName, cc.ColumnName),
                cc => cc,
                SchemaComputedKeyComparer.Instance);

        var indexesByTable = (indexes ?? [])
            .GroupBy(i => (i.Schema, i.TableName))
            .ToDictionary(g => g.Key, g => g.ToList());

        var checksByTable = (checkConstraints ?? [])
            .GroupBy(c => (c.Schema, c.TableName))
            .ToDictionary(g => g.Key, g => g.ToList());

        var uniquesByTable = (uniqueConstraints ?? [])
            .GroupBy(u => (u.Schema, u.TableName))
            .ToDictionary(g => g.Key, g => g.ToList());

        return tables
            .Select(t =>
            {
                var key      = (t.Schema, t.TableName);
                var tablePks = pksByTable.TryGetValue(key, out var pks) ? pks : [];

                var tableFks = fksByTable.TryGetValue(key, out var fks)
                    ? fks.Select(fk => new ForeignKey
                    {
                        SourceField   = fk.SourceColumnName,
                        TargetTable   = $"{fk.TargetSchema}.{fk.TargetTableName}",
                        TargetField   = fk.TargetColumnName,
                        CascadeDelete = fk.CascadeDelete
                    }).ToList()
                    : [];

                var fields = t.Fields.Select(f =>
                {
                    var ccKey = (t.Schema, t.TableName, f.Name);
                    if (computedByKey.TryGetValue(ccKey, out var cc))
                        return f with
                        {
                            IsComputed         = true,
                            ComputedExpression = cc.Definition,
                            IsPersisted        = cc.IsPersisted,
                        };
                    return f;
                }).ToList();

                var tableIndexes = indexesByTable.TryGetValue(key, out var idxList)
                    ? idxList.Select(i => new IndexDefinition
                    {
                        Name             = i.IndexName,
                        Columns          = i.Columns.Split(',', StringSplitOptions.TrimEntries).ToList(),
                        IsUnique         = i.IsUnique,
                        IsClustered      = i.IsClustered,
                        IsFiltered       = i.IsFiltered,
                        FilterExpression = i.FilterExpression,
                        IncludedColumns  = i.IncludedColumns,
                    }).ToList()
                    : (List<IndexDefinition>)[];

                var tableChecks = checksByTable.TryGetValue(key, out var ccList)
                    ? ccList.Select(c => new CheckConstraint
                    {
                        Name       = c.ConstraintName,
                        Expression = c.Expression,
                        Column     = c.ColumnName,
                    }).ToList()
                    : (List<CheckConstraint>)[];

                var tableUniques = uniquesByTable.TryGetValue(key, out var ucList)
                    ? ucList.Select(u => new UniqueConstraint
                    {
                        Name    = u.ConstraintName,
                        Columns = u.Columns.Split(',', StringSplitOptions.TrimEntries).ToList(),
                    }).ToList()
                    : (List<UniqueConstraint>)[];

                return new TableDefinition
                {
                    Name              = $"{t.Schema}.{t.TableName}",
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

    private sealed class SchemaComputedKeyComparer
        : IEqualityComparer<(string Schema, string TableName, string ColumnName)>
    {
        public static readonly SchemaComputedKeyComparer Instance = new();

        public bool Equals(
            (string Schema, string TableName, string ColumnName) x,
            (string Schema, string TableName, string ColumnName) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.Schema,     y.Schema)
            && StringComparer.OrdinalIgnoreCase.Equals(x.TableName,  y.TableName)
            && StringComparer.OrdinalIgnoreCase.Equals(x.ColumnName, y.ColumnName);

        public int GetHashCode((string Schema, string TableName, string ColumnName) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Schema),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.TableName),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ColumnName));
    }
}
