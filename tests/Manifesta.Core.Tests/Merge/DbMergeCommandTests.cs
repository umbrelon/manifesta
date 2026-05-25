using System.Text.Json;
using FluentAssertions;
using Manifesta.Core;
using Manifesta.Core.IR;
using Manifesta.Core.Merge;
using Manifesta.Core.Pipeline;
using Xunit;

namespace Manifesta.Core.Tests;

/// <summary>
/// Integration tests for the <c>db merge</c> pipeline.
/// Tests orchestrate the full merge flow (TableLoader → TableMerger →
/// TableDefinitionSerializer → AtomicWriter → MergeReportGenerator)
/// in <c>--input-dir</c> mode, without requiring a live SQL Server connection.
/// </summary>
public sealed class DbMergeCommandTests : IDisposable
{
    // Absolute path to the Fixtures directory alongside this test file.
    private static readonly string FixturesDir =
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private readonly string _tempDir;

    public DbMergeCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"db_merge_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Orchestrates the full merge pipeline, mirroring DbMergeCommand.ExecuteAsync.
    /// Returns the assembled MergeSession; files are written (or not, if dryRun) to disk.
    /// </summary>
    private static async Task<MergeSession> RunMergeAsync(
        string repoRoot,
        string inputDir,
        bool   removeDeleted       = false,
        bool   removeDeletedTables = false,
        string? newTableDir        = null,
        string? schemaFilter       = null,
        bool   dryRun              = false,
        bool   skipNewTables       = false,
        IReadOnlyList<string>? skipDirs = null,
        CancellationToken ct       = default)
    {
        var loader   = new TableLoader();
        var skipList = skipDirs ?? Array.Empty<string>();

        // ── Load live tables from input directory ──────────────────────────────
        var liveTables = await loader.LoadAsync(inputDir, ct);

        if (schemaFilter is not null)
        {
            var schemas = schemaFilter.Split(',')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            liveTables = liveTables
                .Where(t => schemas.Contains(SchemaPrefix(t.Name)))
                .ToList().AsReadOnly();
        }

        // ── Load repo tables, excluding the input-dir subtree ─────────────────
        var allRepoTables = await loader.LoadAsync(repoRoot, skipList, ct);

        var inputDirNorm = Path.GetFullPath(inputDir).TrimEnd(Path.DirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;

        var repoTables = allRepoTables
            .Where(t => !Path.GetFullPath(t.SourceFile)
                .StartsWith(inputDirNorm, StringComparison.OrdinalIgnoreCase))
            .ToList().AsReadOnly();

        // ── Build name → table map; fail on collision ──────────────────────────
        var repoByName = new Dictionary<string, TableDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in repoTables)
        {
            if (repoByName.TryGetValue(t.Name, out var existing))
                throw new ManifestaConfigException(
                    $"Duplicate table name '{t.Name}' found in repo:\n  {existing.SourceFile}\n  {t.SourceFile}");
            repoByName[t.Name] = t;
        }

        // ── Merge ──────────────────────────────────────────────────────────────
        var merger    = new TableMerger();
        var modified  = new List<MergeResult>();
        var unchanged = new List<MergeResult>();
        var newTables = new List<NewTableResult>();
        var liveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var liveTable in liveTables)
        {
            liveNames.Add(liveTable.Name);

            if (repoByName.TryGetValue(liveTable.Name, out var repoTable))
            {
                var result = merger.Merge(repoTable, liveTable, repoTable.SourceFile, removeDeleted);
                if (result.HasChanges || result.OrphanColumnNames.Count > 0)
                    modified.Add(result);
                else
                    unchanged.Add(result);
            }
            else if (!skipNewTables)
            {
                var targetDir = !string.IsNullOrWhiteSpace(newTableDir)
                    ? newTableDir
                    : Path.Combine(repoRoot, "tables");
                newTables.Add(new NewTableResult
                {
                    Table    = liveTable,
                    FilePath = Path.Combine(targetDir, $"{liveTable.Name}.json"),
                });
            }
        }

        // ── Orphan tables ──────────────────────────────────────────────────────
        var orphanTablePaths  = new List<string>();
        var deletedTablePaths = new List<string>();

        foreach (var repoTable in repoTables)
        {
            if (!liveNames.Contains(repoTable.Name))
            {
                if (removeDeletedTables)
                    deletedTablePaths.Add(repoTable.SourceFile);
                else
                    orphanTablePaths.Add(repoTable.SourceFile);
            }
        }

