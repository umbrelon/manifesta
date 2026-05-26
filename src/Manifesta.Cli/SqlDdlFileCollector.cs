using Manifesta.Core;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Manifesta.Cli;

/// <summary>
/// Shared helper that resolves a <c>--input</c> / <c>--ddl-file</c> argument to an
/// ordered list of full file paths using glob-aware matching.
///
/// <list type="bullet">
///   <item>Single file — returned as-is; <paramref name="pattern"/> and
///     <paramref name="recursive"/> are ignored.</item>
///   <item>Directory + plain filename pattern (no <c>/</c>, <c>\</c>, or <c>**</c>) —
///     <paramref name="recursive"/> prepends <c>**/</c> to search all depths;
///     without it only the root is matched.</item>
///   <item>Directory + path glob (contains <c>/</c>, <c>\</c>, or <c>**</c>) —
///     matched directly via <see cref="Matcher"/>;
///     <paramref name="recursive"/> is ignored with a verbose hint.</item>
/// </list>
/// </summary>
internal static class SqlDdlFileCollector
{
    /// <summary>
    /// Collects SQL files from <paramref name="inputPath"/>.
    /// Returns a non-null sorted list on success.
    /// Returns <c>null</c> after writing an error message on failure —
    /// the caller should propagate the appropriate exit code.
    /// </summary>
    internal static List<string>? Collect(
        string        inputPath,
        string        pattern,
        bool          recursive,
        GlobalOptions globals)
    {
        // ── Single file ───────────────────────────────────────────────────────
        if (File.Exists(inputPath))
            return [inputPath];

        // ── Directory ─────────────────────────────────────────────────────────
        if (!Directory.Exists(inputPath))
        {
            OutputFormatter.WriteError($"Input not found: {inputPath}");
            return null;
        }

        // A pattern is a "path glob" when it encodes directory structure.
        bool isPathGlob = pattern.Contains('/') ||
                          pattern.Contains('\\') ||
                          pattern.Contains("**");

        if (isPathGlob && recursive)
            OutputFormatter.WriteVerbose(
                "--recursive is ignored because --pattern already contains a path glob.",
                globals);

        // Effective glob sent to Matcher:
        //   path glob  → normalise backslashes, use as-is
        //   filename   → --recursive prepends **/ ; without it matches root only
        var effectivePattern = isPathGlob
            ? pattern.Replace('\\', '/')
            : (recursive ? $"**/{pattern}" : pattern);

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(effectivePattern);

        var files = matcher
            .GetResultsInFullPath(inputPath)
            .OrderBy(f => f)
            .ToList();

        if (files.Count == 0)
        {
            var hint = !recursive && !isPathGlob
                ? " (use --recursive to also search subdirectories)"
                : string.Empty;
            OutputFormatter.WriteError(
                $"No files matching '{effectivePattern}' found in: {inputPath}{hint}");
            return null;
        }

        OutputFormatter.WriteVerbose(
            $"Found {files.Count} file(s) matching '{effectivePattern}'", globals);

        return files;
    }
}
