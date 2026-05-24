using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Manifesta.Core.IR;
using Manifesta.Doc;
using Xunit;

namespace Manifesta.Doc.Tests;

/// <summary>
/// Integration tests for soft foreign key support.
///
/// These tests exercise the full pipeline from JSON file on disk
/// through deserialisation to rendered markdown output, verifying
/// that <c>"soft": true</c> in a table definition file produces
/// the expected annotation in the generated documentation.
/// </summary>
public sealed class SoftForeignKeyIntegrationTests
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly DatabaseDocGenerator _generator = new();

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Deserialises a table definition JSON file to <see cref="TableDefinition"/>,
    /// mimicking the behaviour of <c>TableLoader</c>.
    /// </summary>
    private static TableDefinition LoadTableFromJson(string json)
    {
        var table = JsonSerializer.Deserialize<TableDefinition>(json, _jsonOptions);
        table.Should().NotBeNull("JSON should deserialise to a valid TableDefinition");
        return table!;
    }

    private static ManifestRoot BuildRoot(IEnumerable<TableDefinition> tables, string sectionName)
    {
        var tableList = tables.ToList();
        return new ManifestRoot
        {
            Tables = tableList.AsReadOnly(),
            Apis = Array.Empty<ApiDefinition>(),
            Sections = new List<SectionDefinition>
            {
                new()
                {
                    Name = sectionName,
                    Tables = tableList.Select(t => t.Name).ToList().AsReadOnly()
                }
            }.AsReadOnly(),
        };
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public void Pipeline_SoftFkInJson_RenderedWithSoftAnnotation()
    {
        // Represents a hand-authored table.json where the FK relationship
        // is known to business logic but not enforced by a DB constraint.
        var resellerJson = """
            {
              "name": "dbo.Reseller",
              "description": "Reseller accounts",
              "fields": [
                { "name": "lResellerID", "type": "int", "nullable": false, "isPrimaryKey": true }
              ],
              "primaryKey": ["lResellerID"]
            }
            """;

        var mappingJson = """
            {
              "name": "dbo.WholesaleBundleMapping",
              "description": "Wholesale/retail bundle mapping",
              "fields": [
                { "name": "lId",               "type": "int", "nullable": false, "isPrimaryKey": true },
                { "name": "lWholesaleResellerId", "type": "int", "nullable": false }
              ],
              "primaryKey": ["lId"],
              "foreignKeys": [
                {
                  "sourceField": "lWholesaleResellerId",
                  "targetTable": "dbo.Reseller",
                  "targetField": "lResellerID",
                  "kind": "logical"
                }
              ]
            }
            """;

        var reseller = LoadTableFromJson(resellerJson);
        var mapping  = LoadTableFromJson(mappingJson);

        // Verify the FK was deserialised correctly
        mapping.ForeignKeys.Should().HaveCount(1);
        mapping.ForeignKeys[0].Kind.Should().Be(ForeignKeyKind.Logical, "\"kind\": \"logical\" in JSON should deserialise to Kind = Logical");

        var root   = BuildRoot([reseller, mapping], "Wholesale");
        var result = _generator.Generate(root);

        result.Should().Contain("(logical)",
            "a logical FK should be annotated with (logical) in the generated documentation");
        result.Should().Contain("`lWholesaleResellerId` → `dbo.Reseller.lResellerID` (logical)");
    }

    // ── Backward compat: legacy "soft" property ──────────────────────────────

    [Fact]
    public void Pipeline_LegacySoftTrueInJson_DeserializesAsLogical()
    {
        // A hand-authored file written before the "kind" property existed.
        // "soft": true must silently map to Kind = Logical.
        var json = """
            {
              "name": "dbo.Order",
              "fields": [
                { "name": "lOrderID",    "type": "int", "nullable": false, "isPrimaryKey": true },
                { "name": "lCustomerID", "type": "int", "nullable": false }
              ],
              "primaryKey": ["lOrderID"],
              "foreignKeys": [
                {
                  "sourceField": "lCustomerID",
                  "targetTable": "dbo.Customer",
                  "targetField": "lCustomerID",
                  "soft": true
                }
              ]
            }
            """;

        var table = LoadTableFromJson(json);

        table.ForeignKeys.Should().HaveCount(1);
        table.ForeignKeys[0].Kind.Should().Be(ForeignKeyKind.Logical,
            because: "\"soft\": true is the legacy spelling for a logical FK");
    }

    [Fact]
    public void Pipeline_LegacySoftFalseInJson_DeserializesAsPhysical()
    {
        // "soft": false is equivalent to omitting the property — FK is Physical.
        var json = """
            {
              "name": "dbo.Order",
              "fields": [
                { "name": "lOrderID",    "type": "int", "nullable": false, "isPrimaryKey": true },
                { "name": "lCustomerID", "type": "int", "nullable": false }
              ],
              "primaryKey": ["lOrderID"],
              "foreignKeys": [
                {
                  "sourceField": "lCustomerID",
                  "targetTable": "dbo.Customer",
                  "targetField": "lCustomerID",
                  "soft": false
                }
              ]
            }
            """;

        var table = LoadTableFromJson(json);

        table.ForeignKeys[0].Kind.Should().Be(ForeignKeyKind.Physical,
            because: "\"soft\": false is the legacy spelling for a physical FK");
    }

    [Fact]
    public void Pipeline_BothSoftAndKind_ThrowsJsonException()
    {
        // Authoring error: both the deprecated "soft" and the new "kind" are present.
        var json = """
            {
              "name": "dbo.Order",
              "fields": [
                { "name": "lOrderID",    "type": "int", "nullable": false, "isPrimaryKey": true },
                { "name": "lCustomerID", "type": "int", "nullable": false }
              ],
              "primaryKey": ["lOrderID"],
              "foreignKeys": [
                {
                  "sourceField": "lCustomerID",
                  "targetTable": "dbo.Customer",
                  "targetField": "lCustomerID",
                  "soft": true,
                  "kind": "logical"
                }
              ]
            }
            """;

        var act = () => LoadTableFromJson(json);

        act.Should().Throw<JsonException>(
            because: "specifying both 'soft' and 'kind' is an authoring error");
    }

    [Fact]
    public void Pipeline_VirtualFkInJson_RenderedWithVirtualAnnotation()
    {
        var resellerJson = """
            {
              "name": "dbo.Reseller",
              "fields": [
                { "name": "lResellerID", "type": "int", "nullable": false, "isPrimaryKey": true }
              ],
              "primaryKey": ["lResellerID"]
            }
            """;

        var mappingJson = """
            {
              "name": "dbo.WholesaleBundleMapping",
              "fields": [
                { "name": "lId", "type": "int", "nullable": false, "isPrimaryKey": true },
                { "name": "lWholesaleResellerId", "type": "int", "nullable": false }
              ],
              "primaryKey": ["lId"],
              "foreignKeys": [
                {
                  "sourceField": "lWholesaleResellerId",
                  "targetTable": "dbo.Reseller",
                  "targetField": "lResellerID",
                  "kind": "virtual"
                }
              ]
            }
            """;

        var reseller = LoadTableFromJson(resellerJson);
        var mapping  = LoadTableFromJson(mappingJson);

        mapping.ForeignKeys[0].Kind.Should().Be(ForeignKeyKind.Virtual);

        var root   = BuildRoot([reseller, mapping], "Wholesale");
        var result = _generator.Generate(root);

        result.Should().Contain("(virtual)",
            "a virtual FK should be annotated with (virtual) in the generated documentation");
        result.Should().Contain("`lWholesaleResellerId` → `dbo.Reseller.lResellerID` (virtual)");
    }

    [Fact]
    public void Pipeline_LabelFieldInJson_NotRenderedAsAnnotation()
    {
        // labelField is a metadata hint for AI on the target table; it is not shown in the FK line in docs.
        var orderJson = """
            {
              "name": "dbo.Order",
              "fields": [
                { "name": "lOrderID",    "type": "int", "nullable": false, "isPrimaryKey": true },
                { "name": "lCustomerID", "type": "int", "nullable": false }
              ],
              "primaryKey": ["lOrderID"],
              "foreignKeys": [
                {
                  "sourceField": "lCustomerID",
                  "targetTable": "dbo.Customer",
                  "targetField": "lCustomerID",
                  "kind": "logical"
                }
              ]
            }
            """;

        var customerJson = """
            {
              "name": "dbo.Customer",
              "fields": [
                { "name": "lCustomerID", "type": "int", "nullable": false, "isPrimaryKey": true },
                { "name": "szName",      "type": "nvarchar(100)", "nullable": false }
              ],
              "primaryKey": ["lCustomerID"],
              "labelField": "szName"
            }
            """;

        var order    = LoadTableFromJson(orderJson);
        var customer = LoadTableFromJson(customerJson);

        order.ForeignKeys[0].Kind.Should().Be(ForeignKeyKind.Logical);
        customer.LabelField.Should().Be("szName");

        var root   = BuildRoot([customer, order], "Sales");
        var result = _generator.Generate(root);

        result.Should().Contain("`lCustomerID` → `dbo.Customer.lCustomerID` (logical)");
        result.Split('\n').Where(l => l.Contains("→"))
            .Should().AllSatisfy(l => l.Should().NotContain("szName"),
                "labelField is not rendered in the FK annotation line");
    }

    [Fact]
    public void Pipeline_NoSoftPropertyInJson_BackwardsCompatible()
    {
        // Existing table.json files without "soft" should continue to work
        // unchanged — omitting the property is equivalent to "soft": false.
        var tableJson = """
            {
              "name": "dbo.Order",
              "description": "Customer orders",
              "fields": [
                { "name": "lOrderID",    "type": "int", "nullable": false, "isPrimaryKey": true },
                { "name": "lCustomerID", "type": "int", "nullable": false }
              ],
              "primaryKey": ["lOrderID"],
              "foreignKeys": [
                {
                  "sourceField": "lCustomerID",
                  "targetTable": "dbo.Customer",
                  "targetField": "lCustomerID"
                }
              ]
            }
            """;

        var customerJson = """
            {
              "name": "dbo.Customer",
              "fields": [
                { "name": "lCustomerID", "type": "int", "nullable": false, "isPrimaryKey": true }
              ],
              "primaryKey": ["lCustomerID"]
            }
            """;

        var order    = LoadTableFromJson(tableJson);
        var customer = LoadTableFromJson(customerJson);

        // Verify Kind defaults to Physical when absent from JSON
        order.ForeignKeys[0].Kind.Should().Be(ForeignKeyKind.Physical, "omitting \"kind\" should default to Physical");

        var root   = BuildRoot([customer, order], "Sales");
        var result = _generator.Generate(root);

        result.Should().Contain("`lCustomerID` → `dbo.Customer.lCustomerID`");
        result.Should().NotContain("(logical)",
            "a physical FK should not carry a kind annotation");
    }
}
