using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Manifesta.Core;
using Manifesta.Core.Filtering;
using Manifesta.Core.IR;
using Manifesta.Core.Pipeline;

namespace Manifesta.Cli.Commands;

// ── Shared provider helpers ────────────────────────────────────────────────

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

// ═══════════════════════════════════════════════════════════════════════════
// manifesta init
// ═══════════════════════════════════════════════════════════════════════════

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

// ─── init db ──────────────────────────────────────────────────────────────

/// <summary>manifesta init db — introspect a live database and generate the initial layout.</summary>
public sealed class InitDbCommand : ManifestCommandBase
{
    private readonly Option<string?> _connection             = new(["--connection"],                () => null,  "Database connection string (mutually exclusive with --input-dir)");
    private readonly Option<string?> _inputDir               = new(["--input-dir"],                 () => null,  "Directory of pre-exported JSON table files (mutually exclusive with --connection)");
    private readonly Option<string>  _outputDir              = new(["--output-dir"],                () => "./",  "Output directory for _/, tables/, views/ (default: current directory)");
    private readonly Option<string?> _schema                 = new(["--schema"],                    () => null,  "Comma-separated list of schemas to init (default: all; ignored for MySQL)");
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

// ─── init dbml ────────────────────────────────────────────────────────────

/// <summary>manifesta init dbml — parse a DBML file and write one table-definition JSON per table.</summary>
public sealed class InitDbmlCommand : ManifestCommandBase
{
    private readonly Option<string>  _input       = new(["--input"],        "Path to the .dbml file (required)");
    private readonly Option<string?> _outputDir   = new(["--output-dir"],   () => "./tables",            "Directory to write JSON table definition files");
    private readonly Option<string?> _sectionsDir = new(["--sections-dir"], () => "./document-sections", "Directory to write SectionDefinition JSON files from TableGroup blocks");
    private readonly Option<bool>    _noSections  = new(["--no-sections"],  () => false,                 "Suppress writing SectionDefinition files");
    private readonly Option<string?> _schema      = new(["--schema"],       () => null,                  "Prefix all imported table names with <schema> (e.g. --schema dbo → dbo.Customer)");
    private readonly Option<bool>    _overwrite   = new(["--overwrite"],    () => false,                 "Overwrite existing JSON files (without this flag, existing files are skipped)");

    public InitDbmlCommand() : base("dbml", "Parse a DBML file and write one table-definition JSON per table")
    {
        _input.IsRequired = true;
        AddOption(_input);
        AddOption(_outputDir);
        AddOption(_sectionsDir);
        AddOption(_noSections);
        AddOption(_schema);
        AddOption(_overwrite);

        this.SetHandler(context => InvokeBaseAsync(context));
    }

    protected override async Task<int> ExecuteAsync(GlobalOptions globals, InvocationContext context, CancellationToken ct)
    {
        var pr          = context.ParseResult;
        var inputPath   = pr.GetValueForOption(_input)!;
        var outputDir   = pr.GetValueForOption(_outputDir) ?? "./tables";
        var sectionsDir = pr.GetValueForOption(_sectionsDir) ?? "./document-sections";
        var noSections  = pr.GetValueForOption(_noSections);
        var schema      = pr.GetValueForOption(_schema);
        var overwrite   = pr.GetValueForOption(_overwrite);

        if (!File.Exists(inputPath))
        {
            OutputFormatter.WriteError($"Input file not found: {inputPath}");
            return (int)ExitCode.FatalSchemaErrors;
        }

        string dbml;
        try { dbml = await File.ReadAllTextAsync(inputPath, ct); }
        catch (Exception ex)
        {
            OutputFormatter.WriteError($"Could not read {inputPath}: {ex.Message}");
            return (int)ExitCode.FatalSchemaErrors;
        }

        var parseResult = new DbmlParser().Parse(dbml, schema);

        foreach (var err in parseResult.Errors)
            OutputFormatter.WriteError(err);

        if (parseResult.Tables.Count == 0 && parseResult.Errors.Count > 0)
        {
            OutputFormatter.WriteError("No tables could be parsed from the input file.");
            return (int)ExitCode.FatalSchemaErrors;
        }

        IWriter writer = globals.DryRun ? new DryRunWriter() : new AtomicWriter();
        if (!globals.DryRun) Directory.CreateDirectory(outputDir);

        int written = 0, skipped = 0, failed = 0;

        foreach (var table in parseResult.Tables)
        {
            var outputFile = Path.Combine(outputDir, $"{table.Name}.json");
            if (!overwrite && File.Exists(outputFile))
            {
                OutputFormatter.WriteVerbose($"  Skipped (already exists): {outputFile}", globals);
                skipped++;
                continue;
            }

            try
            {
                var json = TableDefinitionSerializer.Serialize(table);
                await writer.WriteAsync(outputFile, json, ct);
                OutputFormatter.WriteVerbose($"  Written: {outputFile}", globals);
                written++;
            }
            catch (Exception ex)
            {
                OutputFormatter.WriteError($"Failed to write {outputFile}: {ex.Message}");
                failed++;
            }
        }

        if (!noSections && parseResult.Sections.Count > 0)
        {
            if (!globals.DryRun) Directory.CreateDirectory(sectionsDir);

            foreach (var section in parseResult.Sections)
            {
                var sectionFile = Path.Combine(sectionsDir, $"{section.Name}.json");
                if (!overwrite && File.Exists(sectionFile))
                {
                    OutputFormatter.WriteVerbose($"  Skipped section (already exists): {sectionFile}", globals);
                    continue;
                }
                var sectionJson = SerializeSection(section);
                await writer.WriteAsync(sectionFile, sectionJson, ct);
                OutputFormatter.WriteVerbose($"  Written section: {sectionFile}", globals);
            }
        }

        OutputFormatter.WriteLine(
            $"Imported {written} table(s)" +
            (skipped > 0 ? $", {skipped} skipped (already exist)" : "") +
            (failed  > 0 ? $", {failed} failed" : "") +
            $" from {Path.GetFileName(inputPath)}",
            globals);

        return parseResult.Errors.Count > 0 || failed > 0
            ? (int)ExitCode.ValidationErrors
            : (int)ExitCode.Success;
    }