        // ── Aggregate warnings ─────────────────────────────────────────────────
        var orphanColumns = modified
            .SelectMany(r => r.OrphanColumnNames.Select(col => new OrphanColumn
            {
                TableName = r.Merged.Name,
                FieldName = col,
                FilePath  = r.RepoFilePath,
            }))
            .ToList();

        // ── Write files ────────────────────────────────────────────────────────
        IWriter writer = dryRun ? new DryRunWriter() : new AtomicWriter();

        foreach (var result in modified.Where(r => r.HasChanges))
        {
            var json = TableDefinitionSerializer.Serialize(result.Merged);
            await writer.WriteAsync(result.RepoFilePath, json, ct);
        }

        foreach (var nt in newTables)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(nt.FilePath)!);
            var json = TableDefinitionSerializer.Serialize(nt.Table);
            await writer.WriteAsync(nt.FilePath, json, ct);
        }

        foreach (var path in deletedTablePaths)
            if (!dryRun)
                File.Delete(path);

        return new MergeSession
        {
            Source            = $"--input-dir ({inputDir})",
            RootPath          = repoRoot,
            IsDryRun          = dryRun,
            Timestamp         = DateTimeOffset.UtcNow,
            TotalLiveTables   = liveTables.Count,
            Modified          = modified.AsReadOnly(),
            Unchanged         = unchanged.AsReadOnly(),
            NewTables         = newTables.AsReadOnly(),
            OrphanTablePaths  = orphanTablePaths.AsReadOnly(),
            DeletedTablePaths = deletedTablePaths.AsReadOnly(),
            OrphanColumns     = orphanColumns.AsReadOnly(),
        };
    }

    private static string SchemaPrefix(string name)
    {
        var dot = name.IndexOf('.');
        return dot >= 0 ? name[..dot] : name;
    }

    /// <summary>Copies the Fixtures/Repo directory into a subdirectory of the temp folder.</summary>
    private string CopyFixtureRepo(string? subdir = null)
    {
        var dest = subdir is null ? _tempDir : Path.Combine(_tempDir, subdir);
        CopyDirectory(Path.Combine(FixturesDir, "Repo"), dest);
        return dest;
    }

    /// <summary>Copies the Fixtures/Export directory into a subdirectory of the temp folder.</summary>
    private string CopyFixtureExport(string? subdir = "export")
    {
        var dest = Path.Combine(_tempDir, subdir!);
        CopyDirectory(Path.Combine(FixturesDir, "Export"), dest);
        return dest;
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src, "*.json", SearchOption.AllDirectories))
        {
            var rel  = Path.GetRelativePath(src, file);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void WriteJson(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private static JsonElement ReadJson(string path) =>
        JsonDocument.Parse(File.ReadAllText(path)).RootElement;

    // ─────────────────────────────────────────────────────────────────────────
    // 1. Idempotency
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Merge_IdenticalSchemas_NoFilesModified()
    {
        // Arrange: repo and export are structurally identical (dbo.OrderStatus has no changes).
        var repoDir    = CopyFixtureRepo();
        var exportDir  = Path.Combine(_tempDir, "export");
        Directory.CreateDirectory(exportDir);

        // Export contains only dbo.OrderStatus (unchanged from repo)
        File.Copy(
            Path.Combine(FixturesDir, "Export", "dbo.OrderStatus.json"),
            Path.Combine(exportDir, "dbo.OrderStatus.json"));

        var repoFileBefore = File.GetLastWriteTimeUtc(
            Path.Combine(repoDir, "dbo.OrderStatus.json"));

        // Act
        var session = await RunMergeAsync(repoDir, exportDir);

        // Assert
        session.Modified.Should().BeEmpty();
        session.Unchanged.Should().ContainSingle(r => r.Merged.Name == "dbo.OrderStatus");
        session.NewTables.Should().BeEmpty();

        // File must not have been touched
        File.GetLastWriteTimeUtc(Path.Combine(repoDir, "dbo.OrderStatus.json"))
            .Should().Be(repoFileBefore);
    }

    [Fact]
    public async Task Merge_RunTwice_SecondRunWritesNoFiles()
    {
        // Arrange: first merge produces changes; a second run against the same export
        // must not rewrite any file (structural fields are already up-to-date).
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        await RunMergeAsync(repoDir, exportDir);

        // Snapshot every repo file after the first merge.
        var snapshots = Directory.GetFiles(repoDir, "*.json")
            .ToDictionary(f => f, File.ReadAllText);

        // Second run with the same export.
        await RunMergeAsync(repoDir, exportDir);

        // No file content may have changed — structural fields are already current.
        // (The orphan column szLegacyCode still surfaces as a warning, but since
        // HasChanges=false the file is not rewritten.)
        foreach (var (path, before) in snapshots)
            File.ReadAllText(path).Should().Be(before,
                $"{Path.GetFileName(path)} must not be rewritten on a second merge");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 2. Structural updates
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Merge_ColumnTypeChanged_FileUpdated()
    {
        // The fixture export has lStatusID as "int" (was "smallint" in repo).
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        await RunMergeAsync(repoDir, exportDir);

        var merged = ReadJson(Path.Combine(repoDir, "dbo.Order.json"));
        var fields = merged.GetProperty("fields").EnumerateArray().ToList();
        var statusField = fields.Single(f => f.GetProperty("name").GetString() == "lStatusID");

        statusField.GetProperty("type").GetString().Should().Be("int",
            "lStatusID type was changed from smallint to int in the export");
    }

    [Fact]
    public async Task Merge_NewColumnAdded_AppendedAtEnd()
    {
        // The fixture export adds dtCancelledAt (not in repo).
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        await RunMergeAsync(repoDir, exportDir);

        var fields = ReadJson(Path.Combine(repoDir, "dbo.Order.json"))
            .GetProperty("fields").EnumerateArray().ToList();

        var names = fields.Select(f => f.GetProperty("name").GetString()).ToList();
        names.Should().Contain("dtCancelledAt");
        names.Last().Should().Be("dtCancelledAt",
            "new columns from the DB are appended after existing repo columns");
    }

    [Fact]
    public async Task Merge_NewColumnAdded_HasNoDescription()
    {
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        await RunMergeAsync(repoDir, exportDir);

        var fields = ReadJson(Path.Combine(repoDir, "dbo.Order.json"))
            .GetProperty("fields").EnumerateArray().ToList();

        var newField = fields.Single(f => f.GetProperty("name").GetString() == "dtCancelledAt");

        // description is omitted (null) when empty — key should be absent
        newField.TryGetProperty("description", out _).Should().BeFalse(
            "newly added columns should not have a description until a human provides one");
    }

    [Fact]
    public async Task Merge_SessionReportsChanges_TypeAndAddedColumn()
    {
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        var session = await RunMergeAsync(repoDir, exportDir);

        var orderResult = session.Modified.SingleOrDefault(r => r.Merged.Name == "dbo.Order");
        orderResult.Should().NotBeNull();

        orderResult!.FieldChanges.Should().Contain(c =>
            c.Kind == FieldChangeKind.TypeChanged && c.FieldName == "lStatusID");

        orderResult.FieldChanges.Should().Contain(c =>
            c.Kind == FieldChangeKind.Added && c.FieldName == "dtCancelledAt");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 3. Metadata preservation
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Merge_TableDescription_NeverOverwritten()
    {
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        await RunMergeAsync(repoDir, exportDir);

        var merged = ReadJson(Path.Combine(repoDir, "dbo.Order.json"));
        merged.GetProperty("description").GetString()
            .Should().Be("Core order table for managing customer purchases.");
    }

    [Fact]
    public async Task Merge_FieldDescription_PreservedOnTypeChange()
    {
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        await RunMergeAsync(repoDir, exportDir);

        var fields = ReadJson(Path.Combine(repoDir, "dbo.Order.json"))
            .GetProperty("fields").EnumerateArray().ToList();

        var statusField = fields.Single(f => f.GetProperty("name").GetString() == "lStatusID");
        statusField.GetProperty("description").GetString()
            .Should().Be("Current order status. References dbo.OrderStatus.",
                "field descriptions must survive structural changes");
    }

    [Fact]
    public async Task Merge_IsMatchColumn_PreservedOnUnchangedField()
    {
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        await RunMergeAsync(repoDir, exportDir);

        var fields = ReadJson(Path.Combine(repoDir, "dbo.OrderStatus.json"))
            .GetProperty("fields").EnumerateArray().ToList();

        var nameField = fields.Single(f => f.GetProperty("name").GetString() == "szName");
        nameField.GetProperty("isMatchColumn").GetBoolean().Should().BeTrue(
            "isMatchColumn is repo-sovereign and must never be touched by merge");
    }

    [Fact]
    public async Task Merge_Sections_PreservedAfterMerge()
    {
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        await RunMergeAsync(repoDir, exportDir);

        var merged = ReadJson(Path.Combine(repoDir, "dbo.Order.json"));
        merged.TryGetProperty("sections", out var sections).Should().BeTrue();
        sections.EnumerateArray().Select(s => s.GetString())
            .Should().ContainSingle("orders");
    }

    [Fact]
    public async Task Merge_DatabaseTypes_PreservedAfterMerge()
    {
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        await RunMergeAsync(repoDir, exportDir);

        var merged = ReadJson(Path.Combine(repoDir, "dbo.Order.json"));
        merged.TryGetProperty("databaseTypes", out var dbTypes).Should().BeTrue();
        dbTypes.EnumerateArray().Select(t => t.GetString())
            .Should().ContainSingle("MSSQL");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 4. Orphan columns
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Merge_OrphanColumn_WithoutFlag_PreservedAndWarned()
    {
        // szLegacyCode is in the repo but absent from the export.
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        var session = await RunMergeAsync(repoDir, exportDir, removeDeleted: false);

        // Column must still be in the file
        var fields = ReadJson(Path.Combine(repoDir, "dbo.Order.json"))
            .GetProperty("fields").EnumerateArray().ToList();

        fields.Should().Contain(f => f.GetProperty("name").GetString() == "szLegacyCode",
            "orphan columns are preserved by default");

        // Session must report it as a warning
        session.OrphanColumns.Should().Contain(c =>
            c.TableName == "dbo.Order" && c.FieldName == "szLegacyCode");
        session.HasWarnings.Should().BeTrue();
    }

    [Fact]
    public async Task Merge_OrphanColumn_WithRemoveDeleted_RemovedFromFile()
    {
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        var session = await RunMergeAsync(repoDir, exportDir, removeDeleted: true);

        var fields = ReadJson(Path.Combine(repoDir, "dbo.Order.json"))
            .GetProperty("fields").EnumerateArray().ToList();

        fields.Should().NotContain(f => f.GetProperty("name").GetString() == "szLegacyCode",
            "orphan columns must be removed when --remove-deleted-columns is set");

        // No orphan warning in session — it's a confirmed change, not a warning
        session.OrphanColumns.Should().BeEmpty();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 5. New table handling
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Merge_NewTable_FileCreatedInDefaultDir()
    {
        // Arrange: export includes dbo.NewTable which has no repo file.
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        WriteJson(Path.Combine(exportDir, "dbo.NewTable.json"), """
            {
              "name": "dbo.NewTable",
              "fields": [{ "name": "Id", "type": "int", "nullable": false }],
              "primaryKey": ["Id"]
            }
            """);

        await RunMergeAsync(repoDir, exportDir);

        // Default new-table dir is <repoRoot>/tables/
        var expectedPath = Path.Combine(repoDir, "tables", "dbo.NewTable.json");
        File.Exists(expectedPath).Should().BeTrue(
            "new tables should be written to <root>/tables/ by default");

        var created = ReadJson(expectedPath);
        created.GetProperty("name").GetString().Should().Be("dbo.NewTable");
    }

    [Fact]
    public async Task Merge_NewTable_FileCreatedInCustomNewTableDir()
    {
        var repoDir      = CopyFixtureRepo();
        var exportDir    = CopyFixtureExport();
        var customNewDir = Path.Combine(_tempDir, "new-tables");

        WriteJson(Path.Combine(exportDir, "dbo.NewTable.json"), """
            {
              "name": "dbo.NewTable",
              "fields": [{ "name": "Id", "type": "int", "nullable": false }],
              "primaryKey": ["Id"]
            }
            """);

        await RunMergeAsync(repoDir, exportDir, newTableDir: customNewDir);

        File.Exists(Path.Combine(customNewDir, "dbo.NewTable.json")).Should().BeTrue(
            "new tables should be written to --new-table-dir when specified");
    }

    [Fact]
    public async Task Merge_SkipNewTables_NoFileCreatedForNewTable()
    {
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        WriteJson(Path.Combine(exportDir, "dbo.NewTable.json"), """
            {
              "name": "dbo.NewTable",
              "fields": [{ "name": "Id", "type": "int", "nullable": false }],
              "primaryKey": ["Id"]
            }
            """);

        var session = await RunMergeAsync(repoDir, exportDir, skipNewTables: true);

        File.Exists(Path.Combine(repoDir, "tables", "dbo.NewTable.json")).Should().BeFalse(
            "no new file should be created when --skip-new-tables is set");
        session.NewTables.Should().BeEmpty(
            "session should report zero new tables when --skip-new-tables is set");
    }

    [Fact]
    public async Task Merge_NewTable_ContainsOnlyDbFields_NoMetadata()
    {
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        WriteJson(Path.Combine(exportDir, "dbo.NewTable.json"), """
            {
              "name": "dbo.NewTable",
              "fields": [{ "name": "Id", "type": "int", "nullable": false }],
              "primaryKey": ["Id"]
            }
            """);

        await RunMergeAsync(repoDir, exportDir);

        var created = ReadJson(Path.Combine(repoDir, "tables", "dbo.NewTable.json"));

        created.TryGetProperty("description", out _).Should().BeFalse(
            "new table files must not have description — metadata must be added by a human");
        created.TryGetProperty("sections", out _).Should().BeFalse(
            "new table files must not have sections");
        created.TryGetProperty("databaseTypes", out _).Should().BeFalse(
            "new table files must not have databaseTypes");
    }

    [Fact]
    public async Task Merge_NewTable_SessionListsItUnderNewTables()
    {
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        WriteJson(Path.Combine(exportDir, "dbo.NewTable.json"), """
            {
              "name": "dbo.NewTable",
              "fields": [{ "name": "Id", "type": "int", "nullable": false }],
              "primaryKey": ["Id"]
            }
            """);

        var session = await RunMergeAsync(repoDir, exportDir);

        session.NewTables.Should().ContainSingle(nt => nt.Table.Name == "dbo.NewTable");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 6. Orphan tables
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Merge_OrphanTable_WithoutFlag_FilePreservedAndWarned()
    {
        // Arrange: repo has dbo.GhostTable that does not appear in the export.
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        var ghostPath = Path.Combine(repoDir, "dbo.GhostTable.json");
        WriteJson(ghostPath, """
            { "name": "dbo.GhostTable", "fields": [], "primaryKey": [] }
            """);

        var session = await RunMergeAsync(repoDir, exportDir);

        File.Exists(ghostPath).Should().BeTrue(
            "orphan table files are preserved by default");

        session.OrphanTablePaths.Should().Contain(ghostPath);
        session.HasWarnings.Should().BeTrue();
    }

    [Fact]
    public async Task Merge_OrphanTable_WithRemoveDeletedTables_FileDeleted()
    {
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        var ghostPath = Path.Combine(repoDir, "dbo.GhostTable.json");
        WriteJson(ghostPath, """
            { "name": "dbo.GhostTable", "fields": [], "primaryKey": [] }
            """);

        var session = await RunMergeAsync(
            repoDir, exportDir,
            removeDeleted: true, removeDeletedTables: true);

        File.Exists(ghostPath).Should().BeFalse(
            "orphan table files must be deleted when --remove-deleted-tables is set");

        session.DeletedTablePaths.Should().Contain(ghostPath);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 7. Dry run
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Merge_DryRun_NoFilesWritten()
    {
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        // Record the state of every repo file before the dry run.
        var snapshots = Directory.GetFiles(repoDir, "*.json")
            .ToDictionary(f => f, f => File.ReadAllText(f));

        var session = await RunMergeAsync(repoDir, exportDir, dryRun: true);

        // No file content may have changed.
        foreach (var (path, before) in snapshots)
            File.ReadAllText(path).Should().Be(before,
                $"{Path.GetFileName(path)} must not be modified during --dry-run");

        // No new files should have appeared in the repo root.
        var filesAfter = Directory.GetFiles(repoDir, "*.json");
        filesAfter.Should().HaveCount(snapshots.Count);

        // Session must still describe what would have changed.
        session.IsDryRun.Should().BeTrue();
        session.Modified.Should().NotBeEmpty("dry run session must still compute the diff");
    }

    [Fact]
    public async Task Merge_DryRun_OrphanTableFileNotDeleted()
    {
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        var ghostPath = Path.Combine(repoDir, "dbo.GhostTable.json");
        WriteJson(ghostPath, """{ "name": "dbo.GhostTable", "fields": [] }""");

        await RunMergeAsync(
            repoDir, exportDir,
            removeDeleted: true, removeDeletedTables: true,
            dryRun: true);

        File.Exists(ghostPath).Should().BeTrue("dry run must not delete files");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 8. Schema filter
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Merge_SchemaFilter_OnlyMatchingTablesProcessed()
    {
        // Arrange: export has both dbo and app tables.
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        WriteJson(Path.Combine(exportDir, "app.Config.json"), """
            {
              "name": "app.Config",
              "fields": [{ "name": "Key", "type": "nvarchar(100)", "nullable": false }],
              "primaryKey": ["Key"]
            }
            """);

        var session = await RunMergeAsync(repoDir, exportDir, schemaFilter: "dbo");

        // app.Config is in the export but filtered out — should not appear in session.
        session.NewTables.Should().NotContain(nt => nt.Table.Name == "app.Config");
        session.TotalLiveTables.Should().Be(2, "only the two dbo tables pass the filter");
    }

    [Fact]
    public async Task Merge_SchemaFilter_MultipleSchemas_BothProcessed()
    {
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        // Add an app-schema repo table and matching export file.
        WriteJson(Path.Combine(repoDir, "app.Config.json"), """
            { "name": "app.Config", "fields": [{ "name": "Key", "type": "nvarchar(50)", "nullable": false }] }
            """);
        WriteJson(Path.Combine(exportDir, "app.Config.json"), """
            { "name": "app.Config", "fields": [{ "name": "Key", "type": "nvarchar(100)", "nullable": false }], "primaryKey": ["Key"] }
            """);

        var session = await RunMergeAsync(repoDir, exportDir, schemaFilter: "dbo,app");

        session.TotalLiveTables.Should().Be(3, "dbo.Order + dbo.OrderStatus + app.Config");
        session.Modified.Should().Contain(r => r.Merged.Name == "app.Config",
            "app.Config has a type change (50 → 100) so it must appear in Modified");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 9. Collision detection
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Merge_DuplicateTableNameInRepo_ThrowsConfigException()
    {
        // Two repo files claim the same name — should fail with exit 4 equivalent.
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        WriteJson(Path.Combine(repoDir, "sub", "dbo.Order.json"), """
            { "name": "dbo.Order", "fields": [] }
            """);

        var act = () => RunMergeAsync(repoDir, exportDir);

        await act.Should().ThrowAsync<ManifestaConfigException>()
            .WithMessage("*Duplicate table name*dbo.Order*");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 10. Merge report
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Merge_Report_ContainsCorrectSummaryCounts()
    {
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        var session = await RunMergeAsync(repoDir, exportDir);

        var report = new MergeReportGenerator().Generate(session);

        report.Should().Contain($"| Tables scanned (live) | {session.TotalLiveTables} |");
        report.Should().Contain($"| Files modified | {session.Modified.Count} |");
        report.Should().Contain($"| Files unchanged | {session.Unchanged.Count} |");
    }

    [Fact]
    public async Task Merge_Report_ListsModifiedTablesWithChanges()
    {
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        var session = await RunMergeAsync(repoDir, exportDir);
        var report  = new MergeReportGenerator().Generate(session);

        report.Should().Contain("dbo.Order");
        report.Should().Contain("Column type changed");
        report.Should().Contain("Column added");
        report.Should().Contain("dtCancelledAt");
    }

    [Fact]
    public async Task Merge_Report_ContainsOrphanColumnWarning()
    {
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        var session = await RunMergeAsync(repoDir, exportDir);
        var report  = new MergeReportGenerator().Generate(session);

        report.Should().Contain("szLegacyCode");
        report.Should().Contain("--remove-deleted-columns");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 11. FK handling
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Merge_SoftFk_PreservedWhenAbsentFromExport()
    {
        var repoDir   = Path.Combine(_tempDir, "repo");
        var exportDir = Path.Combine(_tempDir, "export");

        WriteJson(Path.Combine(repoDir, "dbo.Order.json"), """
            {
              "name": "dbo.Order",
              "fields": [
                { "name": "Id",         "type": "int", "nullable": false },
                { "name": "CustomerId", "type": "int", "nullable": false }
              ],
              "primaryKey": ["Id"],
              "foreignKeys": [
                {
                  "sourceField": "CustomerId",
                  "targetTable": "dbo.Customer",
                  "targetField": "Id",
                  "kind": "logical"
                }
              ]
            }
            """);

        // Export has no FK (DB doesn't enforce the soft relationship).
        WriteJson(Path.Combine(exportDir, "dbo.Order.json"), """
            {
              "name": "dbo.Order",
              "fields": [
                { "name": "Id",         "type": "int", "nullable": false },
                { "name": "CustomerId", "type": "int", "nullable": false }
              ],
              "primaryKey": ["Id"]
            }
            """);

        var session = await RunMergeAsync(repoDir, exportDir);

        var fks = ReadJson(Path.Combine(repoDir, "dbo.Order.json"))
            .GetProperty("foreignKeys").EnumerateArray().ToList();

        fks.Should().ContainSingle("logical FK must be preserved even when absent from the DB");
        fks[0].GetProperty("kind").GetString().Should().Be("logical");

        // Logical FK removal is always silent — no warning.
        session.HasWarnings.Should().BeFalse();
    }

    [Fact]
    public async Task Merge_PhysicalFkRemovedFromDb_AutoDeletedFromFile()
    {
        var repoDir   = Path.Combine(_tempDir, "repo");
        var exportDir = Path.Combine(_tempDir, "export");

        WriteJson(Path.Combine(repoDir, "dbo.Order.json"), """
            {
              "name": "dbo.Order",
              "fields": [
                { "name": "Id",       "type": "int", "nullable": false },
                { "name": "StatusId", "type": "int", "nullable": false }
              ],
              "primaryKey": ["Id"],
              "foreignKeys": [
                { "sourceField": "StatusId", "targetTable": "dbo.OrderStatus", "targetField": "Id" }
              ]
            }
            """);

        // Export has the FK dropped — DB no longer enforces this constraint.
        WriteJson(Path.Combine(exportDir, "dbo.Order.json"), """
            {
              "name": "dbo.Order",
              "fields": [
                { "name": "Id",       "type": "int", "nullable": false },
                { "name": "StatusId", "type": "int", "nullable": false }
              ],
              "primaryKey": ["Id"]
            }
            """);

        var session = await RunMergeAsync(repoDir, exportDir);

        // Physical FK is auto-deleted from the written file.
        var root = ReadJson(Path.Combine(repoDir, "dbo.Order.json"));
        root.TryGetProperty("foreignKeys", out _).Should().BeFalse(
            "Physical FK absent from DB export must be auto-deleted");

        // Auto-deletion is a structural change — table lands in Modified, not Unchanged.
        session.Modified.Should().ContainSingle(r => r.Merged.Name == "dbo.Order");
        session.HasWarnings.Should().BeFalse();
    }

    [Fact]
    public async Task Merge_FkCascadeDeleteChanged_UpdatedInFile()
    {
        var repoDir   = Path.Combine(_tempDir, "repo");
        var exportDir = Path.Combine(_tempDir, "export");

        WriteJson(Path.Combine(repoDir, "dbo.Order.json"), """
            {
              "name": "dbo.Order",
              "fields": [
                { "name": "Id",       "type": "int", "nullable": false },
                { "name": "StatusId", "type": "int", "nullable": false }
              ],
              "primaryKey": ["Id"],
              "foreignKeys": [
                { "sourceField": "StatusId", "targetTable": "dbo.OrderStatus", "targetField": "Id", "cascadeDelete": false }
              ]
            }
            """);

        WriteJson(Path.Combine(exportDir, "dbo.Order.json"), """
            {
              "name": "dbo.Order",
              "fields": [
                { "name": "Id",       "type": "int", "nullable": false },
                { "name": "StatusId", "type": "int", "nullable": false }
              ],
              "primaryKey": ["Id"],
              "foreignKeys": [
                { "sourceField": "StatusId", "targetTable": "dbo.OrderStatus", "targetField": "Id", "cascadeDelete": true }
              ]
            }
            """);

        await RunMergeAsync(repoDir, exportDir);

        var fks = ReadJson(Path.Combine(repoDir, "dbo.Order.json"))
            .GetProperty("foreignKeys").EnumerateArray().ToList();

        fks.Should().ContainSingle();
        fks[0].GetProperty("cascadeDelete").GetBoolean().Should().BeTrue(
            "cascadeDelete is DB-authoritative and must be updated from the export");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 12. Serialization round-trip
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Merge_OutputFile_IsValidJsonWithSchemaVersion()
    {
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        await RunMergeAsync(repoDir, exportDir);

        foreach (var file in Directory.GetFiles(repoDir, "*.json"))
        {
            var json = File.ReadAllText(file);
            var act  = () => JsonDocument.Parse(json);
            act.Should().NotThrow($"{Path.GetFileName(file)} must be valid JSON after merge");

            var root = JsonDocument.Parse(json).RootElement;
            root.TryGetProperty("$schemaVersion", out var ver).Should().BeTrue();
            ver.GetString().Should().Be("1.0");
        }
    }

    [Fact]
    public async Task Merge_PrimaryKeyPreserved_InMergedFile()
    {
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        await RunMergeAsync(repoDir, exportDir);

        var merged = ReadJson(Path.Combine(repoDir, "dbo.Order.json"));
        var pk = merged.GetProperty("primaryKey").EnumerateArray()
            .Select(e => e.GetString()).ToList();

        pk.Should().ContainSingle("lOrderID");
    }

    [Fact]
    public async Task Merge_EmptyExportDir_NoFilesModified()
    {
        var repoDir   = CopyFixtureRepo();
        var exportDir = Path.Combine(_tempDir, "empty-export");
        Directory.CreateDirectory(exportDir);

        var session = await RunMergeAsync(repoDir, exportDir);

        session.TotalLiveTables.Should().Be(0);
        session.Modified.Should().BeEmpty();

        // All repo tables become orphans.
        session.OrphanTablePaths.Should().HaveCount(2,
            "both repo files are orphans when the export is empty");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 13. Skip-dir support
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Merge_SkipDir_ConfigFileInSkippedDirIsNotDeserializedAsTable()
    {
        // Reproduce the user-reported error:
        //   "Invalid JSON in _\manifesta.config.json: JSON deserialization for type
        //    'Manifesta.Core.IR.TableDefinition' was missing required properties including: 'Name'"
        //
        // Arrange: place a non-table JSON file under a "_" subdirectory.
        // Without skip support this causes a ManifestaSchemException.
        // With skip support the file is silently ignored.

        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        var skipSubdir = Path.Combine(repoDir, "_");
        Directory.CreateDirectory(skipSubdir);
        File.WriteAllText(
            Path.Combine(skipSubdir, "manifesta.config.json"),
            """{ "paths": { "root": "../", "skip": ["_"] } }""");

        // Without skip: the loader would throw ManifestaSchemException.
        // With skip:    the file is ignored and the merge proceeds normally.
        var act = () => RunMergeAsync(repoDir, exportDir, skipDirs: ["_"]);

        await act.Should().NotThrowAsync(
            "files inside a skipped directory must not be deserialized as table definitions");

        var session = await RunMergeAsync(repoDir, exportDir, skipDirs: ["_"]);

        // The config file must not have been treated as a table.
        session.Unchanged.Should().NotContain(
            r => r.Merged.Name.Contains("manifesta"),
            "manifesta.config.json must not appear as a known table");
        session.NewTables.Should().NotContain(
            nt => nt.Table.Name.Contains("manifesta"),
            "manifesta.config.json must not appear as a new table");
    }

    [Fact]
    public async Task Merge_SkipDir_TableInsideSkippedDirIsIgnored()
    {
        // Even a *valid* table JSON placed inside a skipped directory must be excluded.
        var repoDir   = CopyFixtureRepo();
        var exportDir = CopyFixtureExport();

        var skipSubdir = Path.Combine(repoDir, "_");
        Directory.CreateDirectory(skipSubdir);
        File.WriteAllText(
            Path.Combine(skipSubdir, "dbo.HiddenTable.json"),
            """{ "name": "dbo.HiddenTable", "fields": [], "primaryKey": [] }""");

        var session = await RunMergeAsync(repoDir, exportDir, skipDirs: ["_"]);

        session.Unchanged.Should().NotContain(r => r.Merged.Name == "dbo.HiddenTable");
        session.NewTables.Should().NotContain(nt => nt.Table.Name == "dbo.HiddenTable");
        session.OrphanTablePaths.Should().NotContain(p => p.Contains("HiddenTable"),
            "tables in skipped dirs are invisible to the loader and cannot become orphans");
    }
}
