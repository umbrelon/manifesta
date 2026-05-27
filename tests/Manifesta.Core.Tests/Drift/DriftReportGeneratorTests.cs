using FluentAssertions;
using Manifesta.Core.Drift;
using Manifesta.Core.IR;
using Manifesta.Core.Merge;
using Xunit;

namespace Manifesta.Core.Tests;

public class DriftReportGeneratorTests
{
    private readonly DriftReportGenerator _generator = new();
    private static readonly DateTimeOffset _ts = new(2026, 5, 15, 9, 0, 0, TimeSpan.Zero);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DriftSession EmptySession(bool includeSchema = false) => new()
    {
        Source          = "--connection (Server=localhost;Database=test;)",
        RootPath        = "/repo/root",
        Timestamp       = _ts,
        TotalLiveTables = 0,
        IncludeSchema   = includeSchema,
    };

    private static TableDefinition SimpleTable(string name, params string[] columns) => new()
    {
        Name   = name,
        Fields = columns.Select(c => new FieldDefinition { Name = c, Type = "int" }).ToList().AsReadOnly(),
    };

    private static DriftResult DriftedResult(
        string name = "dbo.Order",
        IEnumerable<FieldChange>? fieldChanges = null,
        IEnumerable<FkChange>? fkChanges = null,
        PrimaryKeyChange? pkChange = null,
        IEnumerable<string>? extraDbColumns = null,
        TableDefinition? repoTable = null,
        TableDefinition? liveTable = null) => new()
    {
        TableName        = name,
        RepoFilePath     = $"tables/{name}.json",
        RepoTable        = repoTable ?? SimpleTable(name, "Id"),
        LiveTable        = liveTable ?? SimpleTable(name, "Id"),
        FieldChanges     = (fieldChanges    ?? []).ToList().AsReadOnly(),
        FkChanges        = (fkChanges       ?? []).ToList().AsReadOnly(),
        PrimaryKeyChange = pkChange,
        ExtraDbColumns   = (extraDbColumns  ?? []).ToList().AsReadOnly(),
    };

    private static DriftResult CleanResult(string name = "dbo.Customer") => new()
    {
        TableName    = name,
        RepoFilePath = $"tables/{name}.json",
        RepoTable    = SimpleTable(name),
        LiveTable    = SimpleTable(name),
    };

    private static FieldChange RemovedField(string name)  => new() { Kind = FieldChangeKind.Removed,            FieldName = name, OldValue = "int" };
    private static FieldChange TypeChanged(string name)   => new() { Kind = FieldChangeKind.TypeChanged,        FieldName = name, OldValue = "smallint", NewValue = "int" };
    private static FieldChange NullChanged(string name)   => new() { Kind = FieldChangeKind.NullabilityChanged, FieldName = name, OldValue = "false",    NewValue = "true" };
    private static FieldChange DefaultChanged(string name)=> new() { Kind = FieldChangeKind.DefaultChanged,     FieldName = name, OldValue = "0",        NewValue = "1" };

    private static FkChange AddedFk(string src)   => new() { Kind = FkChangeKind.Added,   SourceField = src, TargetTable = "dbo.Other", TargetField = "Id" };
    private static FkChange RemovedFk(string src) => new() { Kind = FkChangeKind.Removed, SourceField = src, TargetTable = "dbo.Other", TargetField = "Id" };
    private static FkChange CascadeChanged(string src) => new()
    {
        Kind = FkChangeKind.CascadeDeleteChanged,
        SourceField = src, TargetTable = "dbo.Other", TargetField = "Id",
        OldValue = "false", NewValue = "true",
    };

    // ── Header ────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_HeaderContainsTimestampSourceAndRoot()
    {
        var report = _generator.Generate(EmptySession());

        report.Should().Contain("# Manifesta DB Drift Report");
        report.Should().Contain("2026-05-15");
        report.Should().Contain("Server=localhost;Database=test;");
        report.Should().Contain("/repo/root");
    }

    // ── Status label ──────────────────────────────────────────────────────────

