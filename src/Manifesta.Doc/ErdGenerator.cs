using System.Text;
using Manifesta.Core;
using Manifesta.Core.IR;

namespace Manifesta.Doc;

/// <summary>
/// Generates a Mermaid <c>erDiagram</c> block from an <see cref="ErdDefinition"/>
/// and the set of resolved <see cref="TableDefinition"/> objects.
///
/// Design rules:
/// <list type="bullet">
///   <item>Only FK relationships where both source and target are in the ERD's
///         table list are rendered (intra-ERD only).</item>
///   <item>Tables in <see cref="ErdDefinition.Tables"/> that are not in the owning
///         section are silently skipped (validation is the caller's concern).</item>
///   <item>Logical FKs are included by default; opt-out via <see cref="ErdDefinition.IncludeLogical"/> = false.</item>
///   <item>Virtual FKs are excluded by default; opt-in via <see cref="ErdDefinition.IncludeVirtual"/> = true.</item>
/// </list>
/// </summary>
public sealed class ErdGenerator
{
    /// <summary>
    /// Generate a Mermaid ERD block.
    /// </summary>
    /// <param name="erd">ERD configuration from the section definition.</param>
    /// <param name="sectionTables">All table names belonging to the section — used
    ///   when <see cref="ErdDefinition.Tables"/> is empty, and as the scope boundary.</param>
    /// <param name="tablesByName">Lookup of all resolved table definitions.</param>
    /// <param name="dialect">Markdown dialect for the fenced code block.</param>
    /// <param name="tableSectionMap">Optional map of table name → section display title for all
    ///   tables across all sections. When provided, physical FKs that cross section boundaries
    ///   are rendered as labelled external stub entities (e.g. <c>"[Core] dbo.Operator"</c>)
    ///   rather than being silently omitted. Pass <c>null</c> to restore the original
    ///   intra-section-only behaviour.</param>
    public string Generate(
        ErdDefinition erd,
        IReadOnlyList<string> sectionTables,
        IReadOnlyDictionary<string, TableDefinition> tablesByName,
        MarkdownDialect dialect = MarkdownDialect.CommonMark,
        IReadOnlyDictionary<string, string>? tableSectionMap = null)
    {
        // Resolve which tables to include, scoped to the section.
        var sectionTableSet = new HashSet<string>(sectionTables, TableNames.Comparer);

        var tablesToInclude = (erd.Tables.Count > 0 ? erd.Tables : sectionTables)
            .Where(t => sectionTableSet.Contains(t) && tablesByName.ContainsKey(t))
            .ToList();

        var erdTableSet = new HashSet<string>(tablesToInclude, TableNames.Comparer);

        var sb = new StringBuilder();

        // Optional title above the diagram.
        if (!string.IsNullOrWhiteSpace(erd.Title))
        {
            sb.AppendLine($"**{erd.Title}**");
            sb.AppendLine();
        }

        if (dialect == MarkdownDialect.AzureDevOps)
            sb.AppendLine(":::mermaid");
        else
            sb.AppendLine("```mermaid");
        sb.AppendLine("erDiagram");
        sb.AppendLine("direction LR");

        // ── Pre-compute incoming target fields ───────────────────────────────
        // For each table in the diagram scope, collect the TargetField values of every
        // FK that points to it and will be rendered. These fields must appear in the
        // parent entity block even when they are not PKs or source-FK columns.
        var incomingTargetFields = new Dictionary<string, HashSet<string>>(TableNames.Comparer);

        foreach (var tableName in tablesToInclude)
        {
            if (!tablesByName.TryGetValue(tableName, out var tbl)) continue;
            foreach (var fk in tbl.ForeignKeys)
            {
                var includeLogical = erd.IncludeLogical ?? true;
                if (fk.Kind == ForeignKeyKind.Logical && !includeLogical) continue;
                if (fk.Kind == ForeignKeyKind.Virtual && !erd.IncludeVirtual) continue;
                if (!erdTableSet.Contains(fk.TargetTable)) continue;

                if (!incomingTargetFields.TryGetValue(fk.TargetTable, out var set))
                    incomingTargetFields[fk.TargetTable] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(fk.TargetField);
            }
        }

        // ── Relationships ────────────────────────────────────────────────────
        // Track which tables appear in at least one rendered relationship so
        // we know which isolated tables need an explicit entity declaration.
        var tablesInRelationships = TableNames.NewSet();

        // Track external stub names rendered so we can emit a summary comment.
        var crossSectionStubs = new HashSet<string>();

        foreach (var tableName in tablesToInclude)
        {
            if (!tablesByName.TryGetValue(tableName, out var table)) continue;

            foreach (var fk in table.ForeignKeys)
            {
                var includeLogical = erd.IncludeLogical ?? true;
                if (fk.Kind == ForeignKeyKind.Logical && !includeLogical) continue;
                if (fk.Kind == ForeignKeyKind.Virtual && !erd.IncludeVirtual) continue;

                var child = MermaidName(tableName);
                var label = RelationshipLabel(fk.SourceField, fk.TargetField);

                if (erdTableSet.Contains(fk.TargetTable))
                {
                    // Intra-section: standard rendering.
                    var parent = MermaidName(fk.TargetTable);
                    sb.AppendLine($"    {parent} ||--o{{ {child} : \"{label}\"");
                    tablesInRelationships.Add(tableName);
                    tablesInRelationships.Add(fk.TargetTable);
                }
                else if (tableSectionMap is not null
                    && fk.Kind == ForeignKeyKind.Physical
                    && tablesByName.ContainsKey(fk.TargetTable))
                {
                    // Cross-section physical FK: render as a labelled external stub.
                    // Logical/Virtual cross-section FKs are intentionally omitted —
                    // logical implies a future physical relationship (same module),
                    // virtual implies a cross-database link (out of scope for ERDs).
                    var sectionLabel = tableSectionMap.TryGetValue(fk.TargetTable, out var s) ? s : "?";
                    var stubName     = ExternalStubName(sectionLabel, fk.TargetTable);
                    sb.AppendLine($"    {stubName} ||--o{{ {child} : \"{label}\"");
                    tablesInRelationships.Add(tableName);
                    crossSectionStubs.Add(stubName);
                }
                // else: target not in ERD scope and no cross-section map — silently omit.
            }
        }

        // Emit a comment listing external stubs so readers know they are intentional.
        if (crossSectionStubs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"    %% External references (cross-section): {string.Join(", ", crossSectionStubs.OrderBy(s => s))}");
        }

