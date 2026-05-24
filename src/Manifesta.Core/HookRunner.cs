namespace Manifesta.Core;

/// <summary>
/// Runs shell commands (hooks).
///
/// Contract:
/// - Hooks do NOT run when <c>suppressHooks</c> is true.
/// - A non-zero hook exit returns false.
/// </summary>
public static class HookRunner
{
    /// <summary>Run <paramref name="cmd"/>, reading suppress/quiet state from <paramref name="options"/>.</summary>
    public static Task<bool> RunAsync(
        string        cmd,
        GlobalOptions options,
        TextWriter?   output = null,
        CancellationToken ct = default)
        => RunAsync(cmd, options.SuppressHooks, options.Quiet, output, ct);

    /// <summary>
    /// Run <paramref name="cmd"/> as a shell command.
    /// Returns <c>true</c> on success, <c>false</c> on non-zero exit.
    /// </summary>
    public static async Task<bool> RunAsync(
        string cmd,
        bool suppressHooks = false,
        bool quiet = false,
        TextWriter? output = null,
        CancellationToken ct = default)
    {
        var writer = output ?? Console.Out;

        if (suppressHooks)
        {
            if (!quiet)
                writer.WriteLine($"[dry-run] Skipping hook: {cmd}");
            return true;
        }

        if (!quiet)
            writer.WriteLine($"[hook] Running: {cmd}");

        var psi = BuildProcessStartInfo(cmd);
        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start hook process: {cmd}");

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            writer.WriteLine($"[hook] Failed with exit code {process.ExitCode}: {cmd}");
            return false;
        }

        return true;
    }

    private static System.Diagnostics.ProcessStartInfo BuildProcessStartInfo(string cmd)
    {
        // Escape embedded double-quotes consistently on both platforms.
        var escaped = cmd.Replace("\"", "\\\"");

        // On Windows: cmd /c "<cmd>"
        // On Unix:    /bin/sh -c "<cmd>"
        return OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("cmd", $"/c \"{escaped}\"")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = false,
                RedirectStandardError  = false,
            }
            : new System.Diagnostics.ProcessStartInfo("/bin/sh", $"-c \"{escaped}\"")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = false,
                RedirectStandardError  = false,
            };
    }
}
