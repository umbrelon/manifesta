using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Manifesta.Core;
using Manifesta.Core.IR;
using Manifesta.Core.Pipeline;
using Manifesta.Doc;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Manifesta.Cli.Commands;

public sealed class DevCommand : Command
{
    public DevCommand() : base("dev", "Developer utilities")
    {
        AddCommand(new DevDumpIrCommand());
        AddCommand(new DevInspectCommand());
        AddCommand(new DevGraphCommand());
    }
}

// ── dump-ir ───────────────────────────────────────────────────────────────────

public sealed class DevDumpIrCommand : ManifestCommandBase
{
    private readonly Option<string>  _format = new(["--format"], () => "json", "Output format: json, yaml");
    private readonly Option<string?> _output = new(["--output"], () => null,   "Output file (default: stdout)");

    public DevDumpIrCommand() : base("dump-ir", "Dump the full internal representation (IR) of the schema")
    {
        AddOption(_format);
        AddOption(_output);
        this.SetHandler(context => InvokeBaseAsync(context));
    }

    protected override async Task<int> ExecuteAsync(GlobalOptions globals, InvocationContext context, CancellationToken ct)
    {
        var format = context.ParseResult.GetValueForOption(_format)!.ToLowerInvariant();
        var output = context.ParseResult.GetValueForOption(_output);

        var config           = ConfigLoader.Load(globals);
        var (tables, sections) = await LoadTablesAndSectionsAsync(globals, config, ct);

        var root = new ManifestRoot { Tables = tables, Sections = sections };

        var text = format == "yaml" ? OssIrSerializer.ToYaml(root) : OssIrSerializer.ToJson(root);

        if (output is not null)
        {
            IWriter writer = globals.DryRun ? new DryRunWriter() : new AtomicWriter();
            await writer.WriteAsync(output, text, ct);
        }
        else
        {
            Console.WriteLine(text);
        }

        return (int)ExitCode.Success;
    }
}

// ── inspect ───────────────────────────────────────────────────────────────────

public sealed class DevInspectCommand : Command
{
    public DevInspectCommand() : base("inspect", "Inspect a table definition")
    {
        AddCommand(new DevInspectTableCommand());
    }
}

public sealed class DevInspectTableCommand : ManifestCommandBase
{
    private readonly Argument<string> _name   = new("TABLE_NAME", "Table name to inspect (case-insensitive)");
    private readonly Option<string>   _format = new(["--format"], () => "human", "Output format: human, json, yaml");

    public DevInspectTableCommand() : base("table", "Inspect a table definition")
    {
        AddArgument(_name);
        AddOption(_format);
        this.SetHandler(context => InvokeBaseAsync(context));
    }

    protected override async Task<int> ExecuteAsync(GlobalOptions globals, InvocationContext context, CancellationToken ct)
    {
        var tableName = context.ParseResult.GetValueForArgument(_name);
        var format    = context.ParseResult.GetValueForOption(_format)!.ToLowerInvariant();

        var config      = ConfigLoader.Load(globals);
        var (tables, _) = await LoadTablesAndSectionsAsync(globals, config, ct);

        var table = tables.FirstOrDefault(t => TableNames.Comparer.Equals(t.Name, tableName));
        if (table is null)
        {
            OutputFormatter.WriteError($"Table not found: {tableName}");
            return (int)ExitCode.ConfigOrInvocationError;
        }

        var text = format switch
        {
            "json" => OssIrSerializer.ToJson(table),
            "yaml" => OssIrSerializer.ToYaml(table),
            _      => OssTableHumanFormatter.Format(table),
        };

        Console.WriteLine(text);
        return (int)ExitCode.Success;
    }
}

// ── graph ─────────────────────────────────────────────────────────────────────

public sealed class DevGraphCommand : ManifestCommandBase
{
    private readonly Option<string>  _format      = new(["--format"],      () => "mermaid", "Output format: mermaid, dot, json");
    private readonly Option<bool>    _interactive = new(["--interactive"], () => false,     "Open graph viewer (requires graphviz)");
    private readonly Option<string?> _output      = new(["--output"],      () => null,      "Output file (default: stdout)");

    public DevGraphCommand() : base("graph", "Visualise the full schema dependency graph")
    {
        AddOption(_format); AddOption(_interactive); AddOption(_output);
        this.SetHandler(context => InvokeBaseAsync(context));
    }

