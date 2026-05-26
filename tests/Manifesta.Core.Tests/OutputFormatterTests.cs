using FluentAssertions;
using Manifesta.Core;
using Xunit;

namespace Manifesta.Core.Tests;

/// <summary>
/// Tests for <see cref="OutputFormatter"/>.
/// Covers JSON/YAML/human formatting, quiet/verbose modes, and error output.
/// </summary>
public sealed class OutputFormatterTests
{
    private sealed record TestObject(string FirstName, string LastName, int RecordCount);

    // ── Format method ──────────────────────────────────────────────────────

    [Fact]
    public void Format_JsonFormat_SerializesAsJson()
    {
        var obj = new TestObject("John", "Doe", 42);

        var result = OutputFormatter.Format(obj, "json");

        result.Should().Contain("firstName").And.Contain("John");
        result.Should().Contain("lastName").And.Contain("Doe");
        result.Should().Contain("recordCount").And.Contain("42");
    }

    [Fact]
    public void Format_JsonFormat_CamelCaseNaming()
    {
        var obj = new TestObject("John", "Doe", 1);

        var result = OutputFormatter.Format(obj, "json");

        result.Should().Contain("firstName").And.Contain("lastName").And.Contain("recordCount");
        result.Should().NotContain("FirstName").And.NotContain("LastName").And.NotContain("RecordCount");
    }

    [Fact]
    public void Format_JsonFormat_PrettyPrinted()
    {
        var obj = new TestObject("John", "Doe", 1);

        var result = OutputFormatter.Format(obj, "json");

        result.Should().Contain("\n").And.Contain("  ");
    }

    [Fact]
    public void Format_YamlFormat_SerializesAsYaml()
    {
        var obj = new TestObject("John", "Doe", 42);

        var result = OutputFormatter.Format(obj, "yaml");

        result.Should().Contain("firstName:").And.Contain("John");
        result.Should().Contain("lastName:").And.Contain("Doe");
        result.Should().Contain("recordCount:").And.Contain("42");
    }

    [Fact]
    public void Format_YamlFormat_CamelCaseNaming()
    {
        var obj = new TestObject("John", "Doe", 1);

        var result = OutputFormatter.Format(obj, "yaml");

        result.Should().Contain("firstName:").And.Contain("lastName:").And.Contain("recordCount:");
    }

    [Fact]
    public void Format_HumanFormat_CallsToString()
    {
        var obj = new TestObject("John", "Doe", 42);

        var result = OutputFormatter.Format(obj, "human");

        result.Should().Be(obj.ToString());
    }

    [Fact]
    public void Format_InvalidFormat_FallsBackToToString()
    {
        var obj = new TestObject("John", "Doe", 42);

        var result = OutputFormatter.Format(obj, "unknown-format");

        result.Should().Be(obj.ToString());
    }

    [Theory]
    [InlineData("json")]
    [InlineData("JSON")]
    [InlineData("Json")]
    public void Format_CaseInsensitive_JsonFormat(string format)
    {
        var result = OutputFormatter.Format(new TestObject("John", "Doe", 1), format);

        result.Should().Contain("firstName");
    }

    [Theory]
    [InlineData("yaml")]
    [InlineData("YAML")]
    [InlineData("Yaml")]
    public void Format_CaseInsensitive_YamlFormat(string format)
    {
        var result = OutputFormatter.Format(new TestObject("John", "Doe", 1), format);

        result.Should().Contain("firstName:");
    }

    [Fact]
    public void Format_NullObject_ReturnsNullJson()
    {
        var result = OutputFormatter.Format(null, "json");

        result.Should().Be("null");
    }

    [Fact]
    public void Format_EmptyString_ReturnsEmptyStringJson()
    {
        var result = OutputFormatter.Format("", "json");

        result.Should().Be("\"\"");
    }

    // ── WriteLine method ───────────────────────────────────────────────────

