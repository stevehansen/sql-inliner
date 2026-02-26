# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

sql-inliner is a .NET CLI tool that optimizes SQL Server views by inlining nested views into a single flattened query, optionally stripping unused columns and joins. It parses SQL using Microsoft's ScriptDom (TSql150Parser) and uses the visitor pattern to analyze and transform the AST.

## Build & Test Commands

```bash
# Build
dotnet build src/SqlInliner/SqlInliner.csproj

# Run all tests
dotnet test src/SqlInliner.Tests/SqlInliner.Tests.csproj

# Run a single test by name
dotnet test src/SqlInliner.Tests/SqlInliner.Tests.csproj --filter "InlineSimpleView"

# Run tests in a specific class
dotnet test src/SqlInliner.Tests/SqlInliner.Tests.csproj --filter "FullyQualifiedName~SimpleTests"

# Run the tool locally
dotnet run --project src/SqlInliner/SqlInliner.csproj -- -vp "./path/to/view.sql" --strip-unused-joins
```

## Architecture

The inlining pipeline flows through these core classes:

1. **Program.cs** — CLI entry point using System.CommandLine. Conditionally compiled out (`#if !RELEASELIBRARY`) when building as a library. Loads `InlinerConfig` from `--config` or auto-discovered `sqlinliner.json`, merges CLI overrides (CLI > config > default).

2. **DatabaseConnection** — Wraps `IDbConnection` (Dapper) to query `sys.views` for non-indexed views. Has a parameterless constructor for testing/file-only workflows that accepts mock view definitions via `AddViewDefinition()`. `ParseObjectName(string)` parses `"schema.name"` or `"name"` strings into `SchemaObjectName`.

3. **DatabaseView** — Parses SQL with `TSql150Parser`, extracts the AST tree and a `ReferencesVisitor`. Handles `CREATE OR ALTER VIEW` conversion via regex. Embeds original SQL between `BeginOriginal`/`EndOriginal` markers so previously-inlined views can be re-inlined from their original source.

4. **ReferencesVisitor** (`TSqlFragmentVisitor`) — Walks the AST to collect view references, table references, and column references. Uses `DatabaseConnection.IsView()` to distinguish views from tables. Auto-assigns aliases to unaliased view references.

5. **DatabaseViewInliner** — Core orchestrator. Recursively resolves nested views, optionally strips unused columns/joins based on `InlinerOptions`, then replaces view references with `QueryDerivedTable` (subqueries) via `TableInlineVisitor`. Handles `BinaryQueryExpression` (UNION/EXCEPT/INTERSECT) by stripping columns across all branches.

6. **TableInlineVisitor** (`TSqlFragmentVisitor`) — Performs the actual AST replacement: swaps `NamedTableReference` nodes for `QueryDerivedTable` and removes unused table references from `FromClause` and `QualifiedJoin`.

7. **DerivedTableStripper** — Post-processing step that strips unused columns and LEFT JOINs inside nested `QueryDerivedTable` nodes produced by inlining. Runs after `DatabaseViewInliner.Inline()` and before `DerivedTableFlattener` when `StripUnusedColumns` or `StripUnusedJoins` is enabled. Iterates until no more stripping occurs (handles cascading effects across nesting levels). Skips derived tables with SELECT *, DISTINCT, TOP, GROUP BY, or HAVING. Only strips LEFT OUTER JOINs to `QueryDerivedTable` nodes (not `NamedTableReference` which were already evaluated by the inlining logic with join hints). Uses scope-aware `OuterScopeColumnReferenceCollector` that stops at derived table boundaries.

8. **DerivedTableFlattener** — Post-processing step that flattens derived tables (subqueries) produced by inlining. Runs after `DerivedTableStripper` when `FlattenDerivedTables` is enabled. Walks the AST to find `QueryDerivedTable` nodes within `QuerySpecification` FROM/JOIN trees and replaces eligible ones with their inner table references. Handles single-table and multi-table (JOIN) inner queries, alias collision resolution, column reference rewriting, and WHERE clause merging. Uses scope-aware visitors (`OuterScopeColumnReferenceCollector`) that stop at derived table boundaries to avoid corrupting inner-scope AST nodes.

9. **InlinerResult** — Formats the final output with a metadata comment containing original SQL, referenced views list, and strip statistics.

### Optimize subsystem (`Optimize/` directory)

Conditionally compiled (`#if !RELEASELIBRARY`) and excluded from the library build via `<Compile Remove="Optimize\**" />`.

10. **OptimizeCommand** — System.CommandLine subcommand (`optimize`) with `--connection-string` and `--view-name` options. Accepts a shared `configOption` from Program.cs; connection string can come from CLI or config. Registered in `Program.cs` via `rootCommand.Add(OptimizeCommand.Create(configOption))`.

11. **OptimizeSession** — Orchestrates the 9-step interactive workflow (connect → select → inline → review → deploy → validate → iterate → benchmark → summary). All I/O goes through `IConsoleWizard` for testability.

12. **ConsoleWizard** / **IConsoleWizard** — Abstraction for interactive console I/O (prompts, colored output, tables). Tests use a `MockWizard` with queued answers.

13. **SessionDirectory** — Manages a session folder (`optimize-{name}-{timestamp}/`), saves iteration files, execution plans (`.sqlplan`), a self-contained HTML benchmark report (`benchmark.html`), computes SHA256 hashes for edit detection, and writes a session log.

14. **QueryRunner** — Executes validation queries (COUNT, EXCEPT) and benchmarks (SET STATISTICS TIME/IO/XML via `SqlConnection.InfoMessage`) with configurable command timeouts. Parses per-table IO statistics (`TableIOStats`) and captures actual execution plans as XML. Results are returned in `BenchmarkResult`.

