namespace Manifesta.Core;

/// <summary>
/// Convenience factory and comparer for database identifier collections
/// (table names, schema names, column names).
/// All comparisons are <see cref="StringComparer.OrdinalIgnoreCase"/> because
/// database identifiers are case-insensitive by convention across all supported providers.
/// </summary>
public static class TableNames
{
    /// <summary>Case-insensitive ordinal comparer for database identifiers.</summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>Creates an empty case-insensitive set of identifier strings.</summary>
    public static HashSet<string> NewSet() => new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a case-insensitive set pre-populated from <paramref name="items"/>.</summary>
    public static HashSet<string> NewSet(IEnumerable<string> items) => new(items, StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates an empty case-insensitive dictionary keyed on identifier strings.</summary>
    public static Dictionary<string, TValue> NewDictionary<TValue>() => new(StringComparer.OrdinalIgnoreCase);
}
