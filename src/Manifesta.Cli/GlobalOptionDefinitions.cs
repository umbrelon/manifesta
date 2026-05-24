using System.CommandLine;
using Manifesta.Core;

namespace Manifesta.Cli;

/// <summary>
/// Defines and owns all System.CommandLine Option&lt;T&gt; instances for global flags.
/// These are added to the root command and bound into <see cref="GlobalOptions"/> by the host.
/// <see cref="GlobalOptions"/> and <see cref="GlobalDefaults"/> live in
/// <c>Manifesta.Core</c> so Core utilities can accept them directly.
/// </summary>
public static class GlobalOptionDefinitions
{
    public static readonly Option<string> Config = new(
        ["--config"],
        () => GlobalDefaults.Config,
        "Path to config file");

    public static readonly Option<string> Root = new(
        ["--root"],
        () => GlobalDefaults.Root,
        "Root directory for scanning");

    public static readonly Option<bool> Verbose = new(
        ["-v", "--verbose"],
        () => false,
        "Print debug logs (human-readable)");

    public static readonly Option<bool> Quiet = new(
        ["-q", "--quiet"],
        () => false,
        "Suppress all non-error output");

    public static readonly Option<bool> WarnOnly = new(
        ["--warn-only"],
        () => false,
        "Exit 0 even if warnings exist (errors still fail)");

    public static readonly Option<bool> DryRun = new(
        ["--dry-run"],
        () => false,
        "Read-only preview mode — no files written, no git, no hooks");

    public static readonly Option<bool> DryRunWithHooks = new(
        ["--dry-run-with-hooks"],
        () => false,
        "Read-only preview, but lifecycle hooks still execute");

    public static readonly Option<bool> Force = new(
        ["--force"],
        () => false,
        "Bypass idempotency checks (output files overwritten even if unchanged)");

    public static readonly Option<int> Parallel = new(
        ["--parallel"],
        () => 1,
        "Parallel workers for safe phases (0 = auto-detect CPU cores)");

    public static readonly Option<string> Format = new(
        ["--format"],
        () => "human",
        "Output format: human, json, yaml");

    public static readonly Option<string?> CacheDir = new(
        ["--cache-dir"],
        () => null,
        "Enable caching; path to cache directory (reserved — not yet implemented)");

    public static readonly Option<bool> NoCache = new(
        ["--no-cache"],
        () => false,
        "Disable cache reads and writes for this run");

    public static readonly Option<string?> PreHook = new(
        ["--pre-hook"],
        () => null,
        "Shell command to run before execution (lifecycle hook)");

    public static readonly Option<string?> PostHook = new(
        ["--post-hook"],
        () => null,
        "Shell command to run after execution (lifecycle hook)");

    /// <summary>Register all global options on the root command.</summary>
    public static void AddToCommand(Command command)
    {
        command.AddGlobalOption(Config);
        command.AddGlobalOption(Root);
        command.AddGlobalOption(Verbose);
        command.AddGlobalOption(Quiet);
        command.AddGlobalOption(WarnOnly);
        command.AddGlobalOption(DryRun);
        command.AddGlobalOption(DryRunWithHooks);
        command.AddGlobalOption(Force);
        command.AddGlobalOption(Parallel);
        command.AddGlobalOption(Format);
        command.AddGlobalOption(CacheDir);
        command.AddGlobalOption(NoCache);
        command.AddGlobalOption(PreHook);
        command.AddGlobalOption(PostHook);
    }

    /// <summary>Bind a parsed invocation context into a <see cref="GlobalOptions"/> record.</summary>
    public static GlobalOptions Bind(System.CommandLine.Parsing.ParseResult pr) => new()
    {
        Config          = pr.GetValueForOption(Config)!,
        Root            = pr.GetValueForOption(Root)!,
        Verbose         = pr.GetValueForOption(Verbose),
        Quiet           = pr.GetValueForOption(Quiet),
        WarnOnly        = pr.GetValueForOption(WarnOnly),
        DryRun          = pr.GetValueForOption(DryRun),
        DryRunWithHooks = pr.GetValueForOption(DryRunWithHooks),
        Force           = pr.GetValueForOption(Force),
        Parallel        = pr.GetValueForOption(Parallel),
        Format          = pr.GetValueForOption(Format)!,
        CacheDir        = pr.GetValueForOption(CacheDir),
        NoCache         = pr.GetValueForOption(NoCache),
        PreHook         = pr.GetValueForOption(PreHook),
        PostHook        = pr.GetValueForOption(PostHook),
    };
}
