using System.Text.Json;
using Manifesta.Core;
using Xunit;

namespace Manifesta.Core.Tests;

/// <summary>
/// Tests for JsonSchemaGenerator - JSON Schema Draft 7 generation for Manifesta definitions.
/// </summary>
public class JsonSchemaGeneratorTests
{
    private readonly JsonSchemaGenerator _generator = new();

    [Fact]
    public void GenerateTableSchema_ReturnsValidJson()
    {
        // Act
        string schema = _generator.GenerateTableSchema();

        // Assert
        Assert.NotNull(schema);
        Assert.NotEmpty(schema);
        var doc = JsonDocument.Parse(schema);
        Assert.NotNull(doc);
    }

    [Fact]
    public void GenerateTableSchema_ContainsCorrectTitle()
    {
        // Act
        string schema = _generator.GenerateTableSchema();

        // Assert
        Assert.Contains("\"title\": \"Table Definition\"", schema);
    }

    [Fact]
    public void GenerateTableSchema_ContainsSchemaVersion()
    {
        // Act
        string schema = _generator.GenerateTableSchema();

        // Assert
        Assert.Contains("\"$schema\": \"http://json-schema.org/draft-07/schema#\"", schema);
    }

    [Fact]
    public void GenerateTableSchema_HasRequiredProperties()
    {
        // Act
        string schema = _generator.GenerateTableSchema();

        // Assert
        Assert.Contains("\"required\"", schema);
        Assert.Contains("\"name\"", schema);
    }

    [Fact]
    public void GenerateTableSchema_DefinesNameProperty()
    {
        // Act
        string schema = _generator.GenerateTableSchema();

        // Assert
        Assert.Contains("\"name\"", schema);
        Assert.Contains("\"type\": \"string\"", schema);
    }

    [Fact]
    public void GenerateTableSchema_DefinesFieldsArray()
    {
        // Act
        string schema = _generator.GenerateTableSchema();

        // Assert
        Assert.Contains("\"fields\"", schema);
        Assert.Contains("\"type\": \"array\"", schema);
    }

    [Fact]
    public void GenerateTableSchema_DefinesFieldProperties()
    {
        // Act
        string schema = _generator.GenerateTableSchema();

        // Assert
        Assert.Contains("Field/column name", schema);
        Assert.Contains("SQL type", schema);
        Assert.Contains("varchar", schema);
        Assert.Contains("\"nullable\"", schema);
        Assert.Contains("\"isPrimaryKey\"", schema);
        Assert.Contains("\"isMatchColumn\"", schema);
    }

    [Fact]
    public void GenerateTableSchema_DefinesDefaultPropertyOnField()
    {
        string schema = _generator.GenerateTableSchema();

        // "default" property key is present inside the field items definition
        Assert.Contains("\"default\"", schema);
        Assert.Contains("Default value for this column", schema);
    }

    [Fact]
    public void GenerateTableSchema_DefinesPrimaryKeyArray()
    {
        // Act
        string schema = _generator.GenerateTableSchema();

        // Assert
        Assert.Contains("\"primaryKey\"", schema);
        Assert.Contains("\"Field names that form the primary key\"", schema);
    }

    [Fact]
    public void GenerateTableSchema_DefinesForeignKeysArray()
    {
        // Act
        string schema = _generator.GenerateTableSchema();

        // Assert
        Assert.Contains("\"foreignKeys\"", schema);
        Assert.Contains("\"Foreign key relationships\"", schema);
        Assert.Contains("\"sourceField\"", schema);
        Assert.Contains("\"targetTable\"", schema);
        Assert.Contains("\"targetField\"", schema);
        Assert.Contains("\"cascadeDelete\"", schema);
    }

    [Fact]
    public void GenerateTableSchema_DefinesKindEnumOnForeignKey()
    {
        string schema = _generator.GenerateTableSchema();

        Assert.Contains("\"kind\"", schema);
        Assert.Contains("\"physical\"", schema);
        Assert.Contains("\"logical\"", schema);
        Assert.Contains("\"virtual\"", schema);
        Assert.DoesNotContain("\"soft\"", schema);
    }

    [Fact]
    public void GenerateTableSchema_DefinesLabelFieldOnTable_NotOnForeignKey()
    {
        string schema = _generator.GenerateTableSchema();

        Assert.Contains("\"labelField\"", schema);
        Assert.Contains("human-readable label", schema);

        // labelField must appear at the table level, not inside the foreignKeys items block
        var fkSection = schema.Substring(schema.IndexOf("\"foreignKeys\""));
        Assert.DoesNotContain("\"labelField\"", fkSection);
    }

    [Fact]
    public void GenerateTableSchema_DefinesSetsArray()
    {
        // Act
        string schema = _generator.GenerateTableSchema();

        // Assert
        Assert.Contains("\"sets\"", schema);
        Assert.Contains("\"Named column sets for sync set expansion\"", schema);
        Assert.Contains("\"columns\"", schema);
    }

