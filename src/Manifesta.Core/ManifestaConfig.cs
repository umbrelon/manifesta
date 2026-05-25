using System.Text.Json.Serialization;

namespace Manifesta.Core;

/// <summary>
/// Identifies the Markdown dialect used when rendering Markdown output files.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MarkdownDialect
{
    /// <summary>Standard CommonMark fenced code blocks (e.g. ```mermaid```).</summary>
    CommonMark,

    /// <summary>Azure DevOps wiki dialect (e.g. :::mermaid / :::).</summary>
    AzureDevOps,
}

// ── Output format config (polymorphic) ───────────────────────────────────────

/// <summary>
/// Base class for output format configuration.
/// Deserialized polymorphically using the <c>"type"</c> discriminator.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MarkdownFormatConfig), "markdown")]
[JsonDerivedType(typeof(HtmlFormatConfig),     "html")]
[JsonDerivedType(typeof(PdfFormatConfig),       "pdf")]
public abstract class OutputFormatConfig { }

/// <summary>Markdown output — default format.</summary>
public sealed class MarkdownFormatConfig : OutputFormatConfig
{
    /// <summary>
    /// Markdown dialect to use when rendering fenced code blocks.
    /// Defaults to <see cref="MarkdownDialect.CommonMark"/>.
    /// </summary>
    [JsonPropertyName("dialect")]
    public MarkdownDialect Dialect { get; set; } = MarkdownDialect.CommonMark;
}

/// <summary>HTML output (stub — future milestone).</summary>
public sealed class HtmlFormatConfig : OutputFormatConfig
{
    /// <summary>HTML template name (optional).</summary>
    [JsonPropertyName("template")]
    public string? Template { get; set; }
}

/// <summary>PDF output (stub — future milestone).</summary>
public sealed class PdfFormatConfig : OutputFormatConfig { }

// ── Root config ───────────────────────────────────────────────────────────────

/// <summary>
/// Represents the contents of <c>manifesta.config.json</c>.
/// </summary>
public sealed class ManifestaConfig
{
    [JsonPropertyName("paths")]
    public ConfigPaths Paths { get; set; } = new();

    [JsonPropertyName("output")]
    public ConfigOutput Output { get; set; } = new();

    [JsonPropertyName("naming")]
    public ConfigNaming Naming { get; set; } = new();

    [JsonPropertyName("adapters")]
    public ConfigAdapters? Adapters { get; set; }

    [JsonPropertyName("linkToTables")]
    public bool LinkToTables { get; set; }

    [JsonPropertyName("referenceTableConfig")]
    public ReferenceTableConfig? ReferenceTableConfig { get; set; }

    [JsonPropertyName("sensitivity")]
    public SensitivityConfig? Sensitivity { get; set; }

    [JsonPropertyName("tenants")]
    public TenantConfig? Tenants { get; set; }
}

public sealed class ConfigPaths
{
    [JsonPropertyName("root")]
    public string Root { get; set; } = "../";

    [JsonPropertyName("skip")]
    public List<string> Skip { get; set; } = [];

    [JsonPropertyName("documentSections")]
    public string DocumentSections { get; set; } = "./document-sections";

    [JsonPropertyName("tables")]
    public string Tables { get; set; } = "./Tables";

    /// <summary>Directory that contains <c>*.adapter.json</c> files.</summary>
    [JsonPropertyName("adapters")]
    public string Adapters { get; set; } = "./adapters";
}

public sealed class ConfigOutput
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("sectionOrder")]
    public List<string> SectionOrder { get; set; } = [];

    /// <summary>
    /// Output format configuration. Defaults to Markdown with CommonMark dialect
    /// when absent from the config file.
    /// </summary>
    [JsonPropertyName("format")]
    public OutputFormatConfig Format { get; set; } = new MarkdownFormatConfig();
}

public sealed class ConfigAdapters
{
    /// <summary>
    /// Names of adapter files to run. When null or empty all adapters in
    /// <c>paths.adapters</c> are executed.
    /// </summary>
    [JsonPropertyName("enabled")]
    public List<string>? Enabled { get; set; }

