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
    public async Task InitDbHelp_ShowsMySqlAndPostgresNotSqlServer()
    {
        if (BinPath is null) return;
        var (code, stdout, _) = await RunAsync("init", "db", "--help");
        code.Should().Be(0);
        stdout.Should().Contain("mysql");
        stdout.Should().Contain("postgres");
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