    private static string SerializeSection(SectionDefinition section)
    {
        var obj = new
        {
            name        = section.Name,
            description = string.IsNullOrEmpty(section.Description) ? null : section.Description,
            tables      = section.Tables,
        };
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions
        {
            WriteIndented          = true,
            PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
    }
}

// ─── init prisma ──────────────────────────────────────────────────────────

/// <summary>manifesta init prisma — parse a Prisma schema (.prisma) and write one table-definition JSON per model.</summary>
public sealed class InitPrismaCommand : ManifestCommandBase
{
    private readonly Option<string>  _input       = new(["--input"],        "Path to the .prisma file (required)");
    private readonly Option<string?> _outputDir   = new(["--output-dir"],   () => "./tables", "Directory to write JSON table definition files");
    private readonly Option<string?> _provider    = new(["--provider"],     () => null,       "Override the datasource provider (mysql, postgres)");
    private readonly Option<string?> _schema      = new(["--schema"],       () => null,       "Prefix all table names with <schema> (e.g. --schema dbo → dbo.User)");
    private readonly Option<bool>    _importEnums = new(["--import-enums"], () => false,      "Also import enum blocks as reference tables");
    private readonly Option<bool>    _overwrite   = new(["--overwrite"],    () => false,      "Overwrite existing JSON files (without this flag, existing files are skipped)");

    public InitPrismaCommand() : base("prisma", "Parse a Prisma schema and write one table-definition JSON per model")
    {
        _input.IsRequired = true;
        AddOption(_input);
        AddOption(_outputDir);
        AddOption(_provider);
        AddOption(_schema);
        AddOption(_importEnums);
        AddOption(_overwrite);

        this.SetHandler(context => InvokeBaseAsync(context));
    }

