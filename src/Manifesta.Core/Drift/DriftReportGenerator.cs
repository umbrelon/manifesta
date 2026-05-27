using System.Text;
using Manifesta.Core.IR;
using Manifesta.Core.Merge;
using Manifesta.Core.Pipeline;

namespace Manifesta.Core.Drift;

/// <summary>
/// Generates a Markdown drift report from a <see cref="DriftSession"/>.
/// Implements <see cref="IGenerator{TInput,TOutput}"/> for composability.
/// </summary>
public sealed class DriftReportGenerator : IGenerator<DriftSession, string>
{
    public string Generate(DriftSession session) =>
        Generate(session, sourceLabel: "Repository", targetLabel: "Source");

    public string Generate(
        DriftSession session,
        string sourceLabel,
        string targetLabel)
    {
        var sb = new StringBuilder();

        // ── Header ──────────────────────────────────────────────────────────────
        sb.AppendLine("# Manifesta DB Drift Report");
        sb.AppendLine();
        sb.AppendLine($"Generated: {session.Timestamp:O}");
        sb.AppendLine($"Source: {session.Source}");
        sb.AppendLine($"Repository: {session.RootPath}");
        sb.AppendLine();

        // ── Summary ──────────────────────────────────────────────────────────────
        var statusLabel = session.HasDrift    ? "❌ Drift detected"
                        : session.HasWarnings ? "⚠ Warnings only"
                        :                       "✅ In sync";

        var totalFkChanges    = session.DriftedTables.Sum(t => t.FkChanges.Count);
        var totalIndexChanges = session.DriftedTables.Sum(t => t.IndexChanges.Count);

        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"**Status: {statusLabel}**");
        sb.AppendLine();
        sb.AppendLine("| Category | Count |");
        sb.AppendLine("|----------|-------|");
        sb.AppendLine($"| Tables scanned (live) | {session.TotalLiveTables} |");
        sb.AppendLine($"| Tables in sync | {session.CleanTables.Count} |");
        sb.AppendLine($"| Tables with drift | {session.DriftedTables.Count} |");
        sb.AppendLine($"| Tables absent from DB | {session.MissingDbTables.Count} |");
        sb.AppendLine($"| Tables absent from repo (⚠) | {session.ExtraDbTables.Count} |");
        sb.AppendLine($"| FK changes | {totalFkChanges} |");
        sb.AppendLine($"| Index changes | {totalIndexChanges} |");
        sb.AppendLine();

        // ── Drifted tables ───────────────────────────────────────────────────────
        if (session.DriftedTables.Count > 0)
        {
            sb.AppendLine("## Drifted Tables");
            sb.AppendLine();
            foreach (var result in session.DriftedTables)
                AppendDriftedTable(sb, result, session.IncludeSchema, session.IncludeFkDrifts, session.IncludeIndexDrifts, sourceLabel, targetLabel);
        }

        // ── Tables absent from target ────────────────────────────────────────────
        if (session.MissingDbTables.Count > 0)
        {
            sb.AppendLine($"## Tables Absent from {targetLabel}");
            sb.AppendLine();
            sb.AppendLine($"These tables are defined in {sourceLabel} but were **not found in {targetLabel}**.");
            if (sourceLabel == "Repository")
                sb.AppendLine("Run `manifesta db merge` to reconcile, or remove the files manually.");
            sb.AppendLine();
            foreach (var path in session.MissingDbTables)
                sb.AppendLine($"- `{path}`");
            sb.AppendLine();
        }

        // ── Warnings: tables absent from source ──────────────────────────────────
        if (session.ExtraDbTables.Count > 0)
        {
            sb.AppendLine($"## Warnings: Tables Absent from {sourceLabel}");
            sb.AppendLine();
            sb.AppendLine($"These tables exist in {targetLabel} but have **no {sourceLabel} definition**.");
            if (sourceLabel == "Repository")
                sb.AppendLine("Run `manifesta db merge` to create repo files for them.");
            sb.AppendLine();
            foreach (var name in session.ExtraDbTables)
                sb.AppendLine($"- `{name}`");
            sb.AppendLine();
        }

