using Manifesta.Core.IR;
using Manifesta.Core.Pipeline;

namespace Manifesta.Core;

/// <summary>
/// Validates cross-entity relationships between tables, sections, and APIs.
/// Pure (no I/O). Requires the complete set of loaded definitions.
/// </summary>
public sealed class CrossValidator
{
    /// <summary>
    /// Run all cross-entity checks and return a merged <see cref="ValidationResult"/>.
    /// </summary>
    public ValidationResult Validate(
        IReadOnlyList<TableDefinition> tables,
        IReadOnlyList<SectionDefinition> sections,
        IReadOnlyList<ApiDefinition> apis)
    {
        var issues = new List<ValidationIssue>();

        var tableIndex   = tables.ToDictionary(t => t.Name, t => t, TableNames.Comparer);
        var sectionNames = sections
            .Select(s => s.Name)
            .ToHashSet(TableNames.Comparer);

        ValidateTables(tables, tableIndex, sectionNames, issues);
        ValidateSections(sections, tableIndex, issues);
        ValidateApis(apis, tableIndex, issues);

        return new ValidationResult { Issues = issues.AsReadOnly() };
    }

    // ── Per-table cross-checks ─────────────────────────────────────────────

    private static void ValidateTables(
        IReadOnlyList<TableDefinition> tables,
        IReadOnlyDictionary<string, TableDefinition> tableIndex,
        IReadOnlySet<string> sectionNames,
        List<ValidationIssue> issues)
    {
        foreach (var table in tables)
        {
            foreach (var fk in table.ForeignKeys)
            {
                if (!tableIndex.ContainsKey(fk.TargetTable))
                {
                    issues.Add(Error("FK-TARGET-MISSING",
                        $"Table '{table.Name}': FK '{fk.SourceField}' → '{fk.TargetTable}': " +
                        $"target table '{fk.TargetTable}' is not defined",
                        table.SourceFile));
                }
            }

            foreach (var sectionName in table.Sections)
            {
                if (!sectionNames.Contains(sectionName))
                    issues.Add(Warning("SECTION-UNDEFINED",
                        $"Table '{table.Name}' references undefined section '{sectionName}'",
                        table.SourceFile));
            }
        }
    }

    // ── Per-section cross-checks ───────────────────────────────────────────

    private static void ValidateSections(
        IReadOnlyList<SectionDefinition> sections,
        IReadOnlyDictionary<string, TableDefinition> tableIndex,
        List<ValidationIssue> issues)
    {
        foreach (var section in sections)
        {
            foreach (var tableName in section.Tables)
            {
                if (!tableIndex.ContainsKey(tableName))
                    issues.Add(Warning("SECTION-TABLE-UNDEFINED",
                        $"Section '{section.Name}' references undefined table '{tableName}'",
                        section.SourceFile));
            }
        }
    }

    // ── Per-API cross-checks ───────────────────────────────────────────────

    private static void ValidateApis(
        IReadOnlyList<ApiDefinition> apis,
        IReadOnlyDictionary<string, TableDefinition> tableIndex,
        List<ValidationIssue> issues)
    {
        foreach (var api in apis)
        {
            foreach (var endpoint in api.Endpoints)
            {
                if (endpoint.DbTable is null)
                    continue;

                if (!tableIndex.TryGetValue(endpoint.DbTable, out var referencedTable))
                {
                    issues.Add(Error("API-DBTABLE-MISSING",
                        $"API '{api.Name}' endpoint '{endpoint.Method} {endpoint.Path}': " +
                        $"x-db-table '{endpoint.DbTable}' is not defined in table definitions",
                        api.SourceFile));
                    continue;
                }

                if (endpoint.DatabaseTypes.Count > 0 && referencedTable.DatabaseTypes.Count > 0)
                {
                    var tableDbTypes = referencedTable.DatabaseTypes
                        .ToHashSet(TableNames.Comparer);

                    foreach (var dbType in endpoint.DatabaseTypes)
                    {
                        if (!tableDbTypes.Contains(dbType))
                            issues.Add(Warning("API-DBTYPE-MISMATCH",
                                $"API '{api.Name}' endpoint '{endpoint.Method} {endpoint.Path}': " +
                                $"x-databaseTypes value '{dbType}' is not declared on table '{referencedTable.Name}' " +
                                $"(table declares: {string.Join(", ", referencedTable.DatabaseTypes)})",
                                api.SourceFile));
                    }
                }
            }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static ValidationIssue Error(string code, string message, string? file = null) => new()
    {
        Severity = ValidationSeverity.Error,
        Code     = code,
        Message  = message,
        File     = string.IsNullOrEmpty(file) ? null : file,
    };

    private static ValidationIssue Warning(string code, string message, string? file = null) => new()
    {
        Severity = ValidationSeverity.Warning,
        Code     = code,
        Message  = message,
        File     = string.IsNullOrEmpty(file) ? null : file,
    };
}
