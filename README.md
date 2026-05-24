# Manifesta OSS

> Schema documentation engine — import, validate, and generate docs from your database schema

Manifesta OSS lets you bootstrap a schema registry from an existing DBML or Prisma file, validate it against a ruleset, and generate Markdown documentation with embedded ERD diagrams — all without a live database connection.

📖 **[Documentation →](docs/documentation.md)**

---

## What it does

| Command | What it gives you |
|---------|------------------|
| `manifesta init dbml` | Bootstrap a schema registry from a `.dbml` file |
| `manifesta init prisma` | Bootstrap a schema registry from a `.prisma` schema |
| `manifesta init db --provider mysql` | Introspect a live MySQL database |
| `manifesta init db --provider postgres` | Introspect a live PostgreSQL database |
| `manifesta doc db` | Generate `database.md` with ERD diagrams and field tables |
| `manifesta doc db --format dbml` | Emit `database.dbml` for upload to dbdocs.io |
| `manifesta validate all` | Run the full per-table validation suite |
| `manifesta validate cross` | Check FK targets, section membership, and cross-entity references |
| `manifesta validate schema` | Export JSON Schema for IDE autocomplete |

---

## Full edition

The full (closed-source) edition of Manifesta adds:

- **Live database introspection** — SQL Server, MySQL, PostgreSQL via `init db`, `db export`, `db merge`, `db drift`
- **API validation and documentation** — OpenAPI 3.x parsing, validation, Swagger UI generation
- **AI description generation** — Auto-generate field and table descriptions via `ai describe`; discover missing FK relationships via `ai infer`
- **Manifest generation** — Produce dependency-ordered manifests for data pipelines
- **Multi-tenant drift analysis** — Compare schema definitions across tenant databases

---

## Quick start

**Import from DBML:**

```bash
manifesta init dbml --input database.dbml
```

This writes one `table.json` per table to `./tables/` and one `section.json` per `TableGroup` to `./document-sections/`.

**Import from Prisma:**

```bash
manifesta init prisma --input ./prisma/schema.prisma
```

SQL types, primary keys, foreign keys, and native type overrides are inferred automatically from the `datasource` block.

**Generate documentation:**

```bash
manifesta doc db --output-dir ./docs
```

Produces `database.md` with a hierarchical table of contents, per-table field listings, and embedded Mermaid ERD diagrams.

**Validate:**

```bash
manifesta validate all --strict
```

Runs the full validation suite — PK/FK rules, reference data consistency, computed field correctness — and writes `validation.json`.

---

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/) or later

No database connection required for OSS commands.

---

## Building from source

```bash
dotnet build
dotnet test
```

The solution builds on Linux, Windows, and macOS.

---

## Documentation

- [Init Commands](docs/commands-init.md) — `init dbml`, `init prisma`
- [Doc Command](docs/commands-doc.md) — `doc db`
- [Validate Commands](docs/commands-validate.md) — `validate schema`, `validate all`, `validate cross`
- [Schema Features](docs/schema-features.md) — table.json format, FK kinds, sections, ERDs

---

## License

MIT — Copyright (c) 2026 RUJASY VOF
