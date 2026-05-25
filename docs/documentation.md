# Manifesta OSS — Documentation

> Schema documentation engine — import, validate, and generate docs from your database schema

---

## Table of Contents

- [Concepts](#concepts)
- [Minimal Working Example](#minimal-working-example)
- [Example Registry](./example-registry.md) — complete two-table registry with section, ERD, and generated output
- [Common Workflows](./workflows.md) — first-time setup, CI validation, docs regeneration, dbdocs.io migration
- [Commands — Init](./commands-init.md)
  - [Init DBML](./commands-init.md#init-dbml)
  - [Init Prisma](./commands-init.md#init-prisma)
- [Commands — Documentation](./commands-doc.md)
  - [Doc DB](./commands-doc.md#doc-db)
- [Commands — Validation](./commands-validate.md)
  - [Validate Schema](./commands-validate.md#validate-schema)
  - [Validate All](./commands-validate.md#validate-all)
  - [Validate Cross](./commands-validate.md#validate-cross)
- [Schema Features](./schema-features.md)
  - [Table Descriptions](./schema-features.md#table-descriptions)
  - [Reference Tables](./schema-features.md#reference-tables)
  - [Computed Fields](./schema-features.md#computed-fields)
  - [Foreign Keys](./schema-features.md#configuring-foreign-keys-in-tablejson)
  - [ERD Diagrams](./schema-features.md#configuring-erd-diagrams-in-sectionjson)
- [Configuration Reference](./config.md) — all `manifesta.config.json` properties
- [table.json / section.json Reference](./table-json-reference.md) — complete field-level property reference

---

## Concepts

### Schema Registry

The JSON representation of a database schema — tables with their columns, types, constraints, and relationships. This is the canonical form that all downstream operations work from. Each table is stored as a separate `table.json` file; sections are stored as `section.json` files that group related tables for documentation purposes.

### Source of truth

Manifesta treats the schema registry files as the source of truth for metadata: descriptions, foreign key kinds, section membership, and reference table data are all repo-sovereign and are never overwritten by automated tooling unless explicitly requested.

### Provider

A database-specific implementation used during import. Select the provider with `--provider sqlserver|mysql|postgres` (default: `sqlserver`). The full edition adds live database introspection; in the OSS edition, the provider affects type mapping when importing from Prisma schemas.

---

## Minimal Working Example

**Import a DBML file:**

```bash
manifesta init dbml --input database.dbml --schema dbo
```

Produces:
```
tables/
  dbo.Customer.json
  dbo.Order.json
document-sections/
  Core.json
```

**Generate documentation:**

```bash
manifesta doc db --output-dir ./publish
```

Produces `./publish/database.md` — a Markdown file with a hierarchical table of contents, per-table field listings, FK annotations, and embedded Mermaid ERD diagrams.

**Validate:**

```bash
manifesta validate all --output-dir ./reports
```

Produces `./reports/validation.json` — a structured JSON report listing every validation issue found across the schema registry.

---

## Usage

```
manifesta init dbml --input <file> [--output-dir <dir>] [--schema <prefix>] [--overwrite]
manifesta init prisma --input <file> [--output-dir <dir>] [--provider sqlserver|mysql|postgres]
manifesta doc db [--format markdown|dbml] [--output <file>] [--output-dir <dir>]
manifesta validate schema <type> --output-dir <dir>
manifesta validate all [--strict] [--output-dir <dir>]
manifesta validate cross [--output <file>] [--output-dir <dir>]
manifesta --version
manifesta --help
```

### Global Flags

| Flag | Default | Description |
|------|---------|-------------|
| `--config PATH` | `manifesta.config.json` | Configuration file path |
| `--root PATH` | from config | Root directory for schema registry scanning |
| `--verbose` / `-v` | false | Debug-level logging |
| `--quiet` / `-q` | false | Suppress non-error output |
| `--warn-only` | false | Treat errors as warnings (exit 0) |
| `--dry-run` | false | Preview without writing any files |
| `--force` | false | Bypass idempotency checks |
| `--pre-hook CMD` | — | Shell command to run before execution |
| `--post-hook CMD` | — | Shell command to run after execution |
| `--help` / `-h` | — | Show help and exit |
