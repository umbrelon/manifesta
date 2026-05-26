using FluentAssertions;
using Manifesta.Core;
using Manifesta.Core.IR;
using Manifesta.Doc;
using Xunit;

namespace Manifesta.Doc.Tests;

/// <summary>
/// Tests for <see cref="ErdGenerator"/>.
/// Covers Mermaid ERD generation: relationships, entity declarations,
/// field modes, scope filtering, soft FK behaviour, and title rendering.
/// </summary>
public sealed class ErdGeneratorTests
{
    private readonly ErdGenerator _gen = new();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static TableDefinition MakeTable(
        string name,
        IEnumerable<FieldDefinition>? fields = null,
        IEnumerable<string>? pk = null,
        IEnumerable<ForeignKey>? fks = null) =>
        new()
        {
            Name        = name,
            Fields      = (fields ?? [new FieldDefinition { Name = "id", Type = "int", Nullable = false }]).ToList().AsReadOnly(),
            PrimaryKey  = (pk    ?? ["id"]).ToList().AsReadOnly(),
            ForeignKeys = (fks   ?? []).ToList().AsReadOnly(),
        };

    private static Dictionary<string, TableDefinition> ToDict(params TableDefinition[] tables) =>
        tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

    // ── Relationship rendering ────────────────────────────────────────────────

    [Fact]
    public void Generate_TablesWithFk_RendersRelationshipLine()
    {
        var parent = MakeTable("dbo.Reseller");
        var child  = MakeTable("dbo.ResellerId",
            fields: [
                new() { Name = "Id",          Type = "uniqueidentifier", Nullable = false },
                new() { Name = "lResellerId", Type = "int",              Nullable = false }
            ],
            pk: ["Id"],
            fks: [new() { SourceField = "lResellerId", TargetTable = "dbo.Reseller", TargetField = "lResellerID" }]);

        var erd    = new ErdDefinition { Tables = ["dbo.Reseller", "dbo.ResellerId"] };
        var result = _gen.Generate(erd, ["dbo.Reseller", "dbo.ResellerId"], ToDict(parent, child));

        // SourceField "lResellerId" and TargetField "lResellerID" differ only by case → same label
        result.Should().Contain("\"dbo.Reseller\" ||--o{ \"dbo.ResellerId\" : \"lResellerId\"");
    }

    [Fact]
    public void Generate_FkTargetNotInErd_RelationshipOmitted()
    {
        var child   = MakeTable("dbo.ResellerId",
            fields: [new() { Name = "lResellerId", Type = "int", Nullable = false }],
            pk: [],
            fks: [new() { SourceField = "lResellerId", TargetTable = "dbo.Reseller", TargetField = "lResellerID" }]);

        // dbo.Reseller is NOT included in the ERD table list
        var erd    = new ErdDefinition { Tables = ["dbo.ResellerId"] };
        var result = _gen.Generate(erd, ["dbo.Reseller", "dbo.ResellerId"], ToDict(child));

        result.Should().NotContain("||--o{");
    }

    // ── Field mode: none ─────────────────────────────────────────────────────

    [Fact]
    public void Generate_FieldsNone_NoAttributeBlocks()
    {
        var parent = MakeTable("dbo.Reseller",
            fields: [new() { Name = "lResellerID", Type = "int", Nullable = false }],
            pk: ["lResellerID"]);
        var child = MakeTable("dbo.ResellerId",
            fields: [new() { Name = "lResellerId", Type = "int", Nullable = false }],
            fks: [new() { SourceField = "lResellerId", TargetTable = "dbo.Reseller", TargetField = "lResellerID" }]);

        var erd    = new ErdDefinition { Tables = ["dbo.Reseller", "dbo.ResellerId"], Fields = ErdFields.None };
        var result = _gen.Generate(erd, ["dbo.Reseller", "dbo.ResellerId"], ToDict(parent, child));

        result.Should().Contain("||--o{"); // relationship still rendered
        result.Should().NotContain(" {");  // no entity attribute blocks (e.g. "\"dbo.Reseller\" {")
    }

