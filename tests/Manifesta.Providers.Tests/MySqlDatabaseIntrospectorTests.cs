using FluentAssertions;
using Manifesta.Providers;
using Xunit;

namespace Manifesta.Providers.Tests;

[Collection("MySQL Collection")]
public class MySqlDatabaseIntrospectorTests
{
    private readonly MySqlTestFixture _fixture;

    public MySqlDatabaseIntrospectorTests(MySqlTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task IntrospectAsync_ReturnsAllTablesAndView()
    {
        var introspector = new MySqlDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();

        tables.Should().NotBeNull();
        tables.Should().HaveCountGreaterThanOrEqualTo(5); // BundleType, BundlePurchaseType, Bundle, Settings, BundleView
    }

    [Fact]
    public async Task IntrospectAsync_TableNamesHaveNoSchemaPrefix()
    {
        var introspector = new MySqlDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();

        tables.Should().AllSatisfy(t => t.Name.Should().NotContain("."));
    }

    [Fact]
    public async Task IntrospectAsync_ReturnsOrderedByName()
    {
        var introspector = new MySqlDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();

        var ordered = tables.OrderBy(t => t.Name).ToList();
        tables.Should().Equal(ordered);
    }

    [Fact]
    public async Task IntrospectTablesOnlyAsync_ExcludesViews()
    {
        var introspector = new MySqlDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectTablesOnlyAsync();

        tables.Should().NotContain(t => t.Name == "BundleView");
        tables.Should().Contain(t => t.Name == "Bundle");
    }

    [Fact]
    public async Task IntrospectViewsOnlyAsync_ReturnsOnlyViews()
    {
        var introspector = new MySqlDatabaseIntrospector(_fixture.ConnectionString);

        var views = await introspector.IntrospectViewsOnlyAsync();

        views.Should().Contain(t => t.Name == "BundleView");
        views.Should().NotContain(t => t.Name == "Bundle");
    }

    [Fact]
    public async Task IntrospectAsync_ExtractsCorrectColumnTypes()
    {
        var introspector = new MySqlDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var bundle = tables.Single(t => t.Name == "Bundle");

        bundle.Fields.Should().Contain(f => f.Name == "lBundleID"     && f.Type == "int"           && !f.Nullable);
        bundle.Fields.Should().Contain(f => f.Name == "szDescription"  && f.Type == "varchar(50)"   && f.Nullable);
        bundle.Fields.Should().Contain(f => f.Name == "lfTotalAmount"  && f.Type == "decimal(18,2)" && !f.Nullable);
        bundle.Fields.Should().Contain(f => f.Name == "TaxPercentage"  && f.Type == "decimal(5,2)"  && f.Nullable);
        bundle.Fields.Should().Contain(f => f.Name == "IsActive"       && f.Type == "tinyint"       && !f.Nullable);
    }

    [Fact]
    public async Task IntrospectAsync_ExtractsDefaultValues()
    {
        var introspector = new MySqlDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var bundle = tables.Single(t => t.Name == "Bundle");

        bundle.Fields.Should().Contain(f => f.Name == "IsActive" && f.Default == "1");
    }

    [Fact]
    public async Task IntrospectAsync_PopulatesPrimaryKeys()
    {
        var introspector = new MySqlDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var bundle = tables.Single(t => t.Name == "Bundle");

        bundle.PrimaryKey.Should().ContainSingle().Which.Should().Be("lBundleID");
    }

    [Fact]
    public async Task IntrospectAsync_ExtractsForeignKeys()
    {
        var introspector = new MySqlDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var bundle = tables.Single(t => t.Name == "Bundle");

        bundle.ForeignKeys.Should().Contain(fk =>
            fk.SourceField == "cTypeBundle" &&
            fk.TargetTable == "BundleType" &&
            fk.TargetField == "Id" &&
            fk.CascadeDelete);

        bundle.ForeignKeys.Should().Contain(fk =>
            fk.SourceField == "BundlePurchaseTypeID" &&
            fk.TargetTable == "BundlePurchaseType" &&
            fk.TargetField == "Id" &&
            !fk.CascadeDelete);
    }

    [Fact]
    public async Task IntrospectAsync_DetectsVirtualGeneratedColumn()
    {
        var introspector = new MySqlDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var bundle = tables.Single(t => t.Name == "Bundle");

        var totalWithTax = bundle.Fields.Single(f => f.Name == "TotalWithTax");
        totalWithTax.IsComputed.Should().BeTrue();
        totalWithTax.IsPersisted.Should().BeFalse();
        totalWithTax.ComputedExpression.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task IntrospectAsync_DetectsStoredGeneratedColumn()
    {
        var introspector = new MySqlDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var bundle = tables.Single(t => t.Name == "Bundle");

        var bundleCode = bundle.Fields.Single(f => f.Name == "BundleCode");
        bundleCode.IsComputed.Should().BeTrue();
        bundleCode.IsPersisted.Should().BeTrue();
        bundleCode.ComputedExpression.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task IntrospectAsync_OmitsNonDatabaseFields()
    {
        var introspector = new MySqlDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();

        tables.Should().AllSatisfy(t =>
        {
            t.Description.Should().Be("", "database introspection should not populate description");
            t.DatabaseTypes.Should().BeEmpty();
            t.Sets.Should().BeEmpty();
            t.Sections.Should().BeEmpty();
            t.SourceFile.Should().Be("", "database introspection should not populate source file");
        });
    }

    [Fact]
    public async Task IntrospectAsync_IgnoresSchemaFilter()
    {
        var introspector = new MySqlDatabaseIntrospector(_fixture.ConnectionString);

        // Even with a schema filter, MySQL returns all tables in the connected database.
        var tablesWithFilter    = await introspector.IntrospectAsync(schemaFilter: "dbo");
        var tablesWithoutFilter = await introspector.IntrospectAsync();

        tablesWithFilter.Should().BeEquivalentTo(tablesWithoutFilter);
    }

    [Fact]
    public async Task GetRowCountsAsync_ReturnsApproximateCounts()
    {
        var introspector = new MySqlDatabaseIntrospector(_fixture.ConnectionString);

        var counts = await introspector.GetRowCountsAsync();

        counts.Should().NotBeNull();
        counts.Keys.Should().NotContain(k => k.Contains('.'));
        counts.Should().ContainKey("BundleType");
        counts.Should().ContainKey("Bundle");
    }

    [Fact]
    public void Constructor_WithNullConnectionString_ThrowsArgumentNullException()
    {
        var action = () => new MySqlDatabaseIntrospector(null!);
        action.Should().Throw<ArgumentNullException>();
    }

    // ── Indexes ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IntrospectAsync_ExtractsRegularIndex()
    {
        var introspector = new MySqlDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var bundle = tables.Single(t => t.Name == "Bundle");

        bundle.Indexes.Should().Contain(i =>
            i.Name == "idx_bundle_cpriority" &&
            i.Columns.Contains("cPriority") &&
            !i.IsUnique &&
            !i.IsFiltered &&
            i.IncludedColumns == null);
    }

    [Fact]
    public async Task IntrospectAsync_PrimaryKeyIndexNotInIndexes()
    {
        var introspector = new MySqlDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var bundle = tables.Single(t => t.Name == "Bundle");

        // The PK index (lBundleID) must not appear in Indexes — it is represented via PrimaryKey.
        bundle.Indexes.Should().NotContain(i => i.Columns.Contains("lBundleID"));
    }

    [Fact]
    public async Task IntrospectAsync_UniqueConstraintIndexNotInIndexes()
    {
        var introspector = new MySqlDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var settings = tables.Single(t => t.Name == "Settings");

        // The unique constraint on SettingKey must not appear as a regular index.
        settings.Indexes.Should().NotContain(i => i.Columns.Contains("SettingKey"));
    }

    // ── Check constraints ──────────────────────────────────────────────────────

    [Fact]
    public async Task IntrospectAsync_ExtractsCheckConstraint()
    {
        var introspector = new MySqlDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var bundle = tables.Single(t => t.Name == "Bundle");

        var chk = bundle.CheckConstraints.SingleOrDefault(c => c.Name == "chk_bundle_price_positive");
        chk.Should().NotBeNull();
        chk!.Expression.Should().NotBeNullOrWhiteSpace();
        chk.Column.Should().BeNull("MySQL does not associate check constraints with individual columns");
    }

    [Fact]
    public async Task IntrospectAsync_ExtractsMultiColumnCheckConstraint()
    {
        var introspector = new MySqlDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var bundle = tables.Single(t => t.Name == "Bundle");

        var chk = bundle.CheckConstraints.SingleOrDefault(c => c.Name == "chk_bundle_amounts_valid");
        chk.Should().NotBeNull();
        chk!.Expression.Should().NotBeNullOrWhiteSpace();
        chk.Column.Should().BeNull();
    }

    // ── Unique constraints ─────────────────────────────────────────────────────

    [Fact]
    public async Task IntrospectAsync_ExtractsUniqueConstraint()
    {
        var introspector = new MySqlDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var settings = tables.Single(t => t.Name == "Settings");

        settings.UniqueConstraints.Should().ContainSingle(uc =>
            uc.Columns.Contains("SettingKey"));
    }
}
