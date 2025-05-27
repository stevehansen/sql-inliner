# sql-inliner
Helper utility to inline SQL Server database views

## What is the purpose of this tool 
While SQL Server can technically handle the nesting of views, it’s important to understand that this practice can lead to significant performance problems. The core issue is not SQL Server’s inability to process nested views, but rather the loss of oversight by developers as the complexity increases. When multiple views are stacked on top of each other, it becomes easy to overlook the extra, often unnecessary data being retrieved—such as redundant joins or unneeded columns. These inefficiencies accumulate and can significantly impact query performance.
For instance, when a nested view pulls in more data than required, it leads to larger datasets being processed, even if they’re irrelevant to the final query result. This not only consumes more memory and processing power but also results in longer execution times. To ensure optimal performance, it’s critical for developers to regularly review and simplify the logic of views, avoiding unnecessary complexity and ensuring that only the required data is fetched.

> **Always verify the generated code manually before deploying to a production database**

## Installation

Install the application as a [.NET tool](https://learn.microsoft.com/dotnet/core/tools/) using:

```bash
dotnet tool install --global sqlinliner
```

This registers the `sqlinliner` command globally so it can be used from any directory.

## Example usage

Using integrated security on a local SQL Server instance:
``sqlinliner -cs "Server=.;Database=Test;Integrated Security=true" -vn "dbo.VHeavy" --strip-unused-joins``

Will fetch the definition of the VHeavy view from the Test database and recursively inline each non-indexed view that it detects while stripping unused columns (defaults to true) and unused joins (both inner and outer) for performance reasons.

Using SQL Server authentication on a SQL Server in the network:
``sqlinliner -cs "Server=hostname.domain.net;Database=databasename;user=login;password='password example with space'" -vn "dbo.theSlowView" --strip-unused-joins``

As with the first example, this command will fetch the definition of view theSlowView from the databasename database and recursively inline each non-indexed view that it detects while stripping unused columns (defaults to true) and unused joins (both inner and outer) for performance reasons.
A connection will be made using SQL Server login and password.

The application will output the new create or alter view statement that can be used on the database to create the improved version of the original view.
The generated statement will include a starting comment containing the original statement (can be used to restore the original code) which is also used when the view is reused in other views to start working from the original statement.
Other included information will be the different views that were used, how many select columns and joins that were stripped.

### Using a view definition from a local file
``sqlinliner -vp "./views/MyView.sql" --strip-unused-joins``

This scenario inlines the view defined in `MyView.sql`, with unused join stripping enabled (via `--strip-unused-joins`).
When a file path is specified for the main view, the tool uses the exact contents of that file. If a connection
string is also supplied, any views referenced *within* `MyView.sql` are fetched from the database.

### Disabling the CREATE OR ALTER wrapper
``sqlinliner -vp "./views/MyView.sql" --generate-create-or-alter false``

Pass `--generate-create-or-alter false` when you only need the inlined `SELECT`
statement. This can be useful when embedding the statement inside a larger
script or when comparing different versions of the SQL.

### Keeping all joins in the final statement
``sqlinliner -cs "Server=.;Database=Test;Integrated Security=true" -vn "dbo.VHeavy" --strip-unused-joins false``

This variant keeps every join from nested views but will still remove unused
columns. It can be handy when you want the view expanded but prefer to control
join optimization yourself.

### Additional options

Several optional parameters can be used to control where the generated SQL and debug information are written:

* `--output-path` (`-op`) – write the resulting SQL to the specified file instead of the console.
* `--log-path` (`-lp`) – write warnings, errors and timing information to the given file. When not provided these details are written to the console.
* `--dry-run` (`-dr`) – list all referenced views recursively without outputting the inlined SQL.

## Verifying the generated code

**Always** verify the SQL by comparing it with the old code.

```sql
select * from dbo.VHeavy except select * from dbo.VHeavy_v2
select * from dbo.VHeavy_v2 except select * from dbo.VHeavy
```

Should return 0 results for both queries.

## Security considerations

`sql-inliner` retrieves view definitions by interpolating the provided view name
directly into a SQL statement. If untrusted input is used for the view name, this
query could be exploited for SQL injection. The tool is normally executed by a
trusted user who also specifies the connection string, so the risk is low, but
**only supply view names from trusted sources or sanitize them before running the
tool.**
