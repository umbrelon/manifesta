using System.Text.Json;
using Manifesta.Core.IR;
using Manifesta.Core.Pipeline;

namespace Manifesta.Core;

/// <summary>
/// Validates <see cref="TableDefinition"/> objects against the specification rules.
/// Implements <see cref="IValidator{T}"/> for <see cref="TableDefinition"/>.
///
/// Enforces validation rules:
/// 1. Required fields (name, fields collection)
/// 2. Primary key field existence
/// 3. Foreign key integrity (source field existence)
/// 4. Valid database types
/// 5. Match column nullability constraint
/// 6. Section existence (optional cross-table)
/// 7. Nullable primary key detection
/// 8. Cascade delete handling
/// 9. CascadeDelete is only valid for Physical FK relationships
/// 10. LabelField must exist in the table's own fields
/// </summary>
public sealed class TableValidator : IValidator<TableDefinition>
{
    private static readonly HashSet<string> ValidDatabaseTypes = new(TableNames.Comparer)
    {
        "sqlserver", "mysql", "postgresql", "oracle", "sqlite"
    };

    /// <summary>
    /// When provided, Rule 6 validates each table section membership against this set.
    /// When <c>null</c> (default), section validation is skipped — sections are not yet
    /// resolved at the single-table validation phase and must be validated cross-table later.
    /// </summary>
    private readonly IReadOnlySet<string>? _knownSections;

    public TableValidator(IReadOnlySet<string>? knownSections = null)
    {
        _knownSections = knownSections;
    }

    /// <summary>
    /// Validate a single table definition.
    /// </summary>
    public ValidationResult Validate(TableDefinition table)
    {
        var issues = new List<ValidationIssue>();

        // Rule 1: Required fields
        if (string.IsNullOrWhiteSpace(table.Name))
        {
            issues.Add(Error("TABLE-NAME-MISSING", "Table name is required and cannot be empty"));
        }

        if (table.Fields.Count == 0)
        {
            issues.Add(Error("TABLE-FIELDS-EMPTY", "Table must have at least one field"));
            return new ValidationResult { Issues = issues.AsReadOnly() };
        }

        // Rule 2: Primary key integrity
        foreach (var pkField in table.PrimaryKey)
        {
            var field = table.Fields.FirstOrDefault(f => f.Name == pkField);
            if (field is null)
                issues.Add(Error("PK-FIELD-MISSING", $"Primary key field '{pkField}' does not exist"));
            else if (field.Nullable)
                issues.Add(Error("PK-NULLABLE", $"Primary key field '{pkField}' cannot be nullable"));
        }

        // Rule 3: Foreign key integrity
        foreach (var fk in table.ForeignKeys)
        {
            if (!table.Fields.Any(f => f.Name == fk.SourceField))
                issues.Add(Error("FK-SOURCE-MISSING", $"Foreign key source field '{fk.SourceField}' does not exist"));

            // Rule 9: CascadeDelete is only meaningful for Physical FK relationships
            if (fk.CascadeDelete && fk.Kind != ForeignKeyKind.Physical)
                issues.Add(Warning("FK-CASCADE-NON-PHYSICAL",
                    $"Foreign key '{fk.SourceField}' → '{fk.TargetTable}.{fk.TargetField}': " +
                    $"cascadeDelete is only valid for Physical FK relationships (kind is '{fk.Kind.ToString().ToLowerInvariant()}')"));
        }

        // Rule 10: LabelField must exist in this table's own fields
        if (table.LabelField is not null)
        {
            if (!table.Fields.Any(f => string.Equals(f.Name, table.LabelField, StringComparison.OrdinalIgnoreCase)))
                issues.Add(Warning("TABLE-LABEL-FIELD-MISSING",
                    $"labelField '{table.LabelField}' does not exist in table '{table.Name}'"));
        }

        // Rule 4: Database types
        foreach (var dbType in table.DatabaseTypes)
        {
            if (!ValidDatabaseTypes.Contains(dbType))
            {
                issues.Add(Error("INVALID-DB-TYPE", $"Unknown database type '{dbType}'"));
            }
        }

        // Rule 5: Match column nullability
        foreach (var field in table.Fields.Where(f => f.IsMatchColumn))
        {
            if (field.Nullable)
            {
                issues.Add(Warning("MATCH-COLUMN-NULLABLE",
                    $"Match column '{field.Name}' should not be nullable"));
            }
        }

        // Rule: Computed column must have an expression
        foreach (var field in table.Fields.Where(f => f.IsComputed))
        {
            if (string.IsNullOrWhiteSpace(field.ComputedExpression))
                issues.Add(Error("COMPUTED-NO-EXPRESSION",
                    $"Computed column '{field.Name}' must have a computedExpression"));
        }

        // Rule: Column sets should not include computed columns (they cannot be written)
        var computedFieldNames = table.Fields
            .Where(f => f.IsComputed)
            .Select(f => f.Name)
            .ToHashSet(TableNames.Comparer);

        if (computedFieldNames.Count > 0)
        {
            foreach (var set in table.Sets)
            {
                foreach (var col in set.Columns)
                {
                    if (computedFieldNames.Contains(col))
                        issues.Add(Warning("COMPUTED-IN-SET",
                            $"Column set '{set.Name}' includes computed column '{col}' — computed columns cannot be written"));
                }
            }
        }

        // Rule 6: Section membership (only when known sections are provided)
        // Skip if _knownSections is null — sections are resolved in a later cross-table pass.
        if (_knownSections is not null)
        {
            foreach (var section in table.Sections)
            {
                if (!_knownSections.Contains(section))
                    issues.Add(Warning("SECTION-UNDEFINED",
                        $"Section '{section}' is not defined in configuration"));
            }
        }

        // Rules 7–10: Reference table data validation (only when IsReferenceTable == true and data is present)
        if (table.IsReferenceTable && table.Data.Count > 0)
            ValidateReferenceData(table, issues);

        // Deprecation rules: warn when deprecated fields are used in structural roles
        foreach (var field in table.Fields.Where(f => f.IsDeprecated))
        {
            if (table.PrimaryKey.Contains(field.Name, TableNames.Comparer))
                issues.Add(Warning("DEPR-PK",
                    $"Field '{field.Name}' is deprecated but is part of the primary key"));

            if (field.IsMatchColumn)
                issues.Add(Warning("DEPR-MATCH-COLUMN",
                    $"Field '{field.Name}' is deprecated but is marked as a match column"));

            if (table.ForeignKeys.Any(fk => TableNames.Comparer.Equals(fk.SourceField, field.Name)))
                issues.Add(Warning("DEPR-FK-SOURCE",
                    $"Field '{field.Name}' is deprecated but is the source of a foreign key"));
        }

        // Sensitivity rules
        var validSensitivities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "public", "internal", "confidential", "pii" };