    [Fact]
    public void Generate_FieldsNone_IsolatedTable_DeclaredExplicitly()
    {
        var isolated = MakeTable("dbo.CallMode",
            fields: [new() { Name = "id", Type = "int", Nullable = false }]);

        var erd    = new ErdDefinition { Tables = ["dbo.CallMode"], Fields = ErdFields.None };
        var result = _gen.Generate(erd, ["dbo.CallMode"], ToDict(isolated));

        // No relationships, so entity must be declared standalone
        result.Should().Contain("\"dbo.CallMode\"");
        // Still no attribute block
        result.Should().NotContain("\"dbo.CallMode\" {");
    }

    // ── Field mode: pk-and-fk ────────────────────────────────────────────────

    [Fact]
    public void Generate_FieldsPkAndFk_OnlyPkAndFkFieldsInBlock()
    {
        var table = MakeTable("dbo.Reseller",
            fields: [
                new() { Name = "lResellerID", Type = "int",          Nullable = false },
                new() { Name = "szName",      Type = "varchar(255)",  Nullable = false },
                new() { Name = "lParentID",   Type = "int",           Nullable = true  },
            ],
            pk: ["lResellerID"],
            fks: [new() { SourceField = "lParentID", TargetTable = "dbo.Reseller", TargetField = "lResellerID" }]);

        var erd    = new ErdDefinition { Tables = ["dbo.Reseller"], Fields = ErdFields.PkAndFk };
        var result = _gen.Generate(erd, ["dbo.Reseller"], ToDict(table));

        result.Should().Contain("lResellerID PK");
        result.Should().Contain("lParentID FK");
        result.Should().NotContain("szName"); // regular field excluded
    }

    [Fact]
    public void Generate_FieldsPkAndFk_IsDefault()
    {
        var table = MakeTable("dbo.Reseller",
            fields: [
                new() { Name = "lResellerID", Type = "int",         Nullable = false },
                new() { Name = "szName",      Type = "varchar(255)", Nullable = false },
            ],
            pk: ["lResellerID"]);

        // No Fields property set — should default to pk-and-fk
        var erd    = new ErdDefinition { Tables = ["dbo.Reseller"] };
        var result = _gen.Generate(erd, ["dbo.Reseller"], ToDict(table));

        result.Should().Contain("lResellerID PK");
        result.Should().NotContain("szName");
    }

    // ── Field mode: all ──────────────────────────────────────────────────────

    [Fact]
    public void Generate_FieldsAll_AllFieldsRendered()
    {
        var table = MakeTable("dbo.Reseller",
            fields: [
                new() { Name = "lResellerID", Type = "int",          Nullable = false },
                new() { Name = "szName",      Type = "varchar(255)",  Nullable = false },
                new() { Name = "szServer",    Type = "varchar(255)",  Nullable = true  },
            ],
            pk: ["lResellerID"]);

        var erd    = new ErdDefinition { Tables = ["dbo.Reseller"], Fields = ErdFields.All };
        var result = _gen.Generate(erd, ["dbo.Reseller"], ToDict(table));

        result.Should().Contain("lResellerID PK");
        result.Should().Contain("varchar szName");
        result.Should().Contain("varchar szServer");
    }

    // ── Empty tables list falls back to section tables ────────────────────────

    [Fact]
    public void Generate_EmptyTableList_UsesSectionTables()
    {
        var t1 = MakeTable("dbo.Reseller");
        var t2 = MakeTable("dbo.ResellerId",
            fks: [new() { SourceField = "lResellerId", TargetTable = "dbo.Reseller", TargetField = "lResellerID" }]);

        // erd.Tables is empty → should fall back to sectionTables
        var erd    = new ErdDefinition();
        var result = _gen.Generate(erd, ["dbo.Reseller", "dbo.ResellerId"], ToDict(t1, t2));

        result.Should().Contain("\"dbo.Reseller\"");
        result.Should().Contain("\"dbo.ResellerId\"");
    }

