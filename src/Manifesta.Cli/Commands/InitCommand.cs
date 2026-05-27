using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Manifesta.Core;
using Manifesta.Core.Filtering;
using Manifesta.Core.IR;
using Manifesta.Core.Pipeline;

namespace Manifesta.Cli.Commands;

// ── Shared provider helper (live-DB commands only) ─────────────────────────────
// SQL Server is excluded here because live introspection requires the enterprise
// edition.  File-based commands (init sql, init dbml, init prisma) accept all
// providers; see SharedInitSubCommands.cs.

file static class InitProviderHelper
{
    internal static readonly Option<string> ProviderOption =
        new(["--provider"], () => "mysql", "Database provider: mysql, postgres, sqlite");

    internal static DbProvider Parse(string? value) =>
        (value ?? "mysql").ToLowerInvariant() switch
        {
            "mysql"                    => DbProvider.MySql,
            "postgres" or "postgresql" => DbProvider.Postgres,
            "sqlite"                   => DbProvider.Sqlite,
            "sqlserver"                => throw new ManifestaConfigException(
                "SQL Server is not supported in the community edition. " +
                "See https://github.com/umbrelon/manifesta-enterprise for the full edition."),
            var s => throw new ManifestaConfigException(
                $"Unknown provider '{s}'. Valid values: mysql, postgres, sqlite.")
        };

    internal static void WarnIfSchemaIgnored(string? schema, DbProvider provider, GlobalOptions globals)
    {
        if (!string.IsNullOrWhiteSpace(schema) && provider is DbProvider.MySql or DbProvider.Sqlite)
            OutputFormatter.WriteVerbose($"--schema is not supported for {provider} and will be ignored.", globals);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// manifesta init
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>manifesta init — one-shot bootstrap from a database or external schema format.</summary>
public sealed class InitCommand : Command
{
    public InitCommand() : base("init", "Bootstrap Manifesta from a live database, DBML, Prisma schema, or SQL DDL file")
    {
        AddCommand(new InitDbCommand());
        AddCommand(new InitDbmlCommand());
        AddCommand(new InitPrismaCommand());
        AddCommand(new InitSqlCommand());
    }
}

// ─── init db ──────────────────────────────────────────────────────────────────

/// <summary>manifesta init db — introspect a live database and generate the initial layout.</summary>
public sealed class InitDbCommand : ManifestCommandBase
{
    private readonly Option<string?> _connection             = new(["--connection"],                () => null,  "Database connection string (mutually exclusive with --input-dir)");
    private readonly Option<string?> _inputDir               = new(["--input-dir"],                 () => null,  "Directory of pre-exported JSON table files (mutually exclusive with --connection)");
    private readonly Option<string>  _outputDir              = new(["--output-dir"],                () => "./",  "Output directory for _/, tables/, views/ (default: current directory)");
    private readonly Option<string?> _schema                 = new(["--schema"],                    () => null,  "Comma-separated list of schemas to init (default: all; ignored for MySQL and SQLite)");
    private readonly Option<bool>    _noCaptureReferenceData = new(["--no-capture-reference-data"], () => false, "Skip capturing reference table row data during init");
    private readonly Option<string>  _provider               = InitProviderHelper.ProviderOption;

    public InitDbCommand() : base("db", "Initialize from a live database connection or pre-exported JSON files")
    {
        AddOption(_connection);
        AddOption(_inputDir);
        AddOption(_outputDir);
        AddOption(_schema);
        AddOption(_noCaptureReferenceData);
        AddOption(_provider);

        this.SetHandler(context => InvokeBaseAsync(context));
    }

    protected override async Task<int> ExecuteAsync(GlobalOptions globals, InvocationContext context, CancellationToken ct)
    {
        var pr               = context.ParseResult;
        var connectionString = pr.GetValueForOption(_connection);
        var inputDir         = pr.GetValueForOption(_inputDir);
        var outputDirPath    = pr.GetValueForOption(_outputDir)!;
        var schemaFilter     = pr.GetValueForOption(_schema);
        var noCapture        = pr.GetValueForOption(_noCaptureReferenceData);
        var provider         = InitProviderHelper.Parse(pr.GetValueForOption(_provider));

        // ── Flag validation ───────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(connectionString) && string.IsNullOrWhiteSpace(inputDir))
            throw new ManifestaConfigException("Either --connection or --input-dir must be provided.");

        if (!string.IsNullOrWhiteSpace(connectionString) && !string.IsNullOrWhiteSpace(inputDir))
            throw new ManifestaConfigException("--connection and --input-dir are mutually exclusive. Provide exactly one.");

        // ── Resolve output directories ────────────────────────────────────
        var outputDir = Path.GetFullPath(outputDirPath);
        var configDir = Path.Combine(outputDir, "_");

        try { Directory.CreateDirectory(configDir); }
        catch (Exception ex) { throw new ManifestaConfigException($"Failed to create output directories: {ex.Message}"); }

        // ── Idempotency guard ─────────────────────────────────────────────
        var configPath = Path.Combine(configDir, "manifesta.config.json");
        if (File.Exists(configPath) && !globals.Force)
            throw new ManifestaConfigException(
                $"Config file already exists at {configPath}. Use --force to overwrite.");

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented        = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        IWriter writer = globals.DryRun ? new DryRunWriter() : new AtomicWriter();

        IReadOnlyList<TableDefinition> tablesList;
        IReadOnlyList<TableDefinition> viewsList;

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            // ── DB connection mode ────────────────────────────────────────
            var tablesDir = Path.Combine(outputDir, "tables");
            var viewsDir  = Path.Combine(outputDir, "views");

            try
            {
                Directory.CreateDirectory(tablesDir);
                Directory.CreateDirectory(viewsDir);
            }
            catch (Exception ex) { throw new ManifestaConfigException($"Failed to create output directories: {ex.Message}"); }

            InitProviderHelper.WarnIfSchemaIgnored(schemaFilter, provider, globals);
            OutputFormatter.WriteVerbose($"init db → {outputDir}", globals);
            if (schemaFilter != null && provider is not DbProvider.MySql and not DbProvider.Sqlite)
                OutputFormatter.WriteVerbose($"Schema filter: {schemaFilter}", globals);

            var effectiveSchema = provider is DbProvider.MySql or DbProvider.Sqlite ? null : schemaFilter;
            var introspector    = DatabaseIntrospectorRegistry.GetFactory().Create(provider, connectionString);
            try
            {
                tablesList = await introspector.IntrospectTablesOnlyAsync(effectiveSchema, ct);
                viewsList  = await introspector.IntrospectViewsOnlyAsync(effectiveSchema, ct);
            }
            catch (Exception ex)
            {
                throw new ManifestaSchemException($"Failed to introspect database: {ex.Message}");
            }

            if (tablesList.Count == 0)
                throw new ManifestaSchemException("No tables found in the database. Initialization requires at least one table.");

            OutputFormatter.WriteVerbose($"Found {tablesList.Count} table(s) and {viewsList.Count} view(s)", globals);

            // Reference data capture
            if (!noCapture)
            {
                ReferenceTableConfig? refConfig = null;
                try { refConfig = ConfigLoader.Load(globals).ReferenceTableConfig; }
                catch { /* no config yet */ }

                if (refConfig is { Enabled: true, AutoCapture: true } &&
                    refConfig.CaptureOn.Contains("db-init", TableNames.Comparer))
                {
                    var capturer = DatabaseIntrospectorRegistry.GetFactory().CreateCapturer(provider, connectionString);
                    tablesList = await DatabaseIntrospectorRegistry.GetFactory().EnrichWithReferenceDataAsync(
                        tablesList, capturer, schemaFilter, refConfig, introspector, globals, ct);
                }
            }

            foreach (var table in tablesList)
            {
                var filePath = Path.Combine(tablesDir, $"{table.Name}.json");
                var json     = TableDefinitionSerializer.Serialize(table);
                try { await writer.WriteAsync(filePath, json, ct); }
                catch (Exception ex) { throw new ManifestaSchemException($"Failed to write {filePath}: {ex.Message}"); }
                OutputFormatter.WriteVerbose($"Exported table: {table.Name}.json", globals);
            }

            foreach (var view in viewsList)
            {
                var filePath = Path.Combine(viewsDir, $"{view.Name}.json");
                var json     = TableDefinitionSerializer.Serialize(view);
                try { await writer.WriteAsync(filePath, json, ct); }
                catch (Exception ex) { throw new ManifestaSchemException($"Failed to write {filePath}: {ex.Message}"); }
                OutputFormatter.WriteVerbose($"Exported view: {view.Name}.json", globals);
            }
        }
        else
        {
            // ── Input directory mode ──────────────────────────────────────
            var resolvedInputDir = Path.GetFullPath(inputDir!);
            OutputFormatter.WriteVerbose($"init db from directory → {resolvedInputDir}", globals);

            if (!Directory.Exists(resolvedInputDir))
                throw new ManifestaConfigException($"Input directory not found: {resolvedInputDir}");

            var loader = new TableLoader();
            IReadOnlyList<TableDefinition> allLoaded;
            try { allLoaded = await loader.LoadAsync(resolvedInputDir, ct); }
            catch (Exception ex) { throw new ManifestaSchemException($"Failed to load table definitions from {resolvedInputDir}: {ex.Message}"); }

            if (!string.IsNullOrWhiteSpace(schemaFilter))
            {
                var filter = new SchemaFilter(schemaFilter);
                allLoaded  = allLoaded.Where(t => filter.Matches(t.Name)).ToList().AsReadOnly();
                OutputFormatter.WriteVerbose($"Schema filter applied: {allLoaded.Count} table(s) matched.", globals);
            }

            if (allLoaded.Count == 0)
                throw new ManifestaSchemException(
                    "No table definitions found in the input directory. Initialization requires at least one table.");

            tablesList = allLoaded;
            viewsList  = Array.Empty<TableDefinition>();
            OutputFormatter.WriteVerbose($"Found {tablesList.Count} table definition(s)", globals);
        }

        // ── Generate _/manifesta.config.json ──────────────────────────────
        var config = new { paths = new { root = "../", skip = new[] { "_" } } };
        var configJson = JsonSerializer.Serialize(config, jsonOptions);
        try { await writer.WriteAsync(configPath, configJson, ct); }
        catch (Exception ex) { throw new ManifestaSchemException($"Failed to write {configPath}: {ex.Message}"); }
        OutputFormatter.WriteVerbose("Generated config: manifesta.config.json", globals);

        // ── Generate _/document-sections/all-tables.json ──────────────────
        var docSectionsDir = Path.Combine(configDir, "document-sections");
        try { Directory.CreateDirectory(docSectionsDir); }
        catch (Exception ex) { throw new ManifestaConfigException($"Failed to create document-sections directory: {ex.Message}"); }

        var sectionDef = new
        {
            name        = "All Tables",
            description = "Auto-generated section containing all database tables",
            tables      = tablesList.Select(t => t.Name).OrderBy(n => n).ToList()
        };

        var sectionJson = JsonSerializer.Serialize(sectionDef, jsonOptions);
        var sectionPath = Path.Combine(docSectionsDir, "all-tables.json");
        try { await writer.WriteAsync(sectionPath, sectionJson, ct); }
        catch (Exception ex) { throw new ManifestaSchemException($"Failed to write {sectionPath}: {ex.Message}"); }
        OutputFormatter.WriteVerbose("Generated section: document-sections/all-tables.json", globals);

        var viewSummary = viewsList.Count > 0 ? $", {viewsList.Count} view(s)" : "";
        OutputFormatter.WriteLine(
            $"Successfully initialized with {tablesList.Count} table(s){viewSummary} in {outputDir}",
            globals);

        return (int)ExitCode.Success;
    }
}
