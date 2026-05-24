using System.Text.RegularExpressions;

namespace Manifesta.Core;

/// <summary>
/// Determines which tables qualify as reference tables based on explicit config and heuristics.
/// </summary>
public static class ReferenceTableDetector
{
    /// <summary>
    /// Returns the set of fully qualified table names (e.g. <c>dbo.CallMode</c>) that
    /// qualify as reference tables under the given config.
    /// </summary>
    /// <param name="tableNames">All candidate fully qualified table names.</param>
    /// <param name="rowCounts">Row count per fully qualified table name (case-insensitive keys).</param>
    /// <param name="config">Reference table config from manifesta.config.json.</param>
    public static IReadOnlySet<string> Detect(
        IEnumerable<string> tableNames,
        IReadOnlyDictionary<string, int> rowCounts,
        ReferenceTableConfig config)
    {
        var result = TableNames.NewSet();

        // Explicit tables always qualify, regardless of row count or patterns.
        foreach (var name in config.ExplicitTables)
            result.Add(name);

        if (!config.Heuristics.Enabled)
            return result;

        foreach (var name in tableNames)
        {
            if (result.Contains(name)) continue;

            // Row count gate: skip tables that exceed the configured limit.
            if (rowCounts.TryGetValue(name, out var count) && count > config.Heuristics.MaxRows)
                continue;

            if (config.Heuristics.Patterns.Any(p => MatchesPattern(name, p)))
                result.Add(name);
        }

        return result;
    }

    /// <summary>
    /// Returns true if <paramref name="tableName"/> matches the glob <paramref name="pattern"/>.
    /// <c>*</c> matches any sequence of characters including dots (so <c>*Mode</c> matches
    /// <c>dbo.CallMode</c>). Use <c>dbo.*Mode</c> to restrict to a specific schema.
    /// Matching is case-insensitive.
    /// </summary>
    public static bool MatchesPattern(string tableName, string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$";
        return Regex.IsMatch(tableName, regexPattern, RegexOptions.IgnoreCase);
    }
}