        foreach (var field in table.Fields)
        {
            if (field.Sensitivity is not null && !validSensitivities.Contains(field.Sensitivity))
                issues.Add(Error("SENS-INVALID-VALUE",
                    $"Field '{field.Name}' has invalid sensitivity value '{field.Sensitivity}'. " +
                    "Allowed: public, internal, confidential, pii"));

            if (string.Equals(field.Sensitivity, "pii", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(field.Description))
                issues.Add(Warning("SENS-PII-NO-DESCRIPTION",
                    $"PII field '{field.Name}' has no description — document what data it stores"));
        }

        // Constraint column existence rules
        foreach (var cc in table.CheckConstraints)
        {
            if (cc.Column is not null
                && !table.Fields.Any(f => TableNames.Comparer.Equals(f.Name, cc.Column)))
                issues.Add(Warning("CHECK-COLUMN-MISSING",
                    $"CHECK constraint '{cc.Name}' references column '{cc.Column}' which does not exist in fields"));
        }

        foreach (var uc in table.UniqueConstraints)
        {
            foreach (var col in uc.Columns)
            {
                if (!table.Fields.Any(f => TableNames.Comparer.Equals(f.Name, col)))
                    issues.Add(Warning("UNIQUE-COLUMN-MISSING",
                        $"UNIQUE constraint '{uc.Name}' references column '{col}' which does not exist in fields"));
            }
        }

        return new ValidationResult { Issues = issues.AsReadOnly() };
    }

    // ── Reference data validation ──────────────────────────────────────────

    private static void ValidateReferenceData(TableDefinition table, List<ValidationIssue> issues)
    {
        var fieldNames = table.Fields
            .Select(f => f.Name)
            .ToHashSet(TableNames.Comparer);

        var pkColumns = table.PrimaryKey;
        var seenPkValues = new HashSet<string>(StringComparer.Ordinal);

        for (var rowIndex = 0; rowIndex < table.Data.Count; rowIndex++)
        {
            var row = table.Data[rowIndex];

            // Rule 7: No extra columns in data rows
            foreach (var key in row.Keys)
            {
                if (!fieldNames.Contains(key))
                    issues.Add(Error("DATA-UNKNOWN-COLUMN",
                        $"data[{rowIndex}] contains column '{key}' which does not exist in fields"));
            }

            // Rule 8: All field names must be present in each row
            foreach (var field in table.Fields)
            {
                if (!row.ContainsKey(field.Name))
                    issues.Add(Error("DATA-MISSING-COLUMN",
                        $"data[{rowIndex}] is missing column '{field.Name}'"));
            }

            // Rule 9: Primary key values must be unique across all rows
            if (pkColumns.Count > 0)
            {
                var pkValue = string.Join("|", pkColumns.Select(pk =>
                    row.TryGetValue(pk, out var v) ? v.ToString() : ""));

                if (!seenPkValues.Add(pkValue))
                    issues.Add(Error("DATA-DUPLICATE-PK",
                        $"data[{rowIndex}] has a duplicate primary key value '{pkValue}'"));
            }
        }

        // Rule 10: Rows should be ordered by primary key (warning)
        if (pkColumns.Count > 0 && table.Data.Count > 1)
        {
            for (var i = 1; i < table.Data.Count; i++)
            {
                var prev = table.Data[i - 1];
                var curr = table.Data[i];
                if (ComparePkValues(prev, curr, pkColumns) > 0)
                {
                    issues.Add(Warning("DATA-UNORDERED",
                        "data rows are not sorted by primary key — sort for deterministic diffs"));
                    break;
                }
            }
        }
    }

    private static int ComparePkValues(
        IReadOnlyDictionary<string, JsonElement> a,
        IReadOnlyDictionary<string, JsonElement> b,
        IReadOnlyList<string> pkColumns)
    {
        foreach (var col in pkColumns)
        {
            var aVal = a.TryGetValue(col, out var av) ? av.ToString() : "";
            var bVal = b.TryGetValue(col, out var bv) ? bv.ToString() : "";
            var cmp  = string.Compare(aVal, bVal, StringComparison.Ordinal);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static ValidationIssue Error(string code, string message) => new()
    {
        Severity = ValidationSeverity.Error,
        Code = code,
        Message = message,
    };

    private static ValidationIssue Warning(string code, string message) => new()
    {
        Severity = ValidationSeverity.Warning,
        Code = code,
        Message = message,
    };
}
