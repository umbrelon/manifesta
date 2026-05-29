using System.Text.Json;
using Manifesta.Core.IR;
using Manifesta.Core.Merge;

namespace Manifesta.Core.Drift;

/// <summary>
/// Pure comparison of two <see cref="TableDefinition"/> records.  No I/O — returns an
/// immutable <see cref="DriftResult"/> describing how the live DB diverges from the repo.
/// </summary>
/// <remarks>
/// Drift rules
/// ───────────
/// Detected as drift (❌):
///   Column absent from source, column type/nullability/default changed,
///   primary key changed, physical FK added or removed, physical FK cascadeDelete changed.
///
/// Detected as warning only (⚠):
///   Column present in live DB but absent from repo (ExtraDbColumns).
///   Never treated as drift — run db merge to add them.
///
/// Ignored entirely (repo-sovereign):
///   Logical/Virtual FKs, descriptions, sets, sections, labelField (table-level).
/// </remarks>
public sealed class TableDiffer
{
    public DriftResult Diff(TableDefinition repo, TableDefinition live, string repoFilePath, DbProvider provider = DbProvider.SqlServer)
    {
        var (fieldChanges, extraDbColumns) = DiffFields(repo, live, provider);
        var pkChange       = DiffPrimaryKey(repo, live);
        var fkChanges      = DiffForeignKeys(repo, live);
        var dataChanges    = DiffData(repo, live);
        var idxChanges     = DiffIndexes(repo, live);
        var checkChanges   = DiffCheckConstraints(repo, live);
        var uniqueChanges  = DiffUniqueConstraints(repo, live);

        return new DriftResult
        {
            TableName               = repo.Name,
            RepoFilePath            = repoFilePath,
            RepoTable               = repo,
            LiveTable               = live,
            FieldChanges            = fieldChanges.AsReadOnly(),
            FkChanges               = fkChanges.AsReadOnly(),
            PrimaryKeyChange        = pkChange,
            ExtraDbColumns          = extraDbColumns.AsReadOnly(),
            DataChanges             = dataChanges.AsReadOnly(),
            IndexChanges            = idxChanges.AsReadOnly(),
            CheckConstraintChanges  = checkChanges.AsReadOnly(),
            UniqueConstraintChanges = uniqueChanges.AsReadOnly(),
        };
    }

    // ── Private diff steps ────────────────────────────────────────────────────

    private static (List<FieldChange> changes, List<string> extraDbColumns)
        DiffFields(TableDefinition repo, TableDefinition live, DbProvider provider)
    {
        var repoByName = repo.Fields.ToDictionary(f => f.Name, FieldComparison.NameComparer);
        var liveByName = live.Fields.ToDictionary(f => f.Name, FieldComparison.NameComparer);
        var changes        = new List<FieldChange>();
        var extraDbColumns = new List<string>();

        // Repo columns: compare against live.
        foreach (var repoField in repo.Fields)
        {
            if (liveByName.TryGetValue(repoField.Name, out var liveField))
            {
                changes.AddRange(FieldComparison.DetectChanges(repoField, liveField, provider));
            }
            else
            {
                // Column in repo but absent from live DB — removed.
                changes.Add(new FieldChange
                {
                    Kind      = FieldChangeKind.Removed,
                    FieldName = repoField.Name,
                    OldValue  = repoField.Type,
                });
            }
        }

        // Live columns absent from repo — extra DB column (warning only).
        foreach (var liveField in live.Fields)
        {
            if (!repoByName.ContainsKey(liveField.Name))
                extraDbColumns.Add(liveField.Name);
        }

        return (changes, extraDbColumns);
    }

    private static PrimaryKeyChange? DiffPrimaryKey(TableDefinition repo, TableDefinition live)
    {
        if (repo.PrimaryKey.SequenceEqual(live.PrimaryKey, FieldComparison.NameComparer))
            return null;

        return new PrimaryKeyChange { Before = repo.PrimaryKey, After = live.PrimaryKey };
    }

