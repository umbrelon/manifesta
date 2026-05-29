using System.Text.RegularExpressions;

namespace Manifesta.Core.Merge;

/// <summary>
/// MySQL-specific type normalisation for drift comparison.
/// Receives a lower-cased, precision-space-stripped type string.
/// </summary>
internal static class MySqlComparisonNormalizer
{
    // Matches integer types with a display width, e.g. tinyint(1), int(11), bigint(20).
    // MySQL 8.0 deprecated display widths; the live introspector returns the bare type name.
    private static readonly Regex s_intDisplayWidth = new(
        @"^(tinyint|smallint|mediumint|int|integer|bigint)\(\d+\)$",
        RegexOptions.Compiled);

    internal static string NormalizeType(string s)
    {
        // Strip display widths: tinyint(1) → tinyint, int(11) → int, etc.
        var m = s_intDisplayWidth.Match(s);
        if (m.Success)
        {
            var baseType = m.Groups[1].Value;
            return baseType == "integer" ? "int" : baseType;
        }

        // integer → int (alias, no display width)
        if (s == "integer") return "int";

        return s;
    }
}
