using System.Text.Json;
using Manifesta.Core.IR;

namespace Manifesta.Core.Tests;

/// <summary>
/// Shared factory helpers for <see cref="TableDifferTests"/> and <see cref="TableMergerTests"/>.
/// Import via <c>using static Manifesta.Core.Tests.TableTestHelpers;</c>.
/// </summary>
internal static class TableTestHelpers
{
    internal static TableDefinition Table(
        string name = "dbo.Order",
        IEnumerable<FieldDefinition>? fields = null,
        IEnumerable<string>? pk = null,
        IEnumerable<ForeignKey>? fks = null,
        string description = "",
        IEnumerable<string>? sections = null,
        IEnumerable<IReadOnlyDictionary<string, JsonElement>>? data = null) => new()
    {
        Name        = name,
        Description = description,
        Fields      = (fields   ?? []).ToList().AsReadOnly(),
        PrimaryKey  = (pk       ?? []).ToList().AsReadOnly(),
        ForeignKeys = (fks      ?? []).ToList().AsReadOnly(),
        Sections    = (sections ?? []).ToList().AsReadOnly(),
        Data        = (data     ?? []).ToList().AsReadOnly(),
    };

    internal static FieldDefinition Field(
        string name,
        string type        = "int",
        bool nullable      = false,
        string desc        = "",
        bool isMatchCol    = false,
        bool isPk          = false,
        string? defaultVal = null) => new()
    {
        Name          = name,
        Type          = type,
        Nullable      = nullable,
        Description   = desc,
        IsMatchColumn = isMatchCol,
        IsPrimaryKey  = isPk,
        Default       = defaultVal,
    };

    internal static ForeignKey Fk(
        string src,
        string targetTable,
        string targetField,
        bool cascade         = false,
        bool soft            = false,
        ForeignKeyKind? kind = null) => new()
    {
        SourceField   = src,
        TargetTable   = targetTable,
        TargetField   = targetField,
        CascadeDelete = cascade,
        Kind          = kind ?? (soft ? ForeignKeyKind.Logical : ForeignKeyKind.Physical),
    };

    internal static IReadOnlyDictionary<string, JsonElement> DataRow(
        params (string Key, object? Value)[] cols)
    {
        var d = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (k, v) in cols)
            d[k] = v is null
                ? JsonSerializer.SerializeToElement<object?>(null)
                : JsonSerializer.SerializeToElement(v);
        return d;
    }

    internal static FieldDefinition ComputedField(
        string name,
        string type        = "nvarchar(101)",
        string? expression = "([A]+' '+[B])",
        bool isPersisted   = false) => new()
    {
        Name               = name,
        Type               = type,
        Nullable           = true,
        IsComputed         = true,
        ComputedExpression = expression,
        IsPersisted        = isPersisted,
    };
}
