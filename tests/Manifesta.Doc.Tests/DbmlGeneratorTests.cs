using System.Text.Json;
using FluentAssertions;
using Manifesta.Core;
using Manifesta.Core.IR;
using Manifesta.Doc;
using Xunit;

namespace Manifesta.Doc.Tests;

public sealed class DbmlGeneratorTests
{
    private readonly DbmlGenerator _gen = new();

    // ── Table block ───────────────────────────────────────────────────────────

    [Fact]
    public void Generate_SingleTable_ProducesTableBlock()
    {
        var ir = Ir(Table("dbo.Customer",
            Field("Id",   "int",          nullable: false, isPk: true),
            Field("Name", "nvarchar(100)", nullable: false)));

        var result = _gen.Generate(ir);

        result.Should().Contain("Table dbo.Customer {");
        result.Should().Contain("  Id int [pk, not null]");
        result.Should().Contain("  Name nvarchar(100) [not null]");
        result.Should().Contain("}");
    }

    [Fact]
    public void Generate_NullableField_OmitsNotNull()
    {
        var ir = Ir(Table("dbo.T", Field("OptCol", "nvarchar(50)", nullable: true)));

        var result = _gen.Generate(ir);

        result.Should().Contain("  OptCol nvarchar(50)");
        result.Should().NotContain("not null");
    }

    [Fact]
    public void Generate_TableWithDescription_EmitsNote()
    {
        var ir = Ir(Table("dbo.T", description: "Stores customers") with
        {
            Fields = [Field("Id", "int", nullable: false)]
        });

        var result = _gen.Generate(ir);

        result.Should().Contain("Note: \"Stores customers\"");
    }

    [Fact]
    public void Generate_FieldWithDescription_EmitsInlineNote()
    {
        var ir = Ir(Table("dbo.T",
            Field("Id", "int", nullable: false, description: "Primary key")));

        var result = _gen.Generate(ir);

        result.Should().Contain("note: \"Primary key\"");
    }

    [Fact]
    public void Generate_ComputedField_EmitsCalculatedNote()
    {
        var field = new FieldDefinition
        {
            Name               = "TotalWithTax",
            Type               = "decimal(10,2)",
            Nullable           = false,
            IsComputed         = true,
            ComputedExpression = "[TotalAmount]*1.21",
            IsPersisted        = true,
        };

        var ir = Ir(Table("dbo.Orders", field));

        var result = _gen.Generate(ir);

        result.Should().Contain("note: \"// calculated: ([TotalAmount]*1.21) PERSISTED\"");
    }

    [Fact]
    public void Generate_ComputedFieldNonPersisted_NoPERSISTEDBadge()
    {
        var field = new FieldDefinition
        {
            Name               = "DisplayName",
            Type               = "nvarchar(200)",
            Nullable           = true,
            IsComputed         = true,
            ComputedExpression = "[First]+' '+[Last]",
            IsPersisted        = false,
        };

        var ir = Ir(Table("dbo.T", field));

        var result = _gen.Generate(ir);

        result.Should().Contain("// calculated: ([First]+' '+[Last])");
        result.Should().NotContain("PERSISTED");
    }

    [Fact]
    public void Generate_ComputedFieldWithDescription_CombinesNoteAndCalc()
    {
        var field = new FieldDefinition
        {
            Name               = "Total",
            Type               = "decimal",
            Nullable           = false,
            Description        = "Calculated total",
            IsComputed         = true,
            ComputedExpression = "[Price]*[Qty]",
            IsPersisted        = false,
        };

        var ir = Ir(Table("dbo.T", field));

        var result = _gen.Generate(ir);

        result.Should().Contain("note: \"Calculated total; // calculated: ([Price]*[Qty])\"");
    }

    // ── Refs ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_PhysicalFk_EmitsRef()
    {
        var orders = new TableDefinition
        {
            Name        = "dbo.Order",
            Fields      = [Field("Id", "int", nullable: false, isPk: true), Field("CustomerId", "int", nullable: false)],
            PrimaryKey  = ["Id"],
            ForeignKeys = [Fk("CustomerId", "dbo.Customer", "Id")],
        };

        var customers = Table("dbo.Customer", Field("Id", "int", nullable: false, isPk: true));
        var ir = Ir(orders, customers);

        var result = _gen.Generate(ir);

        result.Should().Contain("Ref: dbo.Order.CustomerId > dbo.Customer.Id");
    }

    [Fact]
    public void Generate_LogicalFk_EmitsRefWithLogicalComment()
    {
        var t = new TableDefinition
        {
            Name        = "dbo.T",
            Fields      = [Field("Id", "int", false, isPk: true), Field("Ref", "int", false)],
            PrimaryKey  = ["Id"],
            ForeignKeys = [new ForeignKey { SourceField = "Ref", TargetTable = "dbo.Other", TargetField = "Id", Kind = ForeignKeyKind.Logical }],
        };

        var ir = Ir(t);
        var result = _gen.Generate(ir);

        result.Should().Contain("Ref: dbo.T.Ref > dbo.Other.Id // logical");
    }

