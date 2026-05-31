# Manifesta — Init Commands

← [Back to documentation](./documentation.md)

---

## Table of Contents

- [Init DBML](#init-dbml)
- [Init Prisma](#init-prisma)
- [Init SQL](#init-sql)
- [Init DB](#init-db)

---

## Init DBML

Bootstraps a Manifesta project from an existing DBML file. Reads a `.dbml` file, extracts table definitions, and writes one `table.json` per table and one `section.json` per `TableGroup`.

```bash
# Basic import — writes to ./tables and ./document-sections
manifesta init dbml --input database.dbml

# Apply a schema prefix to unqualified table names
manifesta init dbml --input database.dbml --schema dbo

# Custom output directories
manifesta init dbml \
  --input database.dbml \
  --output-dir ./tables \
  --sections-dir ./document-sections

# Preview without writing any files
manifesta init dbml --input database.dbml --dry-run

# Import tables only, skip section files
manifesta init dbml --input database.dbml --no-sections

# Overwrite existing table.json files
manifesta init dbml --input database.dbml --overwrite
```

**Flags:**

| Flag | Required | Default | Description |
|------|----------|---------|-------------|
| `--input` | Yes | — | Path to the DBML file to import |
| `--output-dir` | No | `./tables` | Directory for generated `table.json` files |
| `--sections-dir` | No | `./document-sections` | Directory for generated section JSON files |
| `--no-sections` | No | false | Skip generating section files from `TableGroup` blocks |
| `--schema` | No | — | Schema prefix applied to unqualified table names (e.g. `dbo`) |
| `--overwrite` | No | false | Overwrite existing `table.json` files |

**What is imported:**

| DBML element | Imported as |
|--------------|-------------|
| `Table` block | `table.json` with fields, types, and constraints |
| `[pk]` attribute | `isPrimaryKey: true` and entry in `primaryKey[]` |
| `[not null]` attribute | `nullable: false` |
| `[note: "..."]` attribute | `description` on the field |
| `Table Note: "..."` | `description` on the table |
| `Ref: A.col > B.col` | Physical FK on the source table |
| `Ref: A.col > B.col // logical` | Logical FK |
| `Ref: A.col > B.col // virtual` | Virtual FK |
| `TableGroup` block | `section.json` in `--sections-dir` |
| `note: "// calculated: ([expr]) PERSISTED"` | `isComputed`, `computedExpression`, `isPersisted` |

**What is NOT imported:**

- Column default values (no standard DBML syntax)
- Unique constraints
- Index definitions

**Exit codes:**

| Code | Meaning |
|------|---------|
| `0` | Import succeeded |
| `1` | DBML parse errors or file write failure |
| `2` | Input file not found |

**Recommended migration workflow from dbdocs.io:**

1. Export your DBML file from dbdocs.io
2. Run `manifesta init dbml --input database.dbml --schema dbo`
3. Review the generated `tables/` and `document-sections/` directories
4. Commit the result as your initial Manifesta schema registry

---

## Init Prisma

Bootstraps a Manifesta project from an existing Prisma schema file. Reads a `.prisma` schema, extracts model definitions, and writes one `table.json` per model. The database provider, relation mode, and native type overrides are inferred automatically from the `datasource` block.

```bash
# Basic import — reads schema.prisma, writes to ./tables
manifesta init prisma --input ./prisma/schema.prisma

# Apply a schema prefix to all table names
manifesta init prisma --input ./prisma/schema.prisma --schema dbo

# Override the database provider
manifesta init prisma --input ./prisma/schema.prisma --provider postgres

# Also import enum blocks as reference tables
manifesta init prisma --input ./prisma/schema.prisma --import-enums

# Overwrite existing table.json files
manifesta init prisma --input ./prisma/schema.prisma --overwrite
```

**Flags:**

| Flag | Required | Default | Description |
|------|----------|---------|-------------|
| `--input` | Yes | — | Path to the `.prisma` schema file |
| `--output-dir` | No | `./tables` | Directory for generated `table.json` files |
| `--provider` | No | auto-detected | Override the datasource provider: `sqlserver`, `mysql`, `postgres` |
| `--schema` | No | — | Prefix all table names with `<schema>.` (e.g. `dbo`) |
| `--import-enums` | No | false | Also import `enum` blocks as reference tables |
| `--overwrite` | No | false | Overwrite existing `table.json` files |

**What is imported:**

| Prisma element | Imported as |
|----------------|-------------|
| `model` block | `table.json` with fields, types, PKs, and FKs |
| `@@map("tableName")` | Table name override |
| `@id` / `@@id` | `isPrimaryKey: true` and `primaryKey[]` |
| `@unique` / `@@unique` | `uniqueConstraints[]` entry |
| `@@index` | `indexes[]` entry |
| `@relation(fields:[f], references:[r])` | Physical FK (or logical when `relationMode = "prisma"`) |
| `@db.NativeType(...)` | SQL type override (e.g. `@db.VarChar(255)` → `varchar(255)`) |
| `@default(value)` | `defaultValue` on the field |
| `?` suffix | `nullable: true` |
| `Unsupported("nativeType")` | SQL type set to the quoted native type |
| `enum` block (with `--import-enums`) | `table.json` with `isReferenceTable: true` |

**What is NOT imported:**

- `generator` blocks (ignored)
- `@@schema` (use `--schema` flag instead for a uniform prefix)
- Composite foreign keys (models with multi-field `@relation` are skipped)
- `@default(autoincrement())`, `@default(cuid())`, `@default(uuid())`, `@default(now())` — these have no SQL-level default equivalent and are omitted

**Provider detection:**

The `datasource` block provider is used to select the correct SQL type for each Prisma scalar:

| Prisma type | `sqlserver` | `mysql` | `postgres` |
|-------------|-------------|---------|------------|
| `String` | `nvarchar(max)` | `varchar(191)` | `text` |
| `Int` | `int` | `int` | `integer` |
| `BigInt` | `bigint` | `bigint` | `bigint` |
| `Float` | `float` | `double` | `double precision` |
| `Decimal` | `decimal(18,6)` | `decimal(18,6)` | `decimal(65,30)` |
| `Boolean` | `bit` | `tinyint(1)` | `boolean` |
| `DateTime` | `datetime2` | `datetime` | `timestamp` |
| `Bytes` | `varbinary(max)` | `longblob` | `bytea` |
| `Json` | `nvarchar(max)` | `json` | `jsonb` |

Use `--provider` to override the detected provider when the `datasource` block is absent or uses an unsupported provider value. Defaults to `sqlserver` when no provider can be determined.

**Foreign key kind:**

| Prisma setting | FK kind |
|----------------|---------|
| `relationMode = "foreignKeys"` (default) | `physical` |
| `relationMode = "prisma"` | `logical` |

**Exit codes:**

| Code | Meaning |
|------|---------|
| `0` | Import succeeded |
| `1` | Parse errors, write failures, or unresolvable models |
| `2` | Input file not found |

**Recommended workflow from an existing Prisma project:**

1. Run `manifesta init prisma --input ./prisma/schema.prisma --schema dbo`
2. Review the generated `tables/` directory
3. Commit the result as your initial Manifesta schema registry

---

## Init SQL

`init sql` parses one or more SQL DDL files and writes one `table.json` per table. No live database connection is required — the command operates entirely on text.

All four SQL dialects are supported, including **SQL Server T-SQL** (the only init command where SQL Server is available in the OSS edition, because it requires no live connection).

```bash
# Single file — writes to ./tables by default
manifesta init sql --input schema.sql

# Specify the SQL dialect explicitly
manifesta init sql --input schema.sql --provider postgres

# Parse all .sql files in a directory (top-level only)
manifesta init sql --input ./migrations --provider mysql

# Also search subdirectories — plain filename pattern + --recursive
manifesta init sql --input ./migrations --provider mysql --recursive

# Only process "up" migration files (plain filename pattern + recursive)
manifesta init sql --input ./migrations --provider mysql --recursive --pattern "*_up.sql"

# Equivalent using a path glob directly (no --recursive needed)
manifesta init sql --input ./migrations --provider mysql --pattern "**/*_up.sql"

# Target a specific year directory only
manifesta init sql --input ./migrations --provider mysql --pattern "2024/*.sql"

# Only files in 2024/ that are create-scripts
manifesta init sql --input ./migrations --provider mysql --pattern "2024/create_*.sql"

# Apply a schema prefix to unqualified table names
manifesta init sql --input dump.sql --schema dbo --provider sqlserver

# Also process ALTER TABLE statements after CREATE TABLE blocks (SQL Server T-SQL)
# Required when PKs and FKs are declared via ALTER TABLE ADD CONSTRAINT rather
# than inline in CREATE TABLE — common in SQL Server scripts and instawdb.sql style dumps
manifesta init sql --input instawdb.sql --provider sqlserver --include-migrations

# Skip CREATE VIEW output — useful for DDL-based drift detection (see below)
manifesta init sql --input schema.sql --provider sqlserver --no-views

# Overwrite existing table.json files
manifesta init sql --input schema.sql --overwrite

# Preview without writing
manifesta init sql --input schema.sql --dry-run
```

**Flags:**

| Flag | Required | Default | Description |
|------|----------|---------|-------------|
| `--input` | Yes | — | Path to a `.sql` file **or** a directory of `.sql` files |
| `--output-dir` | No | `./tables` | Directory for generated `table.json` files |
| `--provider` | No | `mysql` | SQL dialect: `mysql`, `postgres`, `sqlite`, `sqlserver` |
| `--schema` | No | — | Schema prefix applied to tables that have no schema qualifier (e.g. `dbo`) |
| `--include-migrations` | No | false | Also process `ALTER TABLE` statements that appear after `CREATE TABLE` blocks. Required when PKs and FKs are declared via `ALTER TABLE ADD CONSTRAINT` rather than inline — common in SQL Server T-SQL scripts. |
| `--no-views` | No | false | Skip writing JSON files for `CREATE VIEW` statements. Useful when generating a temporary snapshot for DDL-based drift detection — views parsed from DDL have column types set to `"unknown"`, which would produce false-positive drift against repo view files that have real types. |
| `--overwrite` | No | false | Overwrite existing `table.json` files |
| `--recursive` / `-r` | No | false | Expand a plain filename `--pattern` to all subdirectories (prepends `**/`). Ignored when `--pattern` already contains a path separator or `**`, and when `--input` is a single file |
| `--pattern` | No | `*.sql` | Glob pattern for file matching when `--input` is a directory. **Plain filename patterns** (e.g. `*_up.sql`) are controlled by `--recursive`. **Path globs** (e.g. `2024/**/*.sql`, `**/create_*.sql`) are matched directly and ignore `--recursive` |

**Supported SQL sources:**

| Source | Notes |
|--------|-------|
| Clean DDL scripts | Hand-authored or tool-generated `CREATE TABLE` statements |
| `mysqldump --no-data` output | MySQL dump files including SET, LOCK, DROP, and comment noise |
| `pg_dump --schema-only` output | PostgreSQL dump files including `SET`, `SELECT pg_catalog.*`, and cast noise |
| SQLite `CREATE TABLE` DDL | As emitted by SQLite tooling |
| SQL Server T-SQL `CREATE TABLE` | Including bracket identifiers, IDENTITY columns, and computed columns |

**What is imported per dialect:**

| Element | MySQL | PostgreSQL | SQLite | SQL Server |
|---------|:-----:|:----------:|:------:|:----------:|
| Table name (schema-qualified) | ✓ | ✓ | ✓ | ✓ |
| Column names and types | ✓ | ✓ | ✓ | ✓ |
| NOT NULL / NULL | ✓ | ✓ | ✓ | ✓ |
| DEFAULT value | ✓ | ✓ | ✓ | ✓ |
| Table-level PRIMARY KEY | ✓ | ✓ | ✓ | ✓ |
| Inline PRIMARY KEY | ✓ | ✓ | ✓ | ✓ |
| FOREIGN KEY (table-level) | ✓ | ✓ | ✓ | ✓ |
| `ALTER TABLE ADD CONSTRAINT` PK / FK (`--include-migrations`) | — | — | — | ✓ |
| UNIQUE constraints | ✓ | ✓ | ✓ | ✓ |
| CHECK constraints | ✓ | ✓ | ✓ | ✓ (normalised) |
| `CREATE VIEW` — column names | ✓ | ✓ | ✓ | ✓ |
| `COMMENT '...'` on column | ✓ | — | — | — |
| `SERIAL` / `BIGSERIAL` → integer | — | ✓ | — | — |
| `GENERATED ALWAYS AS (expr) STORED` | ✓ | ✓ | — | — |
| `col AS (expr) [PERSISTED]` | — | — | — | ✓ |
| `GENERATED ALWAYS AS IDENTITY` | — | stripped | — | — |
| `AUTO_INCREMENT` / `AUTOINCREMENT` | stripped | — | stripped | — |
| `IDENTITY(seed, inc)` | — | — | — | stripped |
| User-defined type aliases (`CREATE TYPE … FROM`) | — | — | — | ✓ |
| XML schema collection qualifier stripped | — | — | — | ✓ |

**Type normalisation:**

| Input | Provider | Stored as |
|-------|----------|-----------|
| `INTEGER` | MySQL | `int` |
| `int(11)` | MySQL | `int` (display width removed) |
| `tinyint(1)` | MySQL | `tinyint(1)` (boolean convention, preserved) |
| `SERIAL` | PostgreSQL | `integer` |
| `BIGSERIAL` | PostgreSQL | `bigint` |
| `CHARACTER VARYING(n)` | PostgreSQL | `character varying(n)` |
| `TIMESTAMP WITH TIME ZONE` | PostgreSQL | `timestamp with time zone` |
| `[int]`, `[nvarchar(100)]` | SQL Server | `int`, `nvarchar(100)` (brackets stripped) |
| All other types | All | lowercased as-is |

**SQL Server note:**

`init sql` is the **only** `init` command that supports SQL Server in the OSS edition. SQL Server support is possible here because parsing DDL text requires no live database connection — the `DatabaseIntrospectorRegistry` enterprise gate is not involved.

SQL Server-specific behaviour:

| Behaviour | Details |
|-----------|---------|
| `--include-migrations` | Processes `ALTER TABLE ADD CONSTRAINT` statements that follow `CREATE TABLE` blocks. SQL Server scripts commonly declare all PKs and FKs this way — without this flag every table will have an empty `primaryKey` and no `foreignKeys`. |
| CHECK expression normalisation | SQL Server stores CHECK expressions in a canonical internal form. The parser applies the same rules: `BETWEEN a AND b` → `>=(a) AND <=(b)`; `IN (...)` → `OR` chain (reversed); function names lowercased; numeric literals parenthesised. The resulting expression matches what `sys.check_constraints` returns, so `db drift` reports zero CHECK drift pre-merge. |
| `CREATE VIEW` | View column names are extracted from the `SELECT` list and stored with `"isView": true` and `"type": "unknown"`. Column types are resolved from the live database by `db merge`. Four aliasing patterns are handled: `expr AS [alias]`, `[alias] = expr` (SQL Server assignment style), `table.[col]` (dotted reference), and bare identifiers. |
| User-defined type aliases | `CREATE TYPE [name] FROM base_type` declarations are scanned and the aliases substituted in `CREATE TABLE` column definitions (e.g. `name` → `nvarchar(50)`, `flag` → `bit`). |
| XML schema collections | `xml([schema].[collection])` column types are normalised to bare `xml` — matching the form returned by the SQL Server introspector. |

To bootstrap from a SQL Server database (rather than a DDL file), use the `init db` command from the full edition.

**DDL-based drift detection (two-phase workflow):**

Use `init sql` together with `db drift` to detect schema drift between your DDL files and your repo JSON files without a live database connection. Use `--no-views` to suppress view output because views parsed from DDL have column types set to `"unknown"`, which would produce false-positive drift against repo view files that already have real types from a previous `db merge`.

```bash
# Phase 1 — parse DDL into a temporary snapshot
manifesta init sql \
  --input ./modules \
  --recursive \
  --provider sqlserver \
  --default-schema dbo \
  --no-views \
  --overwrite \
  --output-dir /tmp/manifesta/tables

# Phase 2 — compare snapshot against repo JSON files
manifesta db drift \
  --input-dir /tmp/manifesta/tables \
  --config ./modules/_/manifesta.config.json
```

**Exit codes:**

| Code | Meaning |
|------|---------|
| `0` | All tables imported successfully (parse errors may still be reported as warnings with `--warn-only`) |
| `1` | One or more parse errors encountered |
| `4` | Missing required `--input` flag, unknown provider, or duplicate table names across input files |
| `5` | Duplicate table names detected across input files (treated as a fatal schema error) |

---

## Init DB

`init db` connects to a live database, introspects the full schema, and writes one `table.json` per table.

**MySQL and PostgreSQL are supported in the OSS edition.** SQL Server requires the full edition.

```bash
# MySQL
manifesta init db --provider mysql --connection "Server=localhost;Database=mydb;Uid=root;Pwd=secret;"

# PostgreSQL
manifesta init db --provider postgres --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret;"

# Apply a schema filter (PostgreSQL example — only introspect the 'public' and 'app' schemas)
manifesta init db --provider postgres --connection "..." --schema public,app

# Preview without writing any files
manifesta init db --provider mysql --connection "..." --dry-run

# Overwrite existing table.json files
manifesta init db --provider postgres --connection "..." --overwrite
```

**Flags:**

| Flag | Required | Default | Description |
|------|----------|---------|-------------|
| `--provider` | Yes | — | Database provider: `mysql` or `postgres` (OSS); `sqlserver` requires full edition |
| `--connection` | Yes | — | ADO.NET connection string for the target database |
| `--output-dir` | No | `./tables` | Directory for generated `table.json` files |
| `--schema` | No | — | Comma-separated list of schemas to include (e.g. `public,app`). When omitted, all non-system schemas are introspected |
| `--dry-run` | No | false | Preview what would be written without creating any files |
| `--overwrite` | No | false | Overwrite existing `table.json` files |

**What is imported:**

| Database element | Imported as |
|-----------------|-------------|
| Tables and views | `table.json` with fields, types, nullability, defaults |
| Primary key | `primaryKey[]` and `isPrimaryKey: true` on the field |
| Foreign keys | Physical FKs in `foreignKeys[]` |
| Generated / computed columns | `isComputed: true`, `computedExpression`, `isPersisted` |
| Indexes | `indexes[]` |
| CHECK constraints | `checkConstraints[]` |
| UNIQUE constraints | `uniqueConstraints[]` |

**What is NOT imported:**

- Reference table row data — populate the `data` array in each `table.json` manually after import
- Views are introspected as read-only table definitions; no special view metadata is preserved

**SQL Server:**

> SQL Server is not currently supported by `init db`.

**Exit codes:**

| Code | Meaning |
|------|---------|
| `0` | Import succeeded |
| `1` | Connection failure, introspection error, or write failure |
| `2` | Missing required flags |