### Validate subsystem (`Optimize/` directory)

Conditionally compiled (`#if !RELEASELIBRARY`) alongside the Optimize subsystem.

16. **ValidateCommand** — System.CommandLine subcommand (`validate`) with `--connection-string`, `--deploy`, `--output-dir`, `--stop-on-error`, `--filter`, and inliner boolean flags. Accepts a shared `configOption` from Program.cs; boolean flags resolved via `Program.ResolveOption` (CLI > config > default). Registered in `Program.cs` via `rootCommand.Add(ValidateCommand.Create(configOption))`.

17. **ValidateSession** — Batch-validates all views: iterates `connection.Views` alphabetically, inlines each with `DatabaseViewInliner`, tracks per-view `ViewValidateResult` (status, elapsed, strip counts, errors/warnings). Supports `--filter` (exact or SQL LIKE `%` wildcard via regex), `--output-dir` (saves inlined SQL), `--stop-on-error`, and `--deploy` (renames to `_Validate`, runs COUNT + EXCEPT via `QueryRunner`, always drops `_Validate` in finally). Prints progress per view and a summary table at end. Status enum: `Pass`, `PassWithWarnings`, `Skipped`, `InlineError`, `ParseError`, `DeployError`, `ValidationFail`, `Exception`.

### Verify subsystem (`Optimize/` directory)

Conditionally compiled (`#if !RELEASELIBRARY`) alongside the Optimize subsystem.

18. **VerifyCommand** — System.CommandLine subcommand (`verify`) with `--connection-string`, `--filter`, `--stop-on-error`, and `--timeout`. Accepts a shared `configOption` from Program.cs; connection string can come from CLI or config. Registered in `Program.cs` via `rootCommand.Add(VerifyCommand.Create(configOption))`.

19. **VerifySession** — Auto-detects deployed inlined views by checking for `BeginOriginal`/`EndOriginal` markers in raw view definitions. For each candidate: extracts the original SQL from markers, deploys it as `[schema].[{name}_Original]`, runs COUNT + EXCEPT comparisons via `QueryRunner`, always drops `_Original` in finally. Skips `_Inlined` companion views when the base view also exists. Supports `--filter` (reuses `ValidateSession.BuildFilterRegex` and `StripBrackets`), `--stop-on-error` (timeouts don't halt), and `--timeout` (configurable query timeout). Status enum: `Pass`, `Skipped`, `DeployError`, `ValidationFail`, `Timeout`, `Exception`.

### Configuration subsystem

15. **InlinerConfig** — Conditionally compiled (`#if !RELEASELIBRARY`). Deserializes `sqlinliner.json` via `System.Text.Json` (camelCase, comments/trailing commas allowed). Properties are all nullable to distinguish "not set" from defaults. `TryLoad(explicitPath)` checks explicit path then auto-discovers `sqlinliner.json` in CWD. `RegisterViews(connection)` reads `.sql` files (paths relative to config directory) and calls `connection.AddViewDefinition()`.

### Key design decisions

- **ScriptDom AST manipulation**: The tool modifies the parsed AST in-place rather than doing string manipulation. SQL is regenerated via `Sql150ScriptGenerator`.
- **Recursive inlining**: `DatabaseViewInliner.Inline()` recurses into each referenced view before replacing it, so deeply nested view chains are fully flattened.
- **Column stripping**: When `StripUnusedColumns` is enabled, columns are removed by index across all branches of a UNION/EXCEPT/INTERSECT to keep them aligned.
- **Join stripping**: Views contributing only 0-1 columns are candidates for removal when `StripUnusedJoins` is enabled.
- **Derived table stripping**: When `StripUnusedColumns` or `StripUnusedJoins` is enabled, `DerivedTableStripper` runs as a post-processing step after inlining (before flattening). It strips unused columns and LEFT OUTER JOINs inside nested `QueryDerivedTable` nodes. Skips derived tables with SELECT *, DISTINCT, TOP, GROUP BY, or HAVING. Only strips LEFT JOINs to `QueryDerivedTable` nodes (not `NamedTableReference` which were already evaluated with join hints). Iterates until no more stripping occurs.
- **Derived table flattening**: When `FlattenDerivedTables` is enabled, `DerivedTableFlattener` runs as a post-processing step after stripping. It replaces eligible `QueryDerivedTable` nodes with their inner `FROM` tree (single table or JOIN tree), rewrites column references, and merges WHERE clauses. Uses `OuterScopeColumnReferenceCollector` that stops at `QueryDerivedTable` boundaries to prevent corrupting shared AST object references.
- **`ParametersToIgnore`**: Maps SQL functions (e.g., DATEADD) to parameter indexes that should be excluded from column reference analysis.

## Testing Patterns

Tests use **NUnit** with **Shouldly** assertions. The standard pattern:
- Create a `DatabaseConnection` with no DB (parameterless constructor)
- Register mock views via `connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VName"), sqlString)`
- Construct `DatabaseViewInliner` with the connection, view SQL, and `InlinerOptions.Recommended()`
- Assert on `inliner.Errors`, `inliner.Result`, and `result.ConvertedSql`

## Build Configurations

- **Debug** — Standard development build
- **Release** — Single-file, trimmed, ReadyToRun publish (for CLI distribution)
- **ReleaseLibrary** — Multi-target library output (net472, netstandard2.0, net8.0, net9.0, net10.0), excludes Program.cs, System.CommandLine, and `Optimize\**`

## Documentation

When adding or changing user-facing features, always update **both** `README.md` and `CLAUDE.md` to reflect the changes. README.md is the public documentation for users; CLAUDE.md is the architecture reference for AI-assisted development. Keep them in sync.
