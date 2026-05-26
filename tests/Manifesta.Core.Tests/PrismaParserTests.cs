using FluentAssertions;
using Manifesta.Core;
using Manifesta.Core.IR;
using Xunit;

namespace Manifesta.Core.Tests;

/// <summary>
/// Unit tests for <see cref="PrismaParser"/>.
/// These tests are pure in-memory — no database container needed.
/// </summary>
public class PrismaParserTests
{
    // ── Basic model parsing ────────────────────────────────────────────────────

    [Fact]
    public void Parse_SingleModel_ReturnsOneTable()
    {
        const string schema = """
            model User {
              id    Int    @id
              email String
            }
            """;

        var result = new PrismaParser().Parse(schema);

        result.Tables.Should().ContainSingle(t => t.Name == "User");
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ModelName_IsUsedAsTableName()
    {
        const string schema = """
            model CustomerProfile {
              id Int @id
            }
            """;

        var result = new PrismaParser().Parse(schema);

        result.Tables.Should().ContainSingle().Which.Name.Should().Be("CustomerProfile");
    }

    [Fact]
    public void Parse_FieldTypes_SqlServerDefaults()
    {
        const string schema = """
            model Product {
              id          Int      @id
              name        String
              price       Decimal
              weight      Float
              isActive    Boolean
              createdAt   DateTime
              bigId       BigInt
              data        Bytes
              metadata    Json
            }
            """;

        var result = new PrismaParser().Parse(schema, DbProvider.SqlServer);
        var t = result.Tables.Single();

        t.Fields.Should().Contain(f => f.Name == "id"        && f.Type == "int");
        t.Fields.Should().Contain(f => f.Name == "name"      && f.Type == "nvarchar(max)");
        t.Fields.Should().Contain(f => f.Name == "price"     && f.Type == "decimal(18,6)");
        t.Fields.Should().Contain(f => f.Name == "weight"    && f.Type == "float");
        t.Fields.Should().Contain(f => f.Name == "isActive"  && f.Type == "bit");
        t.Fields.Should().Contain(f => f.Name == "createdAt" && f.Type == "datetime2");
        t.Fields.Should().Contain(f => f.Name == "bigId"     && f.Type == "bigint");
        t.Fields.Should().Contain(f => f.Name == "data"      && f.Type == "varbinary(max)");
        t.Fields.Should().Contain(f => f.Name == "metadata"  && f.Type == "nvarchar(max)");
    }

    [Fact]
    public void Parse_FieldTypes_MySqlDefaults()
    {
        const string schema = """
            model Product {
              id       Int     @id
              name     String
              price    Decimal
              weight   Float
              isActive Boolean
              created  DateTime
              data     Bytes
              meta     Json
            }
            """;

        var result = new PrismaParser().Parse(schema, DbProvider.MySql);
        var t = result.Tables.Single();

        t.Fields.Should().Contain(f => f.Name == "id"       && f.Type == "int");
        t.Fields.Should().Contain(f => f.Name == "name"     && f.Type == "varchar(191)");
        t.Fields.Should().Contain(f => f.Name == "price"    && f.Type == "decimal(18,6)");
        t.Fields.Should().Contain(f => f.Name == "weight"   && f.Type == "double");
        t.Fields.Should().Contain(f => f.Name == "isActive" && f.Type == "tinyint(1)");
        t.Fields.Should().Contain(f => f.Name == "created"  && f.Type == "datetime");
        t.Fields.Should().Contain(f => f.Name == "data"     && f.Type == "longblob");
        t.Fields.Should().Contain(f => f.Name == "meta"     && f.Type == "json");
    }

    [Fact]
    public void Parse_FieldTypes_PostgresDefaults()
    {
        const string schema = """
            model Product {
              id       Int     @id
              name     String
              price    Decimal
              weight   Float
              isActive Boolean
              created  DateTime
              data     Bytes
              meta     Json
            }
            """;

        var result = new PrismaParser().Parse(schema, DbProvider.Postgres);
        var t = result.Tables.Single();

        t.Fields.Should().Contain(f => f.Name == "id"       && f.Type == "integer");
        t.Fields.Should().Contain(f => f.Name == "name"     && f.Type == "text");
        t.Fields.Should().Contain(f => f.Name == "price"    && f.Type == "decimal(65,30)");
        t.Fields.Should().Contain(f => f.Name == "weight"   && f.Type == "double precision");
        t.Fields.Should().Contain(f => f.Name == "isActive" && f.Type == "boolean");
        t.Fields.Should().Contain(f => f.Name == "created"  && f.Type == "timestamp");
        t.Fields.Should().Contain(f => f.Name == "data"     && f.Type == "bytea");
        t.Fields.Should().Contain(f => f.Name == "meta"     && f.Type == "jsonb");
    }

    // ── Nullability ─────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_NullableField_IsNullable()
    {
        const string schema = """
            model User {
              id          Int     @id
              description String?
            }
            """;

        var result = new PrismaParser().Parse(schema);
        var t = result.Tables.Single();

        t.Fields.Should().Contain(f => f.Name == "description" && f.Nullable);
        t.Fields.Should().Contain(f => f.Name == "id" && !f.Nullable);
    }

    [Fact]
    public void Parse_IdField_IsNeverNullable()
    {
        const string schema = """
            model User {
              id Int? @id
            }
            """;

        var result = new PrismaParser().Parse(schema);
        result.Tables.Single().Fields.Single().Nullable.Should().BeFalse(
            "@id fields must never be nullable even if the type has '?'");
    }

    // ── Primary key ─────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_AtId_PopulatesPrimaryKey()
    {
        const string schema = """
            model Order {
              orderId Int @id
              total   Decimal
            }
            """;

        var result = new PrismaParser().Parse(schema);
        result.Tables.Single().PrimaryKey.Should().ContainSingle().Which.Should().Be("orderId");
    }

    [Fact]
    public void Parse_CompositePkViaModelAttribute_PopulatesPrimaryKey()
    {
        const string schema = """
            model OrderLine {
              orderId   Int
              productId Int
              qty       Int

              @@id([orderId, productId])
            }
            """;

        var result = new PrismaParser().Parse(schema);
        result.Tables.Single().PrimaryKey.Should().BeEquivalentTo(["orderId", "productId"],
            options => options.WithStrictOrdering());
    }

    // ── Native type overrides (@db.) ─────────────────────────────────────────────

    [Fact]
    public void Parse_DbVarChar_ReturnsVarcharWithLength()
    {
        const string schema = """
            model User {
              id    Int    @id
              email String @db.VarChar(255)
            }
            """;

        var result = new PrismaParser().Parse(schema);
        result.Tables.Single().Fields.Should().Contain(f => f.Name == "email" && f.Type == "varchar(255)");
    }

    [Fact]
    public void Parse_DbNVarChar_ReturnsNVarchar()
    {
        const string schema = """
            model Product {
              id   Int    @id
              name String @db.NVarChar(200)
            }
            """;

        var result = new PrismaParser().Parse(schema);
        result.Tables.Single().Fields.Should().Contain(f => f.Name == "name" && f.Type == "nvarchar(200)");
    }

    [Fact]
    public void Parse_DbDecimal_ReturnsDecimalWithPrecision()
    {
        const string schema = """
            model Invoice {
              id     Int     @id
              amount Decimal @db.Decimal(18, 4)
            }
            """;

        var result = new PrismaParser().Parse(schema);
        result.Tables.Single().Fields.Should().Contain(f => f.Name == "amount" && f.Type == "decimal(18, 4)");
    }

    [Fact]
    public void Parse_DbText_ReturnsText()
    {
        const string schema = """
            model Article {
              id      Int    @id
              content String @db.Text
            }
            """;

        var result = new PrismaParser().Parse(schema);
        result.Tables.Single().Fields.Should().Contain(f => f.Name == "content" && f.Type == "text");
    }

    [Fact]
    public void Parse_DbUniqueIdentifier_ReturnsUniqueIdentifier()
    {
        const string schema = """
            model Entity {
              id String @id @db.UniqueIdentifier
            }
            """;

        var result = new PrismaParser().Parse(schema);
        result.Tables.Single().Fields.Should().Contain(f => f.Name == "id" && f.Type == "uniqueidentifier");
    }

    // ── Default values ──────────────────────────────────────────────────────────

    [Fact]
    public void Parse_DefaultAutoincrement_IsNull()
    {
        const string schema = """
            model User {
              id Int @id @default(autoincrement())
            }
            """;

        var result = new PrismaParser().Parse(schema);
        result.Tables.Single().Fields.Single().Default.Should().BeNull();
    }

    [Fact]
    public void Parse_DefaultBooleanFalse_IsPreserved()
    {
        const string schema = """
            model User {
              id       Int     @id
              isActive Boolean @default(false)
            }
            """;

        var result = new PrismaParser().Parse(schema);
        result.Tables.Single().Fields.Should().Contain(f => f.Name == "isActive" && f.Default == "false");
    }

    [Fact]
    public void Parse_DefaultStringLiteral_IsStripped()
    {
        const string schema = """
            model Config {
              id    Int    @id
              value String @default("enabled")
            }
            """;

        var result = new PrismaParser().Parse(schema);
        result.Tables.Single().Fields.Should().Contain(f => f.Name == "value" && f.Default == "enabled");
    }

    [Fact]
    public void Parse_DefaultEnumValue_StripsPrefix()
    {
        const string schema = """
            enum Role {
              USER
              ADMIN
            }
            model User {
              id   Int  @id
              role Role @default(Role.USER)
            }
            """;

        var result = new PrismaParser().Parse(schema);
        result.Tables.Single().Fields.Should().Contain(f => f.Name == "role" && f.Default == "USER");
    }

    [Fact]
    public void Parse_DefaultCuid_IsNull()
    {
        const string schema = """
            model Post {
              id String @id @default(cuid())
            }
            """;

        var result = new PrismaParser().Parse(schema);
        result.Tables.Single().Fields.Single().Default.Should().BeNull();
    }

    // ── Foreign keys (@relation) ────────────────────────────────────────────────

    [Fact]
    public void Parse_Relation_ExtractsForeignKey()
    {
        const string schema = """
            model Post {
              id       Int  @id
              authorId Int
              author   User @relation(fields: [authorId], references: [id])
            }
            model User {
              id    Int    @id
              posts Post[]
            }
            """;

        var result = new PrismaParser().Parse(schema);
        var post = result.Tables.Single(t => t.Name == "Post");

        post.ForeignKeys.Should().ContainSingle(fk =>
            fk.SourceField == "authorId" &&
            fk.TargetTable == "User" &&
            fk.TargetField == "id" &&
            !fk.CascadeDelete);
    }

    [Fact]
    public void Parse_RelationCascadeDelete_SetsCascadeTrue()
    {
        const string schema = """
            model Comment {
              id     Int  @id
              postId Int
              post   Post @relation(fields: [postId], references: [id], onDelete: Cascade)
            }
            model Post {
              id       Int       @id
              comments Comment[]
            }
            """;

        var result = new PrismaParser().Parse(schema);
        var comment = result.Tables.Single(t => t.Name == "Comment");
        comment.ForeignKeys.Single().CascadeDelete.Should().BeTrue();
    }

    [Fact]
    public void Parse_RelationFieldsAndArrayFields_AreNotInFields()
    {
        const string schema = """
            model Post {
              id       Int    @id
              authorId Int
              author   User   @relation(fields: [authorId], references: [id])
              tags     Tag[]
            }
            model User  { id Int @id }
            model Tag   { id Int @id }
            """;

        var result = new PrismaParser().Parse(schema);
        var post = result.Tables.Single(t => t.Name == "Post");

        post.Fields.Should().NotContain(f => f.Name == "author");
        post.Fields.Should().NotContain(f => f.Name == "tags");
        post.Fields.Should().Contain(f => f.Name == "authorId");
    }

    // ── @@map table name override ────────────────────────────────────────────────

    [Fact]
    public void Parse_ModelWithMapAttribute_UsesMapAsTableName()
    {
        const string schema = """
            model User {
              id Int @id
              @@map("users")
            }
            """;

        var result = new PrismaParser().Parse(schema);
        result.Tables.Should().ContainSingle().Which.Name.Should().Be("users");
    }

    [Fact]
    public void Parse_FkTargetUsesMapName()
    {
        const string schema = """
            model Post {
              id       Int  @id
              authorId Int
              author   User @relation(fields: [authorId], references: [id])
              @@map("posts")
            }
            model User {
              id    Int    @id
              posts Post[]
              @@map("users")
            }
            """;

        var result = new PrismaParser().Parse(schema);
        var post = result.Tables.Single(t => t.Name == "posts");
        post.ForeignKeys.Single().TargetTable.Should().Be("users");
    }

    // ── Schema prefix ───────────────────────────────────────────────────────────

    [Fact]
    public void Parse_SchemaPrefix_IsPrependedToTableNames()
    {
        const string schema = """
            model User {
              id Int @id
            }
            """;

        var result = new PrismaParser().Parse(schema, schemaPrefix: "dbo");
        result.Tables.Single().Name.Should().Be("dbo.User");
    }

    [Fact]
    public void Parse_SchemaPrefix_IsPrependedToFkTargetTable()
    {
        const string schema = """
            model Post {
              id       Int  @id
              authorId Int
              author   User @relation(fields: [authorId], references: [id])
            }
            model User { id Int @id }
            """;

        var result = new PrismaParser().Parse(schema, schemaPrefix: "dbo");
        var post = result.Tables.Single(t => t.Name == "dbo.Post");
        post.ForeignKeys.Single().TargetTable.Should().Be("dbo.User");
    }

    // ── Unique constraints ──────────────────────────────────────────────────────

    [Fact]
    public void Parse_AtUnique_ProducesUniqueConstraint()
    {
        const string schema = """
            model User {
              id    Int    @id
              email String @unique
            }
            """;

        var result = new PrismaParser().Parse(schema);
        var t = result.Tables.Single();

        t.UniqueConstraints.Should().ContainSingle(uc => uc.Columns.Contains("email"));
    }

    [Fact]
    public void Parse_AtAtUnique_ProducesUniqueConstraint()
    {
        const string schema = """
            model User {
              id        Int    @id
              firstName String
              lastName  String
              @@unique([firstName, lastName])
            }
            """;

        var result = new PrismaParser().Parse(schema);
        var t = result.Tables.Single();

        t.UniqueConstraints.Should().ContainSingle(uc =>
            uc.Columns.Contains("firstName") && uc.Columns.Contains("lastName"));
    }

    // ── Indexes ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_AtAtIndex_ProducesIndex()
    {
        const string schema = """
            model Post {
              id       Int    @id
              authorId Int
              @@index([authorId], name: "idx_post_author")
            }
            """;

        var result = new PrismaParser().Parse(schema);
        var t = result.Tables.Single();

        t.Indexes.Should().ContainSingle(i =>
            i.Name == "idx_post_author" && i.Columns.Contains("authorId"));
    }

    [Fact]
    public void Parse_AtAtIndex_WithoutName_GeneratesName()
    {
        const string schema = """
            model Post {
              id       Int @id
              authorId Int
              @@index([authorId])
            }
            """;

        var result = new PrismaParser().Parse(schema);
        result.Tables.Single().Indexes.Should().ContainSingle(i =>
            i.Name.StartsWith("idx_") && i.Columns.Contains("authorId"));
    }

    // ── Datasource provider detection ────────────────────────────────────────────

    [Fact]
    public void Parse_DatasourceProviderMySql_UsesMysSqlTypes()
    {
        const string schema = """
            datasource db {
              provider = "mysql"
              url      = env("DATABASE_URL")
            }
            model User {
              id   Int    @id
              name String
            }
            """;

        var result = new PrismaParser().Parse(schema);
        result.Tables.Single().Fields.Should().Contain(f => f.Name == "name" && f.Type == "varchar(191)");
    }

    [Fact]
    public void Parse_DatasourceProviderPostgresql_UsesPostgresTypes()
    {
        const string schema = """
            datasource db {
              provider = "postgresql"
              url      = env("DATABASE_URL")
            }
            model User {
              id   Int    @id
              name String
            }
            """;

        var result = new PrismaParser().Parse(schema);
        result.Tables.Single().Fields.Should().Contain(f => f.Name == "name" && f.Type == "text");
    }

    [Fact]
    public void Parse_ProviderOverride_TakesPrecedenceOverDatasource()
    {
        const string schema = """
            datasource db {
              provider = "mysql"
              url      = env("DATABASE_URL")
            }
            model User {
              id   Int    @id
              name String
            }
            """;

        // Override with SqlServer — String should become nvarchar(max), not varchar(191)
        var result = new PrismaParser().Parse(schema, providerOverride: DbProvider.SqlServer);
        result.Tables.Single().Fields.Should().Contain(f => f.Name == "name" && f.Type == "nvarchar(max)");
    }

    // ── RelationMode → FK kind ───────────────────────────────────────────────────

    [Fact]
    public void Parse_RelationModePrisma_ProducesLogicalFk()
    {
        const string schema = """
            datasource db {
              provider     = "sqlserver"
              url          = env("DATABASE_URL")
              relationMode = "prisma"
            }
            model Post {
              id       Int  @id
              authorId Int
              author   User @relation(fields: [authorId], references: [id])
            }
            model User { id Int @id }
            """;

        var result = new PrismaParser().Parse(schema);
        result.Tables.Single(t => t.Name == "Post")
              .ForeignKeys.Single()
              .Kind.Should().Be(ForeignKeyKind.Logical);
    }

    [Fact]
    public void Parse_RelationModeForeignKeys_ProducesPhysicalFk()
    {
        const string schema = """
            datasource db {
              provider     = "sqlserver"
              url          = env("DATABASE_URL")
              relationMode = "foreignKeys"
            }
            model Post {
              id       Int  @id
              authorId Int
              author   User @relation(fields: [authorId], references: [id])
            }
            model User { id Int @id }
            """;

        var result = new PrismaParser().Parse(schema);
        result.Tables.Single(t => t.Name == "Post")
              .ForeignKeys.Single()
              .Kind.Should().Be(ForeignKeyKind.Physical);
    }

    // ── Enum handling ────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_EnumType_FieldUsesStringType()
    {
        const string schema = """
            enum Role {
              USER
              ADMIN
            }
            model User {
              id   Int  @id
              role Role
            }
            """;

        var result = new PrismaParser().Parse(schema, DbProvider.SqlServer);
        result.Tables.Single().Fields.Should().Contain(f => f.Name == "role" && f.Type == "nvarchar(max)");
    }

    [Fact]
    public void Parse_IncludeEnumsFalse_EnumsNotInResult()
    {
        const string schema = """
            enum Status {
              ACTIVE
              INACTIVE
            }
            model Product {
              id Int @id
            }
            """;

        var result = new PrismaParser().Parse(schema, includeEnums: false);
        result.Enums.Should().BeEmpty();
    }

    [Fact]
    public void Parse_IncludeEnumsTrue_EnumsReturnedAsReferenceTables()
    {
        const string schema = """
            enum Status {
              ACTIVE
              INACTIVE
            }
            model Product {
              id Int @id
            }
            """;

        var result = new PrismaParser().Parse(schema, includeEnums: true);
        result.Enums.Should().ContainSingle().Which.IsReferenceTable.Should().BeTrue();
        result.Enums.Single().Name.Should().Be("Status");
    }

    // ── Unsupported type ─────────────────────────────────────────────────────────

    [Fact]
    public void Parse_UnsupportedType_ExtractsSqlTypeString()
    {
        const string schema = """
            model Spatial {
              id       Int                    @id
              location Unsupported("geometry")
            }
            """;

        var result = new PrismaParser().Parse(schema);
        result.Tables.Single().Fields.Should().Contain(f => f.Name == "location" && f.Type == "geometry");
    }

    // ── Comments and noise ───────────────────────────────────────────────────────

    [Fact]
    public void Parse_CommentsInBlock_AreIgnored()
    {
        const string schema = """
            // top-level comment
            model User {
              // field comment
              id   Int    @id
              name String // inline comment stripped from attrs
            }
            """;

        var result = new PrismaParser().Parse(schema);
        result.Tables.Should().ContainSingle().Which.Fields.Should().HaveCount(2);
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Parse_GeneratorBlock_IsIgnored()
    {
        const string schema = """
            generator client {
              provider = "prisma-client-js"
            }
            model User {
              id Int @id
            }
            """;

        var result = new PrismaParser().Parse(schema);
        result.Tables.Should().ContainSingle(t => t.Name == "User");
    }

    // ── Multi-model schema ───────────────────────────────────────────────────────

    [Fact]
    public void Parse_MultipleModels_ReturnsAllTables()
    {
        const string schema = """
            model User {
              id Int @id
            }
            model Post {
              id Int @id
            }
            model Comment {
              id Int @id
            }
            """;

        var result = new PrismaParser().Parse(schema);
        result.Tables.Should().HaveCount(3);
        result.Tables.Select(t => t.Name).Should().BeEquivalentTo(["User", "Post", "Comment"]);
    }

    // ── Empty / degenerate input ─────────────────────────────────────────────────

    [Fact]
    public void Parse_EmptySchema_ReturnsEmptyResult()
    {
        var result = new PrismaParser().Parse("");

        result.Tables.Should().BeEmpty();
        result.Enums.Should().BeEmpty();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Parse_NoDatasourceBlock_DefaultsToSqlServer()
    {
        const string schema = """
            model User {
              id   Int    @id
              name String
            }
            """;

        // No datasource → default provider = SqlServer → String = nvarchar(max)
        var result = new PrismaParser().Parse(schema);
        result.Tables.Single().Fields.Should().Contain(f => f.Name == "name" && f.Type == "nvarchar(max)");
    }
}
