using System.CommandLine;
using System.CommandLine.Invocation;
using Manifesta.Core;
using Manifesta.Core.Drift;
using Manifesta.Core.Filtering;
using Manifesta.Core.IR;
using Manifesta.Core.Merge;
using Manifesta.Core.Pipeline;
using Manifesta.Cli;

namespace Manifesta.Cli.Commands;

// ─── shared helpers ────────────────────────────────────────────────────────────

file static class DbProviderHelper
{
    internal static readonly Option<string> ProviderOption =
        new(["--provider"], () => "mysql",
            "Database provider: mysql, postgres, sqlite. " +
            "Also accepts sqlserver when used with --ddl-file (pure text parsing, no live connection).");

    /// <summary>
    /// Parses the provider for live-connection and input-dir modes.
    /// SQL Server is rejected here — it requires the full edition for live introspection.
    /// </summary>
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

    /// <summary>
    /// Parses the provider for <c>--ddl-file</c> mode.
    /// SQL Server is accepted here because DDL parsing requires no live connection —
    /// the enterprise gate applies to live introspection only.
    /// </summary>
    internal static DbProvider ParseForDdl(string? value) =>
        (value ?? "mysql").ToLowerInvariant() switch
        {
            "mysql"                    => DbProvider.MySql,
            "postgres" or "postgresql" => DbProvider.Postgres,
            "sqlite"                   => DbProvider.Sqlite,
            "sqlserver" or "mssql"     => DbProvider.SqlServer,
            var s => throw new ManifestaConfigException(
                $"Unknown provider '{s}'. Valid values: mysql, postgres, sqlite, sqlserver.")
        };

    internal static void WarnIfSchemaIgnored(string? schemaFilter, DbProvider provider, GlobalOptions globals)
    {
        if (!string.IsNullOrWhiteSpace(schemaFilter) && provider is DbProvider.MySql or DbProvider.Sqlite)
            OutputFormatter.WriteVerbose($"--schema is not supported for {provider} and will be ignored.", globals);
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
                    provider is DbProvider.MySql or DbProvider.Sqlite ? null : schemaFilter, ct);
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
        AddCommand(new DbExportCommand());
        AddCommand(new DbMergeCommand());
    }
}

// ─── db drift ─────────────────────────────────────────────────────────────

public sealed class DbDriftCommand : ManifestCommandBase
{
    private readonly Option<string?> _connection    = new(["--connection"],     () => null,  "Database connection string (mutually exclusive with --input-dir and --ddl-file)");
    private readonly Option<string?> _inputDir      = new(["--input-dir"],      () => null,  "Directory of pre-exported JSON files (mutually exclusive with --connection and --ddl-file)");
    private readonly Option<string?> _ddlFile       = new(["--ddl-file"],       () => null,
        "Path to a .sql file or directory of .sql files to diff against the registry " +
        "(mutually exclusive with --connection and --input-dir). " +
        "Supports mysql, postgres, sqlite, and sqlserver (no live connection required).");
    private readonly Option<string?> _schema        = new(["--schema"],         () => null,
        "Schema handling — meaning depends on mode: " +
        "with --connection/--input-dir: comma-separated schema filter (default: all, ignored for MySQL/SQLite); " +
        "with --ddl-file: prefix applied to unqualified table names (same as init sql --schema).");
    private readonly Option<bool>    _strict        = new(["--strict"],         () => false, "Exit 1 on warnings (extra columns/tables in DB not in repo)");
    private readonly Option<bool>    _includeSchema = new(["--include-schema"], () => false, "Embed full before/after field listings for drifted tables in the report");
    private readonly Option<string?> _output        = new(["--output"],         () => null,  "Full output file path (overrides --output-dir)");
    private readonly Option<string?> _outputDir     = new(["--output-dir"],     () => null,  "Output directory");
    private readonly Option<string>  _provider      = DbProviderHelper.ProviderOption;
    private readonly Option<bool>    _recursive     = new(["--recursive", "-r"], () => false,
        "Expand a plain filename --pattern to all subdirectories. " +
        "Ignored when --pattern already contains a path separator or **. " +
        "Only applicable when --ddl-file is a directory.");
    private readonly Option<string>  _pattern       = new(["--pattern"],        () => "*.sql",
        "Glob pattern for file matching when --ddl-file is a directory (default: *.sql). " +
        "Plain filename patterns (e.g. *_up.sql) are controlled by --recursive. " +
        "Path globs (e.g. 2024/**/*.sql, **/create_*.sql) are matched directly.");

