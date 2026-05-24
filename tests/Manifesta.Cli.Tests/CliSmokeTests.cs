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
}