    [Fact]
    public void Generate_NoDriftNoWarnings_ShowsInSyncStatus()
    {
        var session = EmptySession() with
        {
            CleanTables     = [CleanResult()],
            TotalLiveTables = 1,
        };

        var report = _generator.Generate(session);

        report.Should().Contain("✅ In sync");
    }

    [Fact]
    public void Generate_DriftDetected_ShowsDriftStatus()
    {
        var session = EmptySession() with
        {
            DriftedTables   = [DriftedResult(fieldChanges: [TypeChanged("Amount")])],
            TotalLiveTables = 1,
        };

        var report = _generator.Generate(session);

        report.Should().Contain("❌ Drift detected");
    }

    [Fact]
    public void Generate_WarningsOnlyNoDrift_ShowsWarningsStatus()
    {
        var session = EmptySession() with
        {
            ExtraDbTables   = ["dbo.NewTable"],
            TotalLiveTables = 1,
        };

        var report = _generator.Generate(session);

        report.Should().Contain("⚠ Warnings only");
    }

    // ── Summary counts ────────────────────────────────────────────────────────

    [Fact]
    public void Generate_SummaryCountsMatchSession()
    {
        var session = EmptySession() with
        {
            TotalLiveTables = 10,
            CleanTables     = [CleanResult("dbo.A"), CleanResult("dbo.B")],
            DriftedTables   = [DriftedResult("dbo.C", fieldChanges: [TypeChanged("X")])],
            MissingDbTables = ["/repo/tables/dbo.Ghost.json"],
            ExtraDbTables   = ["dbo.Unknown"],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("| Tables scanned (live) | 10 |");
        report.Should().Contain("| Tables in sync | 2 |");
        report.Should().Contain("| Tables with drift | 1 |");
        report.Should().Contain("| Tables absent from source | 1 |");
        report.Should().Contain("| Tables absent from repo (⚠) | 1 |");
    }

    // ── Drifted tables section ────────────────────────────────────────────────

    [Fact]
    public void Generate_NoDriftedTables_SectionAbsent()
    {
        var session = EmptySession() with { CleanTables = [CleanResult()] };

        var report = _generator.Generate(session);

        report.Should().NotContain("## Drifted Tables");
    }

    [Fact]
    public void Generate_DriftedTable_SectionPresentWithTableName()
    {
        var session = EmptySession() with
        {
            DriftedTables = [DriftedResult("dbo.Order", fieldChanges: [TypeChanged("Amount")])],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("## Drifted Tables");
        report.Should().Contain("### dbo.Order");
        report.Should().Contain("`tables/dbo.Order.json`");
    }

    // ── Field change rendering ────────────────────────────────────────────────

    [Fact]
    public void Generate_RemovedColumn_RenderedCorrectly()
    {
        var session = EmptySession() with
        {
            DriftedTables = [DriftedResult(fieldChanges: [RemovedField("LegacyCode")])],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("Column absent from source");
        report.Should().Contain("`LegacyCode`");
    }

    [Fact]
    public void Generate_TypeChanged_RenderedCorrectly()
    {
        var session = EmptySession() with
        {
            DriftedTables = [DriftedResult(fieldChanges: [TypeChanged("Amount")])],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("Column type changed");
        report.Should().Contain("`Amount`");
        report.Should().Contain("`smallint`");
        report.Should().Contain("`int`");
    }

    [Fact]
    public void Generate_NullabilityChanged_RenderedCorrectly()
    {
        var session = EmptySession() with
        {
            DriftedTables = [DriftedResult(fieldChanges: [NullChanged("Name")])],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("Column nullability changed");
        report.Should().Contain("`Name`");
    }

    [Fact]
    public void Generate_DefaultChanged_RenderedCorrectly()
    {
        var session = EmptySession() with
        {
            DriftedTables = [DriftedResult(fieldChanges: [DefaultChanged("Status")])],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("Column default changed");
        report.Should().Contain("`Status`");
        report.Should().Contain("`0`");
        report.Should().Contain("`1`");
    }

    // ── FK change rendering ───────────────────────────────────────────────────

    [Fact]
    public void Generate_FkAdded_RenderedCorrectly()
    {
        var session = EmptySession() with
        {
            DriftedTables = [DriftedResult(fkChanges: [AddedFk("CustomerId")])],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("FK added to source");
        report.Should().Contain("`CustomerId`");
        report.Should().Contain("`dbo.Other.Id`");
    }

    [Fact]
    public void Generate_FkRemoved_RenderedCorrectly()
    {
        var session = EmptySession() with
        {
            DriftedTables = [DriftedResult(fkChanges: [RemovedFk("CustomerId")])],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("FK removed from source");
        report.Should().Contain("`CustomerId`");
    }

    [Fact]
    public void Generate_FkCascadeChanged_RenderedCorrectly()
    {
        var session = EmptySession() with
        {
            DriftedTables = [DriftedResult(fkChanges: [CascadeChanged("OrderId")])],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("FK cascadeDelete changed");
        report.Should().Contain("`OrderId`");
        report.Should().Contain("`false`");
        report.Should().Contain("`true`");
    }

    // ── PK change rendering ───────────────────────────────────────────────────

    [Fact]
    public void Generate_PrimaryKeyChanged_RenderedCorrectly()
    {
        var pkChange = new PrimaryKeyChange { Before = ["Id"], After = ["Id", "TenantId"] };
        var session  = EmptySession() with
        {
            DriftedTables = [DriftedResult(pkChange: pkChange)],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("Primary key changed");
        report.Should().Contain("`Id`");
        report.Should().Contain("`Id, TenantId`");
    }

    // ── Extra DB columns (warnings within drifted table) ─────────────────────

    [Fact]
    public void Generate_ExtraDbColumns_RenderedAsTableRowsInsideDriftedTable()
    {
        var session = EmptySession() with
        {
            DriftedTables = [DriftedResult(
                fieldChanges:  [TypeChanged("Amount")],
                extraDbColumns: ["NewCol", "AnotherCol"])],
        };

        var report = _generator.Generate(session);

        // Extra columns must appear as "Column absent from repo" rows inside the change table.
        report.Should().Contain("Column absent from repo");
        report.Should().Contain("`NewCol`");
        report.Should().Contain("`AnotherCol`");
        // The summary note must mention db merge in standard (non-compare) mode.
        report.Should().Contain("db merge");
        // The old bullet-list format must not appear.
        report.Should().NotContain("Extra columns in source");
    }

    // ── Tables absent from DB (drift) ─────────────────────────────────────────

    [Fact]
    public void Generate_MissingDbTables_SectionPresentWithPaths()
    {
        var session = EmptySession() with
        {
            MissingDbTables = ["/repo/tables/dbo.Ghost.json"],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("## Tables Absent from Source");
        report.Should().Contain("`/repo/tables/dbo.Ghost.json`");
        report.Should().Contain("db merge");
    }

    [Fact]
    public void Generate_NoMissingDbTables_SectionAbsent()
    {
        var report = _generator.Generate(EmptySession());

        report.Should().NotContain("## Tables Absent from Source");
    }

    // ── Extra DB tables (warnings) ────────────────────────────────────────────

    [Fact]
    public void Generate_ExtraDbTables_SectionPresentWithNames()
    {
        var session = EmptySession() with
        {
            ExtraDbTables = ["dbo.NewTable", "dbo.AnotherNew"],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("## Warnings: Tables Absent from Repository");
        report.Should().Contain("`dbo.NewTable`");
        report.Should().Contain("`dbo.AnotherNew`");
    }

    [Fact]
    public void Generate_NoExtraDbTables_WarningSectionAbsent()
    {
        var report = _generator.Generate(EmptySession());

        report.Should().NotContain("## Warnings: Tables Absent from Repository");
    }

    // ── Custom labels (compare mode) ──────────────────────────────────────────

    [Fact]
    public void Generate_CustomLabels_UsedInSectionHeaders()
    {
        var session = EmptySession() with
        {
            MissingDbTables = ["dbo.Ghost"],
            ExtraDbTables   = ["dbo.NewTable"],
        };

        var report = _generator.Generate(session, sourceLabel: "Source", targetLabel: "Target");

        report.Should().Contain("## Tables Absent from Target");
        report.Should().Contain("## Warnings: Tables Absent from Source");
        report.Should().NotContain("db merge", "reconciliation hint must be omitted in compare mode");
        report.Should().NotContain("Tables Absent from Repository", "default source label must not appear in section headings");
        report.Should().NotContain("Tables Absent from Live Database", "default target label must not appear in section headings");
    }

    // ── Clean tables section ──────────────────────────────────────────────────

    [Fact]
    public void Generate_CleanTables_SectionPresentWithNames()
    {
        var session = EmptySession() with
        {
            CleanTables     = [CleanResult("dbo.Customer"), CleanResult("dbo.Product")],
            TotalLiveTables = 2,
        };

        var report = _generator.Generate(session);

        report.Should().Contain("## Clean Tables");
        report.Should().Contain("dbo.Customer");
        report.Should().Contain("dbo.Product");
        report.Should().Contain("2 table(s) are fully in sync");
    }

    [Fact]
    public void Generate_NoCleanTables_CleanSectionAbsent()
    {
        var session = EmptySession() with
        {
            DriftedTables = [DriftedResult(fieldChanges: [TypeChanged("X")])],
        };

        var report = _generator.Generate(session);

        report.Should().NotContain("## Clean Tables");
    }

    // ── --include-schema ──────────────────────────────────────────────────────

    [Fact]
    public void Generate_IncludeSchemaFalse_NoFieldListingsInReport()
    {
        var repo = SimpleTable("dbo.Order", "Id", "Amount");
        var live = SimpleTable("dbo.Order", "Id", "Amount", "NewCol");
        var session = EmptySession(includeSchema: false) with
        {
            DriftedTables = [DriftedResult("dbo.Order",
                extraDbColumns: ["NewCol"],
                repoTable: repo, liveTable: live)],
        };

        var report = _generator.Generate(session);

        report.Should().NotContain("Repository definition");
        report.Should().NotContain("Source definition");
    }

    // ── Data drift rendering ──────────────────────────────────────────────────

    private static DataRowChange DataChange(
        DataChangeKind kind,
        string pkCol,
        int pkVal,
        IEnumerable<string>? changedFields = null) => new()
    {
        Kind     = kind,
        PkValues = new Dictionary<string, System.Text.Json.JsonElement>
        {
            [pkCol] = System.Text.Json.JsonSerializer.SerializeToElement(pkVal),
        },
        ChangedFields = (changedFields ?? []).ToList().AsReadOnly(),
    };

    [Fact]
    public void Generate_DataDrift_ShowsDataDriftSubsection()
    {
        var result = DriftedResult("dbo.BundleType") with
        {
            DataChanges = [
                DataChange(DataChangeKind.Added,    "Id", 4),
                DataChange(DataChangeKind.Removed,  "Id", 2),
                DataChange(DataChangeKind.Modified, "Id", 1, ["Name"]),
            ],
        };
        var session = EmptySession() with { DriftedTables = [result] };

        var report = _generator.Generate(session);

        report.Should().Contain("**Data drift (3 change(s)):**");
        report.Should().Contain("Row added");
        report.Should().Contain("Row removed");
        report.Should().Contain("Row modified");
        report.Should().Contain("Fields: Name");
    }

    [Fact]
    public void Generate_DataDrift_AbsentWhenNoDataChanges()
    {
        var session = EmptySession() with
        {
            DriftedTables = [DriftedResult("dbo.BundleType", fieldChanges: [TypeChanged("Code")])],
        };

        var report = _generator.Generate(session);

        report.Should().NotContain("Data drift");
    }

    [Fact]
    public void Generate_DataDrift_PkFormattedInTable()
    {
        var result = DriftedResult("dbo.BundleType") with
        {
            DataChanges = [DataChange(DataChangeKind.Removed, "Id", 99)],
        };
        var session = EmptySession() with { DriftedTables = [result] };

        var report = _generator.Generate(session);

        report.Should().Contain("`Id=99`");
    }

    [Fact]
    public void Generate_IncludeSchemaTrue_FieldListingsEmbedded()
    {
        var repo = SimpleTable("dbo.Order", "Id", "Amount");
        var live = SimpleTable("dbo.Order", "Id", "Amount", "NewCol");
        var session = EmptySession(includeSchema: true) with
        {
            DriftedTables = [DriftedResult("dbo.Order",
                fieldChanges:  [TypeChanged("Amount")],
                repoTable: repo, liveTable: live)],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("**Repository definition:**");
        report.Should().Contain("**Source definition:**");
        report.Should().Contain("| Column | Type | Nullable | Default |");
    }

    // ── Computed field change rendering ───────────────────────────────────────

    private static FieldChange ComputedExprChanged(string name, string old, string @new) => new()
    {
        Kind      = FieldChangeKind.ComputedExpressionChanged,
        FieldName = name,
        OldValue  = old,
        NewValue  = @new,
    };

    private static FieldChange IsPersistedChanged(string name, bool oldVal, bool newVal) => new()
    {
        Kind      = FieldChangeKind.IsPersistedChanged,
        FieldName = name,
        OldValue  = oldVal.ToString().ToLowerInvariant(),
        NewValue  = newVal.ToString().ToLowerInvariant(),
    };

    [Fact]
    public void Generate_ComputedExpressionChanged_RenderedCorrectly()
    {
        var session = EmptySession() with
        {
            DriftedTables = [DriftedResult("dbo.Person",
                fieldChanges: [ComputedExprChanged("FullName", "([OldExpr])", "([NewExpr])")])],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("Computed expression changed");
        report.Should().Contain("`([OldExpr])`");
        report.Should().Contain("`([NewExpr])`");
        report.Should().Contain("`FullName`");
    }

    [Fact]
    public void Generate_IsPersistedChanged_RenderedCorrectly()
    {
        var session = EmptySession() with
        {
            DriftedTables = [DriftedResult("dbo.Person",
                fieldChanges: [IsPersistedChanged("Total", false, true)])],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("Computed persisted flag changed");
        report.Should().Contain("`false`");
        report.Should().Contain("`true`");
        report.Should().Contain("`Total`");
    }

    [Fact]
    public void Generate_IncludeSchema_TableWithComputedColumn_ShowsExpressionColumn()
    {
        var repo = new TableDefinition
        {
            Name   = "dbo.Person",
            Fields = [
                new FieldDefinition { Name = "Id",       Type = "int" },
                new FieldDefinition
                {
                    Name               = "FullName",
                    Type               = "nvarchar(101)",
                    IsComputed         = true,
                    ComputedExpression = "([First]+' '+[Last])",
                },
            ],
        };
        var session = EmptySession(includeSchema: true) with
        {
            DriftedTables = [DriftedResult("dbo.Person",
                fieldChanges: [ComputedExprChanged("FullName", null!, "([First]+' '+[Last])")],
                repoTable: repo, liveTable: repo)],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("| Column | Type | Nullable | Default | Expression |");
        report.Should().Contain("`([First]+' '+[Last])`");
    }

    // ── Summary FK / index counts ─────────────────────────────────────────────

    [Fact]
    public void Generate_SummaryAlwaysShowsFkAndIndexCounts()
    {
        var session = EmptySession() with
        {
            DriftedTables = [
                DriftedResult("dbo.Order",
                    fkChanges: [AddedFk("CustomerId"), RemovedFk("VendorId")]) with
                {
                    IndexChanges = [AddedIndex("IX_Status")],
                },
            ],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("| FK changes | 2 |");
        report.Should().Contain("| Index changes | 1 |");
    }

    [Fact]
    public void Generate_SummaryShowsZeroCountsWhenNoDriftedTables()
    {
        var session = EmptySession() with
        {
            CleanTables     = [CleanResult()],
            TotalLiveTables = 1,
        };

        var report = _generator.Generate(session);

        report.Should().Contain("| FK changes | 0 |");
        report.Should().Contain("| Index changes | 0 |");
    }

    // ── --no-clean-tables ─────────────────────────────────────────────────────

    [Fact]
    public void Generate_NoCleanTables_CleanSectionOmitted()
    {
        var session = EmptySession() with
        {
            CleanTables        = [CleanResult("dbo.Customer"), CleanResult("dbo.Product")],
            TotalLiveTables    = 2,
            IncludeCleanTables = false,
        };

        var report = _generator.Generate(session);

        report.Should().NotContain("## Clean Tables");
        report.Should().NotContain("dbo.Customer");
    }

    [Fact]
    public void Generate_CleanTablesIncludedByDefault()
    {
        var session = EmptySession() with
        {
            CleanTables     = [CleanResult("dbo.Customer")],
            TotalLiveTables = 1,
        };

        var report = _generator.Generate(session);

        report.Should().Contain("## Clean Tables");
        report.Should().Contain("dbo.Customer");
    }

    // ── Custom-label schema headings ──────────────────────────────────────────

    [Fact]
    public void Generate_CustomLabels_SchemaHeadingsUseLabels()
    {
        var repo = SimpleTable("dbo.Order", "Id");
        var live = SimpleTable("dbo.Order", "Id", "Extra");
        var session = EmptySession(includeSchema: true) with
        {
            DriftedTables = [DriftedResult("dbo.Order",
                fieldChanges: [TypeChanged("Id")],
                repoTable: repo, liveTable: live)],
        };

        var report = _generator.Generate(session, sourceLabel: "Source", targetLabel: "Target");

        report.Should().Contain("**Source definition:**");
        report.Should().Contain("**Target definition:**");
        report.Should().NotContain("**Repository definition:**");
    }

    // ── --no-fk-drifts ────────────────────────────────────────────────────────

    [Fact]
    public void Generate_NoFkDrifts_FkRowsOmittedButColumnChangesPresent()
    {
        var session = EmptySession() with
        {
            DriftedTables = [DriftedResult(
                fieldChanges: [TypeChanged("Amount")],
                fkChanges:    [AddedFk("CustomerId")])],
            IncludeFkDrifts = false,
        };

        var report = _generator.Generate(session);

        report.Should().Contain("Column type changed");
        report.Should().NotContain("FK added to source");
        report.Should().NotContain("`CustomerId`");
    }

    [Fact]
    public void Generate_NoFkDrifts_FkOnlyTable_ChangeTableOmitted()
    {
        var session = EmptySession() with
        {
            DriftedTables   = [DriftedResult(fkChanges: [AddedFk("CustomerId")])],
            IncludeFkDrifts = false,
        };

        var report = _generator.Generate(session);

        report.Should().NotContain("| Change | Field / Key | Before | After |");
    }

    // ── --no-index-drifts ─────────────────────────────────────────────────────

    private static IndexChange AddedIndex(string name) => new()
    {
        Kind       = IndexChangeKind.Added,
        IndexName  = name,
        NewColumns = "Col1",
    };

    [Fact]
    public void Generate_IndexDrift_RenderedByDefault()
    {
        var session = EmptySession() with
        {
            DriftedTables = [DriftedResult() with { IndexChanges = [AddedIndex("IX_Order_Status")] }],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("**Index drift (1 change(s)):**");
        report.Should().Contain("`IX_Order_Status`");
        report.Should().Contain("Index added");
    }

    [Fact]
    public void Generate_NoIndexDrifts_IndexSectionOmitted()
    {
        var session = EmptySession() with
        {
            DriftedTables      = [DriftedResult() with { IndexChanges = [AddedIndex("IX_Order_Status")] }],
            IncludeIndexDrifts = false,
        };

        var report = _generator.Generate(session);

        report.Should().NotContain("Index drift");
        report.Should().NotContain("`IX_Order_Status`");
    }
}
