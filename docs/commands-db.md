# Manifesta — DB Commands

← [Back to documentation](./documentation.md)

---

## Table of Contents

- [db export](#db-export)
- [db drift](#db-drift)
  - [db drift --ddl-file](#db-drift---ddl-file)
- [db merge](#db-merge)

---

## db export

Introspects a live database and writes one `table.json` file per table into an output directory. The output is in the exact format consumed by `db drift --input-dir` and `db merge --input-dir`, making this the first step in every air-gapped workflow.

```bash
# Export schema from a live MySQL database
manifesta db export --provider mysql --connection "Server=localhost;Database=mydb;Uid=root;Pwd=secret;"

# Export from PostgreSQL, restricting to specific schemas
manifesta db export --provider postgres --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret;" --schema public,app

# Export from a local SQLite file
manifesta db export --provider sqlite --connection "Data Source=./mydb.sqlite3;"

# Write to a specific directory
manifesta db export --connection "..." --output-dir ./snapshots/prod

# Also export views
manifesta db export --connection "..." --include-views

# Overwrite existing snapshot files (default skips files that already exist)
manifesta db export --connection "..." --overwrite
```

**Flags:**

| Flag | Required | Default | Description |
|------|----------|---------|-------------|
| `--connection` | Yes | — | ADO.NET connection string for the live database |
| `--output-dir` | No | `./export` | Directory for the exported JSON files |
| `--provider` | No | `mysql` | Database provider: `mysql`, `postgres`, or `sqlite`. SQL Server requires the full edition. |
| `--schema` | No | — | Comma-separated schemas to export (e.g. `public,app`). Ignored for MySQL and SQLite. |
| `--include-views` | No | false | Also export database views alongside tables |
| `--overwrite` | No | false | Overwrite existing JSON files. Without this flag, files that already exist are skipped. |

---

### Typical usage

`db export` is the start of the air-gapped workflow. Run it once on a machine with database access to produce a snapshot, then ship the snapshot files to CI or to another machine where no live connection is available:

```bash
# Step 1 — on a machine with DB access
manifesta db export \
  --provider postgres \
  --connection "$DB_CONNECTION" \
  --output-dir ./snapshots/prod \
  --overwrite

# Step 2 — anywhere (no credentials required)
manifesta db drift  --input-dir ./snapshots/prod
manifesta db merge  --input-dir ./snapshots/prod
```

---

### Exit codes

| Code | Meaning |
|------|---------|
| `0` | Export completed successfully |
| `4` | `--connection` not provided, or directory could not be created |
| `5` | Introspection failed (connection error, permission denied, etc.) |

---

## db drift

Compares the schema registry against a live database, pre-exported JSON files, or SQL DDL files, and reports any structural differences. Read-only — no registry files are written. Use it as a CI gate to catch schema divergence before it reaches production.

Three input modes are available — exactly one must be provided:

| Mode | Flag | Live connection | SQL Server |
|------|------|:--------------:|:----------:|
| Live database | `--connection` | Yes | Full edition |
| Pre-exported JSON | `--input-dir` | No | Full edition |
| SQL DDL files | `--ddl-file` | No | **Yes (OSS)** |

```bash
# Compare against a live MySQL database
manifesta db drift --provider mysql --connection "Server=localhost;Database=mydb;Uid=root;Pwd=secret;"

# Compare against a live PostgreSQL database
manifesta db drift --provider postgres --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret;"

# Compare against pre-exported JSON files (no live connection needed)
manifesta db drift --input-dir ./exported-tables

# Compare against SQL DDL files — all four providers, no connection required
manifesta db drift --ddl-file schema.sql --provider mysql
manifesta db drift --ddl-file schema.sql --provider sqlserver

# DDL file directory — recursive with pattern filter
manifesta db drift --ddl-file ./migrations --provider mysql --recursive
manifesta db drift --ddl-file ./migrations --provider mysql --pattern "**/*_up.sql"

# Restrict to specific schemas (connection/input-dir modes; PostgreSQL example)
manifesta db drift --provider postgres --connection "..." --schema public,app

# Exit 1 on warnings as well as drift
manifesta db drift --connection "..." --strict

# Embed full before/after field listings in the report
manifesta db drift --connection "..." --include-schema

# Write the report to a specific directory
manifesta db drift --connection "..." --output-dir ./reports
```

**Flags:**

| Flag | Required | Default | Description |
|------|----------|---------|-------------|
| `--connection` | One of three | — | ADO.NET connection string for the live database |
| `--input-dir` | One of three | — | Directory of pre-exported `table.json` files to treat as the live snapshot |
| `--ddl-file` | One of three | — | Path to a `.sql` file or directory of `.sql` files to diff against the registry. Supports `mysql`, `postgres`, `sqlite`, and `sqlserver` (no live connection required). |
| `--provider` | No | `mysql` | Database provider: `mysql`, `postgres`, `sqlite`. Also accepts `sqlserver` when used with `--ddl-file`. |
| `--schema` | No | — | **With `--connection`/`--input-dir`:** comma-separated schema filter (e.g. `public,app`); ignored for MySQL/SQLite. **With `--ddl-file`:** prefix applied to unqualified table names in the DDL (same as `init sql --schema`). |
| `--recursive` / `-r` | No | false | Expand a plain filename `--pattern` to all subdirectories. Only applicable when `--ddl-file` is a directory. |
| `--pattern` | No | `*.sql` | Glob pattern for file matching when `--ddl-file` is a directory. Plain filename patterns (e.g. `*_up.sql`) are controlled by `--recursive`. Path globs (e.g. `2024/**/*.sql`) are matched directly. |
| `--strict` | No | false | Exit 1 on warnings (extra columns or tables in DB not present in the registry) |
| `--include-schema` | No | false | Embed full before/after field listings for each drifted table in the report |
| `--no-fk-drifts` | No | false | Suppress FK change rows from the per-table drift sections |
| `--no-index-drifts` | No | false | Suppress index change rows from the per-table drift sections |
| `--no-clean-tables` | No | false | Suppress the clean-tables reference list from the report |
| `--output` | No | — | Full path for the drift report file |
| `--output-dir` | No | `.` | Directory for the drift report file |

`--connection`, `--input-dir`, and `--ddl-file` are mutually exclusive. Exactly one must be provided.

---

### db drift --ddl-file

`--ddl-file` parses one or more SQL `CREATE TABLE` files using the same engine as `init sql` and feeds the result directly into the standard drift pipeline. No database connection is required, and **SQL Server is supported in the OSS edition**.

This is the primary CI pattern when:
- Your database is SQL Server (live introspection requires the full edition)
- You generate or version-control DDL scripts alongside the registry
- You want to verify that the registry still matches the authoritative schema after a DDL change

```bash
# Verify a single T-SQL file matches the registry
manifesta db drift --ddl-file tables.sql --provider sqlserver

# Apply a schema prefix to unqualified DDL names — same as init sql --schema
manifesta db drift --ddl-file tables.sql --provider sqlserver --schema dbo

# CI pipeline: verify all up-migrations across a nested directory
manifesta db drift \
  --ddl-file  ./migrations \
  --provider  mysql \
  --pattern   "**/*_up.sql"
```

**`--schema` behaviour with `--ddl-file`:**

When a DDL file contains unqualified table names (e.g. `CREATE TABLE Customer`), the registry JSON typically stores qualified names (e.g. `dbo.Customer`). Use `--schema dbo` to apply the prefix during parsing — this is the same flag used when you originally ran `init sql --schema dbo`.

**Parse errors and `--warn-only`:**

If the DDL file contains errors, the command exits `1` before running the diff — a partial parse could produce a misleading report. Use `--warn-only` to suppress this and proceed with best-effort results (useful when the DDL contains statements that Manifesta doesn't support, such as `CREATE INDEX` or stored procedure definitions).

```bash
# Proceed despite parse errors — only successfully parsed tables are compared
manifesta db drift --ddl-file schema.sql --provider mysql --warn-only
```

---

### What counts as drift

A drifted table causes an exit code of `1`:

| Change | Classified as |
|--------|---------------|
| Field type changed in DB | Drift |
| Field nullability changed in DB | Drift |
| Field default value changed in DB | Drift |
| Computed expression changed in DB | Drift |
| Primary key column sequence changed | Drift |
| Physical FK added or removed in DB | Drift |
| Physical FK cascade rule changed | Drift |
| Field present in registry but absent from DB | Drift |
| Table present in registry but absent from DB | Drift |

The following are classified as **warnings** (exit `0` by default; exit `1` with `--strict`):

| Change | Classified as |
|--------|---------------|
| Extra column in DB not in registry | Warning |
| Extra table in DB not in registry | Warning |

Logical and virtual FKs are always ignored — they are repo-sovereign and have no live DB representation.

---

### Output

`db drift` writes a `drift-report.md` file to the output directory. The report contains:

- A status banner: ✅ **In sync**, ⚠️ **Warnings**, or ❌ **Drift detected**
- A summary counts table (tables scanned, in sync, drifted, absent; FK change total; index change total)
- Per-table change tables listing field, PK, FK, and index changes
- A section for tables in the registry absent from the source
- A section for extra tables in the source not tracked in the registry
- A clean-table reference list (suppressed with `--no-clean-tables`)
- With `--include-schema`: full before/after field listings labelled **Repository definition** and **Source definition**

---

### Exit codes

| Code | Meaning |
|------|---------|
| `0` | No drift detected; registry and source are in sync |
| `1` | Drift detected; warnings present with `--strict`; or DDL parse errors (use `--warn-only` to override) |
| `4` | Invalid flags (wrong combination of `--connection`, `--input-dir`, `--ddl-file`) |
| `5` | Duplicate table name in registry, or no files matched `--pattern` |

---

## db merge

Pulls structural changes from a live database (or pre-exported JSON files) into the registry JSON files. Merges only DB-authoritative properties — all repo-sovereign metadata is preserved.

```bash
# Pull changes from a live MySQL database
manifesta db merge --provider mysql --connection "Server=localhost;Database=mydb;Uid=root;Pwd=secret;"

# Pull changes from a live PostgreSQL database
manifesta db merge --provider postgres --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret;"

# Merge from pre-exported JSON files (air-gapped workflow)
manifesta db merge --input-dir ./exported-tables

# Also remove columns that no longer exist in the DB
manifesta db merge --connection "..." --remove-deleted-columns

# Remove columns AND delete files for tables absent from the DB
manifesta db merge --connection "..." --remove-deleted-columns --remove-deleted-tables

# Write new tables to a specific directory instead of the default tables/
manifesta db merge --connection "..." --new-table-dir ./tables/new

# Update existing tables only; skip creating files for new tables
manifesta db merge --connection "..." --skip-new-tables

# Preview all changes without writing any files
manifesta db merge --connection "..." --dry-run

# Suppress writing the merge report file
manifesta db merge --connection "..." --no-report
```

**Flags:**

| Flag | Required | Default | Description |
|------|----------|---------|-------------|
| `--connection` | One of these | — | ADO.NET connection string for the live database |
| `--input-dir` | One of these | — | Directory of pre-exported `table.json` files to treat as the live snapshot |
| `--provider` | No | `mysql` | Database provider: `mysql`, `postgres`, or `sqlite`. SQL Server requires the full edition. |
| `--schema` | No | — | Comma-separated schemas to restrict the merge (e.g. `public,app`). Ignored for MySQL. |
| `--remove-deleted-columns` | No | false | Remove fields from registry files when they are absent from the live database (opt-in) |
| `--remove-deleted-tables` | No | false | Delete registry files for tables absent from the live database. Requires `--remove-deleted-columns`. |
| `--new-table-dir` | No | `<root>/tables/` | Directory for newly discovered table files |
| `--skip-new-tables` | No | false | Update existing registry files only; skip creating files for tables not yet in the registry |
| `--no-report` | No | false | Suppress writing the merge report file |
| `--output` | No | — | Full path for the merge report file |
| `--output-dir` | No | `.` | Directory for the merge report file |

`--connection` and `--input-dir` are mutually exclusive. Exactly one must be provided.
`--skip-new-tables` and `--new-table-dir` are mutually exclusive.
`--remove-deleted-tables` requires `--remove-deleted-columns`.

---

### What gets merged

`db merge` treats properties differently depending on whether they originate from the live DB or the registry:

**DB-authoritative** — always updated from the live database:

| Property | Notes |
|----------|-------|
| Field type | Reflects the current column type in the DB |
| Field nullability | Reflects `NOT NULL` / nullable in the DB |
| Field default value | Reflects the current column default |
| Computed expression / `isPersisted` | Reflects `sys.computed_columns` (SQL Server) or equivalent |
| Primary key columns | Reflects the current PK definition |
| Physical FKs | Added, updated, or removed to match the live DB |
| Index definitions | Always replaced from the live DB |
| Check constraints | Always replaced from the live DB |
| Unique constraints | Always replaced from the live DB |

**Repo-sovereign** — never overwritten by `db merge`:

| Property | Notes |
|----------|-------|
| `description` | Manual documentation is always preserved |
| `shortDescription` | Manual documentation is always preserved |
| `isMatchColumn` | Sync configuration, set manually |
| `isDeprecated` / `deprecationMessage` | Lifecycle metadata, set manually |
| `sensitivity` | Classification metadata, set manually |
| `isReferenceTable` / `data` | Reference data configuration, set manually |
| Logical and virtual FKs | Repo-sovereign by definition |
| Section membership | Controlled by `section.json` files, not merged |

**Orphan columns** — when a field exists in the registry but is absent from the live DB, it is kept as-is with a warning (unless `--remove-deleted-columns` is set). These appear in the merge report as orphan columns.

---

### Output

`db merge` writes a `merge-report.md` file (unless `--no-report` is set) containing:

- A summary table: modified, unchanged, created, deleted, orphan columns, orphan tables
- A per-table change table for each modified table
- A list of newly created files
- A warnings section listing orphan columns and orphan tables (tables in the registry absent from the DB)

---

### Exit codes

| Code | Meaning |
|------|---------|
| `0` | Merge completed without warnings |
| `1` | Merge completed with warnings (orphan columns or orphan tables in the registry) |
| `4` | Invalid flags |
| `5` | Duplicate table name found in the registry |