    protected override async Task<int> ExecuteAsync(GlobalOptions globals, InvocationContext context, CancellationToken ct)
    {
        var format      = context.ParseResult.GetValueForOption(_format)!.ToLowerInvariant();
        var interactive = context.ParseResult.GetValueForOption(_interactive);
        var output      = context.ParseResult.GetValueForOption(_output);

        var config      = ConfigLoader.Load(globals);
        var (tables, _) = await LoadTablesAndSectionsAsync(globals, config, ct);

        var text = format switch
        {
            "dot"  => OssGraphGenerator.GenerateDot(tables),
            "json" => OssGraphGenerator.GenerateJson(tables),
            _      => OssGraphGenerator.GenerateMermaid(tables),
        };

        if (interactive)
        {
            var dotContent = format == "dot" ? text : OssGraphGenerator.GenerateDot(tables);
            return await OpenInteractiveAsync(dotContent, ct);
        }

        if (output is not null)
        {
            IWriter writer = globals.DryRun ? new DryRunWriter() : new AtomicWriter();
            await writer.WriteAsync(output, text, ct);
        }
        else
        {
            Console.WriteLine(text);
        }

        return (int)ExitCode.Success;
    }

    private static async Task<int> OpenInteractiveAsync(string dotContent, CancellationToken ct)
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), $"manifesta-graph-{Path.GetRandomFileName()}.dot");
        await File.WriteAllTextAsync(tmpFile, dotContent, ct);

        try
        {
            using var proc = Process.Start(new ProcessStartInfo("dot")
            {
                Arguments       = $"-Tx11 \"{tmpFile}\"",
                UseShellExecute = false,
            });
            if (proc is not null)
                await proc.WaitForExitAsync(ct);
            return (int)ExitCode.Success;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            OutputFormatter.WriteError(
                "graphviz 'dot' command not found. Install graphviz (https://graphviz.org) to use --interactive.");
            OutputFormatter.WriteError($"DOT file written to: {tmpFile}");
            return (int)ExitCode.ConfigOrInvocationError;
        }
    }
}

// ── shared helpers ────────────────────────────────────────────────────────────

file static class OssIrSerializer
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly ISerializer YamlSerializer =
        new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

    public static string ToJson(object value) => JsonSerializer.Serialize(value, JsonOpts);

    public static string ToYaml(object value)
    {
        var json = JsonSerializer.Serialize(value, JsonOpts);
        using var doc = JsonDocument.Parse(json);
        return YamlSerializer.Serialize(ToPlain(doc.RootElement));
    }

    private static object? ToPlain(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => el.EnumerateObject()
                                  .ToDictionary(p => p.Name, p => ToPlain(p.Value)),
        JsonValueKind.Array  => el.EnumerateArray().Select(ToPlain).ToList(),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? (object?)l
                              : el.TryGetDouble(out var d) ? d
                              : el.GetRawText(),
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        _                    => null,
    };
}

