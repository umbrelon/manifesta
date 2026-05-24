# Contributing to Manifesta OSS

Thank you for your interest in contributing. This document covers the development setup, conventions, and pull request process.

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- A C# IDE — Visual Studio 2026, Rider, or VS Code with the C# Dev Kit extension

---

## Building

```bash
dotnet build
```

The solution targets .NET 10 and builds on Linux, Windows, and macOS.

---

## Testing

```bash
dotnet test
```

All tests are pure unit tests — no database connection or external service required. The test suite covers:

- IR model and pipeline interfaces (`Manifesta.Core.Tests`)
- DBML parser and generator (`Manifesta.Db.Tests`, `Manifesta.Doc.Tests`)
- Prisma parser (`Manifesta.Db.Tests`)
- Table definition serializer (`Manifesta.Db.Tests`)
- Documentation generator (`Manifesta.Doc.Tests`)
- Validation rules (`Manifesta.Core.Tests`)

---

## Code conventions

**Immutable IR.** All IR types in `Manifesta.Core/IR/` are `sealed record` with `init`-only setters. Never add mutable state to IR types.

**No CLI dependency in libraries.** Projects under `src/` (except the CLI itself) must not reference `System.CommandLine` or any other CLI framework. The CLI references libraries; libraries never reference the CLI.

**Case-insensitive identifier collections.** Use `TableNames.NewSet()`, `TableNames.NewDictionary<TValue>()`, and `TableNames.Comparer` from `Manifesta.Core` instead of writing `StringComparer.OrdinalIgnoreCase` at call sites.

**Atomic writes.** Any code that writes output files must use `AtomicWriter` (write to `.tmp_<guid>`, then rename). Never write directly to the target path.

**Deterministic output.** Loaders return results sorted by file path. Generators must produce identical output for identical input.

**Tests required.** Every new class or method needs test coverage. Do not open a PR without tests.

**Format check.** Run the format check before pushing:

```bash
dotnet format --verify-no-changes --severity warn
```

---

## Project structure

```
src/
  Manifesta.Core/         IR model, pipeline interfaces, validators, shared utilities
  Manifesta.Doc/          Documentation generators (Markdown, DBML, ERD)
  Manifesta.Cli.Oss/      OSS CLI entry point (init, doc, validate commands)
tests/
  Manifesta.Core.Tests/
  Manifesta.Doc.Tests/
  Manifesta.Cli.Oss.Tests/
```

---

## Pull request process

1. Fork the repository and create a branch from `main`.
2. Make your changes with tests.
3. Run `dotnet test` and `dotnet format --verify-no-changes --severity warn` — both must pass.
4. Open a PR against `main` with a clear description of what changed and why.
5. One maintainer review is required before merge.

---

## Reporting bugs

Open a GitHub issue with:
- The command you ran and its flags
- The content of your `table.json` or input file (anonymise if needed)
- The actual output and expected output
- Your OS and .NET version (`dotnet --version`)

---

## Security vulnerabilities

See [SECURITY.md](SECURITY.md).
