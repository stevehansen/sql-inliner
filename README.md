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
| `--config` | `-c` | path | — | Path to a `sqlinliner.json` configuration file (auto-discovers `sqlinliner.json` in current directory) |
| `--connection-string` | `-cs` | string | — | Connection string to the SQL Server database |
| `--view-name` | `-vn` | string | — | Fully qualified name of the view to inline (e.g. `dbo.MyView`) |
| `--view-path` | `-vp` | path | — | Path to a `.sql` file containing a `CREATE VIEW` statement |
| `--strip-unused-columns` | `-suc` | bool | `true` | Remove columns from nested views that the outer view does not reference |
| `--strip-unused-joins` | `-suj` | bool | `false` | Remove joins from nested views whose tables contribute no columns to the result |
| `--aggressive-join-stripping` | — | bool | `false` | Exclude join-condition column references from the usage count (can change results for INNER JOINs — see below) |
| `--flatten-derived-tables` | `-fdt` | bool | `false` | Flatten derived tables (subqueries) produced by inlining into the outer query (experimental — see below) |
| `--generate-create-or-alter` | — | bool | `true` | Wrap the output in a `CREATE OR ALTER VIEW` statement |
| `--output-path` | `-op` | path | — | Write the resulting SQL to a file instead of the console |
| `--log-path` | `-lp` | path | — | Write warnings, errors, and timing info to a file |

At least one of `--view-name` or `--view-path` is required. When both are supplied, `--view-path` provides the main view definition while `--view-name` (with `--connection-string`) is used to fetch any nested views referenced inside it from the database.

A connection string is not required when all referenced views are available locally — either via `--view-path` or the `views` mapping in a config file.

The tool writes the inlined SQL to stdout (or to `--output-path`) and always exits with code `0`. Check the `-- Errors` section in the output metadata comment or the `--log-path` file to detect problems.

## Configuration file

Instead of passing all options on the command line, you can create a `sqlinliner.json` file:

```json
{
    "connectionString": "Server=.;Database=MyDB;User Id=sa;Password=secret",
    "stripUnusedColumns": true,
    "stripUnusedJoins": true,
    "aggressiveJoinStripping": false,
    "flattenDerivedTables": false,
    "generateCreateOrAlter": true,
    "views": {
        "dbo.VPeople": "VPeople.sql",
        "dbo.VNestedPeople": "./nested/VNestedPeople.sql"
    }
}
```

All fields are optional. CLI arguments always override config values.

- **Auto-discovery**: If `--config` is not specified, the tool looks for `sqlinliner.json` in the current directory.
- **View mappings**: The `views` object maps fully qualified view names to `.sql` file paths. Paths are resolved relative to the config file's directory. These views are registered before inlining, so nested views can be resolved from files instead of a database connection.
- **No connection required**: When all referenced views are provided via the `views` mapping, no `--connection-string` is needed.

### Using a config file

```bash
# Explicit config path
sqlinliner -c ./config/sqlinliner.json -vn dbo.VHeavy

# Auto-discover sqlinliner.json in current directory
sqlinliner -vn dbo.VHeavy

# Config + local file (nested views resolved from config)
sqlinliner -c sqlinliner.json -vp ./views/VHeavy.sql --strip-unused-joins
```

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

## Interactive optimization wizard

The `optimize` subcommand provides a guided, interactive workflow for optimizing a view against a backup or development database. It walks you through inlining, deploying, validating correctness, and benchmarking performance — all in one session.

> **Warning:** Only run `optimize` against a backup or development database. It will execute `CREATE OR ALTER VIEW` statements directly.

```bash
sqlinliner optimize \
  -cs "Server=.;Database=TestBackup;Integrated Security=true" \
  -vn "dbo.VHeavy"
```

### Workflow steps