        // ── Entity definitions ───────────────────────────────────────────────
        var fieldsMode = string.IsNullOrWhiteSpace(erd.Fields) ? ErdFields.PkAndFk : erd.Fields;

        foreach (var tableName in tablesToInclude)
        {
            var mermaidName = MermaidName(tableName);

            if (fieldsMode == ErdFields.None)
            {
                // Entities referenced in relationship lines are implicit in Mermaid;
                // only emit a standalone declaration for isolated tables.
                if (!tablesInRelationships.Contains(tableName))
                    sb.AppendLine($"    {mermaidName}");
                continue;
            }

            // Build the field list for this entity.
            if (!tablesByName.TryGetValue(tableName, out var table)) continue;

            var pkSet = new HashSet<string>(table.PrimaryKey, TableNames.Comparer);
            var fkSet = new HashSet<string>(
                table.ForeignKeys.Select(f => f.SourceField),
                StringComparer.OrdinalIgnoreCase);
            var refSet = incomingTargetFields.TryGetValue(tableName, out var incoming)
                ? incoming
                : (IReadOnlyCollection<string>)[];

            var fieldsToRender = table.Fields
                .Where(f => fieldsMode == ErdFields.All
                            || pkSet.Contains(f.Name)
                            || fkSet.Contains(f.Name)
                            || refSet.Contains(f.Name))
                .ToList();

            if (table.IsDeprecated)
                sb.AppendLine($"    %% DEPRECATED: {mermaidName}");

            sb.AppendLine($"    {mermaidName} {{");

            foreach (var field in fieldsToRender)
            {
                var annotation = pkSet.Contains(field.Name) ? " PK"
                               : fkSet.Contains(field.Name) ? " FK"
                               : "";
                if (field.IsComputed)
                    annotation = annotation.Length > 0 ? $"{annotation},C" : " C";
                if (field.IsDeprecated)
                    annotation = annotation.Length > 0 ? $"{annotation},DEPR" : " DEPR";
                var type = MermaidType(field.Type);
                sb.AppendLine($"        {type} {field.Name}{annotation}");
            }

            sb.AppendLine("    }");
        }

        if (dialect == MarkdownDialect.AzureDevOps)
            sb.Append(":::");
        else
            sb.Append("```");

        // Normalise to LF-only so output is consistent across OSes
        // (Mermaid is a web format; CRLF inside a fenced block confuses some renderers).
        return sb.ToString().Replace("\r\n", "\n");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a table name to a quoted Mermaid entity identifier.
    /// Preserves <c>.</c> (schema separator) and <c>-</c>; replaces spaces with underscores.
    /// Double quotes are required because Mermaid does not allow <c>.</c> in bare identifiers.
    /// </summary>
    public static string MermaidName(string tableName) =>
        $"\"{tableName.Replace(" ", "_")}\"";

    /// <summary>
    /// Builds the Mermaid entity name for a cross-section external stub.
    /// Format: <c>"[SectionName] schema.TableName"</c>.
    /// The bracket prefix visually distinguishes external references from
    /// intra-section entities in the rendered diagram.
    /// </summary>
    public static string ExternalStubName(string sectionName, string tableName) =>
        $"\"[{sectionName}] {tableName.Replace(" ", "_")}\"";

    /// <summary>
    /// Strips length/precision from a SQL type for use in Mermaid field declarations.
    /// e.g. <c>varchar(255)</c> → <c>varchar</c>, <c>int</c> → <c>int</c>.
    /// </summary>
    public static string MermaidType(string sqlType) =>
        sqlType.Split('(')[0].ToLowerInvariant();

    /// <summary>
    /// Returns the relationship label for a FK.
    /// When source and target field names are the same (case-insensitive) only the
    /// source name is emitted; when they differ both sides are shown as
    /// <c>sourceField -&gt; targetField</c> so the column mapping is unambiguous.
    /// </summary>
    public static string RelationshipLabel(string sourceField, string targetField) =>
        string.Equals(sourceField, targetField, StringComparison.OrdinalIgnoreCase)
            ? sourceField
            : $"{sourceField} -> {targetField}";
}
