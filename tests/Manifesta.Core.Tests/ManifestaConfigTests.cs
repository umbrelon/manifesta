using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Manifesta.Core.Tests;

/// <summary>
/// Tests for <see cref="ManifestaConfig"/> JSON deserialization.
/// Focuses on properties that are loaded from manifesta.config.json.
/// </summary>
public sealed class ManifestaConfigTests
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── output.title ──────────────────────────────────────────────────────

    [Fact]
    public void Deserialize_OutputTitle_IsReadCorrectly()
    {
        var json = """
            {
              "output": {
                "title": "MY DATABASE MODEL"
              }
            }
            """;

        var config = JsonSerializer.Deserialize<ManifestaConfig>(json, _options)!;

        config.Output.Title.Should().Be("MY DATABASE MODEL");
    }

    [Fact]
    public void Deserialize_OutputTitleAbsent_IsNull()
    {
        var json = """
            {
              "output": {
                "sectionOrder": ["A", "B"]
              }
            }
            """;

        var config = JsonSerializer.Deserialize<ManifestaConfig>(json, _options)!;

        config.Output.Title.Should().BeNull();
    }

    [Fact]
    public void Deserialize_OutputAbsent_TitleIsNull()
    {
        var json = "{}";

        var config = JsonSerializer.Deserialize<ManifestaConfig>(json, _options)!;

        config.Output.Title.Should().BeNull();
    }

    [Fact]
    public void Deserialize_OutputTitle_DoesNotAffectSectionOrder()
    {
        var json = """
            {
              "output": {
                "title": "CUSTOM TITLE",
                "sectionOrder": ["Sales", "Accounts"]
              }
            }
            """;

        var config = JsonSerializer.Deserialize<ManifestaConfig>(json, _options)!;

        config.Output.Title.Should().Be("CUSTOM TITLE");
        config.Output.SectionOrder.Should().Equal("Sales", "Accounts");
    }

    // ── output.format (polymorphic) ───────────────────────────────────────

    [Fact]
    public void Deserialize_FormatAbsent_DefaultsToMarkdownCommonMark()
    {
        var json = "{}";

        var config = JsonSerializer.Deserialize<ManifestaConfig>(json, _options)!;

        config.Output.Format.Should().BeOfType<MarkdownFormatConfig>();
        ((MarkdownFormatConfig)config.Output.Format).Dialect.Should().Be(MarkdownDialect.CommonMark);
    }

    [Fact]
    public void Deserialize_FormatMarkdownCommonMark_IsReadCorrectly()
    {
        var json = """
            {
              "output": {
                "format": {
                  "type": "markdown",
                  "dialect": "CommonMark"
                }
              }
            }
            """;

        var config = JsonSerializer.Deserialize<ManifestaConfig>(json, _options)!;

        config.Output.Format.Should().BeOfType<MarkdownFormatConfig>();
        ((MarkdownFormatConfig)config.Output.Format).Dialect.Should().Be(MarkdownDialect.CommonMark);
    }

    [Fact]
    public void Deserialize_FormatMarkdownAzureDevOps_IsReadCorrectly()
    {
        var json = """
            {
              "output": {
                "format": {
                  "type": "markdown",
                  "dialect": "AzureDevOps"
                }
              }
            }
            """;

        var config = JsonSerializer.Deserialize<ManifestaConfig>(json, _options)!;

        config.Output.Format.Should().BeOfType<MarkdownFormatConfig>();
        ((MarkdownFormatConfig)config.Output.Format).Dialect.Should().Be(MarkdownDialect.AzureDevOps);
    }

    [Fact]
    public void Deserialize_FormatMarkdown_DialectAbsent_DefaultsToCommonMark()
    {
        var json = """
            {
              "output": {
                "format": {
                  "type": "markdown"
                }
              }
            }
            """;

        var config = JsonSerializer.Deserialize<ManifestaConfig>(json, _options)!;

        config.Output.Format.Should().BeOfType<MarkdownFormatConfig>();
        ((MarkdownFormatConfig)config.Output.Format).Dialect.Should().Be(MarkdownDialect.CommonMark);
    }

    [Fact]
    public void Deserialize_FormatHtml_IsReadCorrectly()
    {
        var json = """
            {
              "output": {
                "format": {
                  "type": "html",
                  "template": "default"
                }
              }
            }
            """;

        var config = JsonSerializer.Deserialize<ManifestaConfig>(json, _options)!;

        config.Output.Format.Should().BeOfType<HtmlFormatConfig>();
        ((HtmlFormatConfig)config.Output.Format).Template.Should().Be("default");
    }

    [Fact]
    public void Deserialize_FormatPdf_IsReadCorrectly()
    {
        var json = """
            {
              "output": {
                "format": {
                  "type": "pdf"
                }
              }
            }
            """;

        var config = JsonSerializer.Deserialize<ManifestaConfig>(json, _options)!;

        config.Output.Format.Should().BeOfType<PdfFormatConfig>();
    }

    // ── paths.adapters ────────────────────────────────────────────────────

    [Fact]
    public void Deserialize_PathsAdapters_IsReadCorrectly()
    {
        var json = """
            {
              "paths": {
                "adapters": "./my-adapters"
              }
            }
            """;

        var config = JsonSerializer.Deserialize<ManifestaConfig>(json, _options)!;

        config.Paths.Adapters.Should().Be("./my-adapters");
    }

    [Fact]
    public void Deserialize_PathsAdaptersAbsent_DefaultsToAdaptersFolder()
    {
        var json = "{}";

        var config = JsonSerializer.Deserialize<ManifestaConfig>(json, _options)!;

        config.Paths.Adapters.Should().Be("./adapters");
    }

    // ── adapters section ──────────────────────────────────────────────────

    [Fact]
    public void Deserialize_AdaptersAbsent_IsNull()
    {
        var json = "{}";

        var config = JsonSerializer.Deserialize<ManifestaConfig>(json, _options)!;

        config.Adapters.Should().BeNull();
    }

    [Fact]
    public void Deserialize_Adapters_IsReadCorrectly()
    {
        var json = """
            {
              "adapters": {
                "enabled": ["tables-only", "syncra"],
                "outputDir": "./out/adapters",
                "strict": false
              }
            }
            """;

        var config = JsonSerializer.Deserialize<ManifestaConfig>(json, _options)!;

        config.Adapters.Should().NotBeNull();
        config.Adapters!.Enabled.Should().Equal("tables-only", "syncra");
        config.Adapters.OutputDir.Should().Be("./out/adapters");
        config.Adapters.Strict.Should().BeFalse();
    }

    [Fact]
    public void Deserialize_Adapters_StrictDefaultsToTrue()
    {
        var json = """
            {
              "adapters": {
                "outputDir": "./out"
              }
            }
            """;

        var config = JsonSerializer.Deserialize<ManifestaConfig>(json, _options)!;

        config.Adapters!.Strict.Should().BeTrue();
    }
}
