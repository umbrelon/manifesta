using FluentAssertions;
using Manifesta.Core;
using Manifesta.Core.Pipeline;
using Xunit;

namespace Manifesta.Core.Tests;

public sealed class ExitCodeResolverTests
{
    // ── No issues ────────────────────────────────────────────────────────

    [Fact]
    public void NoIssues_ReturnsSuccess()
    {
        var result = ValidationResult.Empty;
        ExitCodeResolver.FromValidationResult(result).Should().Be(ExitCode.Success);
    }

    // ── Errors ───────────────────────────────────────────────────────────

    [Fact]
    public void HasErrors_ReturnsValidationErrors()
    {
        var result = ValidationResult.FromIssues([Error("MANIFESTA-1001", "bad FK")]);
        ExitCodeResolver.FromValidationResult(result).Should().Be(ExitCode.ValidationErrors);
    }

    [Fact]
    public void HasErrors_WithWarnOnly_StillReturnsValidationErrors()
    {
        var result = ValidationResult.FromIssues([Error("MANIFESTA-1001", "bad FK")]);
        ExitCodeResolver.FromValidationResult(result, warnOnly: true).Should().Be(ExitCode.ValidationErrors);
    }

    [Fact]
    public void HasErrors_WithStrict_ReturnsValidationErrors()
    {
        var result = ValidationResult.FromIssues([Error("MANIFESTA-1001", "bad FK")]);
        ExitCodeResolver.FromValidationResult(result, strict: true).Should().Be(ExitCode.ValidationErrors);
    }

    // ── Warnings ─────────────────────────────────────────────────────────

    [Fact]
    public void HasWarnings_DefaultBehaviour_ReturnsSuccess()
    {
        var result = ValidationResult.FromIssues([Warning("MANIFESTA-W001", "nullable match column")]);
        ExitCodeResolver.FromValidationResult(result).Should().Be(ExitCode.Success);
    }

    [Fact]
    public void HasWarnings_Strict_ReturnsValidationErrors()
    {
        var result = ValidationResult.FromIssues([Warning("MANIFESTA-W001", "nullable match column")]);
        ExitCodeResolver.FromValidationResult(result, strict: true).Should().Be(ExitCode.ValidationErrors);
    }

    [Fact]
    public void HasWarnings_WarnOnly_ReturnsSuccess()
    {
        var result = ValidationResult.FromIssues([Warning("MANIFESTA-W001", "nullable match column")]);
        ExitCodeResolver.FromValidationResult(result, warnOnly: true).Should().Be(ExitCode.Success);
    }

    // ── Exception mapping ─────────────────────────────────────────────────

    [Fact]
    public void SchemaException_ReturnsFatalSchemaErrors()
    {
        ExitCodeResolver.FromException(new ManifestaSchemException("bad json"))
            .Should().Be(ExitCode.FatalSchemaErrors);
    }

    [Fact]
    public void ConfigException_ReturnsConfigOrInvocationError()
    {
        ExitCodeResolver.FromException(new ManifestaConfigException("bad config"))
            .Should().Be(ExitCode.ConfigOrInvocationError);
    }

    [Fact]
    public void ReleaseException_ReturnsReleaseRepoFailure()
    {
        ExitCodeResolver.FromException(new ManifestaReleaseException("git push failed"))
            .Should().Be(ExitCode.ReleaseRepoFailure);
    }

    [Fact]
    public void UnknownException_ReturnsInternalError()
    {
        ExitCodeResolver.FromException(new InvalidOperationException("oops"))
            .Should().Be(ExitCode.InternalError);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static ValidationIssue Error(string code, string message) => new()
    {
        Severity = ValidationSeverity.Error,
        Code     = code,
        Message  = message,
    };

    private static ValidationIssue Warning(string code, string message) => new()
    {
        Severity = ValidationSeverity.Warning,
        Code     = code,
        Message  = message,
    };
}
