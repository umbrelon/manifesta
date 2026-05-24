using System.Text.Json;
using System.Text.Json.Serialization;

namespace Manifesta.Core.Pipeline;

/// <summary>
/// JSON output structure written by <c>validate all</c> and <c>validate cross</c>.
/// </summary>
public sealed record ValidationReport
{
    public required string                       GeneratedAt { get; init; }
    public required ValidationReportSummary      Summary     { get; init; }
    public required IReadOnlyList<ValidationIssueDto> Issues { get; init; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ValidationReport From(
        ValidationResult result,
        int tablesScanned   = 0,
        int sectionsScanned = 0,
        int apisScanned     = 0)
        => new()
        {
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O"),
            Summary = new ValidationReportSummary
            {
                TablesScanned   = tablesScanned,
                SectionsScanned = sectionsScanned,
                ApisScanned     = apisScanned,
                Errors          = result.Issues.Count(i => i.Severity == ValidationSeverity.Error),
                Warnings        = result.Issues.Count(i => i.Severity == ValidationSeverity.Warning),
                HasErrors       = result.HasErrors,
                HasWarnings     = result.HasWarnings,
            },
            Issues = result.Issues.Select(ValidationIssueDto.From).ToList().AsReadOnly(),
        };
}

public sealed record ValidationReportSummary
{
    public required int  TablesScanned   { get; init; }
    public required int  SectionsScanned { get; init; }
    public required int  ApisScanned     { get; init; }
    public required int  Errors          { get; init; }
    public required int  Warnings        { get; init; }
    public required bool HasErrors       { get; init; }
    public required bool HasWarnings     { get; init; }
}

/// <summary>
/// JSON-serializable projection of <see cref="ValidationIssue"/>.
/// Uses string enum values for human-readable output.
/// </summary>
public sealed record ValidationIssueDto
{
    [JsonConverter(typeof(JsonStringEnumConverter<ValidationSeverity>))]
    public required ValidationSeverity Severity { get; init; }
    public required string Code    { get; init; }
    public required string Message { get; init; }
    public string? File  { get; init; }
    public string? Field { get; init; }
    public int?    Line  { get; init; }

    public static ValidationIssueDto From(ValidationIssue issue) => new()
    {
        Severity = issue.Severity,
        Code     = issue.Code,
        Message  = issue.Message,
        File     = issue.File,
        Field    = issue.Field,
        Line     = issue.Line,
    };
}