    public DbDriftCommand() : base("drift", "Compare repository definitions against a live database or DDL file (read-only)")
    {
        AddOption(_connection);
        AddOption(_inputDir);
        AddOption(_ddlFile);
        AddOption(_schema);
        AddOption(_strict);
        AddOption(_includeSchema);
        AddOption(_output);
        AddOption(_outputDir);
        AddOption(_provider);
        AddOption(_recursive);
        AddOption(_pattern);

        this.SetHandler(context => InvokeBaseAsync(context));
    }

    protected override async Task<int> ExecuteAsync(GlobalOptions globals, InvocationContext context, CancellationToken ct)
    {
        var pr            = context.ParseResult;
        var connection    = pr.GetValueForOption(_connection);
        var inputDir      = pr.GetValueForOption(_inputDir);
        var ddlFile       = pr.GetValueForOption(_ddlFile);
        var schema        = pr.GetValueForOption(_schema);
        var strict        = pr.GetValueForOption(_strict);
        var includeSchema = pr.GetValueForOption(_includeSchema);
        var outputArg     = pr.GetValueForOption(_output);
        var outputDir     = pr.GetValueForOption(_outputDir);
        var providerStr   = pr.GetValueForOption(_provider);
        var recursive     = pr.GetValueForOption(_recursive);
        var pattern       = pr.GetValueForOption(_pattern) ?? "*.sql";

        // ── Flag validation ────────────────────────────────────────────────────
        var modeCount = new[] { connection, inputDir, ddlFile }
            .Count(s => !string.IsNullOrWhiteSpace(s));

        if (modeCount == 0)
            throw new ManifestaConfigException(
                "Exactly one of --connection, --input-dir, or --ddl-file must be provided.");

        if (modeCount > 1)
            throw new ManifestaConfigException(
                "--connection, --input-dir, and --ddl-file are mutually exclusive. Provide exactly one.");

        // ── Resolve root ───────────────────────────────────────────────────────
        var config   = ConfigLoader.Load(globals);
        var rootPath = ResolveRootPath(globals, config);
        OutputFormatter.WriteVerbose($"Root: {rootPath}", globals);

        // ── Load live definitions ──────────────────────────────────────────────
        IReadOnlyList<TableDefinition> liveTables;
        string sourceDescription;

        if (!string.IsNullOrWhiteSpace(ddlFile))
        {
            // ── DDL file mode ────────────────────────────────────────────────
            // SQL Server is allowed here — no live connection is made.
            var provider = DbProviderHelper.ParseForDdl(providerStr);

            var sqlFiles = SqlDdlFileCollector.Collect(
                Path.GetFullPath(ddlFile), pattern, recursive, globals);
            if (sqlFiles is null)
                return (int)ExitCode.FatalSchemaErrors;

            var allTables = new List<TableDefinition>();
            var allErrors = new List<string>();

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

                // --schema acts as a prefix for unqualified DDL table names
                var result = new SqlDdlParser().Parse(sql, provider, schemaPrefix: schema);
                foreach (var err in result.Errors)
                    OutputFormatter.WriteError($"[{Path.GetFileName(file)}] {err}");
                allErrors.AddRange(result.Errors);
                allTables.AddRange(result.Tables);
            }

            // Duplicate table names across files are a fatal error — they would
            // produce an ambiguous diff.
            var duplicates = allTables
                .GroupBy(t => t.Name, TableNames.Comparer)
                .Where(g => g.Count() > 1)
                .ToList();
            foreach (var dup in duplicates)
                OutputFormatter.WriteError(
                    $"Duplicate table name '{dup.Key}' found across DDL input files.");
            if (duplicates.Count > 0)
                return (int)ExitCode.FatalSchemaErrors;

            // Parse errors stop here unless the caller has opted in to best-effort mode.
            if (allErrors.Count > 0 && !globals.WarnOnly)
            {
                OutputFormatter.WriteError(
                    "DDL parse errors detected. Fix the errors or use --warn-only to proceed with best-effort results.");
                return (int)ExitCode.ValidationErrors;
            }

            liveTables        = allTables.AsReadOnly();
            sourceDescription = $"--ddl-file ({ddlFile})";
            OutputFormatter.WriteVerbose(
                $"DDL tables parsed: {liveTables.Count}" +
                (allErrors.Count > 0 ? $" ({allErrors.Count} parse error(s) ignored via --warn-only)" : ""),
                globals);
        }
        else
        {
            // ── Connection / input-dir mode ──────────────────────────────────
            // SQL Server is enterprise-only for live introspection.
            var provider = DbProviderHelper.Parse(providerStr);
            (liveTables, sourceDescription) =
                await DbProviderHelper.LoadLiveTablesAsync(
                    connection, inputDir, schema, provider, globals, ct);
        }

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

// ─── db merge ─────────────────────────────────────────────────────────────

public sealed class DbMergeCommand : ManifestCommandBase
{
    private readonly Option<string?> _connection          = new(["--connection"],          () => null,  "Database connection string (mutually exclusive with --input-dir)");
    private readonly Option<string?> _inputDir            = new(["--input-dir"],           () => null,  "Directory of pre-exported JSON files (mutually exclusive with --connection)");
    private readonly Option<string?> _schema              = new(["--schema"],              () => null,  "Comma-separated schemas to process (filters by name prefix; ignored for MySQL)");
    private readonly Option<bool>    _removeDeleted       = new(["--remove-deleted-columns"],  () => false, "Remove columns absent from the live database (opt-in)");
    private readonly Option<bool>    _removeDeletedTables = new(["--remove-deleted-tables"],   () => false, "Delete repo files for tables absent from the live database (requires --remove-deleted-columns)");
    private readonly Option<string?> _newTableDir         = new(["--new-table-dir"],       () => null,  "Directory for newly discovered table files (default: <root>/tables/)");
    private readonly Option<bool>    _skipNewTables       = new(["--skip-new-tables"],     () => false, "Only update existing repo files; skip creating files for new tables");
    private readonly Option<bool>    _noReport            = new(["--no-report"],           () => false, "Suppress writing the merge report file");
    private readonly Option<string?> _output              = new(["--output"],              () => null,  "Full path for the merge report file");
    private readonly Option<string?> _outputDir           = new(["--output-dir"],          () => null,  "Directory for the merge report file");
    private readonly Option<string>  _provider            = DbProviderHelper.ProviderOption;

