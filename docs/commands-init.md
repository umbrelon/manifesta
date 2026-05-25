# Manifesta — Init Commands

← [Back to documentation](./documentation.md)

---

## Table of Contents

- [Init DBML](#init-dbml)
- [Init Prisma](#init-prisma)
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

## Init DB

`init db` connects to a live database, introspects the full schema, and writes one `table.json` per table.

**MySQL and PostgreSQL are supported in the OSS edition.** SQL Server requires the full edition.

```bash
# MySQL
manifesta init db --provider mysql --connection-string "Server=localhost;Database=mydb;Uid=root;Pwd=secret;"

# PostgreSQL
manifesta init db --provider postgres --connection-string "Host=localhost;Database=mydb;Username=postgres;Password=secret;"

# Apply a schema filter (PostgreSQL example — only introspect the 'public' and 'app' schemas)
manifesta init db --provider postgres --connection-string "..." --schema public,app

# Preview without writing any files
manifesta init db --provider mysql --connection-string "..." --dry-run

# Overwrite existing table.json files
manifesta init db --provider postgres --connection-string "..." --overwrite
```

**Flags:**

| Flag | Required | Default | Description |
|------|----------|---------|-------------|
| `--provider` | Yes | — | Database provider: `mysql` or `postgres` (OSS); `sqlserver` requires full edition |
| `--connection-string` | Yes | — | ADO.NET connection string for the target database |
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
