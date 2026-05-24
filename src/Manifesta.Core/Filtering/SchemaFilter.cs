namespace Manifesta.Core.Filtering;

/// <summary>
/// Filters tables by database schema prefix (e.g. <c>"dbo"</c> matches <c>"dbo.Customer"</c>).
/// Table names are expected in <c>schema.TableName</c> format.
/// When inactive (no schemas specified), every table is matched.
/// </summary>
public sealed class SchemaFilter
{
    private readonly HashSet<string> _schemas;

    public SchemaFilter(string? commaSeparatedSchemas)
    {
        if (string.IsNullOrWhiteSpace(commaSeparatedSchemas))
        {
            _schemas = [];
            IsActive = false;
        }
        else
        {
            _schemas = commaSeparatedSchemas
                .Split(',')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToHashSet(TableNames.Comparer);

            IsActive = _schemas.Count > 0;
        }
    }

    /// <summary>True when at least one schema was specified.</summary>
    public bool IsActive { get; }

    /// <summary>The set of configured schema names (case-insensitive).</summary>
    public IReadOnlySet<string> Schemas => _schemas;

    /// <summary>
    /// Returns <c>true</c> when the filter is inactive, or when the table's schema
    /// prefix (the part before the first <c>.</c>) is in the configured set.
    /// Tables without a schema prefix never match an active filter.
    /// </summary>
    public bool Matches(string tableName)
    {
        if (!IsActive)
            return true;

        var dotIndex = tableName.IndexOf('.');
        if (dotIndex <= 0)
            return false;

        var schema = tableName[..dotIndex];
        return _schemas.Contains(schema);
    }
}