    public DbMergeCommand() : base("merge", "Merge live database schema changes into repository JSON definition files")
    {
        AddOption(_connection);
        AddOption(_inputDir);
        AddOption(_schema);
        AddOption(_removeDeleted);
        AddOption(_removeDeletedTables);
        AddOption(_newTableDir);
        AddOption(_skipNewTables);
        AddOption(_noReport);
        AddOption(_output);
        AddOption(_outputDir);
        AddOption(_provider);

        this.SetHandler(context => InvokeBaseAsync(context));
    }

    protected override async Task<int> ExecuteAsync(GlobalOptions globals, InvocationContext context, CancellationToken ct)
    {
        var pr                  = context.ParseResult;
        var connection          = pr.GetValueForOption(_connection);
        var inputDir            = pr.GetValueForOption(_inputDir);
        var schemaFilter        = pr.GetValueForOption(_schema);
        var removeDeleted       = pr.GetValueForOption(_removeDeleted);
        var removeDeletedTables = pr.GetValueForOption(_removeDeletedTables);
        var newTableDir         = pr.GetValueForOption(_newTableDir);
        var skipNewTables       = pr.GetValueForOption(_skipNewTables);
        var noReport            = pr.GetValueForOption(_noReport);
        var outputArg           = pr.GetValueForOption(_output);
        var outputDir           = pr.GetValueForOption(_outputDir);
        var provider            = DbProviderHelper.Parse(pr.GetValueForOption(_provider));

        // ── Flag validation ────────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(connection) && string.IsNullOrWhiteSpace(inputDir))
            throw new ManifestaConfigException("Either --connection or --input-dir must be provided.");

        if (!string.IsNullOrWhiteSpace(connection) && !string.IsNullOrWhiteSpace(inputDir))
            throw new ManifestaConfigException("--connection and --input-dir are mutually exclusive. Provide exactly one.");

        if (removeDeletedTables && !removeDeleted)
            throw new ManifestaConfigException("--remove-deleted-tables requires --remove-deleted-columns. Both flags must be set together.");

        if (skipNewTables && !string.IsNullOrWhiteSpace(newTableDir))
            throw new ManifestaConfigException("--skip-new-tables and --new-table-dir are mutually exclusive.");

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
            OutputFormatter.WriteLine("No live tables found. Nothing to merge.", globals);
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

        // ── Merge ──────────────────────────────────────────────────────────────
        var merger    = new TableMerger();
        var modified  = new List<MergeResult>();
        var unchanged = new List<MergeResult>();
        var newTables = new List<NewTableResult>();
        var liveNames = TableNames.NewSet();

        foreach (var liveTable in liveTables)
        {
            liveNames.Add(liveTable.Name);

            if (repoByName.TryGetValue(liveTable.Name, out var repoTable))
            {
                var result = merger.Merge(repoTable, liveTable, repoTable.SourceFile, removeDeleted);

                if (result.HasChanges || result.OrphanColumnNames.Count > 0 || result.OrphanedDataKeys.Count > 0)
                    modified.Add(result);
                else
                    unchanged.Add(result);
            }
            else if (!skipNewTables)
            {
                var targetDir = !string.IsNullOrWhiteSpace(newTableDir)
                    ? newTableDir
                    : Path.Combine(rootPath, "tables");

                var filePath = Path.Combine(targetDir, $"{liveTable.Name}.json");
                newTables.Add(new NewTableResult { Table = liveTable, FilePath = filePath });
            }
        }

        // ── Orphan tables ──────────────────────────────────────────────────────
        var orphanTablePaths  = new List<string>();
        var deletedTablePaths = new List<string>();

        foreach (var repoTable in repoTables)
        {
            if (!liveNames.Contains(repoTable.Name))
            {
                if (removeDeletedTables)
                    deletedTablePaths.Add(repoTable.SourceFile);
                else
                    orphanTablePaths.Add(repoTable.SourceFile);
            }
        }

        // ── Aggregate warnings ─────────────────────────────────────────────────
        var orphanColumns = modified
            .SelectMany(r => r.OrphanColumnNames.Select(col => new OrphanColumn
            {
                TableName = r.Merged.Name,
                FieldName = col,
                FilePath  = r.RepoFilePath,
            }))
            .ToList();

        var orphanedDataKeys = modified
            .SelectMany(r => r.OrphanedDataKeys.Select(key => new OrphanedDataKey
            {
                TableName  = r.Merged.Name,
                ColumnName = key,
                FilePath   = r.RepoFilePath,
            }))
            .ToList();

        // ── Write files ────────────────────────────────────────────────────────
        IWriter writer = globals.DryRun ? new DryRunWriter() : new AtomicWriter();

        foreach (var result in modified.Where(r => r.HasChanges))
        {
            var json = TableDefinitionSerializer.Serialize(result.Merged);
            await writer.WriteAsync(result.RepoFilePath, json, ct);
            OutputFormatter.WriteVerbose($"Updated: {result.Merged.Name} ({result.RepoFilePath})", globals);
        }

        foreach (var nt in newTables)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(nt.FilePath)!);
            var json = TableDefinitionSerializer.Serialize(nt.Table);
            await writer.WriteAsync(nt.FilePath, json, ct);
            OutputFormatter.WriteVerbose($"Created: {nt.Table.Name} ({nt.FilePath})", globals);
        }

