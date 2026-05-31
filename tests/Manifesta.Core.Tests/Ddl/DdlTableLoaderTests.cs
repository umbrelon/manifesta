using FluentAssertions;
using Manifesta.Core.Ddl;
using Xunit;

namespace Manifesta.Core.Tests.Ddl;

public class DdlTableLoaderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public DdlTableLoaderTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public void Load_SingleFile_ReturnsParsedTables()
    {
        var path = WriteFile("schema.sql", """
            CREATE TABLE public.customer (
                id   SERIAL PRIMARY KEY,
                name VARCHAR(100) NOT NULL
            );
            """);

        var tables = DdlTableLoader.Load([path], DbProvider.Postgres);

        tables.Should().HaveCount(1);
        tables[0].Name.Should().Be("public.customer");
        tables[0].Fields.Should().HaveCount(2);
    }

    [Fact]
    public void Load_MultipleFiles_MergesTablesFromAllFiles()
    {
        var path1 = WriteFile("a.sql", """
            CREATE TABLE public.orders (
                id INT PRIMARY KEY
            );
            """);
        var path2 = WriteFile("b.sql", """
            CREATE TABLE public.products (
                id INT PRIMARY KEY
            );
            """);

        var tables = DdlTableLoader.Load([path1, path2], DbProvider.Postgres);

        tables.Should().HaveCount(2);
        tables.Select(t => t.Name).Should().Contain(["public.orders", "public.products"]);
    }

    [Fact]
    public void Load_SqlServer_ParsesCorrectly()
    {
        var path = WriteFile("schema.sql", """
            CREATE TABLE dbo.Account (
                Id   INT IDENTITY(1,1) PRIMARY KEY,
                Name NVARCHAR(200) NOT NULL
            );
            """);

        var tables = DdlTableLoader.Load([path], DbProvider.SqlServer);

        tables.Should().HaveCount(1);
        tables[0].Name.Should().Be("dbo.Account");
    }

    // ── Error paths ───────────────────────────────────────────────────────────

    [Fact]
    public void Load_FileNotFound_ThrowsManifestaConfigException()
    {
        var act = () => DdlTableLoader.Load(["/nonexistent/schema.sql"], DbProvider.Postgres);

        act.Should().Throw<ManifestaConfigException>()
           .WithMessage("*DDL file not found*");
    }

    [Fact]
    public void Load_DuplicateTableAcrossFiles_ThrowsManifestaConfigException()
    {
        var path1 = WriteFile("a.sql", "CREATE TABLE public.customer (id INT PRIMARY KEY);");
        var path2 = WriteFile("b.sql", "CREATE TABLE public.customer (id INT PRIMARY KEY);");

        var act = () => DdlTableLoader.Load([path1, path2], DbProvider.Postgres);

        act.Should().Throw<ManifestaConfigException>()
           .WithMessage("*Duplicate table*public.customer*");
    }

    [Fact]
    public void Load_EmptyFileList_ReturnsEmptyList()
    {
        var tables = DdlTableLoader.Load([], DbProvider.Postgres);

        tables.Should().BeEmpty();
    }

    [Fact]
    public void Load_FileWithNoTables_ReturnsEmptyList()
    {
        var path = WriteFile("empty.sql", "-- no tables here\nSELECT 1;");

        var tables = DdlTableLoader.Load([path], DbProvider.Postgres);

        tables.Should().BeEmpty();
    }
}
