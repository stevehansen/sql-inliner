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

1. **Program.cs** — CLI entry point using System.CommandLine. Conditionally compiled out (`#if !RELEASELIBRARY`) when building as a library.

2. **DatabaseConnection** — Wraps `IDbConnection` (Dapper) to query `sys.views` for non-indexed views. Has a parameterless constructor for testing that accepts mock view definitions via `AddViewDefinition()`.

3. **DatabaseView** — Parses SQL with `TSql150Parser`, extracts the AST tree and a `ReferencesVisitor`. Handles `CREATE OR ALTER VIEW` conversion via regex. Embeds original SQL between `BeginOriginal`/`EndOriginal` markers so previously-inlined views can be re-inlined from their original source.

4. **ReferencesVisitor** (`TSqlFragmentVisitor`) — Walks the AST to collect view references, table references, and column references. Uses `DatabaseConnection.IsView()` to distinguish views from tables. Auto-assigns aliases to unaliased view references.

5. **DatabaseViewInliner** — Core orchestrator. Recursively resolves nested views, optionally strips unused columns/joins based on `InlinerOptions`, then replaces view references with `QueryDerivedTable` (subqueries) via `TableInlineVisitor`. Handles `BinaryQueryExpression` (UNION/EXCEPT/INTERSECT) by stripping columns across all branches.

6. **TableInlineVisitor** (`TSqlFragmentVisitor`) — Performs the actual AST replacement: swaps `NamedTableReference` nodes for `QueryDerivedTable` and removes unused table references from `FromClause` and `QualifiedJoin`.

7. **InlinerResult** — Formats the final output with a metadata comment containing original SQL, referenced views list, and strip statistics.

### Key design decisions

- **ScriptDom AST manipulation**: The tool modifies the parsed AST in-place rather than doing string manipulation. SQL is regenerated via `Sql150ScriptGenerator`.
- **Recursive inlining**: `DatabaseViewInliner.Inline()` recurses into each referenced view before replacing it, so deeply nested view chains are fully flattened.
- **Column stripping**: When `StripUnusedColumns` is enabled, columns are removed by index across all branches of a UNION/EXCEPT/INTERSECT to keep them aligned.
- **Join stripping**: Views contributing only 0-1 columns are candidates for removal when `StripUnusedJoins` is enabled.
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
- **ReleaseLibrary** — Multi-target library output (net472, netstandard2.0, netcoreapp3.1, net6.0, net8.0), excludes Program.cs and System.CommandLine