1. **Connect & Warn** — Prompts you to confirm the database is a backup/development copy.
2. **Select View** — Validates the view exists and shows metadata (SQL length, nested view count).
3. **Inline** — Runs the inliner with the current options and saves the result to a session directory.
4. **Review** — Optionally opens the generated SQL in your default editor. Detects manual edits and offers to regenerate.
5. **Deploy** — Executes `CREATE OR ALTER VIEW [schema].[ViewName_Inlined]` on the database.
6. **Validate** — Compares the original and inlined views with `COUNT` and `EXCEPT` (both directions) to verify identical results.
7. **Iterate** — Toggle options (strip-joins, aggressive mode) and re-inline, or continue to benchmarking.
8. **Benchmark** — Uses `SET STATISTICS TIME/IO/XML ON` to compare CPU time, elapsed time, and logical reads between the original and inlined views. Shows a per-table IO breakdown (sorted by heaviest reads), saves actual execution plans as `.sqlplan` files (openable in SSMS), and generates a self-contained `benchmark.html` report.
9. **Summary & Cleanup** — Shows results, saves a `recommended.sql`, and prints a `DROP VIEW` statement (never executed automatically).

### Options

| Option | Alias | Type | Description |
|---|---|---|---|
| `--connection-string` | `-cs` | string | Connection string (required, can come from config file) |
| `--view-name` | `-vn` | string | Fully qualified view name. If omitted, you will be prompted. |

The `--config` / `-c` option is shared with the root command and also applies here.

### Session directory

Each optimization session creates a directory in the current working directory (e.g. `optimize-VHeavy-20260225T143022/`) containing:
- `iteration-1.sql`, `iteration-2.sql`, ... — SQL from each iteration
- `recommended.sql` — the final recommended version
- `plan-original.sqlplan`, `plan-inlined.sqlplan` — actual execution plans (open in SSMS for visual comparison)
- `benchmark.html` — self-contained HTML report with performance summary and per-table IO breakdown
- `session.log` — timestamped log of all actions

## Batch validation

The `validate` subcommand processes all views in a database in a single run, inlining each one and reporting pass/fail. This acts as a regression/smoke test for the inlining engine across your entire view catalog.

```bash
sqlinliner validate \
  -cs "Server=.;Database=TestBackup;Integrated Security=true"
```

### Options

| Option | Alias | Type | Default | Description |
|---|---|---|---|---|
| `--connection-string` | `-cs` | string | — | Connection string (required, can come from config file) |
| `--deploy` | `-d` | bool | `false` | Deploy each inlined view and run COUNT + EXCEPT validation |
| `--deploy-only` | — | bool | `false` | Deploy each inlined view to check for SQL errors, but skip COUNT + EXCEPT (faster) |
| `--output-dir` | `-o` | path | — | Save inlined SQL files to a directory |
| `--stop-on-error` | — | bool | `false` | Halt on first failure instead of continuing |
| `--filter` | `-f` | string | — | Only process matching views (exact name or `%` wildcard) |
| `--timeout` | `-t` | int | `90` | Query timeout in seconds for COUNT and EXCEPT queries |
| `--strip-unused-columns` | `-suc` | bool | `true` | Remove unused columns from nested views |
| `--strip-unused-joins` | `-suj` | bool | `false` | Remove unused joins from nested views |
| `--aggressive-join-stripping` | — | bool | `false` | Exclude join-condition references from usage count |
| `--flatten-derived-tables` | `-fdt` | bool | `false` | Flatten derived tables into the outer query |

The `--config` / `-c` option is shared with the root command and also applies here.

### Examples

```bash
# Basic: inline all views, report pass/fail
sqlinliner validate -cs "Server=.;Database=MyDb;Integrated Security=true"

# With config file
sqlinliner validate -c sqlinliner.json

# Deploy and validate correctness (COUNT + EXCEPT)
sqlinliner validate -cs "..." --deploy

# Deploy only — check for SQL errors without COUNT + EXCEPT
sqlinliner validate -cs "..." --deploy-only

# Save inlined SQL files
sqlinliner validate -cs "..." --output-dir ./inlined-views/

# Filter to specific views
sqlinliner validate -cs "..." --filter "dbo.V%"

# Stop on first failure
sqlinliner validate -cs "..." --stop-on-error
```

### Deploy modes

When `--deploy` or `--deploy-only` is enabled, the validation runs in two phases:

1. **Phase 1 (inline)**: All views are inlined in-memory (fast). Views with no nested views are skipped. A summary shows how many views need deploying.
2. **Phase 2 (deploy)**: Only views that passed inlining are deployed, with a progress counter reflecting the actual deployable count.

**`--deploy`** deploys each inlined view as `[schema].[ViewName_Validate]`, runs COUNT + EXCEPT comparisons, and always drops `_Validate` afterward.

