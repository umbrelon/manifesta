using FluentAssertions;
using Manifesta.Core.Filtering;
using Manifesta.Core.IR;
using Xunit;

namespace Manifesta.Core.Tests;

/// <summary>
/// Tests for <see cref="SchemaFilter"/>, <see cref="SectionFilter"/>, and
/// <see cref="CombinedFilter"/>.
/// </summary>
public sealed class FilterTests
{
    // ── SchemaFilter ──────────────────────────────────────────────────────────

    [Fact]
    public void SchemaFilter_Null_IsInactiveAndMatchesAll()
    {
        var filter = new SchemaFilter(null);

        filter.IsActive.Should().BeFalse();
        filter.Matches("dbo.User").Should().BeTrue();
        filter.Matches("app.Config").Should().BeTrue();
        filter.Matches("NoSchemaDot").Should().BeTrue();
    }

    [Fact]
    public void SchemaFilter_EmptyString_IsInactiveAndMatchesAll()
    {
        var filter = new SchemaFilter("   ");

        filter.IsActive.Should().BeFalse();
        filter.Matches("dbo.User").Should().BeTrue();
    }

    [Fact]
    public void SchemaFilter_SingleSchema_MatchesOnlyThatSchema()
    {
        var filter = new SchemaFilter("dbo");

        filter.IsActive.Should().BeTrue();
        filter.Matches("dbo.User").Should().BeTrue();
        filter.Matches("dbo.Order").Should().BeTrue();
        filter.Matches("app.User").Should().BeFalse();
        filter.Matches("reporting.Summary").Should().BeFalse();
    }

    [Fact]
    public void SchemaFilter_MultipleSchemas_MatchesAll()
    {
        var filter = new SchemaFilter("dbo,app");

        filter.Matches("dbo.User").Should().BeTrue();
        filter.Matches("app.Config").Should().BeTrue();
        filter.Matches("reporting.Summary").Should().BeFalse();
    }

    [Fact]
    public void SchemaFilter_IsCaseInsensitive()
    {
        var filter = new SchemaFilter("DBO");

        filter.Matches("dbo.User").Should().BeTrue();
        filter.Matches("Dbo.Order").Should().BeTrue();
    }

    [Fact]
    public void SchemaFilter_TableWithoutDot_NeverMatchesActiveFilter()
    {
        var filter = new SchemaFilter("dbo");

        filter.Matches("UserNoDot").Should().BeFalse();
    }

    [Fact]
    public void SchemaFilter_WithWhitespace_TrimsSchemaNames()
    {
        var filter = new SchemaFilter("  dbo , app  ");

        filter.Matches("dbo.Table").Should().BeTrue();
        filter.Matches("app.Table").Should().BeTrue();
        filter.Matches("other.Table").Should().BeFalse();
    }

    [Fact]
    public void SchemaFilter_ExposesSchemas()
    {
        var filter = new SchemaFilter("dbo,app");

        filter.Schemas.Should().BeEquivalentTo(["dbo", "app"]);
    }

    // ── SectionFilter ─────────────────────────────────────────────────────────

    private static IReadOnlyList<SectionDefinition> TwoSections() =>
    [
        new SectionDefinition
        {
            Name = "Users",
            Tables = ["dbo.User", "dbo.UserRole"]
        },
        new SectionDefinition
        {
            Name = "Orders",
            Tables = ["dbo.Order", "dbo.OrderItem"]
        }
    ];

    [Fact]
    public void SectionFilter_Null_IsInactiveAndMatchesAll()
    {
        var filter = new SectionFilter(null, TwoSections());

        filter.IsActive.Should().BeFalse();
        filter.Matches("dbo.User").Should().BeTrue();
        filter.Matches("dbo.SomeOtherTable").Should().BeTrue();
    }

    [Fact]
    public void SectionFilter_SingleSection_MatchesOnlyItsTables()
    {
        var filter = new SectionFilter("Users", TwoSections());

        filter.IsActive.Should().BeTrue();
        filter.Matches("dbo.User").Should().BeTrue();
        filter.Matches("dbo.UserRole").Should().BeTrue();
        filter.Matches("dbo.Order").Should().BeFalse();
        filter.Matches("dbo.SomeOtherTable").Should().BeFalse();
    }