        foreach (var path in deletedTablePaths)
        {
            if (!globals.DryRun)
            {
                File.Delete(path);
                OutputFormatter.WriteVerbose($"Deleted: {path}", globals);
            }
            else
            {
                OutputFormatter.WriteVerbose($"[dry-run] Would delete: {path}", globals);
            }
        }

        // ── Assemble session ───────────────────────────────────────────────────
        var session = new MergeSession
        {
            Source            = sourceDescription,
            RootPath          = rootPath,
            IsDryRun          = globals.DryRun,
            Timestamp         = DateTimeOffset.UtcNow,
            TotalLiveTables   = liveTables.Count,
            Modified          = modified.AsReadOnly(),
            Unchanged         = unchanged.AsReadOnly(),
            NewTables         = newTables.AsReadOnly(),
            OrphanTablePaths  = orphanTablePaths.AsReadOnly(),
            DeletedTablePaths = deletedTablePaths.AsReadOnly(),
            OrphanColumns     = orphanColumns.AsReadOnly(),
            OrphanedDataKeys  = orphanedDataKeys.AsReadOnly(),
        };

        // ── Write report ───────────────────────────────────────────────────────
        if (!noReport)
        {
            var reportPath    = OutputPathResolver.Resolve(outputArg, outputDir, "merge-report.md");
            var reportContent = new MergeReportGenerator().Generate(session);
            await writer.WriteAsync(reportPath, reportContent, ct);
            OutputFormatter.WriteVerbose($"Report written to: {reportPath}", globals);
        }

