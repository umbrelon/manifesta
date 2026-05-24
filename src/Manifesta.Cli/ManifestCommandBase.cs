using System.CommandLine;
using System.CommandLine.Invocation;
using Manifesta.Core;
using Manifesta.Core.IR;
using Manifesta.Core.Pipeline;
using Manifesta.Doc;

namespace Manifesta.Cli.Commands;

/// <summary>
/// Base for all Manifesta command handlers.
/// Provides access to the resolved <see cref="GlobalOptions"/> and
/// handles the pre/post hook lifecycle automatically.
/// </summary>
public abstract class ManifestCommandBase : Command
{
    protected ManifestCommandBase(string name, string description)
        : base(name, description) { }

    /// <summary>
    /// Bind global options from the parse result and execute the command.
    /// Subclasses implement <see cref="ExecuteAsync"/>.
    /// </summary>
    protected async Task<int> InvokeBaseAsync(InvocationContext context)
    {
        var globals = GlobalOptionDefinitions.Bind(context.ParseResult);
        var ct      = context.GetCancellationToken();

        // Pre-hook
        if (!string.IsNullOrWhiteSpace(globals.PreHook))
        {
            var ok = await HookRunner.RunAsync(globals.PreHook, globals, ct: ct);
            if (!ok) return (int)ExitCode.ValidationErrors;
        }

        int exitCode;
        try
        {
            exitCode = await ExecuteAsync(globals, context, ct);
        }
        catch (ManifestaSchemException ex)
        {
            OutputFormatter.WriteError(ex.Message);
            return (int)ExitCode.FatalSchemaErrors;
        }
        catch (ManifestaConfigException ex)
        {
            OutputFormatter.WriteError(ex.Message);
            return (int)ExitCode.ConfigOrInvocationError;
        }
        catch (ManifestaReleaseException ex)
        {
            OutputFormatter.WriteError(ex.Message);
            return (int)ExitCode.ReleaseRepoFailure;
        }
        catch (Exception ex)
        {
            OutputFormatter.WriteError($"Unexpected error: {ex.Message}");
            if (globals.Verbose)
                Console.Error.WriteLine(ex.StackTrace);
            return (int)ExitCode.InternalError;
        }

        // Post-hook (only on success)
        if (exitCode == 0 && !string.IsNullOrWhiteSpace(globals.PostHook))
        {
            var ok = await HookRunner.RunAsync(globals.PostHook, globals, ct: ct);
            if (!ok) return (int)ExitCode.ValidationErrors;
        }

        return exitCode;
    }

    /// <summary>
    /// Command-specific logic. Return an <see cref="ExitCode"/> cast to int.
    /// </summary>
    protected abstract Task<int> ExecuteAsync(
        GlobalOptions      globals,
        InvocationContext  context,
        CancellationToken  ct);

    // ── Path-resolution helpers ───────────────────────────────────────────

    /// <summary>
    /// Resolves the scanning root directory.
    /// Returns <c>--root</c> if the flag was set explicitly, otherwise resolves
    /// <c>config.Paths.Root</c> relative to the config file's own directory.
    /// </summary>
    protected static string ResolveRootPath(GlobalOptions globals, ManifestaConfig config)
    {
        if (globals.Root != GlobalDefaults.Root)
            return Path.GetFullPath(globals.Root);

        var configDir = Path.GetDirectoryName(Path.GetFullPath(globals.Config)) ?? ".";
        return Path.GetFullPath(config.Paths.Root, configDir);
    }

    /// <summary>
    /// Returns the directory that contains the active config file.
    /// </summary>
    protected static string ConfigFileDirectory(GlobalOptions globals)
        => Path.GetDirectoryName(Path.GetFullPath(globals.Config)) ?? ".";

    // ── Shared loading ────────────────────────────────────────────────────

