using Manifesta.Core.IR;

namespace Manifesta.Core.Filtering;

/// <summary>
/// Filters tables by section membership as defined in loaded
/// <see cref="SectionDefinition"/> objects.
/// When inactive (no sections specified), every table is matched.
/// </summary>
public sealed class SectionFilter
{
    private readonly HashSet<string> _requestedSections;

    /// <summary>
    /// Flat set of all table names that belong to any of the requested sections.
    /// Case-insensitive.
    /// </summary>
    private readonly HashSet<string> _allowedTables;

    /// <summary>
    /// Section names that were requested but not found in the loaded sections.
    /// Populated during construction so callers can log warnings.
    /// </summary>
    public IReadOnlySet<string> UnknownSections { get; }

    public SectionFilter(string? commaSeparatedSections, IReadOnlyList<SectionDefinition> loadedSections)
    {
        if (string.IsNullOrWhiteSpace(commaSeparatedSections))
        {
            _requestedSections = [];
            _allowedTables     = [];
            UnknownSections    = new HashSet<string>();
            IsActive           = false;
            return;
        }

        _requestedSections = commaSeparatedSections
            .Split(',')
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToHashSet(TableNames.Comparer);

        IsActive = _requestedSections.Count > 0;

        // Build a flat set of table names from matching sections (case-insensitive)
        _allowedTables = TableNames.NewSet();
        var foundSections = TableNames.NewSet();

        foreach (var section in loadedSections)
        {
            if (!_requestedSections.Contains(section.Name))
                continue;

            foundSections.Add(section.Name);

            foreach (var table in section.Tables)
                _allowedTables.Add(table);  // preserves original casing for lookup
        }

        // Compute which requested section names were not found
        var unknown = TableNames.NewSet();
        foreach (var requested in _requestedSections)
        {
            if (!foundSections.Contains(requested))
                unknown.Add(requested);
        }

        UnknownSections = unknown;
    }

    /// <summary>True when at least one section name was specified.</summary>
    public bool IsActive { get; }

    /// <summary>The section names that were requested.</summary>
    public IReadOnlySet<string> RequestedSections => _requestedSections;

    /// <summary>
    /// Returns <c>true</c> when the filter is inactive, or when the table name
    /// appears in the tables list of at least one of the requested sections.
    /// Comparison is case-insensitive.
    /// </summary>
    public bool Matches(string tableName)
    {
        if (!IsActive)
            return true;

        return _allowedTables.Contains(tableName);  // HashSet uses OrdinalIgnoreCase
    }
}