    [Fact]
    public void Generate_VirtualFk_EmitsRefWithVirtualComment()
    {
        var t = new TableDefinition
        {
            Name        = "dbo.T",
            Fields      = [Field("Id", "int", false, isPk: true), Field("Ref", "int", false)],
            PrimaryKey  = ["Id"],
            ForeignKeys = [new ForeignKey { SourceField = "Ref", TargetTable = "dbo.Other", TargetField = "Id", Kind = ForeignKeyKind.Virtual }],
        };

        var ir = Ir(t);
        var result = _gen.Generate(ir);

        result.Should().Contain("Ref: dbo.T.Ref > dbo.Other.Id // virtual");
    }

    // ── TableGroups ───────────────────────────────────────────────────────────

    [Fact]
    public void Generate_WithSection_EmitsTableGroup()
    {
        var ir = new ManifestRoot
        {
            Tables   = [Table("dbo.A"), Table("dbo.B")],
            Sections = [new SectionDefinition { Name = "Core", Tables = ["dbo.A", "dbo.B"] }],
        };

        var result = _gen.Generate(ir);

        result.Should().Contain("TableGroup Core {");
        result.Should().Contain("  dbo.A");
        result.Should().Contain("  dbo.B");
    }

    // ── Identifier quoting ────────────────────────────────────────────────────

    [Fact]
    public void Generate_IdentifierWithSpaces_IsQuoted()
    {
        var ir = Ir(Table("My Schema.My Table", Field("My Field", "int", false)));

        var result = _gen.Generate(ir);

        result.Should().Contain("\"My Schema.My Table\"");
        result.Should().Contain("\"My Field\"");
    }

    [Fact]
    public void Generate_SchemaQualifiedIdentifier_IsNotQuoted()
    {
        var ir = Ir(Table("dbo.Customer", Field("Id", "int", false)));

        var result = _gen.Generate(ir);

        // Schema-qualified names with dots are valid unquoted DBML identifiers
        result.Should().Contain("Table dbo.Customer {");
    }

    // ── Output format ─────────────────────────────────────────────────────────

    [Fact]
    public void Generate_Always_EndsWithSingleNewline()
    {
        var ir = Ir(Table("dbo.T", Field("Id", "int", false)));

        var result = _gen.Generate(ir);

        result.Should().EndWith("\n");
        result.Should().NotEndWith("\n\n");
    }

    [Fact]
    public void Generate_EmptyIr_ProducesEmptyOutput()
    {
        var ir     = new ManifestRoot { Tables = [], Sections = [] };
        var result = _gen.Generate(ir);

        result.Trim().Should().BeEmpty();
    }

    // ── Round-trip ────────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_GenerateThenParse_PreservesStructure()
    {
        var original = new ManifestRoot
        {
            Tables =
            [
                new TableDefinition
                {
                    Name        = "dbo.Order",
                    Description = "Sales orders",
                    Fields      =
                    [
                        new FieldDefinition { Name = "Id",         Type = "int",           Nullable = false, IsPrimaryKey = true },
                        new FieldDefinition { Name = "CustomerId", Type = "int",           Nullable = false },
                        new FieldDefinition { Name = "Total",      Type = "decimal(10,2)", Nullable = false, Description = "Grand total" },
                    ],
                    PrimaryKey  = ["Id"],
                    ForeignKeys = [new ForeignKey { SourceField = "CustomerId", TargetTable = "dbo.Customer", TargetField = "Id" }],
                },
                new TableDefinition
                {
                    Name       = "dbo.Customer",
                    Fields     = [new FieldDefinition { Name = "Id", Type = "int", Nullable = false, IsPrimaryKey = true }],
                    PrimaryKey = ["Id"],
                },
            ],
            Sections = [new SectionDefinition { Name = "Sales", Tables = ["dbo.Order", "dbo.Customer"] }],
        };

        var dbml   = _gen.Generate(original);
        var parsed = new DbmlParser().Parse(dbml);

        parsed.Errors.Should().BeEmpty();
        parsed.Tables.Should().HaveCount(2);

        var order = parsed.Tables.Single(t => t.Name == "dbo.Order");
        order.Description.Should().Be("Sales orders");
        order.PrimaryKey.Should().ContainSingle("Id");
        order.ForeignKeys.Should().HaveCount(1);
        order.ForeignKeys[0].TargetTable.Should().Be("dbo.Customer");

        order.Fields.Single(f => f.Name == "Total").Description.Should().Be("Grand total");

        parsed.Sections.Should().HaveCount(1);
        parsed.Sections[0].Name.Should().Be("Sales");
    }

