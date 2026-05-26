using FluentAssertions;
using Manifesta.Providers;
using Xunit;

namespace Manifesta.Providers.Tests;

[Collection("PostgreSQL Collection")]
public class PostgresDatabaseIntrospectorTests
{
    private readonly PostgresTestFixture _fixture;

    public PostgresDatabaseIntrospectorTests(PostgresTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task IntrospectAsync_ReturnsAllTablesAndView()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();

        // public: bundletype, bundlepurchasetype, bundle, settings, bundleview + app: appsetting
        tables.Should().NotBeNull();
        tables.Should().HaveCountGreaterThanOrEqualTo(6);
    }

    [Fact]
    public async Task IntrospectAsync_TableNamesHaveSchemaPrefix()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();

        tables.Should().AllSatisfy(t => t.Name.Should().Contain("."));
    }

    [Fact]
    public async Task IntrospectAsync_PublicTablesHavePublicPrefix()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();

        tables.Should().Contain(t => t.Name == "public.bundle");
        tables.Should().Contain(t => t.Name == "public.bundletype");
        tables.Should().Contain(t => t.Name == "public.settings");
    }

    [Fact]
    public async Task IntrospectAsync_ReturnsOrderedByName()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();

        var ordered = tables.OrderBy(t => t.Name).ToList();
        tables.Should().Equal(ordered);
    }

    [Fact]
    public async Task IntrospectTablesOnlyAsync_ExcludesViews()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectTablesOnlyAsync();

        tables.Should().NotContain(t => t.Name == "public.bundleview");
        tables.Should().Contain(t => t.Name == "public.bundle");
    }

    [Fact]
    public async Task IntrospectViewsOnlyAsync_ReturnsOnlyViews()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var views = await introspector.IntrospectViewsOnlyAsync();

        views.Should().Contain(t => t.Name == "public.bundleview");
        views.Should().NotContain(t => t.Name == "public.bundle");
    }

    [Fact]
    public async Task IntrospectAsync_ExtractsCorrectColumnTypes()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var bundle = tables.Single(t => t.Name == "public.bundle");

        bundle.Fields.Should().Contain(f => f.Name == "lbundleid"     && f.Type == "integer"       && !f.Nullable);
        bundle.Fields.Should().Contain(f => f.Name == "szdescription"  && f.Type == "varchar(50)"   && f.Nullable);
        bundle.Fields.Should().Contain(f => f.Name == "lftotalamount"  && f.Type == "numeric(18,2)" && !f.Nullable);
        bundle.Fields.Should().Contain(f => f.Name == "taxpercentage"  && f.Type == "numeric(5,2)"  && f.Nullable);
        bundle.Fields.Should().Contain(f => f.Name == "isactive"       && f.Type == "boolean"       && !f.Nullable);
    }

    [Fact]
    public async Task IntrospectAsync_ExtractsDefaultValues()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var bundle = tables.Single(t => t.Name == "public.bundle");

        bundle.Fields.Should().Contain(f => f.Name == "isactive" && f.Default != null);
    }

    [Fact]
    public async Task IntrospectAsync_PopulatesPrimaryKeys()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var bundle = tables.Single(t => t.Name == "public.bundle");

        bundle.PrimaryKey.Should().ContainSingle().Which.Should().Be("lbundleid");
    }

    [Fact]
    public async Task IntrospectAsync_ExtractsForeignKeys()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var bundle = tables.Single(t => t.Name == "public.bundle");

        bundle.ForeignKeys.Should().Contain(fk =>
            fk.SourceField == "ctypebundle" &&
            fk.TargetTable == "public.bundletype" &&
            fk.TargetField == "id" &&
            fk.CascadeDelete);

        bundle.ForeignKeys.Should().Contain(fk =>
            fk.SourceField == "bundlepurchasetypeid" &&
            fk.TargetTable == "public.bundlepurchasetype" &&
            fk.TargetField == "id" &&
            !fk.CascadeDelete);
    }

    [Fact]
    public async Task IntrospectAsync_DetectsStoredGeneratedColumn()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var bundle = tables.Single(t => t.Name == "public.bundle");

        var bundleCode = bundle.Fields.Single(f => f.Name == "bundlecode");
        bundleCode.IsComputed.Should().BeTrue();
        bundleCode.IsPersisted.Should().BeTrue();
        bundleCode.ComputedExpression.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task IntrospectAsync_OmitsNonDatabaseFields()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

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
    public async Task IntrospectAsync_SchemaFilterRespectsPublicOnly()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var publicOnly = await introspector.IntrospectAsync(schemaFilter: "public");

        publicOnly.Should().AllSatisfy(t => t.Name.Should().StartWith("public."));
        publicOnly.Should().NotContain(t => t.Name.StartsWith("app."));
    }

    [Fact]
    public async Task IntrospectAsync_SchemaFilterRespectsAppOnly()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var appOnly = await introspector.IntrospectAsync(schemaFilter: "app");

        appOnly.Should().AllSatisfy(t => t.Name.Should().StartWith("app."));
        appOnly.Should().Contain(t => t.Name == "app.appsetting");
        appOnly.Should().NotContain(t => t.Name.StartsWith("public."));
    }

    [Fact]
    public async Task IntrospectAsync_MultiSchemaFilterReturnsBothSchemas()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var both = await introspector.IntrospectAsync(schemaFilter: "public,app");

        both.Should().Contain(t => t.Name.StartsWith("public."));
        both.Should().Contain(t => t.Name.StartsWith("app."));
    }

    [Fact]
    public async Task GetRowCountsAsync_ReturnsSchemaQualifiedKeys()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var counts = await introspector.GetRowCountsAsync();

        counts.Should().NotBeNull();
        counts.Keys.Should().AllSatisfy(k => k.Should().Contain("."));
        counts.Should().ContainKey("public.bundletype");
        counts.Should().ContainKey("public.bundle");
    }

    [Fact]
    public async Task GetRowCountsAsync_SchemaFilterApplied()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var counts = await introspector.GetRowCountsAsync(schemaFilter: "public");

        counts.Keys.Should().AllSatisfy(k => k.Should().StartWith("public."));
        counts.Should().NotContainKey("app.appsetting");
    }

    [Fact]
    public void Constructor_WithNullConnectionString_ThrowsArgumentNullException()
    {
        var action = () => new PostgresDatabaseIntrospector(null!);
        action.Should().Throw<ArgumentNullException>();
    }

    // ── Indexes ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IntrospectAsync_ExtractsRegularIndex()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var bundle = tables.Single(t => t.Name == "public.bundle");

        bundle.Indexes.Should().Contain(i =>
            i.Name == "idx_bundle_cpriority" &&
            i.Columns.Contains("cpriority") &&
            !i.IsUnique &&
            !i.IsFiltered &&
            i.IncludedColumns == null);
    }

    [Fact]
    public async Task IntrospectAsync_ExtractsPartialIndex()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var bundle = tables.Single(t => t.Name == "public.bundle");

        bundle.Indexes.Should().Contain(i =>
            i.Name == "idx_bundle_active_names" &&
            i.Columns.Contains("szbundle") &&
            i.IsFiltered &&
            i.FilterExpression != null &&
            i.FilterExpression.Contains("isactive"));
    }

    [Fact]
    public async Task IntrospectAsync_ExtractsCoveredIndexWithIncludedColumns()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var bundle = tables.Single(t => t.Name == "public.bundle");

        var coveredIdx = bundle.Indexes.SingleOrDefault(i => i.Name == "idx_bundle_type_cover");
        coveredIdx.Should().NotBeNull();
        coveredIdx!.Columns.Should().Contain("ctypebundle");
        coveredIdx.IncludedColumns.Should().NotBeNullOrWhiteSpace();
        coveredIdx.IncludedColumns.Should().Contain("szbundle");
        coveredIdx.IncludedColumns.Should().Contain("price");
    }

    [Fact]
    public async Task IntrospectAsync_PrimaryKeyIndexNotInIndexes()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var bundle = tables.Single(t => t.Name == "public.bundle");

        // The PK index (lbundleid) must not appear in Indexes — it is represented via PrimaryKey.
        bundle.Indexes.Should().NotContain(i => i.Columns.Contains("lbundleid"));
    }

    [Fact]
    public async Task IntrospectAsync_UniqueConstraintIndexNotInIndexes()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var settings = tables.Single(t => t.Name == "public.settings");

        // The unique constraint on settingkey must not appear as a regular index.
        settings.Indexes.Should().NotContain(i => i.Columns.Contains("settingkey"));
    }

    // ── Check constraints ──────────────────────────────────────────────────────

    [Fact]
    public async Task IntrospectAsync_ExtractsSingleColumnCheckConstraint()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var bundle = tables.Single(t => t.Name == "public.bundle");

        var chk = bundle.CheckConstraints.SingleOrDefault(c => c.Name == "chk_bundle_price_positive");
        chk.Should().NotBeNull();
        chk!.Expression.Should().Contain("price");
        chk.Column.Should().Be("price");
    }

    [Fact]
    public async Task IntrospectAsync_ExtractsMultiColumnCheckConstraintWithNullColumn()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var bundle = tables.Single(t => t.Name == "public.bundle");

        var chk = bundle.CheckConstraints.SingleOrDefault(c => c.Name == "chk_bundle_amounts_valid");
        chk.Should().NotBeNull();
        chk!.Expression.Should().Contain("price");
        chk.Column.Should().BeNull("multi-column check constraints have no single Column");
    }

    // ── Unique constraints ─────────────────────────────────────────────────────

    [Fact]
    public async Task IntrospectAsync_ExtractsUniqueConstraint()
    {
        var introspector = new PostgresDatabaseIntrospector(_fixture.ConnectionString);

        var tables = await introspector.IntrospectAsync();
        var settings = tables.Single(t => t.Name == "public.settings");

        settings.UniqueConstraints.Should().ContainSingle(uc =>
            uc.Columns.Contains("settingkey"));
    }
}
