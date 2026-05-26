# Manifesta — DB Commands

← [Back to documentation](./documentation.md)

---

## Table of Contents

- [db drift](#db-drift)
- [db merge](#db-merge)

---

## db drift

Compares the schema registry against a live database (or pre-exported JSON files) and reports any structural differences. Read-only — no registry files are written. Use it as a CI gate to catch schema divergence before it reaches production.

```bash
# Compare against a live MySQL database
manifesta db drift --provider mysql --connection "Server=localhost;Database=mydb;Uid=root;Pwd=secret;"

# Compare against a live PostgreSQL database
manifesta db drift --provider postgres --connection "Host=localhost;Database=mydb;Username=postgres;Password=secret;"

# Compare against pre-exported JSON files (no live connection needed)
manifesta db drift --input-dir ./exported-tables

# Restrict to specific schemas (PostgreSQL)
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
| `--connection` | One of these | — | ADO.NET connection string for the live database |
| `--input-dir` | One of these | — | Directory of pre-exported `table.json` files to treat as the live snapshot |
| `--provider` | No | `mysql` | Database provider: `mysql` or `postgres`. SQL Server requires the full edition. |
| `--schema` | No | — | Comma-separated schemas to restrict the comparison (e.g. `public,app`). Ignored for MySQL. |
| `--strict` | No | false | Exit 1 on warnings (extra columns or tables in DB not present in the registry) |
| `--include-schema` | No | false | Embed full before/after field listings for each drifted table in the report |
| `--output` | No | — | Full path for the drift report file |
| `--output-dir` | No | `.` | Directory for the drift report file |

`--connection` and `--input-dir` are mutually exclusive. Exactly one must be provided.

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
- A summary counts table
- Per-table change tables listing field, FK, and PK changes
- A section for tables in the registry absent from the DB
- A section for extra tables in the DB not tracked in the registry
- A clean-table reference list (with `--include-schema`, each table's full field listing is added)

---

### Exit codes

| Code | Meaning |
|------|---------|
| `0` | No drift detected; registry and database are in sync |
| `1` | Drift detected, or warnings present with `--strict` |
| `4` | Invalid flags (both `--connection` and `--input-dir` given, or neither given) |
| `5` | Duplicate table name found in the registry |

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
| `--provider` | No | `mysql` | Database provider: `mysql` or `postgres`. SQL Server requires the full edition. |
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
