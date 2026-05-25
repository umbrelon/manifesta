using FluentAssertions;
using Manifesta.Core.IR;
using Manifesta.Core.Merge;
using Xunit;

namespace Manifesta.Core.Tests;

public class MergeReportGeneratorTests
{
    private readonly MergeReportGenerator _generator = new();
    private static readonly DateTimeOffset _ts = new(2026, 5, 11, 12, 0, 0, TimeSpan.Zero);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static MergeSession EmptySession(bool dryRun = false) => new()
    {
        Source          = "--connection (Server=localhost;Database=test;)",
        RootPath        = "/repo/root",
        IsDryRun        = dryRun,
        Timestamp       = _ts,
        TotalLiveTables = 0,
    };

    private static MergeResult UnchangedResult(string name = "dbo.Order") => new()
    {
        Merged       = new TableDefinition { Name = name },
        RepoFilePath = $"tables/{name}.json",
    };

    private static MergeResult ModifiedResult(
        string name,
        IEnumerable<FieldChange>? fieldChanges = null,
        IEnumerable<FkChange>? fkChanges = null,
        PrimaryKeyChange? pkChange = null) => new()
    {
        Merged           = new TableDefinition { Name = name },
        RepoFilePath     = $"tables/{name}.json",
        FieldChanges     = (fieldChanges ?? []).ToList().AsReadOnly(),
        FkChanges        = (fkChanges    ?? []).ToList().AsReadOnly(),
        PrimaryKeyChange = pkChange,
    };

    private static FieldChange AddedField(string name)    => new() { Kind = FieldChangeKind.Added,   FieldName = name, NewValue = "int" };
    private static FieldChange RemovedField(string name)  => new() { Kind = FieldChangeKind.Removed, FieldName = name, OldValue = "int" };
    private static FieldChange TypeChanged(string name)   => new() { Kind = FieldChangeKind.TypeChanged, FieldName = name, OldValue = "smallint", NewValue = "int" };

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

