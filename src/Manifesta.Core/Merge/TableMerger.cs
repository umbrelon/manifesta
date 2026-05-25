using Manifesta.Core.IR;

namespace Manifesta.Core.Merge;

/// <summary>
/// Pure merge logic for a single table.  No I/O — takes two <see cref="TableDefinition"/>
/// records and returns an immutable <see cref="MergeResult"/>.
/// </summary>
/// <remarks>
/// Ownership rules
/// ───────────────
/// DB-authoritative (always updated from live):
///   fields[].name, fields[].type, fields[].nullable, fields[].default, fields[].isPrimaryKey,
///   fields[].isComputed, fields[].computedExpression, fields[].isPersisted,
///   primaryKey, foreignKeys[].sourceField / targetTable / targetField / cascadeDelete
///
/// Repo-sovereign (never overwritten):
///   fields[].description, fields[].isMatchColumn,
///   foreignKeys[].kind (Logical/Virtual),
///   description, labelField, databaseTypes, sets, sections
///
/// FK removal policy:
///   Physical FKs absent from live → auto-deleted; recorded in FkChanges as Removed.
///   Logical / Virtual FKs absent from live → silently preserved (repo-sovereign).
/// </remarks>
public sealed class TableMerger
{
    /// <summary>
    /// Merges <paramref name="live"/> structural data into <paramref name="repo"/>,
    /// preserving all repo metadata.
    /// </summary>
    /// <param name="repo">Existing table definition from the repository.</param>
    /// <param name="live">Table definition from the live database or export.</param>
    /// <param name="repoFilePath">Source file path for <paramref name="repo"/> (used in the result).</param>
    /// <param name="removeDeleted">
    /// When <c>true</c>, columns present in <paramref name="repo"/> but absent from
    /// <paramref name="live"/> are removed.  When <c>false</c> (default), they are
    /// preserved and reported as orphans.
    /// </param>
    public MergeResult Merge(
        TableDefinition repo,
        TableDefinition live,
        string          repoFilePath,
        bool            removeDeleted = false)
    {
        var (mergedFields, fieldChanges, orphanColumnNames) = MergeFields(repo, live, removeDeleted);
        var (mergedPk, pkChange, syncedFields)              = MergePrimaryKey(repo, live, mergedFields);
        var (mergedFks, fkChanges)                          = MergeForeignKeys(repo, live);

        var mergedFieldNames  = syncedFields.Select(f => f.Name).ToHashSet(FieldComparison.NameComparer);
        var orphanedDataKeys  = repo.Data
            .SelectMany(row => row.Keys)
            .Where(key => !mergedFieldNames.Contains(key))
            .Distinct(FieldComparison.NameComparer)
            .ToList();

        var merged = repo with
        {
            Fields            = syncedFields.AsReadOnly(),
            PrimaryKey        = mergedPk,
            ForeignKeys       = mergedFks.AsReadOnly(),
            // DB-authoritative structural metadata always replaced from live.
            Indexes           = live.Indexes,
            CheckConstraints  = live.CheckConstraints,
            UniqueConstraints = live.UniqueConstraints,
            // Description, DatabaseTypes, Sets, Sections, SourceFile all preserved via `with`.
        };

        return new MergeResult
        {
            Merged            = merged,
            RepoFilePath      = repoFilePath,
            FieldChanges      = fieldChanges.AsReadOnly(),
            FkChanges         = fkChanges.AsReadOnly(),
            PrimaryKeyChange  = pkChange,
            OrphanColumnNames = orphanColumnNames.AsReadOnly(),
            OrphanedDataKeys  = orphanedDataKeys.AsReadOnly(),
            // NonSoftFkRemovedWarnings always empty: Physical FK removals are now in FkChanges.
        };
    }

    // ── Private merge steps ───────────────────────────────────────────────────

