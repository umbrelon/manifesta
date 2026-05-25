# Manifesta — Validation Commands

← [Back to documentation](./documentation.md)

---

## Table of Contents

- [Validate Schema](#validate-schema)
- [Validate All](#validate-all)
- [Validate Cross](#validate-cross)

---

## Validate Schema

Extracts JSON Schema (Draft 7) definitions for your Manifesta definition files. These schemas can be used in IDEs for validation and autocomplete when editing table, section, and config files.

**Extract table definition schema:**

```bash
manifesta validate schema table \
  --output-dir ./schemas
```

Produces `./schemas/table-schema.json` — a JSON Schema that describes the structure of `table.json` files.

**Extract section definition schema:**

```bash
manifesta validate schema section \
  --output-dir ./schemas
```

Produces `./schemas/section-schema.json` — a JSON Schema for `document-sections/*.json` files.

**Extract config schema:**

```bash
manifesta validate schema config \
  --output-dir ./schemas
```

Produces `./schemas/manifesta-config-schema.json` — a JSON Schema for `manifesta.config.json` itself, covering `paths`, `output`, `adapters`, and `naming`.

**Using schemas in VS Code:**

Add to your `.vscode/settings.json`:

```json
{
  "json.schemas": [
    {
      "fileMatch": ["**/table.json"],
      "url": "./schemas/table-schema.json"
    },
    {
      "fileMatch": ["document-sections/*.json"],
      "url": "./schemas/section-schema.json"
    },
    {
      "fileMatch": ["manifesta.config.json"],
      "url": "./schemas/manifesta-config-schema.json"
    }
  ]
}
```

Once configured, VS Code will provide:
- Real-time validation of your definition files
- Autocomplete suggestions for required and optional properties
- Detailed inline documentation for each property
- Error highlighting for invalid property types or missing required fields

---

## Validate All

Runs the full per-table validation suite across all table definitions in the schema registry and writes a structured JSON report. The validator runs with full cross-table context — section membership and table `labelField` references are checked as part of this pass.

```bash
# Validate all tables, write validation.json to current directory
manifesta validate all

# Treat warnings as errors (exit 1)
manifesta validate all --strict

# Write report to a specific directory
manifesta validate all --output-dir ./reports
```

**Flags:**

| Flag | Default | Description |
|------|---------|-------------|
| `--strict` | false | Exit 1 when warnings are present, not just errors |
| `--output-dir` | `.` | Directory for the `validation.json` report |

**Output:** `validation.json` — a structured JSON file with a summary block and an array of all issues.

```json
{
  "generatedAt": "2026-05-24T10:30:00Z",
  "summary": {
    "tablesScanned": 12,
    "sectionsScanned": 3,
    "errors": 1,
    "warnings": 1,
    "hasErrors": true,
    "hasWarnings": true
  },
  "issues": [
    {
      "severity": "Error",
      "code": "PK-NULLABLE",
      "message": "Primary key field 'Id' cannot be nullable",
      "file": "tables/dbo.Order.json"
    },
    {
      "severity": "Warning",
      "code": "SENS-PII-NO-DESCRIPTION",
      "message": "PII field 'Email' has no description",
      "file": "tables/dbo.Customer.json"
    }
  ]
}
```

**Validation rules (per-table):**

| Code | Severity | Rule |
|------|----------|------|
| `NAME-MISSING` | Error | Table has no `name` |
| `PK-MISSING` | Warning | No `primaryKey` defined |
| `PK-NULLABLE` | Error | A PK field is marked nullable |
| `PK-FIELD-MISSING` | Error | A `primaryKey` entry does not exist in `fields` |
| `FK-SOURCE-MISSING` | Error | FK `sourceField` does not exist in `fields` |
| `FK-CASCADE-NON-PHYSICAL` | Warning | `cascadeDelete: true` on a non-physical FK |
| `MATCH-COLUMN-NULLABLE` | Error | A match column (`isMatchColumn: true`) is nullable |
| `COMPUTED-NO-EXPRESSION` | Error | A field with `isComputed: true` has no `computedExpression` |
| `COMPUTED-IN-SET` | Warning | A column set includes a computed column |
| `CALC-MATCH-COLUMN` | Warning | A computed field is marked `isMatchColumn` |
| `CALC-PK-NOT-PERSISTED` | Error | A non-persisted computed field appears in `primaryKey` |
| `CALC-FK-SOURCE` | Warning | A computed field is the `sourceField` of a FK |
| `DATA-UNKNOWN-COLUMN` | Error | A row in `data` contains a key not in `fields` |
| `DATA-MISSING-COLUMN` | Error | A row in `data` is missing a field from `fields` |
| `DATA-DUPLICATE-PK` | Error | Two rows in `data` share the same primary key |
| `DATA-UNORDERED` | Warning | Rows in `data` are not sorted by primary key |
| `DEPR-PK` | Warning | A deprecated field appears in `primaryKey` |
| `DEPR-FK-SOURCE` | Warning | A deprecated field is the `sourceField` of a FK |
| `SENS-INVALID-VALUE` | Error | `sensitivity` has an unrecognised value |
| `SENS-PII-NO-DESCRIPTION` | Warning | A PII-sensitivity field has no description |

**Exit codes:**

| Code | Meaning |
|------|---------|
| `0` | No errors (warnings present but `--strict` not set) |
| `1` | Errors found, or warnings with `--strict` |
| `2` | Fatal load error (malformed JSON, missing root directory) |
| `4` | Configuration error |

---

## Validate Cross

Validates cross-entity references between tables and sections. Unlike `validate all` (which validates each table individually), `validate cross` checks relationships spanning multiple entities: FK target existence and section membership consistency.

```bash
# Check cross-entity references, write cross-validation.json
manifesta validate cross

# Write to a specific file
manifesta validate cross --output ./reports/cross-validation.json

# Write to a specific directory
manifesta validate cross --output-dir ./reports
```

**Flags:**

| Flag | Default | Description |
|------|---------|-------------|
| `--output` | — | Full path for `cross-validation.json` (overrides `--output-dir`) |
| `--output-dir` | `.` | Output directory (default filename: `cross-validation.json`) |

**Checks performed:**

| Code | Severity | Rule |
|------|----------|------|
| `FK-TARGET-MISSING` | Error | A FK's `targetTable` does not exist in the schema registry |
| `TABLE-LABEL-FIELD-MISSING` | Warning | A table's `labelField` does not exist in its own fields |
| `SECTION-UNDEFINED` | Warning | A table references a section name not defined in any section file |
| `SECTION-TABLE-UNDEFINED` | Warning | A section lists a table name not present in the schema registry |

**Output:** `cross-validation.json` — same structure as `validation.json`.

```json
{
  "generatedAt": "2026-05-24T10:30:00Z",
  "summary": {
    "tablesScanned": 12,
    "sectionsScanned": 3,
    "errors": 1,
    "warnings": 1,
    "hasErrors": true,
    "hasWarnings": false
  },
  "issues": [
    {
      "severity": "Error",
      "code": "FK-TARGET-MISSING",
      "message": "dbo.Order.StatusId references dbo.OrderStatus, which does not exist in the schema registry",
      "file": "tables/dbo.Order.json"
    },
    {
      "severity": "Warning",
      "code": "SECTION-TABLE-UNDEFINED",
      "message": "Section 'Billing' references dbo.Invoice, which is not present in the schema registry",
      "file": "document-sections/Billing.json"
    }
  ]
}

**When to use `validate all` vs `validate cross`:**

| Command | What it validates |
|---------|------------------|
| `validate all` | Per-table rules (PK, FK source fields, data rows, computed columns, etc.) applied to every table |
| `validate cross` | Cross-entity references (FK target existence, section/table membership) |

Running both gives complete coverage.

**Exit codes:**

| Code | Meaning |
|------|---------|
| `0` | No errors (warnings may be present) |
| `1` | Errors found |
| `2` | Fatal load error |
| `4` | Configuration error |