    // ── Title ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_TitlePresent_TitleRenderedAboveDiagram()
    {
        var table  = MakeTable("dbo.Reseller");
        var erd    = new ErdDefinition { Tables = ["dbo.Reseller"], Title = "Core identity" };
        var result = _gen.Generate(erd, ["dbo.Reseller"], ToDict(table));

        result.Should().StartWith("**Core identity**");
        result.Should().Contain("```mermaid");
    }

    [Fact]
    public void Generate_NoTitle_NoTitleLine()
    {
        var table  = MakeTable("dbo.Reseller");
        var erd    = new ErdDefinition { Tables = ["dbo.Reseller"] };
        var result = _gen.Generate(erd, ["dbo.Reseller"], ToDict(table));

        result.Should().StartWith("```mermaid");
    }

    // ── direction LR ─────────────────────────────────────────────────────────

    [Fact]
    public void Generate_AlwaysEmitsDirectionLR()
    {
        var table  = MakeTable("dbo.Reseller");
        var erd    = new ErdDefinition { Tables = ["dbo.Reseller"] };
        var result = _gen.Generate(erd, ["dbo.Reseller"], ToDict(table));

        result.Should().Contain("erDiagram\ndirection LR");
    }

    // ── Markdown dialect fence ───────────────────────────────────────────────

    [Fact]
    public void Generate_DialectAzureDevOps_UsesColonFence()
    {
        var table  = MakeTable("dbo.Reseller");
        var erd    = new ErdDefinition { Tables = ["dbo.Reseller"] };
        var result = _gen.Generate(erd, ["dbo.Reseller"], ToDict(table), dialect: MarkdownDialect.AzureDevOps);

        result.Should().Contain(":::mermaid");
        result.Should().EndWith(":::");
        result.Should().NotContain("```");
    }

    [Fact]
    public void Generate_DialectCommonMark_UsesBacktickFence()
    {
        var table  = MakeTable("dbo.Reseller");
        var erd    = new ErdDefinition { Tables = ["dbo.Reseller"] };
        var result = _gen.Generate(erd, ["dbo.Reseller"], ToDict(table), dialect: MarkdownDialect.CommonMark);

        result.Should().Contain("```mermaid");
        result.Should().EndWith("```");
        result.Should().NotContain(":::");
    }

    [Fact]
    public void Generate_DefaultDialect_IsCommonMark()
    {
        var table  = MakeTable("dbo.Reseller");
        var erd    = new ErdDefinition { Tables = ["dbo.Reseller"] };
        var result = _gen.Generate(erd, ["dbo.Reseller"], ToDict(table));

        result.Should().Contain("```mermaid");
        result.Should().EndWith("```");
        result.Should().NotContain(":::");
    }

    // ── FK kind rendering ────────────────────────────────────────────────────

    [Fact]
    public void Generate_VirtualFkExcludedByDefault()
    {
        var parent = MakeTable("dbo.Reseller");
        var child  = MakeTable("dbo.WholesaleMapping",
            fks: [new() { SourceField = "resellerId", TargetTable = "dbo.Reseller", TargetField = "lResellerID", Kind = ForeignKeyKind.Virtual }]);

        var erd    = new ErdDefinition { Tables = ["dbo.Reseller", "dbo.WholesaleMapping"] };
        var result = _gen.Generate(erd, ["dbo.Reseller", "dbo.WholesaleMapping"], ToDict(parent, child));

        result.Should().NotContain("||--o{");
    }

    [Fact]
    public void Generate_LogicalFkIncludedByDefault()
    {
        var parent = MakeTable("dbo.Reseller");
        var child  = MakeTable("dbo.WholesaleMapping",
            fks: [new() { SourceField = "resellerId", TargetTable = "dbo.Reseller", TargetField = "lResellerID", Kind = ForeignKeyKind.Logical }]);

        var erd    = new ErdDefinition { Tables = ["dbo.Reseller", "dbo.WholesaleMapping"] };
        var result = _gen.Generate(erd, ["dbo.Reseller", "dbo.WholesaleMapping"], ToDict(parent, child));

        // "resellerId" maps to "lResellerID" — different names, label shows both sides
        result.Should().Contain("\"dbo.Reseller\" ||--o{ \"dbo.WholesaleMapping\" : \"resellerId -> lResellerID\"");
    }

