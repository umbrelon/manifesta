using FluentAssertions;
using Manifesta.Core;
using Manifesta.Core.IR;
using Xunit;

namespace Manifesta.Core.Tests;

public sealed class DbmlParserTests
{
    private readonly DbmlParser _parser = new();

    // ── Table basics ──────────────────────────────────────────────────────────

    [Fact]
    public void Parse_SimpleTable_ProducesTableDefinition()
    {
        const string dbml = """
            Table Customer {
              Id   int  [pk, not null]
              Name nvarchar(100) [not null]
            }
            """;

        var result = _parser.Parse(dbml);

        result.Errors.Should().BeEmpty();
        result.Tables.Should().HaveCount(1);

        var table = result.Tables[0];
        table.Name.Should().Be("Customer");
        table.Fields.Should().HaveCount(2);
        table.Fields[0].Name.Should().Be("Id");
        table.Fields[0].Type.Should().Be("int");
        table.Fields[0].IsPrimaryKey.Should().BeTrue();
        table.Fields[0].Nullable.Should().BeFalse();
        table.Fields[1].Nullable.Should().BeFalse();
    }

    [Fact]
    public void Parse_NullableField_IsNullable()
    {
        const string dbml = """
            Table T {
              OptionalCol nvarchar(50)
            }
            """;

        var result = _parser.Parse(dbml);

        result.Tables[0].Fields[0].Nullable.Should().BeTrue();
    }

    [Fact]
    public void Parse_PrimaryKeyField_BuildsPrimaryKeyList()
    {
        const string dbml = """
            Table T {
              Id int [pk]
              Name nvarchar(50)
            }
            """;

        var result = _parser.Parse(dbml);

        result.Tables[0].PrimaryKey.Should().ContainSingle("Id");
    }

    [Fact]
    public void Parse_CompositePrimaryKey_BuildsAllPkFields()
    {
        const string dbml = """
            Table OrderItem {
              OrderId int [pk, not null]
              ItemId  int [pk, not null]
              Qty     int
            }
            """;

        var result = _parser.Parse(dbml);

        var pk = result.Tables[0].PrimaryKey;
        pk.Should().HaveCount(2);
        pk.Should().ContainInOrder("OrderId", "ItemId");
    }

    // ── Table Note ────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_TableNote_SetsDescription()
    {
        const string dbml = """
            Table Customer {
              Id int [pk]

              Note: "Stores all customers"
            }
            """;

        var result = _parser.Parse(dbml);

        result.Tables[0].Description.Should().Be("Stores all customers");
    }

    // ── Field Notes ───────────────────────────────────────────────────────────

    [Fact]
    public void Parse_FieldNote_SetsFieldDescription()
    {
        const string dbml = """
            Table T {
              Id int [pk, note: "Primary key of the table"]
            }
            """;

        var result = _parser.Parse(dbml);

        result.Tables[0].Fields[0].Description.Should().Be("Primary key of the table");
    }

    // ── Schema prefix ─────────────────────────────────────────────────────────

    [Fact]
    public void Parse_WithSchemaPrefix_PrefixesUnqualifiedTables()
    {
        const string dbml = """
            Table Customer {
              Id int [pk]
            }
            """;

        var result = _parser.Parse(dbml, schemaPrefix: "dbo");

        result.Tables[0].Name.Should().Be("dbo.Customer");
    }

    [Fact]
    public void Parse_WithSchemaPrefix_DoesNotPrefixAlreadyQualified()
    {
        const string dbml = """
            Table app.Customer {
              Id int [pk]
            }
            """;

        var result = _parser.Parse(dbml, schemaPrefix: "dbo");

        result.Tables[0].Name.Should().Be("app.Customer");
    }