**`--deploy-only`** deploys each inlined view as `[schema].[ViewName_Validate]` and immediately drops it — catching SQL errors (invalid columns, bad aliases) without the overhead of COUNT/EXCEPT queries. Much faster for initial error checking.

> **Warning:** Only run `--deploy` or `--deploy-only` against a backup or development database.

### Summary report

At the end of the run, a summary shows the status of all views:

```
=== Validation Summary ===
Total: 500 views in 2m 34s

  Status              Count
  ------------------  -----
  Pass                  312
  PassWithWarnings       28
  Skipped              145
  InlineError             8
  ...
```

Status values: `Pass`, `PassWithWarnings`, `Skipped` (no nested views), `InlineError`, `ParseError`, `DeployError`, `ValidationFail`, `Timeout`, `Exception`.

## Verify deployed inlined views

The `verify` subcommand auto-detects views that have been replaced with inlined versions (by checking for embedded `BEGIN ORIGINAL SQL VIEW` / `END ORIGINAL SQL VIEW` markers), deploys the embedded original as a temporary view, and runs COUNT + EXCEPT comparisons to verify the deployed inlined view still matches its original.

This is useful when SqlInliner is used as a library to replace views in-place — `verify` confirms the deployed inlined views are still correct.

```bash
sqlinliner verify \
  -cs "Server=.;Database=MyDb;Integrated Security=true"
```

### Options

