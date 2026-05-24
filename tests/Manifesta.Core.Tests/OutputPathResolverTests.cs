using FluentAssertions;
using Manifesta.Core;
using Xunit;

namespace Manifesta.Core.Tests;

/// <summary>
/// Covers all four scenarios from the spec's output flag precedence table.
/// </summary>
public sealed class OutputPathResolverTests
{
    private const string DefaultFileName  = "validation.json";
    private const string DefaultDirectory = "/default/dir";

    // ── Scenario 1: --output only ────────────────────────────────────────

    [Fact]
    public void OutputOnly_ReturnsExactPath()
    {
        var path = OutputPathResolver.Resolve(
            outputFlag:       "/explicit/path/out.json",
            outputDirFlag:    null,
            defaultFileName:  DefaultFileName,
            defaultDirectory: DefaultDirectory);

        path.Should().Be(Path.GetFullPath("/explicit/path/out.json"));
    }

    [Fact]
    public void OutputOnly_OutputDirIsIgnored()
    {
        var path = OutputPathResolver.Resolve(
            outputFlag:       "/explicit/out.json",
            outputDirFlag:    "/some/dir",          // should be ignored
            defaultFileName:  DefaultFileName,
            defaultDirectory: DefaultDirectory);

        path.Should().Be(Path.GetFullPath("/explicit/out.json"));
    }

    // ── Scenario 2: --output-dir only ────────────────────────────────────

    [Fact]
    public void OutputDirOnly_UsesDefaultFileName()
    {
        var path = OutputPathResolver.Resolve(
            outputFlag:       null,
            outputDirFlag:    "/reports",
            defaultFileName:  DefaultFileName,
            defaultDirectory: DefaultDirectory);

        path.Should().Be(Path.GetFullPath("/reports/validation.json"));
    }

    // ── Scenario 3: both --output and --output-dir ────────────────────────

    [Fact]
    public void BothFlags_OutputWinsForItsFile()
    {
        var path = OutputPathResolver.Resolve(
            outputFlag:       "/specific/out.json",
            outputDirFlag:    "/other/dir",
            defaultFileName:  DefaultFileName,
            defaultDirectory: DefaultDirectory);

        // --output wins
        path.Should().Be(Path.GetFullPath("/specific/out.json"));
    }

    // ── Scenario 4: neither flag ──────────────────────────────────────────

    [Fact]
    public void NeitherFlag_UsesCommandDefault()
    {
        var path = OutputPathResolver.Resolve(
            outputFlag:       null,
            outputDirFlag:    null,
            defaultFileName:  DefaultFileName,
            defaultDirectory: DefaultDirectory);

        path.Should().Be(Path.GetFullPath(Path.Combine(DefaultDirectory, DefaultFileName)));
    }

    [Fact]
    public void NeitherFlag_EmptyStrings_TreatedAsNotSet()
    {
        var path = OutputPathResolver.Resolve(
            outputFlag:       "",
            outputDirFlag:    "   ",
            defaultFileName:  DefaultFileName,
            defaultDirectory: DefaultDirectory);

        path.Should().Be(Path.GetFullPath(Path.Combine(DefaultDirectory, DefaultFileName)));
    }

    // ── ResolveDirectory ─────────────────────────────────────────────────

    [Fact]
    public void ResolveDirectory_WithFlag_ReturnsFlag()
    {
        var dir = OutputPathResolver.ResolveDirectory("/custom/dir", "docs");
        dir.Should().Be(Path.GetFullPath("/custom/dir"));
    }

    [Fact]
    public void ResolveDirectory_WithoutFlag_ReturnsDefault()
    {
        var dir = OutputPathResolver.ResolveDirectory(null, "docs");
        dir.Should().Be(Path.GetFullPath("docs"));
    }
}