    // ── Records blocks ────────────────────────────────────────────────────────

    [Fact]
    public void Generate_ReferenceTableWithData_EmitsRecordsBlock()
    {
        var table = new TableDefinition
        {
            Name             = "dbo.UserRole",
            IsReferenceTable = true,
            Fields           = [Field("Id", "int", false, isPk: true), Field("Role", "nvarchar(20)", false)],
            PrimaryKey       = ["Id"],
            Data             =
            [
                Row(("Id", 1), ("Role", "admin")),
                Row(("Id", 2), ("Role", "member")),
            ],
        };

        var result = _gen.Generate(Ir(table));

        result.Should().Contain("Records dbo.UserRole(Id, Role) {");
        result.Should().Contain("  1, 'admin'");
        result.Should().Contain("  2, 'member'");
    }

    [Fact]
    public void Generate_NonReferenceTable_OmitsRecordsBlock()
    {
        var table = new TableDefinition
        {
            Name             = "dbo.Order",
            IsReferenceTable = false,
            Fields           = [Field("Id", "int", false, isPk: true)],
            PrimaryKey       = ["Id"],
            Data             = [Row(("Id", 1))],
        };

        var result = _gen.Generate(Ir(table));

        result.Should().NotContain("Records");
    }

    [Fact]
    public void Generate_ReferenceTableEmptyData_OmitsRecordsBlock()
    {
        var table = new TableDefinition
        {
            Name             = "dbo.Status",
            IsReferenceTable = true,
            Fields           = [Field("Id", "int", false, isPk: true)],
            PrimaryKey       = ["Id"],
            Data             = [],
        };

        var result = _gen.Generate(Ir(table));

        result.Should().NotContain("Records");
    }

    [Fact]
    public void Generate_RecordsBlock_FormatsValueTypesCorrectly()
    {
        var table = new TableDefinition
        {
            Name             = "dbo.Config",
            IsReferenceTable = true,
            Fields           =
            [
                Field("Id",      "int",          false, isPk: true),
                Field("Flag",    "bit",          false),
                Field("Score",   "decimal(5,2)", false),
                Field("Comment", "nvarchar(50)", true),
            ],
            PrimaryKey = ["Id"],
            Data       =
            [
                Row(("Id", 1), ("Flag", true), ("Score", 3.14), ("Comment", (object?)null)),
            ],
        };

        var result = _gen.Generate(Ir(table));

        result.Should().Contain("  1, true, 3.14, null");
    }

    [Fact]
    public void Generate_RecordsBlock_EscapesSingleQuotesInStrings()
    {
        var table = new TableDefinition
        {
            Name             = "dbo.T",
            IsReferenceTable = true,
            Fields           = [Field("Id", "int", false, isPk: true), Field("Label", "nvarchar(50)", false)],
            PrimaryKey       = ["Id"],
            Data             = [Row(("Id", 1), ("Label", "it's here"))],
        };

        var result = _gen.Generate(Ir(table));

        result.Should().Contain("'it''s here'");
    }

    [Fact]
    public void Generate_RecordsBlock_AppearsAfterTableBlock()
    {
        var table = new TableDefinition
        {
            Name             = "dbo.Role",
            IsReferenceTable = true,
            Fields           = [Field("Id", "int", false, isPk: true), Field("Name", "nvarchar(20)", false)],
            PrimaryKey       = ["Id"],
            Data             = [Row(("Id", 1), ("Name", "admin"))],
        };

        var result = _gen.Generate(Ir(table));

        var tableIdx   = result.IndexOf("Table dbo.Role", StringComparison.Ordinal);
        var recordsIdx = result.IndexOf("Records dbo.Role", StringComparison.Ordinal);
        tableIdx.Should().BeLessThan(recordsIdx);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ManifestRoot Ir(params TableDefinition[] tables)
        => new() { Tables = tables };

    private static TableDefinition Table(string name, params FieldDefinition[] fields)
        => new() { Name = name, Fields = fields, PrimaryKey = fields.Where(f => f.IsPrimaryKey).Select(f => f.Name).ToList() };

    private static TableDefinition Table(string name, string description)
        => new() { Name = name, Description = description, Fields = [], PrimaryKey = [] };

    private static FieldDefinition Field(
        string name, string type,
        bool nullable = false,
        bool isPk     = false,
        string description = "")
        => new() { Name = name, Type = type, Nullable = nullable, IsPrimaryKey = isPk, Description = description };

    private static ForeignKey Fk(string sourceField, string targetTable, string targetField)
        => new() { SourceField = sourceField, TargetTable = targetTable, TargetField = targetField, Kind = ForeignKeyKind.Physical };

    private static IReadOnlyDictionary<string, JsonElement> Row(params (string Key, object? Value)[] entries)
        => entries.ToDictionary(e => e.Key, e => JsonSerializer.SerializeToElement(e.Value));
}
