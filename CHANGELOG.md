# Changelog

All notable changes to Manifesta OSS are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows [Semantic Versioning](https://semver.org/).

---

## [Unreleased]

### Added
- `init dbml` — bootstrap a schema registry from a DBML file; extracts tables, columns, PKs, FKs, and `TableGroup` sections
- `init prisma` — bootstrap a schema registry from a Prisma schema; infers SQL types, native type overrides, PKs, FKs, and relation mode from the `datasource` block
- `doc db` — generate `database.md` with hierarchical TOC, field tables, and embedded Mermaid ERD diagrams
- `doc db --format dbml` — emit `database.dbml` for direct upload to dbdocs.io; round-trips cleanly through `init dbml`
- `validate schema` — export Draft 7 JSON Schema for `table.json`, `section.json`, and `manifesta.config.json`; enables IDE autocomplete and real-time validation
- `validate all` — run the full per-table validation suite with cross-table context; writes `validation.json`
- `validate cross` — check FK target existence and section membership across the full schema registry; writes `cross-validation.json`
- Three FK kinds: `physical`, `logical`, `virtual` — distinct merge and ERD behaviour
- Reference table support — mark tables as lookup tables and embed row data directly in `table.json`
- Computed field support — `isComputed`, `computedExpression`, `isPersisted` properties with validation rules and documentation rendering
- Deprecation and sensitivity classification fields on tables and columns
- ERD diagrams embedded per section, with configurable field verbosity and FK kind filtering
- Azure DevOps Markdown dialect (`:::mermaid` fences)
- Dry-run mode (`--dry-run`) for all write operations
- Global flags: `--config`, `--root`, `--verbose`, `--quiet`, `--warn-only`, `--dry-run`, `--force`, `--format`, `--pre-hook`, `--post-hook`

---

[Unreleased]: https://github.com/arta-solutions/manifesta-oss/compare/HEAD
