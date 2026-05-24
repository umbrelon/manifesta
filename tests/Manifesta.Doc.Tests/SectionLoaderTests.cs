using FluentAssertions;
using Manifesta.Core;
using Manifesta.Doc;
using Xunit;

namespace Manifesta.Doc.Tests;

/// <summary>
/// Tests for <see cref="SectionLoader"/>.
/// Covers loading section definitions from JSON files.
/// </summary>
public sealed class SectionLoaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SectionLoader _loader = new();

    public SectionLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"section_loader_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task LoadAsync_NonExistentDirectory_ReturnsEmptyList()
    {
        var result = await _loader.LoadAsync(Path.Combine(_tempDir, "nonexistent"));

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_EmptyDirectory_ReturnsEmptyList()
    {
        var result = await _loader.LoadAsync(_tempDir);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_ValidSectionJson_LoadsSection()
    {
        var sectionJson = @"{
  ""$schemaVersion"": ""1.0"",
  ""name"": ""Accounts"",
  ""description"": ""User account management tables"",
  ""tables"": [
    ""dbo.User"",
    ""dbo.Account"",
    ""dbo.Permission""
  ]
}";
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "accounts.json"), sectionJson);

        var result = await _loader.LoadAsync(_tempDir);

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Accounts");
        result[0].Description.Should().Be("User account management tables");
        result[0].Tables.Should().HaveCount(3)
            .And.Contain(new[] { "dbo.User", "dbo.Account", "dbo.Permission" });
    }

    [Fact]
    public async Task LoadAsync_MultipleSectionFiles_LoadsAll()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "accounts.json"), @"{
  ""name"": ""Accounts"",
  ""description"": ""Account management"",
  ""tables"": [""dbo.User""]
}");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "orders.json"), @"{
  ""name"": ""Orders"",
  ""description"": ""Order processing"",
  ""tables"": [""dbo.Order"", ""dbo.OrderLine""]
}");

        var result = await _loader.LoadAsync(_tempDir);

        result.Should().HaveCount(2);
        result.Select(s => s.Name).Should().Contain(new[] { "Accounts", "Orders" });
    }

    [Fact]
    public async Task LoadAsync_InvalidJson_ThrowsManifestaException()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "invalid.json"), "{ invalid json }");

        var act = () => _loader.LoadAsync(_tempDir);

        await act.Should().ThrowAsync<ManifestaSchemException>();
    }

    [Fact]
    public async Task LoadAsync_SourceFilePopulated()
    {
        var sectionFile = Path.Combine(_tempDir, "test.json");
        await File.WriteAllTextAsync(sectionFile, @"{
  ""name"": ""Test Section"",
  ""tables"": []
}");

        var result = await _loader.LoadAsync(_tempDir);

        result[0].SourceFile.Should().Be(sectionFile);
    }

    [Fact]
    public async Task LoadAsync_ResultsSorted_ByFilePath()
    {
        foreach (var file in new[] { "zebra.json", "apple.json", "middle.json" })
        {
            var name = Path.GetFileNameWithoutExtension(file);
            await File.WriteAllTextAsync(Path.Combine(_tempDir, file), $@"{{
  ""name"": ""{name}"",
  ""tables"": []
}}");
        }

        var result = await _loader.LoadAsync(_tempDir);

        result.Select(s => s.Name).Should().ContainInOrder("apple", "middle", "zebra");
    }
}