    [Fact]
    public void Generate_LogicalFkExcludedWhenNoLogicalSet()
    {
        var parent = MakeTable("dbo.Reseller");
        var child  = MakeTable("dbo.WholesaleMapping",
            fks: [new() { SourceField = "resellerId", TargetTable = "dbo.Reseller", TargetField = "lResellerID", Kind = ForeignKeyKind.Logical }]);

        var erd    = new ErdDefinition { Tables = ["dbo.Reseller", "dbo.WholesaleMapping"], IncludeLogical = false };
        var result = _gen.Generate(erd, ["dbo.Reseller", "dbo.WholesaleMapping"], ToDict(parent, child));

        result.Should().NotContain("||--o{");
    }

    [Fact]
    public void Generate_VirtualFkIncludedWhenIncludeVirtualIsTrue()
    {
        var parent = MakeTable("dbo.Reseller");
        var child  = MakeTable("dbo.WholesaleMapping",
            fks: [new() { SourceField = "resellerId", TargetTable = "dbo.Reseller", TargetField = "lResellerID", Kind = ForeignKeyKind.Virtual }]);

        var erd    = new ErdDefinition { Tables = ["dbo.Reseller", "dbo.WholesaleMapping"], IncludeVirtual = true };
        var result = _gen.Generate(erd, ["dbo.Reseller", "dbo.WholesaleMapping"], ToDict(parent, child));

        result.Should().Contain("\"dbo.Reseller\" ||--o{ \"dbo.WholesaleMapping\" : \"resellerId -> lResellerID\"");
    }

    [Fact]
    public void Generate_PhysicalFkAlwaysIncluded()
    {
        var parent = MakeTable("dbo.Reseller");
        var child  = MakeTable("dbo.WholesaleMapping",
            fks: [new() { SourceField = "resellerId", TargetTable = "dbo.Reseller", TargetField = "lResellerID", Kind = ForeignKeyKind.Physical }]);

        // Physical FKs are always rendered regardless of IncludeLogical / IncludeVirtual settings.
        var erd    = new ErdDefinition { Tables = ["dbo.Reseller", "dbo.WholesaleMapping"], IncludeLogical = false };
        var result = _gen.Generate(erd, ["dbo.Reseller", "dbo.WholesaleMapping"], ToDict(parent, child));

        result.Should().Contain("\"dbo.Reseller\" ||--o{ \"dbo.WholesaleMapping\" : \"resellerId -> lResellerID\"");
    }

    // ── Scope: table outside section silently skipped ────────────────────────

    [Fact]
    public void Generate_TableOutsideSection_SkippedSilently()
    {
        var inSection    = MakeTable("dbo.Reseller");
        var outsideSection = MakeTable("dbo.Other");

        // ERD requests dbo.Other, but sectionTables only contains dbo.Reseller
        var erd    = new ErdDefinition { Tables = ["dbo.Reseller", "dbo.Other"] };
        var result = _gen.Generate(erd, ["dbo.Reseller"], ToDict(inSection, outsideSection));

        result.Should().Contain("\"dbo.Reseller\"");
        result.Should().NotContain("\"dbo.Other\"");
    }

    // ── Relationship label: source → target field ────────────────────────────

    [Fact]
    public void Generate_FkSameFieldName_LabelShowsFieldOnce()
    {
        var parent = MakeTable("dbo.Reseller",
            fields: [new() { Name = "Id", Type = "int", Nullable = false }],
            pk: ["Id"]);
        var child = MakeTable("dbo.Order",
            fks: [new() { SourceField = "ResellerId", TargetTable = "dbo.Reseller", TargetField = "ResellerId" }]);

        var erd    = new ErdDefinition { Tables = ["dbo.Reseller", "dbo.Order"] };
        var result = _gen.Generate(erd, ["dbo.Reseller", "dbo.Order"], ToDict(parent, child));

        result.Should().Contain(": \"ResellerId\"");
        result.Should().NotContain("->");
    }

