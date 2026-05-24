namespace Manifesta.Core;

/// <summary>
/// Shared default values for global options.
/// Referenced by both <see cref="GlobalOptions"/> and the CLI's
/// <c>GlobalOptionDefinitions</c> so the two never drift independently.
/// </summary>
public static class GlobalDefaults
{
    public const string Config = "_/manifesta.config.json";
    public const string Root   = ".";
}

/// <summary>
/// All global flags as a strongly-typed record, resolved once per CLI invocation
/// and passed down to every command handler.
/// Lives in Core so orchestrators and utilities can accept it directly
/// without a CLI-layer adapter.
/// </summary>
public sealed record GlobalOptions
{
    public string   Config          { get; init; } = GlobalDefaults.Config;
    public string   Root            { get; init; } = GlobalDefaults.Root;
    public bool     Verbose         { get; init; }
    public bool     Quiet           { get; init; }
    public bool     WarnOnly        { get; init; }
    public bool     DryRun          { get; init; }
    public bool     DryRunWithHooks { get; init; }
    public bool     Force           { get; init; }
    public int      Parallel        { get; init; } = 1;
    public string   Format          { get; init; } = "human";
    public string?  CacheDir        { get; init; }
    public bool     NoCache         { get; init; }
    public string?  PreHook         { get; init; }
    public string?  PostHook        { get; init; }

    /// <summary>
    /// True when hooks should be suppressed.
    /// Hooks run unless --dry-run is set (without --dry-run-with-hooks).
    /// </summary>
    public bool SuppressHooks => DryRun && !DryRunWithHooks;
}