        // ── Summary output ─────────────────────────────────────────────────────
        OutputFormatter.WriteLine(
            $"Merge complete — {modified.Count} modified, {newTables.Count} created, " +
            $"{unchanged.Count} unchanged, {deletedTablePaths.Count} deleted.",
            globals);

        if (session.HasWarnings)
        {
            OutputFormatter.WriteLine(
                $"Warnings: {orphanColumns.Count} orphan column(s), " +
                $"{orphanTablePaths.Count} orphan table(s). See report for details.",
                globals);
            return (int)ExitCode.ValidationErrors;
        }

        return (int)ExitCode.Success;
    }
}

// ─── db export ────────────────────────────────────────────────────────────

public sealed class DbExportCommand : ManifestCommandBase
{
    private readonly Option<string?> _connection   = new(["--connection"],    () => null,       "Database connection string (required)");
    private readonly Option<string?> _outputDir    = new(["--output-dir"],    () => "./export", "Output directory for exported JSON files (default: ./export)");
    private readonly Option<string?> _schema       = new(["--schema"],        () => null,       "Comma-separated schemas to export (default: all; ignored for MySQL and SQLite)");
    private readonly Option<bool>    _includeViews = new(["--include-views"], () => false,      "Also export database views");
    private readonly Option<bool>    _overwrite    = new(["--overwrite"],     () => false,      "Overwrite existing JSON files (without this flag, existing files are skipped)");
    private readonly Option<string>  _provider     = DbProviderHelper.ProviderOption;

