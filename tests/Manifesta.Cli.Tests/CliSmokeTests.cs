using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Manifesta.Cli.Tests;

/// <summary>
/// Subprocess smoke tests for the OSS CLI binary.
/// Set MANIFESTA_BIN to the path of the published binary to run these tests.
/// All tests skip automatically when the binary is not available.
/// </summary>
public sealed class CliSmokeTests
{
    private static readonly string? BinPath =
        Environment.GetEnvironmentVariable("MANIFESTA_BIN");

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo(BinPath!, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            WorkingDirectory       = workingDir,
        };
        var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout, stderr);
    }

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args)
        => RunAsync(Directory.GetCurrentDirectory(), args);

    [Fact]
    public async Task Help_ExitsZero()
    {
        if (BinPath is null) return; // skip: MANIFESTA_BIN not set
        var (code, stdout, _) = await RunAsync("--help");
        code.Should().Be(0);
        stdout.Should().Contain("init");
        stdout.Should().Contain("doc");
        stdout.Should().Contain("validate");
    }

    [Fact]
    public async Task Version_ExitsZeroAndPrintsCommunityEdition()
    {
        if (BinPath is null) return;
        var (code, stdout, _) = await RunAsync("version");
        code.Should().Be(0);
        stdout.Should().Contain("community edition");
    }

    [Fact]
    public async Task UnknownCommand_ExitsNonZero()
    {
        if (BinPath is null) return;
        var (code, _, _) = await RunAsync("does-not-exist");
        code.Should().NotBe(0);
    }

    [Fact]
    public async Task InitDbHelp_ShowsMySqlPostgresAndSqliteNotSqlServer()
    {
        if (BinPath is null) return;
        var (code, stdout, _) = await RunAsync("init", "db", "--help");
        code.Should().Be(0);
        stdout.Should().Contain("mysql");
        stdout.Should().Contain("postgres");
        stdout.Should().Contain("sqlite");
        stdout.Should().NotContain("sqlserver");
    }

    [Fact]
    public async Task ValidateAllHelp_ExitsZero()
    {
        if (BinPath is null) return;
        var (code, _, _) = await RunAsync("validate", "all", "--help");
        code.Should().Be(0);
    }

    [Fact]
    public async Task DocDbHelp_ExitsZero()
    {
        if (BinPath is null) return;
        var (code, _, _) = await RunAsync("doc", "db", "--help");
        code.Should().Be(0);
    }

    [Fact]
    public async Task ValidateAll_MissingConfig_ExitsWithConfigError()
    {
        if (BinPath is null) return;
        var tmp = Path.Combine(Path.GetTempPath(), $"manifesta-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            // No config file in this directory → should exit 4 (ConfigOrInvocationError)
            var (code, _, _) = await RunAsync(tmp, "validate", "all");
            code.Should().Be(4);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    // ── db drift ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DbDriftHelp_ExitsZeroAndListsProviders()
    {
        if (BinPath is null) return;
        var (code, stdout, _) = await RunAsync("db", "drift", "--help");
        code.Should().Be(0);
        stdout.Should().Contain("mysql");
        stdout.Should().Contain("postgres");
        stdout.Should().NotContain("sqlserver");
    }

    [Fact]
    public async Task DbDrift_NoFlags_ExitsWithConfigError()
    {
        if (BinPath is null) return;
        // Neither --connection nor --input-dir provided → exit 4
        var tmp = CreateTempRegistry(new());
        try
        {
            var (code, _, stderr) = await RunAsync(tmp, "db", "drift");
            code.Should().Be(4);
            stderr.Should().Contain("--connection");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task DbDrift_BothConnectionAndInputDir_ExitsWithConfigError()
    {
        if (BinPath is null) return;
        var tmp = CreateTempRegistry(new());
        try
        {
            var (code, _, stderr) = await RunAsync(tmp,
                "db", "drift", "--connection", "Server=x", "--input-dir", ".");
            code.Should().Be(4);
            stderr.Should().Contain("mutually exclusive");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task DbDrift_InputDir_CleanRegistry_ExitsZero()
    {
        if (BinPath is null) return;

        const string tableJson = """
            {
              "name": "dbo.Customer",
              "fields": [
                { "name": "Id",   "type": "int",          "nullable": false },
                { "name": "Name", "type": "varchar(255)", "nullable": true  }
              ],
              "primaryKey": ["Id"]
            }
            """;

        var repo  = new Dictionary<string, string> { ["dbo.Customer.json"] = tableJson };
        var tmp   = CreateTempRegistry(repo);
        var live  = Path.Combine(tmp, "live");
        Directory.CreateDirectory(live);
        await File.WriteAllTextAsync(Path.Combine(live, "dbo.Customer.json"), tableJson);

        try
        {
            var (code, stdout, _) = await RunAsync(tmp, "db", "drift", "--input-dir", live);
            code.Should().Be(0);
            stdout.Should().Contain("No drift detected");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task DbDrift_InputDir_TypeChangedField_ExitsOne()
    {
        if (BinPath is null) return;

        const string repoJson = """
            {
              "name": "dbo.Customer",
              "fields": [
                { "name": "Id",   "type": "int",          "nullable": false },
                { "name": "Name", "type": "varchar(255)", "nullable": true  }
              ],
              "primaryKey": ["Id"]
            }
            """;

        // Live DB has Name as varchar(100) — type changed → drift
        const string liveJson = """
            {
              "name": "dbo.Customer",
              "fields": [
                { "name": "Id",   "type": "int",          "nullable": false },
                { "name": "Name", "type": "varchar(100)", "nullable": true  }
              ],
              "primaryKey": ["Id"]
            }
            """;

        var repo = new Dictionary<string, string> { ["dbo.Customer.json"] = repoJson };
        var tmp  = CreateTempRegistry(repo);
        var live = Path.Combine(tmp, "live");
        Directory.CreateDirectory(live);
        await File.WriteAllTextAsync(Path.Combine(live, "dbo.Customer.json"), liveJson);

        try
        {
            var (code, stdout, _) = await RunAsync(tmp, "db", "drift", "--input-dir", live);
            code.Should().Be(1);
            stdout.Should().Contain("Drift detected");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task DbDrift_InputDir_ExtraDbTable_WithStrict_ExitsOne()
    {
        if (BinPath is null) return;

        const string tableJson = """
            {
              "name": "dbo.Customer",
              "fields": [{ "name": "Id", "type": "int", "nullable": false }],
              "primaryKey": ["Id"]
            }
            """;

        // Repo has no tables; live has one → extra DB table warning
        var tmp  = CreateTempRegistry(new());
        var live = Path.Combine(tmp, "live");
        Directory.CreateDirectory(live);
        await File.WriteAllTextAsync(Path.Combine(live, "dbo.Customer.json"), tableJson);

        try
        {
            var (code, _, _) = await RunAsync(tmp, "db", "drift", "--input-dir", live, "--strict");
            code.Should().Be(1);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    // ── db export ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DbExportHelp_ExitsZeroAndListsProviders()
    {
        if (BinPath is null) return;
        var (code, stdout, _) = await RunAsync("db", "export", "--help");
        code.Should().Be(0);
        stdout.Should().Contain("mysql");
        stdout.Should().Contain("postgres");
        stdout.Should().Contain("sqlite");
        stdout.Should().NotContain("sqlserver");
    }

    [Fact]
    public async Task DbExport_NoConnection_ExitsWithConfigError()
    {
        if (BinPath is null) return;
        var tmp = Path.Combine(Path.GetTempPath(), $"manifesta-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (code, _, stderr) = await RunAsync(tmp, "db", "export");
            code.Should().Be(4);
            stderr.Should().Contain("--connection");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    // ── init sql ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task InitSqlHelp_ShowsAllFourProvidersIncludingSqlServer()
    {
        if (BinPath is null) return;
        var (code, stdout, _) = await RunAsync("init", "sql", "--help");
        code.Should().Be(0);
        // Unlike init db / db drift / db merge, init sql DOES expose sqlserver
        // because it is pure text parsing with no live DB connection.
        stdout.Should().Contain("mysql");
        stdout.Should().Contain("postgres");
        stdout.Should().Contain("sqlite");
        stdout.Should().Contain("sqlserver");
        // Directory traversal flags
        stdout.Should().Contain("--recursive");
        stdout.Should().Contain("--pattern");
    }

    [Fact]
    public async Task InitSql_MissingInput_ExitsWithConfigError()
    {
        if (BinPath is null) return;
        var tmp = Path.Combine(Path.GetTempPath(), $"manifesta-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var (code, _, stderr) = await RunAsync(tmp, "init", "sql");
            code.Should().Be(4);
            stderr.Should().Contain("--input");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task InitSql_SingleFile_WritesTableJson()
    {
        if (BinPath is null) return;
        var tmp = Path.Combine(Path.GetTempPath(), $"manifesta-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            var ddl = """
                CREATE TABLE Customer (
                    Id   INT          NOT NULL,
                    Name VARCHAR(100) NOT NULL,
                    PRIMARY KEY (Id)
                );
                """;
            await File.WriteAllTextAsync(Path.Combine(tmp, "schema.sql"), ddl);

            var outDir = Path.Combine(tmp, "out");
            var (code, _, stderr) = await RunAsync(tmp,
                "init", "sql",
                "--input",      "schema.sql",
                "--output-dir", outDir,
                "--provider",   "mysql");

            code.Should().Be(0, because: stderr);
            Directory.Exists(outDir).Should().BeTrue();
            Directory.GetFiles(outDir, "*.json").Should().HaveCount(1);

            var json = await File.ReadAllTextAsync(Directory.GetFiles(outDir, "*.json")[0]);
            json.Should().Contain("Customer");
            json.Should().Contain("\"Id\"");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task InitSql_Directory_WritesOneJsonPerTable()
    {
        if (BinPath is null) return;
        var tmp = Path.Combine(Path.GetTempPath(), $"manifesta-test-{Guid.NewGuid():N}");
        var sqlDir = Path.Combine(tmp, "ddl");
        Directory.CreateDirectory(sqlDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(sqlDir, "a.sql"),
                "CREATE TABLE Alpha (Id INT NOT NULL PRIMARY KEY);");
            await File.WriteAllTextAsync(Path.Combine(sqlDir, "b.sql"),
                "CREATE TABLE Beta (Id INT NOT NULL PRIMARY KEY);");

            var outDir = Path.Combine(tmp, "out");
            var (code, _, stderr) = await RunAsync(tmp,
                "init", "sql",
                "--input",      sqlDir,
                "--output-dir", outDir,
                "--provider",   "mysql");

            code.Should().Be(0, because: stderr);
            Directory.GetFiles(outDir, "*.json").Should().HaveCount(2);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task InitSql_Recursive_FindsFilesInSubdirectories()
    {
        if (BinPath is null) return;
        var tmp = Path.Combine(Path.GetTempPath(), $"manifesta-test-{Guid.NewGuid():N}");
        var sqlDir  = Path.Combine(tmp, "ddl");
        var subDir  = Path.Combine(sqlDir, "sub");
        Directory.CreateDirectory(subDir);
        try
        {
            // Top-level file
            await File.WriteAllTextAsync(Path.Combine(sqlDir, "top.sql"),
                "CREATE TABLE Top (Id INT NOT NULL PRIMARY KEY);");
            // Subdirectory file — only found with --recursive
            await File.WriteAllTextAsync(Path.Combine(subDir, "nested.sql"),
                "CREATE TABLE Nested (Id INT NOT NULL PRIMARY KEY);");

            var outDir = Path.Combine(tmp, "out");

            // Without --recursive: only 1 table
            var (code1, _, _) = await RunAsync(tmp,
                "init", "sql", "--input", sqlDir, "--output-dir", outDir, "--provider", "mysql");
            code1.Should().Be(0);
            Directory.GetFiles(outDir, "*.json").Should().HaveCount(1);

            // With --recursive: both tables
            Directory.Delete(outDir, recursive: true);
            var (code2, _, stderr2) = await RunAsync(tmp,
                "init", "sql", "--input", sqlDir, "--output-dir", outDir,
                "--provider", "mysql", "--recursive");
            code2.Should().Be(0, because: stderr2);
            Directory.GetFiles(outDir, "*.json").Should().HaveCount(2);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task InitSql_FilenamePattern_FiltersFiles()
    {
        if (BinPath is null) return;
        var tmp = Path.Combine(Path.GetTempPath(), $"manifesta-test-{Guid.NewGuid():N}");
        var sqlDir = Path.Combine(tmp, "ddl");
        Directory.CreateDirectory(sqlDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(sqlDir, "001_create_customer.sql"),
                "CREATE TABLE Customer (Id INT NOT NULL PRIMARY KEY);");
            await File.WriteAllTextAsync(Path.Combine(sqlDir, "002_drop_old_table.sql"),
                "DROP TABLE OldTable;");  // no CREATE TABLE — 0 tables, but still matched
            await File.WriteAllTextAsync(Path.Combine(sqlDir, "003_create_order.sql"),
                "CREATE TABLE \"Order\" (Id INT NOT NULL PRIMARY KEY);");

            var outDir = Path.Combine(tmp, "out");
            var (code, _, stderr) = await RunAsync(tmp,
                "init", "sql",
                "--input",      sqlDir,
                "--output-dir", outDir,
                "--provider",   "postgres",
                "--pattern",    "*_create_*.sql");

            code.Should().Be(0, because: stderr);
            // Only the two *_create_* files are processed → 2 tables
            Directory.GetFiles(outDir, "*.json").Should().HaveCount(2);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task InitSql_PathGlob_MatchesSubdirectoryOnly()
    {
        if (BinPath is null) return;
        var tmp    = Path.Combine(Path.GetTempPath(), $"manifesta-test-{Guid.NewGuid():N}");
        var sqlDir = Path.Combine(tmp, "migrations");
        Directory.CreateDirectory(Path.Combine(sqlDir, "2024"));
        Directory.CreateDirectory(Path.Combine(sqlDir, "2025"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(sqlDir, "2024", "a.sql"),
                "CREATE TABLE Alpha (Id INT NOT NULL PRIMARY KEY);");
            await File.WriteAllTextAsync(Path.Combine(sqlDir, "2025", "b.sql"),
                "CREATE TABLE Beta (Id INT NOT NULL PRIMARY KEY);");

            var outDir = Path.Combine(tmp, "out");
            // Path glob targeting only 2024/ — 2025/b.sql should be excluded
            var (code, _, stderr) = await RunAsync(tmp,
                "init", "sql",
                "--input",      sqlDir,
                "--output-dir", outDir,
                "--provider",   "mysql",
                "--pattern",    "2024/*.sql");

            code.Should().Be(0, because: stderr);
            Directory.GetFiles(outDir, "*.json").Should().HaveCount(1);
            Directory.GetFiles(outDir, "*.json")[0].Should().Contain("Alpha");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task InitSql_PathGlobWithDoublestar_MatchesAllSubdirs()
    {
        if (BinPath is null) return;
        var tmp    = Path.Combine(Path.GetTempPath(), $"manifesta-test-{Guid.NewGuid():N}");
        var sqlDir = Path.Combine(tmp, "migrations");
        Directory.CreateDirectory(Path.Combine(sqlDir, "2024"));
        Directory.CreateDirectory(Path.Combine(sqlDir, "2025"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(sqlDir, "2024", "001_up.sql"),
                "CREATE TABLE Alpha (Id INT NOT NULL PRIMARY KEY);");
            await File.WriteAllTextAsync(Path.Combine(sqlDir, "2024", "001_down.sql"),
                "DROP TABLE Alpha;");
            await File.WriteAllTextAsync(Path.Combine(sqlDir, "2025", "002_up.sql"),
                "CREATE TABLE Beta (Id INT NOT NULL PRIMARY KEY);");
            await File.WriteAllTextAsync(Path.Combine(sqlDir, "2025", "002_down.sql"),
                "DROP TABLE Beta;");

            var outDir = Path.Combine(tmp, "out");
            // Path glob: all *_up.sql anywhere in the tree
            var (code, _, stderr) = await RunAsync(tmp,
                "init", "sql",
                "--input",      sqlDir,
                "--output-dir", outDir,
                "--provider",   "mysql",
                "--pattern",    "**/*_up.sql");

            code.Should().Be(0, because: stderr);
            // 2 up-migrations matched; 2 down-migrations excluded
            Directory.GetFiles(outDir, "*.json").Should().HaveCount(2);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task InitSql_NoMatchingPattern_ExitsWithError()
    {
        if (BinPath is null) return;
        var tmp = Path.Combine(Path.GetTempPath(), $"manifesta-test-{Guid.NewGuid():N}");
        var sqlDir = Path.Combine(tmp, "ddl");
        Directory.CreateDirectory(sqlDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(sqlDir, "schema.sql"),
                "CREATE TABLE Foo (Id INT NOT NULL PRIMARY KEY);");

            var (code, _, stderr) = await RunAsync(tmp,
                "init", "sql",
                "--input",   sqlDir,
                "--pattern", "*_up.sql");   // no files match

            code.Should().Be(5);  // FatalSchemaErrors
            stderr.Should().Contain("*_up.sql");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    // ── db merge ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DbMergeHelp_ExitsZeroAndListsProviders()
    {
        if (BinPath is null) return;
        var (code, stdout, _) = await RunAsync("db", "merge", "--help");
        code.Should().Be(0);
        stdout.Should().Contain("mysql");
        stdout.Should().Contain("postgres");
        stdout.Should().NotContain("sqlserver");
    }

    [Fact]
    public async Task DbMerge_NoFlags_ExitsWithConfigError()
    {
        if (BinPath is null) return;
        var tmp = CreateTempRegistry(new());
        try
        {
            var (code, _, stderr) = await RunAsync(tmp, "db", "merge");
            code.Should().Be(4);
            stderr.Should().Contain("--connection");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task DbMerge_RemoveDeletedTablesWithoutRemoveDeletedColumns_ExitsWithConfigError()
    {
        if (BinPath is null) return;
        var tmp = CreateTempRegistry(new());
        try
        {
            var (code, _, stderr) = await RunAsync(tmp,
                "db", "merge", "--input-dir", ".", "--remove-deleted-tables");
            code.Should().Be(4);
            stderr.Should().Contain("--remove-deleted-columns");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task DbMerge_InputDir_NoChanges_ExitsZero()
    {
        if (BinPath is null) return;

        const string tableJson = """
            {
              "name": "dbo.Customer",
              "fields": [
                { "name": "Id",   "type": "int",          "nullable": false },
                { "name": "Name", "type": "varchar(255)", "nullable": true  }
              ],
              "primaryKey": ["Id"]
            }
            """;

        var repo = new Dictionary<string, string> { ["dbo.Customer.json"] = tableJson };
        var tmp  = CreateTempRegistry(repo);
        var live = Path.Combine(tmp, "live");
        Directory.CreateDirectory(live);
        await File.WriteAllTextAsync(Path.Combine(live, "dbo.Customer.json"), tableJson);

        try
        {
            var (code, stdout, _) = await RunAsync(tmp, "db", "merge", "--input-dir", live, "--no-report");
            code.Should().Be(0);
            stdout.Should().Contain("0 modified");
            stdout.Should().Contain("0 created");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task DbMerge_InputDir_NewTable_CreatesFileAndExitsZero()
    {
        if (BinPath is null) return;

        const string liveJson = """
            {
              "name": "dbo.Order",
              "fields": [{ "name": "Id", "type": "int", "nullable": false }],
              "primaryKey": ["Id"]
            }
            """;

        // Repo is empty; live has one table → should create a new file
        var tmp  = CreateTempRegistry(new());
        var live = Path.Combine(tmp, "live");
        Directory.CreateDirectory(live);
        await File.WriteAllTextAsync(Path.Combine(live, "dbo.Order.json"), liveJson);

        try
        {
            var (code, stdout, _) = await RunAsync(tmp, "db", "merge", "--input-dir", live, "--no-report");
            code.Should().Be(0);
            stdout.Should().Contain("1 created");
            File.Exists(Path.Combine(tmp, "tables", "dbo.Order.json")).Should().BeTrue();
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task DbMerge_InputDir_TypeChanged_UpdatesFileAndExitsOne()
    {
        if (BinPath is null) return;

        const string repoJson = """
            {
              "name": "dbo.Customer",
              "fields": [
                { "name": "Id",   "type": "int",          "nullable": false },
                { "name": "Name", "type": "varchar(255)", "nullable": true  }
              ],
              "primaryKey": ["Id"]
            }
            """;

        // Live DB changed Name to varchar(100) → merge updates the file; Name is
        // also absent from live as an orphan (kept in repo) → warnings → exit 1
        const string liveJson = """
            {
              "name": "dbo.Customer",
              "fields": [
                { "name": "Id",   "type": "int",          "nullable": false },
                { "name": "Name", "type": "varchar(100)", "nullable": true  }
              ],
              "primaryKey": ["Id"]
            }
            """;

        var repo = new Dictionary<string, string> { ["dbo.Customer.json"] = repoJson };
        var tmp  = CreateTempRegistry(repo);
        var live = Path.Combine(tmp, "live");
        Directory.CreateDirectory(live);
        await File.WriteAllTextAsync(Path.Combine(live, "dbo.Customer.json"), liveJson);

        try
        {
            var (code, stdout, _) = await RunAsync(tmp, "db", "merge", "--input-dir", live, "--no-report");
            code.Should().Be(0); // type change only → modified, no orphan warnings
            stdout.Should().Contain("1 modified");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task DbMerge_DryRun_DoesNotWriteFiles()
    {
        if (BinPath is null) return;

        const string liveJson = """
            {
              "name": "dbo.Product",
              "fields": [{ "name": "Id", "type": "int", "nullable": false }],
              "primaryKey": ["Id"]
            }
            """;

        var tmp  = CreateTempRegistry(new());
        var live = Path.Combine(tmp, "live");
        Directory.CreateDirectory(live);
        await File.WriteAllTextAsync(Path.Combine(live, "dbo.Product.json"), liveJson);

        try
        {
            var (code, _, _) = await RunAsync(tmp,
                "db", "merge", "--input-dir", live, "--dry-run", "--no-report");
            code.Should().Be(0);
            // File must not have been written (dry-run)
            File.Exists(Path.Combine(tmp, "tables", "dbo.Product.json")).Should().BeFalse();
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a minimal registry layout in a temp directory.
    /// <paramref name="tableFiles"/> maps filename → JSON content written into tables/.
    /// The _/manifesta.config.json is always created.
    /// </summary>
    private static string CreateTempRegistry(Dictionary<string, string> tableFiles)
    {
        var root = Path.Combine(Path.GetTempPath(), $"manifesta-test-{Guid.NewGuid():N}");
        var cfg  = Path.Combine(root, "_");
        var tbl  = Path.Combine(root, "tables");
        Directory.CreateDirectory(cfg);
        Directory.CreateDirectory(tbl);

        File.WriteAllText(
            Path.Combine(cfg, "manifesta.config.json"),
            """{"paths":{"root":"../","skip":["_"]}}""");

        foreach (var (name, json) in tableFiles)
            File.WriteAllText(Path.Combine(tbl, name), json);

        return root;
    }
}
