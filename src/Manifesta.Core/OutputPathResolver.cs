namespace Manifesta.Core;

/// <summary>
/// Resolves the concrete output path for a generated file, applying the
/// <c>--output</c> / <c>--output-dir</c> precedence rule from the spec:
///
/// <list type="bullet">
///   <item><c>--output</c> always wins for the specific file it names.</item>
///   <item><c>--output-dir</c> sets the base directory when <c>--output</c> is absent.</item>
///   <item>Neither → command default location.</item>
/// </list>
/// </summary>
public static class OutputPathResolver
{
    /// <summary>
    /// Resolve the output path for a single-file command.
    /// </summary>
    /// <param name="outputFlag">
    ///   Value of <c>--output</c> (full path). <c>null</c> or empty = not set.
    /// </param>
    /// <param name="outputDirFlag">
    ///   Value of <c>--output-dir</c>. <c>null</c> or empty = not set.
    /// </param>
    /// <param name="defaultFileName">
    ///   The file name to use when neither flag specifies the full path
    ///   (e.g. <c>"validation.json"</c>).
    /// </param>
    /// <param name="defaultDirectory">
    ///   The directory to use when neither flag is set (command default location).
    ///   Defaults to the current working directory.
    /// </param>
    /// <returns>The fully resolved output path.</returns>
    public static string Resolve(
        string? outputFlag,
        string? outputDirFlag,
        string  defaultFileName,
        string  defaultDirectory = ".")
    {
        // Rule 1: --output wins if set.
        if (!string.IsNullOrWhiteSpace(outputFlag))
            return Path.GetFullPath(outputFlag);

        // Rule 2: --output-dir sets the directory.
        if (!string.IsNullOrWhiteSpace(outputDirFlag))
            return Path.GetFullPath(Path.Combine(outputDirFlag, defaultFileName));

        // Rule 3: command default.
        return Path.GetFullPath(Path.Combine(defaultDirectory, defaultFileName));
    }

    /// <summary>
    /// Resolve the output directory for a multi-file command
    /// (commands that accept <c>--output-dir</c> only).
    /// </summary>
    /// <param name="outputDirFlag">Value of <c>--output-dir</c>.</param>
    /// <param name="defaultDirectory">Command default output directory.</param>
    public static string ResolveDirectory(
        string? outputDirFlag,
        string  defaultDirectory)
    {
        if (!string.IsNullOrWhiteSpace(outputDirFlag))
            return Path.GetFullPath(outputDirFlag);

        return Path.GetFullPath(defaultDirectory);
    }
}