    protected override async Task<int> ExecuteAsync(GlobalOptions globals, InvocationContext context, CancellationToken ct)
    {
        var pr          = context.ParseResult;
        var inputPath   = pr.GetValueForOption(_input)!;
        var outputDir   = pr.GetValueForOption(_outputDir) ?? "./tables";
        var provStr     = pr.GetValueForOption(_provider);
        var schema      = pr.GetValueForOption(_schema);
        var importEnums = pr.GetValueForOption(_importEnums);
        var overwrite   = pr.GetValueForOption(_overwrite);

        if (!File.Exists(inputPath))
        {
            OutputFormatter.WriteError($"Input file not found: {inputPath}");
            return (int)ExitCode.FatalSchemaErrors;
        }

        string prismaContent;
        try { prismaContent = await File.ReadAllTextAsync(inputPath, ct); }
        catch (Exception ex)
        {
            OutputFormatter.WriteError($"Could not read {inputPath}: {ex.Message}");
            return (int)ExitCode.FatalSchemaErrors;
        }

        DbProvider? provider = string.IsNullOrWhiteSpace(provStr)
            ? null
            : InitProviderHelper.Parse(provStr);

        var parseResult = new PrismaParser().Parse(
            prismaContent,
            providerOverride: provider,
            schemaPrefix:     schema,
            includeEnums:     importEnums);

        foreach (var err in parseResult.Errors)
            OutputFormatter.WriteError(err);

        IReadOnlyList<TableDefinition> allItems = importEnums
            ? [.. parseResult.Tables, .. parseResult.Enums]
            : parseResult.Tables;

        if (allItems.Count == 0)
        {
            OutputFormatter.WriteError("No models could be parsed from the input file.");
            return (int)ExitCode.FatalSchemaErrors;
        }

        IWriter writer = globals.DryRun ? new DryRunWriter() : new AtomicWriter();
        if (!globals.DryRun) Directory.CreateDirectory(outputDir);

        int written = 0, skipped = 0, failed = 0;

        foreach (var table in allItems)
        {
            var outputFile = Path.Combine(outputDir, $"{table.Name}.json");
            if (!overwrite && File.Exists(outputFile))
            {
                OutputFormatter.WriteVerbose($"  Skipped (already exists): {outputFile}", globals);
                skipped++;
                continue;
            }

            try
            {
                var json = TableDefinitionSerializer.Serialize(table);
                await writer.WriteAsync(outputFile, json, ct);
                OutputFormatter.WriteVerbose($"  Written: {outputFile}", globals);
                written++;
            }
            catch (Exception ex)
            {
                OutputFormatter.WriteError($"Failed to write {outputFile}: {ex.Message}");
                failed++;
            }
        }

        OutputFormatter.WriteLine(
            $"Imported {written} model(s)" +
            (skipped > 0 ? $", {skipped} skipped" : "") +
            (failed  > 0 ? $", {failed} failed" : "") +
            $" from {Path.GetFileName(inputPath)}",
            globals);

        return parseResult.Errors.Count > 0 || failed > 0
            ? (int)ExitCode.ValidationErrors
            : (int)ExitCode.Success;
    }
}

// ─── init sql provider helper ─────────────────────────────────────────────────
// SQL Server is allowed here because init sql is pure file parsing — no live
// connection is required. The enterprise gate applies to live introspection only.

file static class SqlDdlProviderHelper
{
    internal static readonly Option<string> ProviderOption =
        new(["--provider"], () => "mysql",
            "Database dialect of the DDL file: mysql, postgres, sqlite, sqlserver (default: mysql)");

    internal static DbProvider Parse(string? value) =>
        (value ?? "mysql").ToLowerInvariant() switch
        {
            "mysql"                    => DbProvider.MySql,
            "postgres" or "postgresql" => DbProvider.Postgres,
            "sqlite"                   => DbProvider.Sqlite,
            "sqlserver" or "mssql"     => DbProvider.SqlServer,
            var s => throw new ManifestaConfigException(
                $"Unknown provider '{s}'. Valid values: mysql, postgres, sqlite, sqlserver.")
        };
}

// ─── init sql ─────────────────────────────────────────────────────────────────

/// <summary>
/// manifesta init sql — parse SQL DDL CREATE TABLE statements and write one
/// table-definition JSON per table.  Supports MySQL, PostgreSQL, SQLite, and
/// SQL Server dialects.  Works with both clean migration scripts and database
/// dump files (mysqldump --no-data, pg_dump --schema-only).
/// </summary>
public sealed class InitSqlCommand : ManifestCommandBase
{
    private readonly Option<string>  _input     = new(["--input"],
        "Path to a .sql file or directory of .sql files (required)");
    private readonly Option<string?> _outputDir = new(["--output-dir"],
        () => "./tables",
        "Directory to write JSON table definition files (default: ./tables)");
    private readonly Option<string?> _schema    = new(["--schema"],
        () => null,
        "Prefix unqualified table names with <schema> (e.g. --schema dbo → dbo.Customer)");
    private readonly Option<bool>    _overwrite = new(["--overwrite"],
        () => false,
        "Overwrite existing JSON files (without this flag, existing files are skipped)");
    private readonly Option<string>  _provider  = SqlDdlProviderHelper.ProviderOption;
    private readonly Option<bool>    _recursive = new(["--recursive", "-r"],
        () => false,
        "Expand a plain filename --pattern to all subdirectories (prepends **/). " +
        "Ignored when --pattern already contains a path separator or **, and when --input is a file");
    private readonly Option<string>  _pattern   = new(["--pattern"],
        () => "*.sql",
        "Glob pattern for file matching when --input is a directory (default: *.sql). " +
        "Plain filename patterns (e.g. *_up.sql) are controlled by --recursive. " +
        "Path globs (e.g. 2024/**/*.sql, **/create_*.sql) are matched directly and ignore --recursive");

