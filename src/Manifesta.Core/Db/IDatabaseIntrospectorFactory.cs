using Manifesta.Core.IR;

namespace Manifesta.Core;

public interface IDatabaseIntrospectorFactory
{
    IDatabaseIntrospector Create(DbProvider provider, string connectionString);
    IReferenceDataCapturer CreateCapturer(DbProvider provider, string connectionString);
    Task<IReadOnlyList<TableDefinition>> EnrichWithReferenceDataAsync(
        IReadOnlyList<TableDefinition> tables,
        IReferenceDataCapturer capturer,
        string? schemaFilter,
        ReferenceTableConfig config,
        IDatabaseIntrospector introspector,
        GlobalOptions globals,
        CancellationToken ct);
}
