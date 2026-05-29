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

    // ── db drift --ddl-file ───────────────────────────────────────────────────

    // Shared DDL / repo JSON used across multiple DDL drift tests.
    private const string DdlCustomerSql = """
        CREATE TABLE `dbo.Customer` (
            Id   INT          NOT NULL,
            Name VARCHAR(255) NULL,
            PRIMARY KEY (Id)
        );
        """;

    private const string RepoCustomerJson = """
        {
          "name": "dbo.Customer",
          "fields": [
            { "name": "Id",   "type": "int",          "nullable": false },
            { "name": "Name", "type": "varchar(255)", "nullable": true  }
          ],
          "primaryKey": ["Id"]
        }
        """;

    [Fact]
    public async Task DbDriftDdl_Help_ShowsSqlServerInDescription()
    {
        if (BinPath is null) return;
        var (code, stdout, _) = await RunAsync("db", "drift", "--help");
        code.Should().Be(0);
        stdout.Should().Contain("--ddl-file");
        stdout.Should().Contain("sqlserver");   // mentioned in --ddl-file description
    }

    [Fact]
    public async Task DbDriftDdl_NoMode_ErrorMentionsAllThreeModes()
    {
        if (BinPath is null) return;
        var tmp = CreateTempRegistry(new());
        try
        {
            var (code, _, stderr) = await RunAsync(tmp, "db", "drift");
            code.Should().Be(4);
            stderr.Should().Contain("--connection");
            stderr.Should().Contain("--input-dir");
            stderr.Should().Contain("--ddl-file");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task DbDriftDdl_MutuallyExclusive_ExitsWithConfigError()
    {
        if (BinPath is null) return;
        var tmp = CreateTempRegistry(new());
        try
        {
            var (code, _, stderr) = await RunAsync(tmp,
                "db", "drift", "--ddl-file", "schema.sql", "--input-dir", ".");
            code.Should().Be(4);
            stderr.Should().Contain("mutually exclusive");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task DbDriftDdl_NoDrift_ExitsZero()
    {
        if (BinPath is null) return;
        var repo = new Dictionary<string, string> { ["dbo.Customer.json"] = RepoCustomerJson };
        var tmp  = CreateTempRegistry(repo);
        try
        {
            var ddl = Path.Combine(tmp, "schema.sql");
            await File.WriteAllTextAsync(ddl, DdlCustomerSql);

            var (code, stdout, stderr) = await RunAsync(tmp,
                "db", "drift", "--ddl-file", ddl, "--provider", "mysql");
            code.Should().Be(0, because: stderr);
            stdout.Should().Contain("No drift detected");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task DbDriftDdl_TypeChanged_ExitsOne()
    {
        if (BinPath is null) return;
        var repo = new Dictionary<string, string> { ["dbo.Customer.json"] = RepoCustomerJson };
        var tmp  = CreateTempRegistry(repo);
        try
        {
            // DDL has varchar(100) where repo expects varchar(255)
            var ddl = Path.Combine(tmp, "schema.sql");
            await File.WriteAllTextAsync(ddl, DdlCustomerSql.Replace("VARCHAR(255)", "VARCHAR(100)"));

            var (code, stdout, stderr) = await RunAsync(tmp,
                "db", "drift", "--ddl-file", ddl, "--provider", "mysql");
            code.Should().Be(1, because: stderr);
            stdout.Should().Contain("Drift detected");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task DbDriftDdl_ParseError_ExitsOne()
    {
        if (BinPath is null) return;
        var tmp = CreateTempRegistry(new());
        try
        {
            var ddl = Path.Combine(tmp, "bad.sql");
            await File.WriteAllTextAsync(ddl, "this is not valid SQL CREATE TABLE garbage (((");

            var (code, _, _) = await RunAsync(tmp,
                "db", "drift", "--ddl-file", ddl, "--provider", "mysql");
            // No tables parsed + errors → exit 1
            code.Should().BeOneOf(1, 5);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task DbDriftDdl_ParseError_WarnOnly_ExitsZero()
    {
        if (BinPath is null) return;
        // Repo has one table; DDL file has parse errors but also one valid table
        // that matches the repo → --warn-only should exit 0 (best-effort diff).
        var repo = new Dictionary<string, string> { ["dbo.Customer.json"] = RepoCustomerJson };
        var tmp  = CreateTempRegistry(repo);
        try
        {
            // One valid table + one deliberately broken statement after it
            var ddl = Path.Combine(tmp, "schema.sql");
            await File.WriteAllTextAsync(ddl,
                DdlCustomerSql + "\n\nCREATE TABLE Broken ( col UNCLOSED");

            var (code, stdout, _) = await RunAsync(tmp,
                "db", "drift", "--ddl-file", ddl, "--provider", "mysql", "--warn-only");
            code.Should().Be(0);
            stdout.Should().Contain("No drift detected");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task DbDriftDdl_SqlServerProvider_Accepted()
    {
        if (BinPath is null) return;
        var repo = new Dictionary<string, string>
        {
            ["dbo.Customer.json"] = """
                {
                  "name": "dbo.Customer",
                  "fields": [
                    { "name": "Id",   "type": "int",           "nullable": false },
                    { "name": "Name", "type": "nvarchar(255)", "nullable": true  }
                  ],
                  "primaryKey": ["Id"]
                }
                """
        };
        var tmp = CreateTempRegistry(repo);
        try
        {
            var ddl = Path.Combine(tmp, "schema.sql");
            await File.WriteAllTextAsync(ddl, """
                CREATE TABLE [dbo].[Customer] (
                    [Id]   INT            NOT NULL,
                    [Name] NVARCHAR(255)  NULL,
                    PRIMARY KEY ([Id])
                );
                """);

            var (code, stdout, stderr) = await RunAsync(tmp,
                "db", "drift", "--ddl-file", ddl, "--provider", "sqlserver");
            code.Should().Be(0, because: stderr);
            stdout.Should().Contain("No drift detected");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task DbDriftDdl_Directory_NoDrift_ExitsZero()
    {
        if (BinPath is null) return;
        var repo = new Dictionary<string, string> { ["dbo.Customer.json"] = RepoCustomerJson };
        var tmp  = CreateTempRegistry(repo);
        try
        {
            var ddlDir = Path.Combine(tmp, "ddl");
            Directory.CreateDirectory(ddlDir);
            await File.WriteAllTextAsync(Path.Combine(ddlDir, "customer.sql"), DdlCustomerSql);

            var (code, _, stderr) = await RunAsync(tmp,
                "db", "drift", "--ddl-file", ddlDir, "--provider", "mysql");
            code.Should().Be(0, because: stderr);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task DbDriftDdl_Recursive_FindsNestedFile()
    {
        if (BinPath is null) return;
        var repo = new Dictionary<string, string> { ["dbo.Customer.json"] = RepoCustomerJson };
        var tmp  = CreateTempRegistry(repo);
        try
        {
            var subDir = Path.Combine(tmp, "ddl", "sub");
            Directory.CreateDirectory(subDir);
            await File.WriteAllTextAsync(Path.Combine(subDir, "customer.sql"), DdlCustomerSql);

            // Without --recursive: file not found → error
            var ddlDir = Path.Combine(tmp, "ddl");
            var (code1, _, _) = await RunAsync(tmp,
                "db", "drift", "--ddl-file", ddlDir, "--provider", "mysql");
            code1.Should().Be(5);   // FatalSchemaErrors — no files found

            // With --recursive: table found and matches → no drift
            var (code2, stdout2, stderr2) = await RunAsync(tmp,
                "db", "drift", "--ddl-file", ddlDir, "--provider", "mysql", "--recursive");
            code2.Should().Be(0, because: stderr2);
            stdout2.Should().Contain("No drift detected");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task DbDriftDdl_PathGlob_FiltersSubdir()
    {
        if (BinPath is null) return;
        // Repo has only Customer; live DDL directory has Customer + Order.
        // A path glob targeting only the customer file should → no drift.
        var repo = new Dictionary<string, string> { ["dbo.Customer.json"] = RepoCustomerJson };
        var tmp  = CreateTempRegistry(repo);
        try
        {
            var ddlDir = Path.Combine(tmp, "ddl");
            var v1Dir  = Path.Combine(ddlDir, "v1");
            var v2Dir  = Path.Combine(ddlDir, "v2");
            Directory.CreateDirectory(v1Dir);
            Directory.CreateDirectory(v2Dir);

            await File.WriteAllTextAsync(Path.Combine(v1Dir, "customer.sql"), DdlCustomerSql);
            await File.WriteAllTextAsync(Path.Combine(v2Dir, "order.sql"), """
                CREATE TABLE `dbo.Order` (
                    Id INT NOT NULL,
                    PRIMARY KEY (Id)
                );
                """);

            // Only process v1/ — dbo.Order is excluded
            var (code, stdout, stderr) = await RunAsync(tmp,
                "db", "drift", "--ddl-file", ddlDir,
                "--provider", "mysql", "--pattern", "v1/*.sql");
            code.Should().Be(0, because: stderr);
            stdout.Should().Contain("No drift detected");
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
    public async Task InitSql_FileWithIntraFileDuplicates_IsSkippedWithWarning_OtherTablesStillWritten()
    {
        if (BinPath is null) return;
        var tmp    = Path.Combine(Path.GetTempPath(), $"manifesta-test-{Guid.NewGuid():N}");
        var sqlDir = Path.Combine(tmp, "ddl");
        Directory.CreateDirectory(sqlDir);
        try
        {
            // Conditional DDL pattern: two CREATE TABLE for the same name in one file.
            await File.WriteAllTextAsync(Path.Combine(sqlDir, "conditional.sql"), """
                -- variant without identity
                CREATE TABLE Widget (Id INT NOT NULL PRIMARY KEY);
                -- variant with identity
                CREATE TABLE Widget (Id INT NOT NULL AUTO_INCREMENT PRIMARY KEY);
                """);

            // A clean file — should still be processed despite the bad file.
            await File.WriteAllTextAsync(Path.Combine(sqlDir, "clean.sql"),
                "CREATE TABLE Gadget (Id INT NOT NULL PRIMARY KEY);");

            var outDir = Path.Combine(tmp, "out");
            var (code, stdout, stderr) = await RunAsync(tmp,
                "init", "sql",
                "--input",      sqlDir,
                "--output-dir", outDir,
                "--provider",   "mysql");

            // Non-zero because a file was skipped.
            code.Should().Be(1);
            // Only the clean table should be written — Widget was skipped along with its file.
            Directory.GetFiles(outDir, "*.json").Should().HaveCount(1);
            Path.GetFileName(Directory.GetFiles(outDir, "*.json")[0]).Should().Contain("Gadget");
            // Warning appears on stderr mentioning the offending file.
            stderr.Should().Contain("Warning");
            stderr.Should().Contain("conditional.sql");
            stderr.Should().Contain("Widget");
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

    [Fact]
    public async Task InitSql_CreatesConfigAndSectionFile()
    {
        if (BinPath is null) return;
        var tmp = Path.Combine(Path.GetTempPath(), $"manifesta-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, "schema.sql"), """
                CREATE TABLE Actor   (Id INT NOT NULL PRIMARY KEY);
                CREATE TABLE Address (Id INT NOT NULL PRIMARY KEY);
                """);

            var tablesDir = Path.Combine(tmp, "manifesta", "tables");
            var (code, _, stderr) = await RunAsync(tmp,
                "init", "sql",
                "--input",      "schema.sql",
                "--output-dir", tablesDir,
                "--provider",   "mysql");

            code.Should().Be(0, because: stderr);

            // Tables written to --output-dir
            Directory.GetFiles(tablesDir, "*.json").Should().HaveCount(2);

            // Config scaffolded in the parent's _/ directory
            var configPath  = Path.Combine(tmp, "manifesta", "_", "manifesta.config.json");
            var sectionPath = Path.Combine(tmp, "manifesta", "_", "document-sections", "all-tables.json");

            File.Exists(configPath).Should().BeTrue("manifesta.config.json should be created automatically");
            File.Exists(sectionPath).Should().BeTrue("all-tables.json should be created automatically");

            var config  = await File.ReadAllTextAsync(configPath);
            config.Should().Contain("\"root\"").And.Contain("\"../\"");

            var section = await File.ReadAllTextAsync(sectionPath);
            section.Should().Contain("Actor").And.Contain("Address");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task InitSql_DefaultOutputDir_CreatesConfigInCurrentDirectory()
    {
        if (BinPath is null) return;
        var tmp = Path.Combine(Path.GetTempPath(), $"manifesta-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, "schema.sql"),
                "CREATE TABLE Widget (Id INT NOT NULL PRIMARY KEY);");

            // --output-dir defaults to ./tables, so config root is ./  (i.e. tmp)
            var (code, _, stderr) = await RunAsync(tmp,
                "init", "sql",
                "--input",    "schema.sql",
                "--provider", "mysql");

            code.Should().Be(0, because: stderr);

            var configPath = Path.Combine(tmp, "_", "manifesta.config.json");
            File.Exists(configPath).Should().BeTrue("config should land in the current directory's _/ folder");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task InitSql_ExistingConfig_NotOverwrittenWithoutFlag()
    {
        if (BinPath is null) return;
        var tmp = Path.Combine(Path.GetTempPath(), $"manifesta-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmp, "schema.sql"),
                "CREATE TABLE Widget (Id INT NOT NULL PRIMARY KEY);");

            // Run once to create config
            await RunAsync(tmp, "init", "sql", "--input", "schema.sql", "--provider", "mysql");

            var configPath = Path.Combine(tmp, "_", "manifesta.config.json");
            File.Exists(configPath).Should().BeTrue();

            // Overwrite with known sentinel content
            await File.WriteAllTextAsync(configPath, "{\"sentinel\":true}");

            // Run again WITHOUT --overwrite — config must not be touched
            var (code, _, stderr) = await RunAsync(tmp, "init", "sql", "--input", "schema.sql",
                "--provider", "mysql", "--overwrite");
            code.Should().Be(0, because: stderr);

            // Now run without --overwrite — sentinel should be preserved
            await File.WriteAllTextAsync(configPath, "{\"sentinel\":true}");
            await RunAsync(tmp, "init", "sql", "--input", "schema.sql", "--provider", "mysql");
            var content = await File.ReadAllTextAsync(configPath);
            content.Should().Contain("sentinel");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    // ── init sql --default-schema ────────────────────────────────────────────

    [Fact]
    public async Task InitSql_DefaultSchema_PrefixesUnqualifiedTables()
    {
        if (BinPath is null) return;
        var tmp = Path.Combine(Path.GetTempPath(), $"manifesta-test-{Guid.NewGuid():N}");
        var sqlDir = Path.Combine(tmp, "ddl");
        Directory.CreateDirectory(sqlDir);
        try
        {
            // Unqualified table — should receive the default schema prefix.
            await File.WriteAllTextAsync(Path.Combine(sqlDir, "t.sql"),
                "CREATE TABLE Widget (Id INT NOT NULL PRIMARY KEY);");

            var outDir = Path.Combine(tmp, "out");
            var (code, _, _) = await RunAsync(tmp,
                "init", "sql",
                "--input",          sqlDir,
                "--output-dir",     outDir,
                "--provider",       "sqlserver",
                "--default-schema", "dbo");

            code.Should().Be(0);
            Directory.GetFiles(outDir, "*.json").Should().ContainSingle()
                .Which.Should().EndWith("dbo.Widget.json");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task InitSql_DefaultSchema_ExplicitSchemaInDdlTakesPrecedence()
    {
        if (BinPath is null) return;
        var tmp = Path.Combine(Path.GetTempPath(), $"manifesta-test-{Guid.NewGuid():N}");
        var sqlDir = Path.Combine(tmp, "ddl");
        Directory.CreateDirectory(sqlDir);
        try
        {
            // Table has explicit [other].[Widget] — default-schema must not override it.
            await File.WriteAllTextAsync(Path.Combine(sqlDir, "t.sql"),
                "CREATE TABLE [other].[Widget] (Id INT NOT NULL PRIMARY KEY);");

            var outDir = Path.Combine(tmp, "out");
            var (code, _, _) = await RunAsync(tmp,
                "init", "sql",
                "--input",          sqlDir,
                "--output-dir",     outDir,
                "--provider",       "sqlserver",
                "--default-schema", "dbo");

            code.Should().Be(0);
            Directory.GetFiles(outDir, "*.json").Should().ContainSingle()
                .Which.Should().EndWith("other.Widget.json");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task InitSql_SchemaFlags_IgnoredForMySql()
    {
        if (BinPath is null) return;
        var tmp = Path.Combine(Path.GetTempPath(), $"manifesta-test-{Guid.NewGuid():N}");
        var sqlDir = Path.Combine(tmp, "ddl");
        Directory.CreateDirectory(sqlDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(sqlDir, "t.sql"),
                "CREATE TABLE Widget (Id INT NOT NULL PRIMARY KEY);");

            var outDir = Path.Combine(tmp, "out");
            var (code, _, _) = await RunAsync(tmp,
                "init", "sql",
                "--input",          sqlDir,
                "--output-dir",     outDir,
                "--provider",       "mysql",
                "--default-schema", "dbo",
                "--schema",         "dbo");

            code.Should().Be(0);
            // MySQL has no schema namespace — --default-schema and --schema are both ignored.
            // Output should be plain Widget.json, not dbo.Widget.json, and not filtered away.
            var file = Directory.GetFiles(outDir, "*.json").Should().ContainSingle().Which;
            file.Should().EndWith("Widget.json");
            file.Should().NotEndWith("dbo.Widget.json");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task InitSql_SchemaFilter_OnlyIncludesMatchingTables()
    {
        if (BinPath is null) return;
        var tmp = Path.Combine(Path.GetTempPath(), $"manifesta-test-{Guid.NewGuid():N}");
        var sqlDir = Path.Combine(tmp, "ddl");
        Directory.CreateDirectory(sqlDir);
        try
        {
            // Two tables in different schemas in a single file.
            await File.WriteAllTextAsync(Path.Combine(sqlDir, "t.sql"), """
                CREATE TABLE [dbo].[Widget] (Id INT NOT NULL PRIMARY KEY);
                CREATE TABLE [app].[Config] (Id INT NOT NULL PRIMARY KEY);
                """);

            var outDir = Path.Combine(tmp, "out");
            var (code, _, _) = await RunAsync(tmp,
                "init", "sql",
                "--input",      sqlDir,
                "--output-dir", outDir,
                "--provider",   "sqlserver",
                "--schema",     "dbo");

            code.Should().Be(0);
            // Only the dbo table should be written; app.Config is filtered out.
            Directory.GetFiles(outDir, "*.json").Should().ContainSingle()
                .Which.Should().EndWith("dbo.Widget.json");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public async Task InitSql_DefaultSchemaAndSchemaFilter_WorkTogether()
    {
        if (BinPath is null) return;
        var tmp = Path.Combine(Path.GetTempPath(), $"manifesta-test-{Guid.NewGuid():N}");
        var sqlDir = Path.Combine(tmp, "ddl");
        Directory.CreateDirectory(sqlDir);
        try
        {
            // One unqualified (gets dbo. from --default-schema) and one explicit app.Config.
            await File.WriteAllTextAsync(Path.Combine(sqlDir, "t.sql"), """
                CREATE TABLE Widget (Id INT NOT NULL PRIMARY KEY);
                CREATE TABLE [app].[Config] (Id INT NOT NULL PRIMARY KEY);
                """);

            var outDir = Path.Combine(tmp, "out");
            var (code, _, _) = await RunAsync(tmp,
                "init", "sql",
                "--input",          sqlDir,
                "--output-dir",     outDir,
                "--provider",       "sqlserver",
                "--default-schema", "dbo",
                "--schema",         "dbo");

            code.Should().Be(0);
            // Widget gets prefixed to dbo.Widget, then passes the dbo filter.
            // app.Config is filtered out.
            Directory.GetFiles(outDir, "*.json").Should().ContainSingle()
                .Which.Should().EndWith("dbo.Widget.json");
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }
}