    [Fact]
    public void GenerateTableSchema_IsFormattedJson()
    {
        // Act
        string schema = _generator.GenerateTableSchema();

        // Assert
        // WriteIndented should make it multi-line
        Assert.Contains("\n", schema);
        Assert.Contains("  ", schema); // Should have indentation
    }

    [Fact]
    public void GenerateSectionSchema_ReturnsValidJson()
    {
        // Act
        string schema = _generator.GenerateSectionSchema();

        // Assert
        Assert.NotNull(schema);
        Assert.NotEmpty(schema);
        var doc = JsonDocument.Parse(schema);
        Assert.NotNull(doc);
    }

    [Fact]
    public void GenerateSectionSchema_ContainsCorrectTitle()
    {
        // Act
        string schema = _generator.GenerateSectionSchema();

        // Assert
        Assert.Contains("\"title\": \"Section Definition\"", schema);
    }

    [Fact]
    public void GenerateSectionSchema_ContainsSchemaVersion()
    {
        // Act
        string schema = _generator.GenerateSectionSchema();

        // Assert
        Assert.Contains("\"$schema\": \"http://json-schema.org/draft-07/schema#\"", schema);
    }

    [Fact]
    public void GenerateSectionSchema_HasRequiredProperties()
    {
        // Act
        string schema = _generator.GenerateSectionSchema();

        // Assert
        Assert.Contains("\"required\"", schema);
        Assert.Contains("\"name\"", schema);
        Assert.Contains("\"tables\"", schema);
    }

    [Fact]
    public void GenerateSectionSchema_HasSchemaVersionConstant()
    {
        // Act
        string schema = _generator.GenerateSectionSchema();

        // Assert
        Assert.Contains("\"$schemaVersion\"", schema);
        Assert.Contains("\"const\": \"1.0\"", schema);
    }

    [Fact]
    public void GenerateSectionSchema_DefinesTablesArray()
    {
        // Act
        string schema = _generator.GenerateSectionSchema();

        // Assert
        Assert.Contains("\"tables\"", schema);
        Assert.Contains("\"List of table names in this section\"", schema);
    }

    [Fact]
    public void GenerateSectionSchema_DefinesErdsArray()
    {
        string schema = _generator.GenerateSectionSchema();

        Assert.Contains("\"erds\"", schema);
        Assert.Contains("\"fields\"", schema);
        Assert.Contains("\"pk-and-fk\"", schema);
        Assert.DoesNotContain("\"includeSoftFks\"", schema);
    }

    [Fact]
    public void GenerateSectionSchema_ErdHasIncludeLogicalAndIncludeVirtual()
    {
        string schema = _generator.GenerateSectionSchema();

        Assert.Contains("\"includeLogical\"", schema);
        Assert.Contains("\"includeVirtual\"", schema);
        Assert.Contains("Logical (business-logic) FKs", schema);
        Assert.Contains("Virtual (documentation-only) FKs", schema);
    }

    [Fact]
    public void GenerateApiSchema_ReturnsValidJson()
    {
        // Act
        string schema = _generator.GenerateApiSchema();

        // Assert
        Assert.NotNull(schema);
        Assert.NotEmpty(schema);
        var doc = JsonDocument.Parse(schema);
        Assert.NotNull(doc);
    }

    [Fact]
    public void GenerateApiSchema_ContainsCorrectTitle()
    {
        // Act
        string schema = _generator.GenerateApiSchema();

        // Assert
        Assert.Contains("\"title\": \"API Definition\"", schema);
    }

    [Fact]
    public void GenerateApiSchema_ContainsSchemaVersion()
    {
        // Act
        string schema = _generator.GenerateApiSchema();

        // Assert
        Assert.Contains("\"$schema\": \"http://json-schema.org/draft-07/schema#\"", schema);
    }

    [Fact]
    public void GenerateApiSchema_HasRequiredProperties()
    {
        // Act
        string schema = _generator.GenerateApiSchema();

        // Assert
        Assert.Contains("\"required\"", schema);
        Assert.Contains("\"name\"", schema);
        Assert.Contains("\"title\"", schema);
        Assert.Contains("\"version\"", schema);
    }

    [Fact]
    public void GenerateApiSchema_DefinesEndpointsArray()
    {
        // Act
        string schema = _generator.GenerateApiSchema();

        // Assert
        Assert.Contains("\"endpoints\"", schema);
        Assert.Contains("\"API endpoint definitions\"", schema);
    }

    [Fact]
    public void GenerateApiSchema_DefinesEndpointProperties()
    {
        // Act
        string schema = _generator.GenerateApiSchema();

        // Assert
        Assert.Contains("\"path\"", schema);
        Assert.Contains("URL path", schema);
        Assert.Contains("users/{id}", schema);
        Assert.Contains("\"method\"", schema);
        Assert.Contains("HTTP method", schema);
        Assert.Contains("\"summary\"", schema);
        Assert.Contains("\"tags\"", schema);
    }

