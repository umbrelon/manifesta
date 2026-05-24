using Manifesta.Core.IR;

namespace Manifesta.Core.Filtering;

/// <summary>
/// Combines a <see cref="SchemaFilter"/> and a <see cref="SectionFilter"/> using
/// intersection semantics: a table must satisfy <em>both</em> filters to be included.
/// When one filter is inactive, only the other filter's result is used.
/// When both are inactive, every table is matched.
/// </summary>
public sealed class CombinedFilter
{
    private readonly SchemaFilter  _schema;
    private readonly SectionFilter _section;

    public CombinedFilter(SchemaFilter schema, SectionFilter section)
    {
        _schema  = schema  ?? throw new ArgumentNullException(nameof(schema));
        _section = section ?? throw new ArgumentNullException(nameof(section));
    }

    /// <summary>
    /// Convenience factory. Constructs both inner filters from raw CLI option strings.
    /// </summary>
    /// <param name="schemaOption">
    ///   Comma-separated schema names from <c>--schema</c>, or <c>null</c>.
    /// </param>
    /// <param name="sectionsOption">
    ///   Comma-separated section names from <c>--sections</c>, or <c>null</c>.
    /// </param>
    /// <param name="loadedSections">
    ///   Section definitions loaded from disk, used to resolve table membership.
    /// </param>
    public static CombinedFilter Create(
        string?                      schemaOption,
        string?                      sectionsOption,
        IReadOnlyList<SectionDefinition> loadedSections)
    {
        var schema  = new SchemaFilter(schemaOption);
        var section = new SectionFilter(sectionsOption, loadedSections);
        return new CombinedFilter(schema, section);
    }

    /// <summary>The inner schema filter (exposed for warning reporting).</summary>
    public SchemaFilter  Schema  => _schema;

    /// <summary>The inner section filter (exposed for warning reporting).</summary>
    public SectionFilter Section => _section;

    /// <summary>
    /// Returns <c>true</c> when the table satisfies both filters.
    /// </summary>
    public bool Matches(string tableName) =>
        _schema.Matches(tableName) && _section.Matches(tableName);
}
