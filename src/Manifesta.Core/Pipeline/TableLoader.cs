using Manifesta.Core.IR;

namespace Manifesta.Core.Pipeline;

public sealed class TableLoader : JsonFileLoader<TableDefinition>
{
    protected override TableDefinition SetSourceFile(TableDefinition item, string filePath)
        => item with { SourceFile = filePath };
}
