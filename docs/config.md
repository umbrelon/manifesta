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
