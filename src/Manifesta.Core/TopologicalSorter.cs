using Manifesta.Core.IR;

namespace Manifesta.Core;

/// <summary>
/// Topological ordering of <see cref="TableDefinition"/> nodes using Kahn's algorithm.
/// </summary>
/// <remarks>
/// Used by the manifest generator (dependency-ordered <c>syncra.json</c>),
/// and available to <c>db graph</c>, <c>db validate</c>, and the build sequencer
/// in future milestones.
/// </remarks>
public static class TopologicalSorter
{
    /// <summary>
    /// Returns <paramref name="tables"/> sorted so that every table appears after
    /// all tables it references via foreign keys.
    /// </summary>
    /// <remarks>
    /// Only FKs where both the source and target exist in the supplied set are
    /// considered as ordering constraints. Self-references are ignored.
    /// </remarks>
    /// <exception cref="ManifestaSchemException">
    /// Thrown when a circular FK dependency is detected.
    /// </exception>
    public static IReadOnlyList<TableDefinition> Sort(IReadOnlyList<TableDefinition> tables)
    {
        // All three dictionaries use OrdinalIgnoreCase so FK target lookups are consistent
        // with the rest of the codebase (table names like "dbo.User" vs "dbo.user" compare equal).
        var tableDict = tables.ToDictionary(t => t.Name, TableNames.Comparer);
        var inDegree  = new Dictionary<string, int>(TableNames.Comparer);
        var adjacency = new Dictionary<string, List<string>>(TableNames.Comparer);

        foreach (var table in tables)
        {
            inDegree[table.Name]  = 0;
            adjacency[table.Name] = [];
        }

        foreach (var table in tables)
        {
            foreach (var fk in table.ForeignKeys)
            {
                if (fk.Kind == ForeignKeyKind.Virtual)      continue;  // virtual FKs are doc-only; logical FKs drive ordering
                if (!tableDict.ContainsKey(fk.TargetTable)) continue;  // target not in schema
                if (fk.TargetTable == table.Name)           continue;  // self-reference

                // Edge: target → source (target must appear before source)
                adjacency[fk.TargetTable].Add(table.Name);
                inDegree[table.Name]++;
            }
        }

        var queue  = new Queue<string>(tables.Where(t => inDegree[t.Name] == 0).Select(t => t.Name));
        var sorted = new List<TableDefinition>(tables.Count);

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            sorted.Add(tableDict[name]);

            foreach (var dependent in adjacency[name])
            {
                if (--inDegree[dependent] == 0)
                    queue.Enqueue(dependent);
            }
        }

        if (sorted.Count != tables.Count)
            throw new ManifestaSchemException(
                "Circular FK dependency detected. " +
                "Check for tables that depend on each other directly or indirectly.");

        return sorted.AsReadOnly();
    }
}
