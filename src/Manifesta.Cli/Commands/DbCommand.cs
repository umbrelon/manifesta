using System.CommandLine;
using System.CommandLine.Invocation;
using Manifesta.Core;
using Manifesta.Core.Drift;
using Manifesta.Core.Filtering;
using Manifesta.Core.IR;
using Manifesta.Core.Pipeline;

namespace Manifesta.Cli.Commands;

// ─── shared helpers ────────────────────────────────────────────────────────────

file static class DbProviderHelper
{
    internal static readonly Option<string> ProviderOption =
        new(["--provider"], () => "mysql", "Database provider: mysql, postgres");

    internal static DbProvider Parse(string? value) =>
        (value ?? "mysql").ToLowerInvariant() switch
        {
            "mysql"                    => DbProvider.MySql,
            "postgres" or "postgresql" => DbProvider.Postgres,
            "sqlserver"                => throw new ManifestaConfigException(
                "SQL Server is not supported in the community edition. " +
                "See https://github.com/umbrelon/manifesta-enterprise for the full edition."),
            var s => throw new ManifestaConfigException(
                $"Unknown provider '{s}'. Valid values: mysql, postgres.")
        };

    internal static void WarnIfSchemaIgnored(string? schemaFilter, DbProvider provider, GlobalOptions globals)
    {
        if (!string.IsNullOrWhiteSpace(schemaFilter) && provider == DbProvider.MySql)
            OutputFormatter.WriteVerbose("--schema is not supported for MySQL and will be ignored.", globals);
    }

    internal static async Task<(IReadOnlyList<TableDefinition> Tables, string SourceDescription)>
        LoadLiveTablesAsync(
            string?           connection,
            string?           inputDir,
            string?           schemaFilter,
            DbProvider        provider,
            GlobalOptions     globals,
            CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(connection))
        {
            WarnIfSchemaIgnored(schemaFilter, provider, globals);
            OutputFormatter.WriteVerbose("Introspecting database…", globals);
            var introspector = DatabaseIntrospectorRegistry.GetFactory().Create(provider, connection);
            IReadOnlyList<TableDefinition> tables;
            try
            {
                tables = await introspector.IntrospectAsync(
                    provider == DbProvider.MySql ? null : schemaFilter, ct);
            }
            catch (Exception ex)
            {
                throw new ManifestaSchemException($"Failed to introspect database: {ex.Message}");
            }
            return (tables, $"--connection ({connection})");
        }
        else
        {
            OutputFormatter.WriteVerbose($"Loading exported JSON files from {inputDir}…", globals);
            var loader = new TableLoader();
            var tables = (IReadOnlyList<TableDefinition>)await loader.LoadAsync(inputDir!, ct);

            if (!string.IsNullOrWhiteSpace(schemaFilter))
            {
                var filter = new SchemaFilter(schemaFilter);
                tables = tables.Where(t => filter.Matches(t.Name)).ToList().AsReadOnly();
                OutputFormatter.WriteVerbose($"Schema filter applied: {tables.Count} table(s) matched.", globals);
            }
            return (tables, $"--input-dir ({inputDir})");
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// manifesta db
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>manifesta db — database schema operations.</summary>
public sealed class DbCommand : Command
{
    public DbCommand() : base("db", "Database schema operations")
    {
        AddCommand(new DbDriftCommand());
    }
}

// ─── db drift ─────────────────────────────────────────────────────────────

public sealed class DbDriftCommand : ManifestCommandBase
{
    private readonly Option<string?> _connection    = new(["--connection"],     () => null,  "Database connection string (mutually exclusive with --input-dir)");
    private readonly Option<string?> _inputDir      = new(["--input-dir"],      () => null,  "Directory of pre-exported JSON files (mutually exclusive with --connection)");
    private readonly Option<string?> _schema        = new(["--schema"],         () => null,  "Comma-separated schemas to scope the comparison (default: all; ignored for MySQL)");
    private readonly Option<bool>    _strict        = new(["--strict"],         () => false, "Exit 1 on warnings (extra columns/tables in DB not in repo)");
    private readonly Option<bool>    _includeSchema = new(["--include-schema"], () => false, "Embed full before/after field listings for drifted tables in the report");
    private readonly Option<string?> _output        = new(["--output"],         () => null,  "Full output file path (overrides --output-dir)");
    private readonly Option<string?> _outputDir     = new(["--output-dir"],     () => null,  "Output directory");
    private readonly Option<string>  _provider      = DbProviderHelper.ProviderOption;

    public DbDriftCommand() : base("drift", "Compare repository definitions against a live database (read-only)")
    {
        AddOption(_connection);
        AddOption(_inputDir);
        AddOption(_schema);
        AddOption(_strict);
        AddOption(_includeSchema);
        AddOption(_output);
        AddOption(_outputDir);
        AddOption(_provider);

        this.SetHandler(context => InvokeBaseAsync(context));
    }

    protected override async Task<int> ExecuteAsync(GlobalOptions globals, InvocationContext context, CancellationToken ct)
    {
        var pr            = context.ParseResult;
        var connection    = pr.GetValueForOption(_connection);
        var inputDir      = pr.GetValueForOption(_inputDir);
        var schemaFilter  = pr.GetValueForOption(_schema);
        var strict        = pr.GetValueForOption(_strict);
        var includeSchema = pr.GetValueForOption(_includeSchema);
        var outputArg     = pr.GetValueForOption(_output);
        var outputDir     = pr.GetValueForOption(_outputDir);
        var provider      = DbProviderHelper.Parse(pr.GetValueForOption(_provider));

        // ── Flag validation ────────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(connection) && string.IsNullOrWhiteSpace(inputDir))
            throw new ManifestaConfigException("Either --connection or --input-dir must be provided.");

        if (!string.IsNullOrWhiteSpace(connection) && !string.IsNullOrWhiteSpace(inputDir))
            throw new ManifestaConfigException("--connection and --input-dir are mutually exclusive. Provide exactly one.");

        // ── Resolve root ───────────────────────────────────────────────────────
        var config   = ConfigLoader.Load(globals);
        var rootPath = ResolveRootPath(globals, config);
        OutputFormatter.WriteVerbose($"Root: {rootPath}", globals);

        // ── Load live definitions ──────────────────────────────────────────────
        var (liveTables, sourceDescription) =
            await DbProviderHelper.LoadLiveTablesAsync(connection, inputDir, schemaFilter, provider, globals, ct);

        OutputFormatter.WriteVerbose($"Live tables loaded: {liveTables.Count}", globals);

        if (liveTables.Count == 0)
        {
            OutputFormatter.WriteLine("No live tables found. Nothing to compare.", globals);
            return (int)ExitCode.Success;
        }

        // ── Load repo definitions ──────────────────────────────────────────────
        var repoLoader    = new TableLoader();
        var allRepoTables = await repoLoader.LoadAsync(rootPath, config.Paths.Skip, ct);

        var inputDirNorm = string.IsNullOrWhiteSpace(inputDir) ? null
            : Path.GetFullPath(inputDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var repoTables = inputDirNorm is null
            ? allRepoTables
            : allRepoTables
                .Where(t => !Path.GetFullPath(t.SourceFile).StartsWith(inputDirNorm, StringComparison.OrdinalIgnoreCase))
                .ToList()
                .AsReadOnly();

        var repoByName = new Dictionary<string, TableDefinition>(TableNames.Comparer);
        foreach (var t in repoTables)
        {
            if (repoByName.TryGetValue(t.Name, out var existing))
                throw new ManifestaConfigException(
                    $"Duplicate table name '{t.Name}' found in repo:\n  {existing.SourceFile}\n  {t.SourceFile}");
            repoByName[t.Name] = t;
        }

        OutputFormatter.WriteVerbose($"Repo tables loaded: {repoTables.Count}", globals);

        // ── Diff ───────────────────────────────────────────────────────────────
        var differ        = new TableDiffer();
        var drifted       = new List<DriftResult>();
        var clean         = new List<DriftResult>();
        var extraDbTables = new List<string>();
        var liveNames     = TableNames.NewSet();

        foreach (var liveTable in liveTables)
        {
            liveNames.Add(liveTable.Name);

            if (repoByName.TryGetValue(liveTable.Name, out var repoTable))
            {
                var result = differ.Diff(repoTable, liveTable, repoTable.SourceFile);
                if (result.HasDrift || result.HasWarnings)
                    drifted.Add(result);
                else
                    clean.Add(result);
            }
            else
            {
                extraDbTables.Add(liveTable.Name);
            }
        }

        var missingDbTables = repoTables
            .Where(t => !liveNames.Contains(t.Name))
            .Select(t => t.SourceFile)
            .ToList();

        // ── Assemble session ───────────────────────────────────────────────────
        var session = new DriftSession
        {
            Source          = sourceDescription,
            RootPath        = rootPath,
            Timestamp       = DateTimeOffset.UtcNow,
            TotalLiveTables = liveTables.Count,
            IncludeSchema   = includeSchema,
            DriftedTables   = drifted.AsReadOnly(),
            CleanTables     = clean.AsReadOnly(),
            ExtraDbTables   = extraDbTables.AsReadOnly(),
            MissingDbTables = missingDbTables.AsReadOnly(),
        };

        // ── Write report ───────────────────────────────────────────────────────
        var reportPath    = OutputPathResolver.Resolve(outputArg, outputDir, "drift-report.md");
        var reportContent = new DriftReportGenerator().Generate(session);
        var writer        = new AtomicWriter();
        await writer.WriteAsync(reportPath, reportContent, ct);
        OutputFormatter.WriteVerbose($"Report written to: {reportPath}", globals);

        // ── Summary output ─────────────────────────────────────────────────────
        if (!session.HasDrift && !session.HasWarnings)
        {
            OutputFormatter.WriteLine($"No drift detected — {clean.Count} table(s) in sync.", globals);
            return (int)ExitCode.Success;
        }

        if (session.HasDrift)
        {
            OutputFormatter.WriteLine(
                $"Drift detected — {drifted.Count} table(s) drifted, " +
                $"{missingDbTables.Count} absent from DB. See {reportPath}.",
                globals);
            return (int)ExitCode.ValidationErrors;
        }

        // Warnings only (no drift).
        OutputFormatter.WriteLine(
            $"Warnings: {extraDbTables.Count} table(s) in DB have no repo definition. See {reportPath}.",
            globals);
        return strict ? (int)ExitCode.ValidationErrors : (int)ExitCode.Success;
    }
}
