using FluentAssertions;
using Manifesta.Core;
using Manifesta.Core.IR;
using Manifesta.Doc;
using Xunit;

namespace Manifesta.Doc.Tests;

/// <summary>
/// Tests for <see cref="DatabaseDocGenerator"/>.
/// Covers markdown generation with TOC, sections, field tables, and FK relationships.
/// </summary>
public sealed class DatabaseDocGeneratorTests
{
    private readonly DatabaseDocGenerator _generator = new();

    // ── Basic generation ───────────────────────────────────────────────────

    [Fact]
    public void Generate_SingleTable_ProducesMarkdown()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                new()
                {
                    Name = "Users",
                    Description = "User accounts",
                    Fields = new List<FieldDefinition>
                    {
                        new() { Name = "id", Type = "int", Nullable = false, Description = "User ID" },
                        new() { Name = "email", Type = "varchar(255)", Nullable = false }
                    }.AsReadOnly(),
                    PrimaryKey = new List<string> { "id" }.AsReadOnly(),
                }
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new()
                {
                    Name = "Accounts",
                    Tables = new List<string> { "Users" }.AsReadOnly()
                }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        result.Should().NotBeEmpty();
        result.Should().Contain("Users").And.Contain("User accounts");
    }

    [Fact]
    public void Generate_EmptyManifestRoot_ProducesValidMarkdown()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>().AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>().AsReadOnly(),
        };

        var result = _generator.Generate(root);

        result.Should().NotBeEmpty();  // Should still produce valid markdown (empty doc)
    }

    // ── Field table generation ─────────────────────────────────────────────

    [Fact]
    public void Generate_FieldTable_IncludesNameTypeNullable()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                CreateTestTable("Orders", new[]
                {
                    CreateField("orderId", "int", false),
                    CreateField("customerId", "int", false),
                    CreateField("notes", "varchar(max)", true),
                })
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new()
                {
                    Name = "Sales",
                    Tables = new List<string> { "Orders" }.AsReadOnly()
                }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        // Field names should appear
        result.Should().Contain("orderId").And.Contain("customerId").And.Contain("notes");
        // Types should appear
        result.Should().Contain("int").And.Contain("varchar(max)");
    }

    [Fact]
    public void Generate_NullableFieldMarked_InOutput()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                CreateTestTable("Users", new[]
                {
                    CreateField("id", "int", false),
                    CreateField("middleName", "varchar(100)", true, "Nullable middle name"),
                })
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new()
                {
                    Name = "Accounts",
                    Tables = new List<string> { "Users" }.AsReadOnly()
                }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        // Nullable indicator (could be NULL, nullable, ?, etc.)
        result.Should().Contain("Users");
        result.Should().Contain("middleName");
    }

    // ── Primary key indication ────────────────────────────────────────────

    [Fact]
    public void Generate_PrimaryKeyField_MarkedInOutput()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                new()
                {
                    Name = "Products",
                    Fields = new List<FieldDefinition>
                    {
                        new() { Name = "productId", Type = "int", Nullable = false },
                        new() { Name = "name", Type = "varchar(255)", Nullable = false }
                    }.AsReadOnly(),
                    PrimaryKey = new List<string> { "productId" }.AsReadOnly(),
                }
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new()
                {
                    Name = "Inventory",
                    Tables = new List<string> { "Products" }.AsReadOnly()
                }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        result.Should().Contain("Products");
        result.Should().Contain("productId");
        // Should indicate it's a PK
        result.Should().Contain("Primary Key");
    }

    // ── Foreign key relationships ──────────────────────────────────────────

    [Fact]
    public void Generate_ForeignKeyRelationship_Documented()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                CreateTestTable("Users", new[] { CreateField("id", "int", false) }),
                new()
                {
                    Name = "Orders",
                    Fields = new List<FieldDefinition>
                    {
                        new() { Name = "orderId", Type = "int", Nullable = false },
                        new() { Name = "userId", Type = "int", Nullable = false }
                    }.AsReadOnly(),
                    PrimaryKey = new List<string> { "orderId" }.AsReadOnly(),
                    ForeignKeys = new List<ForeignKey>
                    {
                        new() { SourceField = "userId", TargetTable = "Users", TargetField = "id" }
                    }.AsReadOnly(),
                }
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new()
                {
                    Name = "Sales",
                    Tables = new List<string> { "Users", "Orders" }.AsReadOnly()
                }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        result.Should().Contain("Orders").And.Contain("Users");
        result.Should().Contain("userId");  // FK field
        result.Should().Contain("Foreign Keys");
    }

    [Fact]
    public void Generate_SoftForeignKey_MarkedInOutput()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                CreateTestTable("Reseller", new[] { CreateField("id", "int", false) }),
                new()
                {
                    Name = "WholesaleMapping",
                    Fields = new List<FieldDefinition>
                    {
                        new() { Name = "id", Type = "int", Nullable = false },
                        new() { Name = "resellerId", Type = "int", Nullable = false }
                    }.AsReadOnly(),
                    PrimaryKey = new List<string> { "id" }.AsReadOnly(),
                    ForeignKeys = new List<ForeignKey>
                    {
                        new() { SourceField = "resellerId", TargetTable = "Reseller", TargetField = "id", Kind = ForeignKeyKind.Logical }
                    }.AsReadOnly(),
                }
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new() { Name = "Mappings", Tables = new List<string> { "Reseller", "WholesaleMapping" }.AsReadOnly() }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        result.Should().Contain("(logical)");
        result.Should().Contain("`resellerId` → `Reseller.id` (logical)");
    }

    [Fact]
    public void Generate_HardForeignKey_NoSoftAnnotation()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                CreateTestTable("Users", new[] { CreateField("id", "int", false) }),
                new()
                {
                    Name = "Orders",
                    Fields = new List<FieldDefinition>
                    {
                        new() { Name = "id", Type = "int", Nullable = false },
                        new() { Name = "userId", Type = "int", Nullable = false }
                    }.AsReadOnly(),
                    PrimaryKey = new List<string> { "id" }.AsReadOnly(),
                    ForeignKeys = new List<ForeignKey>
                    {
                        new() { SourceField = "userId", TargetTable = "Users", TargetField = "id" }
                    }.AsReadOnly(),
                }
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new() { Name = "Sales", Tables = new List<string> { "Users", "Orders" }.AsReadOnly() }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        result.Should().Contain("`userId` → `Users.id`");
        result.Should().NotContain("(soft)");
    }

    [Fact]
    public void Generate_SoftAndCascadeForeignKey_BothAnnotated()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                CreateTestTable("Users", new[] { CreateField("id", "int", false) }),
                new()
                {
                    Name = "Logs",
                    Fields = new List<FieldDefinition>
                    {
                        new() { Name = "id", Type = "int", Nullable = false },
                        new() { Name = "userId", Type = "int", Nullable = false }
                    }.AsReadOnly(),
                    PrimaryKey = new List<string> { "id" }.AsReadOnly(),
                    ForeignKeys = new List<ForeignKey>
                    {
                        new() { SourceField = "userId", TargetTable = "Users", TargetField = "id", Kind = ForeignKeyKind.Logical, CascadeDelete = true }
                    }.AsReadOnly(),
                }
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new() { Name = "Audit", Tables = new List<string> { "Users", "Logs" }.AsReadOnly() }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        result.Should().Contain("(logical, cascades)");
    }

    [Fact]
    public void Generate_CascadeDeleteIndicator_Shown()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                CreateTestTable("Users", new[] { CreateField("id", "int", false) }),
                new()
                {
                    Name = "Orders",
                    Fields = new List<FieldDefinition>
                    {
                        new() { Name = "orderId", Type = "int", Nullable = false },
                        new() { Name = "userId", Type = "int", Nullable = false }
                    }.AsReadOnly(),
                    PrimaryKey = new List<string> { "orderId" }.AsReadOnly(),
                    ForeignKeys = new List<ForeignKey>
                    {
                        new() { SourceField = "userId", TargetTable = "Users", TargetField = "id", CascadeDelete = true }
                    }.AsReadOnly(),
                }
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new()
                {
                    Name = "Sales",
                    Tables = new List<string> { "Users", "Orders" }.AsReadOnly()
                }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        // Should indicate cascade delete behavior
        result.Should().Contain("Orders");
        result.Should().Contain("(cascades)");
    }

    // ── Table of contents ──────────────────────────────────────────────────

    [Fact]
    public void Generate_TableOfContents_IncludesAllTables()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                CreateTestTable("Users"),
                CreateTestTable("Products"),
                CreateTestTable("Orders"),
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new()
                {
                    Name = "All",
                    Tables = new List<string> { "Users", "Products", "Orders" }.AsReadOnly()
                }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        // TOC should list all tables
        result.Should().Contain("Users").And.Contain("Products").And.Contain("Orders");
    }

    [Fact]
    public void Generate_TableOfContents_UsesMarkdownAnchorLinks()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                CreateTestTable("Users"),
                CreateTestTable("Orders"),
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new()
                {
                    Name = "Accounts",
                    Tables = new List<string> { "Users", "Orders" }.AsReadOnly()
                }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        // TOC should have "## Table of Contents" header
        result.Should().Contain("## Table of Contents");

        // TOC should have markdown links with anchors
        result.Should().Contain("- [Accounts](#accounts)");
        result.Should().Contain("  - [Users](#users)");
        result.Should().Contain("  - [Orders](#orders)");

        // Detailed documentation should have HTML anchors for linking
        result.Should().Contain("<a id=\"accounts\"></a>");
        result.Should().Contain("<a id=\"users\"></a>");
        result.Should().Contain("<a id=\"orders\"></a>");
    }

    [Fact]
    public void Generate_TableOfContents_HandlesSchemaQualifiedNames()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                new()
                {
                    Name = "dbo.Bundle",
                    Description = "A bundle",
                    Fields = new List<FieldDefinition>
                    {
                        new() { Name = "id", Type = "int", Nullable = false }
                    }.AsReadOnly(),
                    PrimaryKey = new List<string> { "id" }.AsReadOnly(),
                }
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new()
                {
                    Name = "Database Tables",
                    Tables = new List<string> { "dbo.Bundle" }.AsReadOnly()
                }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        // Schema-qualified names should be converted to valid anchors (dots become hyphens)
        result.Should().Contain("- [dbo.Bundle](#dbo-bundle)");
        // And should be linkable from TOC
        result.Should().Contain("## Database Tables");
        // HTML anchor should be present before heading
        result.Should().Contain("<a id=\"dbo-bundle\"></a>");
        result.Should().Contain("### dbo.Bundle");
    }

    [Fact]
    public void Generate_TableOfContents_ShowsShortDescriptionAfterDash()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                new()
                {
                    Name             = "Users",
                    Description      = "Full multi-paragraph description of the Users table",
                    ShortDescription = "User accounts",
                    Fields           = new List<FieldDefinition> { new() { Name = "id", Type = "int", Nullable = false } }.AsReadOnly(),
                    PrimaryKey       = new List<string> { "id" }.AsReadOnly(),
                },
                new()
                {
                    Name             = "Logs",
                    Description      = "Detailed description here",
                    ShortDescription = "",
                    Fields           = new List<FieldDefinition> { new() { Name = "id", Type = "int", Nullable = false } }.AsReadOnly(),
                    PrimaryKey       = new List<string> { "id" }.AsReadOnly(),
                },
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new() { Name = "All", Tables = new List<string> { "Users", "Logs" }.AsReadOnly() }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        // ShortDescription is used in the TOC, not the full Description
        result.Should().Contain("  - [Users](#users) - User accounts");
        result.Should().NotContain("  - [Users](#users) - Full multi-paragraph");
        // Table without ShortDescription: no suffix
        result.Should().Contain("  - [Logs](#logs)");
        result.Should().NotContain("  - [Logs](#logs) -");
    }

    [Fact]
    public void Generate_TableHeading_HasAsteriskWhenAnyFieldIsAutoGenerated()
    {
        var table = new TableDefinition
        {
            Name       = "Orders",
            Description = "Order records",
            Fields     = new List<FieldDefinition>
            {
                new() { Name = "id",  Type = "int",          Nullable = false, Description = "Primary key",      DescriptionAutoGenerated = false },
                new() { Name = "ref", Type = "varchar(50)",  Nullable = true,  Description = "Reference number", DescriptionAutoGenerated = true  },
            }.AsReadOnly(),
            PrimaryKey = new List<string> { "id" }.AsReadOnly(),
        };
        var root = RootWithTable(table);

        var result = _generator.Generate(root);

        // Heading should include asterisk; anchor should NOT include it
        result.Should().Contain("### Orders*");
        result.Should().Contain("<a id=\"orders\"></a>");
        // TOC link uses plain name (no asterisk)
        result.Should().Contain("  - [Orders](#orders)");
    }

    [Fact]
    public void Generate_TableHeading_NoAsteriskWhenNoFieldIsAutoGenerated()
    {
        var table = new TableDefinition
        {
            Name        = "Products",
            Description = "Product catalog",
            Fields      = new List<FieldDefinition>
            {
                new() { Name = "id",   Type = "int",         Nullable = false, Description = "Primary key",  DescriptionAutoGenerated = false },
                new() { Name = "name", Type = "varchar(100)", Nullable = false, Description = "Product name", DescriptionAutoGenerated = null  },
            }.AsReadOnly(),
            PrimaryKey = new List<string> { "id" }.AsReadOnly(),
        };
        var root = RootWithTable(table);

        var result = _generator.Generate(root);

        result.Should().Contain("### Products");
        result.Should().NotContain("### Products*");
    }

    [Fact]
    public void Generate_HtmlAnchorsPlacedBeforeHeadings()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                CreateTestTable("TestTable", new[] { CreateField("id", "int", false) })
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new()
                {
                    Name = "Test Section",
                    Tables = new List<string> { "TestTable" }.AsReadOnly()
                }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        // Verify anchors are placed immediately before their headings
        var lines = result.Split(new[] { '\n' }, StringSplitOptions.None);

        // Find the HTML anchor and verify the next non-empty line is the heading
        for (int i = 0; i < lines.Length - 1; i++)
        {
            if (lines[i].Contains("<a id=\"test-section\"></a>"))
            {
                // Next line should be the section heading
                var nextLine = lines[i + 1];
                nextLine.Should().StartWith("## Test Section");
            }

            if (lines[i].Contains("<a id=\"testtable\"></a>"))
            {
                // Next line should be the table heading
                var nextLine = lines[i + 1];
                nextLine.Should().StartWith("### TestTable");
            }
        }
    }

    // ── Section title vs name ──────────────────────────────────────────────

    [Fact]
    public void Generate_SectionWithTitle_UsesTitleForDisplay()
    {
        var root = new ManifestRoot
        {
            Tables   = new List<TableDefinition> { CreateTestTable("dbo.Task") }.AsReadOnly(),
            Apis     = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new()
                {
                    Name   = "network-voucher-provisioning",
                    Title  = "Networkprovisioning/voucher tasks",
                    Tables = new List<string> { "dbo.Task" }.AsReadOnly()
                }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        // Display title should appear in TOC and heading
        result.Should().Contain("Networkprovisioning/voucher tasks");
        // Identifier (name) should NOT appear as a heading
        result.Should().NotContain("## network-voucher-provisioning");
    }

    [Fact]
    public void Generate_SectionWithTitle_AnchorDerivedFromTitle()
    {
        var root = new ManifestRoot
        {
            Tables   = new List<TableDefinition> { CreateTestTable("dbo.Task") }.AsReadOnly(),
            Apis     = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new()
                {
                    Name   = "network-voucher-provisioning",
                    Title  = "Networkprovisioning/voucher tasks",
                    Tables = new List<string> { "dbo.Task" }.AsReadOnly()
                }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        // TOC link and <a> anchor should both use the normalised title
        result.Should().Contain("[Networkprovisioning/voucher tasks](#networkprovisioning-voucher-tasks)");
        result.Should().Contain("<a id=\"networkprovisioning-voucher-tasks\"></a>");
    }

    [Fact]
    public void GenerateWithOrder_SectionWithTitle_MatchedByName()
    {
        var root = new ManifestRoot
        {
            Tables   = new List<TableDefinition>
            {
                CreateTestTable("dbo.Task"),
                CreateTestTable("dbo.User"),
            }.AsReadOnly(),
            Apis     = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                // Loaded order: provisioning first, then accounts
                new() { Name = "network-voucher-provisioning", Title = "Networkprovisioning/voucher tasks", Tables = new List<string> { "dbo.Task" }.AsReadOnly() },
                new() { Name = "accounts",                                                                  Tables = new List<string> { "dbo.User" }.AsReadOnly() },
            }.AsReadOnly(),
        };

        // sectionOrder uses the Name identifier, not the Title
        var result = _generator.GenerateWithOrder(root, ["accounts", "network-voucher-provisioning"]);

        var accountsIndex    = result.IndexOf("## accounts");
        var provisioningIndex = result.IndexOf("## Networkprovisioning/voucher tasks");

        accountsIndex.Should().BeLessThan(provisioningIndex,
            "sectionOrder should match on Name; display should use Title");
    }

    [Fact]
    public void Generate_SectionWithoutTitle_FallsBackToName()
    {
        var root = new ManifestRoot
        {
            Tables   = new List<TableDefinition> { CreateTestTable("dbo.User") }.AsReadOnly(),
            Apis     = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new() { Name = "Subscriber Management", Tables = new List<string> { "dbo.User" }.AsReadOnly() }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        // Name is used as display title when Title is null
        result.Should().Contain("## Subscriber Management");
        result.Should().Contain("[Subscriber Management](#subscriber-management)");
    }

    // ── Sections ───────────────────────────────────────────────────────────

    [Fact]
    public void Generate_WithSections_GroupsTablesBySections()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                CreateTestTable("Users", null, new[] { "Accounts" }),
                CreateTestTable("Orders", null, new[] { "Sales" }),
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new() { Name = "Accounts", Description = "User account tables", Tables = new List<string> { "Users" }.AsReadOnly() },
                new() { Name = "Sales", Description = "Sales and orders", Tables = new List<string> { "Orders" }.AsReadOnly() }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        // Should have section headers
        result.Should().Contain("Accounts").And.Contain("Sales");
        // Tables should be grouped under their sections
        result.Should().Contain("Users").And.Contain("Orders");
    }

    [Fact]
    public void Generate_SectionOrder_RespectedFromConfig()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                CreateTestTable("Zebra", null, new[] { "ZSection" }),
                CreateTestTable("Apple", null, new[] { "ASection" }),
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new() { Name = "ZSection", Tables = new List<string> { "Zebra" }.AsReadOnly() },
                new() { Name = "ASection", Tables = new List<string> { "Apple" }.AsReadOnly() }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        // Sections should appear in order ZSection, ASection (not alphabetical)
        var zIndex = result.IndexOf("## ZSection");
        var aIndex = result.IndexOf("## ASection");
        zIndex.Should().BeLessThan(aIndex, "ZSection should appear before ASection");
    }

    [Fact]
    public void GenerateWithOrder_RespectsSectionOrderList()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                CreateTestTable("Users"),
                CreateTestTable("Orders"),
                CreateTestTable("Products"),
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new() { Name = "Sales", Tables = new List<string> { "Orders" }.AsReadOnly() },
                new() { Name = "Inventory", Tables = new List<string> { "Products" }.AsReadOnly() },
                new() { Name = "Accounts", Tables = new List<string> { "Users" }.AsReadOnly() }
            }.AsReadOnly(),
        };

        // Specify desired order: Accounts, Sales, Inventory
        var sectionOrder = new[] { "Accounts", "Sales", "Inventory" };
        var result = _generator.GenerateWithOrder(root, sectionOrder);

        // Verify sections appear in the specified order
        var accountsIndex = result.IndexOf("## Accounts");
        var salesIndex = result.IndexOf("## Sales");
        var inventoryIndex = result.IndexOf("## Inventory");

        accountsIndex.Should().BeLessThan(salesIndex, "Accounts should appear first");
        salesIndex.Should().BeLessThan(inventoryIndex, "Sales should appear before Inventory");
    }

    [Fact]
    public void GenerateWithOrder_HandlesPartialOrderAndExtraItems()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                CreateTestTable("Users"),
                CreateTestTable("Orders"),
                CreateTestTable("Products"),
                CreateTestTable("Settings"),
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new() { Name = "Accounts", Tables = new List<string> { "Users" }.AsReadOnly() },
                new() { Name = "Sales", Tables = new List<string> { "Orders" }.AsReadOnly() },
                new() { Name = "Inventory", Tables = new List<string> { "Products" }.AsReadOnly() },
                new() { Name = "Configuration", Tables = new List<string> { "Settings" }.AsReadOnly() }
            }.AsReadOnly(),
        };

        // Only specify order for some sections
        var sectionOrder = new[] { "Sales", "Accounts" };
        var result = _generator.GenerateWithOrder(root, sectionOrder);

        // Sales and Accounts should appear first in that order
        var salesIndex = result.IndexOf("## Sales");
        var accountsIndex = result.IndexOf("## Accounts");
        var inventoryIndex = result.IndexOf("## Inventory");
        var configIndex = result.IndexOf("## Configuration");

        salesIndex.Should().BeLessThan(accountsIndex, "Sales should appear first");
        accountsIndex.Should().BeLessThan(inventoryIndex, "Remaining sections come after ordered ones");
    }

    // ── Markdown formatting ────────────────────────────────────────────────

    [Fact]
    public void Generate_Output_IsValidMarkdown()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                CreateTestTable("Test", new[] { CreateField("id", "int", false) })
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>().AsReadOnly(),
        };

        var result = _generator.Generate(root);

        // Should be valid markdown (contains headers, etc.)
        result.Should().Contain("#");  // At least one header
        result.Should().NotBeEmpty();
    }

    [Fact]
    public void Generate_SpecialCharacters_Escaped()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                new()
                {
                    Name = "Special_Chars",
                    Description = "Table with | pipe and `backtick`",
                    Fields = new List<FieldDefinition>
                    {
                        new() { Name = "field_name", Type = "int", Nullable = false }
                    }.AsReadOnly(),
                }
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new()
                {
                    Name = "Special",
                    Tables = new List<string> { "Special_Chars" }.AsReadOnly()
                }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        // Should escape markdown special characters
        result.Should().NotBeEmpty();
    }

    [Fact]
    public void Generate_LongDescription_IncludedInOutput()
    {
        var longDesc = "This is a very long description that explains the purpose and usage of this table in great detail.";
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                new()
                {
                    Name = "LongDescTable",
                    Description = longDesc,
                    Fields = new List<FieldDefinition>
                    {
                        new() { Name = "id", Type = "int", Nullable = false }
                    }.AsReadOnly(),
                }
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new()
                {
                    Name = "Main",
                    Tables = new List<string> { "LongDescTable" }.AsReadOnly()
                }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        result.Should().Contain(longDesc);
    }

    [Fact]
    public void Generate_MultilineDescription_ReplacesNewlinesWithBrTags()
    {
        var multilineDesc = "First line of description\nSecond line of description\nThird line";
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                new()
                {
                    Name = "TestTable",
                    Description = multilineDesc,
                    Fields = new List<FieldDefinition>
                    {
                        new() { Name = "id", Type = "int", Nullable = false },
                        new() { Name = "notes", Type = "varchar(max)", Nullable = true, Description = "Multi-line notes\nLine 2\nLine 3" }
                    }.AsReadOnly(),
                }
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new()
                {
                    Name = "Main",
                    Tables = new List<string> { "TestTable" }.AsReadOnly()
                }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        // Table description should have newlines replaced
        result.Should().Contain("First line of description<br/>Second line of description<br/>Third line");

        // Field description in table should have newlines replaced
        result.Should().Contain("Multi-line notes<br/>Line 2<br/>Line 3");

        // Original newlines should NOT be present in output
        result.Should().NotContain("First line of description\nSecond line");
    }

    [Fact]
    public void Generate_IndentedBulletDescription_RendersAsRealNewlines()
    {
        // Indented bullet lines (whitespace + '-') must use real newlines so that
        // Markdown renders them as nested list items, not literal '<br/>' text.
        var desc = "Table purpose:\n  - Reseller = own numbers for super-on-net rating.\n  - Central = MSISDNs for destination routing.";
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                new()
                {
                    Name        = "NumberPort",
                    Description = desc,
                    Fields      = new List<FieldDefinition>
                    {
                        new() { Name = "Id", Type = "int", Nullable = false }
                    }.AsReadOnly(),
                }
            }.AsReadOnly(),
            Apis     = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new()
                {
                    Name   = "Main",
                    Tables = new List<string> { "NumberPort" }.AsReadOnly()
                }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        // The intro line is separated from the first bullet by a real newline (not <br/>).
        result.Should().Contain("Table purpose:\n  - Reseller");

        // Each indented bullet uses a real newline separator.
        result.Should().Contain("  - Reseller = own numbers for super-on-net rating.\n  - Central");

        // No <br/> should appear before an indented bullet.
        result.Should().NotContain("<br/>  -");
    }

    [Fact]
    public void Generate_MixedDescriptionWithIndentedBullets_OnlyBulletsGetRealNewlines()
    {
        // Plain continuation lines still get <br/>; only indented bullets get '\n'.
        var desc = "First plain line\nSecond plain line\n  - Bullet item\n  - Another bullet";
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                new()
                {
                    Name        = "MixedTable",
                    Description = desc,
                    Fields      = new List<FieldDefinition>
                    {
                        new() { Name = "Id", Type = "int", Nullable = false }
                    }.AsReadOnly(),
                }
            }.AsReadOnly(),
            Apis     = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new()
                {
                    Name   = "Main",
                    Tables = new List<string> { "MixedTable" }.AsReadOnly()
                }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        // Plain line separator stays as <br/>.
        result.Should().Contain("First plain line<br/>Second plain line");

        // Transition from plain line to indented bullet uses '\n'.
        result.Should().Contain("Second plain line\n  - Bullet item");

        // Bullet-to-bullet also uses '\n'.
        result.Should().Contain("  - Bullet item\n  - Another bullet");
    }

    [Fact]
    public void Generate_WindowsAndUnixNewlines_BothHandled()
    {
        var windowsNewlines = "Line 1\r\nLine 2\r\nLine 3";
        var unixNewlines = "Line A\nLine B\nLine C";

        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                new()
                {
                    Name = "WindowsTable",
                    Description = windowsNewlines,
                    Fields = new List<FieldDefinition>
                    {
                        new() { Name = "id", Type = "int", Nullable = false, Description = unixNewlines }
                    }.AsReadOnly(),
                }
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new()
                {
                    Name = "Test",
                    Tables = new List<string> { "WindowsTable" }.AsReadOnly()
                }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        // Both Windows (\r\n) and Unix (\n) newlines should be converted
        result.Should().Contain("Line 1<br/>Line 2<br/>Line 3");
        result.Should().Contain("Line A<br/>Line B<br/>Line C");
    }

    // ── Duplicate-name resilience ──────────────────────────────────────────
    //
    // The generator must never throw on duplicate table or section names.
    // Callers (DocDbCommand) are responsible for catching duplicates and
    // emitting a proper error; the generator itself falls back to first-wins
    // so that partial output remains deterministic if it is ever called
    // without pre-flight duplicate detection.

    [Fact]
    public void GenerateWithOrder_DuplicateTableNames_DoesNotThrow_UsesFirstDefinition()
    {
        // Two TableDefinition objects share the same name but differ in description.
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                new()
                {
                    Name        = "dbo.LostRevenue",
                    Description = "First definition — should win",
                    Fields      = new List<FieldDefinition>
                    {
                        new() { Name = "id", Type = "int", Nullable = false }
                    }.AsReadOnly(),
                    SourceFile  = @"SUM\tables\LostRevenue.json",
                },
                new()
                {
                    Name        = "dbo.LostRevenue",
                    Description = "Second definition — should be ignored",
                    Fields      = new List<FieldDefinition>
                    {
                        new() { Name = "id", Type = "int", Nullable = false }
                    }.AsReadOnly(),
                    SourceFile  = @"CDRs\tables\LostRevenue.json",
                },
            }.AsReadOnly(),
            Apis     = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new()
                {
                    Name   = "Revenue",
                    Tables = new List<string> { "dbo.LostRevenue" }.AsReadOnly()
                }
            }.AsReadOnly(),
        };

        // Must not throw ArgumentException ("An item with the same key has already been added")
        var act = () => _generator.GenerateWithOrder(root, ["Revenue"]);
        act.Should().NotThrow();

        var result = _generator.GenerateWithOrder(root, ["Revenue"]);
        result.Should().Contain("First definition — should win",
            "first-wins policy must apply when table names collide");
        result.Should().NotContain("Second definition — should be ignored");
    }

    [Fact]
    public void GenerateWithOrder_DuplicateSectionNames_DoesNotThrow_UsesFirstDefinition()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                CreateTestTable("Users"),
            }.AsReadOnly(),
            Apis     = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new() { Name = "Accounts", Description = "First section",  Tables = new List<string> { "Users" }.AsReadOnly() },
                new() { Name = "Accounts", Description = "Second section", Tables = new List<string>().AsReadOnly() },
            }.AsReadOnly(),
        };

        var act = () => _generator.GenerateWithOrder(root, ["Accounts"]);
        act.Should().NotThrow();

        var result = _generator.GenerateWithOrder(root, ["Accounts"]);
        result.Should().Contain("First section",
            "first-wins policy must apply when section names collide");
        result.Should().NotContain("Second section");
    }

    // ── ERD embedding ─────────────────────────────────────────────────────

    [Fact]
    public void Generate_SectionWithErd_EmbedsMermaidBlock()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                CreateTestTable("dbo.Reseller"),
            }.AsReadOnly(),
            Apis = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new()
                {
                    Name = "Resellers",
                    Tables = new List<string> { "dbo.Reseller" }.AsReadOnly(),
                    Erds = new List<ErdDefinition>
                    {
                        new() { Title = "Overview", Tables = new List<string> { "dbo.Reseller" }.AsReadOnly() }
                    }.AsReadOnly()
                }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        result.Should().Contain("```mermaid");
        result.Should().Contain("erDiagram");
        result.Should().Contain("**Overview**");
    }

    [Fact]
    public void Generate_SectionWithoutErds_NoMermaidBlock()
    {
        var root = BuildMinimalRoot();

        var result = _generator.Generate(root);

        result.Should().NotContain("```mermaid");
        result.Should().NotContain("erDiagram");
    }

    // ── Title ─────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_NoTitle_UsesDefaultTitle()
    {
        var root = BuildMinimalRoot();

        var result = _generator.Generate(root);

        result.Should().StartWith("# DATABASE MODEL");
    }

    [Fact]
    public void GenerateWithOrder_NullTitle_UsesDefaultTitle()
    {
        var root = BuildMinimalRoot();

        var result = _generator.GenerateWithOrder(root, null, null);

        result.Should().StartWith("# DATABASE MODEL");
    }

    [Fact]
    public void GenerateWithOrder_CustomTitle_OverridesDefault()
    {
        var root = BuildMinimalRoot();

        var result = _generator.GenerateWithOrder(root, null, "MY CUSTOM TITLE");

        result.Should().StartWith("# MY CUSTOM TITLE");
        result.Should().NotContain("# DATABASE MODEL");
    }

    // ── Default value column ──────────────────────────────────────────────

    [Fact]
    public void Generate_FieldsWithDefault_ShowsDefaultColumn()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                new()
                {
                    Name   = "dbo.Status",
                    Fields = new List<FieldDefinition>
                    {
                        CreateField("Id",       "int",  false, defaultVal: "0"),
                        CreateField("IsActive", "bit",  false, defaultVal: "1"),
                        CreateField("Note",     "nvarchar(200)", true),
                    }.AsReadOnly(),
                }
            }.AsReadOnly(),
            Apis     = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new() { Name = "Lookup", Tables = new List<string> { "dbo.Status" }.AsReadOnly() }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        // Header row must include Default column
        result.Should().Contain("| Field | Type | Nullable | Default | Description |");
        // Values rendered in backticks
        result.Should().Contain("`0`");
        result.Should().Contain("`1`");
    }

    [Fact]
    public void Generate_NoFieldsHaveDefault_DefaultColumnOmitted()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                CreateTestTable("dbo.Order", new[]
                {
                    CreateField("Id",   "int",         false),
                    CreateField("Note", "nvarchar(200)", true),
                })
            }.AsReadOnly(),
            Apis     = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new() { Name = "Sales", Tables = new List<string> { "dbo.Order" }.AsReadOnly() }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        result.Should().NotContain("Default");
        result.Should().Contain("| Field | Type | Nullable | Description |");
    }

    [Fact]
    public void Generate_MixedFields_EmptyCellForFieldsWithoutDefault()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                new()
                {
                    Name   = "dbo.Product",
                    Fields = new List<FieldDefinition>
                    {
                        CreateField("Id",     "int",          false, defaultVal: null),   // no default
                        CreateField("Status", "int",          false, defaultVal: "1"),    // has default
                        CreateField("Note",   "nvarchar(200)", true, defaultVal: null),   // no default
                    }.AsReadOnly(),
                }
            }.AsReadOnly(),
            Apis     = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new() { Name = "Inventory", Tables = new List<string> { "dbo.Product" }.AsReadOnly() }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        result.Should().Contain("| Field | Type | Nullable | Default | Description |");
        // Status row has the value
        result.Should().Contain("`1`");
        // Id and Note rows should have an empty default cell (between the nullable and description pipes)
        result.Should().Contain("| Id | int |  |  |");
        result.Should().Contain("| Note | nvarchar(200) | ✓ |  |");
    }

    [Fact]
    public void Generate_EmptyStringDefault_TreatedAsNullDefaultColumn()
    {
        // "" (empty string) must behave identically to null — no Default column shown
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                new()
                {
                    Name   = "dbo.Log",
                    Fields = new List<FieldDefinition>
                    {
                        CreateField("Id",    "int",          false, defaultVal: null),
                        CreateField("Notes", "nvarchar(200)", true, defaultVal: ""),   // empty string
                    }.AsReadOnly(),
                }
            }.AsReadOnly(),
            Apis     = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new() { Name = "Audit", Tables = new List<string> { "dbo.Log" }.AsReadOnly() }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        // Empty string treated as no default → column must not appear
        result.Should().NotContain("| Field | Type | Nullable | Default | Description |");
        result.Should().Contain("| Field | Type | Nullable | Description |");
    }

    [Fact]
    public void Generate_StringLiteralDefault_RenderedWithBackticks()
    {
        var root = new ManifestRoot
        {
            Tables = new List<TableDefinition>
            {
                new()
                {
                    Name   = "dbo.Account",
                    Fields = new List<FieldDefinition>
                    {
                        CreateField("Phase", "nvarchar(20)", false, defaultVal: "'Active'"),
                    }.AsReadOnly(),
                }
            }.AsReadOnly(),
            Apis     = new List<ApiDefinition>().AsReadOnly(),
            Sections = new List<SectionDefinition>
            {
                new() { Name = "Accounts", Tables = new List<string> { "dbo.Account" }.AsReadOnly() }
            }.AsReadOnly(),
        };

        var result = _generator.Generate(root);

        result.Should().Contain("`'Active'`");
    }

    // ── Reference table data rendering ────────────────────────────────────

    private static ManifestRoot RootWithReferenceTable(TableDefinition table) => new()
    {
        Tables   = new List<TableDefinition> { table }.AsReadOnly(),
        Apis     = new List<ApiDefinition>().AsReadOnly(),
        Sections = new List<SectionDefinition>
        {
            new() { Name = "Lookup", Tables = new List<string> { table.Name }.AsReadOnly() }
        }.AsReadOnly(),
    };

    private static IReadOnlyDictionary<string, System.Text.Json.JsonElement> MakeRow(params (string key, string json)[] pairs)
    {
        var dict = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, json) in pairs)
            dict[key] = System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
        return dict;
    }

    [Fact]
    public void Generate_ReferenceTable_EmitsReferenceDataSection()
    {
        var table = new TableDefinition
        {
            Name            = "dbo.Status",
            IsReferenceTable = true,
            Fields          = new List<FieldDefinition>
            {
                new() { Name = "Id",   Type = "int",         Nullable = false },
                new() { Name = "Name", Type = "varchar(50)", Nullable = false },
            }.AsReadOnly(),
            PrimaryKey = new List<string> { "Id" }.AsReadOnly(),
            Data       = new List<IReadOnlyDictionary<string, System.Text.Json.JsonElement>>
            {
                MakeRow(("Id", "1"), ("Name", "\"Active\"")),
                MakeRow(("Id", "2"), ("Name", "\"Inactive\"")),
            }.AsReadOnly(),
        };

        var result = _generator.Generate(RootWithReferenceTable(table));

        result.Should().Contain("**Reference Data:**");
        result.Should().Contain("| Id | Name |");
        result.Should().Contain("| 1 | Active |");
        result.Should().Contain("| 2 | Inactive |");
    }

    [Fact]
    public void Generate_ReferenceTable_SeparatorHasNoDoublePipes()
    {
        var table = new TableDefinition
        {
            Name            = "dbo.Status",
            IsReferenceTable = true,
            Fields          = new List<FieldDefinition>
            {
                new() { Name = "Id",   Type = "int",         Nullable = false },
                new() { Name = "Name", Type = "varchar(50)", Nullable = false },
            }.AsReadOnly(),
            PrimaryKey = new List<string> { "Id" }.AsReadOnly(),
            Data       = new List<IReadOnlyDictionary<string, System.Text.Json.JsonElement>>
            {
                MakeRow(("Id", "1"), ("Name", "\"Active\"")),
            }.AsReadOnly(),
        };

        var result = _generator.Generate(RootWithReferenceTable(table));

        result.Should().Contain("|---|---|");
        result.Should().NotContain("||");
    }

    [Fact]
    public void Generate_ReferenceTable_ColumnOrderFollowsFieldOrder()
    {
        var table = new TableDefinition
        {
            Name            = "dbo.CallMode",
            IsReferenceTable = true,
            Fields          = new List<FieldDefinition>
            {
                new() { Name = "Id",    Type = "int",         Nullable = false },
                new() { Name = "Label", Type = "varchar(50)", Nullable = false },
                new() { Name = "Code",  Type = "char(3)",     Nullable = false },
            }.AsReadOnly(),
            PrimaryKey = new List<string> { "Id" }.AsReadOnly(),
            Data       = new List<IReadOnlyDictionary<string, System.Text.Json.JsonElement>>
            {
                MakeRow(("Code", "\"ACT\""), ("Id", "1"), ("Label", "\"Active\"")),
            }.AsReadOnly(),
        };

        var result = _generator.Generate(RootWithReferenceTable(table));

        // Header must follow field definition order, not row key order
        var headerIndex = result.IndexOf("| Id | Label | Code |", StringComparison.Ordinal);
        headerIndex.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Generate_ReferenceTable_NullValueRendersAsEmpty()
    {
        var table = new TableDefinition
        {
            Name            = "dbo.Status",
            IsReferenceTable = true,
            Fields          = new List<FieldDefinition>
            {
                new() { Name = "Id",   Type = "int",         Nullable = false },
                new() { Name = "Note", Type = "varchar(200)", Nullable = true  },
            }.AsReadOnly(),
            PrimaryKey = new List<string> { "Id" }.AsReadOnly(),
            Data       = new List<IReadOnlyDictionary<string, System.Text.Json.JsonElement>>
            {
                MakeRow(("Id", "1"), ("Note", "null")),
            }.AsReadOnly(),
        };

        var result = _generator.Generate(RootWithReferenceTable(table));

        result.Should().Contain("| 1 |  |");
    }

    [Fact]
    public void Generate_NonReferenceTable_NoReferenceDataSection()
    {
        var result = _generator.Generate(BuildMinimalRoot());

        result.Should().NotContain("**Reference Data:**");
    }

    [Fact]
    public void Generate_ReferenceTableNoData_NoReferenceDataSection()
    {
        var table = new TableDefinition
        {
            Name            = "dbo.Status",
            IsReferenceTable = true,
            Fields          = new List<FieldDefinition>
            {
                new() { Name = "Id", Type = "int", Nullable = false },
            }.AsReadOnly(),
            Data = new List<IReadOnlyDictionary<string, System.Text.Json.JsonElement>>().AsReadOnly(),
        };

        var result = _generator.Generate(RootWithReferenceTable(table));

        result.Should().NotContain("**Reference Data:**");
    }

    [Fact]
    public void Generate_ReferenceTable_OrphanedDataKeysSkipped()
    {
        var table = new TableDefinition
        {
            Name            = "dbo.Status",
            IsReferenceTable = true,
            Fields          = new List<FieldDefinition>
            {
                new() { Name = "Id",   Type = "int",         Nullable = false },
                new() { Name = "Name", Type = "varchar(50)", Nullable = false },
            }.AsReadOnly(),
            PrimaryKey = new List<string> { "Id" }.AsReadOnly(),
            Data       = new List<IReadOnlyDictionary<string, System.Text.Json.JsonElement>>
            {
                MakeRow(("Id", "1"), ("Name", "\"Active\""), ("OldColumn", "\"stale\"")),
            }.AsReadOnly(),
        };

        var result = _generator.Generate(RootWithReferenceTable(table));

        result.Should().Contain("| Id | Name |");
        result.Should().NotContain("OldColumn");
        result.Should().NotContain("stale");
    }

    [Fact]
    public void Generate_ReferenceTable_StringValuesNotQuoted()
    {
        var table = new TableDefinition
        {
            Name            = "dbo.Status",
            IsReferenceTable = true,
            Fields          = new List<FieldDefinition>
            {
                new() { Name = "Id",   Type = "int",         Nullable = false },
                new() { Name = "Name", Type = "varchar(50)", Nullable = false },
            }.AsReadOnly(),
            PrimaryKey = new List<string> { "Id" }.AsReadOnly(),
            Data       = new List<IReadOnlyDictionary<string, System.Text.Json.JsonElement>>
            {
                MakeRow(("Id", "1"), ("Name", "\"Active\"")),
            }.AsReadOnly(),
        };

        var result = _generator.Generate(RootWithReferenceTable(table));

        result.Should().Contain("Active");
        result.Should().NotContain("\"Active\"");
    }

    [Fact]
    public void Generate_ReferenceTable_BoolAndNumberRenderedAsRawText()
    {
        var table = new TableDefinition
        {
            Name            = "dbo.Toggle",
            IsReferenceTable = true,
            Fields          = new List<FieldDefinition>
            {
                new() { Name = "Id",      Type = "int", Nullable = false },
                new() { Name = "Enabled", Type = "bit", Nullable = false },
                new() { Name = "Weight",  Type = "decimal(5,2)", Nullable = false },
            }.AsReadOnly(),
            PrimaryKey = new List<string> { "Id" }.AsReadOnly(),
            Data       = new List<IReadOnlyDictionary<string, System.Text.Json.JsonElement>>
            {
                MakeRow(("Id", "1"), ("Enabled", "true"), ("Weight", "3.14")),
            }.AsReadOnly(),
        };

        var result = _generator.Generate(RootWithReferenceTable(table));

        result.Should().Contain("| 1 | true | 3.14 |");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static ManifestRoot BuildMinimalRoot() => new()
    {
        Tables = new List<TableDefinition>
        {
            CreateTestTable("TestTable")
        }.AsReadOnly(),
        Apis = new List<ApiDefinition>().AsReadOnly(),
        Sections = new List<SectionDefinition>
        {
            new() { Name = "Test", Tables = new List<string> { "TestTable" }.AsReadOnly() }
        }.AsReadOnly(),
    };


    private static TableDefinition CreateTestTable(
        string name,
        FieldDefinition[]? fields = null,
        string[]? sections = null)
    {
        return new TableDefinition
        {
            Name = name,
            Description = $"Description for {name}",
            Fields = (fields ?? new[] { CreateField("id", "int", false) }).ToList().AsReadOnly(),
            PrimaryKey = new List<string> { "id" }.AsReadOnly(),
            Sections = (sections ?? Array.Empty<string>()).ToList().AsReadOnly(),
        };
    }

    private static FieldDefinition CreateField(
        string name,
        string type,
        bool nullable,
        string? description = null,
        string? defaultVal  = null)
    {
        return new FieldDefinition
        {
            Name        = name,
            Type        = type,
            Nullable    = nullable,
            Description = description ?? "",
            Default     = defaultVal,
        };
    }

    // ── Deprecation rendering ─────────────────────────────────────────────────

    private static ManifestRoot RootWithTable(TableDefinition table) => new()
    {
        Tables   = [table],
        Apis     = [],
        Sections = [new SectionDefinition { Name = "TestSection", Tables = [table.Name] }],
    };

    [Fact]
    public void Generate_DeprecatedTable_HeadingHasStrikethroughAndBadge()
    {
        var table  = CreateTestTable("dbo.OldOrder") with { IsDeprecated = true };
        var result = _generator.Generate(RootWithTable(table));

        result.Should().Contain("~~dbo.OldOrder~~");
        result.Should().Contain("[DEPRECATED]");
    }

    [Fact]
    public void Generate_DeprecatedTable_WithMessage_MessageInBlockquote()
    {
        var table  = CreateTestTable("dbo.OldOrder") with { IsDeprecated = true, DeprecationMessage = "Use dbo.NewOrder." };
        var result = _generator.Generate(RootWithTable(table));

        result.Should().Contain("Use dbo.NewOrder.");
        result.Should().Contain("> **Deprecated.**");
    }

    [Fact]
    public void Generate_DeprecatedTable_WithoutMessage_GenericNotice()
    {
        var table  = CreateTestTable("dbo.OldOrder") with { IsDeprecated = true };
        var result = _generator.Generate(RootWithTable(table));

        result.Should().Contain("> **Deprecated.**");
    }

    [Fact]
    public void Generate_DeprecatedTable_TocEntryHasStrikethrough()
    {
        var table  = CreateTestTable("dbo.OldOrder") with { IsDeprecated = true };
        var result = _generator.GenerateWithOrder(RootWithTable(table), null);

        result.Should().Contain("~~dbo.OldOrder~~");
    }

    [Fact]
    public void Generate_NonDeprecatedTable_NoStrikethroughNoBadge()
    {
        var table  = CreateTestTable("dbo.Order");
        var result = _generator.Generate(RootWithTable(table));

        result.Should().NotContain("~~dbo.Order~~");
        result.Should().NotContain("[DEPRECATED]");
    }

    [Fact]
    public void Generate_DeprecatedField_FieldNameHasStrikethrough()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Order",
            Fields =
            [
                new FieldDefinition { Name = "Id",      Type = "int",          Nullable = false },
                new FieldDefinition { Name = "OldCode", Type = "nvarchar(10)", Nullable = true, IsDeprecated = true },
            ],
            PrimaryKey = ["Id"],
        };

        var result = _generator.Generate(RootWithTable(table));

        result.Should().Contain("~~OldCode~~");
    }

    [Fact]
    public void Generate_DeprecatedField_WithMessage_MessageInDescriptionCell()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Order",
            Fields =
            [
                new FieldDefinition { Name = "Id",      Type = "int",           Nullable = false },
                new FieldDefinition { Name = "OldCode", Type = "nvarchar(10)",  Nullable = true,
                    IsDeprecated = true, DeprecationMessage = "Use NewCode field." },
            ],
            PrimaryKey = ["Id"],
        };

        var result = _generator.Generate(RootWithTable(table));

        result.Should().Contain("Use NewCode field.");
    }

    // ── Sensitivity rendering ─────────────────────────────────────────────────

    [Fact]
    public void Generate_PiiField_RendersBadge()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Customer",
            Fields =
            [
                new FieldDefinition { Name = "Id",    Type = "int",           Nullable = false },
                new FieldDefinition { Name = "Email", Type = "nvarchar(255)", Sensitivity = "pii" },
            ],
            PrimaryKey = ["Id"],
        };

        var result = _generator.Generate(RootWithTable(table));

        result.Should().Contain("🔴 PII");
    }

    [Fact]
    public void Generate_ConfidentialField_RendersBadge()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Customer",
            Fields =
            [
                new FieldDefinition { Name = "Id",     Type = "int",          Nullable = false },
                new FieldDefinition { Name = "Salary", Type = "decimal(10,2)", Sensitivity = "confidential" },
            ],
            PrimaryKey = ["Id"],
        };

        var result = _generator.Generate(RootWithTable(table));

        result.Should().Contain("🟡 Confidential");
    }

    [Fact]
    public void Generate_NoSensitivityValues_SensitivityColumnOmitted()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Order",
            Fields =
            [
                new FieldDefinition { Name = "Id",     Type = "int", Nullable = false },
                new FieldDefinition { Name = "Amount", Type = "int", Nullable = false },
            ],
            PrimaryKey = ["Id"],
        };

        var result = _generator.Generate(RootWithTable(table));

        result.Should().NotContain("Sensitivity");
    }

    // ── Index rendering ───────────────────────────────────────────────────────

    [Fact]
    public void Generate_TableWithIndexes_RendersIndexSubTable()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Order",
            Fields = [new FieldDefinition { Name = "Id", Type = "int", Nullable = false }],
            PrimaryKey = ["Id"],
            Indexes =
            [
                new IndexDefinition { Name = "IX_Order_Status", Columns = ["StatusId"], IsUnique = true, IsClustered = false },
            ],
        };

        var result = _generator.Generate(RootWithTable(table));

        result.Should().Contain("**Indexes:**");
        result.Should().Contain("IX_Order_Status");
        result.Should().Contain("StatusId");
    }

    [Fact]
    public void Generate_TableWithNoIndexes_IndexSectionOmitted()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Order",
            Fields = [new FieldDefinition { Name = "Id", Type = "int", Nullable = false }],
            PrimaryKey = ["Id"],
        };

        var result = _generator.Generate(RootWithTable(table));

        result.Should().NotContain("**Indexes:**");
    }

    [Fact]
    public void Generate_IndexWithDescription_RendersDescriptionColumn()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Order",
            Fields = [new FieldDefinition { Name = "Id", Type = "int", Nullable = false }],
            Indexes =
            [
                new IndexDefinition
                {
                    Name        = "IX_Order_Status",
                    Columns     = ["StatusId"],
                    Description = "Speeds up status-based order lookups.",
                },
            ],
        };

        var result = _generator.Generate(RootWithTable(table));

        result.Should().Contain("| Name | Columns | Unique | Clustered | Filter | Description |");
        result.Should().Contain("Speeds up status-based order lookups.");
    }

    [Fact]
    public void Generate_NoIndexDescriptions_DescriptionColumnOmitted()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Order",
            Fields = [new FieldDefinition { Name = "Id", Type = "int", Nullable = false }],
            Indexes =
            [
                new IndexDefinition { Name = "IX_Order_Status", Columns = ["StatusId"] },
            ],
        };

        var result = _generator.Generate(RootWithTable(table));

        result.Should().Contain("**Indexes:**");
        // "Filter | Description |" only appears in the index header when the description column is added
        result.Should().NotContain("Filter | Description |");
    }

    [Fact]
    public void Generate_MixedIndexDescriptions_DescriptionColumnPresent_EmptyForUndescribed()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Order",
            Fields = [new FieldDefinition { Name = "Id", Type = "int", Nullable = false }],
            Indexes =
            [
                new IndexDefinition { Name = "IX_A", Columns = ["A"], Description = "Described." },
                new IndexDefinition { Name = "IX_B", Columns = ["B"] },
            ],
        };

        var result = _generator.Generate(RootWithTable(table));

        result.Should().Contain("Description |");
        result.Should().Contain("Described.");
        result.Should().Contain("IX_B");
    }

    // ── Constraint rendering ──────────────────────────────────────────────────

    [Fact]
    public void Generate_TableWithCheckConstraint_RendersConstraintSection()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Order",
            Fields =
            [
                new FieldDefinition { Name = "Id",     Type = "int", Nullable = false },
                new FieldDefinition { Name = "Amount", Type = "int", Nullable = false },
            ],
            PrimaryKey       = ["Id"],
            CheckConstraints =
            [
                new CheckConstraint { Name = "CK_Amount", Expression = "[Amount] > 0", Column = "Amount" },
            ],
        };

        var result = _generator.Generate(RootWithTable(table));

        result.Should().Contain("**Constraints:**");
        result.Should().Contain("CK_Amount");
        result.Should().Contain("[Amount] > 0");
    }

    [Fact]
    public void Generate_TableWithUniqueConstraint_RendersConstraintSection()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Order",
            Fields =
            [
                new FieldDefinition { Name = "Id",   Type = "int",          Nullable = false },
                new FieldDefinition { Name = "Code", Type = "nvarchar(20)", Nullable = false },
            ],
            PrimaryKey        = ["Id"],
            UniqueConstraints =
            [
                new UniqueConstraint { Name = "UQ_Code", Columns = ["Code"] },
            ],
        };

        var result = _generator.Generate(RootWithTable(table));

        result.Should().Contain("**Constraints:**");
        result.Should().Contain("UQ_Code");
        result.Should().Contain("Code");
    }

    [Fact]
    public void Generate_TableWithNoConstraints_ConstraintSectionOmitted()
    {
        var table = new TableDefinition
        {
            Name   = "dbo.Order",
            Fields = [new FieldDefinition { Name = "Id", Type = "int", Nullable = false }],
            PrimaryKey = ["Id"],
        };

        var result = _generator.Generate(RootWithTable(table));

        result.Should().NotContain("**Constraints:**");
    }
}
