# sql-inliner
Helper utility to inline SQL server database views

> **Always verify the generated code manually before deploying to a production database**


## Example usage

``sql-inliner -cs "Server=.;Database=Test;Integrated Security=true" -vn "dbo.VHeavy" --strip-unused-joins``

Will fetch the definition of the VHeavy view from the Test database and recursively inline each non-indexed view that it detects while stripping unused columns (defaults to true) and unused joins (both inner and outer) for performance reasons.
The application will output the new create or alter view statement that can be used on the database.

The generated statement will include a starting comment containing the original statement (can be used to restore the original code) which is also used when the view is reused in other views to start working from the original statement.
Other included information will be the different views that were used, how many select columsn and joins that were stripped.


## Verifying the generated code

**Always** verify the SQL by comparing it with the old code.

```sql
select * from dbo.VHeavy except select * from dbo.VHeavy_v2
select * from dbo.VHeavy_v2 except select * from dbo.VHeavy
```

Should return 0 results for both queries.