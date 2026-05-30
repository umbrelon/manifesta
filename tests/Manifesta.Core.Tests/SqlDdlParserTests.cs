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
    public void MySQL_SpatialKey_SkippedNotParsedAsColumn()
    {
        // SPATIAL KEY is a MySQL index-type qualifier.  The parser must treat it
        // as an inline index (silently skipped) rather than a column named "SPATIAL".
        const string sql = """
            CREATE TABLE `address` (
                `address_id` smallint unsigned NOT NULL AUTO_INCREMENT,
                `location`   geometry NOT NULL,
                PRIMARY KEY (`address_id`),
                SPATIAL KEY `idx_location` (`location`)
            ) ENGINE=InnoDB;
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables.Should().HaveCount(1);
        r.Errors.Should().BeEmpty();
        r.Tables[0].Fields.Should().HaveCount(2);
        r.Tables[0].Fields.Should().NotContain(f => f.Name == "SPATIAL");
    }

    [Fact]
    public void MySQL_FulltextKey_SkippedNotParsedAsColumn()
    {
        // FULLTEXT KEY is a MySQL index-type qualifier.  The parser must treat it
        // as an inline index (silently skipped) rather than a column named "FULLTEXT".
        const string sql = """
            CREATE TABLE `film_text` (
                `film_id`     smallint NOT NULL,
                `title`       varchar(255) NOT NULL,
                `description` text,
                PRIMARY KEY (`film_id`),
                FULLTEXT KEY `idx_title_description` (`title`,`description`)
            ) ENGINE=InnoDB;
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables.Should().HaveCount(1);
        r.Errors.Should().BeEmpty();
        r.Tables[0].Fields.Should().HaveCount(3);
        r.Tables[0].Fields.Should().NotContain(f => f.Name == "FULLTEXT");
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
    public void Postgres_CharacterVarying_NormalisedToVarchar()
    {
        const string sql = """
            CREATE TABLE t (name CHARACTER VARYING(255) NOT NULL);
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Fields[0].Type.Should().Be("varchar(255)");
    }

    [Fact]
    public void Postgres_CharacterVaryingNoLength_NormalisedToVarchar()
    {
        const string sql = "CREATE TABLE t (name CHARACTER VARYING NOT NULL);";

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Fields[0].Type.Should().Be("varchar");
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
    public void Postgres_TimestampWithPrecisionAndTimeZone_PrecisionStripped()
    {
        // pg_dump writes timestamp(6) with explicit default precision;
        // information_schema returns without precision. Normaliser strips it.
        const string sql = """
            CREATE TABLE t (created_at TIMESTAMP(6) WITH TIME ZONE NOT NULL);
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Fields[0].Type.Should().Be("timestamp with time zone");
    }

    [Fact]
    public void Postgres_TimestampWithoutTimeZoneAndPrecision_PrecisionStripped()
    {
        const string sql = """
            CREATE TABLE t (created_at TIMESTAMP(6) WITHOUT TIME ZONE NOT NULL);
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Fields[0].Type.Should().Be("timestamp without time zone");
    }

    [Fact]
    public void Postgres_TimeWithPrecision_PrecisionStripped()
    {
        const string sql = "CREATE TABLE t (start_at TIME(3) WITHOUT TIME ZONE NOT NULL);";

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Fields[0].Type.Should().Be("time without time zone");
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
    public void SqlServer_NamedDefaultConstraint_NegativeValue_CapturedCorrectly()
    {
        // CONSTRAINT DF_name DEFAULT -1 — the named-constraint prefix must not
        // swallow the DEFAULT clause, and the unary minus must not be dropped.
        const string sql = """
            CREATE TABLE [dbo].[Bundle] (
                PackageID INT NOT NULL CONSTRAINT DF_Bundle_PackageID DEFAULT -1,
                ProductID INT NOT NULL CONSTRAINT DF_Bundle_ProductID DEFAULT -1,
                Score     INT NOT NULL DEFAULT -42
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        var fields = r.Tables[0].Fields;
        fields[0].Default.Should().Be("-1");
        fields[1].Default.Should().Be("-1");
        fields[2].Default.Should().Be("-42");
    }

    [Fact]
    public void SqlServer_DefaultNormalisation_BareKeywordsGetCanonicalForm()
    {
        // Bare keywords (no parens) that appear in hand-written DDL must be
        // normalised to the canonical function-call form so that drift detection
        // does not produce false positives against live-db introspection results.
        const string sql = """
            CREATE TABLE [dbo].[t] (
                [a] datetime  NOT NULL DEFAULT GETDATE,
                [b] datetime  NOT NULL DEFAULT CURRENT_TIMESTAMP,
                [c] nvarchar(128) NOT NULL DEFAULT SUSER_NAME,
                [d] uniqueidentifier NOT NULL DEFAULT NEWID,
                [e] datetime  NOT NULL DEFAULT GETUTCDATE,
                [f] datetime  NOT NULL DEFAULT SYSUTCDATETIME
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        var fields = r.Tables[0].Fields;
        fields[0].Default.Should().Be("getdate()");
        fields[1].Default.Should().Be("getdate()");    // CURRENT_TIMESTAMP synonym
        fields[2].Default.Should().Be("suser_name()");
        fields[3].Default.Should().Be("newid()");
        fields[4].Default.Should().Be("getutcdate()");
        fields[5].Default.Should().Be("sysutcdatetime()");
    }

    [Fact]
    public void SqlServer_DefaultNormalisation_SystemUser_MapsToSuserSname()
    {
        const string sql = """
            CREATE TABLE [dbo].[t] (
                [a] nvarchar(128) NOT NULL DEFAULT SYSTEM_USER,
                [b] nvarchar(128) NOT NULL DEFAULT system_user
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        r.Tables[0].Fields[0].Default.Should().Be("suser_sname()");
        r.Tables[0].Fields[1].Default.Should().Be("suser_sname()");
    }

    [Fact]
    public void SqlServer_DefaultNormalisation_SpaceN_WrapsArgInExtraParens()
    {
        // sys.default_constraints stores space(1) as space((1)); normalise DDL to match.
        const string sql = """
            CREATE TABLE [dbo].[t] (
                [a] char(1) NOT NULL DEFAULT space(1),
                [b] char(5) NOT NULL DEFAULT SPACE(5)
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        r.Tables[0].Fields[0].Default.Should().Be("space((1))");
        r.Tables[0].Fields[1].Default.Should().Be("space((5))");
    }

    [Fact]
    public void SqlServer_DefaultNormalisation_DateLiterals_GetQuoted()
    {
        // Bare YYYYMMDD and YYYYMMDD HH:MM:SS literals must be wrapped in single
        // quotes to match what sys.default_constraints returns for the live DB.
        // This applies whether the DDL writes them bare or already quoted
        // (UnquoteString strips the quotes; normalisation re-adds them).
        const string sql = """
            CREATE TABLE [dbo].[t] (
                [a] datetime  NOT NULL DEFAULT 20000101,
                [b] datetime  NOT NULL DEFAULT '20000101',
                [c] datetime  NOT NULL DEFAULT (20000101),
                [d] datetime  NOT NULL DEFAULT '29991231 23:59:59',
                [e] datetime2 NOT NULL DEFAULT '20000101 00:00:00.0000000'
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        var fields = r.Tables[0].Fields;
        fields[0].Default.Should().Be("'20000101'");   // bare integer
        fields[1].Default.Should().Be("'20000101'");   // quoted → unquoted → re-quoted
        fields[2].Default.Should().Be("'20000101'");   // parenthesised bare integer
        fields[3].Default.Should().Be("'29991231 23:59:59'");
        fields[4].Default.Should().Be("'20000101 00:00:00.0000000'");
    }

    [Fact]
    public void SqlServer_DefaultNormalisation_AlreadyCanonical_NotDoubled()
    {
        // Defaults that already include parentheses must not get a second pair added.
        const string sql = """
            CREATE TABLE [dbo].[t] (
                [a] datetime      NOT NULL DEFAULT getdate(),
                [b] nvarchar(128) NOT NULL DEFAULT suser_name(),
                [c] uniqueidentifier NOT NULL DEFAULT newid()
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        var fields = r.Tables[0].Fields;
        fields[0].Default.Should().Be("getdate()");
        fields[1].Default.Should().Be("suser_name()");
        fields[2].Default.Should().Be("newid()");
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
    public void SqlServer_NamedConstraintDefault_CapturedCorrectly()
    {
        const string sql = """
            CREATE TABLE [dbo].[Address] (
                [lParent]          int          NOT NULL CONSTRAINT DF_Address_lParent   DEFAULT ((-1)),
                [szName]           varchar(81)  NOT NULL CONSTRAINT DF_Address_szName    DEFAULT (''),
                [cPaymentMethod]   smallint     NOT NULL CONSTRAINT DF_Address_cPayment  DEFAULT (0),
                [dCreate]          datetime     NULL     CONSTRAINT DF_Address_dCreate   DEFAULT (getdate()),
                [szUser]           varchar(64)  NULL     CONSTRAINT DF_Address_szUser    DEFAULT (suser_sname()),
                [lNoConstraint]    int          NOT NULL DEFAULT (42)
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        var fields = r.Tables[0].Fields;
        fields[0].Default.Should().Be("(-1)");
        fields[1].Default.Should().Be("''");
        fields[2].Default.Should().Be("0");
        fields[3].Default.Should().Be("getdate()");
        fields[4].Default.Should().Be("suser_sname()");
        fields[5].Default.Should().Be("42");
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

    // ── time / datetimeoffset implicit precision normalisation ─────────────────

    [Fact]
    public void SqlServer_TimeBareType_NormalisedToTime7()
    {
        // SQL Server default precision for time is 7; bare 'time' in DDL must
        // normalise to 'time(7)' so it matches what sys.columns returns via
        // live introspection — eliminating false drift on columns like Shift.StartTime.
        const string sql = """
            CREATE TABLE [dbo].[Shift] (
                [ShiftID]   [tinyint]  IDENTITY(1,1) NOT NULL,
                [Name]      [nvarchar](50) NOT NULL,
                [StartTime] [time]     NOT NULL,
                [EndTime]   [time]     NOT NULL
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        var fields = r.Tables[0].Fields;
        fields.Single(f => f.Name == "StartTime").Type.Should().Be("time(7)");
        fields.Single(f => f.Name == "EndTime")  .Type.Should().Be("time(7)");
    }

    [Fact]
    public void SqlServer_TimeExplicitPrecision_PreservedAsIs()
    {
        // Explicit precision must not be double-decorated.
        const string sql = """
            CREATE TABLE [dbo].[t] (
                [a] [time](0) NOT NULL,
                [b] [time](3) NOT NULL
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        r.Tables[0].Fields[0].Type.Should().Be("time(0)");
        r.Tables[0].Fields[1].Type.Should().Be("time(3)");
    }

    [Fact]
    public void SqlServer_DatetimeoffsetBareType_NormalisedToDatetimeoffset7()
    {
        const string sql = """
            CREATE TABLE [dbo].[t] (
                [ts] [datetimeoffset] NOT NULL
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        r.Tables[0].Fields[0].Type.Should().Be("datetimeoffset(7)");
    }

    // ── Computed column expression capture ────────────────────────────────────

    [Fact]
    public void SqlServer_ComputedColumn_MethodCallOnHierarchyid_ExpressionCaptured()
    {
        // Reproduces the AdventureWorks pattern:
        //   [OrganizationLevel] AS [OrganizationNode].[GetLevel]()
        // The expression is NOT parenthesised at the top level; the parser must
        // collect all tokens until a modifier keyword and reconstruct the expression.
        const string sql = """
            CREATE TABLE [HumanResources].[Employee] (
                [BusinessEntityID]  [int]         NOT NULL,
                [OrganizationNode]  [hierarchyid] NULL,
                [OrganizationLevel] AS [OrganizationNode].[GetLevel]()
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        var f = r.Tables[0].Fields.Single(x => x.Name == "OrganizationLevel");
        f.IsComputed.Should().BeTrue();
        f.ComputedExpression.Should().NotBeNullOrEmpty();
        f.ComputedExpression.Should().Contain("OrganizationNode");
        f.ComputedExpression.Should().Contain("GetLevel");
    }

    [Fact]
    public void SqlServer_ComputedColumn_ParenthesisedExpression_StillCaptured()
    {
        // Parenthesised form must still work after the refactor — this is the
        // existing path: AS ([first_name] + ' ' + [last_name])
        const string sql = """
            CREATE TABLE [dbo].[t] (
                [first_name] [nvarchar](50) NOT NULL,
                [last_name]  [nvarchar](50) NOT NULL,
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
    public void SqlServer_ComputedColumn_UdfReference_ExpressionCaptured()
    {
        // AS ISNULL('AW' + [dbo].[ufnLeadingZeros](CustomerID), '') — top-level
        // token is ISNULL which is not parenthesised directly.
        const string sql = """
            CREATE TABLE [Sales].[Customer] (
                [CustomerID]    [int]        IDENTITY(1,1) NOT NULL,
                [AccountNumber] AS ISNULL(N'AW' + [dbo].[ufnLeadingZeros]([CustomerID]), N'*** ERROR ***') NOT NULL
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        var f = r.Tables[0].Fields.Single(x => x.Name == "AccountNumber");
        f.IsComputed.Should().BeTrue();
        f.ComputedExpression.Should().NotBeNullOrEmpty();
        f.ComputedExpression.Should().Contain("ufnLeadingZeros");
    }

    [Fact]
    public void SqlServer_IdentityColumn_ImpliedNotNull()
    {
        // IDENTITY columns must never be nullable even when NOT NULL is omitted.
        const string sql = """
            CREATE TABLE [dbo].[t] (
                [Id] [int] IDENTITY(1,1),
                [Name] [nvarchar](50) NOT NULL
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        var fields = r.Tables[0].Fields;
        fields[0].Name.Should().Be("Id");
        fields[0].Nullable.Should().BeFalse("IDENTITY implies NOT NULL");
        fields[1].Nullable.Should().BeFalse();
    }

    [Fact]
    public void SqlServer_TrailingConstraintWithoutComma_CorrectPkColumn()
    {
        // Reproduces a real-world pattern: the CONSTRAINT clause is attached to the
        // last column entry without a separating comma. The parser must register the
        // column(s) listed inside the CONSTRAINT, not the column the clause is
        // attached to, as the primary key.
        const string sql = """
            CREATE TABLE HlrProfile (
                [HlrProfileId] [int] IDENTITY(1,1),
                [SN] [VARCHAR](32) NOT NULL,
                [MSISDN] VARCHAR(20) NOT NULL,
                [ProfileType] [TINYINT] NOT NULL,
                [Value] [NVARCHAR](MAX) NOT NULL,
                [LastUpdated] [DATETIME] NOT NULL
                CONSTRAINT PK_HlrProfile PRIMARY KEY CLUSTERED (HlrProfileId) ON [PRIMARY]
            ) ON [PRIMARY]
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        var table = r.Tables[0];

        // Correct PK
        table.PrimaryKey.Should().ContainSingle().Which.Should().Be("HlrProfileId");

        // LastUpdated must NOT be marked as PK (it has NOT NULL in the DDL so Nullable=false is correct)
        table.Fields.First(f => f.Name == "LastUpdated").IsPrimaryKey.Should().BeFalse();

        // HlrProfileId: IDENTITY → NOT NULL; in PK → NOT NULL via post-pass
        var pkField = table.Fields.First(f => f.Name == "HlrProfileId");
        pkField.IsPrimaryKey.Should().BeTrue();
        pkField.Nullable.Should().BeFalse();
    }

    [Fact]
    public void SqlServer_PkColumnsAlwaysNotNull()
    {
        // A table-level PRIMARY KEY constraint must force all listed columns to
        // nullable=false even when they lack an explicit NOT NULL in the DDL.
        const string sql = """
            CREATE TABLE [dbo].[t] (
                [a] int,
                [b] int NOT NULL,
                PRIMARY KEY (a, b)
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        r.Tables[0].Fields[0].Nullable.Should().BeFalse("PK column a must be NOT NULL");
        r.Tables[0].Fields[1].Nullable.Should().BeFalse("PK column b must be NOT NULL");
    }

    [Fact]
    public void SqlServer_Datetime2WithoutPrecision_DefaultsToScale7()
    {
        const string sql = """
            CREATE TABLE [dbo].[t] (
                [a] [datetime2] NOT NULL,
                [b] [datetime2](3) NOT NULL
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);
        r.Tables[0].Fields[0].Type.Should().Be("datetime2(7)");
        r.Tables[0].Fields[1].Type.Should().Be("datetime2(3)");
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

    // ── SQL Server PERIOD FOR SYSTEM_TIME (temporal tables) ───────────────────

    [Fact]
    public void SqlServer_TemporalTable_PeriodClause_NoPhantomColumn()
    {
        // Regression guard: PERIOD FOR SYSTEM_TIME (...) was parsed as a phantom
        // column named "PERIOD" because PERIOD is not in IsConstraintTypeKeyword.
        // The two hidden system-period columns and the PERIOD clause must be
        // silently consumed; no "PERIOD" field must appear in the output.
        const string sql = """
            CREATE TABLE [dbo].[Temporal] (
                [Id]        [int]       IDENTITY(1,1) NOT NULL,
                [Name]      [nvarchar](100)            NOT NULL,
                [ValidFrom] [datetime2] GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
                [ValidTo]   [datetime2] GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
                PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo])
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);

        r.Tables.Should().ContainSingle();
        var t = r.Tables[0];

        // Must NOT have a phantom "PERIOD" column
        t.Fields.Should().NotContain(f => f.Name == "PERIOD",
            "PERIOD FOR SYSTEM_TIME is a temporal clause, not a column definition");

        // Real columns must be present
        t.Fields.Should().Contain(f => f.Name == "Id");
        t.Fields.Should().Contain(f => f.Name == "Name");
        t.Fields.Should().Contain(f => f.Name == "ValidFrom");
        t.Fields.Should().Contain(f => f.Name == "ValidTo");

        // System-time columns are datetime2 NOT NULL (HIDDEN is a SQL Server modifier, not type)
        var validFrom = t.Fields.First(f => f.Name == "ValidFrom");
        validFrom.Type.Should().StartWith("datetime2", "GENERATED ALWAYS AS ROW START is a datetime2 column");
        validFrom.Nullable.Should().BeFalse("system-time columns are NOT NULL");
    }

    [Fact]
    public void SqlServer_TemporalTable_PeriodClause_ExactFieldCount()
    {
        // Secondary guard: the table must have exactly 4 fields (Id, Name, ValidFrom, ValidTo).
        // Any phantom field would inflate the count.
        const string sql = """
            CREATE TABLE [dbo].[Temporal] (
                [Id]        [int]       IDENTITY(1,1) NOT NULL,
                [Name]      [nvarchar](100)            NOT NULL,
                [ValidFrom] [datetime2] GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
                [ValidTo]   [datetime2] GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
                PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo])
            );
            """;

        var r = _parser.Parse(sql, DbProvider.SqlServer);

        r.Tables[0].Fields.Should().HaveCount(4,
            "exactly Id, Name, ValidFrom, ValidTo — no phantom PERIOD column");
    }

    // ── PostgreSQL array type tests ───────────────────────────────────────────

    [Fact]
    public void Postgres_IntegerArray_SuffixPreserved()
    {
        const string sql = "CREATE TABLE t (ids integer[] NOT NULL);";

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Fields[0].Type.Should().Be("integer[]");
    }

    [Fact]
    public void Postgres_TextArray_SuffixPreserved()
    {
        const string sql = "CREATE TABLE t (tags text[] NOT NULL);";

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Fields[0].Type.Should().Be("text[]");
    }

    [Fact]
    public void Postgres_CharacterVaryingArray_NormalisedToVarcharArray()
    {
        const string sql = "CREATE TABLE t (names character varying[] NOT NULL);";

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Fields[0].Type.Should().Be("varchar[]");
    }

    [Fact]
    public void Postgres_IntegerArrayWithDefault_DefaultCaptured()
    {
        // pg_dump writes DEFAULT '{}'::integer[]; parser should capture {} as default
        const string sql = """
            CREATE TABLE t (ids integer[] DEFAULT '{}' NOT NULL);
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Fields[0].Type.Should().Be("integer[]");
        r.Tables[0].Fields[0].Default.Should().NotBeNull();
    }

    [Fact]
    public void Postgres_MixedColumns_ArrayAndNonArray_BothCorrect()
    {
        // Real Discourse DDL pattern: mix of regular and array columns
        const string sql = """
            CREATE TABLE ai_agents (
                id bigint NOT NULL,
                name character varying(100) NOT NULL,
                allowed_group_ids integer[] DEFAULT '{}' NOT NULL
            );
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        var fields = r.Tables[0].Fields;
        fields.Should().HaveCount(3);
        fields.Single(f => f.Name == "id").Type.Should().Be("bigint");
        fields.Single(f => f.Name == "name").Type.Should().Be("varchar(100)");
        fields.Single(f => f.Name == "allowed_group_ids").Type.Should().Be("integer[]");
    }

    // ── Schema-qualified type and FULLTEXT column name (Pagila gaps) ──────────

    [Fact]
    public void Postgres_SchemaQualifiedDomainType_CapturedFully()
    {
        // Pagila uses public.year and public.mpaa_rating as column types.
        // The tokenizer must not split on the dot.
        const string sql = """
            CREATE TABLE film (
                release_year public.year,
                rating public.mpaa_rating DEFAULT 'G'
            );
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Fields.Should().HaveCount(2);
        r.Tables[0].Fields[0].Type.Should().Be("public.year");
        r.Tables[0].Fields[1].Type.Should().Be("public.mpaa_rating");
    }

    [Fact]
    public void Postgres_ColumnNamedFulltext_NotTreatedAsConstraint()
    {
        // "fulltext" is a valid PostgreSQL column name (used in Pagila for tsvector).
        // IsConstraintEntry must not skip it — only "FULLTEXT KEY/INDEX" is MySQL syntax.
        const string sql = """
            CREATE TABLE film (
                id integer NOT NULL,
                fulltext tsvector NOT NULL
            );
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Fields.Should().HaveCount(2);
        var ft = r.Tables[0].Fields.Single(f => f.Name == "fulltext");
        ft.Type.Should().Be("tsvector");
        ft.Nullable.Should().BeFalse();
    }

    [Fact]
    public void MySQL_FulltextKey_StillTreatedAsConstraint()
    {
        // MySQL FULLTEXT KEY / FULLTEXT INDEX must still be skipped (not read as a column).
        const string sql = """
            CREATE TABLE articles (
                id int NOT NULL,
                body text NOT NULL,
                FULLTEXT KEY idx_body (body)
            );
            """;

        var r = _parser.Parse(sql, DbProvider.MySql);
        r.Tables[0].Fields.Should().HaveCount(2,
            "FULLTEXT KEY is an index declaration, not a column");
    }

    // ── character(N) → char(N) normalisation ──────────────────────────────────

    [Fact]
    public void Postgres_CharacterNoLength_NormalisedToChar()
    {
        const string sql = """
            CREATE TABLE t (
                code character NOT NULL
            );
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Fields[0].Type.Should().Be("char",
            "bare 'character' is the SQL long form of 'char'");
    }

    [Fact]
    public void Postgres_CharacterWithLength_NormalisedToChar()
    {
        const string sql = """
            CREATE TABLE t (
                code character(10) NOT NULL
            );
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Fields[0].Type.Should().Be("char(10)",
            "'character(N)' must normalise to 'char(N)'");
    }

    [Fact]
    public void Postgres_CharacterVaryingUnaffectedByCharFix()
    {
        // Regression: 'character varying' must not accidentally match the 'character' prefix check
        const string sql = """
            CREATE TABLE t (
                name character varying(100) NOT NULL
            );
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables[0].Fields[0].Type.Should().Be("varchar(100)",
            "'character varying' must still normalise to 'varchar', not 'char varying'");
    }

    // ── Dollar-quote function body erased from DDL ────────────────────────────

    [Fact]
    public void Postgres_DollarQuotedFunctionBody_PhantomTableNotCreated()
    {
        // A CREATE TEMPORARY TABLE inside a $_$-delimited function body must not
        // appear in the parse result.  This covers the Pagila rewards_report gap.
        const string sql = """
            CREATE TABLE real_table (
                id integer NOT NULL
            );

            CREATE FUNCTION rewards_report() RETURNS void AS $_$
            DECLARE
                tmpSQL text;
            BEGIN
                CREATE TEMPORARY TABLE tmpCustomer (customer_id INTEGER NOT NULL PRIMARY KEY);
                tmpSQL := 'SELECT 1';
            END
            $_$ LANGUAGE plpgsql;
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables.Should().HaveCount(1, "tmpCustomer lives inside a dollar-quoted function body and must be erased");
        r.Tables[0].Name.Should().Be("real_table");
    }

    [Fact]
    public void Postgres_EmptyTagDollarQuote_BodyErased()
    {
        // $$ (empty tag) is the most common dollar-quote delimiter in PostgreSQL.
        const string sql = """
            CREATE TABLE orders (
                id integer NOT NULL
            );

            CREATE FUNCTION do_nothing() RETURNS void AS $$
            BEGIN
                CREATE TEMPORARY TABLE ghost (x integer);
            END
            $$ LANGUAGE plpgsql;
            """;

        var r = _parser.Parse(sql, DbProvider.Postgres);
        r.Tables.Should().HaveCount(1, "ghost table inside $$ body must not appear in parse result");
        r.Tables[0].Name.Should().Be("orders");
    }
}