    private static List<FkChange> DiffForeignKeys(TableDefinition repo, TableDefinition live)
    {
        // Only physical FKs are DB-authoritative; Logical/Virtual are ignored.
        var repoPhysical = repo.ForeignKeys
            .Where(fk => fk.Kind == ForeignKeyKind.Physical)
            .ToDictionary(fk => FieldComparison.FkKey(fk));
        var liveByKey = live.ForeignKeys.ToDictionary(fk => FieldComparison.FkKey(fk));
        var changes   = new List<FkChange>();

        // Physical FKs in repo: compare against live.
        foreach (var repoFk in repoPhysical.Values)
        {
            if (liveByKey.TryGetValue(FieldComparison.FkKey(repoFk), out var liveFk))
            {
                if (repoFk.CascadeDelete != liveFk.CascadeDelete)
                    changes.Add(new FkChange
                    {
                        Kind        = FkChangeKind.CascadeDeleteChanged,
                        SourceField = repoFk.SourceField,
                        TargetTable = repoFk.TargetTable,
                        TargetField = repoFk.TargetField,
                        OldValue    = repoFk.CascadeDelete.ToString().ToLowerInvariant(),
                        NewValue    = liveFk.CascadeDelete.ToString().ToLowerInvariant(),
                    });
            }
            else
            {
                // Physical FK removed from live DB.
                changes.Add(new FkChange
                {
                    Kind        = FkChangeKind.Removed,
                    SourceField = repoFk.SourceField,
                    TargetTable = repoFk.TargetTable,
                    TargetField = repoFk.TargetField,
                });
            }
        }

        // Physical FKs in live DB absent from repo.
        foreach (var liveFk in live.ForeignKeys)
        {
            if (!repoPhysical.ContainsKey(FieldComparison.FkKey(liveFk)))
                changes.Add(new FkChange
                {
                    Kind        = FkChangeKind.Added,
                    SourceField = liveFk.SourceField,
                    TargetTable = liveFk.TargetTable,
                    TargetField = liveFk.TargetField,
                });
        }

        return changes;
    }

    // ── Data diff ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Compares <paramref name="repo"/>.Data against <paramref name="live"/>.Data row by row,
    /// matching on primary key columns from <paramref name="repo"/>.
    /// Returns an empty list when either side has no data or when no PK is defined.
    /// </summary>
    private static List<DataRowChange> DiffData(TableDefinition repo, TableDefinition live)
    {
        if (repo.Data.Count == 0 && live.Data.Count == 0)
            return [];

        var pkColumns = repo.PrimaryKey;
        if (pkColumns.Count == 0)
            return [];

        var repoByPk = repo.Data.ToDictionary(row => PkKey(row, pkColumns));
        var liveByPk = live.Data.ToDictionary(row => PkKey(row, pkColumns));
        var changes  = new List<DataRowChange>();

        foreach (var (pk, repoRow) in repoByPk)
        {
            if (!liveByPk.TryGetValue(pk, out var liveRow))
            {
                changes.Add(new DataRowChange
                {
                    Kind     = DataChangeKind.Removed,
                    PkValues = ExtractPkValues(repoRow, pkColumns),
                    RepoRow  = repoRow,
                });
            }
            else if (!RowsEqual(repoRow, liveRow))
            {
                changes.Add(new DataRowChange
                {
                    Kind          = DataChangeKind.Modified,
                    PkValues      = ExtractPkValues(repoRow, pkColumns),
                    RepoRow       = repoRow,
                    LiveRow       = liveRow,
                    ChangedFields = FindChangedFields(repoRow, liveRow).AsReadOnly(),
                });
            }
        }

        foreach (var (pk, liveRow) in liveByPk)
        {
            if (!repoByPk.ContainsKey(pk))
                changes.Add(new DataRowChange
                {
                    Kind     = DataChangeKind.Added,
                    PkValues = ExtractPkValues(liveRow, pkColumns),
                    LiveRow  = liveRow,
                });
        }

        return changes;
    }

    private static string PkKey(IReadOnlyDictionary<string, JsonElement> row, IReadOnlyList<string> pkColumns) =>
        string.Join("|", pkColumns.Select(col =>
            row.TryGetValue(col, out var v) ? v.GetRawText() : "null"));

