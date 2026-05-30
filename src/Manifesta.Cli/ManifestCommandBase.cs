using System.CommandLine;
using System.CommandLine.Invocation;
using Manifesta.Core;
using Manifesta.Core.IR;
using Manifesta.Core.Pipeline;
using Manifesta.Doc;

namespace Manifesta.Cli.Commands;

/// <summary>
/// Per-edition base class for OSS command handlers.
/// Extends <see cref="ManifestSharedCommandBase"/> with table/section loading helpers.
/// </summary>
public abstract class ManifestCommandBase : ManifestSharedCommandBase
{
    protected ManifestCommandBase(string name, string description)
        : base(name, description) { }

    // ── Path-resolution helpers ───────────────────────────────────────────

    protected static string ResolveRootPath(GlobalOptions globals, ManifestaConfig config)
    {
        if (globals.Root != GlobalDefaults.Root)
            return Path.GetFullPath(globals.Root);

        var configDir = Path.GetDirectoryName(Path.GetFullPath(globals.Config)) ?? ".";
        return Path.GetFullPath(config.Paths.Root, configDir);
    }

    protected static string ConfigFileDirectory(GlobalOptions globals)
        => Path.GetDirectoryName(Path.GetFullPath(globals.Config)) ?? ".";

    // ── Shared loading ────────────────────────────────────────────────────

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

    protected static bool PathHasSkippedComponent(
        string          path,
        string          rootPath,
        HashSet<string> skip)
    {
        var rel   = Path.GetRelativePath(rootPath, path);
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => skip.Contains(p));
    }

    protected static string SchemaPrefix(string tableName)
    {
        var dot = tableName.IndexOf('.');
        return dot >= 0 ? tableName[..dot] : tableName;
    }
}
