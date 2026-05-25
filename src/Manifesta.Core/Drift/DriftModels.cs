using System.Text.Json;
using Manifesta.Core.IR;
using Manifesta.Core.Merge;

namespace Manifesta.Core.Drift;

// ─── Data row changes ─────────────────────────────────────────────────────────

public enum DataChangeKind { Added, Removed, Modified }

/// <summary>
/// Describes a single row-level difference between repo <c>data</c> and the live database.
/// </summary>
public sealed record DataRowChange
{
    public required DataChangeKind Kind { get; init; }

    /// <summary>PK column values identifying this row (e.g. {"Id": 42}).</summary>
    public required IReadOnlyDictionary<string, JsonElement> PkValues { get; init; }

    /// <summary>Full repo row; null for <see cref="DataChangeKind.Added"/>.</summary>
    public IReadOnlyDictionary<string, JsonElement>? RepoRow { get; init; }

    /// <summary>Full live row; null for <see cref="DataChangeKind.Removed"/>.</summary>
    public IReadOnlyDictionary<string, JsonElement>? LiveRow { get; init; }

    /// <summary>Column names whose values differ (for <see cref="DataChangeKind.Modified"/> only).</summary>
    public IReadOnlyList<string> ChangedFields { get; init; } = [];
}

// ─── Index changes ────────────────────────────────────────────────────────────

public enum IndexChangeKind { Added, Removed, ColumnsChanged, UniquenessChanged }

public sealed record IndexChange
{
    public required IndexChangeKind Kind      { get; init; }
    public required string          IndexName { get; init; }

    /// <summary>Comma-joined columns before the change (null for Added).</summary>
    public string? OldColumns { get; init; }

    /// <summary>Comma-joined columns after the change (null for Removed).</summary>
    public string? NewColumns { get; init; }

    public bool? OldIsUnique { get; init; }
    public bool? NewIsUnique { get; init; }
}

// ─── Per-table drift result ───────────────────────────────────────────────────

/// <summary>
/// The output of <c>TableDiffer.Diff()</c> for a single table.
/// Reuses <see cref="FieldChange"/>, <see cref="FkChange"/>, and
/// <see cref="PrimaryKeyChange"/> from the merge models — they describe the same concepts.
/// </summary>
public sealed record DriftResult
{
    public required string          TableName    { get; init; }
    public required string          RepoFilePath { get; init; }
    public required TableDefinition RepoTable    { get; init; }
    public required TableDefinition LiveTable    { get; init; }

    /// <summary>Structural changes (type, nullability, default, removed, PK).</summary>
    public IReadOnlyList<FieldChange> FieldChanges    { get; init; } = [];
    public IReadOnlyList<FkChange>    FkChanges       { get; init; } = [];
    public PrimaryKeyChange?          PrimaryKeyChange { get; init; }

    /// <summary>Columns present in the live DB but absent from the repo definition (warnings, not drift).</summary>
    public IReadOnlyList<string> ExtraDbColumns { get; init; } = [];

    /// <summary>Row-level differences between repo <c>data</c> and the live database (populated when <c>--include-reference-data-drift</c> is set).</summary>
    public IReadOnlyList<DataRowChange> DataChanges { get; init; } = [];

    /// <summary>Index-level changes detected by comparing repo and live index definitions.</summary>
    public IReadOnlyList<IndexChange> IndexChanges { get; init; } = [];

    public bool HasDrift    => FieldChanges.Count > 0 || FkChanges.Count > 0 || PrimaryKeyChange is not null || DataChanges.Count > 0 || IndexChanges.Count > 0;
    public bool HasWarnings => ExtraDbColumns.Count > 0;
}

// ─── Drift session ────────────────────────────────────────────────────────────

/// <summary>
/// Full result of a <c>db drift</c> run — passed to <c>DriftReportGenerator</c>.
/// </summary>
public sealed record DriftSession
{
    public required string         Source          { get; init; }
    public required string         RootPath        { get; init; }
    public required DateTimeOffset Timestamp       { get; init; }
    public required int            TotalLiveTables { get; init; }
    public required bool           IncludeSchema            { get; init; }
    public bool                    IncludeReferenceDataDrift { get; init; }

    /// <summary>Tables where the repo definition diverges from the live DB.</summary>
    public IReadOnlyList<DriftResult> DriftedTables  { get; init; } = [];

    /// <summary>Tables that are fully in sync.</summary>
    public IReadOnlyList<DriftResult> CleanTables    { get; init; } = [];

    /// <summary>Tables present in the live DB but absent from the repo (warnings).</summary>
    public IReadOnlyList<string> ExtraDbTables   { get; init; } = [];

    /// <summary>Repo file paths for tables absent from the live DB (drift).</summary>
    public IReadOnlyList<string> MissingDbTables { get; init; } = [];

    public bool HasDrift =>
        DriftedTables.Count > 0 ||
        MissingDbTables.Count > 0;

    public bool HasWarnings =>
        ExtraDbTables.Count > 0 ||
        DriftedTables.Any(d => d.HasWarnings);
}