    // ── Refs ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_RefGreaterThan_AddsPhysicalFkToSourceTable()
    {
        const string dbml = """
            Table Order {
              Id         int [pk]
              CustomerId int
            }

            Table Customer {
              Id int [pk]
            }

            Ref: Order.CustomerId > Customer.Id
            """;

        var result = _parser.Parse(dbml);

        result.Errors.Should().BeEmpty();
        var order = result.Tables.Single(t => t.Name == "Order");
        order.ForeignKeys.Should().HaveCount(1);
        var fk = order.ForeignKeys[0];
        fk.SourceField.Should().Be("CustomerId");
        fk.TargetTable.Should().Be("Customer");
        fk.TargetField.Should().Be("Id");
        fk.Kind.Should().Be(ForeignKeyKind.Physical);
    }

    [Fact]
    public void Parse_RefLessThan_AddsPhysicalFkToRightTable()
    {
        const string dbml = """
            Table Customer {
              Id int [pk]
            }

            Table Order {
              Id         int [pk]
              CustomerId int
            }

            Ref: Customer.Id < Order.CustomerId
            """;

        var result = _parser.Parse(dbml);

        result.Errors.Should().BeEmpty();
        var order = result.Tables.Single(t => t.Name == "Order");
        order.ForeignKeys.Should().HaveCount(1);
        var fk = order.ForeignKeys[0];
        fk.SourceField.Should().Be("CustomerId");
        fk.TargetTable.Should().Be("Customer");
        fk.TargetField.Should().Be("Id");
    }

    [Fact]
    public void Parse_RefWithLogicalComment_SetsLogicalKind()
    {
        const string dbml = """
            Table A { Id int [pk] Ref int }
            Table B { Id int [pk] }
            Ref: A.Ref > B.Id // logical
            """;

        var result = _parser.Parse(dbml);

        result.Tables.Single(t => t.Name == "A").ForeignKeys[0].Kind
            .Should().Be(ForeignKeyKind.Logical);
    }

    [Fact]
    public void Parse_RefWithVirtualComment_SetsVirtualKind()
    {
        const string dbml = """
            Table A { Id int [pk] Ref int }
            Table B { Id int [pk] }
            Ref: A.Ref > B.Id // virtual
            """;

        var result = _parser.Parse(dbml);

        result.Tables.Single(t => t.Name == "A").ForeignKeys[0].Kind
            .Should().Be(ForeignKeyKind.Virtual);
    }

    [Fact]
    public void Parse_RefWithSchemaQualifiedNames_ResolvesCorrectly()
    {
        const string dbml = """
            Table dbo.Order {
              Id         int [pk]
              CustomerId int
            }

            Table dbo.Customer {
              Id int [pk]
            }

            Ref: dbo.Order.CustomerId > dbo.Customer.Id
            """;

        var result = _parser.Parse(dbml);

        var order = result.Tables.Single(t => t.Name == "dbo.Order");
        var fk    = order.ForeignKeys.Should().ContainSingle().Subject;
        fk.TargetTable.Should().Be("dbo.Customer");
    }

    // ── TableGroup / Sections ─────────────────────────────────────────────────

    [Fact]
    public void Parse_TableGroup_ProducesSection()
    {
        const string dbml = """
            Table A { Id int [pk] }
            Table B { Id int [pk] }

            TableGroup Core {
              A
              B
            }
            """;

        var result = _parser.Parse(dbml);

        result.Sections.Should().HaveCount(1);
        result.Sections[0].Name.Should().Be("Core");
        result.Sections[0].Tables.Should().ContainInOrder("A", "B");
    }

    // ── Computed column round-trip ────────────────────────────────────────────

    [Fact]
    public void Parse_CalculatedNoteWithPersisted_SetsComputedProperties()
    {
        const string dbml = """
            Table dbo.OrderItem {
              TotalWithTax decimal(10,2) [not null, note: "// calculated: ([UnitPrice]*1.21) PERSISTED"]
            }
            """;

        var result = _parser.Parse(dbml);

        var field = result.Tables[0].Fields[0];
        field.IsComputed.Should().BeTrue();
        field.ComputedExpression.Should().Be("[UnitPrice]*1.21");
        field.IsPersisted.Should().BeTrue();
        field.Description.Should().BeEmpty();
    }

