using FluentAssertions;
using Manifesta.Core.IR;
using Manifesta.Core.Pipeline;
using Manifesta.Core.Tenant;
using Xunit;

namespace Manifesta.Core.Tests.Tenant;

public sealed class TenantConfigValidatorTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal valid config: root type "central" (requires "Core"),
    /// partner type (allowed under central), two databases, one module section.
    /// </summary>
    private static TenantConfig ValidConfig() => new()
    {
        Types = new(StringComparer.OrdinalIgnoreCase)
        {
            ["central"] = new TenantTypeDefinition { Root = true, RequiredSections = ["Core"] },
            ["partner"] = new TenantTypeDefinition { AllowedParents = ["central"] },
        },
        Databases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["central-db"] = new TenantDatabaseEntry { Type = "central", Connection = "...", Sections = ["Core"] },
            ["partner-db"] = new TenantDatabaseEntry { Type = "partner", Parent = "central-db", Connection = "...", Sections = ["Core"] },
        },
    };

    private static List<SectionDefinition> ModuleSections(params string[] names)
        => names.Select(n => new SectionDefinition { Name = n, IsModule = true }).ToList();

    private static ValidationResult Run(TenantConfig config, List<SectionDefinition>? sections = null)
        => new TenantConfigValidator(config, sections ?? ModuleSections("Core")).Validate();

    private static void HasIssue(ValidationResult result, string code, ValidationSeverity severity)
        => result.Issues.Should().Contain(i => i.Code == code && i.Severity == severity,
            because: $"expected issue '{code}' with severity {severity}");

    private static void NoIssue(ValidationResult result, string code)
        => result.Issues.Should().NotContain(i => i.Code == code,
            because: $"issue '{code}' should not be present");

    // ── Clean config ──────────────────────────────────────────────────────────

    [Fact]
    public void ValidConfig_ProducesNoIssues()
    {
        var result = Run(ValidConfig());

        result.Issues.Should().BeEmpty();
    }

    // ── TENANT-ROOT-MISSING ───────────────────────────────────────────────────

    [Fact]
    public void Validate_NoRootType_EmitsRootMissing()
    {
        var config = ValidConfig();
        config.Types["central"] = new TenantTypeDefinition { Root = false, RequiredSections = ["Core"] };

        var result = Run(config);

        HasIssue(result, "TENANT-ROOT-MISSING", ValidationSeverity.Error);
    }

    [Fact]
    public void Validate_TypesEmpty_EmitsRootMissing()
    {
        var config = new TenantConfig();

        var result = Run(config);

        HasIssue(result, "TENANT-ROOT-MISSING", ValidationSeverity.Error);
    }

    // ── TENANT-ROOT-DUPLICATE ─────────────────────────────────────────────────

    [Fact]
    public void Validate_TwoRootDatabases_EmitsRootDuplicate()
    {
        var config = ValidConfig();
        // Add a second database with the root type
        config.Databases["central-db-2"] = new TenantDatabaseEntry { Type = "central", Connection = "...", Sections = ["Core"] };

        var result = Run(config);

        HasIssue(result, "TENANT-ROOT-DUPLICATE", ValidationSeverity.Error);
    }

    [Fact]
    public void Validate_OneRootDatabase_NoRootDuplicateIssue()
    {
        var result = Run(ValidConfig());

        NoIssue(result, "TENANT-ROOT-DUPLICATE");
    }

    // ── TENANT-TYPE-UNKNOWN ───────────────────────────────────────────────────

    [Fact]
    public void Validate_DatabaseReferencesUnknownType_EmitsTypeUnknown()
    {
        var config = ValidConfig();
        config.Databases["partner-db"] = new TenantDatabaseEntry
        {
            Type = "nonexistent-type", Parent = "central-db", Connection = "...", Sections = ["Core"]
        };

        var result = Run(config);

        HasIssue(result, "TENANT-TYPE-UNKNOWN", ValidationSeverity.Error);
    }

    // ── TENANT-SECTION-NOT-MODULE ─────────────────────────────────────────────

    [Fact]
    public void Validate_DatabaseUsesNonModuleSection_EmitsSectionNotModule()
    {
        var config = ValidConfig();
        config.Databases["central-db"] = new TenantDatabaseEntry
        {
            Type = "central", Connection = "...", Sections = ["Core", "NotAModule"]
        };
        // "NotAModule" is not in the sections list at all; only "Core" is a module
        var result = Run(config, ModuleSections("Core"));

        HasIssue(result, "TENANT-SECTION-NOT-MODULE", ValidationSeverity.Error);
    }

    [Fact]
    public void Validate_SectionExistsButNotFlaggedAsModule_EmitsSectionNotModule()
    {
        var config = ValidConfig();
        config.Databases["partner-db"] = new TenantDatabaseEntry
        {
            Type = "partner", Parent = "central-db", Connection = "...", Sections = ["Core", "Billing"]
        };
        // "Billing" exists as a section but IsModule is not set
        var sections = new List<SectionDefinition>
        {
            new() { Name = "Core",    IsModule = true  },
            new() { Name = "Billing", IsModule = false },
        };

        var result = Run(config, sections);

        HasIssue(result, "TENANT-SECTION-NOT-MODULE", ValidationSeverity.Error);
    }

    // ── TENANT-REQUIRED-SECTION-MISSING ──────────────────────────────────────

    [Fact]
    public void Validate_DatabaseMissingRequiredSection_EmitsRequiredSectionMissing()
    {
        var config = ValidConfig();
        // "central" type requires "Core", but this database doesn't install it
        config.Databases["central-db"] = new TenantDatabaseEntry
        {
            Type = "central", Connection = "...", Sections = []
        };

        var result = Run(config);

        HasIssue(result, "TENANT-REQUIRED-SECTION-MISSING", ValidationSeverity.Error);
    }

    [Fact]
    public void Validate_DatabaseHasAllRequiredSections_NoRequiredSectionMissingIssue()
    {
        var result = Run(ValidConfig());

        NoIssue(result, "TENANT-REQUIRED-SECTION-MISSING");
    }

    // ── TENANT-PARENT-UNKNOWN ─────────────────────────────────────────────────

    [Fact]
    public void Validate_DatabaseParentNotInDatabases_EmitsParentUnknown()
    {
        var config = ValidConfig();
        config.Databases["partner-db"] = new TenantDatabaseEntry
        {
            Type = "partner", Parent = "ghost-db", Connection = "...", Sections = ["Core"]
        };

        var result = Run(config);

        HasIssue(result, "TENANT-PARENT-UNKNOWN", ValidationSeverity.Error);
    }

    // ── TENANT-PARENT-NOT-ALLOWED ─────────────────────────────────────────────

    [Fact]
    public void Validate_ParentTypeNotInAllowedParents_EmitsParentNotAllowed()
    {
        var config = ValidConfig();
        // Add a "realtime" type that only allows "partner" as parent
        config.Types["realtime"] = new TenantTypeDefinition { AllowedParents = ["partner"] };
        // But attach it under "central-db" (type "central") which is not in allowedParents
        config.Databases["realtime-db"] = new TenantDatabaseEntry
        {
            Type = "realtime", Parent = "central-db", Connection = "...", Sections = ["Core"]
        };

        var result = Run(config);

        HasIssue(result, "TENANT-PARENT-NOT-ALLOWED", ValidationSeverity.Error);
    }

    [Fact]
    public void Validate_ParentTypeInAllowedParents_NoParentNotAllowedIssue()
    {
        var result = Run(ValidConfig());

        NoIssue(result, "TENANT-PARENT-NOT-ALLOWED");
    }

    // ── TENANT-CYCLE ──────────────────────────────────────────────────────────

    [Fact]
    public void Validate_DirectCycle_EmitsCycle()
    {
        // A's parent is B and B's parent is A
        var config = new TenantConfig
        {
            Types = new(StringComparer.OrdinalIgnoreCase)
            {
                ["central"] = new TenantTypeDefinition { Root = true },
                ["partner"] = new TenantTypeDefinition { AllowedParents = ["central", "partner"] },
            },
            Databases = new(StringComparer.OrdinalIgnoreCase)
            {
                ["db-a"] = new TenantDatabaseEntry { Type = "partner", Parent = "db-b", Connection = "..." },
                ["db-b"] = new TenantDatabaseEntry { Type = "partner", Parent = "db-a", Connection = "..." },
            },
        };

        var result = Run(config);

        HasIssue(result, "TENANT-CYCLE", ValidationSeverity.Error);
    }

    [Fact]
    public void Validate_ThreeNodeCycle_EmitsCycle()
    {
        // A → B → C → A
        var config = new TenantConfig
        {
            Types = new(StringComparer.OrdinalIgnoreCase)
            {
                ["central"] = new TenantTypeDefinition { Root = true },
                ["node"]    = new TenantTypeDefinition { AllowedParents = ["central", "node"] },
            },
            Databases = new(StringComparer.OrdinalIgnoreCase)
            {
                ["root-db"] = new TenantDatabaseEntry { Type = "central", Connection = "..." },
                ["db-a"]    = new TenantDatabaseEntry { Type = "node", Parent = "db-c", Connection = "..." },
                ["db-b"]    = new TenantDatabaseEntry { Type = "node", Parent = "db-a", Connection = "..." },
                ["db-c"]    = new TenantDatabaseEntry { Type = "node", Parent = "db-b", Connection = "..." },
            },
        };

        var result = Run(config);

        HasIssue(result, "TENANT-CYCLE", ValidationSeverity.Error);
    }

    [Fact]
    public void Validate_NoCycles_NoCycleIssue()
    {
        var result = Run(ValidConfig());

        NoIssue(result, "TENANT-CYCLE");
    }

    // ── TENANT-ORPHAN ─────────────────────────────────────────────────────────

    [Fact]
    public void Validate_NonRootDatabaseWithNoParent_EmitsOrphan()
    {
        var config = ValidConfig();
        // Remove the parent from the partner database
        config.Databases["partner-db"] = new TenantDatabaseEntry
        {
            Type = "partner", Parent = null, Connection = "...", Sections = ["Core"]
        };

        var result = Run(config);

        HasIssue(result, "TENANT-ORPHAN", ValidationSeverity.Warning);
    }

    [Fact]
    public void Validate_RootDatabaseWithNoParent_NoOrphanIssue()
    {
        var result = Run(ValidConfig());

        NoIssue(result, "TENANT-ORPHAN");
    }

    // ── TENANT-ROOT-HAS-PARENT ────────────────────────────────────────────────

    [Fact]
    public void Validate_RootDatabaseHasParent_EmitsRootHasParent()
    {
        var config = ValidConfig();
        // Give the root database a parent (nonsensical but the validator catches it)
        config.Databases["central-db"] = new TenantDatabaseEntry
        {
            Type = "central", Parent = "partner-db", Connection = "...", Sections = ["Core"]
        };

        var result = Run(config);

        HasIssue(result, "TENANT-ROOT-HAS-PARENT", ValidationSeverity.Error);
    }

    [Fact]
    public void Validate_RootDatabaseNoParent_NoRootHasParentIssue()
    {
        var result = Run(ValidConfig());

        NoIssue(result, "TENANT-ROOT-HAS-PARENT");
    }

    // ── IsModule serialization (SectionDefinition) ────────────────────────────

    [Fact]
    public void SectionDefinition_IsModule_DefaultIsNull()
    {
        var section = new SectionDefinition { Name = "Core" };

        section.IsModule.Should().BeNull();
    }

    [Fact]
    public void SectionDefinition_IsModule_True_IsRecognisedAsModule()
    {
        var sections = new List<SectionDefinition>
        {
            new() { Name = "Core",    IsModule = true  },
            new() { Name = "Archive", IsModule = null  },
        };

        var config = ValidConfig();
        var validator = new TenantConfigValidator(config, sections);

        // "Archive" is not a module — a database referencing it should fail
        config.Databases["central-db"] = new TenantDatabaseEntry
        {
            Type = "central", Connection = "...", Sections = ["Core", "Archive"]
        };

        var result = validator.Validate();

        HasIssue(result, "TENANT-SECTION-NOT-MODULE", ValidationSeverity.Error);
    }

    // ── ManifestaConfig deserialization ───────────────────────────────────────

    [Fact]
    public void ManifestaConfig_TenantsAbsent_IsNull()
    {
        var config = System.Text.Json.JsonSerializer.Deserialize<ManifestaConfig>("{}")!;

        config.Tenants.Should().BeNull();
    }

    [Fact]
    public void ManifestaConfig_Tenants_RoundTrips()
    {
        var json = """
            {
              "tenants": {
                "types": {
                  "central": { "root": true, "requiredSections": ["Core"] },
                  "partner":  { "allowedParents": ["central"] }
                },
                "databases": {
                  "central-db": { "type": "central", "connection": "...", "sections": ["Core"] },
                  "partner-db": { "type": "partner",  "parent": "central-db", "connection": "...", "sections": ["Core"] }
                }
              }
            }
            """;

        var config = System.Text.Json.JsonSerializer.Deserialize<ManifestaConfig>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        config.Tenants.Should().NotBeNull();
        config.Tenants!.Types.Should().ContainKey("central");
        config.Tenants.Types["central"].Root.Should().BeTrue();
        config.Tenants.Types["central"].RequiredSections.Should().Equal("Core");
        config.Tenants.Types["partner"].AllowedParents.Should().Equal("central");
        config.Tenants.Databases.Should().ContainKey("central-db");
        config.Tenants.Databases["central-db"].Type.Should().Be("central");
        config.Tenants.Databases["partner-db"].Parent.Should().Be("central-db");
    }
}
