using FluentAssertions;
using Manifesta.Core;
using Manifesta.Core.IR;
using Xunit;

namespace Manifesta.Core.Tests;

public sealed class SqlDdlParserTests
{
    private readonly SqlDdlParser _parser = new();

    // ═══════════════════════════════════════════════════════════════════════════
    // SHARED / DIALECT-AGNOSTIC
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Parse_EmptySql_ReturnsEmptyResult()
    {
        var r = _parser.Parse("", DbProvider.MySql);
        r.Tables.Should().BeEmpty();
        r.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Parse_NoCreateTable_ReturnsEmptyResult()
    {
        const string sql = """
            SET NAMES utf8mb4;
            SET FOREIGN_KEY_CHECKS = 0;
            INSERT INTO foo VALUES (1, 'bar');
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables.Should().BeEmpty();
    }

    [Fact]
    public void Parse_MultipleTables_ReturnsAll()
    {
        const string sql = """
            CREATE TABLE customer (id INT NOT NULL, PRIMARY KEY (id));
            CREATE TABLE order_header (id INT NOT NULL, PRIMARY KEY (id));
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables.Should().HaveCount(2);
        r.Tables.Select(t => t.Name).Should().BeEquivalentTo(["customer", "order_header"]);
    }

    [Fact]
    public void Parse_IfNotExists_ParsedCorrectly()
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS customer (
                id INT NOT NULL,
                PRIMARY KEY (id)
            );
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables.Should().HaveCount(1);
        r.Tables[0].Name.Should().Be("customer");
    }

    [Fact]
    public void Parse_SchemaPrefix_AppliedToUnqualifiedTable()
    {
        const string sql = """
            CREATE TABLE customer (id INT NOT NULL, PRIMARY KEY (id));
            """;

        var r = _parser.Parse(sql, DbProvider.MySql, schemaPrefix: "dbo");
        r.Tables[0].Name.Should().Be("dbo.customer");
    }

