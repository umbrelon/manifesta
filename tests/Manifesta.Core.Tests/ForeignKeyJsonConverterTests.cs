using System.Text.Json;
using FluentAssertions;
using Manifesta.Core.IR;
using Xunit;

namespace Manifesta.Core.Tests;

public sealed class ForeignKeyJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new ForeignKeyJsonConverter() },
    };

    // ── Canonical "kind" form ─────────────────────────────────────────────

    [Theory]
    [InlineData("physical", ForeignKeyKind.Physical)]
    [InlineData("logical",  ForeignKeyKind.Logical)]
    [InlineData("virtual",  ForeignKeyKind.Virtual)]
    public void Read_KindProperty_ParsedCorrectly(string kindValue, ForeignKeyKind expected)
    {
        var json = $@"{{""sourceField"":""UserId"",""targetTable"":""dbo.User"",""targetField"":""Id"",""kind"":""{kindValue}""}}";

        var fk = Deserialize(json);

        fk.Kind.Should().Be(expected);
    }

    [Fact]
    public void Read_NoKindAndNoSoft_DefaultsToPhysical()
    {
        var json = @"{""sourceField"":""UserId"",""targetTable"":""dbo.User"",""targetField"":""Id""}";

        var fk = Deserialize(json);

        fk.Kind.Should().Be(ForeignKeyKind.Physical);
    }

    // ── Deprecated "soft" form ────────────────────────────────────────────

    [Fact]
    public void Read_SoftTrue_MapsToLogical()
    {
        var json = @"{""sourceField"":""UserId"",""targetTable"":""dbo.User"",""targetField"":""Id"",""soft"":true}";

        var fk = Deserialize(json);

        fk.Kind.Should().Be(ForeignKeyKind.Logical);
    }

    [Fact]
    public void Read_SoftFalse_MapsToPhysical()
    {
        var json = @"{""sourceField"":""UserId"",""targetTable"":""dbo.User"",""targetField"":""Id"",""soft"":false}";

        var fk = Deserialize(json);

        fk.Kind.Should().Be(ForeignKeyKind.Physical);
    }

    // ── Both "soft" and "kind" present ────────────────────────────────────

    [Fact]
    public void Read_BothSoftAndKind_ThrowsJsonException()
    {
        var json = @"{""sourceField"":""UserId"",""targetTable"":""dbo.User"",""targetField"":""Id"",""soft"":true,""kind"":""logical""}";

        var act = () => Deserialize(json);

        act.Should().Throw<JsonException>().WithMessage("*soft*");
    }

    // ── Required field validation ─────────────────────────────────────────

    [Fact]
    public void Read_MissingSourceField_ThrowsJsonException()
    {
        var json = @"{""targetTable"":""dbo.User"",""targetField"":""Id""}";

        var act = () => Deserialize(json);

        act.Should().Throw<JsonException>().WithMessage("*sourceField*");
    }

    [Fact]
    public void Read_MissingTargetTable_ThrowsJsonException()
    {
        var json = @"{""sourceField"":""UserId"",""targetField"":""Id""}";

        var act = () => Deserialize(json);

        act.Should().Throw<JsonException>().WithMessage("*targetTable*");
    }

    [Fact]
    public void Read_MissingTargetField_ThrowsJsonException()
    {
        var json = @"{""sourceField"":""UserId"",""targetTable"":""dbo.User""}";

        var act = () => Deserialize(json);

        act.Should().Throw<JsonException>().WithMessage("*targetField*");
    }

    // ── Unknown kind value ─────────────────────────────────────────────────

    [Fact]
    public void Read_UnknownKindValue_ThrowsJsonException()
    {
        var json = @"{""sourceField"":""UserId"",""targetTable"":""dbo.User"",""targetField"":""Id"",""kind"":""cascade""}";

        var act = () => Deserialize(json);

        act.Should().Throw<JsonException>().WithMessage("*cascade*");
    }

    // ── CascadeDelete ─────────────────────────────────────────────────────

    [Fact]
    public void Read_CascadeDeleteTrue_Parsed()
    {
        var json = @"{""sourceField"":""UserId"",""targetTable"":""dbo.User"",""targetField"":""Id"",""cascadeDelete"":true}";

        var fk = Deserialize(json);

        fk.CascadeDelete.Should().BeTrue();
    }

    [Fact]
    public void Read_CascadeDeleteAbsent_DefaultsFalse()
    {
        var json = @"{""sourceField"":""UserId"",""targetTable"":""dbo.User"",""targetField"":""Id""}";

        var fk = Deserialize(json);

        fk.CascadeDelete.Should().BeFalse();
    }

    // ── Case-insensitive property names ───────────────────────────────────

    [Fact]
    public void Read_PropertyNamesUpperCase_ParsedCaseInsensitively()
    {
        var json = @"{""SOURCEFIELD"":""UserId"",""TARGETTABLE"":""dbo.User"",""TARGETFIELD"":""Id""}";

        var fk = Deserialize(json);

        fk.SourceField.Should().Be("UserId");
        fk.TargetTable.Should().Be("dbo.User");
        fk.TargetField.Should().Be("Id");
    }

    // ── Write (serialisation) ─────────────────────────────────────────────

    [Fact]
    public void Write_PhysicalFk_OmitsKindProperty()
    {
        var fk = new ForeignKey { SourceField = "UserId", TargetTable = "dbo.User", TargetField = "Id" };

        var json = JsonSerializer.Serialize(fk, Options);

        json.Should().NotContain("kind");
        json.Should().NotContain("soft");
    }

    [Theory]
    [InlineData(ForeignKeyKind.Logical, "logical")]
    [InlineData(ForeignKeyKind.Virtual, "virtual")]
    public void Write_NonPhysicalFk_IncludesKindProperty(ForeignKeyKind kind, string expectedKind)
    {
        var fk = new ForeignKey
        {
            SourceField = "UserId",
            TargetTable = "dbo.User",
            TargetField = "Id",
            Kind        = kind,
        };

        var json = JsonSerializer.Serialize(fk, Options);

        json.Should().Contain($@"""kind"":""{expectedKind}""");
    }

    [Fact]
    public void Write_CascadeDeleteFalse_OmitsCascadeDeleteProperty()
    {
        var fk = new ForeignKey { SourceField = "UserId", TargetTable = "dbo.User", TargetField = "Id" };

        var json = JsonSerializer.Serialize(fk, Options);

        json.Should().NotContain("cascadeDelete");
    }

    [Fact]
    public void Write_CascadeDeleteTrue_IncludesCascadeDeleteProperty()
    {
        var fk = new ForeignKey
        {
            SourceField   = "UserId",
            TargetTable   = "dbo.User",
            TargetField   = "Id",
            CascadeDelete = true,
        };

        var json = JsonSerializer.Serialize(fk, Options);

        json.Should().Contain(@"""cascadeDelete"":true");
    }

    // ── Round-trip ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ForeignKeyKind.Physical, false)]
    [InlineData(ForeignKeyKind.Logical,  false)]
    [InlineData(ForeignKeyKind.Virtual,  true)]
    public void RoundTrip_SerializeDeserialize_PreservesAllProperties(ForeignKeyKind kind, bool cascade)
    {
        var original = new ForeignKey
        {
            SourceField   = "UserId",
            TargetTable   = "dbo.User",
            TargetField   = "Id",
            Kind          = kind,
            CascadeDelete = cascade,
        };

        var json   = JsonSerializer.Serialize(original, Options);
        var result = JsonSerializer.Deserialize<ForeignKey>(json, Options);

        result.Should().Be(original);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static ForeignKey Deserialize(string json) =>
        JsonSerializer.Deserialize<ForeignKey>(json, Options)!;
}
