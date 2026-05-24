using System.Text.Json;
using System.Text.Json.Serialization;

namespace Manifesta.Core.IR;

/// <summary>
/// Custom JSON converter for <see cref="ForeignKey"/> that provides backward
/// compatibility with the deprecated <c>"soft"</c> property.
/// </summary>
/// <remarks>
/// Migration rule:
/// <list type="bullet">
///   <item><c>"soft": true</c>  → <see cref="ForeignKeyKind.Logical"/>  (deprecated, still accepted)</item>
///   <item><c>"soft": false</c> → <see cref="ForeignKeyKind.Physical"/> (deprecated, still accepted)</item>
///   <item><c>"kind": "…"</c>   → canonical new form, takes precedence</item>
///   <item>Both <c>"soft"</c> and <c>"kind"</c> present → <see cref="JsonException"/> (authoring error)</item>
/// </list>
/// Files are written forward-only using <c>"kind"</c> by <c>TableDefinitionSerializer</c>.
/// Files that go through a db-merge cycle will automatically stop carrying <c>"soft"</c>.
/// </remarks>
public sealed class ForeignKeyJsonConverter : JsonConverter<ForeignKey>
{
    public override ForeignKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc  = JsonDocument.ParseValue(ref reader);
        var       root = doc.RootElement;

        var hasSoft = TryGetProp(root, "soft",          out var softElem);
        var hasKind = TryGetProp(root, "kind",          out var kindElem);

        if (hasSoft && hasKind)
            throw new JsonException(
                "A foreign key cannot specify both 'soft' (deprecated) and 'kind'. " +
                "Remove 'soft' and use 'kind' only.");

        var sourceField = RequireString(root, "sourceField");
        var targetTable = RequireString(root, "targetTable");
        var targetField = RequireString(root, "targetField");

        var cascadeDelete = TryGetProp(root, "cascadeDelete", out var cdElem) && cdElem.GetBoolean();

        ForeignKeyKind kind;
        if (hasSoft)
        {
            kind = softElem.GetBoolean() ? ForeignKeyKind.Logical : ForeignKeyKind.Physical;
        }
        else if (hasKind)
        {
            var kindStr = kindElem.GetString();
            kind = kindStr?.ToLowerInvariant() switch
            {
                "physical" => ForeignKeyKind.Physical,
                "logical"  => ForeignKeyKind.Logical,
                "virtual"  => ForeignKeyKind.Virtual,
                _ => throw new JsonException(
                    $"Unknown foreign key kind '{kindStr}'. Expected 'physical', 'logical', or 'virtual'."),
            };
        }
        else
        {
            kind = ForeignKeyKind.Physical;
        }

        return new ForeignKey
        {
            SourceField   = sourceField,
            TargetTable   = targetTable,
            TargetField   = targetField,
            CascadeDelete = cascadeDelete,
            Kind          = kind,
        };
    }

    public override void Write(Utf8JsonWriter writer, ForeignKey value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("sourceField", value.SourceField);
        writer.WriteString("targetTable", value.TargetTable);
        writer.WriteString("targetField", value.TargetField);
        if (value.CascadeDelete)
            writer.WriteBoolean("cascadeDelete", value.CascadeDelete);
        if (value.Kind != ForeignKeyKind.Physical)
            writer.WriteString("kind", value.Kind.ToString().ToLowerInvariant());
        writer.WriteEndObject();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Case-insensitive property lookup (mirrors JsonFileLoader's PropertyNameCaseInsensitive).</summary>
    private static bool TryGetProp(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string RequireString(JsonElement obj, string name)
    {
        if (!TryGetProp(obj, name, out var elem))
            throw new JsonException($"'{name}' is required on a foreign key.");
        return elem.GetString() ?? throw new JsonException($"'{name}' must not be null.");
    }
}
