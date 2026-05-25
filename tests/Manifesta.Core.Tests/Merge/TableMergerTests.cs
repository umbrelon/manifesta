using System.Text.Json;
using FluentAssertions;
using Manifesta.Core.IR;
using Manifesta.Core.Merge;
using Xunit;
using static Manifesta.Core.Tests.TableTestHelpers;

namespace Manifesta.Core.Tests;

public class TableMergerTests
{
    private readonly TableMerger _merger = new();
    private const string RepoPath = "tables/dbo.Order.json";

    // ── Idempotency ───────────────────────────────────────────────────────────

    [Fact]
    public void Merge_IdenticalDefinitions_HasNoChanges()
    {
        var repo = Table(fields: [Field("Id", "int", isPk: true)], pk: ["Id"]);
        var live = Table(fields: [Field("Id", "int", isPk: true)], pk: ["Id"]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.HasChanges.Should().BeFalse();
        result.OrphanColumnNames.Should().BeEmpty();
        result.NonSoftFkRemovedWarnings.Should().BeEmpty();
    }

    // ── Column type change ─────────────────────────────────────────────────────

    [Fact]
    public void Merge_ColumnTypeChanged_UpdatesTypePreservesDescription()
    {
        var repo = Table(fields: [Field("StatusId", "smallint", desc: "Order status")]);
        var live = Table(fields: [Field("StatusId", "int")]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.HasChanges.Should().BeTrue();
        result.FieldChanges.Should().ContainSingle(c =>
            c.Kind      == FieldChangeKind.TypeChanged &&
            c.FieldName == "StatusId" &&
            c.OldValue  == "smallint" &&
            c.NewValue  == "int");

        var mergedField = result.Merged.Fields.Single(f => f.Name == "StatusId");
        mergedField.Type.Should().Be("int");
        mergedField.Description.Should().Be("Order status"); // preserved
    }

    // ── Column nullability change ──────────────────────────────────────────────

    [Fact]
    public void Merge_NullabilityChanged_UpdatesNullable()
    {
        var repo = Table(fields: [Field("Note", "nvarchar(500)", nullable: false)]);
        var live = Table(fields: [Field("Note", "nvarchar(500)", nullable: true)]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.HasChanges.Should().BeTrue();
        result.FieldChanges.Should().ContainSingle(c =>
            c.Kind      == FieldChangeKind.NullabilityChanged &&
            c.FieldName == "Note" &&
            c.OldValue  == "false" &&
            c.NewValue  == "true");

        result.Merged.Fields.Single(f => f.Name == "Note").Nullable.Should().BeTrue();
    }

    // ── Column added ───────────────────────────────────────────────────────────

    [Fact]
    public void Merge_ColumnAdded_AppendsColumnWithNoDescription()
    {
        var repo = Table(fields: [Field("Id", "int")]);
        var live = Table(fields: [Field("Id", "int"), Field("CreatedAt", "datetime", nullable: false)]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.HasChanges.Should().BeTrue();
        result.FieldChanges.Should().ContainSingle(c =>
            c.Kind == FieldChangeKind.Added && c.FieldName == "CreatedAt");

        var newField = result.Merged.Fields.Single(f => f.Name == "CreatedAt");
        newField.Type.Should().Be("datetime");
        newField.Description.Should().BeEmpty();
        newField.IsMatchColumn.Should().BeFalse();

        // New column is appended after existing ones
        result.Merged.Fields.Last().Name.Should().Be("CreatedAt");
    }

    // ── Column removed (no flag) ───────────────────────────────────────────────

    [Fact]
    public void Merge_ColumnAbsentFromLive_PreservesColumn_ReportsOrphan()
    {
        var repo = Table(fields: [Field("Id", "int"), Field("LegacyCode", "varchar(10)")]);
        var live = Table(fields: [Field("Id", "int")]);

        var result = _merger.Merge(repo, live, RepoPath, removeDeleted: false);

        result.HasChanges.Should().BeFalse(); // no structural changes — only an orphan warning
        result.OrphanColumnNames.Should().ContainSingle("LegacyCode");
        result.Merged.Fields.Should().Contain(f => f.Name == "LegacyCode"); // preserved
    }

    // ── Column removed (with flag) ─────────────────────────────────────────────

    [Fact]
    public void Merge_ColumnAbsentFromLive_WithRemoveDeleted_RemovesColumn()
    {
        var repo = Table(fields: [Field("Id", "int"), Field("LegacyCode", "varchar(10)")]);
        var live = Table(fields: [Field("Id", "int")]);

        var result = _merger.Merge(repo, live, RepoPath, removeDeleted: true);

        result.HasChanges.Should().BeTrue();
        result.FieldChanges.Should().ContainSingle(c =>
            c.Kind == FieldChangeKind.Removed && c.FieldName == "LegacyCode");
        result.Merged.Fields.Should().NotContain(f => f.Name == "LegacyCode");
        result.OrphanColumnNames.Should().BeEmpty();
    }

    // ── isMatchColumn preserved ────────────────────────────────────────────────

    [Fact]
    public void Merge_TypeChanges_IsMatchColumnPreserved()
    {
        var repo = Table(fields: [Field("Code", "varchar(20)", isMatchCol: true)]);
        var live = Table(fields: [Field("Code", "nvarchar(20)")]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.Merged.Fields.Single(f => f.Name == "Code").IsMatchColumn.Should().BeTrue();
    }

    // ── Repo metadata preserved ────────────────────────────────────────────────

    [Fact]
    public void Merge_PreservesTableDescription_DatabaseTypes_Sets_Sections()
    {
        var repo = Table(
            description: "Core order table",
            sections: ["orders"],
            fields: [Field("Id", "int")]);
        var live = Table(fields: [Field("Id", "int")]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.Merged.Description.Should().Be("Core order table");
        result.Merged.Sections.Should().ContainSingle("orders");
    }

    // ── Primary key changed ────────────────────────────────────────────────────

    [Fact]
    public void Merge_PrimaryKeyChanged_UpdatesPkAndIsPrimaryKeyFlag()
    {
        var repo = Table(
            fields: [Field("OldId", "int", isPk: true), Field("Name", "nvarchar(100)")],
            pk:    ["OldId"]);
        var live = Table(
            fields: [Field("OldId", "int"), Field("Name", "nvarchar(100)", isPk: true)],
            pk:    ["Name"]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.PrimaryKeyChange.Should().NotBeNull();
        result.PrimaryKeyChange!.Before.Should().Equal("OldId");
        result.PrimaryKeyChange!.After.Should().Equal("Name");

        result.Merged.Fields.Single(f => f.Name == "OldId").IsPrimaryKey.Should().BeFalse();
        result.Merged.Fields.Single(f => f.Name == "Name").IsPrimaryKey.Should().BeTrue();
    }

    // ── FK added ───────────────────────────────────────────────────────────────

    [Fact]
    public void Merge_FkAdded_AppendsWithSoftFalse()
    {
        var repo = Table(fields: [Field("Id", "int")]);
        var live = Table(
            fields: [Field("Id", "int"), Field("StatusId", "int")],
            fks:   [Fk("StatusId", "dbo.OrderStatus", "Id")]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.FkChanges.Should().ContainSingle(c =>
            c.Kind == FkChangeKind.Added && c.SourceField == "StatusId");

        var addedFk = result.Merged.ForeignKeys.Single(f => f.SourceField == "StatusId");
        addedFk.Kind.Should().Be(ForeignKeyKind.Physical);
        addedFk.TargetTable.Should().Be("dbo.OrderStatus");
    }

    // ── Soft FK removal — silent ───────────────────────────────────────────────

    [Fact]
    public void Merge_SoftFkAbsentFromLive_SilentlyPreserved()
    {
        var softFk = Fk("CustomerId", "dbo.Customer", "Id", soft: true);
        var repo   = Table(fields: [Field("Id", "int"), Field("CustomerId", "int")], fks: [softFk]);
        var live   = Table(fields: [Field("Id", "int"), Field("CustomerId", "int")]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.NonSoftFkRemovedWarnings.Should().BeEmpty();
        result.Merged.ForeignKeys.Should().ContainSingle(f => f.SourceField == "CustomerId" && f.Kind == ForeignKeyKind.Logical);
    }

    // ── Physical FK removal — auto-delete ────────────────────────────────────

    [Fact]
    public void Merge_PhysicalFkAbsentFromLive_AutoDeleted()
    {
        var realFk = Fk("StatusId", "dbo.OrderStatus", "Id");
        var repo   = Table(fields: [Field("Id", "int"), Field("StatusId", "int")], fks: [realFk]);
        var live   = Table(fields: [Field("Id", "int"), Field("StatusId", "int")]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.HasChanges.Should().BeTrue();
        result.FkChanges.Should().ContainSingle(c =>
            c.Kind == FkChangeKind.Removed && c.SourceField == "StatusId");
        result.Merged.ForeignKeys.Should().BeEmpty("Physical FK absent from live is auto-deleted");
        result.NonSoftFkRemovedWarnings.Should().BeEmpty();
    }

    // ── FK soft flag preserved ─────────────────────────────────────────────────

    [Fact]
    public void Merge_FkPresentInBoth_SoftFlagPreservedFromRepo()
    {
        var repoFk = Fk("StatusId", "dbo.OrderStatus", "Id", cascade: false, soft: true);
        var liveFk = Fk("StatusId", "dbo.OrderStatus", "Id", cascade: true);

        var repo = Table(fields: [Field("StatusId", "int")], fks: [repoFk]);
        var live = Table(fields: [Field("StatusId", "int")], fks: [liveFk]);

        var result = _merger.Merge(repo, live, RepoPath);

        var merged = result.Merged.ForeignKeys.Single();
        merged.Kind.Should().Be(ForeignKeyKind.Logical);  // preserved from repo
        merged.CascadeDelete.Should().BeTrue();   // updated from live
    }

    // ── Column ordering ────────────────────────────────────────────────────────

    [Fact]
    public void Merge_NewColumnsAppendedAtEnd_RepoOrderPreserved()
    {
        var repo = Table(fields: [Field("Id", "int"), Field("Name", "nvarchar(100)")]);
        var live = Table(fields: [Field("NewFirst", "int"), Field("Id", "int"), Field("Name", "nvarchar(100)")]);

        var result = _merger.Merge(repo, live, RepoPath);

        var names = result.Merged.Fields.Select(f => f.Name).ToList();
        // Repo order preserved: Id, Name — then new column appended
        names.Should().Equal("Id", "Name", "NewFirst");
    }

    // ── FK cascadeDelete changed ───────────────────────────────────────────────

    [Fact]
    public void Merge_FkCascadeDeleteChanged_RecordedAsChange()
    {
        var repoFk = Fk("StatusId", "dbo.OrderStatus", "Id", cascade: false);
        var liveFk = Fk("StatusId", "dbo.OrderStatus", "Id", cascade: true);

        var repo = Table(fields: [Field("StatusId", "int")], fks: [repoFk]);
        var live = Table(fields: [Field("StatusId", "int")], fks: [liveFk]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.HasChanges.Should().BeTrue();
        result.FkChanges.Should().ContainSingle(c =>
            c.Kind        == FkChangeKind.CascadeDeleteChanged &&
            c.SourceField == "StatusId" &&
            c.OldValue    == "false" &&
            c.NewValue    == "true");

        result.Merged.ForeignKeys.Single().CascadeDelete.Should().BeTrue();
    }

    // ── RepoFilePath preserved ─────────────────────────────────────────────────

    [Fact]
    public void Merge_RepoFilePath_SetOnResult()
    {
        var repo   = Table(fields: [Field("Id", "int")]);
        var live   = Table(fields: [Field("Id", "int")]);
        var result = _merger.Merge(repo, live, "some/specific/path.json");

        result.RepoFilePath.Should().Be("some/specific/path.json");
    }

    // ── Case-insensitive column matching ───────────────────────────────────────

    [Fact]
    public void Merge_ColumnNameDifferentCase_MatchedAndUpdated()
    {
        // Repo uses PascalCase; live introspector returns lowercase
        var repo = Table(fields: [Field("StatusId", "smallint", desc: "Status FK")]);
        var live = Table(fields: [Field("statusid", "int")]);

        var result = _merger.Merge(repo, live, RepoPath);

        // Matched — not treated as add + remove
        result.FieldChanges.Should().ContainSingle(c => c.Kind == FieldChangeKind.TypeChanged);
        result.FieldChanges.Should().NotContain(c => c.Kind == FieldChangeKind.Added);
        result.FieldChanges.Should().NotContain(c => c.Kind == FieldChangeKind.Removed);

        // Description from repo preserved
        result.Merged.Fields.Single().Description.Should().Be("Status FK");
    }

    // ── Case-insensitive FK matching ───────────────────────────────────────────

    [Fact]
    public void Merge_FkKeyDifferentCase_Matched()
    {
        var repoFk = Fk("StatusId", "dbo.OrderStatus", "Id", cascade: false);
        var liveFk = Fk("statusid", "dbo.orderstatus", "id", cascade: true);

        var repo = Table(fields: [Field("StatusId", "int")], fks: [repoFk]);
        var live = Table(fields: [Field("StatusId", "int")], fks: [liveFk]);

        var result = _merger.Merge(repo, live, RepoPath);

        // Treated as the same FK, not add + remove
        result.FkChanges.Should().ContainSingle(c => c.Kind == FkChangeKind.CascadeDeleteChanged);
        result.FkChanges.Should().NotContain(c => c.Kind == FkChangeKind.Added);
        result.Merged.ForeignKeys.Should().HaveCount(1);
    }

    // ── Composite primary key ──────────────────────────────────────────────────

    [Fact]
    public void Merge_CompositePrimaryKey_AllFlagsUpdated()
    {
        var repo = Table(
            fields: [Field("OrderId", "int", isPk: true), Field("LineNo", "int", isPk: true), Field("Note", "nvarchar(200)")],
            pk:    ["OrderId", "LineNo"]);

        // Live drops LineNo from PK, adds ProductId
        var live = Table(
            fields: [Field("OrderId", "int"), Field("LineNo", "int"), Field("Note", "nvarchar(200)"), Field("ProductId", "int")],
            pk:    ["OrderId", "ProductId"]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.PrimaryKeyChange.Should().NotBeNull();
        result.PrimaryKeyChange!.After.Should().Equal("OrderId", "ProductId");

        result.Merged.Fields.Single(f => f.Name == "OrderId").IsPrimaryKey.Should().BeTrue();
        result.Merged.Fields.Single(f => f.Name == "LineNo").IsPrimaryKey.Should().BeFalse();
        result.Merged.Fields.Single(f => f.Name == "ProductId").IsPrimaryKey.Should().BeTrue();
    }

    // ── Multiple simultaneous changes ──────────────────────────────────────────

    [Fact]
    public void Merge_MultipleSimultaneousChanges_AllRecorded()
    {
        var repo = Table(
            fields: [
                Field("Id",        "int"),
                Field("StatusId",  "smallint", desc: "Status ref"),
                Field("LegacyCol", "varchar(10)"),
            ],
            fks: [Fk("StatusId", "dbo.Status", "Id")]);

        var live = Table(
            fields: [
                Field("Id",       "int"),
                Field("StatusId", "int"),         // type changed
                Field("NewCol",   "datetime"),    // added
                // LegacyCol removed
            ],
            fks: [
                Fk("StatusId", "dbo.Status", "Id"),
                Fk("NewCol",   "dbo.Event",  "Id"),  // FK added
            ]);

        var result = _merger.Merge(repo, live, RepoPath, removeDeleted: true);

        result.FieldChanges.Should().Contain(c => c.Kind == FieldChangeKind.TypeChanged   && c.FieldName == "StatusId");
        result.FieldChanges.Should().Contain(c => c.Kind == FieldChangeKind.Added         && c.FieldName == "NewCol");
        result.FieldChanges.Should().Contain(c => c.Kind == FieldChangeKind.Removed       && c.FieldName == "LegacyCol");
        result.FkChanges.Should().Contain(c => c.Kind == FkChangeKind.Added && c.SourceField == "NewCol");

        // Description on StatusId preserved despite type change
        result.Merged.Fields.Single(f => f.Name == "StatusId").Description.Should().Be("Status ref");
    }

    // ── Default value changed ──────────────────────────────────────────────────

    [Fact]
    public void Merge_DefaultChanged_RecordedAndUpdated()
    {
        var repo = Table(fields: [Field("IsActive", "bit", defaultVal: "0")]);
        var live = Table(fields: [Field("IsActive", "bit", defaultVal: "1")]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.HasChanges.Should().BeTrue();
        result.FieldChanges.Should().ContainSingle(c =>
            c.Kind      == FieldChangeKind.DefaultChanged &&
            c.FieldName == "IsActive" &&
            c.OldValue  == "0" &&
            c.NewValue  == "1");

        result.Merged.Fields.Single(f => f.Name == "IsActive").Default.Should().Be("1");
    }

    [Fact]
    public void Merge_DefaultSetWhereNoneExisted_RecordedAsChange()
    {
        var repo = Table(fields: [Field("IsActive", "bit")]);           // no default in repo
        var live = Table(fields: [Field("IsActive", "bit", defaultVal: "0")]); // DB now has default

        var result = _merger.Merge(repo, live, RepoPath);

        result.HasChanges.Should().BeTrue();
        result.FieldChanges.Should().ContainSingle(c =>
            c.Kind      == FieldChangeKind.DefaultChanged &&
            c.FieldName == "IsActive" &&
            c.OldValue  == null &&
            c.NewValue  == "0");

        result.Merged.Fields.Single(f => f.Name == "IsActive").Default.Should().Be("0");
    }

    [Fact]
    public void Merge_IdenticalDefaults_NoDefaultChangeRecorded()
    {
        var repo = Table(fields: [Field("Status", "int", defaultVal: "1")]);
        var live = Table(fields: [Field("Status", "int", defaultVal: "1")]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.HasChanges.Should().BeFalse();
        result.FieldChanges.Should().NotContain(c => c.Kind == FieldChangeKind.DefaultChanged);
    }

    [Fact]
    public void Merge_NewColumnFromLive_DefaultCarriedThrough()
    {
        var repo = Table(fields: [Field("Id", "int")]);
        var live = Table(fields: [Field("Id", "int"), Field("Status", "int", defaultVal: "0")]);

        var result = _merger.Merge(repo, live, RepoPath);

        var newField = result.Merged.Fields.Single(f => f.Name == "Status");
        newField.Default.Should().Be("0");
    }

    // ── Empty table ────────────────────────────────────────────────────────────

    [Fact]
    public void Merge_EmptyTable_NoChangesNoErrors()
    {
        var repo = Table();
        var live = Table();

        var result = _merger.Merge(repo, live, RepoPath);

        result.HasChanges.Should().BeFalse();
        result.Merged.Fields.Should().BeEmpty();
        result.Merged.PrimaryKey.Should().BeEmpty();
        result.Merged.ForeignKeys.Should().BeEmpty();
    }

    // ── PK field also has description ──────────────────────────────────────────

    [Fact]
    public void Merge_PkFieldWithDescription_DescriptionPreservedAfterPkChange()
    {
        var repo = Table(
            fields: [Field("OldId", "int", isPk: true, desc: "Legacy identifier"), Field("NewId", "int")],
            pk:    ["OldId"]);

        var live = Table(
            fields: [Field("OldId", "int"), Field("NewId", "int")],
            pk:    ["NewId"]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.PrimaryKeyChange.Should().NotBeNull();

        // OldId loses isPrimaryKey but keeps its description
        var oldId = result.Merged.Fields.Single(f => f.Name == "OldId");
        oldId.IsPrimaryKey.Should().BeFalse();
        oldId.Description.Should().Be("Legacy identifier");
    }

    // ── Physical FK auto-delete counts as structural change ───────────────────

    [Fact]
    public void Merge_PhysicalFkAbsentFromLive_HasChangesIsTrue()
    {
        // Physical FK removal is DB-authoritative: auto-deleted and recorded in FkChanges.
        // HasChanges is true so the file is written out with the FK removed.
        var repoFk = Fk("StatusId", "dbo.OrderStatus", "Id");
        var repo   = Table(fields: [Field("Id", "int"), Field("StatusId", "int")], fks: [repoFk]);
        var live   = Table(fields: [Field("Id", "int"), Field("StatusId", "int")]); // FK dropped from DB

        var result = _merger.Merge(repo, live, RepoPath);

        result.HasChanges.Should().BeTrue();
        result.FkChanges.Should().ContainSingle(c => c.Kind == FkChangeKind.Removed);
        result.Merged.ForeignKeys.Should().BeEmpty();
        result.NonSoftFkRemovedWarnings.Should().BeEmpty();
    }

    // ── Multiple orphan columns ────────────────────────────────────────────────

    [Fact]
    public void Merge_MultipleOrphanColumns_AllListed()
    {
        var repo = Table(fields: [
            Field("Id",       "int"),
            Field("OldCol1",  "varchar(10)"),
            Field("OldCol2",  "varchar(20)"),
        ]);
        var live = Table(fields: [Field("Id", "int")]);

        var result = _merger.Merge(repo, live, RepoPath, removeDeleted: false);

        result.OrphanColumnNames.Should().BeEquivalentTo(["OldCol1", "OldCol2"]);
        result.Merged.Fields.Should().HaveCount(3); // all preserved
        result.HasChanges.Should().BeFalse();
    }

    // ── Sets and DatabaseTypes preserved ──────────────────────────────────────

    [Fact]
    public void Merge_SetsAndDatabaseTypes_AlwaysPreserved()
    {
        var set = new ColumnSet { Name = "Sync", Columns = ["Id", "StatusId"] };
        var repo = new TableDefinition
        {
            Name          = "dbo.Order",
            Fields        = [Field("Id", "int"), Field("StatusId", "int")],
            PrimaryKey    = ["Id"],
            DatabaseTypes = ["MSSQL", "Postgres"],
            Sets          = [set],
        };
        var live = Table(
            fields: [Field("Id", "int"), Field("StatusId", "bigint")],  // type change
            pk:    ["Id"]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.Merged.DatabaseTypes.Should().Equal("MSSQL", "Postgres");
        result.Merged.Sets.Should().HaveCount(1);
        result.Merged.Sets[0].Name.Should().Be("Sync");
    }

    // ── No PK (table without primary key) ─────────────────────────────────────

    [Fact]
    public void Merge_NoPrimaryKey_RemainsEmpty()
    {
        var repo = Table(fields: [Field("EventId", "uniqueidentifier")]);
        var live = Table(fields: [Field("EventId", "uniqueidentifier")]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.HasChanges.Should().BeFalse();
        result.Merged.PrimaryKey.Should().BeEmpty();
    }

    // ── Virtual FK absent from live — silently preserved ──────────────────────

    [Fact]
    public void Merge_VirtualFkAbsentFromLive_SilentlyPreserved()
    {
        var virtualFk = Fk("RegionId", "dbo.Region", "Id", kind: ForeignKeyKind.Virtual);
        var repo      = Table(fields: [Field("Id", "int"), Field("RegionId", "int")], fks: [virtualFk]);
        var live      = Table(fields: [Field("Id", "int"), Field("RegionId", "int")]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.HasChanges.Should().BeFalse("Virtual FK is repo-sovereign — no change recorded");
        result.FkChanges.Should().BeEmpty();
        result.NonSoftFkRemovedWarnings.Should().BeEmpty();
        result.Merged.ForeignKeys.Should().ContainSingle(f =>
            f.SourceField == "RegionId" && f.Kind == ForeignKeyKind.Virtual);
    }

    // ── LabelField preservation ────────────────────────────────────────────────

    [Fact]
    public void Merge_TableLabelField_PreservedFromRepo()
    {
        // LabelField is repo-sovereign on the table — never overwritten from live.
        var repo = Table(fields: [Field("Id", "int"), Field("szName", "nvarchar(100)")]) with { LabelField = "szName" };
        var live = Table(fields: [Field("Id", "int"), Field("szName", "nvarchar(100)")]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.Merged.LabelField.Should().Be("szName");
    }

    [Fact]
    public void Merge_TableLabelField_NullInRepo_RemainsNull()
    {
        // Live never provides labelField; null in repo stays null after merge.
        var repo = Table(fields: [Field("Id", "int")]);
        var live = Table(fields: [Field("Id", "int")]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.Merged.LabelField.Should().BeNull();
    }

    // ── Orphaned data keys ─────────────────────────────────────────────────────

    [Fact]
    public void Merge_NoData_NoOrphanedDataKeys()
    {
        var repo = Table(fields: [Field("Id", "int"), Field("Name", "nvarchar(50)")]);
        var live = Table(fields: [Field("Id", "int")]);

        var result = _merger.Merge(repo, live, RepoPath, removeDeleted: true);

        result.OrphanedDataKeys.Should().BeEmpty();
    }

    [Fact]
    public void Merge_DataKeysAllPresentInMergedSchema_NoOrphanedDataKeys()
    {
        var data = new[] { DataRow(("Id", 1), ("Code", 2)) };
        var repo = Table(
            fields: [Field("Id", "int"), Field("Code", "int")],
            data:   data);
        var live = Table(fields: [Field("Id", "int"), Field("Code", "int")]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.OrphanedDataKeys.Should().BeEmpty();
    }

    [Fact]
    public void Merge_ColumnRemovedWithFlag_DataHasThatKey_ReportsOrphanedDataKey()
    {
        var data = new[] { DataRow(("Id", 1), ("LegacyCode", 99)) };
        var repo = Table(
            fields: [Field("Id", "int"), Field("LegacyCode", "int")],
            data:   data);
        var live = Table(fields: [Field("Id", "int")]);

        var result = _merger.Merge(repo, live, RepoPath, removeDeleted: true);

        result.OrphanedDataKeys.Should().ContainSingle("LegacyCode");
    }

    [Fact]
    public void Merge_OrphanColumn_DataKeyStillValid_NoOrphanedDataKey()
    {
        // Column preserved as orphan (removeDeleted=false) → key still in schema → not orphaned.
        var data = new[] { DataRow(("Id", 1), ("LegacyCode", 99)) };
        var repo = Table(
            fields: [Field("Id", "int"), Field("LegacyCode", "int")],
            data:   data);
        var live = Table(fields: [Field("Id", "int")]);

        var result = _merger.Merge(repo, live, RepoPath, removeDeleted: false);

        result.OrphanedDataKeys.Should().BeEmpty();
        result.OrphanColumnNames.Should().ContainSingle("LegacyCode"); // still an orphan column warning
    }

    [Fact]
    public void Merge_MultipleRowsSameOrphanedKey_DeduplicatedToOne()
    {
        var data = new[]
        {
            DataRow(("Id", 1), ("OldCol", 10)),
            DataRow(("Id", 2), ("OldCol", 20)),
            DataRow(("Id", 3), ("OldCol", 30)),
        };
        var repo = Table(
            fields: [Field("Id", "int"), Field("OldCol", "int")],
            data:   data);
        var live = Table(fields: [Field("Id", "int")]);

        var result = _merger.Merge(repo, live, RepoPath, removeDeleted: true);

        result.OrphanedDataKeys.Should().ContainSingle("OldCol");
    }

    [Fact]
    public void Merge_MultipleOrphanedDataKeys_AllReported()
    {
        var data = new[] { DataRow(("Id", 1), ("OldA", 10), ("OldB", 20)) };
        var repo = Table(
            fields: [Field("Id", "int"), Field("OldA", "int"), Field("OldB", "int")],
            data:   data);
        var live = Table(fields: [Field("Id", "int")]);

        var result = _merger.Merge(repo, live, RepoPath, removeDeleted: true);

        result.OrphanedDataKeys.Should().BeEquivalentTo(["OldA", "OldB"]);
    }

    [Fact]
    public void Merge_DataWithNoOrphanedColumns_HasNoOrphanedDataKeys()
    {
        // Reference table with intact schema — no data key should be orphaned.
        var data = new[] { DataRow(("Id", 1)) };
        var repo = Table(
            fields: [Field("Id", "int"), Field("Name", "int")],
            data:   data);
        var live = Table(fields: [Field("Id", "int"), Field("Name", "int")]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.OrphanedDataKeys.Should().BeEmpty();
        result.HasChanges.Should().BeFalse();
    }

    // ── Computed field merge ────────────────────────────────────────────────────

    [Fact]
    public void Merge_ComputedField_ExpressionUnchanged_NoChange()
    {
        var repo = Table(fields: [Field("Id", "int"), ComputedField("FullName")]);
        var live = Table(fields: [Field("Id", "int"), ComputedField("FullName")]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.HasChanges.Should().BeFalse();
        result.FieldChanges.Should().NotContain(c => c.Kind == FieldChangeKind.ComputedExpressionChanged);
    }

    [Fact]
    public void Merge_ComputedField_ExpressionChanged_RecordedAndApplied()
    {
        var repo = Table(fields: [Field("Id", "int"), ComputedField("FullName", expression: "([OldExpr])")]);
        var live = Table(fields: [Field("Id", "int"), ComputedField("FullName", expression: "([NewExpr])")]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.HasChanges.Should().BeTrue();
        result.FieldChanges.Should().ContainSingle(c =>
            c.Kind      == FieldChangeKind.ComputedExpressionChanged &&
            c.FieldName == "FullName" &&
            c.OldValue  == "([OldExpr])" &&
            c.NewValue  == "([NewExpr])");

        result.Merged.Fields.Single(f => f.Name == "FullName").ComputedExpression
              .Should().Be("([NewExpr])");
    }

    [Fact]
    public void Merge_ComputedField_ColumnBecomesPersisted_RecordedAndApplied()
    {
        var repo = Table(fields: [Field("Id", "int"), ComputedField("Total", isPersisted: false)]);
        var live = Table(fields: [Field("Id", "int"), ComputedField("Total", isPersisted: true)]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.HasChanges.Should().BeTrue();
        result.FieldChanges.Should().ContainSingle(c =>
            c.Kind      == FieldChangeKind.IsPersistedChanged &&
            c.FieldName == "Total" &&
            c.OldValue  == "false" &&
            c.NewValue  == "true");

        result.Merged.Fields.Single(f => f.Name == "Total").IsPersisted.Should().BeTrue();
    }

    [Fact]
    public void Merge_ComputedField_DescriptionPreservedFromRepo()
    {
        var repoField = ComputedField("FullName") with { Description = "Computed full name" };
        var liveField = ComputedField("FullName") with { ComputedExpression = "([NewExpr])" };

        var repo = Table(fields: [Field("Id", "int"), repoField]);
        var live = Table(fields: [Field("Id", "int"), liveField]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.Merged.Fields.Single(f => f.Name == "FullName").Description
              .Should().Be("Computed full name", "description is repo-sovereign");
    }

    [Fact]
    public void Merge_NewComputedColumnFromLive_ExpressionCarriedThrough()
    {
        var repo = Table(fields: [Field("Id", "int")]);
        var live = Table(fields: [Field("Id", "int"), ComputedField("FullName", expression: "([F]+' '+[L])")]);

        var result = _merger.Merge(repo, live, RepoPath);

        result.HasChanges.Should().BeTrue();
        result.FieldChanges.Should().ContainSingle(c => c.Kind == FieldChangeKind.Added && c.FieldName == "FullName");

        var newField = result.Merged.Fields.Single(f => f.Name == "FullName");
        newField.IsComputed.Should().BeTrue();
        newField.ComputedExpression.Should().Be("([F]+' '+[L])");
        newField.Description.Should().BeEmpty("new columns start with no description");
    }
}