        report.Should().Contain("# Manifesta DB Merge Report");
        report.Should().Contain("2026-05-11");
        report.Should().Contain("Server=localhost;Database=test;");
        report.Should().Contain("/repo/root");
        report.Should().Contain("Dry run: false");
    }

    [Fact]
    public void Generate_DryRunTrue_ReflectedInHeader()
    {
        var report = _generator.Generate(EmptySession(dryRun: true));

        report.Should().Contain("Dry run: true");
    }

    // ── Summary counts ────────────────────────────────────────────────────────

    [Fact]
    public void Generate_SummaryCounts_MatchSessionData()
    {
        var session = EmptySession() with
        {
            TotalLiveTables  = 10,
            Modified         = [ModifiedResult("dbo.A"), ModifiedResult("dbo.B")],
            Unchanged        = [UnchangedResult("dbo.C")],
            NewTables        = [new NewTableResult { Table = new TableDefinition { Name = "dbo.New" }, FilePath = "tables/dbo.New.json" }],
            DeletedTablePaths = ["tables/dbo.Old.json"],
            OrphanColumns    = [new OrphanColumn { TableName = "dbo.A", FieldName = "OldCol", FilePath = "tables/dbo.A.json" }],
            OrphanTablePaths = ["tables/dbo.Ghost.json"],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("| Tables scanned (live) | 10 |");
        report.Should().Contain("| Repo files matched | 3 |");   // 2 modified + 1 unchanged
        report.Should().Contain("| Files modified | 2 |");
        report.Should().Contain("| Files created (new tables) | 1 |");
        report.Should().Contain("| Files unchanged | 1 |");
        report.Should().Contain("| Files deleted | 1 |");
        report.Should().Contain("| Orphan columns (warnings) | 1 |");
        report.Should().Contain("| Orphan tables (warnings) | 1 |");
    }

    // ── Modified tables section ───────────────────────────────────────────────

    [Fact]
    public void Generate_ModifiedSection_AppearsWhenThereAreModifications()
    {
        var session = EmptySession() with
        {
            Modified = [ModifiedResult("dbo.Order", fieldChanges: [AddedField("NewCol")])],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("## Modified Tables");
        report.Should().Contain("### dbo.Order");
        report.Should().Contain("Column added");
        report.Should().Contain("`NewCol`");
    }

    [Fact]
    public void Generate_ModifiedSection_AbsentWhenNoModifications()
    {
        var report = _generator.Generate(EmptySession());

        report.Should().NotContain("## Modified Tables");
    }

    [Fact]
    public void Generate_ModifiedSection_ShowsAllChangeKinds()
    {
        var session = EmptySession() with
        {
            Modified =
            [
                ModifiedResult("dbo.Order",
                    fieldChanges: [AddedField("NewCol"), RemovedField("OldCol"), TypeChanged("StatusId")],
                    fkChanges:    [AddedFk("NewCol"), CascadeChanged("StatusId")],
                    pkChange:     new PrimaryKeyChange { Before = ["OldId"], After = ["NewId"] }),
            ],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("Column added");
        report.Should().Contain("Column removed");
        report.Should().Contain("Column type changed");
        report.Should().Contain("FK added");
        report.Should().Contain("FK cascadeDelete changed");
        report.Should().Contain("Primary key changed");
    }

    // ── New tables section ────────────────────────────────────────────────────

    [Fact]
    public void Generate_NewTablesSection_AppearsWithWarning()
    {
        var session = EmptySession() with
        {
            NewTables = [new NewTableResult
            {
                Table    = new TableDefinition { Name = "dbo.AuditLog" },
                FilePath = "tables/dbo.AuditLog.json",
            }],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("## New Tables");
        report.Should().Contain("### dbo.AuditLog");
        report.Should().Contain("tables/dbo.AuditLog.json");
        report.Should().Contain("⚠");
    }

    [Fact]
    public void Generate_NewTablesSection_AbsentWhenNone()
    {
        var report = _generator.Generate(EmptySession());

        report.Should().NotContain("## New Tables");
    }

    // ── Deleted tables section ────────────────────────────────────────────────

    [Fact]
    public void Generate_DeletedTablesSection_AppearsWhenPresent()
    {
        var session = EmptySession() with
        {
            DeletedTablePaths = ["tables/dbo.Obsolete.json"],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("## Deleted Tables");
        report.Should().Contain("tables/dbo.Obsolete.json");
    }

    [Fact]
    public void Generate_DeletedTablesSection_AbsentWhenNone()
    {
        var report = _generator.Generate(EmptySession());

        report.Should().NotContain("## Deleted Tables");
    }

    // ── Warnings section ──────────────────────────────────────────────────────

    [Fact]
    public void Generate_WarningsSection_AbsentOnCleanSession()
    {
        var report = _generator.Generate(EmptySession());

        report.Should().NotContain("## Warnings");
    }

    [Fact]
    public void Generate_OrphanColumnsWarning_ListsTableAndField()
    {
        var session = EmptySession() with
        {
            OrphanColumns = [new OrphanColumn
            {
                TableName = "dbo.Order",
                FieldName = "szLegacyCode",
                FilePath  = "tables/dbo.Order.json",
            }],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("## Warnings");
        report.Should().Contain("### Orphan Columns");
        report.Should().Contain("dbo.Order");
        report.Should().Contain("`szLegacyCode`");
        report.Should().Contain("--remove-deleted-columns");
    }

    [Fact]
    public void Generate_OrphanTablesWarning_ListsFilePath()
    {
        var session = EmptySession() with
        {
            OrphanTablePaths = ["tables/dbo.Ghost.json"],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("### Orphan Tables");
        report.Should().Contain("tables/dbo.Ghost.json");
        report.Should().Contain("--remove-deleted-tables");
    }

    [Fact]
    public void Generate_PhysicalFkRemoved_AppearsInChangeTable()
    {
        // Physical FK removals are auto-deleted by TableMerger and land in FkChanges.
        // The report must render them in the per-table change table, not a separate warnings section.
        var session = EmptySession() with
        {
            Modified = [ModifiedResult("dbo.Order", fkChanges: [RemovedFk("StatusId")])],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("FK removed");
        report.Should().Contain("`StatusId`");
        report.Should().NotContain("Non-Soft FK Removals");
        report.Should().NotContain("preserved but require review");
    }

    // ── Unchanged tables section ──────────────────────────────────────────────

    [Fact]
    public void Generate_UnchangedSection_ListsAllUnchangedTables()
    {
        var session = EmptySession() with
        {
            Unchanged = [UnchangedResult("dbo.A"), UnchangedResult("dbo.B")],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("## Unchanged Tables");
        report.Should().Contain("dbo.A");
        report.Should().Contain("dbo.B");
        report.Should().Contain("2 table(s)");
    }

    [Fact]
    public void Generate_UnchangedSection_AbsentWhenNone()
    {
        var report = _generator.Generate(EmptySession());

        report.Should().NotContain("## Unchanged Tables");
    }

    // ── DefaultChanged field rendering ────────────────────────────────────────

    [Fact]
    public void Generate_DefaultChangedField_RendersCorrectly()
    {
        var change = new FieldChange
        {
            Kind     = FieldChangeKind.DefaultChanged,
            FieldName = "IsActive",
            OldValue  = "0",
            NewValue  = "1",
        };
        var session = EmptySession() with
        {
            Modified = [ModifiedResult("dbo.Order", fieldChanges: [change])],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("Column default changed");
        report.Should().Contain("`0`");
        report.Should().Contain("`1`");
    }

    // ── Orphaned data keys warnings ───────────────────────────────────────────

    [Fact]
    public void Generate_OrphanedDataKeys_AppearsInWarningsSection()
    {
        var session = EmptySession() with
        {
            OrphanedDataKeys = [new OrphanedDataKey { TableName = "dbo.BundleType", ColumnName = "LegacyCode", FilePath = "tables/dbo.BundleType.json" }],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("## Warnings");
        report.Should().Contain("### Orphaned Data Keys");
        report.Should().Contain("dbo.BundleType");
        report.Should().Contain("`LegacyCode`");
        report.Should().Contain("tables/dbo.BundleType.json");
    }

    [Fact]
    public void Generate_OrphanedDataKeys_AbsentWhenNone()
    {
        var report = _generator.Generate(EmptySession());

        report.Should().NotContain("### Orphaned Data Keys");
    }

    [Fact]
    public void Generate_OrphanedDataKeys_SummaryRowReflectsCount()
    {
        var session = EmptySession() with
        {
            OrphanedDataKeys =
            [
                new OrphanedDataKey { TableName = "dbo.A", ColumnName = "Col1", FilePath = "tables/dbo.A.json" },
                new OrphanedDataKey { TableName = "dbo.B", ColumnName = "Col2", FilePath = "tables/dbo.B.json" },
            ],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("| Orphaned data keys (warnings) | 2 |");
    }

    [Fact]
    public void Generate_OrphanedDataKeys_HasWarnings_SetsCorrectly()
    {
        var sessionWith = EmptySession() with
        {
            OrphanedDataKeys = [new OrphanedDataKey { TableName = "dbo.A", ColumnName = "OldCol", FilePath = "f.json" }],
        };
        var sessionWithout = EmptySession();

        sessionWith.HasWarnings.Should().BeTrue();
        sessionWithout.HasWarnings.Should().BeFalse();
    }

    // ── Valid Markdown ─────────────────────────────────────────────────────────

    [Fact]
    public void Generate_OutputIsNonEmpty_StartsWithHeading()
    {
        var report = _generator.Generate(EmptySession());

        report.Should().NotBeNullOrWhiteSpace();
        report.TrimStart().Should().StartWith("# Manifesta DB Merge Report");
    }

    // ── Computed field change rendering ───────────────────────────────────────

    [Fact]
    public void Generate_ComputedExpressionChanged_RenderedCorrectly()
    {
        var change = new FieldChange
        {
            Kind      = FieldChangeKind.ComputedExpressionChanged,
            FieldName = "FullName",
            OldValue  = "([OldExpr])",
            NewValue  = "([NewExpr])",
        };
        var session = EmptySession() with
        {
            Modified = [ModifiedResult("dbo.Person", fieldChanges: [change])],
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
        var change = new FieldChange
        {
            Kind      = FieldChangeKind.IsPersistedChanged,
            FieldName = "Total",
            OldValue  = "false",
            NewValue  = "true",
        };
        var session = EmptySession() with
        {
            Modified = [ModifiedResult("dbo.Person", fieldChanges: [change])],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("Computed persisted flag changed");
        report.Should().Contain("`false`");
        report.Should().Contain("`true`");
        report.Should().Contain("`Total`");
    }

    // ── --force-capture-reference-data ────────────────────────────────────────

    [Fact]
    public void MergeResult_DataRefreshed_MakesHasChangesTrue()
    {
        var result = new MergeResult
        {
            Merged       = new TableDefinition { Name = "dbo.Status" },
            RepoFilePath = "tables/dbo.Status.json",
            DataRefreshed = true,
        };

        result.HasChanges.Should().BeTrue();
    }

    [Fact]
    public void MergeResult_DataRefreshedFalse_DoesNotInflateHasChanges()
    {
        var result = new MergeResult
        {
            Merged       = new TableDefinition { Name = "dbo.Status" },
            RepoFilePath = "tables/dbo.Status.json",
            DataRefreshed = false,
        };

        result.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void Generate_RefreshedDataCount_InSummaryTable()
    {
        var refreshed = new MergeResult
        {
            Merged        = new TableDefinition { Name = "dbo.Status" },
            RepoFilePath  = "tables/dbo.Status.json",
            DataRefreshed = true,
        };
        var session = EmptySession() with { Modified = [refreshed] };

        var report = _generator.Generate(session);

        report.Should().Contain("| Reference tables data-refreshed | 1 |");
    }

    [Fact]
    public void Generate_RefreshedDataCount_ZeroWhenNoRefresh()
    {
        var session = EmptySession() with
        {
            Modified = [ModifiedResult("dbo.Order", fieldChanges: [AddedField("NewCol")])],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("| Reference tables data-refreshed | 0 |");
    }

    [Fact]
    public void Generate_DataRefreshedNote_AppearsPerTableWhenRefreshed()
    {
        var refreshed = new MergeResult
        {
            Merged        = new TableDefinition { Name = "dbo.Status", Data = [new Dictionary<string, System.Text.Json.JsonElement>(), new Dictionary<string, System.Text.Json.JsonElement>()] },
            RepoFilePath  = "tables/dbo.Status.json",
            DataRefreshed = true,
        };
        var session = EmptySession() with { Modified = [refreshed] };

        var report = _generator.Generate(session);

        report.Should().Contain("Reference data refreshed: 2 row(s) captured.");
    }

    [Fact]
    public void Generate_DataRefreshedNote_AbsentWhenNotRefreshed()
    {
        var session = EmptySession() with
        {
            Modified = [ModifiedResult("dbo.Order", fieldChanges: [AddedField("NewCol")])],
        };

        var report = _generator.Generate(session);

        report.Should().NotContain("Reference data refreshed");
    }

    [Fact]
    public void Generate_OrphanedDataKeys_HintMentionsBothCommands()
    {
        var session = EmptySession() with
        {
            OrphanedDataKeys = [new OrphanedDataKey { TableName = "dbo.Status", ColumnName = "OldCol", FilePath = "tables/dbo.Status.json" }],
        };

        var report = _generator.Generate(session);

        report.Should().Contain("db merge");
        report.Should().Contain("db export");
        report.Should().Contain("--force-capture-reference-data");
    }

    [Fact]
    public void MergeSession_RefreshedDataCount_CountsOnlyDataRefreshedResults()
    {
        var refreshed = new MergeResult
        {
            Merged        = new TableDefinition { Name = "dbo.Status" },
            RepoFilePath  = "tables/dbo.Status.json",
            DataRefreshed = true,
        };
        var notRefreshed = new MergeResult
        {
            Merged        = new TableDefinition { Name = "dbo.Order" },
            RepoFilePath  = "tables/dbo.Order.json",
            DataRefreshed = false,
        };
        var session = EmptySession() with { Modified = [refreshed, notRefreshed] };

        session.RefreshedDataCount.Should().Be(1);
    }
}
