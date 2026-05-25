using System.Text.Json;
using FluentAssertions;
using Manifesta.Core.Drift;
using Manifesta.Core.IR;
using Manifesta.Core.Merge;
using Xunit;
using static Manifesta.Core.Tests.TableTestHelpers;

namespace Manifesta.Core.Tests;

public class TableDifferTests
{
    private readonly TableDiffer _differ = new();
    private const string RepoPath = "tables/dbo.Order.json";

    // ── Idempotency ───────────────────────────────────────────────────────────

    [Fact]
    public void Diff_IdenticalDefinitions_NoDriftNoWarnings()
    {
        var repo = Table(fields: [Field("Id", "int", isPk: true)], pk: ["Id"]);
        var live = Table(fields: [Field("Id", "int", isPk: true)], pk: ["Id"]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeFalse();
        result.HasWarnings.Should().BeFalse();
        result.FieldChanges.Should().BeEmpty();
        result.FkChanges.Should().BeEmpty();
        result.PrimaryKeyChange.Should().BeNull();
        result.ExtraDbColumns.Should().BeEmpty();
    }

    [Fact]
    public void Diff_EmptyTables_NoDrift()
    {
        var result = _differ.Diff(Table(), Table(), RepoPath);

        result.HasDrift.Should().BeFalse();
        result.HasWarnings.Should().BeFalse();
    }

    // ── Field changes (drift) ─────────────────────────────────────────────────

    [Fact]
    public void Diff_ColumnTypeChanged_RecordedAsDrift()
    {
        var repo = Table(fields: [Field("Amount", "smallint")]);
        var live = Table(fields: [Field("Amount", "int")]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeTrue();
        result.FieldChanges.Should().HaveCount(1);
        result.FieldChanges[0].Kind.Should().Be(FieldChangeKind.TypeChanged);
        result.FieldChanges[0].FieldName.Should().Be("Amount");
        result.FieldChanges[0].OldValue.Should().Be("smallint");
        result.FieldChanges[0].NewValue.Should().Be("int");
    }

    [Fact]
    public void Diff_ColumnNullabilityChanged_RecordedAsDrift()
    {
        var repo = Table(fields: [Field("Name", "nvarchar(100)", nullable: false)]);
        var live = Table(fields: [Field("Name", "nvarchar(100)", nullable: true)]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeTrue();
        result.FieldChanges.Should().HaveCount(1);
        result.FieldChanges[0].Kind.Should().Be(FieldChangeKind.NullabilityChanged);
        result.FieldChanges[0].OldValue.Should().Be("false");
        result.FieldChanges[0].NewValue.Should().Be("true");
    }

    [Fact]
    public void Diff_ColumnDefaultChanged_RecordedAsDrift()
    {
        var repo = Table(fields: [Field("Status", "int", defaultVal: "0")]);
        var live = Table(fields: [Field("Status", "int", defaultVal: "1")]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeTrue();
        result.FieldChanges.Should().HaveCount(1);
        result.FieldChanges[0].Kind.Should().Be(FieldChangeKind.DefaultChanged);
        result.FieldChanges[0].OldValue.Should().Be("0");
        result.FieldChanges[0].NewValue.Should().Be("1");
    }

    [Fact]
    public void Diff_ColumnDefaultRemovedFromDB_RecordedAsDrift()
    {
        var repo = Table(fields: [Field("Status", "int", defaultVal: "0")]);
        var live = Table(fields: [Field("Status", "int", defaultVal: null)]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeTrue();
        result.FieldChanges[0].Kind.Should().Be(FieldChangeKind.DefaultChanged);
        result.FieldChanges[0].OldValue.Should().Be("0");
        result.FieldChanges[0].NewValue.Should().BeNull();
    }

    [Fact]
    public void Diff_ColumnRemovedFromDB_RecordedAsDrift()
    {
        var repo = Table(fields: [Field("Id"), Field("LegacyCode", "nvarchar(10)")]);
        var live = Table(fields: [Field("Id")]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeTrue();
        result.FieldChanges.Should().HaveCount(1);
        result.FieldChanges[0].Kind.Should().Be(FieldChangeKind.Removed);
        result.FieldChanges[0].FieldName.Should().Be("LegacyCode");
        result.FieldChanges[0].OldValue.Should().Be("nvarchar(10)");
    }

    // ── Extra DB columns (warnings, not drift) ────────────────────────────────

    [Fact]
    public void Diff_ColumnAddedToDBNotInRepo_IsWarningNotDrift()
    {
        var repo = Table(fields: [Field("Id")]);
        var live = Table(fields: [Field("Id"), Field("NewCol", "bit")]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeFalse();
        result.HasWarnings.Should().BeTrue();
        result.ExtraDbColumns.Should().ContainSingle().Which.Should().Be("NewCol");
        result.FieldChanges.Should().BeEmpty();
    }

    [Fact]
    public void Diff_MultipleExtraDbColumns_AllReportedAsWarnings()
    {
        var repo = Table(fields: [Field("Id")]);
        var live = Table(fields: [Field("Id"), Field("ColA"), Field("ColB")]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeFalse();
        result.ExtraDbColumns.Should().HaveCount(2).And.Contain(["ColA", "ColB"]);
    }

    [Fact]
    public void Diff_DriftAndWarningTogether_BothSurfaced()
    {
        var repo = Table(fields: [Field("Id"), Field("Amount", "smallint")]);
        var live = Table(fields: [Field("Id"), Field("Amount", "int"), Field("NewCol")]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeTrue();
        result.HasWarnings.Should().BeTrue();
        result.FieldChanges.Should().HaveCount(1).And.ContainSingle(fc => fc.Kind == FieldChangeKind.TypeChanged);
        result.ExtraDbColumns.Should().ContainSingle().Which.Should().Be("NewCol");
    }

    // ── Primary key drift ─────────────────────────────────────────────────────

    [Fact]
    public void Diff_PrimaryKeyUnchanged_NoPkDrift()
    {
        var repo = Table(fields: [Field("Id", isPk: true)], pk: ["Id"]);
        var live = Table(fields: [Field("Id", isPk: true)], pk: ["Id"]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.PrimaryKeyChange.Should().BeNull();
        result.HasDrift.Should().BeFalse();
    }

    [Fact]
    public void Diff_PrimaryKeyChanged_RecordedAsDrift()
    {
        var repo = Table(fields: [Field("Id"), Field("TenantId")], pk: ["Id"]);
        var live = Table(fields: [Field("Id"), Field("TenantId")], pk: ["Id", "TenantId"]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeTrue();
        result.PrimaryKeyChange.Should().NotBeNull();
        result.PrimaryKeyChange!.Before.Should().Equal(["Id"]);
        result.PrimaryKeyChange.After.Should().Equal(["Id", "TenantId"]);
    }

    [Fact]
    public void Diff_PrimaryKeyReordered_RecordedAsDrift()
    {
        var repo = Table(pk: ["TenantId", "Id"]);
        var live = Table(pk: ["Id", "TenantId"]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.PrimaryKeyChange.Should().NotBeNull();
    }

    // ── FK drift ──────────────────────────────────────────────────────────────

    [Fact]
    public void Diff_PhysicalFkRemovedFromDB_RecordedAsDrift()
    {
        var repo = Table(fks: [Fk("CustomerId", "dbo.Customers", "Id", kind: ForeignKeyKind.Physical)]);
        var live = Table();

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeTrue();
        result.FkChanges.Should().HaveCount(1);
        result.FkChanges[0].Kind.Should().Be(FkChangeKind.Removed);
        result.FkChanges[0].SourceField.Should().Be("CustomerId");
    }

    [Fact]
    public void Diff_PhysicalFkAddedToDB_RecordedAsDrift()
    {
        var repo = Table();
        var live = Table(fks: [Fk("CustomerId", "dbo.Customers", "Id")]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeTrue();
        result.FkChanges.Should().HaveCount(1);
        result.FkChanges[0].Kind.Should().Be(FkChangeKind.Added);
        result.FkChanges[0].SourceField.Should().Be("CustomerId");
        result.FkChanges[0].TargetTable.Should().Be("dbo.Customers");
    }

    [Fact]
    public void Diff_PhysicalFkCascadeDeleteChanged_RecordedAsDrift()
    {
        var repo = Table(fks: [Fk("CustomerId", "dbo.Customers", "Id", cascade: false)]);
        var live = Table(fks: [Fk("CustomerId", "dbo.Customers", "Id", cascade: true)]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeTrue();
        result.FkChanges.Should().HaveCount(1);
        result.FkChanges[0].Kind.Should().Be(FkChangeKind.CascadeDeleteChanged);
        result.FkChanges[0].OldValue.Should().Be("false");
        result.FkChanges[0].NewValue.Should().Be("true");
    }

    [Fact]
    public void Diff_PhysicalFkUnchanged_NoDrift()
    {
        var fk = Fk("CustomerId", "dbo.Customers", "Id", cascade: false);
        var repo = Table(fks: [fk]);
        var live = Table(fks: [fk]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeFalse();
        result.FkChanges.Should().BeEmpty();
    }

    // ── Repo-sovereign FK types — always ignored ──────────────────────────────

    [Fact]
    public void Diff_LogicalFkAbsentFromDB_Ignored()
    {
        var repo = Table(fks: [Fk("RegionId", "dbo.Regions", "Id", kind: ForeignKeyKind.Logical)]);
        var live = Table();

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeFalse();
        result.FkChanges.Should().BeEmpty();
    }

    [Fact]
    public void Diff_VirtualFkAbsentFromDB_Ignored()
    {
        var repo = Table(fks: [Fk("Msisdn", "dbo.Subscribers", "Phone", kind: ForeignKeyKind.Virtual)]);
        var live = Table();

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeFalse();
        result.FkChanges.Should().BeEmpty();
    }

    [Fact]
    public void Diff_MixedFkKinds_OnlyPhysicalEvaluated()
    {
        var repo = Table(fks: [
            Fk("CustomerId", "dbo.Customers", "Id", kind: ForeignKeyKind.Physical),
            Fk("RegionId",   "dbo.Regions",   "Id", kind: ForeignKeyKind.Logical),
            Fk("Msisdn",     "dbo.Subs",      "Phone", kind: ForeignKeyKind.Virtual),
        ]);
        // Only the physical FK survives in DB.
        var live = Table(fks: [
            Fk("CustomerId", "dbo.Customers", "Id"),
        ]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeFalse();
        result.FkChanges.Should().BeEmpty();
    }

    // ── Case-insensitivity ────────────────────────────────────────────────────

    [Fact]
    public void Diff_ColumnNameCaseInsensitiveMatch_NoDrift()
    {
        var repo = Table(fields: [Field("CustomerId", "int")]);
        var live = Table(fields: [Field("customerid", "int")]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeFalse();
        result.ExtraDbColumns.Should().BeEmpty();
    }

    [Fact]
    public void Diff_PkCaseInsensitiveMatch_NoPkDrift()
    {
        var repo = Table(pk: ["Id"]);
        var live = Table(pk: ["id"]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.PrimaryKeyChange.Should().BeNull();
    }

    // ── Result metadata ───────────────────────────────────────────────────────

    [Fact]
    public void Diff_RepoAndLiveTablesStoredOnResult()
    {
        var repo = Table(fields: [Field("Id")]);
        var live = Table(fields: [Field("Id"), Field("Extra")]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.RepoFilePath.Should().Be(RepoPath);
        result.TableName.Should().Be("dbo.Order");
        result.RepoTable.Should().Be(repo);
        result.LiveTable.Should().Be(live);
    }

    [Fact]
    public void Diff_MultipleSimultaneousChanges_AllCaptured()
    {
        var repo = Table(
            fields: [Field("Id"), Field("Amount", "smallint"), Field("LegacyCol")],
            pk:     ["Id"],
            fks:    [Fk("CustomerId", "dbo.Customers", "Id")]);

        var live = Table(
            fields: [Field("Id"), Field("Amount", "int"), Field("NewCol")],
            pk:     ["Id", "TenantId"],
            fks:    []);

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeTrue();
        result.HasWarnings.Should().BeTrue();
        result.FieldChanges.Should().Contain(fc => fc.Kind == FieldChangeKind.TypeChanged && fc.FieldName == "Amount");
        result.FieldChanges.Should().Contain(fc => fc.Kind == FieldChangeKind.Removed && fc.FieldName == "LegacyCol");
        result.PrimaryKeyChange.Should().NotBeNull();
        result.FkChanges.Should().ContainSingle(fk => fk.Kind == FkChangeKind.Removed);
        result.ExtraDbColumns.Should().ContainSingle().Which.Should().Be("NewCol");
    }

    // ── Data drift ─────────────────────────────────────────────────────────────

    [Fact]
    public void Diff_BothDataEmpty_NoDataChanges()
    {
        var repo = Table(fields: [Field("Id")], pk: ["Id"]);
        var live = Table(fields: [Field("Id")], pk: ["Id"]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.DataChanges.Should().BeEmpty();
    }

    [Fact]
    public void Diff_RowAddedInLive_ReportedAsAdded()
    {
        var repoData = new[] { DataRow(("Id", (object?)1), ("Name", "Std")) };
        var liveData = new[] { DataRow(("Id", (object?)1), ("Name", "Std")), DataRow(("Id", (object?)2), ("Name", "Premium")) };
        var repo = Table(fields: [Field("Id"), Field("Name", "nvarchar(50)")], pk: ["Id"], data: repoData);
        var live = Table(fields: [Field("Id"), Field("Name", "nvarchar(50)")], pk: ["Id"], data: liveData);

        var result = _differ.Diff(repo, live, RepoPath);

        result.DataChanges.Should().ContainSingle(c =>
            c.Kind == DataChangeKind.Added &&
            c.PkValues["Id"].GetRawText() == "2");
    }

    [Fact]
    public void Diff_RowRemovedFromLive_ReportedAsRemoved()
    {
        var repoData = new[] { DataRow(("Id", (object?)1), ("Name", "Std")), DataRow(("Id", (object?)2), ("Name", "Premium")) };
        var liveData = new[] { DataRow(("Id", (object?)1), ("Name", "Std")) };
        var repo = Table(fields: [Field("Id"), Field("Name", "nvarchar(50)")], pk: ["Id"], data: repoData);
        var live = Table(fields: [Field("Id"), Field("Name", "nvarchar(50)")], pk: ["Id"], data: liveData);

        var result = _differ.Diff(repo, live, RepoPath);

        result.DataChanges.Should().ContainSingle(c =>
            c.Kind == DataChangeKind.Removed &&
            c.PkValues["Id"].GetRawText() == "2");
    }

    [Fact]
    public void Diff_RowValueChanged_ReportedAsModifiedWithChangedFields()
    {
        var repoData = new[] { DataRow(("Id", (object?)1), ("Name", "Standard"), ("Code", "STD")) };
        var liveData = new[] { DataRow(("Id", (object?)1), ("Name", "Standard Plus"), ("Code", "STD")) };
        var repo = Table(fields: [Field("Id"), Field("Name", "nvarchar(50)"), Field("Code", "nvarchar(10)")], pk: ["Id"], data: repoData);
        var live = Table(fields: [Field("Id"), Field("Name", "nvarchar(50)"), Field("Code", "nvarchar(10)")], pk: ["Id"], data: liveData);

        var result = _differ.Diff(repo, live, RepoPath);

        result.DataChanges.Should().ContainSingle(c =>
            c.Kind == DataChangeKind.Modified &&
            c.PkValues["Id"].GetRawText() == "1" &&
            c.ChangedFields.Contains("Name") &&
            !c.ChangedFields.Contains("Code"));
    }

    [Fact]
    public void Diff_RowUnchanged_NoDataChange()
    {
        var row = new[] { DataRow(("Id", (object?)1), ("Name", "Std")) };
        var repo = Table(fields: [Field("Id"), Field("Name", "nvarchar(50)")], pk: ["Id"], data: row);
        var live = Table(fields: [Field("Id"), Field("Name", "nvarchar(50)")], pk: ["Id"], data: row);

        var result = _differ.Diff(repo, live, RepoPath);

        result.DataChanges.Should().BeEmpty();
    }

    [Fact]
    public void Diff_NoPrimaryKey_DataChangesEmpty()
    {
        var repoData = new[] { DataRow(("Id", (object?)1)) };
        var liveData = new[] { DataRow(("Id", (object?)2)) };
        var repo = Table(fields: [Field("Id")], pk: [], data: repoData);
        var live = Table(fields: [Field("Id")], pk: [], data: liveData);

        var result = _differ.Diff(repo, live, RepoPath);

        result.DataChanges.Should().BeEmpty();
    }

    [Fact]
    public void Diff_DataChanges_CountsAsHasDrift()
    {
        var repoData = new[] { DataRow(("Id", (object?)1), ("Name", "Old")) };
        var liveData = new[] { DataRow(("Id", (object?)1), ("Name", "New")) };
        var repo = Table(fields: [Field("Id"), Field("Name", "nvarchar(50)")], pk: ["Id"], data: repoData);
        var live = Table(fields: [Field("Id"), Field("Name", "nvarchar(50)")], pk: ["Id"], data: liveData);

        var result = _differ.Diff(repo, live, RepoPath);

        result.DataChanges.Should().ContainSingle(c => c.Kind == DataChangeKind.Modified);
        result.HasDrift.Should().BeTrue();
    }

    // ── Computed field drift ───────────────────────────────────────────────────

    [Fact]
    public void Diff_ComputedField_ExpressionUnchanged_NoDrift()
    {
        var repo = Table(fields: [Field("Id"), ComputedField("FullName")]);
        var live = Table(fields: [Field("Id"), ComputedField("FullName")]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeFalse();
        result.FieldChanges.Should().NotContain(c => c.Kind == FieldChangeKind.ComputedExpressionChanged);
    }

    [Fact]
    public void Diff_ComputedField_ExpressionChanged_ReportedAsDrift()
    {
        var repo = Table(fields: [Field("Id"), ComputedField("FullName", expression: "([OldExpr])")]);
        var live = Table(fields: [Field("Id"), ComputedField("FullName", expression: "([NewExpr])")]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeTrue();
        result.FieldChanges.Should().ContainSingle(c =>
            c.Kind      == FieldChangeKind.ComputedExpressionChanged &&
            c.FieldName == "FullName" &&
            c.OldValue  == "([OldExpr])" &&
            c.NewValue  == "([NewExpr])");
    }

    [Fact]
    public void Diff_ComputedField_PersistedFlagChanged_ReportedAsDrift()
    {
        var repo = Table(fields: [Field("Id"), ComputedField("Total", isPersisted: false)]);
        var live = Table(fields: [Field("Id"), ComputedField("Total", isPersisted: true)]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeTrue();
        result.FieldChanges.Should().ContainSingle(c =>
            c.Kind      == FieldChangeKind.IsPersistedChanged &&
            c.FieldName == "Total" &&
            c.OldValue  == "false" &&
            c.NewValue  == "true");
    }

    [Fact]
    public void Diff_RegularFieldBecomesComputed_ExpressionChangedReported()
    {
        // Repo has a plain column; live turns it into a computed column.
        var repo = Table(fields: [Field("Id"), Field("FullName", "nvarchar(101)", nullable: true)]);
        var live = Table(fields: [Field("Id"), ComputedField("FullName")]);

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeTrue();
        result.FieldChanges.Should().Contain(c =>
            c.Kind == FieldChangeKind.ComputedExpressionChanged && c.FieldName == "FullName");
    }

    // ── Index drift ───────────────────────────────────────────────────────────

    [Fact]
    public void Diff_IndexAddedInLive_RecordedAsAdded()
    {
        var repo = Table(fields: [Field("Id", "int", isPk: true)], pk: ["Id"]);
        var live = repo with
        {
            Indexes =
            [
                new IndexDefinition { Name = "IX_Status", Columns = ["StatusId"], IsUnique = false },
            ],
        };

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeTrue();
        result.IndexChanges.Should().ContainSingle(c =>
            c.Kind      == IndexChangeKind.Added &&
            c.IndexName == "IX_Status");
    }

    [Fact]
    public void Diff_IndexRemovedFromLive_RecordedAsRemoved()
    {
        var repo = Table(fields: [Field("Id", "int", isPk: true)], pk: ["Id"]) with
        {
            Indexes =
            [
                new IndexDefinition { Name = "IX_OldStatus", Columns = ["OldStatusId"], IsUnique = false },
            ],
        };
        var live = repo with { Indexes = [] };

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeTrue();
        result.IndexChanges.Should().ContainSingle(c =>
            c.Kind      == IndexChangeKind.Removed &&
            c.IndexName == "IX_OldStatus");
    }

    [Fact]
    public void Diff_IndexColumnsChanged_RecordedAsColumnsChanged()
    {
        var idx = new IndexDefinition { Name = "IX_Order", Columns = ["StatusId"], IsUnique = false };
        var repo = Table(fields: [Field("Id", "int", isPk: true)], pk: ["Id"]) with
        {
            Indexes = [idx],
        };
        var live = repo with
        {
            Indexes = [idx with { Columns = ["StatusId", "CustomerId"] }],
        };

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeTrue();
        result.IndexChanges.Should().ContainSingle(c =>
            c.Kind      == IndexChangeKind.ColumnsChanged &&
            c.IndexName == "IX_Order");
    }

    [Fact]
    public void Diff_IndexUniquenessChanged_RecordedAsUniquenessChanged()
    {
        var idx  = new IndexDefinition { Name = "IX_Code", Columns = ["Code"], IsUnique = false };
        var repo = Table(fields: [Field("Id", "int", isPk: true)], pk: ["Id"]) with
        {
            Indexes = [idx],
        };
        var live = repo with
        {
            Indexes = [idx with { IsUnique = true }],
        };

        var result = _differ.Diff(repo, live, RepoPath);

        result.HasDrift.Should().BeTrue();
        result.IndexChanges.Should().ContainSingle(c =>
            c.Kind      == IndexChangeKind.UniquenessChanged &&
            c.IndexName == "IX_Code");
    }

    [Fact]
    public void Diff_IdenticalIndexes_NoDrift()
    {
        var idx = new IndexDefinition { Name = "IX_Status", Columns = ["StatusId"], IsUnique = true };
        var repo = Table(fields: [Field("Id", "int", isPk: true)], pk: ["Id"]) with
        {
            Indexes = [idx],
        };

        var result = _differ.Diff(repo, repo, RepoPath);

        result.IndexChanges.Should().BeEmpty();
    }
}
