using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Manifesta.Core;
using Manifesta.Core.Filtering;
using Manifesta.Core.IR;
using Manifesta.Core.Pipeline;

// ── This file is shared between the OSS and enterprise CLI builds via a linked
// source file reference in the enterprise .csproj.  Keep it free of any
// OSS-vs-enterprise conditional logic.  Provider gating for live-DB commands
// (init db) lives in each CLI's own InitCommand.cs. ──────────────────────────

namespace Manifesta.Cli.Commands;

// ── Provider helpers (file-scoped — only used within this file) ───────────────

// For init sql: supports all four dialects; no live-DB gate needed.
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

// For init prisma: provider is an optional override — null means "detect from file".
file static class FileInitProviderHelper
{
    internal static DbProvider? ParseOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null :
        value.ToLowerInvariant() switch
        {
            "mysql"                    => DbProvider.MySql,
            "postgres" or "postgresql" => DbProvider.Postgres,
            "sqlite"                   => DbProvider.Sqlite,
            "sqlserver" or "mssql"     => DbProvider.SqlServer,
            var s => throw new ManifestaConfigException(
                $"Unknown provider '{s}'. Valid values: mysql, postgres, sqlite, sqlserver.")
        };
}

// ═══════════════════════════════════════════════════════════════════════════════
// manifesta init sql
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// manifesta init sql — parse SQL DDL CREATE TABLE statements and write one
/// table-definition JSON per table.  Supports MySQL, PostgreSQL, SQLite, and
/// SQL Server dialects.  Works with both clean migration scripts and database
/// dump files (mysqldump --no-data, pg_dump --schema-only).
/// </summary>
public sealed class InitSqlCommand : ManifestCommandBase
{
    private readonly Option<string>  _input         = new(["--input"],
        "Path to a .sql file or directory of .sql files (required)");
    private readonly Option<string?> _outputDir     = new(["--output-dir"],
        () => "./tables",
        "Directory to write JSON table definition files (default: ./tables)");
    private readonly Option<string?> _defaultSchema = new(["--default-schema"],
        () => null,
        "Schema assigned to unqualified table names (e.g. --default-schema dbo → dbo.Customer). " +
        "Tables with an explicit schema qualifier in the DDL keep their own schema. " +
        "Ignored for MySQL and SQLite (those providers have no schema namespace).");
    private readonly Option<string?> _schema        = new(["--schema"],
        () => null,
        "Only import tables whose schema matches (e.g. --schema dbo imports only dbo.* tables). " +
        "Comma-separate to allow multiple schemas (--schema dbo,app). " +
        "Ignored for MySQL and SQLite (those providers have no schema namespace).");
    private readonly Option<bool>    _overwrite     = new(["--overwrite"],
        () => false,
        "Overwrite existing JSON files (without this flag, existing files are skipped)");
    private readonly Option<string>  _provider      = SqlDdlProviderHelper.ProviderOption;
    private readonly Option<bool>    _recursive     = new(["--recursive", "-r"],
        () => false,
        "Expand a plain filename --pattern to all subdirectories (prepends **/). " +
        "Ignored when --pattern already contains a path separator or **, and when --input is a file");
    private readonly Option<string>  _pattern       = new(["--pattern"],
        () => "*.sql",
        "Glob pattern for file matching when --input is a directory (default: *.sql). " +
        "Plain filename patterns (e.g. *_up.sql) are controlled by --recursive. " +
        "Path globs (e.g. 2024/**/*.sql, **/create_*.sql) are matched directly and ignore --recursive");
    private readonly Option<bool>    _lastWins      = new(["--last-wins"],
        () => false,
        "When the same table name appears in multiple input files, keep the last-parsed definition " +
        "and emit a warning instead of failing. Useful when scanning a monorepo where shared tables " +
        "are duplicated across modules.");

    public InitSqlCommand() : base("sql",
        "Parse SQL DDL files and write one table-definition JSON per table")
    {
        _input.IsRequired = true;
        AddOption(_input);
        AddOption(_outputDir);
        AddOption(_defaultSchema);
        AddOption(_schema);
        AddOption(_overwrite);
        AddOption(_provider);
        AddOption(_recursive);
        AddOption(_pattern);
        AddOption(_lastWins);

        this.SetHandler(context => InvokeBaseAsync(context));
    }