| Option | Alias | Type | Default | Description |
|---|---|---|---|---|
| `--connection-string` | `-cs` | string | — | Connection string (required, can come from config file) |
| `--filter` | `-f` | string | — | Only process matching views (exact name or `%` wildcard) |
| `--stop-on-error` | — | bool | `false` | Halt on first failure (timeouts don't halt) |
| `--timeout` | `-t` | int | `120` | Query timeout in seconds for COUNT and EXCEPT queries |

The `--config` / `-c` option is shared with the root command and also applies here.

### How it works

For each view with markers:

1. Extracts the original SQL from between the markers
2. Deploys the original as `[schema].[ViewName_Original]`
3. Runs `COUNT` and `EXCEPT` comparisons between the original and the deployed inlined view
4. Always drops `_Original` afterward (cleanup)

Views with a `_Inlined` suffix are automatically skipped when the base view also exists (these are companion copies from the `optimize` workflow, not the canonical inlined versions).

### Examples

```bash
# Verify all inlined views
sqlinliner verify -cs "Server=.;Database=MyDb;Integrated Security=true"

# With config file
sqlinliner verify -c sqlinliner.json

# Filter to specific views
sqlinliner verify -cs "..." --filter "dbo.V%"

# Stop on first failure
sqlinliner verify -cs "..." --stop-on-error

# Custom timeout (for heavy views)
sqlinliner verify -cs "..." --timeout 300
```

### Summary report

```
=== Verify Summary ===
Total: 42 views in 1m 15s

  Status              Count
  ------------------  -----
  Pass                   38
  Timeout                 3
  ValidationFail          1
```

Status values: `Pass`, `Skipped`, `DeployError`, `ValidationFail`, `Timeout`, `Exception`.

## Analyze inlining candidates

The `analyze` subcommand identifies which views would benefit most from inlining by combining dependency depth analysis with production Query Store statistics. Use it to proactively find candidates instead of waiting for timeouts.

```bash
sqlinliner analyze \
  -cs "Server=.;Database=MyDb;Integrated Security=true"
```

### Options

| Option | Alias | Type | Default | Description |
|---|---|---|---|---|
| `--connection-string` | `-cs` | string | — | Connection string (required for live mode, can come from config file) |
| `--filter` | `-f` | string | — | Only process matching views (exact name or `%` wildcard) |
| `--days` | — | int | `30` | Query Store lookback period in days |
| `--min-executions` | — | int | `5` | Minimum execution count for Query Store stats |
| `--top` | — | int | — | Limit output to the top N candidates by score |
| `--generate-script` | — | bool | — | Generate a SQL Server stored procedure for extracting analyze data |
| `--from-file` | — | path | — | Load analyze data from a previously exported JSON file (offline mode) |
| `--output-path` | `-op` | path | — | Write output to a file instead of the console (used with `--generate-script`) |

The `--config` / `-c` option is shared with the root command and also applies here.

### How it works

1. **Dependency analysis** — Queries `sys.sql_expression_dependencies` with a recursive CTE to compute nesting depth, direct view references, and transitive view count for each view
2. **Query Store statistics** — If Query Store is enabled, pulls aggregated execution counts, durations, CPU time, and logical reads, then matches them to views by name
3. **Inlined status detection** — Checks each view for `BeginOriginal`/`EndOriginal` markers to identify already-inlined views

### Two-phase workflow (offline analysis)

Running `analyze` directly against production may not be acceptable. Since Query Store data survives backup/restore, you can extract the data from a restored backup — or from production using a lightweight, reviewed stored procedure — and analyze offline.

**Phase 1: Extract data**

Generate the extraction stored procedure:

```bash
# Print to stdout (review before deploying)
sqlinliner analyze --generate-script

# Save to file
sqlinliner analyze --generate-script --output-path extract.sql

# Custom defaults baked into the SP
sqlinliner analyze --generate-script --days 7 --min-executions 10
```

Deploy and run the SP in SSMS or sqlcmd:

```sql
-- Create the stored procedure
-- (run the generated script)

-- Extract as JSON for sqlinliner:
EXEC dbo.SqlInliner_ExtractAnalyzeData @OutputFormat = 'Json';

-- Or as result sets for SSMS grid view:
EXEC dbo.SqlInliner_ExtractAnalyzeData @OutputFormat = 'ResultSets';
```

Save the JSON output to a file (e.g. `analyze-data.json`).

**Phase 2: Analyze offline**

```bash
# Score and rank from exported data
sqlinliner analyze --from-file analyze-data.json

# With filtering and limits
sqlinliner analyze --from-file analyze-data.json --top 20 --filter "dbo.VCar%"
```

The `--from-file` mode requires no database connection. It uses the same scoring and display logic as live mode.

### Scoring

Views are scored and ranked using:

```
Score = 3 * log2(1 + depth) * log2(1 + transitiveViews)
      + 2 * log10(1 + executions)
      + 1 * log10(1 + totalCpuMs + totalLogicalReadsK)
```

Nesting depth is weighted highest (primary benefit driver), execution frequency second (amplifies benefit), raw cost lowest (may not be nesting-related).

### Examples

```bash
# Analyze all views (live)
sqlinliner analyze -cs "Server=.;Database=MyDb;Integrated Security=true"

# With config file
sqlinliner analyze -c sqlinliner.json

# Top 20 candidates
sqlinliner analyze -cs "..." --top 20

# Filter to specific views
sqlinliner analyze -cs "..." --filter "dbo.VCar%"

# Custom Query Store lookback
sqlinliner analyze -cs "..." --days 7 --min-executions 10

# Generate extraction script
sqlinliner analyze --generate-script --output-path extract.sql

# Offline analysis from exported JSON
sqlinliner analyze --from-file analyze-data.json
```

### Output

Two tables are displayed:

1. **Candidates** — Views not yet inlined, sorted by score descending, with nesting depth, transitive view count, and Query Store stats (if available)
2. **Already Inlined** — Views with inlined markers (informational)

Plus a summary of views skipped because they have no nested view references.

## Credential management

The `credentials` subcommand stores database credentials in your OS credential store (Windows Credential Manager, macOS Keychain, or Linux libsecret). This eliminates the need to put passwords in connection strings, CLI arguments, or config files.

### Storing credentials

```bash
# Store credentials (prompts for password with masked input)
sqlinliner credentials add -s myserver -d mydb -u myuser

# Store with prompted username too
sqlinliner credentials add -s myserver -d mydb
```

### Auto-injection

Once credentials are stored, connection strings without explicit credentials will automatically use the stored values:

```bash
# Password-free connection string — credentials auto-injected from store
sqlinliner -cs "Server=myserver;Database=mydb" -vn dbo.VHeavy

# Also works with subcommands
sqlinliner validate -cs "Server=myserver;Database=mydb"
sqlinliner optimize -cs "Server=myserver;Database=mydb"
```

The resolution order is:
1. Explicit `User Id` and `Password` in the connection string — used as-is
2. `Integrated Security=true` in the connection string — used as-is
3. Stored credentials matching the server/database — injected automatically
4. Fallback — `Integrated Security=true` (Windows Authentication)

### Listing and removing credentials

```bash
# List all stored credentials (passwords are never shown)
sqlinliner credentials list

# Remove stored credentials
sqlinliner credentials remove -s myserver -d mydb
```

### Config files without passwords

With the credential store, config files can omit passwords entirely:

```json
{
    "connectionString": "Server=myserver;Database=mydb"
}
```

### Platform support

| Platform | Backend |
|----------|---------|
| Windows | Credential Manager (advapi32.dll) |
| macOS | Keychain (`security` CLI) |
| Linux | libsecret (`secret-tool` CLI) |

On Linux, `secret-tool` must be installed (`sudo apt install libsecret-tools` on Ubuntu/Debian).

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

### Nested derived table stripping

When column stripping or join stripping is enabled, a post-processing step automatically strips unused columns and LEFT JOINs *inside* nested derived tables produced by inlining. This handles cases where the first-level inlining produces subqueries that still carry unused columns or unnecessary joins from deeper nesting levels.

The stripper:
- Iterates until no more stripping occurs (handles cascading effects across nesting levels)
- Skips derived tables with `SELECT *`, `DISTINCT`, `TOP`, `GROUP BY`, or `HAVING` (same safety rules as first-level stripping)
- Only strips `LEFT OUTER JOIN`s to other derived tables (inlined views), not to base tables which were already evaluated with join hints
- Treats single-part column identifiers conservatively (assumes they could reference any table)

No additional CLI flag is needed — this runs automatically when `--strip-unused-columns` or `--strip-unused-joins` is enabled.

### Derived table flattening (experimental)

Disabled by default; enable with `--flatten-derived-tables`. After inlining, each nested view reference becomes a derived table (subquery in the `FROM` clause). When derived table flattening is enabled, the tool removes these subquery wrappers and promotes the inner tables directly into the outer query, producing a single flat `SELECT` with no nesting.

**Example:**

```sql
-- After inlining (default): derived table wrapper remains
SELECT [v].[Id], [v].[Name]
FROM (
    SELECT [p].[Id], [p].[Name]
    FROM [dbo].[People] [p]
    WHERE [p].[Active] = 1
) [v]
WHERE [v].[Id] > 10

-- With --flatten-derived-tables: fully flat query
SELECT [p].[Id], [p].[Name]
FROM [dbo].[People] [p]
WHERE ([p].[Id] > 10) AND ([p].[Active] = 1)
```

Inner JOINs within the subquery are also promoted:

```sql
-- After inlining: nested JOIN inside derived table
SELECT [v].[Id], [v].[Name]
FROM (
    SELECT [a].[Id], [b].[Name]
    FROM [dbo].[A] [a] INNER JOIN [dbo].[B] [b] ON [a].[Id] = [b].[AId]
) [v]

-- With --flatten-derived-tables: JOINs promoted to outer query
SELECT [a].[Id], [b].[Name]
FROM [dbo].[A] [a] INNER JOIN [dbo].[B] [b] ON [a].[Id] = [b].[AId]
```

A derived table is eligible for flattening when:

- The inner query is a simple `SELECT` (not `UNION`/`EXCEPT`/`INTERSECT`)
- No `GROUP BY`, `HAVING`, `TOP`, or `DISTINCT`
- No `SELECT *`
- All columns referenced by the outer query are simple column references (not expressions like `CASE` or function calls)
- All tables in the inner `FROM` clause are named tables (no nested derived tables)

The tool automatically detects and resolves alias collisions when the inner table aliases conflict with tables already in the outer query.

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

-- Removed: 12 select columns and 4 joins and flattened 3 derived tables

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
| `FlattenDerivedTables` | `bool` | `false` | Flatten derived tables (subqueries) into the outer query |

Use `InlinerOptions.Recommended()` for the suggested defaults (`StripUnusedJoins = true`, everything else at default).

## Security considerations

`sql-inliner` retrieves view definitions by interpolating the provided view name directly into a SQL statement. If untrusted input is used for the view name, this query could be exploited for SQL injection. The tool is normally executed by a trusted user who also specifies the connection string, so the risk is low, but **only supply view names from trusted sources or sanitize them before running the tool.**

## License

[MIT](LICENSE)
