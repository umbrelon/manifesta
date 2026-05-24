using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Manifesta.Core;
using Manifesta.Core.IR;
using Manifesta.Core.Pipeline;
using ValidationSeverity = Manifesta.Core.Pipeline.ValidationSeverity;

namespace Manifesta.Cli.Commands;

// ═══════════════════════════════════════════════════════════════════════════
// manifesta validate
// ═══════════════════════════════════════════════════════════════════════════

public sealed class ValidateCommand : Command
{
    public ValidateCommand() : base("validate", "Validation-only workflows")
    {
        AddCommand(new ValidateAllCommand());
        AddCommand(new ValidateCrossCommand());
        AddCommand(new ValidateSchemaCommand());
    }
}

// ─── validate all ─────────────────────────────────────────────────────────

public sealed class ValidateAllCommand : ManifestCommandBase
{
    private readonly Option<bool>    _strict    = new(["--strict"],     () => false, "Treat warnings as errors (exit 1 on warnings)");
    private readonly Option<string?> _outputDir = new(["--output-dir"], () => null,  "Output directory for validation.json");

    public ValidateAllCommand() : base("all", "Validate all DB table definitions and write a structured report")
    {
        AddOption(_strict); AddOption(_outputDir);
        this.SetHandler(context => InvokeBaseAsync(context));
    }

    protected override async Task<int> ExecuteAsync(GlobalOptions globals, InvocationContext context, CancellationToken ct)
    {
        var pr        = context.ParseResult;
        var strict    = pr.GetValueForOption(_strict);
        var outputDir = pr.GetValueForOption(_outputDir);

        var config = ConfigLoader.Load(globals);
        var (tables, sections) = await LoadTablesAndSectionsAsync(globals, config, ct);

        var knownSections = sections
            .Select(s => s.Name)
            .ToHashSet(TableNames.Comparer);

        // ── Table validation ──────────────────────────────────────────────
        IValidator<TableDefinition> tableValidator = new TableValidator(knownSections);
        var tableResult = tableValidator.ValidateAll(tables);

        foreach (var issue in tableResult.Issues)
        {
            if (issue.Severity == ValidationSeverity.Error)
                OutputFormatter.WriteError($"[{issue.Code}] {issue.Message}" +
                    (issue.File is not null ? $" ({issue.File})" : ""));
            else
                OutputFormatter.WriteVerbose($"[{issue.Code}] {issue.Message}" +
                    (issue.File is not null ? $" ({issue.File})" : ""), globals);
        }

        var resolvedDir    = OutputPathResolver.ResolveDirectory(outputDir, ".");
        var tableReport    = ValidationReport.From(tableResult, tablesScanned: tables.Count, sectionsScanned: sections.Count);
        var tableJson      = JsonSerializer.Serialize(tableReport, ValidationReport.JsonOptions);
        IWriter writer     = globals.DryRun ? new DryRunWriter() : new AtomicWriter();
        var validationFile = Path.Combine(resolvedDir, "validation.json");
        await writer.WriteAsync(validationFile, tableJson, ct);
        OutputFormatter.WriteLine(
            $"Wrote validation.json: {tableResult.Issues.Count(i => i.Severity == ValidationSeverity.Error)} error(s), " +
            $"{tableResult.Issues.Count(i => i.Severity == ValidationSeverity.Warning)} warning(s) — {validationFile}", globals);

        return (int)ExitCodeResolver.FromValidationResult(tableResult, strict, globals.WarnOnly);
    }
}

// ─── validate cross ───────────────────────────────────────────────────────

public sealed class ValidateCrossCommand : ManifestCommandBase
{
    private readonly Option<string?> _output    = new(["--output"],     () => null, "Full output file path (overrides --output-dir)");
    private readonly Option<string?> _outputDir = new(["--output-dir"], () => null, "Output directory");

    public ValidateCrossCommand() : base("cross", "Cross-validate FK targets and section memberships")
    {
        AddOption(_output); AddOption(_outputDir);
        this.SetHandler(context => InvokeBaseAsync(context));
    }

    protected override async Task<int> ExecuteAsync(GlobalOptions globals, InvocationContext context, CancellationToken ct)
    {
        var pr        = context.ParseResult;
        var outputArg = pr.GetValueForOption(_output);
        var outputDir = pr.GetValueForOption(_outputDir);

        var config = ConfigLoader.Load(globals);
        var (tables, sections) = await LoadTablesAndSectionsAsync(globals, config, ct);

        // API cross-validation is not available in the community edition.
        IReadOnlyList<ApiDefinition> apis = [];
        var result = new CrossValidator().Validate(tables, sections, apis);

        foreach (var issue in result.Issues)
        {
            if (issue.Severity == ValidationSeverity.Error)
                OutputFormatter.WriteError($"[{issue.Code}] {issue.Message}" +
                    (issue.File is not null ? $" ({issue.File})" : ""));
            else
                OutputFormatter.WriteVerbose($"[{issue.Code}] {issue.Message}" +
                    (issue.File is not null ? $" ({issue.File})" : ""), globals);
        }

        var resolvedPath = OutputPathResolver.Resolve(outputArg, outputDir, "cross-validation.json");
        var report       = ValidationReport.From(result, tablesScanned: tables.Count, sectionsScanned: sections.Count);
        var json         = JsonSerializer.Serialize(report, ValidationReport.JsonOptions);
        IWriter writer   = globals.DryRun ? new DryRunWriter() : new AtomicWriter();
        await writer.WriteAsync(resolvedPath, json, ct);
        OutputFormatter.WriteLine(
            $"Wrote cross-validation.json: {result.Issues.Count(i => i.Severity == ValidationSeverity.Error)} error(s), " +
            $"{result.Issues.Count(i => i.Severity == ValidationSeverity.Warning)} warning(s) — {resolvedPath}", globals);

        return (int)ExitCodeResolver.FromValidationResult(result, strict: false, globals.WarnOnly);
    }
}