    protected override async Task<int> ExecuteAsync(
        GlobalOptions globals, InvocationContext context, CancellationToken ct)
    {
        var pr            = context.ParseResult;
        var inputPath     = pr.GetValueForOption(_input)!;
        var outputDir     = pr.GetValueForOption(_outputDir) ?? "./tables";
        var defaultSchema = pr.GetValueForOption(_defaultSchema);
        var schemaFilter  = pr.GetValueForOption(_schema);
        var overwrite     = pr.GetValueForOption(_overwrite);
        var provider      = SqlDdlProviderHelper.Parse(pr.GetValueForOption(_provider));
        var recursive     = pr.GetValueForOption(_recursive);
        var pattern       = pr.GetValueForOption(_pattern) ?? "*.sql";
        var lastWins      = pr.GetValueForOption(_lastWins);

        // MySQL and SQLite have no schema namespace — both schema flags are no-ops.
        if (provider is DbProvider.MySql or DbProvider.Sqlite)
        {
            if (!string.IsNullOrWhiteSpace(defaultSchema))
                OutputFormatter.WriteVerbose(
                    $"--default-schema is not applicable for {provider} and will be ignored.", globals);
            if (!string.IsNullOrWhiteSpace(schemaFilter))
                OutputFormatter.WriteVerbose(
                    $"--schema is not applicable for {provider} and will be ignored.", globals);
            defaultSchema = null;
            schemaFilter  = null;
        }

        var schema = defaultSchema;

        // ── Collect .sql files ────────────────────────────────────────────────
        var sqlFiles = SqlDdlFileCollector.Collect(inputPath, pattern, recursive, globals);
        if (sqlFiles is null)
            return (int)ExitCode.FatalSchemaErrors;

        // ── Parse all files ───────────────────────────────────────────────────
        var parser        = new SqlDdlParser();
        var allTables     = new List<TableDefinition>();
        var allErrors     = new List<string>();
        // Collects ALTER TABLE … ADD CONSTRAINT … PRIMARY KEY additions from every file.
        // Used below to backfill tables whose CREATE TABLE body omits the PK constraint
        // (common in SQL Server database projects where PKs live in *_Updates.sql files).
        var allPkAdditions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        int filesWithDups = 0;

        foreach (var file in sqlFiles)
        {
            OutputFormatter.WriteVerbose($"Parsing: {file}", globals);
            string sql;
            try { sql = await File.ReadAllTextAsync(file, ct); }
            catch (Exception ex)
            {
                OutputFormatter.WriteError($"Could not read {file}: {ex.Message}");
                return (int)ExitCode.FatalSchemaErrors;
            }

            var result = parser.Parse(sql, provider, schema);

            foreach (var err in result.Errors)
                OutputFormatter.WriteError($"[{Path.GetFileName(file)}] {err}");
            allErrors.AddRange(result.Errors);

            // Accumulate PK additions (last writer wins if multiple files set the same table's PK).
            foreach (var pk in result.PkAdditions)
                allPkAdditions[pk.TableName] = pk.Columns;

            // Skip files where the same table name appears more than once
            // (typical cause: conditional migration scripts with two CREATE TABLE variants).
            var intraFileDups = result.Tables
                .GroupBy(t => t.Name, TableNames.Comparer)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (intraFileDups.Count > 0)
            {
                var names = string.Join(", ", intraFileDups.Select(n => $"'{n}'"));
                OutputFormatter.WriteWarning(
                    $"[{Path.GetFileName(file)}] Skipped: {names} " +
                    $"{(intraFileDups.Count == 1 ? "is" : "are")} defined more than once in this file. " +
                    "Remove duplicate CREATE TABLE statements and re-run.");
                allErrors.Add(
                    $"[{Path.GetFileName(file)}] skipped — duplicate table names within file: {names}");
                filesWithDups++;
                continue;
            }

            allTables.AddRange(result.Tables);
        }

        // ── Back-fill PRIMARY KEY from ALTER TABLE statements ─────────────────
        // Tables in SQL Server database projects often have their PK defined in a
        // separate *_Updates.sql file via ALTER TABLE … ADD CONSTRAINT … PRIMARY KEY.
        // Apply those additions to any table that still has an empty PrimaryKey list.
        if (allPkAdditions.Count > 0)
        {
            for (int idx = 0; idx < allTables.Count; idx++)
            {
                var t = allTables[idx];
                if (t.PrimaryKey.Count == 0 && allPkAdditions.TryGetValue(t.Name, out var pkCols))
                    allTables[idx] = t with { PrimaryKey = pkCols };
            }
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

        // ── Apply --schema filter ─────────────────────────────────────────────
        var filter = new SchemaFilter(schemaFilter);
        if (filter.IsActive)
        {
            var before = allTables.Count;
            allTables.RemoveAll(t => !filter.Matches(t.Name));
            OutputFormatter.WriteVerbose(
                $"Schema filter '{schemaFilter}': {allTables.Count} of {before} table(s) kept.", globals);
        }

        // ── Detect duplicate table names across files ─────────────────────────
        var duplicates = allTables
            .GroupBy(t => t.Name, TableNames.Comparer)
            .Where(g => g.Count() > 1)
            .ToList();

        if (duplicates.Count > 0)
        {
            if (lastWins)
            {
                // Keep only the last definition for each name; emit a warning per duplicate.
                foreach (var dup in duplicates)
                    OutputFormatter.WriteWarning(
                        $"Duplicate table name '{dup.Key}' found across input files — keeping last definition (--last-wins).");

                var seen = new HashSet<string>(TableNames.Comparer);
                // Iterate in reverse so the last occurrence is the one we keep.
                for (int idx = allTables.Count - 1; idx >= 0; idx--)
                {
                    if (!seen.Add(allTables[idx].Name))
                        allTables.RemoveAt(idx);
                }
            }
            else
            {
                foreach (var dup in duplicates)
                    OutputFormatter.WriteError($"Duplicate table name '{dup.Key}' found across input files.");
                return (int)ExitCode.FatalSchemaErrors;
            }
        }

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

        // ── Scaffold _/manifesta.config.json and _/document-sections/all-tables.json ──
        // Strip any trailing directory separator before computing the parent.  Without this,
        // a path ending in '/' (e.g. "--output-dir ./manifesta/tables/") resolves to the
        // output directory itself instead of its parent, placing the config scaffold inside
        // the tables directory instead of alongside it.
        var resolvedOutputDir = Path.GetFullPath(outputDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var configRoot        = Path.GetDirectoryName(resolvedOutputDir) ?? resolvedOutputDir;
        var configDir         = Path.Combine(configRoot, "_");

        if (!globals.DryRun)
        {
            try { Directory.CreateDirectory(configDir); }
            catch (Exception ex) { throw new ManifestaConfigException($"Failed to create config directory: {ex.Message}"); }
        }

        var jsonOpts = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        var configPath = Path.Combine(configDir, "manifesta.config.json");
        if (!File.Exists(configPath) || overwrite)
        {
            var configObj  = new { paths = new { root = "../", skip = new[] { "_" } } };
            var configJson = JsonSerializer.Serialize(configObj, jsonOpts);
            try { await writer.WriteAsync(configPath, configJson, ct); }
            catch (Exception ex) { throw new ManifestaSchemException($"Failed to write {configPath}: {ex.Message}"); }
            OutputFormatter.WriteVerbose("Generated config: _/manifesta.config.json", globals);
        }
        else
        {
            OutputFormatter.WriteVerbose("Skipped config (already exists): _/manifesta.config.json", globals);
        }

        var docSectionsDir = Path.Combine(configDir, "document-sections");
        if (!globals.DryRun)
        {
            try { Directory.CreateDirectory(docSectionsDir); }
            catch (Exception ex) { throw new ManifestaConfigException($"Failed to create document-sections directory: {ex.Message}"); }
        }

        var sectionPath = Path.Combine(docSectionsDir, "all-tables.json");
        if (!File.Exists(sectionPath) || overwrite)
        {
            var sectionDef  = new
            {
                name        = "All Tables",
                description = "Auto-generated section containing all database tables",
                tables      = allTables.Select(t => t.Name).OrderBy(n => n).ToList()
            };
            var sectionJson = JsonSerializer.Serialize(sectionDef, jsonOpts);
            try { await writer.WriteAsync(sectionPath, sectionJson, ct); }
            catch (Exception ex) { throw new ManifestaSchemException($"Failed to write {sectionPath}: {ex.Message}"); }
            OutputFormatter.WriteVerbose("Generated section: _/document-sections/all-tables.json", globals);
        }
        else
        {
            OutputFormatter.WriteVerbose("Skipped section (already exists): _/document-sections/all-tables.json", globals);
        }

        OutputFormatter.WriteLine(
            $"Imported {written} table(s) from SQL DDL" +
            (skipped       > 0 ? $", {skipped} skipped (already exist)" : "") +
            (filesWithDups > 0 ? $", {filesWithDups} file(s) skipped (intra-file duplicate tables — see warnings)" : "") +
            (failed        > 0 ? $", {failed} failed" : "") +
            $" into {outputDir} — registry initialized at {configRoot}",
            globals);

        if (failed > 0)
            return (int)ExitCode.ValidationErrors;
        return allErrors.Count > 0 && !globals.WarnOnly
            ? (int)ExitCode.ValidationErrors
            : (int)ExitCode.Success;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// manifesta init dbml
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>manifesta init dbml — parse a DBML file and write one table-definition JSON per table.</summary>
public sealed class InitDbmlCommand : ManifestCommandBase
{
    private readonly Option<string>  _input         = new(["--input"],          "Path to the .dbml file (required)");
    private readonly Option<string?> _outputDir     = new(["--output-dir"],     () => "./tables",            "Directory to write JSON table definition files");
    private readonly Option<string?> _sectionsDir   = new(["--sections-dir"],   () => "./document-sections", "Directory to write SectionDefinition JSON files from TableGroup blocks");
    private readonly Option<bool>    _noSections    = new(["--no-sections"],    () => false,                 "Suppress writing SectionDefinition files");
    private readonly Option<string?> _defaultSchema = new(["--default-schema"], () => null,
        "Schema prefix for all imported table names (e.g. --default-schema dbo → dbo.Customer)");
    private readonly Option<bool>    _overwrite     = new(["--overwrite"],      () => false,                 "Overwrite existing JSON files (without this flag, existing files are skipped)");

    public InitDbmlCommand() : base("dbml", "Parse a DBML file and write one table-definition JSON per table")
    {
        _input.IsRequired = true;
        AddOption(_input);
        AddOption(_outputDir);
        AddOption(_sectionsDir);
        AddOption(_noSections);
        AddOption(_defaultSchema);
        AddOption(_overwrite);

        this.SetHandler(context => InvokeBaseAsync(context));
    }

    protected override async Task<int> ExecuteAsync(GlobalOptions globals, InvocationContext context, CancellationToken ct)
    {
        var pr            = context.ParseResult;
        var inputPath     = pr.GetValueForOption(_input)!;
        var outputDir     = pr.GetValueForOption(_outputDir) ?? "./tables";
        var sectionsDir   = pr.GetValueForOption(_sectionsDir) ?? "./document-sections";
        var noSections    = pr.GetValueForOption(_noSections);
        var defaultSchema = pr.GetValueForOption(_defaultSchema);
        var overwrite     = pr.GetValueForOption(_overwrite);

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

        var parseResult = new DbmlParser().Parse(dbml, defaultSchema);

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

// ═══════════════════════════════════════════════════════════════════════════════
// manifesta init prisma
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>manifesta init prisma — parse a Prisma schema (.prisma) and write one table-definition JSON per model.</summary>
public sealed class InitPrismaCommand : ManifestCommandBase
{
    private readonly Option<string>  _input         = new(["--input"],          "Path to the .prisma file (required)");
    private readonly Option<string?> _outputDir     = new(["--output-dir"],     () => "./tables", "Directory to write JSON table definition files");
    private readonly Option<string?> _provider      = new(["--provider"],       () => null,       "Override the datasource provider (mysql, postgres, sqlite, sqlserver)");
    private readonly Option<string?> _defaultSchema = new(["--default-schema"], () => null,       "Schema prefix for all table names (e.g. --default-schema dbo → dbo.User)");
    private readonly Option<bool>    _importEnums   = new(["--import-enums"],   () => false,      "Also import enum blocks as reference tables");
    private readonly Option<bool>    _overwrite     = new(["--overwrite"],      () => false,      "Overwrite existing JSON files (without this flag, existing files are skipped)");

    public InitPrismaCommand() : base("prisma", "Parse a Prisma schema and write one table-definition JSON per model")
    {
        _input.IsRequired = true;
        AddOption(_input);
        AddOption(_outputDir);
        AddOption(_provider);
        AddOption(_defaultSchema);
        AddOption(_importEnums);
        AddOption(_overwrite);

        this.SetHandler(context => InvokeBaseAsync(context));
    }

    protected override async Task<int> ExecuteAsync(GlobalOptions globals, InvocationContext context, CancellationToken ct)
    {
        var pr            = context.ParseResult;
        var inputPath     = pr.GetValueForOption(_input)!;
        var outputDir     = pr.GetValueForOption(_outputDir) ?? "./tables";
        var provStr       = pr.GetValueForOption(_provider);
        var defaultSchema = pr.GetValueForOption(_defaultSchema);
        var importEnums   = pr.GetValueForOption(_importEnums);
        var overwrite     = pr.GetValueForOption(_overwrite);

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

        var provider = FileInitProviderHelper.ParseOptional(provStr);

        var parseResult = new PrismaParser().Parse(
            prismaContent,
            providerOverride: provider,
            schemaPrefix:     defaultSchema,
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