file static class OssTableHumanFormatter
{
    public static string Format(TableDefinition table)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Table: {table.Name}");
        if (!string.IsNullOrWhiteSpace(table.Description))
            sb.AppendLine($"Description: {table.Description}");
        if (!string.IsNullOrWhiteSpace(table.ShortDescription))
            sb.AppendLine($"Short description: {table.ShortDescription}");
        if (table.DatabaseTypes.Count > 0)
            sb.AppendLine($"Database types: {string.Join(", ", table.DatabaseTypes)}");
        if (table.Sections.Count > 0)
            sb.AppendLine($"Sections: {string.Join(", ", table.Sections)}");
        if (table.IsReferenceTable)
            sb.AppendLine("Reference table: yes");
        if (table.IsDeprecated)
        {
            sb.AppendLine("Deprecated: yes");
            if (!string.IsNullOrWhiteSpace(table.DeprecationMessage))
                sb.AppendLine($"Deprecation message: {table.DeprecationMessage}");
        }

        sb.AppendLine($"Primary key: {(table.PrimaryKey.Count > 0 ? string.Join(", ", table.PrimaryKey) : "(none)")}");

        // ── Fields ────────────────────────────────────────────────────────────

        sb.AppendLine();
        sb.AppendLine($"Fields ({table.Fields.Count}):");

        if (table.Fields.Count == 0)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            var pkSet = new HashSet<string>(table.PrimaryKey, TableNames.Comparer);
            var fkSet = new HashSet<string>(table.ForeignKeys.Select(f => f.SourceField), TableNames.Comparer);

            var nameW = Math.Max(4, table.Fields.Max(f => f.Name.Length));
            var typeW = Math.Max(4, table.Fields.Max(f => f.Type.Length));

            sb.AppendLine($"  {"Name".PadRight(nameW)}  {"Type".PadRight(typeW)}  {"Nullable",-8}  {"PK",-2}  {"FK",-2}  Description");
            sb.AppendLine($"  {new string('─', nameW)}  {new string('─', typeW)}  {"────────",-8}  {"──",-2}  {"──",-2}  ───────────");
            foreach (var f in table.Fields)
            {
                var pk = pkSet.Contains(f.Name) ? "✓" : "";
                var fk = fkSet.Contains(f.Name) ? "✓" : "";
                sb.AppendLine($"  {f.Name.PadRight(nameW)}  {f.Type.PadRight(typeW)}  {(f.Nullable ? "Yes" : "No"),-8}  {pk,-2}  {fk,-2}  {f.Description}");
            }
        }

        // ── Foreign keys ──────────────────────────────────────────────────────

        sb.AppendLine();
        sb.AppendLine($"Foreign keys ({table.ForeignKeys.Count}):");

        if (table.ForeignKeys.Count == 0)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            var srcW = Math.Max(12, table.ForeignKeys.Max(f => f.SourceField.Length));
            var ttW  = Math.Max(12, table.ForeignKeys.Max(f => f.TargetTable.Length));
            var tfW  = Math.Max(12, table.ForeignKeys.Max(f => f.TargetField.Length));

            sb.AppendLine($"  {"Source field".PadRight(srcW)}  {"Target table".PadRight(ttW)}  {"Target field".PadRight(tfW)}  Kind");
            sb.AppendLine($"  {new string('─', srcW)}  {new string('─', ttW)}  {new string('─', tfW)}  ────────");
            foreach (var fk in table.ForeignKeys)
                sb.AppendLine($"  {fk.SourceField.PadRight(srcW)}  {fk.TargetTable.PadRight(ttW)}  {fk.TargetField.PadRight(tfW)}  {fk.Kind.ToString().ToLowerInvariant()}");
        }

        return sb.ToString().TrimEnd();
    }
}

file static class OssGraphGenerator
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string GenerateMermaid(IReadOnlyList<TableDefinition> tables)
    {
        var tablesByName  = tables.ToDictionary(t => t.Name, TableNames.Comparer);
        var allTableNames = tables.Select(t => t.Name).ToList();
        var syntheticErd  = new ErdDefinition
        {
            Fields         = ErdFields.PkAndFk,
            IncludeLogical = true,
            IncludeVirtual = false,
        };
        return new ErdGenerator().Generate(syntheticErd, allTableNames, tablesByName);
    }

    public static string GenerateDot(IReadOnlyList<TableDefinition> tables)
    {
        var sb = new StringBuilder();
        sb.AppendLine("digraph schema {");
        sb.AppendLine("  rankdir=LR;");
        sb.AppendLine("  node [shape=box, fontsize=10];");
        sb.AppendLine("  edge [fontsize=9];");
        sb.AppendLine();

        foreach (var t in tables.OrderBy(t => t.Name, TableNames.Comparer))
            sb.AppendLine($"  \"{Esc(t.Name)}\";");

        sb.AppendLine();

        foreach (var t in tables.OrderBy(t => t.Name, TableNames.Comparer))
        {
            foreach (var fk in t.ForeignKeys)
            {
                var label = $"{fk.SourceField}→{fk.TargetField}";
                sb.AppendLine($"  \"{Esc(t.Name)}\" -> \"{Esc(fk.TargetTable)}\" [label=\"{Esc(label)}\"];");
            }
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    public static string GenerateJson(IReadOnlyList<TableDefinition> tables)
    {
        var graph = new SchemaGraph
        {
            Nodes = tables,
            Edges = tables
                .SelectMany(t => t.ForeignKeys.Select(fk => new GraphEdge
                {
                    SourceTable = t.Name,
                    TargetTable = fk.TargetTable,
                    SourceField = fk.SourceField,
                    TargetField = fk.TargetField,
                }))
                .ToList(),
        };
        return JsonSerializer.Serialize(graph, JsonOpts);
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
