using FluentAssertions;
using Manifesta.Core;
using Xunit;

namespace Manifesta.Core.Tests;

// Note: HookRunner has been moved to Manifesta.Core for testing

/// <summary>
/// Tests for <see cref="HookRunner"/>.
/// Covers hook execution, suppression, OS-specific behavior, and error handling.
/// Uses real process execution with safe shell commands (echo, true/false).
/// </summary>
public sealed class HookRunnerTests
{
    private static string SuccessCommand => OperatingSystem.IsWindows() ? "cmd /c exit 0" : "sh -c 'exit 0'";
    private static string FailureCommand => OperatingSystem.IsWindows() ? "cmd /c exit 1" : "sh -c 'exit 1'";

    // ── Hook suppression ───────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SuppressHooks_SkipsExecution()
    {
        var output = new StringWriter();

        var result = await HookRunner.RunAsync(SuccessCommand, suppressHooks: true, quiet: false, output: output);

        result.Should().BeTrue("suppressed hooks should return success without executing");
        output.ToString().Should().Contain("[dry-run]").And.Contain("Skipping hook");
    }

    [Fact]
    public async Task RunAsync_HooksEnabled_ExecutesCommand()
    {
        var output = new StringWriter();

        var result = await HookRunner.RunAsync(SuccessCommand, suppressHooks: false, quiet: false, output: output);

        result.Should().BeTrue("successful hook should return true");
        output.ToString().Should().Contain("[hook]");
    }

    // ── Command success/failure ────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_CommandSucceedsExitCode0_ReturnsTrue()
    {
        var result = await HookRunner.RunAsync(SuccessCommand, suppressHooks: false, quiet: true);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_CommandFailsNonZeroExit_ReturnsFalse()
    {
        var output = new StringWriter();

        var result = await HookRunner.RunAsync(FailureCommand, suppressHooks: false, quiet: true, output: output);

        result.Should().BeFalse();
        output.ToString().Should().Contain("Failed with exit code");
    }

    // ── Output control ────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_QuietMode_NoHookMessages()
    {
        var output = new StringWriter();

        await HookRunner.RunAsync(SuccessCommand, suppressHooks: false, quiet: true, output: output);

        output.ToString().Should().BeEmpty("quiet mode should suppress all hook output");
    }

    [Fact]
    public async Task RunAsync_VerboseMode_OutputsHookInfo()
    {
        var output = new StringWriter();

        await HookRunner.RunAsync(SuccessCommand, suppressHooks: false, quiet: false, output: output);

        output.ToString().Should().Contain("[hook]").And.Contain("Running:");
    }

    [Fact]
    public async Task RunAsync_NullOutputWriter_DefaultsToConsoleOut()
    {
        var action = async () => await HookRunner.RunAsync(SuccessCommand, suppressHooks: false, quiet: true, output: null);

        await action.Should().NotThrowAsync();
    }

    // ── Cancellation ───────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_CancellationRequested_CancelsProcess()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var action = async () => await HookRunner.RunAsync(
            OperatingSystem.IsWindows() ? "cmd /c timeout 10" : "sleep 10",
            suppressHooks: false,
            quiet: true,
            ct: cts.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── OS-specific shell behavior ─────────────────────────────────────────

    [SkippableFact]
    public async Task RunAsync_Windows_UsesCmdShell()
    {
        Skip.IfNot(OperatingSystem.IsWindows());

        var result = await HookRunner.RunAsync("cmd /c exit 0", suppressHooks: false, quiet: true);

        result.Should().BeTrue();
    }

    [SkippableFact]
    public async Task RunAsync_Unix_UsesShShell()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());

        var result = await HookRunner.RunAsync("sh -c 'exit 0'", suppressHooks: false, quiet: true);

        result.Should().BeTrue();
    }

    // ── Command with special characters ────────────────────────────────────

    [SkippableFact]
    public async Task RunAsync_UnixCommand_ProperlyEscapesQuotes()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());

        var result = await HookRunner.RunAsync(
            "sh -c 'echo \"test string\"'",
            suppressHooks: false,
            quiet: true);

        result.Should().BeTrue();
    }

    // ── Multiple hooks in sequence ────────────────────────────────────────

    [Fact]
    public async Task RunAsync_MultipleHookCalls_AllSucceed()
    {
        var result1 = await HookRunner.RunAsync(SuccessCommand, suppressHooks: false, quiet: true);
        var result2 = await HookRunner.RunAsync(SuccessCommand, suppressHooks: false, quiet: true);
        var result3 = await HookRunner.RunAsync(SuccessCommand, suppressHooks: false, quiet: true);

        result1.Should().BeTrue();
        result2.Should().BeTrue();
        result3.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_FailedHook_ReturnsFalse()
    {
        var output = new StringWriter();

        var result = await HookRunner.RunAsync(FailureCommand, suppressHooks: false, quiet: false, output: output);

        result.Should().BeFalse();
        output.ToString().Should().Contain("Failed with exit code 1");
    }
}