    [Fact]
    public void Generate_FkSameFieldNameDifferentCase_LabelShowsFieldOnce()
    {
        var parent = MakeTable("dbo.Reseller",
            fields: [new() { Name = "lResellerID", Type = "int", Nullable = false }],
            pk: ["lResellerID"]);
        var child = MakeTable("dbo.Order",
            fks: [new() { SourceField = "lResellerId", TargetTable = "dbo.Reseller", TargetField = "lResellerID" }]);

        var erd    = new ErdDefinition { Tables = ["dbo.Reseller", "dbo.Order"] };
        var result = _gen.Generate(erd, ["dbo.Reseller", "dbo.Order"], ToDict(parent, child));

        // Case-insensitively equal → single-name label, no arrow
        result.Should().Contain(": \"lResellerId\"");
        result.Should().NotContain("->");
    }

    [Fact]
    public void Generate_FkDifferentFieldNames_LabelShowsBothSides()
    {
        var parent = MakeTable("dbo.Reseller",
            fields: [new() { Name = "lResellerID", Type = "int", Nullable = false }],
            pk: ["lResellerID"]);
        var child = MakeTable("dbo.Order",
            fks: [new() { SourceField = "resellerId", TargetTable = "dbo.Reseller", TargetField = "lResellerID" }]);

        var erd    = new ErdDefinition { Tables = ["dbo.Reseller", "dbo.Order"] };
        var result = _gen.Generate(erd, ["dbo.Reseller", "dbo.Order"], ToDict(parent, child));

        result.Should().Contain(": \"resellerId -> lResellerID\"");
    }

    // ── Target field in parent entity block ──────────────────────────────────

    [Fact]
    public void Generate_LogicalFkTargetFieldNotPkOrFk_IncludedInParentEntity()
    {
        // dbo.Reseller has "szCode" which is not a PK or FK source, but a logical FK
        // on dbo.Order references it as TargetField. It must appear in dbo.Reseller's entity block.
        var parent = MakeTable("dbo.Reseller",
            fields: [
                new() { Name = "lResellerID", Type = "int",         Nullable = false },
                new() { Name = "szCode",      Type = "varchar(50)", Nullable = false },
            ],
            pk: ["lResellerID"]);
        var child = MakeTable("dbo.Order",
            fks: [new() { SourceField = "resellerCode", TargetTable = "dbo.Reseller", TargetField = "szCode", Kind = ForeignKeyKind.Logical }]);

        var erd    = new ErdDefinition { Tables = ["dbo.Reseller", "dbo.Order"] };
        var result = _gen.Generate(erd, ["dbo.Reseller", "dbo.Order"], ToDict(parent, child));

        // Field appears in the entity block (no annotation — REF is not a valid Mermaid key)
        result.Should().Contain("varchar szCode");
    }

    [Fact]
    public void Generate_TargetFieldAlreadyPk_NoPkAnnotationNotOverridden()
    {
        var parent = MakeTable("dbo.Reseller",
            fields: [new() { Name = "lResellerID", Type = "int", Nullable = false }],
            pk: ["lResellerID"]);
        var child = MakeTable("dbo.Order",
            fks: [new() { SourceField = "resellerId", TargetTable = "dbo.Reseller", TargetField = "lResellerID" }]);

        var erd    = new ErdDefinition { Tables = ["dbo.Reseller", "dbo.Order"] };
        var result = _gen.Generate(erd, ["dbo.Reseller", "dbo.Order"], ToDict(parent, child));

        // PK annotation takes precedence; field must not lose its PK marker
        result.Should().Contain("lResellerID PK");
    }

    [Fact]
    public void Generate_TargetFieldExcludedWhenFkKindFiltered()
    {
        // Logical FK is excluded (IncludeLogical = false) → target field must NOT appear via REF
        var parent = MakeTable("dbo.Reseller",
            fields: [
                new() { Name = "lResellerID", Type = "int",         Nullable = false },
                new() { Name = "szCode",      Type = "varchar(50)", Nullable = false },
            ],
            pk: ["lResellerID"]);
        var child = MakeTable("dbo.Order",
            fks: [new() { SourceField = "resellerCode", TargetTable = "dbo.Reseller", TargetField = "szCode", Kind = ForeignKeyKind.Logical }]);

        var erd    = new ErdDefinition { Tables = ["dbo.Reseller", "dbo.Order"], IncludeLogical = false };
        var result = _gen.Generate(erd, ["dbo.Reseller", "dbo.Order"], ToDict(parent, child));

        result.Should().NotContain("szCode");
    }

