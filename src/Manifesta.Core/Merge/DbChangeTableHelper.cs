using System.Text;

namespace Manifesta.Core.Merge;

/// <summary>
/// Renders the shared "Change / Field / Before / After" Markdown table used by
/// both <see cref="Drift.DriftReportGenerator"/> and <see cref="MergeReportGenerator"/>.
/// The <c>dbSource</c> flag selects the label variant:
/// <c>true</c> = drift labels ("Column removed from DB", "FK added to DB");
/// <c>false</c> = merge labels ("Column removed", "FK added").
/// </summary>
internal static class DbChangeTableHelper
{
    internal static void Append(
        StringBuilder              sb,
        PrimaryKeyChange?          pkc,
        IReadOnlyList<FieldChange> fieldChanges,
        IReadOnlyList<FkChange>    fkChanges,
        bool                       dbSource)
    {
        sb.AppendLine("| Change | Field / Key | Before | After |");
        sb.AppendLine("|--------|-------------|--------|-------|");

        if (pkc is not null)
        {
            var before = string.Join(", ", pkc.Before);
            var after  = string.Join(", ", pkc.After);
            sb.AppendLine($"| Primary key changed | — | `{before}` | `{after}` |");
        }

        foreach (var fc in fieldChanges)
        {
            var (change, before, after) = fc.Kind switch
            {
                FieldChangeKind.Added                     => ("Column added",                                                    "—",                       $"`{fc.NewValue}`"),
                FieldChangeKind.Removed                   => (dbSource ? "Column removed from DB" : "Column removed",           $"`{fc.OldValue}`",        "—"),
                FieldChangeKind.TypeChanged               => ("Column type changed",                                             $"`{fc.OldValue}`",        $"`{fc.NewValue}`"),
                FieldChangeKind.NullabilityChanged        => ("Column nullability changed",                                      $"`{fc.OldValue}`",        $"`{fc.NewValue}`"),
                FieldChangeKind.DefaultChanged            => ("Column default changed",                                          $"`{fc.OldValue ?? "—"}`", $"`{fc.NewValue ?? "—"}`"),
                FieldChangeKind.ComputedExpressionChanged => ("Computed expression changed",                                     $"`{fc.OldValue ?? "—"}`", $"`{fc.NewValue ?? "—"}`"),
                FieldChangeKind.IsPersistedChanged        => ("Computed persisted flag changed",                                 $"`{fc.OldValue}`",        $"`{fc.NewValue}`"),
                _                                         => ("Changed",                                                         fc.OldValue ?? "—",        fc.NewValue ?? "—"),
            };
            sb.AppendLine($"| {change} | `{fc.FieldName}` | {before} | {after} |");
        }

        foreach (var fk in fkChanges)
        {
            var fkDesc = $"`{fk.SourceField}` → `{fk.TargetTable}.{fk.TargetField}`";
            var (desc, before, after) = fk.Kind switch
            {
                FkChangeKind.Added                => (dbSource ? "FK added to DB"    : "FK added",    "—",                fkDesc),
                FkChangeKind.Removed              => (dbSource ? "FK removed from DB": "FK removed",  fkDesc,             "—"),
                FkChangeKind.CascadeDeleteChanged => ("FK cascadeDelete changed",                      $"`{fk.OldValue}`", $"`{fk.NewValue}`"),
                _                                 => ("FK changed",                                    fk.OldValue ?? "—", fk.NewValue ?? "—"),
            };
            sb.AppendLine($"| {desc} | {fkDesc} | {before} | {after} |");
        }

        sb.AppendLine();
    }
}
