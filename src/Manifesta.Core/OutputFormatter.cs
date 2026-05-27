using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Manifesta.Core;

/// <summary>
/// Formats structured objects for output, driven by format specification.
/// </summary>
public static class OutputFormatter
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly ISerializer _yamlSerializer =
        new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

    /// <summary>
    /// Serialise <paramref name="value"/> according to the format specification.
    /// </summary>
    public static string Format(object? value, string format) => format.ToLowerInvariant() switch
    {
        "json" => JsonSerializer.Serialize(value, _jsonOptions),
        "yaml" => _yamlSerializer.Serialize(value),
        _ => value?.ToString() ?? "",   // human: let callers build their own strings
    };

    /// <summary>
    /// Write a line to stdout unless quiet mode is enabled.
    /// </summary>
    public static void WriteLine(string message, bool quiet = false, TextWriter? writer = null)
    {
        if (quiet) return;
        (writer ?? Console.Out).WriteLine(message);
    }

    /// <summary>Write a line to stdout, driven by <paramref name="options"/>.</summary>
    public static void WriteLine(string message, GlobalOptions options, TextWriter? writer = null)
        => WriteLine(message, options.Quiet, writer);

    /// <summary>
    /// Write a line to stdout, respecting both quiet and verbose modes.
    /// Only written when verbose mode is active.
    /// </summary>
    public static void WriteVerbose(string message, bool verbose = false, TextWriter? writer = null)
    {
        if (!verbose) return;
        (writer ?? Console.Out).WriteLine($"[verbose] {message}");
    }

    /// <summary>Write a verbose line to stdout, driven by <paramref name="options"/>.</summary>
    public static void WriteVerbose(string message, GlobalOptions options, TextWriter? writer = null)
        => WriteVerbose(message, options.Verbose, writer);

    /// <summary>
    /// Writes a warning to stderr.  Always shown — not suppressed by quiet mode.
    /// Use for recoverable issues where processing continues (e.g. a file was skipped).
    /// </summary>
    public static void WriteWarning(string message, TextWriter? writer = null)
        => (writer ?? Console.Error).WriteLine($"Warning: {message}");

    /// <summary>Always writes to stderr (errors are never suppressed by quiet mode).</summary>
    public static void WriteError(string message, TextWriter? writer = null)
        => (writer ?? Console.Error).WriteLine($"Error: {message}");
}