    // ── RelationshipLabel helper ──────────────────────────────────────────────

    [Theory]
    [InlineData("Id",          "Id",          "Id")]
    [InlineData("resellerId",  "resellerId",  "resellerId")]
    [InlineData("lResellerId", "lResellerID", "lResellerId")]   // case-insensitive equal
    [InlineData("resellerId",  "lResellerID", "resellerId -> lResellerID")]
    [InlineData("operatorId",  "Id",          "operatorId -> Id")]
    public void RelationshipLabel_ReturnsExpected(string source, string target, string expected)
    {
        ErdGenerator.RelationshipLabel(source, target).Should().Be(expected);
    }

    // ── MermaidName helper ────────────────────────────────────────────────────

    [Theory]
    [InlineData("dbo.Reseller",          "\"dbo.Reseller\"")]
    [InlineData("dbo.Partner reference", "\"dbo.Partner_reference\"")]
    [InlineData("dbo.Some-Table",        "\"dbo.Some-Table\"")]
    public void MermaidName_ConvertsSpecialChars(string input, string expected)
    {
        ErdGenerator.MermaidName(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("varchar(255)", "varchar")]
    [InlineData("nvarchar(50)", "nvarchar")]
    [InlineData("int",          "int")]
    [InlineData("datetime",     "datetime")]
    public void MermaidType_StripsLength(string input, string expected)
    {
        ErdGenerator.MermaidType(input).Should().Be(expected);
    }

    // ── Deprecation rendering ─────────────────────────────────────────────────

    [Fact]
    public void Generate_DeprecatedTable_EmitsCommentBeforeEntity()
    {
        var table = MakeTable("dbo.OldOrder") with { IsDeprecated = true };
        var erd   = new ErdDefinition { Tables = ["dbo.OldOrder"] };

        var result = _gen.Generate(erd, ["dbo.OldOrder"], ToDict(table));

        result.Should().Contain("%% DEPRECATED: \"dbo.OldOrder\"");
    }

    [Fact]
    public void Generate_NonDeprecatedTable_NoDeprecatedComment()
    {
        var table  = MakeTable("dbo.Order");
        var erd    = new ErdDefinition { Tables = ["dbo.Order"] };

        var result = _gen.Generate(erd, ["dbo.Order"], ToDict(table));

        result.Should().NotContain("%% DEPRECATED");
    }

    [Fact]
    public void Generate_DeprecatedField_EmitsDeprAnnotation()
    {
        var table = MakeTable("dbo.Order",
            fields:
            [
                new FieldDefinition { Name = "Id",       Type = "int",         Nullable = false, IsPrimaryKey = true },
                new FieldDefinition { Name = "OldCode",  Type = "nvarchar(10)", Nullable = true,  IsDeprecated = true },
            ],
            pk: ["Id"]);

        var erd    = new ErdDefinition { Tables = ["dbo.Order"], Fields = ErdFields.All };
        var result = _gen.Generate(erd, ["dbo.Order"], ToDict(table));

        result.Should().Contain("DEPR");
    }

    [Fact]
    public void Generate_NonDeprecatedField_NoDeprAnnotation()
    {
        var table = MakeTable("dbo.Order",
            fields: [new FieldDefinition { Name = "Id", Type = "int", Nullable = false, IsPrimaryKey = true }],
            pk: ["Id"]);

        var erd    = new ErdDefinition { Tables = ["dbo.Order"], Fields = ErdFields.All };
        var result = _gen.Generate(erd, ["dbo.Order"], ToDict(table));

        result.Should().NotContain("DEPR");
    }

    // ── Cross-section FK references ───────────────────────────────────────────

    private static Dictionary<string, string> SectionMap(params (string table, string section)[] entries) =>
        entries.ToDictionary(e => e.table, e => e.section, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Generate_PhysicalFkCrossSection_RendersExternalStub()
    {
        // Platform section: OperatorPrefix has physical FK to Core section's dbo.Operator.
        var opPrefix = MakeTable("platform.OperatorPrefix",
            fks: [new() { SourceField = "operatorId", TargetTable = "dbo.Operator", TargetField = "Id", Kind = ForeignKeyKind.Physical }]);
        var opTable = MakeTable("dbo.Operator");

        var erd    = new ErdDefinition { Tables = ["platform.OperatorPrefix"] };
        var map    = SectionMap(("dbo.Operator", "Core"), ("platform.OperatorPrefix", "Platform"));
        var result = _gen.Generate(erd, ["platform.OperatorPrefix"], ToDict(opPrefix, opTable), tableSectionMap: map);

        // "operatorId" maps to "Id" — different names, label shows both sides
        result.Should().Contain("\"[Core] dbo.Operator\" ||--o{ \"platform.OperatorPrefix\" : \"operatorId -> Id\"");
    }

    [Fact]
    public void Generate_PhysicalFkCrossSection_StubNameFormat()
    {
        var child  = MakeTable("platform.OperatorPrefix",
            fks: [new() { SourceField = "operatorId", TargetTable = "dbo.Operator", TargetField = "Id", Kind = ForeignKeyKind.Physical }]);
        var parent = MakeTable("dbo.Operator");

        var map    = SectionMap(("dbo.Operator", "Core"));
        var result = _gen.Generate(new ErdDefinition { Tables = ["platform.OperatorPrefix"] },
            ["platform.OperatorPrefix"], ToDict(child, parent), tableSectionMap: map);

        // Stub name must include section label in brackets.
        result.Should().Contain("[Core] dbo.Operator");
    }

    [Fact]
    public void Generate_PhysicalFkCrossSection_EmitsCrossSectionComment()
    {
        var child  = MakeTable("platform.OperatorPrefix",
            fks: [new() { SourceField = "operatorId", TargetTable = "dbo.Operator", TargetField = "Id", Kind = ForeignKeyKind.Physical }]);
        var parent = MakeTable("dbo.Operator");

        var map    = SectionMap(("dbo.Operator", "Core"));
        var result = _gen.Generate(new ErdDefinition { Tables = ["platform.OperatorPrefix"] },
            ["platform.OperatorPrefix"], ToDict(child, parent), tableSectionMap: map);

        result.Should().Contain("%% External references (cross-section):");
    }

    [Fact]
    public void Generate_LogicalFkCrossSection_NotRenderedAsExternalStub()
    {
        // Logical FKs crossing section boundaries are intentionally omitted —
        // they imply a future physical relationship within the same module boundary.
        var child  = MakeTable("platform.OperatorPrefix",
            fks: [new() { SourceField = "operatorId", TargetTable = "dbo.Operator", TargetField = "Id", Kind = ForeignKeyKind.Logical }]);
        var parent = MakeTable("dbo.Operator");

        var map    = SectionMap(("dbo.Operator", "Core"));
        var result = _gen.Generate(new ErdDefinition { Tables = ["platform.OperatorPrefix"] },
            ["platform.OperatorPrefix"], ToDict(child, parent), tableSectionMap: map);

        result.Should().NotContain("||--o{");
        result.Should().NotContain("[Core]");
    }

    [Fact]
    public void Generate_VirtualFkCrossSection_NotRenderedAsExternalStub()
    {
        var child  = MakeTable("platform.OperatorPrefix",
            fks: [new() { SourceField = "operatorId", TargetTable = "dbo.Operator", TargetField = "Id", Kind = ForeignKeyKind.Virtual }]);
        var parent = MakeTable("dbo.Operator");

        var map    = SectionMap(("dbo.Operator", "Core"));
        var result = _gen.Generate(new ErdDefinition { Tables = ["platform.OperatorPrefix"] },
            ["platform.OperatorPrefix"], ToDict(child, parent), tableSectionMap: map);

        result.Should().NotContain("||--o{");
        result.Should().NotContain("[Core]");
    }

    [Fact]
    public void Generate_TableSectionMapNull_CrossSectionFkOmitted()
    {
        // Without a tableSectionMap the original intra-section-only behaviour is preserved.
        var child  = MakeTable("platform.OperatorPrefix",
            fks: [new() { SourceField = "operatorId", TargetTable = "dbo.Operator", TargetField = "Id", Kind = ForeignKeyKind.Physical }]);
        var parent = MakeTable("dbo.Operator");

        var result = _gen.Generate(new ErdDefinition { Tables = ["platform.OperatorPrefix"] },
            ["platform.OperatorPrefix"], ToDict(child, parent)); // no tableSectionMap

        result.Should().NotContain("||--o{");
        result.Should().NotContain("[Core]");
    }

    [Fact]
    public void Generate_CrossSectionFkTargetNotInTablesByName_SilentlySkipped()
    {
        // Target exists in the map but not in tablesByName — silently omit.
        var child  = MakeTable("platform.OperatorPrefix",
            fks: [new() { SourceField = "operatorId", TargetTable = "dbo.Operator", TargetField = "Id", Kind = ForeignKeyKind.Physical }]);

        // dbo.Operator is NOT in tablesByName
        var map    = SectionMap(("dbo.Operator", "Core"));
        var result = _gen.Generate(new ErdDefinition { Tables = ["platform.OperatorPrefix"] },
            ["platform.OperatorPrefix"], ToDict(child), tableSectionMap: map);

        result.Should().NotContain("||--o{");
        result.Should().NotContain("[Core]");
    }

    [Fact]
    public void Generate_MultipleFksToCrossSection_EachRelationshipRendered()
    {
        var child = MakeTable("platform.OperatorPrefix",
            fks: [
                new() { SourceField = "operatorId",   TargetTable = "dbo.Operator", TargetField = "Id",   Kind = ForeignKeyKind.Physical },
                new() { SourceField = "operatorCode",  TargetTable = "dbo.Operator", TargetField = "Code", Kind = ForeignKeyKind.Physical },
            ]);
        var parent = MakeTable("dbo.Operator");

        var map    = SectionMap(("dbo.Operator", "Core"));
        var result = _gen.Generate(new ErdDefinition { Tables = ["platform.OperatorPrefix"] },
            ["platform.OperatorPrefix"], ToDict(child, parent), tableSectionMap: map);

        // Both FK relationship lines must appear with column-mapping labels.
        result.Should().Contain("\"[Core] dbo.Operator\" ||--o{ \"platform.OperatorPrefix\" : \"operatorId -> Id\"");
        result.Should().Contain("\"[Core] dbo.Operator\" ||--o{ \"platform.OperatorPrefix\" : \"operatorCode -> Code\"");
    }

    [Fact]
    public void Generate_UnknownSectionInMap_UsesQuestionMarkLabel()
    {
        // Target is in tablesByName but not in tableSectionMap — label falls back to "?".
        var child  = MakeTable("platform.OperatorPrefix",
            fks: [new() { SourceField = "operatorId", TargetTable = "dbo.Operator", TargetField = "Id", Kind = ForeignKeyKind.Physical }]);
        var parent = MakeTable("dbo.Operator");

        // Empty map — target not present.
        var result = _gen.Generate(new ErdDefinition { Tables = ["platform.OperatorPrefix"] },
            ["platform.OperatorPrefix"], ToDict(child, parent), tableSectionMap: new Dictionary<string, string>());

        result.Should().Contain("\"[?] dbo.Operator\" ||--o{ \"platform.OperatorPrefix\" : \"operatorId -> Id\"");
    }

    // ── ExternalStubName helper ───────────────────────────────────────────────

    [Theory]
    [InlineData("Core",    "dbo.Operator",    "\"[Core] dbo.Operator\"")]
    [InlineData("Billing", "billing.Invoice", "\"[Billing] billing.Invoice\"")]
    [InlineData("My Section", "dbo.T A B",   "\"[My Section] dbo.T_A_B\"")]
    public void ExternalStubName_FormatsCorrectly(string section, string table, string expected)
    {
        ErdGenerator.ExternalStubName(section, table).Should().Be(expected);
    }
}