    [Fact]
    public void WriteLine_WithoutQuiet_WritesMessage()
    {
        var output = new StringWriter();

        OutputFormatter.WriteLine("test message", quiet: false, writer: output);

        output.ToString().Should().Contain("test message").And.EndWith("\n");
    }

    [Fact]
    public void WriteLine_WithQuiet_SuppressesOutput()
    {
        var output = new StringWriter();

        OutputFormatter.WriteLine("test message", quiet: true, writer: output);

        output.ToString().Should().Be("");
    }

    [Fact]
    public void WriteLine_NullWriterDefaults_ToConsoleOut()
    {
        var action = () => OutputFormatter.WriteLine("test", quiet: true, writer: null);

        action.Should().NotThrow();
    }

    [Fact]
    public void WriteLine_MultipleLines_AllSuppressedInQuietMode()
    {
        var output = new StringWriter();

        OutputFormatter.WriteLine("line 1", quiet: true, writer: output);
        OutputFormatter.WriteLine("line 2", quiet: true, writer: output);
        OutputFormatter.WriteLine("line 3", quiet: true, writer: output);

        output.ToString().Should().Be("");
    }

    // ── WriteVerbose method ────────────────────────────────────────────────

    [Fact]
    public void WriteVerbose_WithVerbose_WritesMessage()
    {
        var output = new StringWriter();

        OutputFormatter.WriteVerbose("verbose message", verbose: true, writer: output);

        output.ToString().Should().StartWith("[verbose] ").And.Contain("verbose message");
    }

    [Fact]
    public void WriteVerbose_WithoutVerbose_SuppressesOutput()
    {
        var output = new StringWriter();

        OutputFormatter.WriteVerbose("verbose message", verbose: false, writer: output);

        output.ToString().Should().Be("");
    }

    [Fact]
    public void WriteVerbose_NullWriterDefaults_ToConsoleOut()
    {
        var action = () => OutputFormatter.WriteVerbose("test", verbose: true, writer: null);

        action.Should().NotThrow();
    }

    // ── WriteError method ──────────────────────────────────────────────────

    [Fact]
    public void WriteError_WritesErrorMessage()
    {
        var output = new StringWriter();

        OutputFormatter.WriteError("something went wrong", output);

        output.ToString().Should().StartWith("Error: ").And.Contain("something went wrong");
    }

    [Fact]
    public void WriteError_IgnoresQuietFlag()
    {
        // Errors bypass --quiet; tested by calling WriteError and asserting output appears
        var output = new StringWriter();

        OutputFormatter.WriteError("critical error", output);

        output.ToString().Should().Contain("Error:").And.Contain("critical error");
    }

    [Fact]
    public void WriteError_NullWriterDefaults_ToConsoleError()
    {
        var action = () => OutputFormatter.WriteError("test", null);

        action.Should().NotThrow();
    }

    [Fact]
    public void WriteError_MultipleErrors_AllWritten()
    {
        var output = new StringWriter();

        OutputFormatter.WriteError("error 1", output);
        OutputFormatter.WriteError("error 2", output);
        OutputFormatter.WriteError("error 3", output);

        var text = output.ToString();
        text.Should().Contain("error 1").And.Contain("error 2").And.Contain("error 3");
    }

    // ── Complex objects ────────────────────────────────────────────────────

    [Fact]
    public void Format_ComplexNestedObject_SerializesCorrectly()
    {
        var nested = new
        {
            user = new { firstName = "John", lastName = "Doe" },
            metadata = new { version = 1, tags = new[] { "a", "b", "c" } }
        };

        var result = OutputFormatter.Format(nested, "json");

        result.Should().Contain("user")
            .And.Contain("firstName")
            .And.Contain("metadata")
            .And.Contain("version");
    }

    [Fact]
    public void Format_EmptyCollection_SerializesCorrectly()
    {
        var obj = new { items = Array.Empty<string>() };

        var result = OutputFormatter.Format(obj, "json");

        result.Should().Contain("items").And.Contain("[]");
    }
}
