using Manifesta.Core.IR;

namespace Manifesta.Core;

public sealed record PrismaParseResult(
    IReadOnlyList<TableDefinition> Tables,
    IReadOnlyList<TableDefinition> Enums,
    IReadOnlyList<string>          Errors);
