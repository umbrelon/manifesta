using System.Text.RegularExpressions;
using Manifesta.Core.IR;

namespace Manifesta.Core.Merge;

/// <summary>
/// Shared field and FK comparison utilities used by <see cref="Drift.TableDiffer"/> and
/// <see cref="TableMerger"/>.
/// </summary>
internal static class FieldComparison
{
    internal static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    // Matches a single outer pair of parentheses wrapping the entire value, e.g. (-1) or (0).
    private static readonly Regex s_outerParensRx = new(
        @"^\(([^()]*)\)$",
        RegexOptions.Compiled);

    // Matches precision/scale groups with inner spaces, e.g. decimal(18, 0).
    private static readonly Regex s_precisionSpaceRx = new(
        @"\(\s*(\d+)\s*,\s*(\d+)\s*\)",
        RegexOptions.Compiled);

    /// <summary>
    /// Normalises a SQL type string for comparison so that purely formatting
    /// differences (e.g. <c>decimal(18, 0)</c> vs <c>decimal(18,0)</c>, or
    /// <c>dec</c> vs <c>decimal</c>) and provider-specific default-precision
    /// aliases (e.g. SQL Server <c>datetime2</c> vs <c>datetime2(7)</c>) do not
    /// produce false drift.
    /// </summary>
    internal static string? NormalizeTypeForComparison(string? type, DbProvider? provider = null)
    {
        if (type is null) return null;
        var s = type.Trim().ToLowerInvariant();

        // dec(...) → decimal(...) — T-SQL alias, safe for all providers
        if (s.StartsWith("dec(", StringComparison.Ordinal))
            s = "decimal" + s[3..];

        // Remove spaces inside precision/scale: decimal(18, 0) → decimal(18,0)
        s = s_precisionSpaceRx.Replace(s, "($1,$2)");

        // Dialect-specific normalisation
        s = provider switch
        {
            DbProvider.SqlServer => SqlServerComparisonNormalizer.NormalizeType(s),
            DbProvider.MySql     => MySqlComparisonNormalizer.NormalizeType(s),
            _                    => s,
        };

        return s;
    }

    /// <summary>
    /// Normalises a SQL default value string for comparison so that SQL Server's
    /// habit of wrapping defaults in an extra pair of parentheses (e.g. <c>(-1)</c>
    /// vs <c>-1</c>) does not produce false drift.
    /// </summary>
    internal static string? NormalizeDefaultForComparison(string? value)
    {
        if (value is null) return null;
        var m = s_outerParensRx.Match(value.Trim());
        return m.Success ? m.Groups[1].Value : value;
    }

    /// <summary>
    /// Returns a <see cref="FieldChange"/> for every DB-authoritative property of
    /// <paramref name="repo"/> that differs from <paramref name="live"/>.
    /// Does not handle Added/Removed — callers own that logic.
    /// </summary>
    internal static IReadOnlyList<FieldChange> DetectChanges(
        FieldDefinition repo,
        FieldDefinition live,
        DbProvider?     provider = null)
    {
        var changes = new List<FieldChange>();

        var repoType = NormalizeTypeForComparison(repo.Type, provider);
        var liveType = NormalizeTypeForComparison(live.Type, provider);
        if (!string.Equals(repoType, liveType, StringComparison.OrdinalIgnoreCase))
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

        var repoDefault = NormalizeDefaultForComparison(repo.Default);
        var liveDefault = NormalizeDefaultForComparison(live.Default);
        if (!string.Equals(repoDefault, liveDefault, StringComparison.OrdinalIgnoreCase))
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
