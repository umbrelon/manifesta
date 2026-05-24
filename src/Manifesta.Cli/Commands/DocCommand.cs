using System.CommandLine;
using System.CommandLine.Invocation;
using Manifesta.Core;
using Manifesta.Core.IR;
using Manifesta.Core.Pipeline;
using Manifesta.Doc;

namespace Manifesta.Cli.Commands;

// ═══════════════════════════════════════════════════════════════════════════
// manifesta doc
// ═══════════════════════════════════════════════════════════════════════════

public sealed class DocCommand : Command
{
    public DocCommand() : base("doc", "Documentation generation")
    {
        AddCommand(new DocDbCommand());
    }
}

public sealed class DocDbCommand : ManifestCommandBase
{
    private readonly Option<string?> _outputDir      = new(["--output-dir"],      () => null,       "Output directory");
    private readonly Option<bool>    _noLogical      = new(["--no-logical"],      () => false,      "Exclude logical (business-logic) FKs from ERD diagrams");
    private readonly Option<bool>    _includeVirtual = new(["--include-virtual"], () => false,      "Include virtual (doc-only) FKs in ERD diagrams");
    private readonly Option<bool>    _noCrossSection = new(["--no-cross-section"],() => false,      "Exclude cross-section FK references from ERD diagrams");
    private readonly Option<string>  _format         = new(["--format"],          () => "markdown", "Output format: markdown, dbml");

    public DocDbCommand() : base("db", "Generate database documentation")
    {
        AddOption(_outputDir);
        AddOption(_noLogical);
        AddOption(_includeVirtual);
        AddOption(_noCrossSection);
        AddOption(_format);
        this.SetHandler(context => InvokeBaseAsync(context));
    }

    protected override async Task<int> ExecuteAsync(GlobalOptions globals, InvocationContext context, CancellationToken ct)
    {
        var config    = ConfigLoader.Load(globals);
        var rootPath  = ResolveRootPath(globals, config);
        var configDir = ConfigFileDirectory(globals);

        if (!Directory.Exists(rootPath))
        {
            OutputFormatter.WriteError($"Root directory not found: {rootPath}");
            return (int)ExitCode.ConfigOrInvocationError;
        }

        var outputDir = OutputPathResolver.ResolveDirectory(
            context.ParseResult.GetValueForOption(_outputDir),
            Path.Combine(rootPath, "docs"));

        // ── Load tables ───────────────────────────────────────────────────
        OutputFormatter.WriteVerbose($"Scanning from: {rootPath}", globals);

        var skipSet = config.Paths.Skip.Count > 0
            ? new HashSet<string>(config.Paths.Skip, TableNames.Comparer)
            : null;

        var tablesDirs = Directory
            .GetDirectories(rootPath, "tables", SearchOption.AllDirectories)
            .Where(d => skipSet is null || !PathHasSkippedComponent(d, rootPath, skipSet))
            .OrderBy(d => d);

        var tableLoader = new TableLoader();
        var tables      = new List<TableDefinition>();

        foreach (var dir in tablesDirs)
        {
            OutputFormatter.WriteVerbose($"Loading tables from: {dir}", globals);
            var loaded = await tableLoader.LoadAsync(dir, ct);
            tables.AddRange(loaded);
            OutputFormatter.WriteVerbose($"  {loaded.Count} table(s) loaded", globals);
        }

        if (tables.Count == 0)
        {
            OutputFormatter.WriteError("No table definitions found in any 'tables' directory");
            return (int)ExitCode.FatalSchemaErrors;
        }

        var duplicates = tables
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
            return (int)ExitCode.FatalSchemaErrors;
        }

        // ── Load sections ─────────────────────────────────────────────────
        var sections     = new List<SectionDefinition>();
        var sectionOrder = new List<string>();

        if (!string.IsNullOrEmpty(config.Paths.DocumentSections))
        {
            var docSectionsPath = Path.GetFullPath(config.Paths.DocumentSections, configDir);
            try
            {
                sections = (await new SectionLoader().LoadAsync(docSectionsPath, ct)).ToList();
                if (sections.Count > 0)
                    OutputFormatter.WriteVerbose($"Loaded {sections.Count} section(s) from: {docSectionsPath}", globals);
            }
            catch (ManifestaSchemException ex)
            {
                OutputFormatter.WriteError($"Error loading sections from {docSectionsPath}: {ex.Message}");
                return (int)ExitCode.FatalSchemaErrors;
            }
        }

        if (config.Output?.SectionOrder?.Count > 0)
            sectionOrder = config.Output.SectionOrder;

        if (sections.Count == 0)
        {
            sections.Add(new() { Name = "Database Tables", Description = "All database tables",
                Tables = tables.Select(t => t.Name).ToList() });
        }

        // ── Generate and write ────────────────────────────────────────────
        var noLogical      = context.ParseResult.GetValueForOption(_noLogical);
        var includeVirtual = context.ParseResult.GetValueForOption(_includeVirtual);
        var noCrossSection = context.ParseResult.GetValueForOption(_noCrossSection);
        var format         = context.ParseResult.GetValueForOption(_format) ?? "markdown";

        var ir = new ManifestRoot { Tables = tables, Sections = sections };
        IWriter fileWriter = globals.DryRun ? new DryRunWriter() : new AtomicWriter();

        if (format.Equals("dbml", StringComparison.OrdinalIgnoreCase))
        {
            var dbml       = new DatabaseDocGenerator().GenerateDbml(ir);
            var outputFile = Path.Combine(outputDir, "database.dbml");
            await fileWriter.WriteAsync(outputFile, dbml, ct);
            OutputFormatter.WriteLine($"Generated database.dbml with {tables.Count} tables at {outputFile}", globals);
        }
        else
        {
            var dialect  = (config.Output?.Format as MarkdownFormatConfig)?.Dialect ?? MarkdownDialect.CommonMark;
            var markdown = new DatabaseDocGenerator().GenerateWithOrder(
                ir, sectionOrder, config.Output?.Title, dialect,
                includeLogicalOverride:  noLogical      ? false : null,
                includeVirtualOverride:  includeVirtual ? true  : null,
                includeCrossSectionRefs: !noCrossSection);
            var outputFile = Path.Combine(outputDir, "database.md");
            await fileWriter.WriteAsync(outputFile, markdown, ct);
            OutputFormatter.WriteLine($"Generated database.md with {tables.Count} tables at {outputFile}", globals);
        }

        return (int)ExitCode.Success;
    }
}
