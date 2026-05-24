using FluentAssertions;
using Manifesta.Core.IR;
using Xunit;

namespace Manifesta.Core.Tests;

public sealed class IrModelTests
{
    // ── TableDefinition ───────────────────────────────────────────────────

    [Fact]
    public void TableDefinition_Equality_BasedOnValue()
    {
        var a = new TableDefinition { Name = "dbo.Customer", Description = "Customers" };
        var b = new TableDefinition { Name = "dbo.Customer", Description = "Customers" };

        a.Should().Be(b);
    }

    [Fact]
    public void TableDefinition_DifferentNames_NotEqual()
    {
        var a = new TableDefinition { Name = "dbo.Customer" };
        var b = new TableDefinition { Name = "dbo.Order" };

        a.Should().NotBe(b);
    }

    [Fact]
    public void TableDefinition_DefaultCollections_AreEmpty()
    {
        var t = new TableDefinition { Name = "dbo.Test" };

        t.Fields.Should().BeEmpty();
        t.PrimaryKey.Should().BeEmpty();
        t.ForeignKeys.Should().BeEmpty();
        t.DatabaseTypes.Should().BeEmpty();
        t.Sets.Should().BeEmpty();
        t.Sections.Should().BeEmpty();
    }

    // ── FieldDefinition ───────────────────────────────────────────────────

    [Fact]
    public void FieldDefinition_RequiredProperties_Set()
    {
        var f = new FieldDefinition { Name = "CustomerId", Type = "int" };
        f.Name.Should().Be("CustomerId");
        f.Type.Should().Be("int");
        f.Nullable.Should().BeFalse();  // default
    }

    [Fact]
    public void FieldDefinition_Default_IsNullByDefault()
    {
        var f = new FieldDefinition { Name = "Status", Type = "int" };
        f.Default.Should().BeNull();
    }

    [Fact]
    public void FieldDefinition_Default_CanBeSet()
    {
        var f = new FieldDefinition { Name = "Status", Type = "int", Default = "0" };
        f.Default.Should().Be("0");
    }

    [Fact]
    public void FieldDefinition_Default_IncludedInValueEquality()
    {
        var a = new FieldDefinition { Name = "Status", Type = "int", Default = "0" };
        var b = new FieldDefinition { Name = "Status", Type = "int", Default = "1" };
        var c = new FieldDefinition { Name = "Status", Type = "int", Default = "0" };

        a.Should().NotBe(b);
        a.Should().Be(c);
    }

    // ── ForeignKey ────────────────────────────────────────────────────────

    [Fact]
    public void ForeignKey_Equality()
    {
        var a = new ForeignKey { SourceField = "OrderId", TargetTable = "dbo.Order", TargetField = "Id" };
        var b = new ForeignKey { SourceField = "OrderId", TargetTable = "dbo.Order", TargetField = "Id" };
        a.Should().Be(b);
    }

    [Fact]
    public void ForeignKey_Kind_DefaultIsPhysical()
    {
        var fk = new ForeignKey { SourceField = "userId", TargetTable = "dbo.User", TargetField = "id" };
        fk.Kind.Should().Be(ForeignKeyKind.Physical);
    }

    [Fact]
    public void ForeignKey_Kind_CanBeSetToLogical()
    {
        var fk = new ForeignKey { SourceField = "userId", TargetTable = "dbo.User", TargetField = "id", Kind = ForeignKeyKind.Logical };
        fk.Kind.Should().Be(ForeignKeyKind.Logical);
    }

    [Fact]
    public void ForeignKey_Kind_CanBeSetToVirtual()
    {
        var fk = new ForeignKey { SourceField = "stateId", TargetTable = "dbo.State", TargetField = "id", Kind = ForeignKeyKind.Virtual };
        fk.Kind.Should().Be(ForeignKeyKind.Virtual);
    }

    [Fact]
    public void TableDefinition_LabelField_DefaultIsNull()
    {
        var table = new TableDefinition { Name = "dbo.State", Fields = [new FieldDefinition { Name = "Id", Type = "int" }] };
        table.LabelField.Should().BeNull();
    }

    [Fact]
    public void TableDefinition_LabelField_CanBeSet()
    {
        var table = new TableDefinition { Name = "dbo.State", Fields = [new FieldDefinition { Name = "Id", Type = "int" }], LabelField = "szState" };
        table.LabelField.Should().Be("szState");
    }

    [Fact]
    public void ForeignKey_Kind_IncludedInValueEquality()
    {
        var a = new ForeignKey { SourceField = "stateId", TargetTable = "dbo.State", TargetField = "id", Kind = ForeignKeyKind.Virtual };
        var b = new ForeignKey { SourceField = "stateId", TargetTable = "dbo.State", TargetField = "id", Kind = ForeignKeyKind.Virtual };
        var c = new ForeignKey { SourceField = "stateId", TargetTable = "dbo.State", TargetField = "id", Kind = ForeignKeyKind.Logical };

        a.Should().Be(b);
        a.Should().NotBe(c);
    }

    // ── ErdDefinition ─────────────────────────────────────────────────────

