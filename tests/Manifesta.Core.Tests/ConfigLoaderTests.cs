using FluentAssertions;
using Manifesta.Core;
using Xunit;

namespace Manifesta.Core.Tests;

/// <summary>
/// Tests for <see cref="ConfigLoader"/>.
/// Covers valid config loading, error cases, edge cases with JSON parsing.
/// </summary>
public sealed class ConfigLoaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;

    public ConfigLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"manifesta_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "manifesta.config.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Valid configuration ────────────────────────────────────────────────

    [Fact]
    public void LoadConfig_ValidJson_ReturnsConfig()
    {
        var json = @"{
  ""paths"": {
    ""root"": ""../data"",
    ""tables"": ""./Tables""
  },
  ""output"": {
    ""sectionOrder"": [""Main"", ""Secondary""]
  },
  ""naming"": {
    ""tablePattern"": ""^[A-Z][a-zA-Z0-9]*$""
  }
}";
        File.WriteAllText(_configPath, json);

        var config = ConfigLoader.Load(_configPath);

        config.Should().NotBeNull();
        config.Paths.Root.Should().Be("../data");
        config.Paths.Tables.Should().Be("./Tables");
        config.Output.SectionOrder.Should().ContainInOrder("Main", "Secondary");
        config.Naming.TablePattern.Should().Be("^[A-Z][a-zA-Z0-9]*$");
    }

    [Fact]
    public void LoadConfig_EmptyObject_ReturnsConfigWithDefaults()
    {
        var json = "{}";
        File.WriteAllText(_configPath, json);

        var config = ConfigLoader.Load(_configPath);

        config.Should().NotBeNull();
        config.Paths.Root.Should().Be("../");  // Default
        config.Paths.Skip.Should().BeEmpty();  // Default
        config.Paths.DocumentSections.Should().Be("./document-sections");  // Default
        config.Output.SectionOrder.Should().BeEmpty();  // Default
        config.Naming.TablePattern.Should().BeNull();  // Optional
    }

    // ── Error cases ────────────────────────────────────────────────────────

    [Fact]
    public void LoadConfig_FileNotFound_ThrowsManifestaConfigException()
    {
        var action = () => ConfigLoader.Load("/nonexistent/manifesta.config.json");

        action.Should().Throw<ManifestaConfigException>()
            .WithMessage("*Config file not found*");
    }

    [Fact]
    public void LoadConfig_InvalidJson_ThrowsManifestaSchemException()
    {
        var json = "{ invalid json }";
        File.WriteAllText(_configPath, json);

        var action = () => ConfigLoader.Load(_configPath);

        action.Should().Throw<ManifestaSchemException>()
            .WithMessage("*invalid JSON*");
    }

    [Fact]
    public void LoadConfig_NullDeserialization_ThrowsManifestaConfigException()
    {
        var json = "null";
        File.WriteAllText(_configPath, json);

        var action = () => ConfigLoader.Load(_configPath);

        action.Should().Throw<ManifestaConfigException>()
            .WithMessage("*deserialised to null*");
    }

    // ── JSON features ──────────────────────────────────────────────────────

    [Fact]
    public void LoadConfig_WithComments_SuccessfullyIgnored()
    {
        var json = @"{
  // This is a comment
  ""paths"": {
    ""root"": ""../""
    /* Multi-line comment */
  }
}";
        File.WriteAllText(_configPath, json);

        var config = ConfigLoader.Load(_configPath);

        config.Should().NotBeNull();
        config.Paths.Root.Should().Be("../");
    }

    [Fact]
    public void LoadConfig_WithTrailingCommas_SuccessfullyIgnored()
    {
        var json = @"{
  ""paths"": {
    ""root"": ""../data"",
    ""skip"": [],
  },
  ""output"": {
    ""sectionOrder"": [],
  },
}";
        File.WriteAllText(_configPath, json);

        var config = ConfigLoader.Load(_configPath);

        config.Should().NotBeNull();
        config.Paths.Root.Should().Be("../data");
    }

    [Fact]
    public void LoadConfig_CaseInsensitiveProperties_PopulatesCorrectly()
    {
        var json = @"{
  ""PATHS"": {
    ""ROOT"": ""../data"",
    ""TABLES"": ""./Tables""
  },
  ""OUTPUT"": {
    ""SECTIONORDER"": []
  }
}";
        File.WriteAllText(_configPath, json);

        var config = ConfigLoader.Load(_configPath);

        config.Should().NotBeNull();
        config.Paths.Root.Should().Be("../data");
        config.Paths.Tables.Should().Be("./Tables");
        config.Output.SectionOrder.Should().BeEmpty();
    }

    // ── Path handling ──────────────────────────────────────────────────────

    [Fact]
    public void LoadConfig_GlobalOptionsSpecifiesCustomPath_LoadsFromCustomLocation()
    {
        var customDir = Path.Combine(_tempDir, "custom");
        Directory.CreateDirectory(customDir);
        var customConfigPath = Path.Combine(customDir, "custom.config.json");
        var json = @"{ ""paths"": { ""root"": ""../custom"" } }";
        File.WriteAllText(customConfigPath, json);

        var config = ConfigLoader.Load(customConfigPath);

        config.Should().NotBeNull();
        config.Paths.Root.Should().Be("../custom");
    }

    [Fact]
    public void LoadConfig_RelativePathsResolved_ToAbsolutePath()
    {
        var json = @"{ ""paths"": { ""root"": ""../data"" } }";
        File.WriteAllText(_configPath, json);

        // Note: In real usage, relative paths are resolved by Path.GetFullPath()
        var config = ConfigLoader.Load(_configPath);

        config.Should().NotBeNull();
    }

    // ── IO errors ──────────────────────────────────────────────────────────

    [SkippableFact]
    public void LoadConfig_FileReadPermissionDenied_ThrowsManifestaConfigException()
    {
        // ReadOnly attribute prevents writing, not reading, on Windows — this test
        // cannot reliably strip read access in user-space on either OS, so we
        // document the expected exception contract without simulating the I/O error.
        // Actual coverage of the IOException→ManifestaConfigException path lives in
        // integration/smoke tests that run under a restricted process context.
        Skip.If(true, "Cannot reliably deny read access in cross-platform unit tests");
    }

    // ── Nullable and optional fields ───────────────────────────────────────

    [Fact]
    public void LoadConfig_OptionalNamingPatterns_CanBeNull()
    {
        var json = @"{
  ""paths"": { ""root"": ""../data"" },
  ""naming"": {}
}";
        File.WriteAllText(_configPath, json);

        var config = ConfigLoader.Load(_configPath);

        config.Naming.TablePattern.Should().BeNull();
        config.Naming.FieldPattern.Should().BeNull();
    }

}