    /// <summary>Root directory where adapter output is written.</summary>
    [JsonPropertyName("outputDir")]
    public string OutputDir { get; set; } = "./output/adapters";

    /// <summary>When <c>true</c>, any adapter error fails the entire run.</summary>
    [JsonPropertyName("strict")]
    public bool Strict { get; set; } = true;
}

public sealed class ReferenceTableConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("autoCapture")]
    public bool AutoCapture { get; set; } = true;

    [JsonPropertyName("captureOn")]
    public List<string> CaptureOn { get; set; } = ["db-export"];

    [JsonPropertyName("explicitTables")]
    public List<string> ExplicitTables { get; set; } = [];

    [JsonPropertyName("heuristics")]
    public ReferenceTableHeuristicsConfig Heuristics { get; set; } = new();
}

public sealed class ReferenceTableHeuristicsConfig
{
    public const int DefaultMaxRows   = 100;
    public const int DefaultMaxSizeKb = 1024;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("maxRows")]
    public int MaxRows { get; set; } = DefaultMaxRows;

    [JsonPropertyName("maxSizeKb")]
    public int MaxSizeKb { get; set; } = DefaultMaxSizeKb;

    [JsonPropertyName("patterns")]
    public List<string> Patterns { get; set; } = ["*Mode", "*Type", "*Status", "*State", "*Category", "*Reason"];
}

public sealed class ConfigNaming
{
    /// <summary>Regex pattern that table names must match (optional).</summary>
    [JsonPropertyName("tablePattern")]
    public string? TablePattern { get; set; }

    /// <summary>Regex pattern that field names must match (optional).</summary>
    [JsonPropertyName("fieldPattern")]
    public string? FieldPattern { get; set; }
}

public sealed class SensitivityConfig
{
    /// <summary>When <c>true</c>, every field must have an explicit sensitivity value.</summary>
    [JsonPropertyName("requireClassification")]
    public bool RequireClassification { get; set; }

    /// <summary>When <c>true</c> (default), PII fields without a description emit a warning.</summary>
    [JsonPropertyName("piiRequiresDescription")]
    public bool PiiRequiresDescription { get; set; } = true;
}

// ── Tenant configuration ──────────────────────────────────────────────────────

/// <summary>
/// Describes the multi-tenant topology: database types and database instances.
/// Used by <c>db tenant-drift</c> for module-scoped drift detection across a tenant tree.
/// </summary>
public sealed class TenantConfig
{
    /// <summary>User-defined labels for classes of databases, with optional structural rules.</summary>
    [JsonPropertyName("types")]
    public Dictionary<string, TenantTypeDefinition> Types { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Named database instances with connection strings and installed modules.</summary>
    [JsonPropertyName("databases")]
    public Dictionary<string, TenantDatabaseEntry> Databases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Structural rules for a class of databases in the tenant topology.
/// </summary>
public sealed class TenantTypeDefinition
{
    /// <summary>When <c>true</c>, databases of this type are the tree root. Exactly one database in the topology must be of a root type.</summary>
    [JsonPropertyName("root")]
    public bool Root { get; set; }

    /// <summary>Type names whose databases may be the direct parent of a database of this type.</summary>
    [JsonPropertyName("allowedParents")]
    public List<string> AllowedParents { get; set; } = [];

    /// <summary>Section names that every database of this type must have installed.</summary>
    [JsonPropertyName("requiredSections")]
    public List<string> RequiredSections { get; set; } = [];
}

/// <summary>
/// A named database instance in the tenant topology.
/// </summary>
public sealed class TenantDatabaseEntry
{
    /// <summary>Connection string for this database instance.</summary>
    [JsonPropertyName("connection")]
    public string Connection { get; set; } = "";

    /// <summary>Type name from <see cref="TenantConfig.Types"/>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>Name of the parent database entry, or <c>null</c> for the root database.</summary>
    [JsonPropertyName("parent")]
    public string? Parent { get; set; }

    /// <summary>Section names (module names) installed on this database.</summary>
    [JsonPropertyName("sections")]
    public List<string> Sections { get; set; } = [];
}
