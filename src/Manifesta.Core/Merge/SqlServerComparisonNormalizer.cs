namespace Manifesta.Core.Merge;

/// <summary>
/// SQL Server-specific type normalisation for drift comparison.
/// Receives a lower-cased, precision-space-stripped type string.
/// </summary>
internal static class SqlServerComparisonNormalizer
{
    internal static string NormalizeType(string s) => s switch
    {
        "sysname"   => "nvarchar(128)",
        "integer"   => "int",
        "datetime2" => "datetime2(7)",
        _           => s,
    };
}