    [Fact]
    public void GenerateApiSchema_DefinesHttpMethods()
    {
        // Act
        string schema = _generator.GenerateApiSchema();

        // Assert
        Assert.Contains("\"enum\"", schema);
        Assert.Contains("\"GET\"", schema);
        Assert.Contains("\"POST\"", schema);
        Assert.Contains("\"PUT\"", schema);
        Assert.Contains("\"PATCH\"", schema);
        Assert.Contains("\"DELETE\"", schema);
    }

    [Fact]
    public void GenerateApiSchema_DefinesExtensionFields()
    {
        // Act
        string schema = _generator.GenerateApiSchema();

        // Assert
        // x-db-table and x-db-operation extensions
        Assert.Contains("\"dbTable\"", schema);
        Assert.Contains("x-db-table extension", schema);
        Assert.Contains("\"dbOperation\"", schema);
        Assert.Contains("x-db-operation extension", schema);
    }

    [Fact]
    public void AllSchemas_CanBeParsedByJsonDocument()
    {
        var tableSchema   = _generator.GenerateTableSchema();
        var sectionSchema = _generator.GenerateSectionSchema();
        var apiSchema     = _generator.GenerateApiSchema();
        var configSchema  = _generator.GenerateConfigSchema();

        using var tableDoc   = JsonDocument.Parse(tableSchema);
        using var sectionDoc = JsonDocument.Parse(sectionSchema);
        using var apiDoc     = JsonDocument.Parse(apiSchema);
        using var configDoc  = JsonDocument.Parse(configSchema);

        Assert.Equal(JsonValueKind.Object, tableDoc.RootElement.ValueKind);
        Assert.Equal(JsonValueKind.Object, sectionDoc.RootElement.ValueKind);
        Assert.Equal(JsonValueKind.Object, apiDoc.RootElement.ValueKind);
        Assert.Equal(JsonValueKind.Object, configDoc.RootElement.ValueKind);
    }

    [Fact]
    public void AllSchemas_ContainDescriptions()
    {
        var tableSchema   = _generator.GenerateTableSchema();
        var sectionSchema = _generator.GenerateSectionSchema();
        var apiSchema     = _generator.GenerateApiSchema();
        var configSchema  = _generator.GenerateConfigSchema();

        Assert.Contains("\"description\"", tableSchema);
        Assert.Contains("\"description\"", sectionSchema);
        Assert.Contains("\"description\"", apiSchema);
        Assert.Contains("\"description\"", configSchema);
    }

    // ── Config schema ─────────────────────────────────────────────────────

    [Fact]
    public void GenerateConfigSchema_ReturnsValidJson()
    {
        var schema = _generator.GenerateConfigSchema();

        Assert.NotNull(schema);
        Assert.NotEmpty(schema);
        JsonDocument.Parse(schema);
    }

    [Fact]
    public void GenerateConfigSchema_ContainsCorrectTitle()
    {
        var schema = _generator.GenerateConfigSchema();

        Assert.Contains("\"title\": \"Manifesta Config\"", schema);
    }

    [Fact]
    public void GenerateConfigSchema_DefinesPathsSection()
    {
        var schema = _generator.GenerateConfigSchema();

        Assert.Contains("\"paths\"", schema);
        Assert.Contains("\"root\"", schema);
        Assert.Contains("\"tables\"", schema);
        Assert.Contains("\"adapters\"", schema);
        Assert.Contains("\"documentSections\"", schema);
    }

    [Fact]
    public void GenerateConfigSchema_DefinesOutputSection()
    {
        var schema = _generator.GenerateConfigSchema();

        Assert.Contains("\"output\"", schema);
        Assert.Contains("\"title\"", schema);
        Assert.Contains("\"sectionOrder\"", schema);
        Assert.Contains("\"format\"", schema);
    }

    [Fact]
    public void GenerateConfigSchema_OutputFormat_HasPolymorphicOneOf()
    {
        var schema = _generator.GenerateConfigSchema();

        Assert.Contains("\"oneOf\"", schema);
        Assert.Contains("\"markdown\"", schema);
        Assert.Contains("\"html\"", schema);
        Assert.Contains("\"pdf\"", schema);
    }

    [Fact]
    public void GenerateConfigSchema_MarkdownFormat_HasDialectEnum()
    {
        var schema = _generator.GenerateConfigSchema();

        Assert.Contains("\"dialect\"", schema);
        Assert.Contains("\"CommonMark\"", schema);
        Assert.Contains("\"AzureDevOps\"", schema);
    }

    [Fact]
    public void GenerateConfigSchema_DefinesAdaptersSection()
    {
        var schema = _generator.GenerateConfigSchema();

        Assert.Contains("\"adapters\"", schema);
        Assert.Contains("\"enabled\"", schema);
        Assert.Contains("\"outputDir\"", schema);
        Assert.Contains("\"strict\"", schema);
    }

    [Fact]
    public void GenerateConfigSchema_DefinesNamingSection()
    {
        var schema = _generator.GenerateConfigSchema();

        Assert.Contains("\"naming\"", schema);
        Assert.Contains("\"tablePattern\"", schema);
        Assert.Contains("\"fieldPattern\"", schema);
    }
}
