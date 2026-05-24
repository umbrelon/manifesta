using System.Text;
using System.Text.Json;
using Manifesta.Core;
using Manifesta.Core.IR;

namespace Manifesta.Doc;

/// <summary>
/// Generates a single <c>database.dbml</c> file from the resolved IR.
///
/// Mapping:
/// <list type="bullet">
///   <item><see cref="TableDefinition"/> → DBML <c>Table</c> block</item>
///   <item><see cref="FieldDefinition"/> → column with type, <c>not null</c>, <c>pk</c>, inline note</item>
///   <item><see cref="ForeignKey"/> (all kinds) → <c>Ref</c> entry; logical/virtual get <c>// logical</c> / <c>// virtual</c> comment</item>
///   <item><see cref="SectionDefinition"/> → <c>TableGroup</c> block</item>
///   <item>Computed columns → inline column note: <c>// calculated: ([expr]) PERSISTED</c></item>
///   <item>Reference table data → <c>Records</c> block immediately after the <c>Table</c> block</item>
/// </list>
/// </summary>
public sealed class DbmlGenerator
{
    /// <summary>Generates a DBML string from the given IR.</summary>
    public string Generate(ManifestRoot ir)
    {
        var sb = new StringBuilder();

        // ── Tables ────────────────────────────────────────────────────────────

        for (int i = 0; i < ir.Tables.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            AppendTable(sb, ir.Tables[i]);
            AppendRecords(sb, ir.Tables[i]);
        }

        // ── Refs ──────────────────────────────────────────────────────────────

        bool anyRefs = ir.Tables.Any(t => t.ForeignKeys.Count > 0);
        if (anyRefs)
        {
            sb.AppendLine();
            foreach (var table in ir.Tables)
            {
                foreach (var fk in table.ForeignKeys)
                    AppendRef(sb, table.Name, fk);
            }
        }

        // ── TableGroups ───────────────────────────────────────────────────────

        if (ir.Sections.Count > 0)
        {
            sb.AppendLine();
            for (int i = 0; i < ir.Sections.Count; i++)
            {
                if (i > 0) sb.AppendLine();
                AppendTableGroup(sb, ir.Sections[i]);
            }
        }

        // Ensure single trailing newline.
        var result = sb.ToString().TrimEnd('\r', '\n');
        return result + "\n";
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static void AppendTable(StringBuilder sb, TableDefinition table)
    {
        sb.AppendLine($"Table {EscapeIdentifier(table.Name)} {{");

        foreach (var field in table.Fields)
            AppendField(sb, field, table.PrimaryKey);

        if (!string.IsNullOrWhiteSpace(table.Description))
        {
            sb.AppendLine();
            sb.AppendLine($"  Note: \"{EscapeString(table.Description)}\"");
        }

        sb.AppendLine("}");
    }

    private static void AppendField(StringBuilder sb, FieldDefinition field, IReadOnlyList<string> primaryKey)
    {
        var constraints = new List<string>();

        if (primaryKey.Contains(field.Name, TableNames.Comparer))
            constraints.Add("pk");

        if (!field.Nullable)
            constraints.Add("not null");

        var note = BuildFieldNote(field);
        if (!string.IsNullOrEmpty(note))
            constraints.Add($"note: \"{EscapeString(note)}\"");

        var constraintStr = constraints.Count > 0
            ? $" [{string.Join(", ", constraints)}]"
            : "";

        sb.AppendLine($"  {EscapeIdentifier(field.Name)} {field.Type}{constraintStr}");
    }

    private static string? BuildFieldNote(FieldDefinition field)
    {
        string? calcPart = null;
        if (field.IsComputed && field.ComputedExpression != null)
        {
            calcPart = field.IsPersisted
                ? $"// calculated: ({field.ComputedExpression}) PERSISTED"
                : $"// calculated: ({field.ComputedExpression})";
        }

        var desc = string.IsNullOrEmpty(field.Description) ? null : field.Description;

        if (desc != null && calcPart != null) return $"{desc}; {calcPart}";
        if (desc != null)                    return desc;
        return calcPart;
    }

    private static void AppendRef(StringBuilder sb, string sourceTable, ForeignKey fk)
    {
        var comment = fk.Kind switch
        {
            ForeignKeyKind.Logical => " // logical",
            ForeignKeyKind.Virtual => " // virtual",
            _                      => "",
        };

        sb.AppendLine(
            $"Ref: {EscapeIdentifier(sourceTable)}.{EscapeIdentifier(fk.SourceField)} > " +
            $"{EscapeIdentifier(fk.TargetTable)}.{EscapeIdentifier(fk.TargetField)}{comment}");
    }

    private static void AppendTableGroup(StringBuilder sb, SectionDefinition section)
    {
        sb.AppendLine($"TableGroup {EscapeIdentifier(section.Name)} {{");
        foreach (var tableName in section.Tables)
            sb.AppendLine($"  {EscapeIdentifier(tableName)}");
        sb.AppendLine("}");
    }

    private static void AppendRecords(StringBuilder sb, TableDefinition table)
    {
        if (!table.IsReferenceTable || table.Data.Count == 0) return;

        var columns = table.Fields.Select(f => f.Name).ToList();
        var colList = string.Join(", ", columns.Select(EscapeIdentifier));

        sb.AppendLine();
        sb.AppendLine($"Records {EscapeIdentifier(table.Name)}({colList}) {{");

        foreach (var row in table.Data)
        {
            var values = columns.Select(col =>
                row.TryGetValue(col, out var val) ? FormatValue(val) : "null");
            sb.AppendLine($"  {string.Join(", ", values)}");
        }

        sb.AppendLine("}");
    }

    private static string FormatValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => $"'{element.GetString()!.Replace("'", "''")}'",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True   => "true",
        JsonValueKind.False  => "false",
        _                    => "null",
    };

    /// <summary>
    /// Quote the identifier if it contains characters outside of alphanumeric, underscore, or dot
    /// (dots are allowed unquoted in DBML for schema-qualified names like <c>dbo.Customer</c>).
    /// </summary>
    private static string EscapeIdentifier(string name)
    {
        if (name.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '.'))
            return name;
        return $"\"{name.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    private static string EscapeString(string s)
        => s.Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "")
            .Replace("\n", " ");
}
