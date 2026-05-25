using System.Text;
using Manifesta.Core.IR;
using Manifesta.Core.Pipeline;

namespace Manifesta.Core.Merge;

/// <summary>
/// Generates a Markdown merge report from a <see cref="MergeSession"/>.
/// Implements <see cref="IGenerator{TInput,TOutput}"/> for composability.
/// </summary>
public sealed class MergeReportGenerator : IGenerator<MergeSession, string>
{
    public string Generate(MergeSession session)
    {
        var sb = new StringBuilder();

        // ── Header ─────────────────────────────────────────────────────────────
        sb.AppendLine("# Manifesta DB Merge Report");
        sb.AppendLine();
        sb.AppendLine($"Generated: {session.Timestamp:O}");
        sb.AppendLine($"Source: {session.Source}");
        sb.AppendLine($"Root: {session.RootPath}");
        sb.AppendLine($"Dry run: {session.IsDryRun.ToString().ToLowerInvariant()}");
        sb.AppendLine();

        // ── Summary ────────────────────────────────────────────────────────────
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("| Category | Count |");
        sb.AppendLine("|----------|-------|");
        sb.AppendLine($"| Tables scanned (live) | {session.TotalLiveTables} |");
        sb.AppendLine($"| Repo files matched | {session.Modified.Count + session.Unchanged.Count} |");
        sb.AppendLine($"| Files modified | {session.Modified.Count} |");
        sb.AppendLine($"| Files created (new tables) | {session.NewTables.Count} |");
        sb.AppendLine($"| Files unchanged | {session.Unchanged.Count} |");
        sb.AppendLine($"| Files deleted | {session.DeletedTablePaths.Count} |");
        sb.AppendLine($"| Reference tables data-refreshed | {session.RefreshedDataCount} |");
        sb.AppendLine($"| Orphan columns (warnings) | {session.OrphanColumns.Count} |");
        sb.AppendLine($"| Orphan tables (warnings) | {session.OrphanTablePaths.Count} |");
        sb.AppendLine($"| Orphaned data keys (warnings) | {session.OrphanedDataKeys.Count} |");
        sb.AppendLine();

        // ── Modified tables ────────────────────────────────────────────────────
        if (session.Modified.Count > 0)
        {
            sb.AppendLine("## Modified Tables");
            sb.AppendLine();
            foreach (var result in session.Modified)
                AppendModifiedTable(sb, result);
        }

        // ── New tables ─────────────────────────────────────────────────────────
        if (session.NewTables.Count > 0)
        {
            sb.AppendLine("## New Tables");
            sb.AppendLine();
            foreach (var nt in session.NewTables)
            {
                sb.AppendLine($"### {nt.Table.Name}");
                sb.AppendLine();
                sb.AppendLine($"**File:** `{nt.FilePath}` *(created)*");
                sb.AppendLine();
                sb.AppendLine("> ⚠ No existing repo file found. Add descriptions and section membership before committing.");
                sb.AppendLine();
            }
        }

        // ── Deleted tables ─────────────────────────────────────────────────────
        if (session.DeletedTablePaths.Count > 0)
        {
            sb.AppendLine("## Deleted Tables");
            sb.AppendLine();
            sb.AppendLine("The following repo files were deleted because their tables no longer exist in the database.");
            sb.AppendLine();
            foreach (var path in session.DeletedTablePaths)
                sb.AppendLine($"- `{path}`");
            sb.AppendLine();
        }

        // ── Warnings ───────────────────────────────────────────────────────────
        bool hasWarnings = session.OrphanColumns.Count    > 0 ||
                           session.OrphanTablePaths.Count > 0 ||
                           session.OrphanedDataKeys.Count > 0;

        if (hasWarnings)
        {
            sb.AppendLine("## Warnings");
            sb.AppendLine();

            if (session.OrphanColumns.Count > 0)
            {
                sb.AppendLine("### Orphan Columns");
                sb.AppendLine();
                sb.AppendLine("These columns exist in repo files but were **not found in the database**.");
                sb.AppendLine("They have been preserved. Re-run with `--remove-deleted-columns` to remove them.");
                sb.AppendLine();
                sb.AppendLine("| Table | Column | Repo file |");
                sb.AppendLine("|-------|--------|-----------|");
                foreach (var oc in session.OrphanColumns)
                    sb.AppendLine($"| {oc.TableName} | `{oc.FieldName}` | `{oc.FilePath}` |");
                sb.AppendLine();
            }

            if (session.OrphanTablePaths.Count > 0)
            {
                sb.AppendLine("### Orphan Tables");
                sb.AppendLine();
                sb.AppendLine("These repo files have table names that were **not found in the database**.");
                sb.AppendLine("They have been left untouched. Re-run with `--remove-deleted-tables` to delete them.");
                sb.AppendLine();
                foreach (var path in session.OrphanTablePaths)
                    sb.AppendLine($"- `{path}`");
                sb.AppendLine();
            }

            if (session.OrphanedDataKeys.Count > 0)
            {
                sb.AppendLine("### Orphaned Data Keys");
                sb.AppendLine();
                sb.AppendLine("These column names appear in the `data` section of a reference table but **no longer exist in the schema**.");
                sb.AppendLine("The data rows have been preserved unchanged. Remove the stale keys manually, or re-run with `--force-capture-reference-data` (via `db merge` or `db export`) to replace the data.");
                sb.AppendLine();
                sb.AppendLine("| Table | Stale column key | Repo file |");
                sb.AppendLine("|-------|-----------------|-----------|");
                foreach (var dk in session.OrphanedDataKeys)
                    sb.AppendLine($"| {dk.TableName} | `{dk.ColumnName}` | `{dk.FilePath}` |");
                sb.AppendLine();
            }

        }

        // ── Unchanged tables (verbose reference) ───────────────────────────────
        if (session.Unchanged.Count > 0)
        {
            sb.AppendLine("## Unchanged Tables");
            sb.AppendLine();
            sb.AppendLine($"The following {session.Unchanged.Count} table(s) required no changes.");
            sb.AppendLine();
            foreach (var result in session.Unchanged)
                sb.AppendLine($"- {result.Merged.Name} (`{result.RepoFilePath}`)");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void AppendModifiedTable(StringBuilder sb, MergeResult result)
    {
        sb.AppendLine($"### {result.Merged.Name}");
        sb.AppendLine();
        sb.AppendLine($"**File:** `{result.RepoFilePath}`");
        sb.AppendLine();

        bool hasChanges = result.FieldChanges.Count > 0 ||
                          result.FkChanges.Count    > 0 ||
                          result.PrimaryKeyChange   is not null;

        if (result.DataRefreshed)
        {
            sb.AppendLine($"> Reference data refreshed: {result.Merged.Data.Count} row(s) captured.");
            sb.AppendLine();
        }

        if (hasChanges)
            DbChangeTableHelper.Append(sb, result.PrimaryKeyChange, result.FieldChanges, result.FkChanges, dbSource: false);

    }
}
