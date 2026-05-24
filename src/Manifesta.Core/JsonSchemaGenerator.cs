using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Manifesta.Core.IR;

namespace Manifesta.Core;

/// <summary>
/// Generates JSON Schema (Draft 7) from IR model types.
/// Used to provide schema validation and IDE autocomplete for definition files.
/// </summary>
public sealed class JsonSchemaGenerator
{
    private static readonly JsonSerializerOptions _serializeOptions = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Generate JSON Schema for TableDefinition.
    /// </summary>
    public string GenerateTableSchema()
    {
        var schema = new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["title"] = "Table Definition",
            ["description"] = "JSON schema for Manifesta table definitions (table.json files)",
            ["type"] = "object",
            ["required"] = new JsonArray("name"),
            ["properties"] = new JsonObject
            {
                ["name"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Logical table name (e.g., 'dbo.Customer')",
                    ["minLength"] = 1
                },
                ["description"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Human-readable description of the table"
                },
                ["fields"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Column definitions",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("name", "type"),
                        ["properties"] = new JsonObject
                        {
                            ["name"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Field/column name"
                            },
                            ["type"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "SQL type (e.g., 'int', 'varchar(255)', 'datetime')"
                            },
                            ["nullable"] = new JsonObject
                            {
                                ["type"] = "boolean",
                                ["description"] = "Whether the field can be NULL",
                                ["default"] = false
                            },
                            ["description"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Field documentation"
                            },
                            ["isPrimaryKey"] = new JsonObject
                            {
                                ["type"] = "boolean",
                                ["description"] = "Whether this field is part of the primary key",
                                ["default"] = false
                            },
                            ["isMatchColumn"] = new JsonObject
                            {
                                ["type"] = "boolean",
                                ["description"] = "Whether this field is a match column (non-nullable, unique)",
                                ["default"] = false
                            },
                            ["default"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Default value for this column as defined in the database (e.g. \"0\", \"'Active'\", \"getdate()\")"
                            },
                            ["isComputed"] = new JsonObject
                            {
                                ["type"] = "boolean",
                                ["description"] = "True when this is a computed column. DB-authoritative — populated by db export/merge.",
                                ["default"] = false
                            },
                            ["computedExpression"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "SQL expression for computed columns (e.g. \"([FirstName]+' '+[LastName])\"). Null for non-computed columns. DB-authoritative."
                            },
                            ["isPersisted"] = new JsonObject
                            {
                                ["type"] = "boolean",
                                ["description"] = "True when the computed column is physically stored (PERSISTED). Always false for non-computed columns. DB-authoritative.",
                                ["default"] = false
                            }
                        }
                    }
                },
                ["primaryKey"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Field names that form the primary key",
                    ["items"] = new JsonObject { ["type"] = "string" }
                },
                ["labelField"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Field name that holds the human-readable label for this table. Used by AI description generation and documentation rendering when this table is referenced as a FK target. Repo-sovereign."
                },
                ["foreignKeys"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Foreign key relationships",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("sourceField", "targetTable", "targetField"),
                        ["properties"] = new JsonObject
                        {
                            ["sourceField"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Field name on this table"
                            },
                            ["targetTable"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Referenced table name"
                            },
                            ["targetField"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Referenced field name on the target table"
                            },
                            ["cascadeDelete"] = new JsonObject
                            {
                                ["type"] = "boolean",
                                ["description"] = "Whether this FK has cascade delete enabled. Only valid for physical relationships.",
                                ["default"] = false
                            },
                            ["kind"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Relationship kind: physical (DB-enforced, default), logical (business-logic, not DB-enforced), or virtual (documentation-only conceptual link).",
                                ["enum"] = new JsonArray("physical", "logical", "virtual"),
                                ["default"] = "physical"
                            }
                        }
                    }
                },
                ["databaseTypes"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Database type tags (e.g., 'MSSQL', 'Postgres')",
                    ["items"] = new JsonObject { ["type"] = "string" }
                },
                ["sets"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Named column sets for sync set expansion",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("name"),
                        ["properties"] = new JsonObject
                        {
                            ["name"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Name of the column set"
                            },
                            ["columns"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["description"] = "Field names in this set",
                                ["items"] = new JsonObject { ["type"] = "string" }
                            }
                        }
                    }
                },
                ["sections"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Section membership (resolved from section definition files)",
                    ["items"] = new JsonObject { ["type"] = "string" }
                }
            }
        };

        return JsonSerializer.Serialize(schema, _serializeOptions);
    }

    /// <summary>
    /// Generate JSON Schema for SectionDefinition.
    /// </summary>
    public string GenerateSectionSchema()
    {
        var schema = new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["title"] = "Section Definition",
            ["description"] = "JSON schema for Manifesta section definitions (document-sections/*.json files)",
            ["type"] = "object",
            ["required"] = new JsonArray("name", "tables"),
            ["properties"] = new JsonObject
            {
                ["$schemaVersion"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Schema version for forward compatibility",
                    ["const"] = "1.0"
                },
                ["name"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Stable kebab-case identifier used for sectionOrder matching (e.g., 'subscriber-management'). Never displayed to end users.",
                    ["minLength"] = 1
                },
                ["title"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Human-readable heading used in the TOC and section headings. Falls back to 'name' when omitted."
                },
                ["description"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Human-readable description of the section"
                },
                ["tables"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "List of table names in this section",
                    ["items"] = new JsonObject { ["type"] = "string" }
                },
                ["erds"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Auto-generated Mermaid ERD diagrams embedded in the section",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["title"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Optional heading rendered above the diagram"
                            },
                            ["tables"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["description"] = "Subset of section tables to include. Omit to include all section tables.",
                                ["items"] = new JsonObject { ["type"] = "string" }
                            },
                            ["includeLogical"] = new JsonObject
                            {
                                ["type"] = "boolean",
                                ["description"] = "Whether to include Logical (business-logic) FKs in the diagram. Omit or set true to include (default); set false to exclude."
                            },
                            ["includeVirtual"] = new JsonObject
                            {
                                ["type"] = "boolean",
                                ["description"] = "Whether to include Virtual (documentation-only) FKs as dashed lines in the diagram.",
                                ["default"] = false
                            },
                            ["fields"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Which fields to show inside each entity box",
                                ["enum"] = new JsonArray("none", "pk-and-fk", "all"),
                                ["default"] = "pk-and-fk"
                            }
                        }
                    }
                }
            }
        };

        return JsonSerializer.Serialize(schema, _serializeOptions);
    }

    /// <summary>
    /// Generate JSON Schema for <c>manifesta.config.json</c>.
    /// Covers paths, output (polymorphic format), adapters, and naming sections.
    /// </summary>
    public string GenerateConfigSchema()
    {
        var markdownFormat = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "Markdown output (default)",
            ["required"] = new JsonArray("type"),
            ["properties"] = new JsonObject
            {
                ["type"] = new JsonObject
                {
                    ["const"] = "markdown"
                },
                ["dialect"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Markdown dialect for fenced code blocks",
                    ["enum"] = new JsonArray("CommonMark", "AzureDevOps"),
                    ["default"] = "CommonMark"
                }
            },
            ["additionalProperties"] = false
        };

        var htmlFormat = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "HTML output",
            ["required"] = new JsonArray("type"),
            ["properties"] = new JsonObject
            {
                ["type"] = new JsonObject { ["const"] = "html" },
                ["template"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "HTML template name"
                }
            },
            ["additionalProperties"] = false
        };

        var pdfFormat = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "PDF output",
            ["required"] = new JsonArray("type"),
            ["properties"] = new JsonObject
            {
                ["type"] = new JsonObject { ["const"] = "pdf" }
            },
            ["additionalProperties"] = false
        };

        var schema = new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["title"] = "Manifesta Config",
            ["description"] = "JSON schema for manifesta.config.json",
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["paths"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "File system paths used by Manifesta",
                    ["properties"] = new JsonObject
                    {
                        ["root"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Root directory for scanning (default: '../')",
                            ["default"] = "../"
                        },
                        ["skip"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = "Glob patterns of paths to skip during scanning",
                            ["items"] = new JsonObject { ["type"] = "string" }
                        },
                        ["documentSections"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Directory containing section definition JSON files",
                            ["default"] = "./document-sections"
                        },
                        ["tables"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Directory containing table definition JSON files",
                            ["default"] = "./Tables"
                        },
                        ["adapters"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Directory containing adapter definition files (*.adapter.json)",
                            ["default"] = "./adapters"
                        }
                    },
                    ["additionalProperties"] = false
                },
                ["output"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Documentation output settings",
                    ["properties"] = new JsonObject
                    {
                        ["title"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Document title (default: 'DATABASE MODEL')"
                        },
                        ["sectionOrder"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = "Explicit section ordering. Sections not listed appear at the end.",
                            ["items"] = new JsonObject { ["type"] = "string" }
                        },
                        ["format"] = new JsonObject
                        {
                            ["description"] = "Output format. Defaults to markdown/CommonMark when absent.",
                            ["oneOf"] = new JsonArray(markdownFormat, htmlFormat, pdfFormat)
                        }
                    },
                    ["additionalProperties"] = false
                },
                ["adapters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Adapter execution settings",
                    ["properties"] = new JsonObject
                    {
                        ["enabled"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = "Names of adapters to run. Omit to run all adapters in paths.adapters.",
                            ["items"] = new JsonObject { ["type"] = "string" }
                        },
                        ["outputDir"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Root directory where adapter output is written",
                            ["default"] = "./output/adapters"
                        },
                        ["strict"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "When true, any adapter error fails the entire run",
                            ["default"] = true
                        }
                    },
                    ["additionalProperties"] = false
                },
                ["naming"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "Naming convention enforcement",
                    ["properties"] = new JsonObject
                    {
                        ["tablePattern"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Regex pattern that table names must match"
                        },
                        ["fieldPattern"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Regex pattern that field names must match"
                        }
                    },
                    ["additionalProperties"] = false
                },
                ["linkToTables"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Generate cross-links to table definitions in documentation",
                    ["default"] = false
                },
            },
            ["additionalProperties"] = false
        };

        return JsonSerializer.Serialize(schema, _serializeOptions);
    }

    /// <summary>
    /// Generate JSON Schema for ApiDefinition.
    /// </summary>
    public string GenerateApiSchema()
    {
        var schema = new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["title"] = "API Definition",
            ["description"] = "JSON schema for Manifesta API definitions (api.json files)",
            ["type"] = "object",
            ["required"] = new JsonArray("name", "title", "version"),
            ["properties"] = new JsonObject
            {
                ["name"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "API identifier name",
                    ["minLength"] = 1
                },
                ["title"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Human-readable API title"
                },
                ["version"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "API version (e.g., '1.0.0')"
                },
                ["description"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "API description"
                },
                ["databaseTypes"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Database type tags",
                    ["items"] = new JsonObject { ["type"] = "string" }
                },
                ["endpoints"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "API endpoint definitions",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray("path", "method"),
                        ["properties"] = new JsonObject
                        {
                            ["path"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "URL path (e.g., '/users/{id}')"
                            },
                            ["method"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "HTTP method",
                                ["enum"] = new JsonArray("GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS")
                            },
                            ["summary"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Short summary of the endpoint"
                            },
                            ["tags"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["description"] = "Endpoint tags for grouping",
                                ["items"] = new JsonObject { ["type"] = "string" }
                            },
                            ["dbTable"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Associated database table (x-db-table extension)"
                            },
                            ["dbOperation"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Database operation type (x-db-operation extension)"
                            },
                            ["databaseTypes"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["description"] = "Database types supported by this endpoint",
                                ["items"] = new JsonObject { ["type"] = "string" }
                            }
                        }
                    }
                }
            }
        };

        return JsonSerializer.Serialize(schema, _serializeOptions);
    }
}
