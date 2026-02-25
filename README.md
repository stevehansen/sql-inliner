# sql-inliner

[![NuGet (Tool)](https://img.shields.io/nuget/v/SqlInliner?label=SqlInliner&logo=nuget)](https://www.nuget.org/packages/SqlInliner)
[![NuGet (Library)](https://img.shields.io/nuget/v/SqlInliner.Library?label=SqlInliner.Library&logo=nuget)](https://www.nuget.org/packages/SqlInliner.Library)
[![CI](https://github.com/stevehansen/sql-inliner/actions/workflows/ci.yml/badge.svg)](https://github.com/stevehansen/sql-inliner/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A CLI tool and .NET library that optimizes SQL Server views by inlining nested views into a single flattened query. It can strip unused columns and joins to produce a leaner, faster view.

## Why use sql-inliner?

While SQL Server can handle nested views, stacking views on top of each other leads to significant performance problems. As nesting grows, developers lose sight of the extra joins and columns being pulled in. These inefficiencies accumulate: larger intermediate datasets, wasted memory, and longer execution times.

sql-inliner solves this by:

- **Flattening** nested view references into a single query (recursively, no matter how deep)
- **Stripping unused columns** so only the data the outer view actually needs is selected
- **Stripping unused joins** so tables that contribute nothing to the result are removed entirely
- **Preserving the original SQL** inside the output so you can always restore or re-inline later

### Before and after

Given two nested views where the outer view only uses a subset of columns:

```sql
-- Inner view: selects many columns and joins several tables
CREATE VIEW dbo.VPerson AS
SELECT p.Id, p.Name, p.Email, a.City, a.Street, a.Zip
FROM dbo.Person p
LEFT JOIN dbo.Address a ON a.PersonId = p.Id

-- Outer view: only uses Id and Name
CREATE VIEW dbo.VPersonNames AS
SELECT v.Id, v.Name
FROM dbo.VPerson v
```

After inlining with `--strip-unused-columns --strip-unused-joins`:

```sql
CREATE OR ALTER VIEW [dbo].[VPersonNames] AS
SELECT [v].[Id], [v].[Name]
FROM (
    SELECT [p].[Id], [p].[Name]
    FROM [dbo].[Person] [p]
    -- Address join removed: contributed no columns to the result
) [v]
```

The nested view reference is replaced with a subquery, the unused `Email`/`City`/`Street`/`Zip` columns are stripped, and the `Address` join is removed entirely because none of its columns are needed.

> **Always verify the generated code manually before deploying to a production database.**

## Prerequisites

The CLI tool is distributed as a [.NET global tool](https://learn.microsoft.com/dotnet/core/tools/global-tools) and requires the [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or later) to be installed. You can verify your installation by running `dotnet --version`.

## Installation

### CLI tool

```bash
dotnet tool install --global sqlinliner
```

This registers the `sqlinliner` command globally so it can be used from any directory. Run `sqlinliner --help` to see all available options, or `sqlinliner --version` to check the installed version.

### Library (NuGet)

If you want to integrate view inlining into your own application or build pipeline, install the library package instead:

```bash
dotnet add package SqlInliner.Library
```

The library targets `net472`, `netstandard2.0`, `net8.0`, `net9.0`, and `net10.0`. See [Library usage](#library-usage) below for API examples.

## CLI reference

```
sqlinliner [options]
```

| Option | Alias | Type | Default | Description |
|---|---|---|---|---|
| `--connection-string` | `-cs` | string | — | Connection string to the SQL Server database |
| `--view-name` | `-vn` | string | — | Fully qualified name of the view to inline (e.g. `dbo.MyView`) |
| `--view-path` | `-vp` | path | — | Path to a `.sql` file containing a `CREATE VIEW` statement |
| `--strip-unused-columns` | `-suc` | bool | `true` | Remove columns from nested views that the outer view does not reference |
| `--strip-unused-joins` | `-suj` | bool | `false` | Remove joins from nested views whose tables contribute no columns to the result |
| `--aggressive-join-stripping` | — | bool | `false` | Exclude join-condition column references from the usage count (can change results for INNER JOINs — see below) |
| `--generate-create-or-alter` | — | bool | `true` | Wrap the output in a `CREATE OR ALTER VIEW` statement |
| `--output-path` | `-op` | path | — | Write the resulting SQL to a file instead of the console |
| `--log-path` | `-lp` | path | — | Write warnings, errors, and timing info to a file |

At least one of `--view-name` or `--view-path` is required. When both are supplied, `--view-path` provides the main view definition while `--view-name` (with `--connection-string`) is used to fetch any nested views referenced inside it from the database.

The tool writes the inlined SQL to stdout (or to `--output-path`) and always exits with code `0`. Check the `-- Errors` section in the output metadata comment or the `--log-path` file to detect problems.

## Examples

### Inline a view from a database

```bash
sqlinliner \
  -cs "Server=.;Database=Test;Integrated Security=true" \
  -vn "dbo.VHeavy" \
  --strip-unused-joins
```

Fetches the definition of `dbo.VHeavy`, recursively inlines every nested non-indexed view, strips unused columns (on by default) and unused joins.

### SQL Server authentication

```bash
sqlinliner \
  -cs "Server=hostname.domain.net;Database=mydb;User=myuser;Password='secret'" \
  -vn "dbo.SlowView" \
  --strip-unused-joins
```

### Inline a view from a local file

```bash
sqlinliner -vp "./views/MyView.sql" --strip-unused-joins
```

Uses the exact contents of `MyView.sql`. If a connection string is also supplied, any views referenced *within* `MyView.sql` are fetched from the database.

### Disable the CREATE OR ALTER wrapper

```bash
sqlinliner -vp "./views/MyView.sql" --generate-create-or-alter false
```

Outputs only the inlined `SELECT` statement — useful when embedding the result inside a larger script or when comparing different versions.

### Combine a local file with database lookups

```bash
sqlinliner \
  -vp "./views/VHeavy.sql" \
  -cs "Server=.;Database=Test;Integrated Security=true" \
  --strip-unused-joins
```

The main view definition comes from `VHeavy.sql`, but any nested views it references (e.g. `dbo.VInner`) are fetched from the database via the connection string. This is useful when iterating on the outer view locally while the inner views live in the database.

### Write output and logs to files

```bash
sqlinliner \
  -cs "Server=.;Database=Test;Integrated Security=true" \
  -vn "dbo.VHeavy" \
  --strip-unused-joins \
  -op "./output/VHeavy_inlined.sql" \
  -lp "./output/VHeavy.log"
```

## Feature details

### Column stripping

Enabled by default (`--strip-unused-columns true`). When a nested view selects columns that the outer view never references, those columns are removed from the inlined subquery. This reduces the amount of data SQL Server has to process.

For views that use `UNION`, `EXCEPT`, or `INTERSECT`, columns are removed by position across all branches to keep the query valid.

### Join stripping

Disabled by default; enable with `--strip-unused-joins`. After column stripping, some tables in the nested view may no longer contribute any columns to the result. Join stripping removes those tables entirely, eliminating unnecessary I/O.

A join is considered safe to remove when:

- The table contributes zero columns to the outer query (or at most one column that is only used in its own join condition).
- For `LEFT JOIN`: the join is marked `@join:unique` (see [Join hints](#join-hints) below), guaranteeing at most one match per row and no row duplication.
- For `INNER JOIN`: the join is marked both `@join:unique` and `@join:required`, guaranteeing exactly one match per row (no filtering, no duplication).

Without these hints, the tool cannot be certain that removing a join won't change the result set, so it leaves the join in place.

### Aggressive join stripping

When `--aggressive-join-stripping` is enabled, column references that appear *only* in a table's own `ON` clause are excluded from the usage count. This allows the tool to strip joins where the table is referenced solely in its join condition (e.g. `INNER JOIN b ON a.Id = b.Id AND b.Type = 'X'`).

**Use with care**: for `INNER JOIN`s, the `ON` clause can act as a filter. Removing such a join may change the result set if rows exist that don't match the condition.

### Join hints

Join hints are SQL comments placed on or near a `JOIN` clause that tell sql-inliner about the join's cardinality. They enable safe join removal that would otherwise be skipped.

**Available hints:**

| Hint | Meaning |
|---|---|
| `@join:unique` | The join produces at most one matching row per source row (join references a unique/primary key) |
| `@join:required` | Every source row has a matching row in the joined table (FK is `NOT NULL` and referential integrity is enforced) |

**Syntax** — place hints as comments between the `JOIN` keyword and the `ON` clause:

```sql
-- A LEFT JOIN that is safe to remove when unused (at most 1 match, all left rows preserved):
LEFT JOIN /* @join:unique */ dbo.Address a ON a.PersonId = p.Id

-- An INNER JOIN that is safe to remove (exactly 1 match per row, no filtering):
INNER JOIN /* @join:unique @join:required */ dbo.Status s ON s.Id = p.StatusId

-- Multiple separate comments work too:
LEFT JOIN /* @join:unique */ /* @join:required */ dbo.Lookup l ON l.Id = p.LookupId

-- Single-line comment syntax is also supported:
LEFT JOIN -- @join:unique
  dbo.Address a ON a.PersonId = p.Id
```

**Safety matrix:**

| Join type | Hints | Safe to remove? | Reason |
|---|---|---|---|
| `LEFT JOIN` | `@join:unique` | Yes | At most 1 match; all left-side rows preserved |
| `LEFT JOIN` | `@join:unique @join:required` | Yes | Exactly 1 match; no row loss |
| `INNER JOIN` | `@join:unique @join:required` | Yes | Exactly 1 match per row; no filtering |
| `INNER JOIN` | `@join:unique` (no `@required`) | **No** | May filter out rows without a match |
| Any | `@join:required` (no `@unique`) | **No** | Could fan out (multiple matches per row) |
| `RIGHT JOIN` | Any | **No** | Not currently handled |
| `FULL OUTER JOIN` | Any | **No** | Not currently handled |

### Re-inlining support

The generated output embeds the original SQL between `-- BEGIN ORIGINAL SQL VIEW --` and `-- END ORIGINAL SQL VIEW --` markers inside a comment block. When a previously-inlined view is referenced by another view, sql-inliner automatically extracts and uses the original source — so re-inlining always starts from the un-inlined definition rather than compounding transformations.

### Output format

The generated SQL includes a metadata comment followed by the inlined view:

```sql
/*
-- Generated on 1/15/2025 3:42 PM by SQL inliner in 00:00:00.1234567
-- BEGIN ORIGINAL SQL VIEW --
<original CREATE VIEW statement>
-- END ORIGINAL SQL VIEW --

-- Referenced views (3):
[dbo].[VInner1]
[dbo].[VInner2]
[dbo].[VInner3]

-- Removed: 12 select columns and 4 joins

-- Warnings (0):

-- Errors (0):

*/
CREATE OR ALTER VIEW [dbo].[VHeavy] AS
SELECT ...
```

## Verifying the generated code

**Always** compare the inlined view against the original to confirm they return identical results:

```sql
SELECT * FROM dbo.VHeavy EXCEPT SELECT * FROM dbo.VHeavy_v2;
SELECT * FROM dbo.VHeavy_v2 EXCEPT SELECT * FROM dbo.VHeavy;
```

Both queries should return zero rows.

## Library usage

The `SqlInliner.Library` NuGet package exposes the same inlining engine without the CLI. Use it to integrate view inlining into your own tooling, build pipelines, or automated workflows.

```csharp
using SqlInliner;

// Option 1: Use a live database connection
using var sqlConnection = new SqlConnection("Server=.;Database=Test;Integrated Security=true");
sqlConnection.Open();
var connection = new DatabaseConnection(sqlConnection);
var viewSql = connection.GetViewDefinition("dbo.VHeavy");

var inliner = new DatabaseViewInliner(connection, viewSql, InlinerOptions.Recommended());

if (inliner.Errors.Count == 0)
{
    Console.WriteLine(inliner.Result.Sql);
}

// Option 2: Use mock view definitions (no database required)
var mockConnection = new DatabaseConnection();
mockConnection.AddViewDefinition(
    DatabaseConnection.ToObjectName("dbo", "VInner"),
    "CREATE VIEW dbo.VInner AS SELECT Id, Name FROM dbo.People"
);

var outerSql = @"CREATE VIEW dbo.VOuter AS
    SELECT v.Id FROM dbo.VInner v";

var inliner2 = new DatabaseViewInliner(mockConnection, outerSql, new InlinerOptions
{
    StripUnusedColumns = true,
    StripUnusedJoins = true,
});

Console.WriteLine(inliner2.Result.Sql);
```

The `DatabaseViewInliner` exposes two ways to get the SQL:

| Property | Returns |
|---|---|
| `inliner.Sql` | The inlined SQL only (shorthand, same as `Result.Sql` on success or the original SQL on error) |
| `inliner.Result.Sql` | The full output including the metadata comment block with original SQL, referenced views, and strip statistics |
| `inliner.Result.ConvertedSql` | Just the inlined `CREATE VIEW` / `SELECT` statement without the metadata comment |

### InlinerOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `StripUnusedColumns` | `bool` | `true` | Remove unused columns from nested views |
| `StripUnusedJoins` | `bool` | `false` | Remove unused joins from nested views |
| `AggressiveJoinStripping` | `bool` | `false` | Exclude join-condition references from usage count |

Use `InlinerOptions.Recommended()` for the suggested defaults (`StripUnusedJoins = true`, everything else at default).

## Security considerations

`sql-inliner` retrieves view definitions by interpolating the provided view name directly into a SQL statement. If untrusted input is used for the view name, this query could be exploited for SQL injection. The tool is normally executed by a trusted user who also specifies the connection string, so the risk is low, but **only supply view names from trusted sources or sanitize them before running the tool.**

## License

[MIT](LICENSE)
