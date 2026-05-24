using Manifesta.Core.IR;

namespace Manifesta.Core;

public interface IDatabaseIntrospector
{
    Task<IReadOnlyList<TableDefinition>> IntrospectAsync(string? schemaFilter = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TableDefinition>> IntrospectTablesOnlyAsync(string? schemaFilter = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TableDefinition>> IntrospectViewsOnlyAsync(string? schemaFilter = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, int>> GetRowCountsAsync(string? schemaFilter = null, CancellationToken cancellationToken = default);
}
