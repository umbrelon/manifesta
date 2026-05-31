# Changelog

All notable changes to Manifesta OSS are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows [Semantic Versioning](https://semver.org/).

---

## [Unreleased]

### Added
- `db export` — introspect a live MySQL, PostgreSQL, or SQLite database and write one `table.json` file per table (and optionally per view) into an output directory; output is in the exact format consumed by `db drift --input-dir` and `db merge --input-dir`, completing the air-gapped workflow without requiring `init db`
- SQLite support in `db drift` and `db merge` (`--provider sqlite`); previously only `init db` accepted the `sqlite` provider
- `db drift --ddl` — compare the schema registry against SQL DDL files without a live database connection; supports all four dialects including SQL Server T-SQL (the only `db drift` mode that accepts `--provider sqlserver` in OSS); accepts comma-separated file paths; parse errors block the comparison before the diff runs to prevent misleading reports; `--ddl` is a third mutually-exclusive mode alongside the existing `--connection` and `--input-dir` modes
- `init sql` — bootstrap a schema registry from SQL DDL files (`CREATE TABLE` statements); supports MySQL, PostgreSQL, SQLite, and SQL Server T-SQL dialects; works on clean migration scripts and dump files (`mysqldump --no-data`, `pg_dump --schema-only`); the only OSS `init` command that accepts `--provider sqlserver`, because it performs pure text parsing with no live database connection; `--recursive` descends into subdirectories; `--pattern` accepts both plain filename wildcards (e.g. `*_up.sql`, controlled by `--recursive`) and full path globs (e.g. `2024/**/*.sql`, `**/create_*.sql`) that match against the file path relative to the input directory
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
