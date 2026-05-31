# Manifesta — Configuration Reference

← [Back to documentation](./documentation.md)

---

`manifesta.config.json` is the optional project configuration file. All settings in it can be overridden by CLI flags. Manifesta looks for it in the current working directory by default; pass `--config PATH` to use a different location.

Generate the full JSON Schema for IDE autocomplete and validation:

```bash
manifesta validate schema config --output-dir ./schemas
```

Add to `.vscode/settings.json` to enable real-time validation in VS Code:

```json
{
  "json.schemas": [
    {
      "fileMatch": ["manifesta.config.json"],
      "url": "./schemas/manifesta-config-schema.json"
    }
  ]
}
```

---

## Table of Contents

- [paths](#paths)
- [output](#output)
- [adapters](#adapters)
- [naming](#naming)
- [tenants](#tenants)
- [Full example](#full-example)

---

## paths

Controls where Manifesta looks for and writes registry files.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `paths.tables` | string | `"./tables"` | Directory containing `table.json` files |
| `paths.sections` | string | `"./document-sections"` | Directory containing section `*.json` files |
| `paths.root` | string | `"."` | Project root used when no other path is specified. Equivalent to `--root` on the CLI. |

```json
{
  "paths": {
    "root": "./schema",
    "tables": "./schema/tables",
    "sections": "./schema/document-sections"
  }
}
```

---

## output

Controls documentation generation behaviour for `manifesta doc db`.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `output.path` | string | — | Override the default output file path. When set, `--output-dir` is ignored unless also supplied on the CLI. |
| `output.type` | string | `"markdown"` | Output format: `"markdown"` or `"dbml"`. Equivalent to `--format` on the CLI. |
| `output.dialect` | string | — | Markdown rendering dialect. Set to `"AzureDevOps"` to use `:::mermaid` / `:::` fences instead of ` ```mermaid ``` ` for Azure DevOps wiki rendering. |
| `output.sectionOrder` | string[] | — | Explicit section render order by section name. Sections not listed are appended alphabetically after the listed ones. |

```json
{
  "output": {
    "path": "./publish/database.md",
    "dialect": "AzureDevOps",
    "sectionOrder": ["Core", "Orders", "Billing", "Reference"]
  }
}
```

---

## adapters

Provider-specific adapter configuration. Used by `init db` and the full edition's database commands to supply default connection settings per provider.

Run `manifesta validate schema config --output-dir ./schemas` to get the full JSON Schema for the `adapters` block — the available sub-properties depend on the installed providers.

```json
{
  "adapters": {
    "mysql": {
      "connectionString": "Server=localhost;Database=mydb;Uid=root;Pwd=secret;"
    },
    "postgres": {
      "connectionString": "Host=localhost;Database=mydb;Username=postgres;Password=secret;"
    }
  }
}
```

Connection strings in config are loaded at runtime and never written back. Use environment variables for secrets in CI.

---

## naming

Controls how table and field names are normalised when writing registry files.

Run `manifesta validate schema config --output-dir ./schemas` for the full JSON Schema for the `naming` block.

```json
{
  "naming": {
    "defaultSchema": "dbo"
  }
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `naming.defaultSchema` | string | — | Schema prefix applied to unqualified table names during import. Equivalent to `--schema` on init commands. |

---

## tenants

Declares the multi-tenant topology for the full edition's `db tenant-drift` command. Omit this section entirely if you are not using multi-tenant drift detection.

> **Full edition only.** The `tenants` block is parsed and validated by `Manifesta.Core` (OSS), but the `db tenant-drift` command that reads it requires the full edition. OSS users can still declare the block and get IDE autocomplete and config validation via `manifesta validate schema config`.

### tenants.types

A dictionary of named database-class definitions. Each key is an arbitrary label you choose (e.g. `"central"`, `"partner"`).

| Property | Type | Required | Default | Description |
|----------|------|----------|---------|-------------|
| `root` | boolean | No | `false` | When `true`, databases of this type are the root of the topology tree. Exactly one type in the topology must have `"root": true`. |
| `allowedParents` | string[] | No | `[]` | Type names whose databases may be the direct parent of a database of this type. Leave empty on the root type. |
| `requiredSections` | string[] | No | `[]` | Section (module) names that every database of this type must have installed. Validated by `validate schema config`. |

### tenants.databases

A dictionary of named database instances. Each key is a logical name you choose (e.g. `"central-db"`, `"partner-eu"`).

Each entry must specify exactly one **source** — the data used when `db tenant-drift` checks this database for schema drift. The three sources are mutually exclusive:

| Property | Type | Required | Default | Description |
|----------|------|----------|---------|-------------|
| `type` | string | Yes | — | One of the keys declared in `tenants.types`. |
| `connection` | string | One of three | — | Connection string for live introspection. Use environment variable substitution in CI rather than committing secrets. SQL Server requires the full edition. |
| `inputDir` | string | One of three | — | Directory of pre-exported JSON files (output of `db export`). Enables air-gapped drift detection without a live database connection. |
| `ddl` | string | One of three | — | Comma-separated paths to DDL SQL files (`CREATE TABLE` statements). Enables offline drift from schema files. SQL Server DDL accepted in the community edition (text-only parsing, no live connection). |
| `parent` | string | No | — | Logical name of the parent database in the topology. Omit on the root database. |
| `sections` | string[] | No | `[]` | Section (module) names installed on this database. Only tables belonging to these sections are checked for drift. |

```json
{
  "tenants": {
    "types": {
      "central": { "root": true },
      "partner": { "allowedParents": ["central"] }
    },
    "databases": {
      "central-db": {
        "type": "central",
        "connection": "Server=central.db;Database=Main;...",
        "sections": ["Core", "Billing"]
      },
      "partner-eu": {
        "type": "partner",
        "parent": "central-db",
        "connection": "Server=eu.db;Database=PartnerEU;...",
        "sections": ["Core"]
      },
      "partner-us-air-gapped": {
        "type": "partner",
        "parent": "central-db",
        "inputDir": "./snapshots/partner-us",
        "sections": ["Core"]
      },
      "partner-offline": {
        "type": "partner",
        "parent": "central-db",
        "ddl": "./ddl/partner-offline.sql",
        "sections": ["Core"]
      }
    }
  }
}
```

> **Tip:** Mark sections as modules in your `section.json` files (`"isModule": true`) so tooling can distinguish installable modules from structural groupings used only for documentation.

---

## Full example

```json
{
  "paths": {
    "tables": "./tables",
    "sections": "./document-sections"
  },
  "output": {
    "path": "./publish/database.md",
    "sectionOrder": ["Core", "Orders", "Billing", "Reference"]
  },
  "naming": {
    "defaultSchema": "dbo"
  }
}
```

This is the recommended minimal config for a project that uses default directory names and wants an explicit section order. Everything not listed here is resolved to its default at runtime.
