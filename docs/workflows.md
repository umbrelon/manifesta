# Manifesta — Common Workflows

← [Back to documentation](./documentation.md)

---

## Table of Contents

- [First-time setup from a DBML file](#first-time-setup-from-a-dbml-file)
- [First-time setup from a Prisma schema](#first-time-setup-from-a-prisma-schema)
- [Regenerate docs after schema changes](#regenerate-docs-after-schema-changes)
- [Add manual descriptions after import](#add-manual-descriptions-after-import)
- [Run full validation in CI](#run-full-validation-in-ci)
- [Detect schema drift in CI](#detect-schema-drift-in-ci)
- [Keep the registry in sync](#keep-the-registry-in-sync)
- [Migrate from dbdocs.io](#migrate-from-dbdocsio)

---

## First-time setup from a DBML file

Starting from a `.dbml` file you already have (or exported from dbdocs.io):

```bash
# 1. Bootstrap the schema registry
manifesta init dbml --input database.dbml --schema dbo

# 2. Review the generated files
ls tables/
ls document-sections/

# 3. Generate documentation
manifesta doc db --output-dir ./publish

# 4. Validate
manifesta validate all
manifesta validate cross

# 5. Commit everything
git add tables/ document-sections/ publish/
git commit -m "chore: initial Manifesta schema registry"
```

The registry files in `tables/` and `document-sections/` are now your source of truth. Edit them directly to add descriptions, adjust FK kinds, or configure ERD diagrams. `database.dbml` can be regenerated at any time with `manifesta doc db --format dbml`.

---

## First-time setup from a Prisma schema

```bash
# 1. Bootstrap from schema.prisma — provider is inferred from the datasource block
manifesta init prisma --input ./prisma/schema.prisma --schema dbo

# 2. Optionally import enum blocks as reference tables
manifesta init prisma --input ./prisma/schema.prisma --schema dbo --import-enums

# 3. Generate documentation and validate
manifesta doc db --output-dir ./publish
manifesta validate all
manifesta validate cross
```

FK kinds follow the `relationMode` in your `datasource` block: `"foreignKeys"` → physical, `"prisma"` → logical.

---

## Regenerate docs after schema changes

When you add or modify tables in the registry:

```bash
# Regenerate database.md
manifesta doc db --output-dir ./publish

# Re-validate to catch any new issues introduced by the changes
manifesta validate all --strict
manifesta validate cross
```

Because output is deterministic, the diff in `database.md` will contain exactly the changes you made — nothing more. This makes it safe to commit generated docs alongside schema changes in the same PR.

**Tip:** Add `manifesta doc db` as a `--post-hook` in your workflow to keep docs in sync automatically:

```bash
manifesta validate all --post-hook "manifesta doc db --output-dir ./publish"
```

---

## Add manual descriptions after import

After bootstrapping from DBML or Prisma, field descriptions are blank. Fill them in directly in the JSON registry:

```bash
# 1. Open a generated table file
# tables/dbo.Customer.json

# 2. Add descriptions to fields and the table itself:
# {
#   "name": "dbo.Customer",
#   "description": "Registered customers...",
#   "fields": [
#     { "name": "Id", "description": "Surrogate primary key." },
#     ...
#   ]
# }

# 3. Regenerate docs to see the result
manifesta doc db --output-dir ./publish
```

Use `--dry-run` to preview what would be written before committing:

```bash
manifesta doc db --output-dir ./publish --dry-run
```

---

## Run full validation in CI

Both `validate all` and `validate cross` exit with a non-zero code on failure, making them drop-in pipeline steps.

**GitHub Actions example:**

```yaml
name: Schema validation

on: [push, pull_request]

jobs:
  validate:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Install .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.x"

      - name: Install Manifesta
        run: dotnet tool install --global Rujasy.Manifesta

      - name: Validate per-table rules
        run: manifesta validate all --strict --output-dir ./reports

      - name: Validate cross-entity references
        run: manifesta validate cross --output-dir ./reports

      - name: Upload validation reports
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: validation-reports
          path: reports/
```

`--strict` promotes warnings to errors so they block the PR. Remove it if you want warnings to pass silently.

---

## Detect schema drift in CI

Use `db drift` as a read-only CI gate that exits `1` when the live database has diverged from the schema registry. No registry files are written.

The `--input-dir` mode is the CI-friendly path — export a snapshot from the database, commit it to an artifact, and compare without needing a live connection inside CI:

**GitHub Actions example (live connection):**

```yaml
name: Schema drift check

on:
  schedule:
    - cron: '0 6 * * *'   # daily at 06:00 UTC
  workflow_dispatch:

jobs:
  drift:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Install Manifesta
        run: |
          curl -sSL https://github.com/rujasy/manifesta/releases/latest/download/manifesta-linux-x64 \
            -o /usr/local/bin/manifesta
          chmod +x /usr/local/bin/manifesta

      - name: Check for schema drift
        env:
          DB_CONNECTION: ${{ secrets.DB_CONNECTION }}
        run: |
          manifesta db drift \
            --provider postgres \
            --connection "$DB_CONNECTION" \
            --output-dir ./reports \
            --strict

      - name: Upload drift report
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: drift-report
          path: reports/drift-report.md
```

**Using pre-exported JSON files (no live connection in CI):**

```bash
# Step 1 (local or in a privileged pipeline): export a live snapshot
manifesta db drift --provider postgres --connection "..." --input-dir ./snapshots

# Step 2 (CI): compare registry against the committed snapshot — no credentials needed
manifesta db drift --input-dir ./snapshots --output-dir ./reports
```

`--strict` promotes "extra tables in DB not in registry" warnings to failures. Remove it if you only care about structural drift in tracked tables.

---

## Keep the registry in sync

When `db drift` reports changes, use `db merge` to pull those changes into the registry. Run `db merge` locally, review the diff, then commit.

**Standard day-2 workflow:**

```bash
# 1. See what has changed
manifesta db drift \
  --provider postgres \
  --connection "$DB_CONNECTION" \
  --output-dir ./reports

# drift-report.md now lists every change. Review it.
cat reports/drift-report.md

# 2. Preview the merge without writing any files
manifesta db merge \
  --provider postgres \
  --connection "$DB_CONNECTION" \
  --dry-run

# 3. Apply the merge
manifesta db merge \
  --provider postgres \
  --connection "$DB_CONNECTION" \
  --output-dir ./reports

# 4. Regenerate docs and validate
manifesta doc db --output-dir ./publish
manifesta validate all --strict
manifesta validate cross

# 5. Commit
git add tables/ publish/ reports/merge-report.md
git commit -m "chore: sync schema registry with production"
```

**Handling new tables:**

New tables discovered in the DB are written to `<root>/tables/` by default. Use `--new-table-dir` to route them elsewhere, or `--skip-new-tables` to ignore them entirely on this run:

```bash
# Write new tables to a staging directory for review before promoting
manifesta db merge --connection "..." --new-table-dir ./tables/pending

# Or skip new tables and only update what is already tracked
manifesta db merge --connection "..." --skip-new-tables
```

**Removing deleted columns and tables:**

By default `db merge` keeps fields that no longer exist in the DB (reporting them as orphan columns) so you can review before deleting. Opt in explicitly when you are ready:

```bash
# Remove columns absent from the DB
manifesta db merge --connection "..." --remove-deleted-columns

# Also delete registry files for tables absent from the DB
manifesta db merge --connection "..." --remove-deleted-columns --remove-deleted-tables
```

**Air-gapped / export-then-merge workflow:**

In environments where CI cannot reach the database directly, export a snapshot first and merge from the files:

```bash
# On a machine with DB access: export a snapshot
manifesta db drift --provider mysql --connection "..." --input-dir ./snapshots
# (or use init db to produce the raw JSON files)

# Anywhere else: merge from the snapshot
manifesta db merge --input-dir ./snapshots
```

---

## Migrate from dbdocs.io

Manifesta's `init dbml` and `doc db --format dbml` form a complete two-way bridge with dbdocs.io.

**Import from dbdocs.io:**

```bash
# 1. Export your DBML from dbdocs.io (File → Export DBML)
# 2. Bootstrap the Manifesta registry
manifesta init dbml --input database.dbml --schema dbo

# 3. Generate documentation locally
manifesta doc db --output-dir ./publish
```

**Publish back to dbdocs.io (optional):**

```bash
# Regenerate the DBML from the Manifesta registry
manifesta doc db --format dbml --output ./publish/database.dbml

# Upload with the dbdocs CLI
dbdocs build ./publish/database.dbml
```

The DBML round-trip preserves computed column expressions, FK kinds, and table notes via inline `[note: "..."]` attributes.
