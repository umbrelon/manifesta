using FluentAssertions;
using Manifesta.Core;
using Manifesta.Core.IR;
using Manifesta.Core.Pipeline;
using System.Text.Json;
using Xunit;

namespace Manifesta.Core.Tests;

/// <summary>
/// Tests for <see cref="TableLoader"/>.
/// Covers loading table definitions from JSON files, error handling, and determinism.
/// </summary>
public sealed class TableLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public TableLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"table_loader_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Valid loading ──────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_SingleTableJson_LoadsAndReturnsTableDefinition()
    {
        var tableDir = Path.Combine(_tempDir, "tables");
        Directory.CreateDirectory(tableDir);

        var tableJson = """
{
  "name": "Users",
  "description": "User accounts",
  "fields": [
    { "name": "id", "type": "int", "nullable": false },
    { "name": "email", "type": "varchar(255)", "nullable": false }
  ],
  "primaryKey": ["id"]
}
""";
        File.WriteAllText(Path.Combine(tableDir, "Users.json"), tableJson);

        var loader = new TableLoader();
        var tables = await loader.LoadAsync(tableDir, CancellationToken.None);

        tables.Should().HaveCount(1);
        tables[0].Name.Should().Be("Users");
        tables[0].Fields.Should().HaveCount(2);
        tables[0].PrimaryKey.Should().ContainSingle().Which.Should().Be("id");
    }

    [Fact]
    public async Task LoadAsync_MultipleTableFiles_ReturnsAllTables()
    {
        var tableDir = Path.Combine(_tempDir, "tables");
        Directory.CreateDirectory(tableDir);

        CreateTableFile(tableDir, "Users.json", "Users", new[] { "id", "email" });
        CreateTableFile(tableDir, "Products.json", "Products", new[] { "id", "name" });
        CreateTableFile(tableDir, "Orders.json", "Orders", new[] { "id", "userId" });

        var loader = new TableLoader();
        var tables = await loader.LoadAsync(tableDir, CancellationToken.None);

        tables.Should().HaveCount(3);
        tables.Select(t => t.Name).Should().Contain(new[] { "Users", "Products", "Orders" });
    }

    [Fact]
    public async Task LoadAsync_ResultsSortedByFilePath_EnsuresDeterminism()
    {
        var tableDir = Path.Combine(_tempDir, "tables");
        Directory.CreateDirectory(tableDir);

        // Create files in random order
        CreateTableFile(tableDir, "zebra.json", "Zebra");
        CreateTableFile(tableDir, "apple.json", "Apple");
        CreateTableFile(tableDir, "monkey.json", "Monkey");

        var loader = new TableLoader();
        var tables = await loader.LoadAsync(tableDir, CancellationToken.None);

        // Results should be sorted by file path regardless of creation order
        var filePaths = tables.Select(t => t.SourceFile).ToList();
        filePaths.Should().BeInAscendingOrder("determinism requires sorted results");
    }

    [Fact]
    public async Task LoadAsync_NestedDirectories_FindsAllTableFiles()
    {
        var rootDir = Path.Combine(_tempDir, "root");
        Directory.CreateDirectory(rootDir);

        var subDir1 = Path.Combine(rootDir, "module1", "tables");
        var subDir2 = Path.Combine(rootDir, "module2", "tables");
        Directory.CreateDirectory(subDir1);
        Directory.CreateDirectory(subDir2);

        CreateTableFile(subDir1, "Table1.json", "Table1");
        CreateTableFile(subDir2, "Table2.json", "Table2");

        var loader = new TableLoader();
        // TableLoader should recursively find all tables/ directories
        var tables = await loader.LoadAsync(rootDir, CancellationToken.None);

        tables.Should().HaveCount(2);
    }

    // ── Error cases ────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_MissingDirectory_ReturnsEmptyList()
    {
        var nonexistentDir = Path.Combine(_tempDir, "nonexistent");
        var loader = new TableLoader();

        var tables = await loader.LoadAsync(nonexistentDir, CancellationToken.None);

        tables.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_InvalidJson_ThrowsException()
    {
        var tableDir = Path.Combine(_tempDir, "tables");
        Directory.CreateDirectory(tableDir);

        File.WriteAllText(Path.Combine(tableDir, "invalid.json"), "{ invalid json }");

        var loader = new TableLoader();

        var action = async () => await loader.LoadAsync(tableDir, CancellationToken.None);

        await action.Should().ThrowAsync<ManifestaSchemException>();
    }

    [Fact]
    public async Task LoadAsync_CamelCaseDeserialization_PropertiesMatched()
    {
        var tableDir = Path.Combine(_tempDir, "tables");
        Directory.CreateDirectory(tableDir);

        var tableJson = """
{
  "name": "Users",
  "fields": [
    { "name": "userId", "type": "int", "nullable": false }
  ],
  "primaryKey": ["userId"]
}
""";
        File.WriteAllText(Path.Combine(tableDir, "Users.json"), tableJson);

        var loader = new TableLoader();
        var tables = await loader.LoadAsync(tableDir, CancellationToken.None);

        tables.Should().HaveCount(1);
        tables[0].Fields.Should().HaveCount(1);
        tables[0].Fields[0].Name.Should().Be("userId");
    }

    // ── Edge cases ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_EmptyDirectory_ReturnsEmptyList()
    {
        var emptyDir = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(emptyDir);

        var loader = new TableLoader();
        var tables = await loader.LoadAsync(emptyDir, CancellationToken.None);

        tables.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_MinimalTableDefinition_Deserializes()
    {
        var tableDir = Path.Combine(_tempDir, "tables");
        Directory.CreateDirectory(tableDir);

        var minimalJson = """
{
  "name": "MinimalTable",
  "fields": []
}
""";
        File.WriteAllText(Path.Combine(tableDir, "Minimal.json"), minimalJson);

        var loader = new TableLoader();
        var tables = await loader.LoadAsync(tableDir, CancellationToken.None);

        tables.Should().HaveCount(1);
        tables[0].Name.Should().Be("MinimalTable");
        tables[0].Fields.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_TableWithForeignKeys_DeserializesCorrectly()
    {
        var tableDir = Path.Combine(_tempDir, "tables");
        Directory.CreateDirectory(tableDir);

        var tableJson = """
{
  "name": "Orders",
  "fields": [
    { "name": "orderId", "type": "int", "nullable": false },
    { "name": "userId", "type": "int", "nullable": false }
  ],
  "primaryKey": ["orderId"],
  "foreignKeys": [
    {
      "sourceField": "userId",
      "targetTable": "Users",
      "targetField": "id",
      "cascadeDelete": true
    }
  ]
}
""";
        File.WriteAllText(Path.Combine(tableDir, "Orders.json"), tableJson);

        var loader = new TableLoader();
        var tables = await loader.LoadAsync(tableDir, CancellationToken.None);

        tables.Should().HaveCount(1);
        var orders = tables[0];
        orders.ForeignKeys.Should().HaveCount(1);
        orders.ForeignKeys[0].SourceField.Should().Be("userId");
        orders.ForeignKeys[0].TargetTable.Should().Be("Users");
        orders.ForeignKeys[0].CascadeDelete.Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_CancellationRequested_StopsLoading()
    {
        var tableDir = Path.Combine(_tempDir, "tables");
        Directory.CreateDirectory(tableDir);

        CreateTableFile(tableDir, "Table1.json", "Table1");
        CreateTableFile(tableDir, "Table2.json", "Table2");

        var loader = new TableLoader();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var action = async () => await loader.LoadAsync(tableDir, cts.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task LoadAsync_LargeNumberOfTables_LoadsAll()
    {
        var tableDir = Path.Combine(_tempDir, "tables");
        Directory.CreateDirectory(tableDir);

        // Create 100 table files
        for (int i = 0; i < 100; i++)
        {
            CreateTableFile(tableDir, $"Table{i:D3}.json", $"Table{i:D3}");
        }

        var loader = new TableLoader();
        var tables = await loader.LoadAsync(tableDir, CancellationToken.None);

        tables.Should().HaveCount(100);
        tables.Select(t => t.Name).Should().AllSatisfy(n => n.Should().StartWith("Table"));
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static void CreateTableFile(string dir, string filename, string tableName, string[]? fieldNames = null)
    {
        var fields = fieldNames?.Select((n, i) => new
        {
            name = n,
            type = "varchar(255)",
            nullable = false
        }).ToArray() ?? Array.Empty<object>();

        var primaryKey = fieldNames?.Where(f => f == "id").ToArray() ?? Array.Empty<string>();

        var table = new
        {
            name = tableName,
            fields,
            primaryKey
        };

        var json = JsonSerializer.Serialize(table, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(dir, filename), json);
    }
}