    [Fact]
    public void Parse_CalculatedNoteWithoutPersisted_SetsNotPersisted()
    {
        const string dbml = """
            Table T {
              Display nvarchar(200) [note: "// calculated: ([First]+' '+[Last])"]
            }
            """;

        var result = _parser.Parse(dbml);

        var field = result.Tables[0].Fields[0];
        field.IsComputed.Should().BeTrue();
        field.ComputedExpression.Should().Be("[First]+' '+[Last]");
        field.IsPersisted.Should().BeFalse();
    }

    [Fact]
    public void Parse_CalculatedNoteWithDescription_SetsDescriptionAndCalc()
    {
        const string dbml = """
            Table T {
              Total decimal [note: "Calculated total; // calculated: ([Price]*[Qty])"]
            }
            """;

        var result = _parser.Parse(dbml);

        var field = result.Tables[0].Fields[0];
        field.Description.Should().Be("Calculated total");
        field.IsComputed.Should().BeTrue();
        field.ComputedExpression.Should().Be("[Price]*[Qty]");
    }

    // ── Error handling ────────────────────────────────────────────────────────

    [Fact]
    public void Parse_RefWithUnknownSourceTable_AddsError()
    {
        const string dbml = """
            Table Customer {
              Id int [pk]
            }

            Ref: NonExistent.CustomerId > Customer.Id
            """;

        var result = _parser.Parse(dbml);

        result.Errors.Should().ContainSingle(e => e.Contains("NonExistent"));
    }

    [Fact]
    public void Parse_RefWithUnknownTargetTable_AddsFkWithoutError()
    {
        // Target table may be defined elsewhere (partial import). FK is still recorded.
        const string dbml = """
            Table Order {
              Id         int [pk]
              CustomerId int
            }

            Ref: Order.CustomerId > NonExistent.Id
            """;

        var result = _parser.Parse(dbml);

        result.Errors.Should().BeEmpty();
        var order = result.Tables.Single(t => t.Name == "Order");
        order.ForeignKeys.Should().HaveCount(1);
        order.ForeignKeys[0].TargetTable.Should().Be("NonExistent");
    }

    [Fact]
    public void Parse_EmptyInput_ProducesNoTablesNoErrors()
    {
        var result = _parser.Parse("");

        result.Tables.Should().BeEmpty();
        result.Sections.Should().BeEmpty();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Parse_MultipleTablesAndRefs_ProducesAllTablesWithFks()
    {
        const string dbml = """
            Table Category {
              Id   int          [pk, not null]
              Name nvarchar(50) [not null]
            }

            Table Product {
              Id         int           [pk, not null]
              CategoryId int           [not null]
              Name       nvarchar(200) [not null]
            }

            Ref: Product.CategoryId > Category.Id
            """;

        var result = _parser.Parse(dbml);

        result.Errors.Should().BeEmpty();
        result.Tables.Should().HaveCount(2);

        var product = result.Tables.Single(t => t.Name == "Product");
        product.ForeignKeys.Should().HaveCount(1);
        product.ForeignKeys[0].TargetTable.Should().Be("Category");
    }

    // ── Inline comment stripping ──────────────────────────────────────────────

    [Fact]
    public void Parse_InlineLineComment_IsIgnored()
    {
        const string dbml = """
            Table T {
              Id int [pk] // this is a comment
              Name nvarchar(50)
            }
            """;

        var result = _parser.Parse(dbml);

        result.Tables[0].Fields.Should().HaveCount(2);
        result.Tables[0].Fields[0].Name.Should().Be("Id");
    }

    [Fact]
    public void Parse_NoteWithDoubleSlashInsideQuote_NotStripped()
    {
        const string dbml = """
            Table T {
              Id int [pk, note: "See https://example.com"]
            }
            """;

        var result = _parser.Parse(dbml);

        // The // inside the note string should not cause the note to be stripped
        result.Tables[0].Fields[0].Description.Should().Be("See https://example.com");
    }

}