        // ── Clean tables (reference) ─────────────────────────────────────────────
        if (session.IncludeCleanTables && session.CleanTables.Count > 0)
        {
            sb.AppendLine("## Clean Tables");
            sb.AppendLine();
            sb.AppendLine($"The following {session.CleanTables.Count} table(s) are fully in sync.");
            sb.AppendLine();
            foreach (var result in session.CleanTables)
                sb.AppendLine($"- {result.TableName} (`{result.RepoFilePath}`)");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ── Per-table rendering ───────────────────────────────────────────────────

    private static void AppendDriftedTable(
        StringBuilder sb,
        DriftResult   result,
        bool          includeSchema,
        bool          includeFkDrifts,
        bool          includeIndexDrifts,
        string        sourceLabel,
        string        targetLabel)
    {
        sb.AppendLine($"### {result.TableName}");
        sb.AppendLine();
        sb.AppendLine($"**File:** `{result.RepoFilePath}`");
        sb.AppendLine();

        var effectiveFks = includeFkDrifts ? result.FkChanges : (IReadOnlyList<FkChange>)[];
        var hasStructuralChanges = result.FieldChanges.Count > 0 || result.PrimaryKeyChange is not null || effectiveFks.Count > 0;
        if (hasStructuralChanges)
            DbChangeTableHelper.Append(sb, result.PrimaryKeyChange, result.FieldChanges, effectiveFks, dbSource: true);

        if (result.HasWarnings)
        {
            sb.AppendLine("**Extra columns in DB (not in repo):**");
            sb.AppendLine();
            foreach (var col in result.ExtraDbColumns)
                sb.AppendLine($"- `{col}` *(run `db merge` to add to repo)*");
            sb.AppendLine();
        }

        if (result.DataChanges.Count > 0)
        {
            sb.AppendLine($"**Data drift ({result.DataChanges.Count} change(s)):**");
            sb.AppendLine();
            sb.AppendLine("| Change | PK | Details |");
            sb.AppendLine("|--------|----|---------|");
            foreach (var dc in result.DataChanges)
            {
                var pk = FormatPk(dc.PkValues);
                var (label, details) = dc.Kind switch
                {
                    DataChangeKind.Added    => ("Row added",    "—"),
                    DataChangeKind.Removed  => ("Row removed",  "—"),
                    DataChangeKind.Modified => ("Row modified",
                        dc.ChangedFields.Count > 0
                            ? $"Fields: {string.Join(", ", dc.ChangedFields)}"
                            : "—"),
                    _ => ("Changed", "—"),
                };
                sb.AppendLine($"| {label} | `{pk}` | {details} |");
            }
            sb.AppendLine();
        }

        if (includeIndexDrifts && result.IndexChanges.Count > 0)
        {
            sb.AppendLine($"**Index drift ({result.IndexChanges.Count} change(s)):**");
            sb.AppendLine();
            sb.AppendLine("| Change | Index | Details |");
            sb.AppendLine("|--------|-------|---------|");
            foreach (var ic in result.IndexChanges)
            {
                var (label, details) = ic.Kind switch
                {
                    IndexChangeKind.Added            => ("Index added",           $"Columns: {ic.NewColumns}"),
                    IndexChangeKind.Removed          => ("Index removed",         $"Columns: {ic.OldColumns}"),
                    IndexChangeKind.ColumnsChanged   => ("Columns changed",       $"{ic.OldColumns} → {ic.NewColumns}"),
                    IndexChangeKind.UniquenessChanged => ("Uniqueness changed",   $"unique: {ic.OldIsUnique} → {ic.NewIsUnique}"),
                    _                                => ("Changed",               "—"),
                };
                sb.AppendLine($"| {label} | `{ic.IndexName}` | {details} |");
            }
            sb.AppendLine();
        }

        if (includeSchema)
        {
            AppendSchemaTable(sb, $"{sourceLabel} definition", result.RepoTable.Fields);
            AppendSchemaTable(sb, $"{targetLabel} definition", result.LiveTable.Fields);
        }
    }

    private static string FormatPk(IReadOnlyDictionary<string, System.Text.Json.JsonElement> pk) =>
        string.Join(", ", pk.Select(kv => $"{kv.Key}={kv.Value.GetRawText()}"));

    private static void AppendSchemaTable(StringBuilder sb, string heading, IReadOnlyList<FieldDefinition> fields)
    {
        sb.AppendLine($"**{heading}:**");
        sb.AppendLine();
        var hasComputed = fields.Any(f => f.IsComputed);
        if (hasComputed)
        {
            sb.AppendLine("| Column | Type | Nullable | Default | Expression |");
            sb.AppendLine("|--------|------|----------|---------|------------|");
            foreach (var f in fields)
                sb.AppendLine($"| `{f.Name}` | `{f.Type}` | {f.Nullable.ToString().ToLowerInvariant()} | `{f.Default ?? "—"}` | {(f.IsComputed ? $"`{f.ComputedExpression}`" : "—")} |");
        }
        else
        {
            sb.AppendLine("| Column | Type | Nullable | Default |");
            sb.AppendLine("|--------|------|----------|---------|");
            foreach (var f in fields)
                sb.AppendLine($"| `{f.Name}` | `{f.Type}` | {f.Nullable.ToString().ToLowerInvariant()} | `{f.Default ?? "—"}` |");
        }
        sb.AppendLine();
    }
}