    [Fact]
    public void Parse_SchemaQualifiedName_PreservedAsIs()
    {
        const string sql = """
            CREATE TABLE public.customer (id INT NOT NULL, PRIMARY KEY (id));
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Name.Should().Be("public.customer");
    }

    [Fact]
    public void Parse_SchemaPrefix_NotAppliedToAlreadyQualifiedName()
    {
        const string sql = """
            CREATE TABLE public.customer (id INT NOT NULL, PRIMARY KEY (id));
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres, schemaPrefix: "other");
        // Already has a schema — prefix must not be applied
        r.Tables[0].Name.Should().Be("public.customer");
    }

    // ── Nullability ───────────────────────────────────────────────────────────

    [Fact]
    public void Parse_NotNullColumn_IsNotNullable()
    {
        const string sql = """
            CREATE TABLE t (name VARCHAR(255) NOT NULL);
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables[0].Fields[0].Nullable.Should().BeFalse();
    }

    [Fact]
    public void Parse_NoNullConstraint_IsNullable()
    {
        const string sql = """
            CREATE TABLE t (name VARCHAR(255));
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables[0].Fields[0].Nullable.Should().BeTrue();
    }

    [Fact]
    public void Parse_ExplicitNull_IsNullable()
    {
        const string sql = """
            CREATE TABLE t (name VARCHAR(255) NULL);
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables[0].Fields[0].Nullable.Should().BeTrue();
    }

    // ── Default values ────────────────────────────────────────────────────────

    [Fact]
    public void Parse_NumericDefault_Captured()
    {
        const string sql = """
            CREATE TABLE t (qty INT NOT NULL DEFAULT 0);
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables[0].Fields[0].Default.Should().Be("0");
    }

    [Fact]
    public void Parse_StringDefault_UnquotedAndCaptured()
    {
        const string sql = """
            CREATE TABLE t (status VARCHAR(20) NOT NULL DEFAULT 'active');
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables[0].Fields[0].Default.Should().Be("active");
    }

    [Fact]
    public void Parse_NullDefault_StoredAsNull()
    {
        const string sql = """
            CREATE TABLE t (note TEXT DEFAULT NULL);
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables[0].Fields[0].Default.Should().BeNull();
    }

    [Fact]
    public void Parse_KeywordDefault_Captured()
    {
        const string sql = """
            CREATE TABLE t (created_at DATETIME DEFAULT CURRENT_TIMESTAMP);
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables[0].Fields[0].Default.Should().Be("CURRENT_TIMESTAMP");
    }

    // ── Primary keys ──────────────────────────────────────────────────────────

    [Fact]
    public void Parse_InlinePrimaryKey_RegisteredInPrimaryKeyList()
    {
        const string sql = """
            CREATE TABLE t (id INT NOT NULL PRIMARY KEY);
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables[0].PrimaryKey.Should().ContainSingle().Which.Should().Be("id");
        r.Tables[0].Fields[0].IsPrimaryKey.Should().BeTrue();
    }

    [Fact]
    public void Parse_TableLevelPrimaryKey_SingleColumn()
    {
        const string sql = """
            CREATE TABLE t (
                id INT NOT NULL,
                name VARCHAR(255),
                PRIMARY KEY (id)
            );
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables[0].PrimaryKey.Should().ContainSingle().Which.Should().Be("id");
    }

    [Fact]
    public void Parse_TableLevelPrimaryKey_CompositeKey()
    {
        const string sql = """
            CREATE TABLE order_item (
                order_id  INT NOT NULL,
                product_id INT NOT NULL,
                quantity  INT NOT NULL,
                PRIMARY KEY (order_id, product_id)
            );
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables[0].PrimaryKey.Should().BeEquivalentTo(["order_id", "product_id"],
            o => o.WithStrictOrdering());
    }

    [Fact]
    public void Parse_TableLevelPrimaryKey_WithConstraintName()
    {
        const string sql = """
            CREATE TABLE t (
                id INT NOT NULL,
                CONSTRAINT pk_t PRIMARY KEY (id)
            );
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].PrimaryKey.Should().ContainSingle().Which.Should().Be("id");
    }

    // ── Foreign keys ──────────────────────────────────────────────────────────

    [Fact]
    public void Parse_TableLevelForeignKey_PhysicalKind()
    {
        const string sql = """
            CREATE TABLE order_header (
                id          INT NOT NULL,
                customer_id INT NOT NULL,
                PRIMARY KEY (id),
                FOREIGN KEY (customer_id) REFERENCES customer (id)
            );
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables[0].ForeignKeys.Should().HaveCount(1);
        var fk = r.Tables[0].ForeignKeys[0];
        fk.SourceField.Should().Be("customer_id");
        fk.TargetTable.Should().Be("customer");
        fk.TargetField.Should().Be("id");
        fk.Kind.Should().Be(ForeignKeyKind.Physical);
        fk.CascadeDelete.Should().BeFalse();
    }

    [Fact]
    public void Parse_ForeignKey_CascadeDelete_Captured()
    {
        const string sql = """
            CREATE TABLE order_item (
                id       INT NOT NULL,
                order_id INT NOT NULL,
                FOREIGN KEY (order_id) REFERENCES order_header (id) ON DELETE CASCADE
            );
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables[0].ForeignKeys[0].CascadeDelete.Should().BeTrue();
    }

    [Fact]
    public void Parse_ForeignKey_WithConstraintName()
    {
        const string sql = """
            CREATE TABLE t (
                col INT NOT NULL,
                CONSTRAINT fk_t_ref FOREIGN KEY (col) REFERENCES other_table (id)
            );
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].ForeignKeys.Should().HaveCount(1);
        r.Tables[0].ForeignKeys[0].SourceField.Should().Be("col");
    }

    // ── UNIQUE constraints ────────────────────────────────────────────────────

    [Fact]
    public void Parse_TableLevelUnique_SingleColumn()
    {
        const string sql = """
            CREATE TABLE t (
                id    INT NOT NULL,
                email VARCHAR(255) NOT NULL,
                PRIMARY KEY (id),
                UNIQUE (email)
            );
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables[0].UniqueConstraints.Should().HaveCount(1);
        r.Tables[0].UniqueConstraints[0].Columns.Should().ContainSingle().Which.Should().Be("email");
    }

    [Fact]
    public void Parse_TableLevelUnique_Composite()
    {
        const string sql = """
            CREATE TABLE t (
                a INT NOT NULL,
                b INT NOT NULL,
                CONSTRAINT uq_ab UNIQUE (a, b)
            );
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].UniqueConstraints.Should().HaveCount(1);
        r.Tables[0].UniqueConstraints[0].Name.Should().Be("uq_ab");
        r.Tables[0].UniqueConstraints[0].Columns.Should().BeEquivalentTo(["a", "b"],
            o => o.WithStrictOrdering());
    }

    // ── CHECK constraints ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_CheckConstraint_Captured()
    {
        const string sql = """
            CREATE TABLE t (
                id  INT NOT NULL,
                age INT,
                CONSTRAINT ck_age CHECK (age >= 0)
            );
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].CheckConstraints.Should().HaveCount(1);
        r.Tables[0].CheckConstraints[0].Name.Should().Be("ck_age");
        r.Tables[0].CheckConstraints[0].Expression.Should().Be("age >= 0");
    }

    // ── Comment removal ───────────────────────────────────────────────────────

    [Fact]
    public void Parse_BlockComment_Removed()
    {
        const string sql = """
            /* This is a header comment */
            CREATE TABLE t (
                id INT NOT NULL /* inline comment */,
                PRIMARY KEY (id)
            );
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables.Should().HaveCount(1);
        r.Tables[0].Fields.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_LineComment_Removed()
    {
        const string sql = """
            -- drop existing
            CREATE TABLE t (
                id   INT NOT NULL, -- surrogate key
                name VARCHAR(255), -- display name
                PRIMARY KEY (id)
            );
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables.Should().HaveCount(1);
        r.Tables[0].Fields.Should().HaveCount(2);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // MYSQL
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void MySQL_BacktickQuoting_ParsedCorrectly()
    {
        const string sql = """
            CREATE TABLE `customer` (
                `id`    int NOT NULL AUTO_INCREMENT,
                `email` varchar(255) NOT NULL,
                PRIMARY KEY (`id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables.Should().HaveCount(1);
        r.Tables[0].Name.Should().Be("customer");
        r.Tables[0].Fields[0].Name.Should().Be("id");
        r.Tables[0].Fields[1].Name.Should().Be("email");
    }

    [Fact]
    public void MySQL_AutoIncrement_Stripped_TypePreserved()
    {
        const string sql = """
            CREATE TABLE t (`id` int NOT NULL AUTO_INCREMENT, PRIMARY KEY (`id`));
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables[0].Fields[0].Type.Should().Be("int");
        r.Tables[0].Fields[0].Nullable.Should().BeFalse();
    }

    [Fact]
    public void MySQL_Tinyint1_Preserved()
    {
        const string sql = """
            CREATE TABLE t (`active` tinyint(1) NOT NULL DEFAULT '1');
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables[0].Fields[0].Type.Should().Be("tinyint(1)");
    }

    [Fact]
    public void MySQL_IntDisplayWidth_Stripped()
    {
        const string sql = """
            CREATE TABLE t (
                `a` int(11)      NOT NULL,
                `b` bigint(20)   NOT NULL,
                `c` smallint(6)  NOT NULL,
                `d` tinyint(4)   NOT NULL
            );
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables[0].Fields[0].Type.Should().Be("int");
        r.Tables[0].Fields[1].Type.Should().Be("bigint");
        r.Tables[0].Fields[2].Type.Should().Be("smallint");
        r.Tables[0].Fields[3].Type.Should().Be("tinyint");
    }

    [Fact]
    public void MySQL_IntegerNormalisedToInt()
    {
        const string sql = "CREATE TABLE t (id INTEGER NOT NULL);";

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables[0].Fields[0].Type.Should().Be("int");
    }

    [Fact]
    public void MySQL_EngineAndCharsetStripped()
    {
        // Table-level options after the closing ) must not produce errors
        const string sql = """
            CREATE TABLE `t` (
                `id` int NOT NULL,
                PRIMARY KEY (`id`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables.Should().HaveCount(1);
        r.Errors.Should().BeEmpty();
    }

    [Fact]
    public void MySQL_CommentAnnotation_MappedToDescription()
    {
        const string sql = """
            CREATE TABLE `customer` (
                `id` int NOT NULL COMMENT 'Surrogate primary key'
            );
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables[0].Fields[0].Description.Should().Be("Surrogate primary key");
    }

    [Fact]
    public void MySQL_HashLineComment_Removed()
    {
        const string sql = """
            # MySQL dump
            CREATE TABLE t (
                id INT NOT NULL, # PK
                PRIMARY KEY (id)
            );
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables.Should().HaveCount(1);
        r.Tables[0].Fields.Should().HaveCount(1);
    }

    [Fact]
    public void MySQL_ConditionalComment_Removed()
    {
        const string sql = """
            /*!40101 SET NAMES utf8mb4 */;
            CREATE TABLE `t` (`id` int NOT NULL, PRIMARY KEY (`id`));
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables.Should().HaveCount(1);
    }

    [Fact]
    public void MySQL_DumpNoise_Stripped()
    {
        const string sql = """
            SET NAMES utf8mb4;
            SET FOREIGN_KEY_CHECKS = 0;
            LOCK TABLES `customer` WRITE;
            DROP TABLE IF EXISTS `customer`;
            CREATE TABLE `customer` (
                `id`    int NOT NULL AUTO_INCREMENT,
                `email` varchar(255) NOT NULL,
                PRIMARY KEY (`id`)
            ) ENGINE=InnoDB;
            UNLOCK TABLES;
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables.Should().HaveCount(1);
        r.Tables[0].Name.Should().Be("customer");
    }

    [Fact]
    public void MySQL_InlineForeignKey_Parsed()
    {
        const string sql = """
            CREATE TABLE order_item (
                order_id INT NOT NULL REFERENCES order_header(id)
            );
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables[0].ForeignKeys.Should().HaveCount(1);
        r.Tables[0].ForeignKeys[0].SourceField.Should().Be("order_id");
        r.Tables[0].ForeignKeys[0].TargetTable.Should().Be("order_header");
    }

    [Fact]
    public void MySQL_InlineIndex_Skipped_NoError()
    {
        const string sql = """
            CREATE TABLE `customer` (
                `id`    int NOT NULL AUTO_INCREMENT,
                `email` varchar(255) NOT NULL,
                PRIMARY KEY (`id`),
                KEY `idx_email` (`email`)
            ) ENGINE=InnoDB;
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables.Should().HaveCount(1);
        r.Errors.Should().BeEmpty();
    }

    [Fact]
    public void MySQL_ComputedColumn_Parsed()
    {
        const string sql = """
            CREATE TABLE t (
                a    INT NOT NULL,
                b    INT NOT NULL,
                ab   INT GENERATED ALWAYS AS (a + b) STORED
            );
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        var f = r.Tables[0].Fields.Single(x => x.Name == "ab");
        f.IsComputed.Should().BeTrue();
        f.ComputedExpression.Should().Be("a + b");
        f.IsPersisted.Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // POSTGRESQL
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Postgres_DoubleQuoteQuoting_ParsedCorrectly()
    {
        const string sql = """
            CREATE TABLE "public"."customer" (
                "id"    integer NOT NULL,
                "email" text    NOT NULL,
                CONSTRAINT pk_customer PRIMARY KEY ("id")
            );
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Name.Should().Be("public.customer");
        r.Tables[0].Fields[0].Name.Should().Be("id");
        r.Tables[0].PrimaryKey.Should().ContainSingle().Which.Should().Be("id");
    }

    [Fact]
    public void Postgres_Serial_NormalisedToInteger()
    {
        const string sql = """
            CREATE TABLE t (id SERIAL PRIMARY KEY, name TEXT);
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Fields[0].Type.Should().Be("integer");
    }

    [Fact]
    public void Postgres_BigSerial_NormalisedToBigint()
    {
        const string sql = "CREATE TABLE t (id BIGSERIAL NOT NULL);";

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Fields[0].Type.Should().Be("bigint");
    }

    [Fact]
    public void Postgres_CharacterVarying_TypePreserved()
    {
        const string sql = """
            CREATE TABLE t (name CHARACTER VARYING(255) NOT NULL);
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Fields[0].Type.Should().Be("character varying(255)");
    }

    [Fact]
    public void Postgres_DoublePrecision_TypePreserved()
    {
        const string sql = "CREATE TABLE t (value DOUBLE PRECISION NOT NULL);";

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Fields[0].Type.Should().Be("double precision");
    }

    [Fact]
    public void Postgres_TimestampWithTimeZone_TypePreserved()
    {
        const string sql = """
            CREATE TABLE t (created_at TIMESTAMP WITH TIME ZONE NOT NULL);
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Fields[0].Type.Should().Be("timestamp with time zone");
    }

    [Fact]
    public void Postgres_TimestampWithPrecisionAndTimeZone_TypePreserved()
    {
        const string sql = """
            CREATE TABLE t (created_at TIMESTAMP(6) WITH TIME ZONE NOT NULL);
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Fields[0].Type.Should().Be("timestamp(6) with time zone");
    }

    [Fact]
    public void Postgres_TimestampWithoutTimeZone_TypePreserved()
    {
        const string sql = """
            CREATE TABLE t (ts TIMESTAMP WITHOUT TIME ZONE DEFAULT NOW());
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Fields[0].Type.Should().Be("timestamp without time zone");
    }

    [Fact]
    public void Postgres_GeneratedAlwaysAsIdentity_Stripped()
    {
        const string sql = """
            CREATE TABLE t (
                id integer NOT NULL GENERATED ALWAYS AS IDENTITY,
                name text NOT NULL
            );
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Fields[0].Type.Should().Be("integer");
        r.Tables[0].Fields[0].Nullable.Should().BeFalse();
    }

    [Fact]
    public void Postgres_GeneratedAlwaysAsExpression_ComputedColumn()
    {
        const string sql = """
            CREATE TABLE t (
                a    integer NOT NULL,
                b    integer NOT NULL,
                sum  integer GENERATED ALWAYS AS (a + b) STORED
            );
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        var f = r.Tables[0].Fields.Single(x => x.Name == "sum");
        f.IsComputed.Should().BeTrue();
        f.ComputedExpression.Should().Be("a + b");
        f.IsPersisted.Should().BeTrue();
    }

    [Fact]
    public void Postgres_ParenthesisedDefault_Captured()
    {
        const string sql = """
            CREATE TABLE t (created_at timestamptz NOT NULL DEFAULT (now()));
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Fields[0].Default.Should().Be("now()");
    }

    [Fact]
    public void Postgres_MultipleConstraintsWithNames()
    {
        const string sql = """
            CREATE TABLE customer (
                id    integer NOT NULL,
                email text    NOT NULL,
                CONSTRAINT pk_customer  PRIMARY KEY (id),
                CONSTRAINT uq_customer_email UNIQUE (email),
                CONSTRAINT ck_email CHECK (email LIKE '%@%')
            );
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].PrimaryKey.Should().ContainSingle().Which.Should().Be("id");
        r.Tables[0].UniqueConstraints.Should().HaveCount(1);
        r.Tables[0].UniqueConstraints[0].Name.Should().Be("uq_customer_email");
        r.Tables[0].CheckConstraints.Should().HaveCount(1);
        r.Tables[0].CheckConstraints[0].Name.Should().Be("ck_email");
    }

    [Fact]
    public void Postgres_PgDumpStyleDump_ParsedCorrectly()
    {
        const string sql = """
            --
            -- PostgreSQL database dump
            --

            SET statement_timeout = 0;
            SET client_encoding = 'UTF8';

            CREATE TABLE public.customer (
                id    integer NOT NULL,
                email character varying(255) NOT NULL,
                active boolean NOT NULL DEFAULT true
            );

            ALTER TABLE ONLY public.customer
                ADD CONSTRAINT pk_customer PRIMARY KEY (id);
            """;

        // ALTER TABLE is skipped; the table itself should parse fine
        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables.Should().HaveCount(1);
        r.Tables[0].Name.Should().Be("public.customer");
        r.Tables[0].Fields.Should().HaveCount(3);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SQLITE
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void SQLite_BasicTable_ParsedCorrectly()
    {
        const string sql = """
            CREATE TABLE customer (
                id    INTEGER PRIMARY KEY AUTOINCREMENT,
                email TEXT    NOT NULL,
                score REAL
            );
            """;

        var r = _parser.Parse(sql, DbProvider.Sqlite);
        r.Tables.Should().HaveCount(1);
        r.Tables[0].Name.Should().Be("customer");
        r.Tables[0].Fields.Should().HaveCount(3);
        r.Tables[0].Fields[0].Type.Should().Be("integer");
        r.Tables[0].Fields[1].Type.Should().Be("text");
        r.Tables[0].Fields[2].Type.Should().Be("real");
    }

    [Fact]
    public void SQLite_Autoincrement_Stripped_TypePreserved()
    {
        const string sql = """
            CREATE TABLE t (id INTEGER PRIMARY KEY AUTOINCREMENT);
            """;

        var r = _parser.Parse(sql, DbProvider.Sqlite);
        r.Tables[0].Fields[0].Type.Should().Be("integer");
        r.Tables[0].Fields[0].IsPrimaryKey.Should().BeTrue();
    }

    [Fact]
    public void SQLite_BracketQuoting_ParsedCorrectly()
    {
        const string sql = """
            CREATE TABLE [customer] ([id] INTEGER PRIMARY KEY, [name] TEXT NOT NULL);
            """;

        var r = _parser.Parse(sql, DbProvider.Sqlite);
        r.Tables[0].Name.Should().Be("customer");
        r.Tables[0].Fields[0].Name.Should().Be("id");
        r.Tables[0].Fields[1].Name.Should().Be("name");
    }

    [Fact]
    public void SQLite_PermissiveType_PreservedLowercased()
    {
        const string sql = """
            CREATE TABLE t (
                a ANY_RANDOM_TYPE NOT NULL,
                b NUMERIC(10,2)
            );
            """;

        var r = _parser.Parse(sql, DbProvider.Sqlite);
        r.Tables[0].Fields[0].Type.Should().Be("any_random_type");
        r.Tables[0].Fields[1].Type.Should().Be("numeric(10,2)");
    }

    [Fact]
    public void SQLite_SchemaPrefix_Applied()
    {
        const string sql = "CREATE TABLE customer (id INTEGER PRIMARY KEY);";

        var r = _parser.Parse(sql, DbProvider.Sqlite, schemaPrefix: "main");
        r.Tables[0].Name.Should().Be("main.customer");
    }

    [Fact]
    public void SQLite_ForeignKey_TableLevel_Parsed()
    {
        const string sql = """
            CREATE TABLE order_item (
                id       INTEGER PRIMARY KEY,
                order_id INTEGER NOT NULL,
                FOREIGN KEY (order_id) REFERENCES order_header(id) ON DELETE CASCADE
            );
            """;

        var r = _parser.Parse(sql, DbProvider.Sqlite);
        r.Tables[0].ForeignKeys.Should().HaveCount(1);
        r.Tables[0].ForeignKeys[0].CascadeDelete.Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SQL SERVER
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void SqlServer_BracketQuoting_ParsedCorrectly()
    {
        const string sql = """
            CREATE TABLE [dbo].[customer] (
                [id]    [int] NOT NULL,
                [email] [nvarchar](255) NOT NULL,
                CONSTRAINT [pk_customer] PRIMARY KEY CLUSTERED ([id])
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        r.Tables[0].Name.Should().Be("dbo.customer");
        r.Tables[0].Fields[0].Name.Should().Be("id");
        r.Tables[0].Fields[0].Type.Should().Be("int");
        r.Tables[0].Fields[1].Type.Should().Be("nvarchar(255)");
        r.Tables[0].PrimaryKey.Should().ContainSingle().Which.Should().Be("id");
    }

    [Fact]
    public void SqlServer_Identity_Stripped_TypePreserved()
    {
        const string sql = """
            CREATE TABLE [dbo].[t] (
                [id] [int] NOT NULL IDENTITY(1,1),
                PRIMARY KEY ([id])
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        r.Tables[0].Fields[0].Type.Should().Be("int");
        r.Tables[0].Fields[0].Nullable.Should().BeFalse();
    }

    [Fact]
    public void SqlServer_ComputedColumn_ParsedCorrectly()
    {
        const string sql = """
            CREATE TABLE [dbo].[t] (
                [first_name] [nvarchar](100) NOT NULL,
                [last_name]  [nvarchar](100) NOT NULL,
                [full_name]  AS ([first_name] + ' ' + [last_name]) PERSISTED
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        var f = r.Tables[0].Fields.Single(x => x.Name == "full_name");
        f.IsComputed.Should().BeTrue();
        f.ComputedExpression.Should().Be("[first_name] + ' ' + [last_name]");
        f.IsPersisted.Should().BeTrue();
    }

    [Fact]
    public void SqlServer_NvarcharMax_TypePreserved()
    {
        const string sql = """
            CREATE TABLE [dbo].[t] ([body] [nvarchar](max) NULL);
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        r.Tables[0].Fields[0].Type.Should().Be("nvarchar(max)");
        r.Tables[0].Fields[0].Nullable.Should().BeTrue();
    }

    [Fact]
    public void SqlServer_UnicodeDefault_Captured()
    {
        const string sql = """
            CREATE TABLE [dbo].[t] (
                [status] [nvarchar](50) NOT NULL DEFAULT N'active'
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        r.Tables[0].Fields[0].Default.Should().Be("active");
    }

    [Fact]
    public void SqlServer_GoSeparator_MultipleTables()
    {
        // GO is a batch separator — both tables must be found
        const string sql = """
            CREATE TABLE [dbo].[customer] ([id] [int] NOT NULL, PRIMARY KEY ([id]))
            GO
            CREATE TABLE [dbo].[order_header] ([id] [int] NOT NULL, PRIMARY KEY ([id]))
            GO
            """;

        // GO appears but our block extractor finds CREATE TABLE anywhere in the text
        var r = _parser.Parse(sql, DbProvider.SqlServer);
        r.Tables.Should().HaveCount(2);
        r.Tables[0].Name.Should().Be("dbo.customer");
        r.Tables[1].Name.Should().Be("dbo.order_header");
    }

    [Fact]
    public void SqlServer_ParenthesisedDefaultExpression_Captured()
    {
        const string sql = """
            CREATE TABLE [dbo].[t] (
                [id]         [uniqueidentifier] NOT NULL DEFAULT (newid()),
                [created_at] [datetime2](7)     NOT NULL DEFAULT (getutcdate())
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        r.Tables[0].Fields[0].Default.Should().Be("newid()");
        r.Tables[0].Fields[1].Default.Should().Be("getutcdate()");
    }

    [Fact]
    public void SqlServer_ForeignKeyWithNameAndCascade()
    {
        const string sql = """
            CREATE TABLE [dbo].[order_item] (
                [id]       [int] NOT NULL,
                [order_id] [int] NOT NULL,
                CONSTRAINT [pk_order_item] PRIMARY KEY ([id]),
                CONSTRAINT [fk_order_item_order]
                    FOREIGN KEY ([order_id])
                    REFERENCES [dbo].[order_header] ([id])
                    ON DELETE CASCADE
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        r.Tables[0].ForeignKeys.Should().HaveCount(1);
        r.Tables[0].ForeignKeys[0].SourceField.Should().Be("order_id");
        r.Tables[0].ForeignKeys[0].TargetTable.Should().Be("dbo.order_header");
        r.Tables[0].ForeignKeys[0].CascadeDelete.Should().BeTrue();
    }

    [Fact]
    public void SqlServer_TypesNormalisedToLowercase()
    {
        const string sql = """
            CREATE TABLE [dbo].[t] (
                [a] [INT]          NOT NULL,
                [b] [BIGINT]       NOT NULL,
                [c] [NVARCHAR](50) NOT NULL,
                [d] [DATETIME2](7) NOT NULL,
                [e] [BIT]          NOT NULL,
                [f] [UNIQUEIDENTIFIER] NOT NULL
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        r.Tables[0].Fields[0].Type.Should().Be("int");
        r.Tables[0].Fields[1].Type.Should().Be("bigint");
        r.Tables[0].Fields[2].Type.Should().Be("nvarchar(50)");
        r.Tables[0].Fields[3].Type.Should().Be("datetime2(7)");
        r.Tables[0].Fields[4].Type.Should().Be("bit");
        r.Tables[0].Fields[5].Type.Should().Be("uniqueidentifier");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SQL SERVER — decimal / dec type normalisation
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void SqlServer_DecimalPrecisionSpaces_NormalisedToNoSpaces()
    {
        // SQL Server sometimes stores types as "decimal(18, 0)" with a space after the comma.
        // The parser should normalise to "decimal(18,0)" to avoid spurious drift reports.
        const string sql = """
            CREATE TABLE [dbo].[t] (
                [Amount] [decimal](18, 0) NOT NULL,
                [Rate]   [decimal](10, 4) NOT NULL
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        r.Tables[0].Fields[0].Type.Should().Be("decimal(18,0)");
        r.Tables[0].Fields[1].Type.Should().Be("decimal(10,4)");
    }

    [Fact]
    public void SqlServer_DecAliasNormalisedToDecimal()
    {
        // DEC is a standard alias for DECIMAL in T-SQL.
        const string sql = """
            CREATE TABLE [dbo].[t] (
                [Amount] [dec](18, 2) NOT NULL
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        r.Tables[0].Fields[0].Type.Should().Be("decimal(18,2)");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SQL SERVER — ALTER TABLE … ADD CONSTRAINT … PRIMARY KEY extraction
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void SqlServer_AlterTablePk_NoPkInCreateTable_PkAdditionExtracted()
    {
        // SQL Server database projects often have PKs in separate *_Updates.sql files.
        const string sql = """
            ALTER TABLE [dbo].[AccessType] ADD CONSTRAINT [PK_AccessType] PRIMARY KEY CLUSTERED ([Id] ASC);
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        r.PkAdditions.Should().ContainSingle();
        r.PkAdditions[0].TableName.Should().Be("dbo.AccessType");
        r.PkAdditions[0].Columns.Should().ContainSingle().Which.Should().Be("Id");
    }

    [Fact]
    public void SqlServer_AlterTablePk_NoConstraintKeyword_PkAdditionExtracted()
    {
        // CONSTRAINT clause is optional.
        const string sql = """
            ALTER TABLE [dbo].[t] ADD PRIMARY KEY ([Id]);
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        r.PkAdditions.Should().ContainSingle();
        r.PkAdditions[0].TableName.Should().Be("dbo.t");
        r.PkAdditions[0].Columns.Should().ContainSingle().Which.Should().Be("Id");
    }

    [Fact]
    public void SqlServer_AlterTablePk_CompositePk_AllColumnsExtracted()
    {
        const string sql = """
            ALTER TABLE [dbo].[OrderItem] ADD CONSTRAINT [PK_OrderItem] PRIMARY KEY ([OrderId], [LineNo]);
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        r.PkAdditions.Should().ContainSingle();
        r.PkAdditions[0].Columns.Should().BeEquivalentTo(["OrderId", "LineNo"], o => o.WithStrictOrdering());
    }

    [Fact]
    public void SqlServer_AlterTablePk_UnqualifiedTableName_SchemaPrefixApplied()
    {
        // When schemaPrefix = "dbo", unqualified names should get the prefix.
        const string sql = """
            ALTER TABLE AccessType ADD CONSTRAINT PK_AccessType PRIMARY KEY (Id);
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer, schemaPrefix: "dbo");
        r.PkAdditions.Should().ContainSingle();
        r.PkAdditions[0].TableName.Should().Be("dbo.AccessType");
    }

    [Fact]
    public void SqlServer_AlterTablePk_NonPkAlter_NotAddedToPkAdditions()
    {
        // ALTER TABLE ADD CONSTRAINT … FOREIGN KEY should not produce a PkAddition.
        const string sql = """
            ALTER TABLE [dbo].[order_item] ADD CONSTRAINT [fk_oi_order]
                FOREIGN KEY ([order_id]) REFERENCES [dbo].[order_header] ([id]);
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        r.PkAdditions.Should().BeEmpty();
    }

    [Fact]
    public void SqlServer_AlterTablePk_MixedWithCreateTable_BothExtracted()
    {
        // A typical *_Updates.sql file: one CREATE TABLE + one ALTER TABLE PK.
        const string sql = """
            CREATE TABLE [dbo].[Widget] (
                [Id]   [int]          NOT NULL,
                [Name] [nvarchar](50) NOT NULL
            );

            ALTER TABLE [dbo].[Widget] ADD CONSTRAINT [PK_Widget] PRIMARY KEY ([Id]);
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        r.Tables.Should().ContainSingle().Which.Name.Should().Be("dbo.Widget");
        r.PkAdditions.Should().ContainSingle();
        r.PkAdditions[0].TableName.Should().Be("dbo.Widget");
        r.PkAdditions[0].Columns.Should().ContainSingle().Which.Should().Be("Id");
    }

    [Fact]
    public void SqlServer_SysnameNormalisedToNvarchar128()
    {
        const string sql = """
            CREATE TABLE [dbo].[SysObjects] (
                [TableName] [sysname] NOT NULL,
                [SchemaId]  [int]     NOT NULL
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        r.Tables[0].Fields[0].Type.Should().Be("nvarchar(128)");
    }

    [Fact]
    public void SqlServer_IntegerNormalisedToInt()
    {
        const string sql = """
            CREATE TABLE [dbo].[t] (
                [Id]    [integer] NOT NULL,
                [Value] [bigint]  NOT NULL
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        r.Tables[0].Fields[0].Type.Should().Be("int");
        r.Tables[0].Fields[1].Type.Should().Be("bigint");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TOKENIZER
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Tokenize_BacktickIdentifier_ReturnedAsOneToken()
    {
        var tokens = SqlDdlParser.Tokenize("`my column`");
        tokens.Should().ContainSingle().Which.Should().Be("`my column`");
    }

    [Fact]
    public void Tokenize_BracketIdentifier_ReturnedAsOneToken()
    {
        var tokens = SqlDdlParser.Tokenize("[my column]");
        tokens.Should().ContainSingle().Which.Should().Be("[my column]");
    }

    [Fact]
    public void Tokenize_ParenGroupWithNestedParens_ReturnedAsOneToken()
    {
        var tokens = SqlDdlParser.Tokenize("DEFAULT (COALESCE(a, 0))");
        tokens.Should().HaveCount(2);
        tokens[0].Should().Be("DEFAULT");
        tokens[1].Should().Be("(COALESCE(a, 0))");
    }

    [Fact]
    public void Tokenize_StringWithParens_NotSplitAsParenGroup()
    {
        // The '(test)' is a string literal, not a paren group
        var tokens = SqlDdlParser.Tokenize("DEFAULT '(test)'");
        tokens.Should().HaveCount(2);
        tokens[1].Should().Be("'(test)'");
    }

    [Fact]
    public void Tokenize_UnicodeStringLiteral_ReturnedAsOneToken()
    {
        var tokens = SqlDdlParser.Tokenize("DEFAULT N'hello'");
        tokens.Should().HaveCount(2);
        tokens[1].Should().Be("N'hello'");
    }
}
