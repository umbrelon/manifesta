using FluentAssertions;
using Manifesta.Core.IR;
using Manifesta.Core.Pipeline;
using Xunit;

namespace Manifesta.Core.Tests;

public sealed class CrossValidatorTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    private static TableDefinition Table(
        string name,
        IReadOnlyList<ForeignKey>? fks = null,
        IReadOnlyList<string>?     sections = null,
        IReadOnlyList<string>?     fields = null,
        string?                    labelField = null) => new()
    {
        Name       = name,
        SourceFile = $"tables/{name}.json",
        Fields     = (fields ?? ["Id"])
                         .Select(f => new FieldDefinition { Name = f, Type = "int" })
                         .ToList(),
        ForeignKeys = fks ?? [],
        Sections    = sections ?? [],
        LabelField  = labelField,
    };

    private static SectionDefinition Section(string name, IReadOnlyList<string>? tables = null) => new()
    {
        Name       = name,
        SourceFile = $"sections/{name}.json",
        Tables     = tables ?? [],
    };

    private static ApiDefinition Api(
        string name,
        IReadOnlyList<EndpointDefinition>? endpoints = null,
        IReadOnlyList<string>?             dbTypes   = null) => new()
    {
        Name       = name,
        Title      = name,
        Version    = "1.0",
        SourceFile = $"apis/{name}.json",
        Endpoints  = endpoints ?? [],
        DatabaseTypes = dbTypes ?? [],
    };

    private static EndpointDefinition Endpoint(
        string path,
        string method   = "GET",
        string? dbTable = null,
        IReadOnlyList<string>? dbTypes = null) => new()
    {
        Path      = path,
        Method    = method,
        DbTable   = dbTable,
        DatabaseTypes = dbTypes ?? [],
    };

    private static ForeignKey Fk(string source, string targetTable, string targetField) => new()
    {
        SourceField = source,
        TargetTable = targetTable,
        TargetField = targetField,
    };

    private readonly CrossValidator _sut = new();

    // ── Clean scenarios ────────────────────────────────────────────────────

    [Fact]
    public void Clean_NoTablesNoSectionsNoApis_ProducesNoIssues()
    {
        var result = _sut.Validate([], [], []);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Clean_SingleTableNoFkNoSection_ProducesNoIssues()
    {
        var result = _sut.Validate([Table("dbo.Customer")], [], []);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Clean_FkTargetExists_ProducesNoIssues()
    {
        var customer = Table("dbo.Customer", fields: ["Id", "Name"]);
        var order    = Table("dbo.Order",    fks: [Fk("CustomerId", "dbo.Customer", "Id")]);

        var result = _sut.Validate([customer, order], [], []);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Clean_TableLabelFieldSet_ProducesNoIssues()
    {
        // LabelField validation is handled by TableValidator; CrossValidator does not re-check it.
        var customer = Table("dbo.Customer", fields: ["Id", "Name"], labelField: "Name");
        var order    = Table("dbo.Order",    fks: [Fk("CustomerId", "dbo.Customer", "Id")]);

        var result = _sut.Validate([customer, order], [], []);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Clean_SectionExistsForTable_ProducesNoIssues()
    {
        var customer = Table("dbo.Customer", sections: ["crm"]);
        var section  = Section("crm", tables: ["dbo.Customer"]);

        var result = _sut.Validate([customer], [section], []);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Clean_SectionTableReferenceExists_ProducesNoIssues()
    {
        var customer = Table("dbo.Customer");
        var section  = Section("crm", tables: ["dbo.Customer"]);

        var result = _sut.Validate([customer], [section], []);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Clean_FkTargetCheck_IsCaseInsensitive()
    {
        var customer = Table("DBO.Customer", fields: ["Id"]);
        var order    = Table("dbo.Order", fks: [Fk("CustId", "dbo.customer", "Id")]);

        var result = _sut.Validate([customer, order], [], []);
        result.Issues.Should().BeEmpty();
    }

    // ── FK-TARGET-MISSING ──────────────────────────────────────────────────

    [Fact]
    public void FkTargetMissing_EmitsError()
    {
        var order = Table("dbo.Order", fks: [Fk("CustomerId", "dbo.Customer", "Id")]);

        var result = _sut.Validate([order], [], []);

        result.HasErrors.Should().BeTrue();
        result.Issues.Should().ContainSingle(i => i.Code == "FK-TARGET-MISSING");
    }

    [Fact]
    public void FkTargetMissing_Issue_ContainsSourceAndTargetNames()
    {
        var order = Table("dbo.Order", fks: [Fk("CustomerId", "dbo.Customer", "Id")]);

        var issue = _sut.Validate([order], [], []).Issues.Single();

        issue.Message.Should().Contain("dbo.Order");
        issue.Message.Should().Contain("dbo.Customer");
        issue.Message.Should().Contain("CustomerId");
    }

    [Fact]
    public void FkTargetMissing_Issue_IncludesSourceFile()
    {
        var order = Table("dbo.Order", fks: [Fk("CustomerId", "dbo.MissingTable", "Id")]);

        var issue = _sut.Validate([order], [], []).Issues.Single();

        issue.File.Should().Be("tables/dbo.Order.json");
    }

    [Fact]
    public void FkTargetMissing_MultipleTables_EachEmitsOwnError()
    {
        var a = Table("dbo.A", fks: [Fk("X", "dbo.Missing1", "Id")]);
        var b = Table("dbo.B", fks: [Fk("Y", "dbo.Missing2", "Id")]);

        var result = _sut.Validate([a, b], [], []);

        result.Issues.Should().HaveCount(2);
        result.Issues.Should().OnlyContain(i => i.Code == "FK-TARGET-MISSING");
    }

    // ── LabelField (table-level) ───────────────────────────────────────────
    // CrossValidator no longer checks labelField — that is handled by TableValidator.

    [Fact]
    public void TableLabelField_CrossValidator_ProducesNoLabelIssues()
    {
        // Even if a labelField names a non-existent field, CrossValidator stays silent.
        var customer = Table("dbo.Customer", fields: ["Id"], labelField: "NonExistent");
        var order    = Table("dbo.Order", fks: [Fk("CustId", "dbo.Customer", "Id")]);

        var result = _sut.Validate([customer, order], [], []);

        result.Issues.Should().NotContain(i => i.Code == "TABLE-LABEL-FIELD-MISSING");
        result.Issues.Should().NotContain(i => i.Code == "FK-LABEL-FIELD-MISSING");
    }

    // ── SECTION-UNDEFINED ─────────────────────────────────────────────────

    [Fact]
    public void SectionUndefined_EmitsWarning()
    {
        var table = Table("dbo.Customer", sections: ["crm"]);

        var result = _sut.Validate([table], [], []);

        result.HasWarnings.Should().BeTrue();
        result.Issues.Should().ContainSingle(i => i.Code == "SECTION-UNDEFINED");
    }

    [Fact]
    public void SectionUndefined_SectionExists_ProducesNoIssue()
    {
        var table   = Table("dbo.Customer", sections: ["crm"]);
        var section = Section("crm");

        var result = _sut.Validate([table], [section], []);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void SectionUndefined_IsCaseInsensitive()
    {
        var table   = Table("dbo.Customer", sections: ["CRM"]);
        var section = Section("crm");

        var result = _sut.Validate([table], [section], []);
        result.Issues.Should().BeEmpty();
    }

    // ── SECTION-TABLE-UNDEFINED ───────────────────────────────────────────

    [Fact]
    public void SectionTableUndefined_EmitsWarning()
    {
        var section = Section("crm", tables: ["dbo.Customer"]);

        var result = _sut.Validate([], [section], []);

        result.HasWarnings.Should().BeTrue();
        result.Issues.Should().ContainSingle(i => i.Code == "SECTION-TABLE-UNDEFINED");
    }

    [Fact]
    public void SectionTableUndefined_TableExists_ProducesNoIssue()
    {
        var customer = Table("dbo.Customer");
        var section  = Section("crm", tables: ["dbo.Customer"]);

        var result = _sut.Validate([customer], [section], []);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void SectionTableUndefined_IsCaseInsensitive()
    {
        var customer = Table("DBO.Customer");
        var section  = Section("crm", tables: ["dbo.customer"]);

        var result = _sut.Validate([customer], [section], []);
        result.Issues.Should().BeEmpty();
    }

    // ── API-DBTABLE-MISSING ───────────────────────────────────────────────

    [Fact]
    public void ApiDbTableMissing_EmitsError()
    {
        var api = Api("MyApi", endpoints: [Endpoint("/orders", dbTable: "dbo.Order")]);

        var result = _sut.Validate([], [], [api]);

        result.HasErrors.Should().BeTrue();
        result.Issues.Should().ContainSingle(i => i.Code == "API-DBTABLE-MISSING");
    }

    [Fact]
    public void ApiDbTableMissing_TableExists_ProducesNoIssue()
    {
        var order = Table("dbo.Order");
        var api   = Api("MyApi", endpoints: [Endpoint("/orders", dbTable: "dbo.Order")]);

        var result = _sut.Validate([order], [], [api]);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void ApiDbTableMissing_NullDbTable_ProducesNoIssue()
    {
        var api = Api("MyApi", endpoints: [Endpoint("/health", dbTable: null)]);

        var result = _sut.Validate([], [], [api]);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void ApiDbTableMissing_Issue_ContainsApiAndTableName()
    {
        var api = Api("MyApi", endpoints: [Endpoint("/orders", dbTable: "dbo.Missing")]);

        var issue = _sut.Validate([], [], [api]).Issues.Single();

        issue.Message.Should().Contain("MyApi");
        issue.Message.Should().Contain("dbo.Missing");
    }

    // ── API-DBTYPE-MISMATCH ───────────────────────────────────────────────

    [Fact]
    public void ApiDbTypeMismatch_EmitsWarning()
    {
        var order = Table("dbo.Order", fields: ["Id"]) with { DatabaseTypes = ["sqlserver"] };
        var api   = Api("MyApi", endpoints: [
            Endpoint("/orders", dbTable: "dbo.Order", dbTypes: ["mysql"])
        ]);

        var result = _sut.Validate([order], [], [api]);

        result.HasWarnings.Should().BeTrue();
        result.Issues.Should().ContainSingle(i => i.Code == "API-DBTYPE-MISMATCH");
    }

    [Fact]
    public void ApiDbTypeMismatch_TypeMatches_ProducesNoIssue()
    {
        var order = Table("dbo.Order", fields: ["Id"]) with { DatabaseTypes = ["sqlserver"] };
        var api   = Api("MyApi", endpoints: [
            Endpoint("/orders", dbTable: "dbo.Order", dbTypes: ["sqlserver"])
        ]);

        var result = _sut.Validate([order], [], [api]);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void ApiDbTypeMismatch_TableHasNoDbTypes_ProducesNoIssue()
    {
        // Table with no databaseTypes → skip the mismatch check
        var order = Table("dbo.Order", fields: ["Id"]);
        var api   = Api("MyApi", endpoints: [
            Endpoint("/orders", dbTable: "dbo.Order", dbTypes: ["mysql"])
        ]);

        var result = _sut.Validate([order], [], [api]);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void ApiDbTypeMismatch_IsCaseInsensitive()
    {
        var order = Table("dbo.Order", fields: ["Id"]) with { DatabaseTypes = ["SQLServer"] };
        var api   = Api("MyApi", endpoints: [
            Endpoint("/orders", dbTable: "dbo.Order", dbTypes: ["sqlserver"])
        ]);

        var result = _sut.Validate([order], [], [api]);
        result.Issues.Should().BeEmpty();
    }

    // ── Combined / integration ─────────────────────────────────────────────

    [Fact]
    public void MultipleIssues_AllReported()
    {
        // FK target missing (error) + section undefined (warning)
        var table = Table("dbo.Order",
            fks: [Fk("CustId", "dbo.Customer", "Id")],
            sections: ["crm"]);

        var result = _sut.Validate([table], [], []);

        result.Issues.Should().HaveCount(2);
        result.Issues.Should().Contain(i => i.Code == "FK-TARGET-MISSING");
        result.Issues.Should().Contain(i => i.Code == "SECTION-UNDEFINED");
    }

    [Fact]
    public void HasDrift_ReflectsErrorPresence()
    {
        var order = Table("dbo.Order", fks: [Fk("CustId", "dbo.MissingTable", "Id")]);

        var result = _sut.Validate([order], [], []);

        result.HasErrors.Should().BeTrue();
        result.HasWarnings.Should().BeFalse();
    }
}