    [Fact]
    public void SectionFilter_MultipleSections_MatchesAllTheirTables()
    {
        var filter = new SectionFilter("Users,Orders", TwoSections());

        filter.Matches("dbo.User").Should().BeTrue();
        filter.Matches("dbo.Order").Should().BeTrue();
        filter.Matches("dbo.OrderItem").Should().BeTrue();
        filter.Matches("dbo.Product").Should().BeFalse();
    }

    [Fact]
    public void SectionFilter_IsCaseInsensitiveForTableNames()
    {
        var filter = new SectionFilter("Users", TwoSections());

        filter.Matches("DBO.USER").Should().BeTrue();
        filter.Matches("dbo.user").Should().BeTrue();
    }

    [Fact]
    public void SectionFilter_IsCaseInsensitiveForSectionNames()
    {
        var filter = new SectionFilter("USERS", TwoSections());

        filter.IsActive.Should().BeTrue();
        filter.Matches("dbo.User").Should().BeTrue();
    }

    [Fact]
    public void SectionFilter_UnknownSection_IsReportedAndMatchesNothing()
    {
        var filter = new SectionFilter("DoesNotExist", TwoSections());

        filter.IsActive.Should().BeTrue();
        filter.UnknownSections.Should().Contain("DoesNotExist");
        filter.Matches("dbo.User").Should().BeFalse();
    }

    [Fact]
    public void SectionFilter_MixedKnownAndUnknown_ReportsOnlyUnknown()
    {
        var filter = new SectionFilter("Users,DoesNotExist", TwoSections());

        filter.UnknownSections.Should().Contain("DoesNotExist");
        filter.UnknownSections.Should().NotContain("Users");
        filter.Matches("dbo.User").Should().BeTrue();     // from known section
        filter.Matches("dbo.Order").Should().BeFalse();   // not in Users
    }

    [Fact]
    public void SectionFilter_EmptySectionList_NothingMatches()
    {
        var filter = new SectionFilter("Users", []);

        filter.IsActive.Should().BeTrue();
        filter.UnknownSections.Should().Contain("Users");
        filter.Matches("dbo.User").Should().BeFalse();
    }

    // ── CombinedFilter ────────────────────────────────────────────────────────

    private static IReadOnlyList<SectionDefinition> MixedSections() =>
    [
        new SectionDefinition
        {
            Name = "Users",
            Tables = ["dbo.User", "app.User"]
        }
    ];

    [Fact]
    public void CombinedFilter_NeitherActive_MatchesEverything()
    {
        var filter = CombinedFilter.Create(null, null, MixedSections());

        filter.Matches("dbo.User").Should().BeTrue();
        filter.Matches("app.Whatever").Should().BeTrue();
        filter.Matches("SomeNoDotTable").Should().BeTrue();
    }

    [Fact]
    public void CombinedFilter_SchemaOnly_FiltersToSchema()
    {
        var filter = CombinedFilter.Create("dbo", null, MixedSections());

        filter.Matches("dbo.User").Should().BeTrue();
        filter.Matches("dbo.AnythingElse").Should().BeTrue();
        filter.Matches("app.User").Should().BeFalse();
    }

    [Fact]
    public void CombinedFilter_SectionOnly_FiltersToSection()
    {
        var filter = CombinedFilter.Create(null, "Users", MixedSections());

        filter.Matches("dbo.User").Should().BeTrue();
        filter.Matches("app.User").Should().BeTrue();
        filter.Matches("dbo.Order").Should().BeFalse();
    }

    [Fact]
    public void CombinedFilter_BothActive_RequiresIntersection()
    {
        var filter = CombinedFilter.Create("dbo", "Users", MixedSections());

        filter.Matches("dbo.User").Should().BeTrue();    // in dbo AND in Users
        filter.Matches("app.User").Should().BeFalse();   // in Users but not dbo
        filter.Matches("dbo.Order").Should().BeFalse();  // in dbo but not Users
        filter.Matches("app.Order").Should().BeFalse();  // in neither
    }

    [Fact]
    public void CombinedFilter_ExposesInnerFilters()
    {
        var filter = CombinedFilter.Create("dbo", "Users", MixedSections());

        filter.Schema.IsActive.Should().BeTrue();
        filter.Section.IsActive.Should().BeTrue();
    }
}