    private static (List<FieldDefinition> merged, List<FieldChange> changes, List<string> orphans)
        MergeFields(TableDefinition repo, TableDefinition live, bool removeDeleted)
    {
        var repoByName = repo.Fields.ToDictionary(f => f.Name, f => f, FieldComparison.NameComparer);
        var liveByName = live.Fields.ToDictionary(f => f.Name, f => f, FieldComparison.NameComparer);
        var merged  = new List<FieldDefinition>();
        var changes = new List<FieldChange>();
        var orphans = new List<string>();

        // Walk repo fields in original order — update DB-authoritative properties, preserve metadata.
        foreach (var repoField in repo.Fields)
        {
            if (liveByName.TryGetValue(repoField.Name, out var liveField))
            {
                changes.AddRange(FieldComparison.DetectChanges(repoField, liveField));

                merged.Add(repoField with
                {
                    Type               = liveField.Type,
                    Nullable           = liveField.Nullable,
                    Default            = liveField.Default,
                    IsComputed         = liveField.IsComputed,
                    ComputedExpression = liveField.ComputedExpression,
                    IsPersisted        = liveField.IsPersisted,
                    // Description and IsMatchColumn preserved from repo
                });
            }
            else
            {
                // Column exists in repo but not in live — orphan.
                if (!removeDeleted)
                {
                    merged.Add(repoField);
                    orphans.Add(repoField.Name);
                }
                else
                {
                    changes.Add(new FieldChange
                    {
                        Kind      = FieldChangeKind.Removed,
                        FieldName = repoField.Name,
                        OldValue  = repoField.Type,
                    });
                    // Field is intentionally not added to merged — it is removed.
                }
            }
        }

        // Append new columns from live that don't exist in the repo.
        foreach (var liveField in live.Fields)
        {
            if (!repoByName.ContainsKey(liveField.Name))
            {
                merged.Add(liveField with { Description = "", IsMatchColumn = false });
                changes.Add(new FieldChange
                {
                    Kind      = FieldChangeKind.Added,
                    FieldName = liveField.Name,
                    NewValue  = liveField.Type,
                });
            }
        }

        return (merged, changes, orphans);
    }

    private static (IReadOnlyList<string> mergedPk, PrimaryKeyChange? change, List<FieldDefinition> syncedFields)
        MergePrimaryKey(TableDefinition repo, TableDefinition live, List<FieldDefinition> mergedFields)
    {
        var repoPk = repo.PrimaryKey;
        var livePk = live.PrimaryKey;

        PrimaryKeyChange? change = null;
        if (!repoPk.SequenceEqual(livePk, FieldComparison.NameComparer))
            change = new PrimaryKeyChange { Before = repoPk, After = livePk };

        var mergedPk = change is not null ? livePk : repoPk;
        var pkSet    = mergedPk.ToHashSet(FieldComparison.NameComparer);
        var synced   = mergedFields
            .Select(f => f with { IsPrimaryKey = pkSet.Contains(f.Name) })
            .ToList();

        return (mergedPk, change, synced);
    }

    private static (List<ForeignKey> merged, List<FkChange> changes)
        MergeForeignKeys(TableDefinition repo, TableDefinition live)
    {
        var repoByKey = repo.ForeignKeys.ToDictionary(fk => FieldComparison.FkKey(fk));
        var liveByKey = live.ForeignKeys.ToDictionary(fk => FieldComparison.FkKey(fk));
        var merged  = new List<ForeignKey>();
        var changes = new List<FkChange>();

        // Walk repo FKs: sync cascadeDelete from live, apply ownership rules for removals.
        foreach (var repoFk in repo.ForeignKeys)
        {
            if (liveByKey.TryGetValue(FieldComparison.FkKey(repoFk), out var liveFk))
            {
                // FK present in both — update cascadeDelete; all repo-sovereign fields preserved.
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

                merged.Add(repoFk with { CascadeDelete = liveFk.CascadeDelete });
            }
            else if (repoFk.Kind == ForeignKeyKind.Physical)
            {
                // Physical FK is DB-authoritative: absent from live → auto-delete.
                changes.Add(new FkChange
                {
                    Kind        = FkChangeKind.Removed,
                    SourceField = repoFk.SourceField,
                    TargetTable = repoFk.TargetTable,
                    TargetField = repoFk.TargetField,
                });
            }
            else
            {
                // Logical/Virtual FK is repo-sovereign: silently preserve even when absent from live.
                merged.Add(repoFk);
            }
        }

        // Append new FKs from live that don't exist in the repo.
        foreach (var liveFk in live.ForeignKeys)
        {
            if (!repoByKey.ContainsKey(FieldComparison.FkKey(liveFk)))
            {
                merged.Add(liveFk with { Kind = ForeignKeyKind.Physical });
                changes.Add(new FkChange
                {
                    Kind        = FkChangeKind.Added,
                    SourceField = liveFk.SourceField,
                    TargetTable = liveFk.TargetTable,
                    TargetField = liveFk.TargetField,
                });
            }
        }

        return (merged, changes);
    }
}