    /// <summary>
    /// Scans <c>config.Paths.Root</c> for <c>tables/</c> directories, loads all
    /// <see cref="TableDefinition"/> files (with duplicate-name detection), then
    /// loads all <see cref="SectionDefinition"/> files from
    /// <c>config.Paths.DocumentSections</c>.
    /// </summary>
    protected async Task<(List<TableDefinition> Tables, List<SectionDefinition> Sections)>
        LoadTablesAndSectionsAsync(
            GlobalOptions     globals,
            ManifestaConfig   config,
            CancellationToken ct)
    {
        var rootPath  = ResolveRootPath(globals, config);
        var configDir = ConfigFileDirectory(globals);

        if (!Directory.Exists(rootPath))
            throw new ManifestaConfigException($"Root directory not found: {rootPath}");

        OutputFormatter.WriteVerbose($"Scanning from: {rootPath}", globals);

        // ── Load tables ───────────────────────────────────────────────────

        var skipSet = config.Paths.Skip.Count > 0
            ? new HashSet<string>(config.Paths.Skip, TableNames.Comparer)
            : null;

        var tablesDirs = Directory
            .GetDirectories(rootPath, "tables", SearchOption.AllDirectories)
            .Where(d => skipSet is null || !PathHasSkippedComponent(d, rootPath, skipSet))
            .OrderBy(d => d);

        var tableLoader = new TableLoader();
        var allTables   = new List<TableDefinition>();

        foreach (var dir in tablesDirs)
        {
            OutputFormatter.WriteVerbose($"Loading tables from: {dir}", globals);
            var loaded = await tableLoader.LoadAsync(dir, ct);
            allTables.AddRange(loaded);
            OutputFormatter.WriteVerbose($"  {loaded.Count} table(s) loaded", globals);
        }

        if (allTables.Count == 0)
            throw new ManifestaSchemException(
                "No table definitions found in any 'tables' directory.");

        var duplicates = allTables
            .GroupBy(t => t.Name, TableNames.Comparer)
            .Where(g => g.Count() > 1)
            .ToList();

        if (duplicates.Count > 0)
        {
            OutputFormatter.WriteError(
                $"Duplicate table definitions found ({duplicates.Count} conflict(s)). " +
                "Each table name must be unique across all 'tables' directories:");
            foreach (var group in duplicates)
            {
                OutputFormatter.WriteError($"  Duplicate key: {group.Key}");
                foreach (var t in group)
                    OutputFormatter.WriteError($"    - {Path.GetRelativePath(rootPath, t.SourceFile)}");
            }
            throw new ManifestaSchemException(
                $"{duplicates.Count} duplicate table name(s) detected — see errors above.");
        }

        // ── Load sections ─────────────────────────────────────────────────

        var loadedSections = new List<SectionDefinition>();

        if (!string.IsNullOrEmpty(config.Paths.DocumentSections))
        {
            var docSectionsPath = Path.GetFullPath(config.Paths.DocumentSections, configDir);
            try
            {
                loadedSections = (await new SectionLoader().LoadAsync(docSectionsPath, ct)).ToList();
                OutputFormatter.WriteVerbose(
                    $"Loaded {loadedSections.Count} section(s) from: {docSectionsPath}", globals);
            }
            catch (ManifestaSchemException ex)
            {
                throw new ManifestaSchemException(
                    $"Error loading sections from {docSectionsPath}: {ex.Message}", ex);
            }
        }

        return (allTables, loadedSections);
    }

    /// <summary>
    /// Returns <c>true</c> when any path component of <paramref name="path"/>
    /// (relative to <paramref name="rootPath"/>) appears in <paramref name="skip"/>.
    /// </summary>
    protected static bool PathHasSkippedComponent(
        string          path,
        string          rootPath,
        HashSet<string> skip)
    {
        var rel   = Path.GetRelativePath(rootPath, path);
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => skip.Contains(p));
    }

    /// <summary>
    /// Returns the schema prefix of a dotted table name (e.g. "dbo" from "dbo.Customer").
    /// Returns the full name unchanged when no dot is present.
    /// </summary>
    protected static string SchemaPrefix(string tableName)
    {
        var dot = tableName.IndexOf('.');
        return dot >= 0 ? tableName[..dot] : tableName;
    }
}
