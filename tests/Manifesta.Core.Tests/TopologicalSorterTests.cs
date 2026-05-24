using FluentAssertions;
using Manifesta.Core.IR;
using Xunit;

namespace Manifesta.Core.Tests;

public sealed class TopologicalSorterTests
{
    // ── Empty / trivial inputs ─────────────────────────────────────────────

    [Fact]
    public void Sort_EmptyList_ReturnsEmpty()
    {
        var result = TopologicalSorter.Sort([]);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Sort_SingleTable_NoFks_ReturnsSingleTable()
    {
        var table = Table("dbo.Foo");
        var result = TopologicalSorter.Sort([table]);
        result.Should().ContainSingle().Which.Name.Should().Be("dbo.Foo");
    }

    // ── Linear chains ─────────────────────────────────────────────────────

    [Fact]
    public void Sort_LinearChain_TargetBeforeSource()
    {
        // Order → Customer (Order depends on Customer)
        var customer = Table("dbo.Customer");
        var order    = Table("dbo.Order", Fk("CustomerId", "dbo.Customer", "Id"));

        var result = TopologicalSorter.Sort([order, customer]);

        result.Select(t => t.Name).Should().Equal("dbo.Customer", "dbo.Order");
    }

    [Fact]
    public void Sort_ThreeTableChain_FullyOrdered()
    {
        // Item → Order → Customer
        var customer = Table("dbo.Customer");
        var order    = Table("dbo.Order",    Fk("CustomerId", "dbo.Customer",  "Id"));
        var item     = Table("dbo.OrderItem", Fk("OrderId",   "dbo.Order",     "Id"));

        var result = TopologicalSorter.Sort([item, order, customer]);

        var names = result.Select(t => t.Name).ToList();
        names.IndexOf("dbo.Customer").Should().BeLessThan(names.IndexOf("dbo.Order"));
        names.IndexOf("dbo.Order").Should().BeLessThan(names.IndexOf("dbo.OrderItem"));
    }

    // ── Virtual FK filtering ───────────────────────────────────────────────

    [Fact]
    public void Sort_VirtualFkOnly_DoesNotCreateOrderingConstraint()
    {
        // B has a virtual FK to A — should not force A before B
        var a = Table("dbo.A");
        var b = Table("dbo.B", new ForeignKey
        {
            SourceField = "AId",
            TargetTable = "dbo.A",
            TargetField = "Id",
            Kind        = ForeignKeyKind.Virtual,
        });

        // Either order is valid; Sort must not throw
        var result = TopologicalSorter.Sort([b, a]);
        result.Should().HaveCount(2);
    }

    [Fact]
    public void Sort_LogicalFk_DoesCreateOrderingConstraint()
    {
        var a = Table("dbo.A");
        var b = Table("dbo.B", new ForeignKey
        {
            SourceField = "AId",
            TargetTable = "dbo.A",
            TargetField = "Id",
            Kind        = ForeignKeyKind.Logical,
        });

        var result = TopologicalSorter.Sort([b, a]);

        result.Select(t => t.Name).Should().Equal("dbo.A", "dbo.B");
    }

    // ── Self-reference ─────────────────────────────────────────────────────

    [Fact]
    public void Sort_SelfReferencingFk_DoesNotThrow()
    {
        var employee = Table("dbo.Employee", Fk("ManagerId", "dbo.Employee", "Id"));

        var act = () => TopologicalSorter.Sort([employee]);
        act.Should().NotThrow();
    }

    // ── FK target outside schema ───────────────────────────────────────────

    [Fact]
    public void Sort_FkTargetNotInSchema_IsIgnored()
    {
        // Order references an external Customer table not in this set
        var order = Table("dbo.Order", Fk("CustomerId", "ext.Customer", "Id"));

        var act = () => TopologicalSorter.Sort([order]);
        act.Should().NotThrow();
    }

    // ── Multiple FKs / fan-out ─────────────────────────────────────────────

    [Fact]
    public void Sort_TableWithTwoFks_BothParentsAppearFirst()
    {
        var customer = Table("dbo.Customer");
        var product  = Table("dbo.Product");
        var order    = Table("dbo.Order",
            Fk("CustomerId", "dbo.Customer", "Id"),
            Fk("ProductId",  "dbo.Product",  "Id"));

        var result = TopologicalSorter.Sort([order, customer, product]);

        var names = result.Select(t => t.Name).ToList();
        names.IndexOf("dbo.Customer").Should().BeLessThan(names.IndexOf("dbo.Order"));
        names.IndexOf("dbo.Product").Should().BeLessThan(names.IndexOf("dbo.Order"));
    }

    // ── Cycle detection ───────────────────────────────────────────────────

    [Fact]
    public void Sort_DirectCycle_ThrowsManifestaSchemException()
    {
        var a = Table("dbo.A", Fk("BId", "dbo.B", "Id"));
        var b = Table("dbo.B", Fk("AId", "dbo.A", "Id"));

        var act = () => TopologicalSorter.Sort([a, b]);
        act.Should().Throw<ManifestaSchemException>()
           .WithMessage("*Circular*");
    }

    [Fact]
    public void Sort_IndirectCycle_ThrowsManifestaSchemException()
    {
        var a = Table("dbo.A", Fk("BId", "dbo.B", "Id"));
        var b = Table("dbo.B", Fk("CId", "dbo.C", "Id"));
        var c = Table("dbo.C", Fk("AId", "dbo.A", "Id"));

        var act = () => TopologicalSorter.Sort([a, b, c]);
        act.Should().Throw<ManifestaSchemException>();
    }

    // ── Case-insensitivity ────────────────────────────────────────────────

    [Fact]
    public void Sort_FkTargetCaseDiffersFromTableName_StillResolved()
    {
        var customer = Table("dbo.Customer");
        var order    = Table("dbo.Order", Fk("CustomerId", "DBO.CUSTOMER", "Id"));

        var result = TopologicalSorter.Sort([order, customer]);

        result.Select(t => t.Name).Should().Equal("dbo.Customer", "dbo.Order");
    }

    // ── Result count ──────────────────────────────────────────────────────

    [Fact]
    public void Sort_ResultContainsAllInputTables()
    {
        var tables = Enumerable.Range(1, 10)
            .Select(i => Table($"dbo.T{i}"))
            .ToList();

        var result = TopologicalSorter.Sort(tables);

        result.Should().HaveCount(10);
        result.Select(t => t.Name).Should().BeEquivalentTo(tables.Select(t => t.Name));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static TableDefinition Table(string name, params ForeignKey[] fks) =>
        new() { Name = name, ForeignKeys = fks };

    private static ForeignKey Fk(string source, string targetTable, string targetField) =>
        new() { SourceField = source, TargetTable = targetTable, TargetField = targetField };
}