    [Fact]
    public void ErdDefinition_IncludeLogical_DefaultIsNull()
    {
        var erd = new ErdDefinition();
        erd.IncludeLogical.Should().BeNull();
    }

    [Fact]
    public void ErdDefinition_IncludeVirtual_DefaultIsFalse()
    {
        var erd = new ErdDefinition();
        erd.IncludeVirtual.Should().BeFalse();
    }

    [Fact]
    public void ErdDefinition_IncludeLogical_CanBeExplicitlyDisabled()
    {
        var erd = new ErdDefinition { IncludeLogical = false };
        erd.IncludeLogical.Should().BeFalse();
    }

    [Fact]
    public void ErdDefinition_IncludeVirtual_CanBeEnabled()
    {
        var erd = new ErdDefinition { IncludeVirtual = true };
        erd.IncludeVirtual.Should().BeTrue();
    }

    // ── ManifestRoot ──────────────────────────────────────────────────────

    [Fact]
    public void ManifestRoot_DefaultCollections_AreEmpty()
    {
        var root = new ManifestRoot();
        root.Tables.Should().BeEmpty();
        root.Apis.Should().BeEmpty();
        root.Sections.Should().BeEmpty();
    }

    [Fact]
    public void ManifestRoot_WithTables_IsImmutable()
    {
        var table = new TableDefinition { Name = "dbo.Test" };
        var root  = new ManifestRoot { Tables = [table] };

        root.Tables.Should().HaveCount(1);
        root.Tables[0].Should().Be(table);
    }

    // ── SchemaGraph ───────────────────────────────────────────────────────

    [Fact]
    public void SchemaGraph_DefaultEdges_AreEmpty()
    {
        var g = new SchemaGraph();
        g.Nodes.Should().BeEmpty();
        g.Edges.Should().BeEmpty();
    }

    // ── InferredRelationship ──────────────────────────────────────────────────

    [Fact]
    public void InferredRelationship_RequiredProperties_Set()
    {
        var r = new InferredRelationship
        {
            ChildTable   = "dbo.Order",
            ChildColumn  = "CustomerId",
            ParentTable  = "dbo.Customer",
            ParentColumn = "Id",
            Confidence   = 0.87,
            Signals      = new SignalScores { Naming = 0.9, Data = 0.8 },
            Status       = InferredRelationshipStatus.Pending,
        };

        r.ChildTable.Should().Be("dbo.Order");
        r.ChildColumn.Should().Be("CustomerId");
        r.ParentTable.Should().Be("dbo.Customer");
        r.ParentColumn.Should().Be("Id");
        r.Confidence.Should().Be(0.87);
        r.Status.Should().Be(InferredRelationshipStatus.Pending);
    }

    [Fact]
    public void InferredRelationship_MissingSignals_DefaultIsEmpty()
    {
        var r = new InferredRelationship
        {
            ChildTable   = "dbo.Order",
            ChildColumn  = "CustomerId",
            ParentTable  = "dbo.Customer",
            ParentColumn = "Id",
            Confidence   = 0.5,
            Signals      = new SignalScores(),
            Status       = InferredRelationshipStatus.Pending,
        };

        r.MissingSignals.Should().BeEmpty();
    }

    [Fact]
    public void InferredRelationship_AllStatusValues_Defined()
    {
        var values = Enum.GetValues<InferredRelationshipStatus>();
        values.Should().Contain(InferredRelationshipStatus.Pending);
        values.Should().Contain(InferredRelationshipStatus.Accepted);
        values.Should().Contain(InferredRelationshipStatus.Rejected);
    }

    [Fact]
    public void SignalScores_AllNullByDefault()
    {
        var s = new SignalScores();
        s.Naming.Should().BeNull();
        s.Data.Should().BeNull();
        s.Query.Should().BeNull();
        s.Semantic.Should().BeNull();
    }

    [Fact]
    public void SignalScores_Equality_BasedOnValue()
    {
        var a = new SignalScores { Naming = 0.9, Data = 0.7 };
        var b = new SignalScores { Naming = 0.9, Data = 0.7 };
        a.Should().Be(b);
    }

    [Fact]
    public void TableDefinition_InferredForeignKeys_DefaultIsEmpty()
    {
        var t = new TableDefinition { Name = "dbo.Test" };
        t.InferredForeignKeys.Should().BeEmpty();
    }

    [Fact]
    public void TableDefinition_InferredForeignKeys_CanBeSet()
    {
        var r = new InferredRelationship
        {
            ChildTable   = "dbo.Order",
            ChildColumn  = "CustomerId",
            ParentTable  = "dbo.Customer",
            ParentColumn = "Id",
            Confidence   = 0.92,
            Signals      = new SignalScores { Naming = 1.0 },
            Status       = InferredRelationshipStatus.Accepted,
        };

        var t = new TableDefinition { Name = "dbo.Order", InferredForeignKeys = [r] };
        t.InferredForeignKeys.Should().HaveCount(1);
        t.InferredForeignKeys[0].Should().Be(r);
    }
}
