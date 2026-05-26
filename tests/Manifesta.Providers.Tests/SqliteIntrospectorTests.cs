using FluentAssertions;
using Manifesta.Providers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Manifesta.Providers.Tests;

/// <summary>
/// Integration tests for <see cref="SqliteDatabaseIntrospector"/> using an in-memory SQLite database.
/// Each test class instance gets its own named in-memory database; the keep-alive connection keeps
/// it alive until the test disposes it.
/// </summary>
public sealed class SqliteIntrospectorTests : IDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;

    public SqliteIntrospectorTests()
    {
        var dbName = Guid.NewGuid().ToString("N");
        _connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    public void Dispose() => _keepAlive.Dispose();

    private void Exec(string sql)
    {
        using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private SqliteDatabaseIntrospector Introspector() => new(_connectionString);

    // ── basic column / PK detection ───────────────────────────────────────

    [Fact]
    public async Task IntrospectTablesOnlyAsync_SimpleTable_ReturnsColumnsAndPrimaryKey()
    {
        Exec("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL, email TEXT)");

        var tables = await Introspector().IntrospectTablesOnlyAsync();

        tables.Should().ContainSingle();
        var t = tables[0];
        t.Name.Should().Be("users");
        t.PrimaryKey.Should().ContainSingle().Which.Should().Be("id");

        var name  = t.Fields.Single(f => f.Name == "name");
        var email = t.Fields.Single(f => f.Name == "email");
        name.Nullable.Should().BeFalse();
        email.Nullable.Should().BeTrue();
    }

    [Fact]
    public async Task IntrospectTablesOnlyAsync_CompositePrimaryKey_CapturesAllKeyColumns()
    {
        Exec(@"
            CREATE TABLE order_items (
                order_id   INTEGER NOT NULL,
                product_id INTEGER NOT NULL,
                qty        INTEGER NOT NULL,
                PRIMARY KEY (order_id, product_id)
            )");

        var tables = await Introspector().IntrospectTablesOnlyAsync();

        var t = tables.Should().ContainSingle().Subject;
        t.PrimaryKey.Should().BeEquivalentTo(["order_id", "product_id"], o => o.WithStrictOrdering());
    }

    // ── foreign key detection ─────────────────────────────────────────────

    [Fact]
    public async Task IntrospectTablesOnlyAsync_ForeignKey_IsCaptured()
    {
        Exec("CREATE TABLE categories (id INTEGER PRIMARY KEY, label TEXT NOT NULL)");
        Exec(@"
            CREATE TABLE products (
                id          INTEGER PRIMARY KEY,
                category_id INTEGER NOT NULL REFERENCES categories(id)
            )");

        var tables  = await Introspector().IntrospectTablesOnlyAsync();
        var product = tables.Single(t => t.Name == "products");

        product.ForeignKeys.Should().ContainSingle();
        var fk = product.ForeignKeys[0];
        fk.SourceField.Should().Be("category_id");
        fk.TargetTable.Should().Be("categories");
        fk.TargetField.Should().Be("id");
        fk.CascadeDelete.Should().BeFalse();
    }

    [Fact]
    public async Task IntrospectTablesOnlyAsync_ForeignKeyWithCascade_DetectsCascadeDelete()
    {
        Exec("CREATE TABLE parents (id INTEGER PRIMARY KEY)");
        Exec(@"
            CREATE TABLE children (
                id        INTEGER PRIMARY KEY,
                parent_id INTEGER NOT NULL REFERENCES parents(id) ON DELETE CASCADE
            )");

        var tables = await Introspector().IntrospectTablesOnlyAsync();
        var child  = tables.Single(t => t.Name == "children");

        child.ForeignKeys.Should().ContainSingle()
            .Which.CascadeDelete.Should().BeTrue();
    }

    // ── index / unique constraint detection ───────────────────────────────

    [Fact]
    public async Task IntrospectTablesOnlyAsync_RegularIndex_IsCaptured()
    {
        Exec("CREATE TABLE events (id INTEGER PRIMARY KEY, name TEXT NOT NULL, ts INTEGER)");
        Exec("CREATE INDEX ix_events_ts ON events(ts)");

        var tables = await Introspector().IntrospectTablesOnlyAsync();
        var t      = tables.Single(t => t.Name == "events");

        t.Indexes.Should().ContainSingle()
            .Which.Name.Should().Be("ix_events_ts");
        t.Indexes[0].Columns.Should().ContainSingle().Which.Should().Be("ts");
        t.Indexes[0].IsUnique.Should().BeFalse();
    }

    [Fact]
    public async Task IntrospectTablesOnlyAsync_UniqueIndex_IsReportedAsUniqueConstraint()
    {
        Exec("CREATE TABLE accounts (id INTEGER PRIMARY KEY, email TEXT NOT NULL)");
        Exec("CREATE UNIQUE INDEX uq_accounts_email ON accounts(email)");

        var tables = await Introspector().IntrospectTablesOnlyAsync();
        var t      = tables.Single(t => t.Name == "accounts");

        t.UniqueConstraints.Should().ContainSingle()
            .Which.Name.Should().Be("uq_accounts_email");
        t.UniqueConstraints[0].Columns.Should().ContainSingle().Which.Should().Be("email");
        t.Indexes.Should().BeEmpty();
    }

    // ── view detection ────────────────────────────────────────────────────

    [Fact]
    public async Task IntrospectViewsOnlyAsync_SimpleView_IsReturned()
    {
        Exec("CREATE TABLE items (id INTEGER PRIMARY KEY, label TEXT)");
        Exec("CREATE VIEW active_items AS SELECT id, label FROM items");

        var views = await Introspector().IntrospectViewsOnlyAsync();

        views.Should().ContainSingle().Which.Name.Should().Be("active_items");
    }

    [Fact]
    public async Task IntrospectTablesOnlyAsync_DoesNotReturnViews()
    {
        Exec("CREATE TABLE real_table (id INTEGER PRIMARY KEY)");
        Exec("CREATE VIEW v_real AS SELECT id FROM real_table");

        var tables = await Introspector().IntrospectTablesOnlyAsync();

        tables.Should().ContainSingle().Which.Name.Should().Be("real_table");
    }

    // ── internal tables exclusion ─────────────────────────────────────────

    [Fact]
    public async Task IntrospectTablesOnlyAsync_ExcludesSqliteInternalTables()
    {
        Exec("CREATE TABLE my_table (id INTEGER PRIMARY KEY)");

        var tables = await Introspector().IntrospectTablesOnlyAsync();

        tables.Should().NotContain(t => t.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase));
    }

    // ── row count ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRowCountsAsync_ReturnsCorrectCounts()
    {
        Exec("CREATE TABLE things (id INTEGER PRIMARY KEY, val TEXT)");
        Exec("INSERT INTO things VALUES (1, 'a')");
        Exec("INSERT INTO things VALUES (2, 'b')");
        Exec("INSERT INTO things VALUES (3, 'c')");

        var counts = await Introspector().GetRowCountsAsync();

        counts.Should().ContainKey("things").WhoseValue.Should().Be(3);
    }

    // ── column defaults ───────────────────────────────────────────────────

    [Fact]
    public async Task IntrospectTablesOnlyAsync_ColumnDefault_IsCaptured()
    {
        Exec("CREATE TABLE settings (id INTEGER PRIMARY KEY, active INTEGER NOT NULL DEFAULT 1)");

        var tables = await Introspector().IntrospectTablesOnlyAsync();
        var active = tables[0].Fields.Single(f => f.Name == "active");

        active.Default.Should().Be("1");
    }

    // ── generated columns ─────────────────────────────────────────────────

    [Fact]
    public async Task IntrospectTablesOnlyAsync_GeneratedColumn_IsMarkedAsComputed()
    {
        Exec(@"
            CREATE TABLE rects (
                id     INTEGER PRIMARY KEY,
                w      REAL NOT NULL,
                h      REAL NOT NULL,
                area   REAL GENERATED ALWAYS AS (w * h) STORED
            )");

        var tables = await Introspector().IntrospectTablesOnlyAsync();
        var area   = tables[0].Fields.Single(f => f.Name == "area");

        area.IsComputed.Should().BeTrue();
        area.IsPersisted.Should().BeTrue();
    }
}