    private static IReadOnlyDictionary<string, JsonElement> ExtractPkValues(
        IReadOnlyDictionary<string, JsonElement> row, IReadOnlyList<string> pkColumns)
    {
        var d = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var col in pkColumns)
            if (row.TryGetValue(col, out var v))
                d[col] = v;
        return d;
    }

    private static bool RowsEqual(
        IReadOnlyDictionary<string, JsonElement> a,
        IReadOnlyDictionary<string, JsonElement> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, av) in a)
        {
            if (!b.TryGetValue(k, out var bv) || av.GetRawText() != bv.GetRawText())
                return false;
        }
        return true;
    }

    // ── Index diff ────────────────────────────────────────────────────────────

    private static List<IndexChange> DiffIndexes(TableDefinition repo, TableDefinition live)
    {
        var repoByName = repo.Indexes.ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);
        var liveByName = live.Indexes.ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);
        var changes    = new List<IndexChange>();

        foreach (var repoIdx in repo.Indexes)
        {
            if (!liveByName.TryGetValue(repoIdx.Name, out var liveIdx))
            {
                changes.Add(new IndexChange
                {
                    Kind       = IndexChangeKind.Removed,
                    IndexName  = repoIdx.Name,
                    OldColumns = string.Join(", ", repoIdx.Columns),
                });
                continue;
            }

            var repoColKey  = string.Join(",", repoIdx.Columns.Select(c => c.ToLowerInvariant()));
            var liveColKey  = string.Join(",", liveIdx.Columns.Select(c => c.ToLowerInvariant()));

            if (repoColKey != liveColKey)
                changes.Add(new IndexChange
                {
                    Kind       = IndexChangeKind.ColumnsChanged,
                    IndexName  = repoIdx.Name,
                    OldColumns = string.Join(", ", repoIdx.Columns),
                    NewColumns = string.Join(", ", liveIdx.Columns),
                });
            else if (repoIdx.IsUnique != liveIdx.IsUnique)
                changes.Add(new IndexChange
                {
                    Kind        = IndexChangeKind.UniquenessChanged,
                    IndexName   = repoIdx.Name,
                    OldIsUnique = repoIdx.IsUnique,
                    NewIsUnique = liveIdx.IsUnique,
                });
        }

        foreach (var liveIdx in live.Indexes)
        {
            if (!repoByName.ContainsKey(liveIdx.Name))
                changes.Add(new IndexChange
                {
                    Kind       = IndexChangeKind.Added,
                    IndexName  = liveIdx.Name,
                    NewColumns = string.Join(", ", liveIdx.Columns),
                });
        }

        return changes;
    }

    private static List<string> FindChangedFields(
        IReadOnlyDictionary<string, JsonElement> repo,
        IReadOnlyDictionary<string, JsonElement> live)
    {
        var changed = new List<string>();
        foreach (var (k, rv) in repo)
        {
            if (!live.TryGetValue(k, out var lv) || rv.GetRawText() != lv.GetRawText())
                changed.Add(k);
        }
        foreach (var k in live.Keys)
            if (!repo.ContainsKey(k))
                changed.Add(k);
        return changed;
    }

    // ── Check constraint diff ─────────────────────────────────────────────────

    private static List<CheckConstraintChange> DiffCheckConstraints(
        TableDefinition repo, TableDefinition live)
    {
        var repoByName = repo.CheckConstraints.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var liveByName = live.CheckConstraints.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var changes    = new List<CheckConstraintChange>();

        foreach (var repoC in repo.CheckConstraints)
        {
            if (!liveByName.TryGetValue(repoC.Name, out var liveC))
            {
                changes.Add(new CheckConstraintChange
                {
                    Kind           = CheckConstraintChangeKind.Removed,
                    ConstraintName = repoC.Name,
                    OldExpression  = repoC.Expression,
                });
            }
            else if (!string.Equals(repoC.Expression, liveC.Expression, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new CheckConstraintChange
                {
                    Kind           = CheckConstraintChangeKind.ExpressionChanged,
                    ConstraintName = repoC.Name,
                    OldExpression  = repoC.Expression,
                    NewExpression  = liveC.Expression,
                });
            }
        }

        foreach (var liveC in live.CheckConstraints)
        {
            if (!repoByName.ContainsKey(liveC.Name))
                changes.Add(new CheckConstraintChange
                {
                    Kind           = CheckConstraintChangeKind.Added,
                    ConstraintName = liveC.Name,
                    NewExpression  = liveC.Expression,
                });
        }

        return changes;
    }

    // ── Unique constraint diff ────────────────────────────────────────────────

    private static List<UniqueConstraintChange> DiffUniqueConstraints(
        TableDefinition repo, TableDefinition live)
    {
        var repoByName = repo.UniqueConstraints.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var liveByName = live.UniqueConstraints.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var changes    = new List<UniqueConstraintChange>();

        foreach (var repoC in repo.UniqueConstraints)
        {
            if (!liveByName.TryGetValue(repoC.Name, out var liveC))
            {
                changes.Add(new UniqueConstraintChange
                {
                    Kind           = UniqueConstraintChangeKind.Removed,
                    ConstraintName = repoC.Name,
                    OldColumns     = string.Join(", ", repoC.Columns),
                });
            }
            else
            {
                var repoColKey = string.Join(",", repoC.Columns.Select(c => c.ToLowerInvariant()));
                var liveColKey = string.Join(",", liveC.Columns.Select(c => c.ToLowerInvariant()));
                if (repoColKey != liveColKey)
                    changes.Add(new UniqueConstraintChange
                    {
                        Kind           = UniqueConstraintChangeKind.ColumnsChanged,
                        ConstraintName = repoC.Name,
                        OldColumns     = string.Join(", ", repoC.Columns),
                        NewColumns     = string.Join(", ", liveC.Columns),
                    });
            }
        }

        foreach (var liveC in live.UniqueConstraints)
        {
            if (!repoByName.ContainsKey(liveC.Name))
                changes.Add(new UniqueConstraintChange
                {
                    Kind           = UniqueConstraintChangeKind.Added,
                    ConstraintName = liveC.Name,
                    NewColumns     = string.Join(", ", liveC.Columns),
                });
        }

        return changes;
    }
}
