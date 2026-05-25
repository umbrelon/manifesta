using Manifesta.Core.IR;

namespace Manifesta.Core.Merge;

/// <summary>
/// Shared field and FK comparison utilities used by <see cref="Drift.TableDiffer"/> and
/// <see cref="TableMerger"/>.
/// </summary>
internal static class FieldComparison
{
    internal static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Returns a <see cref="FieldChange"/> for every DB-authoritative property of
    /// <paramref name="repo"/> that differs from <paramref name="live"/>.
    /// Does not handle Added/Removed — callers own that logic.
    /// </summary>
    internal static IReadOnlyList<FieldChange> DetectChanges(FieldDefinition repo, FieldDefinition live)
    {
        var changes = new List<FieldChange>();

        if (!string.Equals(repo.Type, live.Type, StringComparison.OrdinalIgnoreCase))
            changes.Add(new FieldChange
            {
                Kind      = FieldChangeKind.TypeChanged,
                FieldName = repo.Name,
                OldValue  = repo.Type,
                NewValue  = live.Type,
            });

        if (repo.Nullable != live.Nullable)
            changes.Add(new FieldChange
            {
                Kind      = FieldChangeKind.NullabilityChanged,
                FieldName = repo.Name,
                OldValue  = repo.Nullable.ToString().ToLowerInvariant(),
                NewValue  = live.Nullable.ToString().ToLowerInvariant(),
            });

        if (!string.Equals(repo.Default, live.Default, StringComparison.Ordinal))
            changes.Add(new FieldChange
            {
                Kind      = FieldChangeKind.DefaultChanged,
                FieldName = repo.Name,
                OldValue  = repo.Default,
                NewValue  = live.Default,
            });

        if (repo.IsComputed != live.IsComputed
            || !string.Equals(repo.ComputedExpression, live.ComputedExpression, StringComparison.Ordinal))
            changes.Add(new FieldChange
            {
                Kind      = FieldChangeKind.ComputedExpressionChanged,
                FieldName = repo.Name,
                OldValue  = repo.ComputedExpression,
                NewValue  = live.ComputedExpression,
            });

        if (repo.IsPersisted != live.IsPersisted)
            changes.Add(new FieldChange
            {
                Kind      = FieldChangeKind.IsPersistedChanged,
                FieldName = repo.Name,
                OldValue  = repo.IsPersisted.ToString().ToLowerInvariant(),
                NewValue  = live.IsPersisted.ToString().ToLowerInvariant(),
            });

        return changes;
    }

    internal static string FkKey(ForeignKey fk) =>
        $"{fk.SourceField}|{fk.TargetTable}|{fk.TargetField}".ToUpperInvariant();
}