    public DbExportCommand() : base("export", "Export live database schema to JSON files for use with --input-dir")
    {
        AddOption(_connection);
        AddOption(_outputDir);
        AddOption(_schema);
        AddOption(_includeViews);
        AddOption(_overwrite);
        AddOption(_provider);

        this.SetHandler(context => InvokeBaseAsync(context));
    }

    protected override async Task<int> ExecuteAsync(GlobalOptions globals, InvocationContext context, CancellationToken ct)
    {
        var pr           = context.ParseResult;
        var connection   = pr.GetValueForOption(_connection);
        var outputDirArg = pr.GetValueForOption(_outputDir) ?? "./export";
        var schemaFilter = pr.GetValueForOption(_schema);
        var includeViews = pr.GetValueForOption(_includeViews);
        var overwrite    = pr.GetValueForOption(_overwrite);
        var provider     = DbProviderHelper.Parse(pr.GetValueForOption(_provider));

        // ── Flag validation ────────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(connection))
            throw new ManifestaConfigException("--connection is required for db export.");

        DbProviderHelper.WarnIfSchemaIgnored(schemaFilter, provider, globals);

        // ── Resolve output directory ───────────────────────────────────────────
        var outputDir = Path.GetFullPath(outputDirArg);
        OutputFormatter.WriteVerbose($"Output directory: {outputDir}", globals);

        // ── Introspect ─────────────────────────────────────────────────────────
        OutputFormatter.WriteVerbose("Introspecting database…", globals);
        var effectiveSchema = provider is DbProvider.MySql or DbProvider.Sqlite ? null : schemaFilter;
        var introspector    = DatabaseIntrospectorRegistry.GetFactory().Create(provider, connection);

        IReadOnlyList<TableDefinition> tables;
        IReadOnlyList<TableDefinition> views;
        try
        {
            tables = await introspector.IntrospectTablesOnlyAsync(effectiveSchema, ct);
            views  = includeViews
                ? await introspector.IntrospectViewsOnlyAsync(effectiveSchema, ct)
                : Array.Empty<TableDefinition>();
        }
        catch (Exception ex)
        {
            throw new ManifestaSchemException($"Failed to introspect database: {ex.Message}");
        }

        OutputFormatter.WriteVerbose($"Found {tables.Count} table(s) and {views.Count} view(s)", globals);

        if (tables.Count == 0 && views.Count == 0)
        {
            OutputFormatter.WriteLine("No tables or views found in the database. Nothing to export.", globals);
            return (int)ExitCode.Success;
        }

        // ── Write files ────────────────────────────────────────────────────────
        if (!globals.DryRun)
        {
            try { Directory.CreateDirectory(outputDir); }
            catch (Exception ex) { throw new ManifestaConfigException($"Failed to create output directory: {ex.Message}"); }
        }

        IWriter writer  = globals.DryRun ? new DryRunWriter() : new AtomicWriter();
        int     written = 0;
        int     skipped = 0;

        foreach (var table in tables.Concat(views))
        {
            var filePath = Path.Combine(outputDir, $"{table.Name}.json");

            if (!overwrite && File.Exists(filePath))
            {
                OutputFormatter.WriteVerbose($"Skipped (already exists): {table.Name}.json", globals);
                skipped++;
                continue;
            }

            var json = TableDefinitionSerializer.Serialize(table);
            await writer.WriteAsync(filePath, json, ct);
            OutputFormatter.WriteVerbose($"Exported: {table.Name}.json", globals);
            written++;
        }

        // ── Summary ────────────────────────────────────────────────────────────
        var viewSummary = includeViews && views.Count > 0 ? $" (including {views.Count} view(s))" : "";
        var skipSummary = skipped > 0 ? $", {skipped} skipped (already exist)" : "";
        OutputFormatter.WriteLine(
            $"Exported {written} file(s){viewSummary}{skipSummary} to {outputDir}",
            globals);

        return (int)ExitCode.Success;
    }
}