    public InitSqlCommand() : base("sql",
        "Parse SQL DDL files and write one table-definition JSON per table")
    {
        _input.IsRequired = true;
        AddOption(_input);
        AddOption(_outputDir);
        AddOption(_schema);
        AddOption(_overwrite);
        AddOption(_provider);
        AddOption(_recursive);
        AddOption(_pattern);

        this.SetHandler(context => InvokeBaseAsync(context));
    }

    protected override async Task<int> ExecuteAsync(
        GlobalOptions globals, InvocationContext context, CancellationToken ct)
    {
        var pr        = context.ParseResult;
        var inputPath = pr.GetValueForOption(_input)!;
        var outputDir = pr.GetValueForOption(_outputDir) ?? "./tables";
        var schema    = pr.GetValueForOption(_schema);
        var overwrite = pr.GetValueForOption(_overwrite);
        var provider  = SqlDdlProviderHelper.Parse(pr.GetValueForOption(_provider));
        var recursive = pr.GetValueForOption(_recursive);
        var pattern   = pr.GetValueForOption(_pattern) ?? "*.sql";

        // ── Collect .sql files ────────────────────────────────────────────────
        var sqlFiles = SqlDdlFileCollector.Collect(inputPath, pattern, recursive, globals);
        if (sqlFiles is null)
            return (int)ExitCode.FatalSchemaErrors;

        // ── Parse all files ───────────────────────────────────────────────────
        var parser   = new SqlDdlParser();
        var allTables = new List<TableDefinition>();
        var allErrors = new List<string>();

        foreach (var file in sqlFiles)
        {
            OutputFormatter.WriteVerbose($"Parsing: {file}", globals);
            string sql;
            try
            {
                sql = await File.ReadAllTextAsync(file, ct);
            }
            catch (Exception ex)
            {
                OutputFormatter.WriteError($"Could not read {file}: {ex.Message}");
                return (int)ExitCode.FatalSchemaErrors;
            }

            var result = parser.Parse(sql, provider, schema);

            foreach (var err in result.Errors)
                OutputFormatter.WriteError($"[{Path.GetFileName(file)}] {err}");

            allErrors.AddRange(result.Errors);
            allTables.AddRange(result.Tables);
        }

        if (allTables.Count == 0)
        {
            if (allErrors.Count > 0)
            {
                OutputFormatter.WriteError("No tables could be parsed from the input.");
                return (int)ExitCode.FatalSchemaErrors;
            }

            OutputFormatter.WriteLine("No CREATE TABLE statements found in the input.", globals);
            return (int)ExitCode.Success;
        }

        // ── Detect duplicate table names across files ─────────────────────────
        var duplicates = allTables
            .GroupBy(t => t.Name, TableNames.Comparer)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var dup in duplicates)
            OutputFormatter.WriteError(
                $"Duplicate table name '{dup.Key}' found across input files.");

        if (duplicates.Count > 0)
            return (int)ExitCode.FatalSchemaErrors;

        // ── Write JSON files ──────────────────────────────────────────────────
        IWriter writer = globals.DryRun ? new DryRunWriter() : new AtomicWriter();
        if (!globals.DryRun) Directory.CreateDirectory(outputDir);

        int written = 0, skipped = 0, failed = 0;

        foreach (var table in allTables)
        {
            var outputFile = Path.Combine(outputDir, $"{table.Name}.json");

            if (!overwrite && File.Exists(outputFile))
            {
                OutputFormatter.WriteVerbose($"  Skipped (already exists): {outputFile}", globals);
                skipped++;
                continue;
            }

            try
            {
                var json = TableDefinitionSerializer.Serialize(table);
                await writer.WriteAsync(outputFile, json, ct);
                OutputFormatter.WriteVerbose($"  Written: {outputFile}", globals);
                written++;
            }
            catch (Exception ex)
            {
                OutputFormatter.WriteError($"Failed to write {outputFile}: {ex.Message}");
                failed++;
            }
        }

        OutputFormatter.WriteLine(
            $"Imported {written} table(s) from SQL DDL" +
            (skipped > 0 ? $", {skipped} skipped (already exist)" : "") +
            (failed  > 0 ? $", {failed} failed" : "") +
            $" into {outputDir}",
            globals);

        return allErrors.Count > 0 || failed > 0
            ? (int)ExitCode.ValidationErrors
            : (int)ExitCode.Success;
    }
}
