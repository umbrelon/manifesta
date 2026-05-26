using FluentAssertions;
using Manifesta.Core;
using Manifesta.Core.IR;
using Manifesta.Core.Pipeline;
using Xunit;

namespace Manifesta.Core.Tests;

/// <summary>
/// Tests for <see cref="TableValidator"/>.
/// Covers all 8 validation rules from the spec:
/// 1. Required fields
/// 2. PK/FK integrity
/// 3. Invalid database types
/// 4. Match column validity
/// 5. Section/table consistency
/// 6. Naming conventions
/// 7. Cross-table references
/// 8. Circular dependencies
/// </summary>
public sealed class TableValidatorTests
{
    private readonly IValidator<TableDefinition> _validator = new TableValidator();

    // ── Rule 1: Required fields ────────────────────────────────────────────

    [Fact]
    public void Validate_ValidTable_NoIssues()
    {
        var table = new TableDefinition
        {
            Name = "Users",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "id", Type = "int", Nullable = false },
                new() { Name = "email", Type = "varchar(255)", Nullable = false }
            }.AsReadOnly(),
            PrimaryKey = new List<string> { "id" }.AsReadOnly(),
        };

        var result = _validator.Validate(table);

        result.HasErrors.Should().BeFalse();
        result.HasWarnings.Should().BeFalse();
    }

    [Fact]
    public void Validate_MissingTableName_ProducesError()
    {
        var table = new TableDefinition
        {
            Name = "",  // Empty name
            Fields = new List<FieldDefinition>().AsReadOnly(),
        };

        var result = _validator.Validate(table);

        result.HasErrors.Should().BeTrue();
        result.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Error &&
            (i.Code.Contains("NAME") || i.Message.Contains("name")));
    }

    [Fact]
    public void Validate_EmptyFieldsList_ProducesWarningOrError()
    {
        var table = new TableDefinition
        {
            Name = "EmptyTable",
            Fields = new List<FieldDefinition>().AsReadOnly(),
        };

        var result = _validator.Validate(table);

        // Empty fields list should produce a warning or error
        result.HasErrors.Should().BeTrue();
    }

    // ── Rule 2: Primary Key Integrity ─────────────────────────────────────

    [Fact]
    public void Validate_PrimaryKeyFieldDoesNotExist_ProducesError()
    {
        var table = new TableDefinition
        {
            Name = "Users",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "id", Type = "int", Nullable = false }
            }.AsReadOnly(),
            PrimaryKey = new List<string> { "nonexistent_id" }.AsReadOnly(),
        };

        var result = _validator.Validate(table);

        result.HasErrors.Should().BeTrue();
        result.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Error &&
            (i.Code.Contains("PK", StringComparison.OrdinalIgnoreCase) ||
             i.Message.Contains("primary key", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Validate_ValidPrimaryKey_NoError()
    {
        var table = new TableDefinition
        {
            Name = "Users",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "id", Type = "int", Nullable = false },
                new() { Name = "email", Type = "varchar(255)", Nullable = false }
            }.AsReadOnly(),
            PrimaryKey = new List<string> { "id" }.AsReadOnly(),
        };

        var result = _validator.Validate(table);

        result.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Validate_CompositePrimaryKey_AllFieldsMustExist()
    {
        var table = new TableDefinition
        {
            Name = "OrderItems",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "orderId", Type = "int", Nullable = false },
                new() { Name = "itemId", Type = "int", Nullable = false },
                new() { Name = "quantity", Type = "int", Nullable = false }
            }.AsReadOnly(),
            PrimaryKey = new List<string> { "orderId", "itemId" }.AsReadOnly(),
        };

        var result = _validator.Validate(table);

        result.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Validate_PrimaryKeyFieldName_IsCaseInsensitive()
    {
        var table = new TableDefinition
        {
            Name = "Users",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "UserId", Type = "int", Nullable = false }
            }.AsReadOnly(),
            PrimaryKey = new List<string> { "userid" }.AsReadOnly(),
        };

        var result = _validator.Validate(table);

        result.Issues.Should().NotContain(i => i.Code == "PK-FIELD-MISSING");
    }

    // ── Rule 3: Foreign Key Integrity ─────────────────────────────────────

    [Fact]
    public void Validate_ForeignKeySourceFieldDoesNotExist_ProducesError()
    {
        var table = new TableDefinition
        {
            Name = "Orders",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "orderId", Type = "int", Nullable = false }
            }.AsReadOnly(),
            ForeignKeys = new List<ForeignKey>
            {
                new()
                {
                    SourceField = "nonexistent",
                    TargetTable = "Users",
                    TargetField = "id"
                }
            }.AsReadOnly(),
        };

        var result = _validator.Validate(table);

        result.HasErrors.Should().BeTrue();
        result.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Error &&
            (i.Code.Contains("FK", StringComparison.OrdinalIgnoreCase) ||
             i.Message.Contains("foreign key", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Validate_ValidForeignKey_NoError()
    {
        var table = new TableDefinition
        {
            Name = "Orders",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "orderId", Type = "int", Nullable = false },
                new() { Name = "userId", Type = "int", Nullable = false }
            }.AsReadOnly(),
            ForeignKeys = new List<ForeignKey>
            {
                new()
                {
                    SourceField = "userId",
                    TargetTable = "Users",
                    TargetField = "id"
                }
            }.AsReadOnly(),
        };

        var result = _validator.Validate(table);

        result.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Validate_ForeignKeySourceFieldName_IsCaseInsensitive()
    {
        var table = new TableDefinition
        {
            Name = "Orders",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "OrderId", Type = "int", Nullable = false },
                new() { Name = "UserId",  Type = "int", Nullable = false }
            }.AsReadOnly(),
            ForeignKeys = new List<ForeignKey>
            {
                new()
                {
                    SourceField = "userid",
                    TargetTable = "Users",
                    TargetField = "id"
                }
            }.AsReadOnly(),
        };

        var result = _validator.Validate(table);

        result.Issues.Should().NotContain(i => i.Code == "FK-SOURCE-MISSING");
    }

    // ── Rule 4: Database Types ────────────────────────────────────────────

    [Fact]
    public void Validate_InvalidDatabaseType_ProducesError()
    {
        var table = new TableDefinition
        {
            Name = "Users",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "id", Type = "int", Nullable = false }
            }.AsReadOnly(),
            DatabaseTypes = new List<string> { "sqlserver", "invalid_type" }.AsReadOnly(),
        };

        var result = _validator.Validate(table);

        result.HasErrors.Should().BeTrue();
    }

    [Fact]
    public void Validate_ValidDatabaseTypes_NoError()
    {
        var table = new TableDefinition
        {
            Name = "Users",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "id", Type = "int", Nullable = false }
            }.AsReadOnly(),
            DatabaseTypes = new List<string> { "sqlserver", "mysql", "postgresql" }.AsReadOnly(),
        };

        var result = _validator.Validate(table);

        result.HasErrors.Should().BeFalse();
    }

    // ── Rule 5: Match Column Validity ─────────────────────────────────────

    [Fact]
    public void Validate_MatchColumnIsNullable_ProducesWarning()
    {
        var table = new TableDefinition
        {
            Name = "Users",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "id", Type = "int", Nullable = false },
                new() { Name = "email", Type = "varchar(255)", Nullable = true, IsMatchColumn = true }
            }.AsReadOnly(),
            PrimaryKey = new List<string> { "id" }.AsReadOnly(),
        };

        var result = _validator.Validate(table);

        result.HasWarnings.Should().BeTrue();
        result.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Warning &&
            (i.Code.Contains("MATCH") || i.Message.Contains("match column")));
    }

    [Fact]
    public void Validate_MatchColumnNonNull_NoWarning()
    {
        var table = new TableDefinition
        {
            Name = "Users",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "id", Type = "int", Nullable = false },
                new() { Name = "email", Type = "varchar(255)", Nullable = false, IsMatchColumn = true }
            }.AsReadOnly(),
            PrimaryKey = new List<string> { "id" }.AsReadOnly(),
        };

        var result = _validator.Validate(table);

        result.HasWarnings.Should().BeFalse();
    }

    // ── Rule 6: Sections ───────────────────────────────────────────────────

    [Fact]
    public void Validate_TableReferencesUndefinedSection_ProducesWarning()
    {
        // Provide an empty known-section set so the validator can detect the mismatch.
        // Without it (null), section validation is intentionally skipped.
        var validator = new TableValidator(knownSections: new HashSet<string>());
        var table = new TableDefinition
        {
            Name = "Users",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "id", Type = "int", Nullable = false }
            }.AsReadOnly(),
            Sections = new List<string> { "undefined_section" }.AsReadOnly(),
        };

        var result = validator.Validate(table);

        // Section not in the known set → warning, not an error.
        result.HasWarnings.Should().BeTrue();
        result.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Warning &&
            i.Code == "SECTION-UNDEFINED");
    }

    [Fact]
    public void Validate_TableWithSections_NoKnownSections_SkipsSectionRule()
    {
        // Default constructor (null knownSections) → Rule 6 is skipped entirely.
        var table = new TableDefinition
        {
            Name = "Users",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "id", Type = "int", Nullable = false }
            }.AsReadOnly(),
            Sections = new List<string> { "some_section" }.AsReadOnly(),
        };

        var result = _validator.Validate(table);

        result.HasWarnings.Should().BeFalse("section rule is skipped when no knownSections provided");
    }

    [Fact]
    public void Validate_TableWithKnownSection_NoWarning()
    {
        var validator = new TableValidator(knownSections: new HashSet<string> { "accounts" });
        var table = new TableDefinition
        {
            Name = "Users",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "id", Type = "int", Nullable = false }
            }.AsReadOnly(),
            Sections = new List<string> { "accounts" }.AsReadOnly(),
        };

        var result = validator.Validate(table);

        result.HasWarnings.Should().BeFalse();
    }

    // ── Rule 7: Nullable Primary Keys ──────────────────────────────────────

    [Fact]
    public void Validate_NullablePrimaryKeyField_ProducesError()
    {
        var table = new TableDefinition
        {
            Name = "Users",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "id", Type = "int", Nullable = true }  // ❌ PK should not be nullable
            }.AsReadOnly(),
            PrimaryKey = new List<string> { "id" }.AsReadOnly(),
        };

        var result = _validator.Validate(table);

        result.HasErrors.Should().BeTrue();
    }

    // ── Rule 8: Cascade Delete on Foreign Keys ────────────────────────────

    [Fact]
    public void Validate_ForeignKeyWithCascadeDelete_IsValid()
    {
        var table = new TableDefinition
        {
            Name = "Orders",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "orderId", Type = "int", Nullable = false },
                new() { Name = "userId", Type = "int", Nullable = false }
            }.AsReadOnly(),
            ForeignKeys = new List<ForeignKey>
            {
                new()
                {
                    SourceField = "userId",
                    TargetTable = "Users",
                    TargetField = "id",
                    CascadeDelete = true
                }
            }.AsReadOnly(),
        };

        var result = _validator.Validate(table);

        result.HasErrors.Should().BeFalse();
    }

    // ── ValidateAll (batch validation) ────────────────────────────────────

    [Fact]
    public void ValidateAll_MultipleTables_MergesResults()
    {
        var table1 = new TableDefinition
        {
            Name = "Users",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "id", Type = "int", Nullable = false }
            }.AsReadOnly(),
            PrimaryKey = new List<string> { "id" }.AsReadOnly(),
        };

        var table2 = new TableDefinition
        {
            Name = "Orders",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "orderId", Type = "int", Nullable = false }
            }.AsReadOnly(),
            ForeignKeys = new List<ForeignKey>
            {
                new()
                {
                    SourceField = "nonexistent",  // Invalid
                    TargetTable = "Users",
                    TargetField = "id"
                }
            }.AsReadOnly(),
        };

        var result = _validator.ValidateAll(new[] { table1, table2 });

        result.Issues.Should().HaveCountGreaterThan(0);
        result.HasErrors.Should().BeTrue();
    }

    [Fact]
    public void Validate_EmptyTable_ProducesError()
    {
        var table = new TableDefinition
        {
            Name = "",
            Fields = new List<FieldDefinition>().AsReadOnly(),
        };

        var result = _validator.Validate(table);

        result.HasErrors.Should().BeTrue();
    }

    [Fact]
    public void Validate_ColumnTypeWithLength_ValidatesSuccessfully()
    {
        var table = new TableDefinition
        {
            Name = "Users",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "id", Type = "int", Nullable = false },
                new() { Name = "email", Type = "varchar(255)", Nullable = false },
                new() { Name = "description", Type = "nvarchar(max)", Nullable = true }
            }.AsReadOnly(),
            PrimaryKey = new List<string> { "id" }.AsReadOnly(),
        };

        var result = _validator.Validate(table);

        result.HasErrors.Should().BeFalse();
    }

    // ── Rule 9: CascadeDelete only valid for Physical FKs ─────────────────

    [Fact]
    public void Validate_CascadeDeleteOnLogicalFk_ProducesWarning()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Order",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "Id",         Type = "int", Nullable = false },
                new() { Name = "CustomerId", Type = "int", Nullable = false },
            }.AsReadOnly(),
            ForeignKeys = new List<ForeignKey>
            {
                new() { SourceField = "CustomerId", TargetTable = "dbo.Customer", TargetField = "Id",
                        Kind = ForeignKeyKind.Logical, CascadeDelete = true },
            }.AsReadOnly(),
        };

        var result = _validator.Validate(table);

        result.HasWarnings.Should().BeTrue();
        result.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Warning &&
            i.Code == "FK-CASCADE-NON-PHYSICAL");
    }

    [Fact]
    public void Validate_CascadeDeleteOnVirtualFk_ProducesWarning()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Order",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "Id",       Type = "int", Nullable = false },
                new() { Name = "RegionId", Type = "int", Nullable = true },
            }.AsReadOnly(),
            ForeignKeys = new List<ForeignKey>
            {
                new() { SourceField = "RegionId", TargetTable = "dbo.Region", TargetField = "Id",
                        Kind = ForeignKeyKind.Virtual, CascadeDelete = true },
            }.AsReadOnly(),
        };

        var result = _validator.Validate(table);

        result.HasWarnings.Should().BeTrue();
        result.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Warning &&
            i.Code == "FK-CASCADE-NON-PHYSICAL");
    }

    [Fact]
    public void Validate_CascadeDeleteOnPhysicalFk_NoWarning()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Order",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "Id",         Type = "int", Nullable = false },
                new() { Name = "CustomerId", Type = "int", Nullable = false },
            }.AsReadOnly(),
            ForeignKeys = new List<ForeignKey>
            {
                new() { SourceField = "CustomerId", TargetTable = "dbo.Customer", TargetField = "Id",
                        Kind = ForeignKeyKind.Physical, CascadeDelete = true },
            }.AsReadOnly(),
        };

        var result = _validator.Validate(table);

        result.Issues.Should().NotContain(i => i.Code == "FK-CASCADE-NON-PHYSICAL");
    }

    // ── Rule 10: LabelField must exist in the table's own fields ─────────────

    [Fact]
    public void Validate_LabelFieldExistsInFields_NoWarning()
    {
        var table = new TableDefinition
        {
            Name       = "dbo.Customer",
            Fields     = [new() { Name = "Id", Type = "int" }, new() { Name = "szName", Type = "nvarchar(100)" }],
            LabelField = "szName",
        };

        var result = _validator.Validate(table);

        result.Issues.Should().NotContain(i => i.Code == "TABLE-LABEL-FIELD-MISSING");
    }

    [Fact]
    public void Validate_LabelFieldMissingFromFields_ProducesWarning()
    {
        var table = new TableDefinition
        {
            Name       = "dbo.Customer",
            Fields     = [new() { Name = "Id", Type = "int" }],
            LabelField = "szName",  // not in fields
        };

        var result = _validator.Validate(table);

        result.HasWarnings.Should().BeTrue();
        result.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Warning &&
            i.Code == "TABLE-LABEL-FIELD-MISSING");
    }

    [Fact]
    public void Validate_LabelFieldCheck_IsCaseInsensitive()
    {
        var table = new TableDefinition
        {
            Name       = "dbo.Customer",
            Fields     = [new() { Name = "szName", Type = "nvarchar(100)" }],
            LabelField = "SZNAME",
        };

        var result = _validator.Validate(table);

        result.Issues.Should().NotContain(i => i.Code == "TABLE-LABEL-FIELD-MISSING");
    }

    [Fact]
    public void Validate_NullLabelField_Rule10NotFired()
    {
        var table = new TableDefinition
        {
            Name       = "dbo.Customer",
            Fields     = [new() { Name = "Id", Type = "int" }],
            LabelField = null,
        };

        var result = _validator.Validate(table);

        result.Issues.Should().NotContain(i => i.Code == "TABLE-LABEL-FIELD-MISSING");
    }

    // ── Rules 7–10: Reference data validation ─────────────────────────────

    private static TableDefinition ReferenceTable(
        IReadOnlyList<IReadOnlyDictionary<string, System.Text.Json.JsonElement>> data) =>
        new()
        {
            Name             = "dbo.Status",
            IsReferenceTable = true,
            Fields           =
            [
                new FieldDefinition { Name = "Id",    Type = "int",         Nullable = false },
                new FieldDefinition { Name = "State", Type = "varchar(20)", Nullable = false },
            ],
            PrimaryKey = ["Id"],
            Data       = data,
        };

    private static IReadOnlyDictionary<string, System.Text.Json.JsonElement> Row(int id, string state)
    {
        var doc = System.Text.Json.JsonDocument.Parse($"{{\"Id\":{id},\"State\":\"{state}\"}}").RootElement;
        return new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["Id"]    = doc.GetProperty("Id"),
            ["State"] = doc.GetProperty("State"),
        };
    }

    [Fact]
    public void Validate_ReferenceTable_ValidData_NoIssues()
    {
        var table  = ReferenceTable([Row(1, "Active"), Row(2, "Inactive")]);
        var result = _validator.Validate(table);

        result.HasErrors.Should().BeFalse();
        result.HasWarnings.Should().BeFalse();
    }

    [Fact]
    public void Validate_ReferenceTable_NotReferenceTable_DataSkipped()
    {
        // Rules 7-10 must not fire when IsReferenceTable == false
        var table = new TableDefinition
        {
            Name             = "dbo.Status",
            IsReferenceTable = false,
            Fields           = [new FieldDefinition { Name = "Id", Type = "int" }],
            PrimaryKey       = ["Id"],
        };

        var result = _validator.Validate(table);

        result.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Validate_ReferenceTable_EmptyData_DataRulesSkipped()
    {
        // Empty data is valid — means "marked but not yet captured"
        var table = new TableDefinition
        {
            Name             = "dbo.Status",
            IsReferenceTable = true,
            Fields           = [new FieldDefinition { Name = "Id", Type = "int" }],
            PrimaryKey       = ["Id"],
        };

        var result = _validator.Validate(table);

        result.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Validate_ReferenceData_UnknownColumn_ProducesError()
    {
        var doc   = System.Text.Json.JsonDocument.Parse("{\"Id\":1,\"State\":\"Active\",\"Ghost\":\"x\"}").RootElement;
        var badRow = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["Id"]    = doc.GetProperty("Id"),
            ["State"] = doc.GetProperty("State"),
            ["Ghost"] = doc.GetProperty("Ghost"),  // not in fields
        };

        var result = _validator.Validate(ReferenceTable([badRow]));

        result.HasErrors.Should().BeTrue();
        result.Issues.Should().Contain(i => i.Code == "DATA-UNKNOWN-COLUMN");
    }

    [Fact]
    public void Validate_ReferenceData_MissingColumn_ProducesError()
    {
        var doc    = System.Text.Json.JsonDocument.Parse("{\"Id\":1}").RootElement;
        var badRow = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["Id"] = doc.GetProperty("Id"),
            // "State" is missing
        };

        var result = _validator.Validate(ReferenceTable([badRow]));

        result.HasErrors.Should().BeTrue();
        result.Issues.Should().Contain(i => i.Code == "DATA-MISSING-COLUMN");
    }

    [Fact]
    public void Validate_ReferenceData_DuplicatePk_ProducesError()
    {
        var result = _validator.Validate(ReferenceTable([Row(1, "Active"), Row(1, "Duplicate")]));

        result.HasErrors.Should().BeTrue();
        result.Issues.Should().Contain(i => i.Code == "DATA-DUPLICATE-PK");
    }

    [Fact]
    public void Validate_ReferenceData_OutOfOrder_ProducesWarning()
    {
        // Rows descending by Id → warning
        var result = _validator.Validate(ReferenceTable([Row(2, "Inactive"), Row(1, "Active")]));

        result.HasWarnings.Should().BeTrue();
        result.Issues.Should().Contain(i => i.Code == "DATA-UNORDERED");
    }

    [Fact]
    public void Validate_ReferenceData_SingleRow_NoOrderWarning()
    {
        var result = _validator.Validate(ReferenceTable([Row(1, "Active")]));

        result.Issues.Should().NotContain(i => i.Code == "DATA-UNORDERED");
    }

    // ── Computed field rules ────────────────────────────────────────────────

    [Fact]
    public void Validate_ComputedField_WithExpression_NoError()
    {
        var table = new TableDefinition
        {
            Name = "dbo.Person",
            Fields =
            [
                new FieldDefinition { Name = "Id",        Type = "int",          Nullable = false },
                new FieldDefinition { Name = "FirstName",  Type = "nvarchar(50)", Nullable = false },
                new FieldDefinition { Name = "LastName",   Type = "nvarchar(50)", Nullable = false },
                new FieldDefinition
                {
                    Name               = "FullName",
                    Type               = "nvarchar(101)",
                    Nullable           = true,
                    IsComputed         = true,
                    ComputedExpression = "([FirstName]+' '+[LastName])",
                },
            ],
            PrimaryKey = ["Id"],
        };

        var result = _validator.Validate(table);

        result.Issues.Should().NotContain(i => i.Code == "COMPUTED-NO-EXPRESSION");
    }

    [Fact]
    public void Validate_ComputedField_MissingExpression_ProducesError()
    {
        var table = new TableDefinition
        {
            Name = "dbo.Person",
            Fields =
            [
                new FieldDefinition { Name = "Id",       Type = "int",          Nullable = false },
                new FieldDefinition
                {
                    Name       = "FullName",
                    Type       = "nvarchar(101)",
                    Nullable   = true,
                    IsComputed = true,
                    // ComputedExpression intentionally omitted
                },
            ],
            PrimaryKey = ["Id"],
        };

        var result = _validator.Validate(table);

        result.HasErrors.Should().BeTrue();
        result.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Error &&
            i.Code     == "COMPUTED-NO-EXPRESSION" &&
            i.Message.Contains("FullName"));
    }

    [Fact]
    public void Validate_ComputedField_EmptyExpression_ProducesError()
    {
        var table = new TableDefinition
        {
            Name = "dbo.Person",
            Fields =
            [
                new FieldDefinition { Name = "Id",       Type = "int", Nullable = false },
                new FieldDefinition
                {
                    Name               = "Derived",
                    Type               = "int",
                    IsComputed         = true,
                    ComputedExpression = "   ",  // whitespace only
                },
            ],
            PrimaryKey = ["Id"],
        };

        var result = _validator.Validate(table);

        result.HasErrors.Should().BeTrue();
        result.Issues.Should().Contain(i => i.Code == "COMPUTED-NO-EXPRESSION");
    }

    [Fact]
    public void Validate_ComputedFieldInColumnSet_ProducesWarning()
    {
        var table = new TableDefinition
        {
            Name = "dbo.Person",
            Fields =
            [
                new FieldDefinition { Name = "Id",       Type = "int",          Nullable = false },
                new FieldDefinition { Name = "FirstName", Type = "nvarchar(50)", Nullable = false },
                new FieldDefinition
                {
                    Name               = "FullName",
                    Type               = "nvarchar(101)",
                    Nullable           = true,
                    IsComputed         = true,
                    ComputedExpression = "([FirstName]+' '+[LastName])",
                },
            ],
            PrimaryKey = ["Id"],
            Sets =
            [
                new ColumnSet { Name = "SyncSet", Columns = ["Id", "FirstName", "FullName"] },
            ],
        };

        var result = _validator.Validate(table);

        result.HasWarnings.Should().BeTrue();
        result.Issues.Should().ContainSingle(i =>
            i.Severity == ValidationSeverity.Warning &&
            i.Code     == "COMPUTED-IN-SET" &&
            i.Message.Contains("SyncSet") &&
            i.Message.Contains("FullName"));
    }

    [Fact]
    public void Validate_NonComputedFieldInColumnSet_NoWarning()
    {
        var table = new TableDefinition
        {
            Name = "dbo.Order",
            Fields =
            [
                new FieldDefinition { Name = "Id",       Type = "int", Nullable = false },
                new FieldDefinition { Name = "StatusId", Type = "int", Nullable = false },
            ],
            PrimaryKey = ["Id"],
            Sets = [new ColumnSet { Name = "SyncSet", Columns = ["Id", "StatusId"] }],
        };

        var result = _validator.Validate(table);

        result.Issues.Should().NotContain(i => i.Code == "COMPUTED-IN-SET");
    }

    [Fact]
    public void Validate_ComputedFieldNotInAnySet_NoWarning()
    {
        var table = new TableDefinition
        {
            Name = "dbo.Person",
            Fields =
            [
                new FieldDefinition { Name = "Id",       Type = "int",          Nullable = false },
                new FieldDefinition { Name = "FirstName", Type = "nvarchar(50)", Nullable = false },
                new FieldDefinition
                {
                    Name               = "FullName",
                    Type               = "nvarchar(101)",
                    IsComputed         = true,
                    ComputedExpression = "([FirstName]+' '+[LastName])",
                },
            ],
            PrimaryKey = ["Id"],
            Sets = [new ColumnSet { Name = "SyncSet", Columns = ["Id", "FirstName"] }],
        };

        var result = _validator.Validate(table);

        result.Issues.Should().NotContain(i => i.Code == "COMPUTED-IN-SET");
    }

    // ── Deprecation rules (DEPR-*) ────────────────────────────────────────────

    [Fact]
    public void Validate_DeprecatedFieldInPrimaryKey_ProducesWarning()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Order",
            Fields =
            [
                new FieldDefinition { Name = "Id", Type = "int", Nullable = false, IsDeprecated = true },
            ],
            PrimaryKey = ["Id"],
        };

        var result = _validator.Validate(table);

        result.HasWarnings.Should().BeTrue();
        result.Issues.Should().Contain(i =>
            i.Severity == ValidationSeverity.Warning &&
            i.Code     == "DEPR-PK");
    }

    [Fact]
    public void Validate_DeprecatedFieldAsMatchColumn_ProducesWarning()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Order",
            Fields =
            [
                new FieldDefinition { Name = "Id",   Type = "int",          Nullable = false },
                new FieldDefinition { Name = "Code", Type = "nvarchar(20)", Nullable = false, IsDeprecated = true, IsMatchColumn = true },
            ],
            PrimaryKey = ["Id"],
        };

        var result = _validator.Validate(table);

        result.HasWarnings.Should().BeTrue();
        result.Issues.Should().Contain(i =>
            i.Severity == ValidationSeverity.Warning &&
            i.Code     == "DEPR-MATCH-COLUMN");
    }

    [Fact]
    public void Validate_DeprecatedFieldAsFkSource_ProducesWarning()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Order",
            Fields =
            [
                new FieldDefinition { Name = "Id",         Type = "int", Nullable = false },
                new FieldDefinition { Name = "CustomerId", Type = "int", Nullable = false, IsDeprecated = true },
            ],
            PrimaryKey  = ["Id"],
            ForeignKeys =
            [
                new ForeignKey { SourceField = "CustomerId", TargetTable = "dbo.Customer", TargetField = "Id" },
            ],
        };

        var result = _validator.Validate(table);

        result.HasWarnings.Should().BeTrue();
        result.Issues.Should().Contain(i =>
            i.Severity == ValidationSeverity.Warning &&
            i.Code     == "DEPR-FK-SOURCE");
    }

    [Fact]
    public void Validate_NonDeprecatedFieldInPk_NoDeprWarning()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Order",
            Fields = [new FieldDefinition { Name = "Id", Type = "int", Nullable = false }],
            PrimaryKey = ["Id"],
        };

        var result = _validator.Validate(table);

        result.Issues.Should().NotContain(i => i.Code == "DEPR-PK");
        result.Issues.Should().NotContain(i => i.Code == "DEPR-MATCH-COLUMN");
        result.Issues.Should().NotContain(i => i.Code == "DEPR-FK-SOURCE");
    }

    // ── Sensitivity rules (SENS-*) ────────────────────────────────────────────

    [Fact]
    public void Validate_InvalidSensitivityValue_ProducesError()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Customer",
            Fields =
            [
                new FieldDefinition { Name = "Id",    Type = "int",          Nullable = false },
                new FieldDefinition { Name = "Email", Type = "nvarchar(255)", Sensitivity = "secret" },
            ],
            PrimaryKey = ["Id"],
        };

        var result = _validator.Validate(table);

        result.HasErrors.Should().BeTrue();
        result.Issues.Should().Contain(i =>
            i.Severity == ValidationSeverity.Error &&
            i.Code     == "SENS-INVALID-VALUE");
    }

    [Theory]
    [InlineData("pii")]
    [InlineData("confidential")]
    [InlineData("internal")]
    [InlineData("public")]
    public void Validate_ValidSensitivityValue_NoError(string value)
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Customer",
            Fields =
            [
                new FieldDefinition { Name = "Id",    Type = "int",           Nullable = false },
                new FieldDefinition { Name = "Email", Type = "nvarchar(255)", Sensitivity = value },
            ],
            PrimaryKey = ["Id"],
        };

        var result = _validator.Validate(table);

        result.Issues.Should().NotContain(i => i.Code == "SENS-INVALID-VALUE");
    }

    [Fact]
    public void Validate_PiiFieldWithNoDescription_ProducesWarning()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Customer",
            Fields =
            [
                new FieldDefinition { Name = "Id",    Type = "int",           Nullable = false },
                new FieldDefinition { Name = "Email", Type = "nvarchar(255)", Sensitivity = "pii", Description = "" },
            ],
            PrimaryKey = ["Id"],
        };

        var result = _validator.Validate(table);

        result.HasWarnings.Should().BeTrue();
        result.Issues.Should().Contain(i =>
            i.Severity == ValidationSeverity.Warning &&
            i.Code     == "SENS-PII-NO-DESCRIPTION");
    }

    [Fact]
    public void Validate_PiiFieldWithDescription_NoWarning()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Customer",
            Fields =
            [
                new FieldDefinition { Name = "Id",    Type = "int",           Nullable = false },
                new FieldDefinition { Name = "Email", Type = "nvarchar(255)", Sensitivity = "pii", Description = "Primary contact email." },
            ],
            PrimaryKey = ["Id"],
        };

        var result = _validator.Validate(table);

        result.Issues.Should().NotContain(i => i.Code == "SENS-PII-NO-DESCRIPTION");
    }

    // ── Constraint rules (CHECK-*/UNIQUE-*) ───────────────────────────────────

    [Fact]
    public void Validate_CheckConstraintWithMissingColumn_ProducesWarning()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Order",
            Fields =
            [
                new FieldDefinition { Name = "Id", Type = "int", Nullable = false },
            ],
            PrimaryKey       = ["Id"],
            CheckConstraints =
            [
                new CheckConstraint { Name = "CK_Amount", Expression = "[Amount] > 0", Column = "Amount" },
            ],
        };

        var result = _validator.Validate(table);

        result.HasWarnings.Should().BeTrue();
        result.Issues.Should().Contain(i =>
            i.Severity == ValidationSeverity.Warning &&
            i.Code     == "CHECK-COLUMN-MISSING");
    }

    [Fact]
    public void Validate_CheckConstraintWithKnownColumn_NoWarning()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Order",
            Fields =
            [
                new FieldDefinition { Name = "Id",     Type = "int", Nullable = false },
                new FieldDefinition { Name = "Amount", Type = "int", Nullable = false },
            ],
            PrimaryKey       = ["Id"],
            CheckConstraints =
            [
                new CheckConstraint { Name = "CK_Amount", Expression = "[Amount] > 0", Column = "Amount" },
            ],
        };

        var result = _validator.Validate(table);

        result.Issues.Should().NotContain(i => i.Code == "CHECK-COLUMN-MISSING");
    }

    [Fact]
    public void Validate_CheckConstraintWithNoColumnScoping_NoWarning()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Order",
            Fields = [new FieldDefinition { Name = "Id", Type = "int", Nullable = false }],
            PrimaryKey       = ["Id"],
            CheckConstraints =
            [
                new CheckConstraint { Name = "CK_Multi", Expression = "[A] > [B]", Column = null },
            ],
        };

        var result = _validator.Validate(table);

        result.Issues.Should().NotContain(i => i.Code == "CHECK-COLUMN-MISSING");
    }

    [Fact]
    public void Validate_UniqueConstraintWithMissingColumn_ProducesWarning()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Order",
            Fields =
            [
                new FieldDefinition { Name = "Id", Type = "int", Nullable = false },
            ],
            PrimaryKey        = ["Id"],
            UniqueConstraints =
            [
                new UniqueConstraint { Name = "UQ_Code", Columns = ["Code"] },
            ],
        };

        var result = _validator.Validate(table);

        result.HasWarnings.Should().BeTrue();
        result.Issues.Should().Contain(i =>
            i.Severity == ValidationSeverity.Warning &&
            i.Code     == "UNIQUE-COLUMN-MISSING");
    }

    [Fact]
    public void Validate_UniqueConstraintWithKnownColumns_NoWarning()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Order",
            Fields =
            [
                new FieldDefinition { Name = "Id",   Type = "int",          Nullable = false },
                new FieldDefinition { Name = "Code", Type = "nvarchar(20)", Nullable = false },
            ],
            PrimaryKey        = ["Id"],
            UniqueConstraints =
            [
                new UniqueConstraint { Name = "UQ_Code", Columns = ["Code"] },
            ],
        };

        var result = _validator.Validate(table);

        result.Issues.Should().NotContain(i => i.Code == "UNIQUE-COLUMN-MISSING");
    }
}
