# Manifesta — Dev Commands

← [Back to documentation](./documentation.md)

---

## Table of Contents

- [dev dump-ir](#dev-dump-ir)
- [dev inspect table](#dev-inspect-table)
- [dev graph](#dev-graph)

---

## dev dump-ir

Dumps the full internal representation (IR) — all loaded tables and sections — as a single JSON or YAML document. Useful for debugging configuration resolution or understanding exactly what Manifesta has loaded.

```bash
# Print the full IR to stdout as JSON (default)
manifesta dev dump-ir

# Write YAML to a file
manifesta dev dump-ir --format yaml --output ir.yaml

# Include verbose loading info
manifesta dev dump-ir -v
```

**Flags:**

| Flag | Required | Default | Description |
|------|----------|---------|-------------|
| `--format` | No | `json` | Output format: `json`, `yaml` |
| `--output` | No | stdout | Write to this file path instead of stdout |

**Global flags** (`--verbose`, `--dry-run`, `--config`, `--root`) apply as usual.

**Exit codes:**

| Code | Meaning |
|------|---------|
| 0 | Success |
| 2 | No tables found or schema load error |
| 4 | Root directory not found or configuration error |

---

## dev inspect table

Prints detailed information about a single table definition: fields (with type, nullability, PK/FK markers, and description) and foreign keys.

```bash
# Human-readable summary (default)
manifesta dev inspect table Customer

# Machine-readable JSON (full TableDefinition)
manifesta dev inspect table Customer --format json

# YAML output
manifesta dev inspect table Customer --format yaml
```

**Arguments:**

| Argument | Required | Description |
|----------|----------|-------------|
| `TABLE_NAME` | Yes | Table name, e.g. `Customer` or `dbo.Customer`. Case-insensitive. |

**Flags:**

| Flag | Required | Default | Description |
|------|----------|---------|-------------|
| `--format` | No | `human` | Output format: `human`, `json`, `yaml` |

**Human output includes:**
- Table metadata: description, short description, database types, sections, reference table flag, deprecation status
- Primary key
- Fields table — name, type, nullability, PK and FK markers, description
- Foreign keys table — source field, target table, target field, kind (`physical` / `logical` / `virtual`)

**Exit codes:**

| Code | Meaning |
|------|---------|
| 0 | Table found and printed |
| 2 | Schema load error |
| 4 | Table not found, root not found, or configuration error |

---

## dev graph

Generates a visual or machine-readable representation of the full schema dependency graph — all tables and their foreign key relationships.

```bash
# Mermaid ERD to stdout (default)
manifesta dev graph

# Graphviz DOT to a file
manifesta dev graph --format dot --output schema.dot

# SchemaGraph JSON
manifesta dev graph --format json --output schema-graph.json

# Open interactive graphviz viewer (requires graphviz installed)
manifesta dev graph --interactive

# Write mermaid to file
manifesta dev graph --output schema.mmd
```

**Flags:**

| Flag | Required | Default | Description |
|------|----------|---------|-------------|
| `--format` | No | `mermaid` | Output format: `mermaid`, `dot`, `json` |
| `--interactive` | No | false | Open graph viewer via `dot -Tx11` (requires [graphviz](https://graphviz.org)). Writes a temp DOT file and launches the viewer. Exits 4 if graphviz is not installed. |
| `--output` | No | stdout | Write to this file path instead of stdout. Ignored when `--interactive` is set. |

**Format details:**

| Format | Output | Use case |
|--------|--------|----------|
| `mermaid` | `erDiagram` Mermaid block with all tables and FK relationships | Paste into Markdown or a Mermaid renderer |
| `dot` | Graphviz DOT language directed graph | Feed to `dot -Tsvg`, `dot -Tpng`, or `xdot` |
| `json` | `SchemaGraph` JSON with `nodes` (full TableDefinition records) and `edges` (FK edges) | Tooling integration, custom graph processing |

**Rendering Mermaid locally:**

```bash
# Install the Mermaid CLI
npm install -g @mermaid-js/mermaid-cli

# Generate a PNG
manifesta dev graph | mmdc -i - -o schema.png
```

**Installing graphviz for `--interactive`:**

```bash
# macOS
brew install graphviz

# Ubuntu / Debian
sudo apt install graphviz

# Windows (winget)
winget install graphviz
```

**Exit codes:**

| Code | Meaning |
|------|---------|
| 0 | Graph generated successfully |
| 2 | Schema load error |
| 4 | Root not found, configuration error, or graphviz not found (`--interactive`) |

---

> **Note:** `dev inspect api` is available in the enterprise edition only. The OSS edition does not include API (OpenAPI/Swagger) support.
