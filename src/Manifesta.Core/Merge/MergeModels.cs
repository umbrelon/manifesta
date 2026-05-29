using Manifesta.Core.IR;

namespace Manifesta.Core.Merge;

// ─── Change records ───────────────────────────────────────────────────────────

public enum FieldChangeKind
{
    Added,
    Removed,
    TypeChanged,
    NullabilityChanged,
    DefaultChanged,
    ComputedExpressionChanged,
    IsPersistedChanged,
}

public enum FkChangeKind
{
    Added,
    Removed,
    CascadeDeleteChanged,
}

/// <summary>
/// Describes a single field-level change produced by a merge.
/// <see cref="OldValue"/> and <see cref="NewValue"/> carry the before/after value
/// (type string for TypeChanged; "true"/"false" for NullabilityChanged; null for Added/Removed).
/// </summary>
public sealed record FieldChange
{
    public required FieldChangeKind Kind      { get; init; }
    public required string          FieldName { get; init; }
    public string?                  OldValue  { get; init; }
    public string?                  NewValue  { get; init; }
}

/// <summary>
/// Describes a single FK-level change produced by a merge.
/// <see cref="OldValue"/> / <see cref="NewValue"/> carry before/after for CascadeDeleteChanged.
/// </summary>
public sealed record FkChange
{
    public required FkChangeKind Kind        { get; init; }
    public required string       SourceField { get; init; }
    public required string       TargetTable { get; init; }
    public required string       TargetField { get; init; }
    public string?               OldValue    { get; init; }
    public string?               NewValue    { get; init; }
}

public sealed record PrimaryKeyChange
{
    public required IReadOnlyList<string> Before { get; init; }
    public required IReadOnlyList<string> After  { get; init; }
}

// ─── Per-table merge result ───────────────────────────────────────────────────

/// <summary>
/// The output of <c>TableMerger.Merge()</c> for a single table.
/// </summary>
public sealed record MergeResult
{
    public required TableDefinition Merged       { get; init; }
    public required string          RepoFilePath { get; init; }

    public IReadOnlyList<FieldChange> FieldChanges { get; init; } = [];
    public IReadOnlyList<FkChange>    FkChanges    { get; init; } = [];
    public PrimaryKeyChange?          PrimaryKeyChange { get; init; }

    /// <summary>Column names that exist in the repo but not in the live DB (orphans).</summary>
    public IReadOnlyList<string>   OrphanColumnNames        { get; init; } = [];

    /// <summary>
    /// Column names referenced in <c>data</c> rows that no longer exist in the merged schema.
    /// Populated when a column present in <c>data</c> keys was removed during merge.
    /// </summary>
    public IReadOnlyList<string>   OrphanedDataKeys         { get; init; } = [];

    /// <summary>
    /// Always empty as of Phase 5: Physical FK removals are now auto-applied and recorded
    /// in <see cref="FkChanges"/> as <c>Removed</c>. Retained for downstream compatibility.
    /// </summary>
    public IReadOnlyList<FkChange> NonSoftFkRemovedWarnings { get; init; } = [];

    /// <summary>True when reference table data was replaced with freshly captured rows.</summary>
    public bool DataRefreshed { get; init; }

    /// <summary>True when the set of indexes (names or columns) changed between repo and live.</summary>
    public bool IndexesChanged { get; init; }

    /// <summary>True when at least one structural change was detected, or reference data was refreshed.</summary>
    public bool HasChanges =>
        FieldChanges.Count > 0 ||
        FkChanges.Count    > 0 ||
        PrimaryKeyChange   is not null ||
        DataRefreshed      ||
        IndexesChanged;
}

// ─── Session-level aggregates ─────────────────────────────────────────────────

public sealed record OrphanColumn
{
    public required string TableName { get; init; }
    public required string FieldName { get; init; }
    public required string FilePath  { get; init; }
}

public sealed record OrphanedDataKey
{
    public required string TableName  { get; init; }
    public required string ColumnName { get; init; }
    public required string FilePath   { get; init; }
}

public sealed record NewTableResult
{
    public required TableDefinition Table    { get; init; }
    public required string          FilePath { get; init; }
}

// ─── Merge session ────────────────────────────────────────────────────────────

/// <summary>
/// Full result of a <c>db merge</c> run — passed to <c>MergeReportGenerator</c>.
/// </summary>
public sealed record MergeSession
{
    /// <summary>Human-readable description of the source ("--connection ..." or "--input-dir ...").</summary>
    public required string         Source          { get; init; }
    public required string         RootPath        { get; init; }
    public required bool           IsDryRun        { get; init; }
    public required DateTimeOffset Timestamp       { get; init; }
    public required int            TotalLiveTables { get; init; }

    public IReadOnlyList<MergeResult>    Modified          { get; init; } = [];
    public IReadOnlyList<MergeResult>    Unchanged         { get; init; } = [];
    public IReadOnlyList<NewTableResult> NewTables         { get; init; } = [];
    public IReadOnlyList<string>         OrphanTablePaths  { get; init; } = [];
    public IReadOnlyList<string>         DeletedTablePaths { get; init; } = [];

    public IReadOnlyList<OrphanColumn>    OrphanColumns    { get; init; } = [];
    public IReadOnlyList<OrphanedDataKey> OrphanedDataKeys { get; init; } = [];

    public int RefreshedDataCount => Modified.Count(r => r.DataRefreshed);

    public bool HasWarnings =>
        OrphanTablePaths.Count  > 0 ||
        OrphanColumns.Count     > 0 ||
        OrphanedDataKeys.Count  > 0;
}