// ─── validate schema ──────────────────────────────────────────────────────

public sealed class ValidateSchemaCommand : Command
{
    public ValidateSchemaCommand() : base("schema", "Extract JSON schemas for IDE validation and autocomplete")
    {
        AddCommand(new ValidateSchemaTableCommand());
        AddCommand(new ValidateSchemaSectionCommand());
        AddCommand(new ValidateSchemaApiCommand());
        AddCommand(new ValidateSchemaConfigCommand());
    }
}

public sealed class ValidateSchemaTableCommand : ManifestCommandBase
{
    private readonly Option<string?> _outputDir = new(["--output-dir"], () => null, "Output directory (required)");

    public ValidateSchemaTableCommand() : base("table", "Extract JSON schema for table definition files")
    {
        _outputDir.IsRequired = true;
        AddOption(_outputDir);
        this.SetHandler(context => InvokeBaseAsync(context));
    }

    protected override async Task<int> ExecuteAsync(GlobalOptions globals, InvocationContext context, CancellationToken ct)
    {
        var outputDir  = context.ParseResult.GetValueForOption(_outputDir)!;
        var schema     = new JsonSchemaGenerator().GenerateTableSchema();
        IWriter writer = globals.DryRun ? new DryRunWriter() : new AtomicWriter();
        var outputFile = Path.Combine(outputDir, "table-schema.json");
        await writer.WriteAsync(outputFile, schema, ct);
        OutputFormatter.WriteLine($"Generated table-schema.json at {outputFile}", globals);
        return (int)ExitCode.Success;
    }
}

public sealed class ValidateSchemaSectionCommand : ManifestCommandBase
{
    private readonly Option<string?> _outputDir = new(["--output-dir"], () => null, "Output directory (required)");

    public ValidateSchemaSectionCommand() : base("section", "Extract JSON schema for section definition files")
    {
        _outputDir.IsRequired = true;
        AddOption(_outputDir);
        this.SetHandler(context => InvokeBaseAsync(context));
    }

    protected override async Task<int> ExecuteAsync(GlobalOptions globals, InvocationContext context, CancellationToken ct)
    {
        var outputDir  = context.ParseResult.GetValueForOption(_outputDir)!;
        var schema     = new JsonSchemaGenerator().GenerateSectionSchema();
        IWriter writer = globals.DryRun ? new DryRunWriter() : new AtomicWriter();
        var outputFile = Path.Combine(outputDir, "section-schema.json");
        await writer.WriteAsync(outputFile, schema, ct);
        OutputFormatter.WriteLine($"Generated section-schema.json at {outputFile}", globals);
        return (int)ExitCode.Success;
    }
}

public sealed class ValidateSchemaApiCommand : ManifestCommandBase
{
    private readonly Option<string?> _outputDir = new(["--output-dir"], () => null, "Output directory (required)");

    public ValidateSchemaApiCommand() : base("api", "Extract JSON schema for API definition files")
    {
        _outputDir.IsRequired = true;
        AddOption(_outputDir);
        this.SetHandler(context => InvokeBaseAsync(context));
    }

    protected override async Task<int> ExecuteAsync(GlobalOptions globals, InvocationContext context, CancellationToken ct)
    {
        var outputDir  = context.ParseResult.GetValueForOption(_outputDir)!;
        var schema     = new JsonSchemaGenerator().GenerateApiSchema();
        IWriter writer = globals.DryRun ? new DryRunWriter() : new AtomicWriter();
        var outputFile = Path.Combine(outputDir, "api-schema.json");
        await writer.WriteAsync(outputFile, schema, ct);
        OutputFormatter.WriteLine($"Generated api-schema.json at {outputFile}", globals);
        return (int)ExitCode.Success;
    }
}

public sealed class ValidateSchemaConfigCommand : ManifestCommandBase
{
    private readonly Option<string?> _outputDir = new(["--output-dir"], () => null, "Output directory (required)");

    public ValidateSchemaConfigCommand() : base("config", "Extract JSON schema for manifesta.config.json")
    {
        _outputDir.IsRequired = true;
        AddOption(_outputDir);
        this.SetHandler(context => InvokeBaseAsync(context));
    }

    protected override async Task<int> ExecuteAsync(GlobalOptions globals, InvocationContext context, CancellationToken ct)
    {
        var outputDir  = context.ParseResult.GetValueForOption(_outputDir)!;
        var schema     = new JsonSchemaGenerator().GenerateConfigSchema();
        IWriter writer = globals.DryRun ? new DryRunWriter() : new AtomicWriter();
        var outputFile = Path.Combine(outputDir, "manifesta-config-schema.json");
        await writer.WriteAsync(outputFile, schema, ct);
        OutputFormatter.WriteLine($"Generated manifesta-config-schema.json at {outputFile}", globals);
        return (int)ExitCode.Success;
    }
}
