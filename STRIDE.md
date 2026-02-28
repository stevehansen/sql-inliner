# sql-inliner — STRIDE Threat Model

## Document Information

| Field | Value |
|-------|-------|
| Application | sql-inliner |
| Version | v1 |
| Created | 2026-02-28 |
| Last Updated | 2026-02-28 |
| Next Review | 2029-02-28 |

## 1. System Overview

### Application Description

sql-inliner is a .NET CLI tool and NuGet library that optimizes SQL Server views by inlining nested views into a single flattened query, optionally stripping unused columns and joins. It parses SQL using Microsoft's ScriptDom (TSql150Parser) and uses the visitor pattern to analyze and transform the AST.

### User Types

| User Type | Description | Trust Level |
|-----------|-------------|-------------|
| Developer/DBA | Runs the CLI tool locally or in CI/CD to optimize SQL views | High — has direct database access |
| Library Consumer | Uses the NuGet library programmatically in their own application | High — embeds tool in their code |
| CI/CD Pipeline | Automated execution via GitHub Actions or similar | Medium — runs with configured credentials |

### Components

| Component | Technology | Description |
|-----------|-----------|-------------|
| CLI Entry Point | System.CommandLine | Parses arguments, orchestrates commands |
| Inliner Engine | ScriptDom AST | Parses, transforms, and regenerates SQL |
| Database Connection | Dapper / SqlConnection | Queries `sys.views`, `OBJECT_DEFINITION()`, executes validation SQL |
| Optimize Subsystem | Interactive wizard | Step-by-step view optimization with benchmarking |
| Validate Subsystem | Batch processing | Bulk inlines and validates all views |
| Verify Subsystem | Comparison runner | Validates deployed inlined views against originals |
| Analyze Subsystem | Query Store analysis | Identifies inlining candidates by usage stats |
| Config Loader | System.Text.Json | Loads `sqlinliner.json` with connection strings and view paths |
| Session Directory | File I/O | Saves iteration files, execution plans, benchmark reports |

### Data Flow Diagram

```
                    ┌─────────────────┐
                    │   User (CLI)    │
                    │  --view-name    │
                    │  --conn-string  │
                    │  --config       │
                    └────────┬────────┘
                             │
                    ┌────────▼────────┐
                    │  System.Command │
          ┌────────│  Line Parser    │────────┐
          │        └────────┬────────┘        │
          │                 │                 │
   ┌──────▼──────┐  ┌──────▼──────┐  ┌──────▼──────┐
   │  sqlinliner │  │  --view-path│  │  --config   │
   │  .json      │  │  (SQL file) │  │  (explicit) │
   │  (auto-disc)│  └──────┬──────┘  └──────┬──────┘
   └──────┬──────┘         │                │
          │         ┌──────▼────────────────▼──────┐
          └────────►│    DatabaseConnection         │
                    │  • GetViewDefinition()        │
                    │  • sys.views query            │
                    └──────┬──────────────┬────────┘
                           │              │
                    ┌──────▼──────┐ ┌─────▼────────┐
                    │  ScriptDom  │ │  SQL Server   │
                    │  AST Parse  │ │  Database     │
                    │  & Transform│ │  (Dapper)     │
                    └──────┬──────┘ └─────┬────────┘
                           │              │
                    ┌──────▼──────────────▼────────┐
                    │  Output                       │
                    │  • stdout (inlined SQL)       │
                    │  • Session files (.sql, .log) │
                    │  • Benchmark reports (.html)  │
                    └──────────────────────────────┘
```

### Trust Boundaries

| Boundary | From | To | Data Crossing |
|----------|------|----|---------------|
| TB-1 | User | CLI | View names, connection strings, config paths, SQL file paths |
| TB-2 | CLI | SQL Server | SQL queries (OBJECT_DEFINITION, COUNT, EXCEPT, CREATE/DROP VIEW) |
| TB-3 | CLI | File System | Config files (read), SQL files (read/write), session logs, reports |
| TB-4 | SQL Server | CLI | View definitions, query results, execution plans, statistics |
| TB-5 | NuGet | Consumer App | Library API (view SQL, options) — no DB connection in library mode |

### Data Classification

| Data Type | Classification | Examples |
|-----------|---------------|----------|
| Database Credentials | **Sensitive** | Connection strings with passwords in config/CLI |
| View Definitions | Internal | SQL source code from `OBJECT_DEFINITION()` |
| Query Statistics | Internal | CPU time, logical reads, execution plans |
| Environment Metadata | Internal | Machine name, user name (in benchmark reports) |
| Session Files | Internal | Iteration SQL, logs, benchmark HTML |

## 2. STRIDE Analysis

### S — Spoofing

| ID | Threat | Attack Path | Likelihood | Impact | Score | Mitigation |
|----|--------|-------------|------------|--------|-------|------------|
| S-1 | Malicious config file injection | Attacker places a crafted `sqlinliner.json` in the working directory; tool auto-discovers and loads it | 2 | 3 | 6 | Tool auto-discovers only in CWD. Users should verify CWD before running. No remote config loading. |
| S-2 | Connection string substitution | Attacker replaces config file to redirect DB connections to a malicious server | 2 | 3 | 6 | Config files are local; OS file permissions protect them. Users should not run tool in untrusted directories. |

**Countermeasures:**
- Config auto-discovery is limited to the current working directory
- No remote configuration loading — all paths are local
- Connection strings are validated through `SqlConnectionStringBuilder`
- Library build (`ReleaseLibrary`) excludes all DB-connected subsystems

### T — Tampering

| ID | Threat | Attack Path | Likelihood | Impact | Score | Mitigation |
|----|--------|-------------|------------|--------|-------|------------|
| T-1 | SQL injection via `--view-name` | User-supplied view name is interpolated into `OBJECT_DEFINITION(object_id('...'))` without parameterization | 2 | 4 | **8** | **Unmitigated.** String interpolation in `DatabaseConnection.cs:78,111`. Should use parameterized queries. |
| T-2 | Malicious SQL file via `--view-path` or config `views` | Attacker provides a crafted `.sql` file that contains malicious SQL; tool parses and may deploy it | 2 | 3 | 6 | ScriptDom parses the SQL as a view definition (CREATE VIEW). Deployment only occurs in interactive optimize flow with user confirmation. |
| T-3 | Path traversal in config view paths | Config `views` dictionary specifies `../../../sensitive.sql` to read arbitrary files | 2 | 2 | 4 | `Path.GetFullPath()` normalizes paths but does not validate containment within config directory. Impact limited to reading files as SQL (would fail parsing). |
| T-4 | Session file tampering | Attacker modifies iteration files between optimize steps to inject SQL | 1 | 3 | 3 | Session files are in a timestamped directory. SHA256 hash comparison detects edits. User confirms before deployment. |
| T-5 | Deployed view tampering via CREATE/DROP | Tool executes `CREATE VIEW` and `DROP VIEW` DDL on the target database | 2 | 3 | 6 | Only in validate/verify/optimize subsystems. DROP targets only tool-created views (`_Inlined`, `_Validate`, `_Original` suffixes). Bracketed identifiers prevent injection in DDL. |

**Countermeasures:**
- ScriptDom AST parsing rejects non-view SQL structures
- SHA256 hash verification for session file edits
- Bracketed identifiers `[schema].[viewName]` in DDL statements (QueryRunner, ValidateSession, VerifySession)
- Interactive confirmation before deployment in optimize flow
- **Gap:** `GetViewDefinition()` and `TryGetRawViewDefinition()` use string interpolation — should be parameterized

### R — Repudiation

| ID | Threat | Attack Path | Likelihood | Impact | Score | Mitigation |
|----|--------|-------------|------------|--------|-------|------------|
| R-1 | No audit trail for view deployments | Views are deployed (CREATE OR ALTER VIEW) without logging who deployed or when | 2 | 2 | 4 | Session log records operations with timestamps. Git history tracks source changes. SQL Server has its own audit capabilities. |
| R-2 | Validate/verify operations not logged | Batch operations modify database (temporary views) without persistent audit | 2 | 2 | 4 | Validate writes `validate-errors.log`. Verify cleans up temp views in `finally` blocks. Operations are transient. |

**Countermeasures:**
- Session directory logs all optimize operations with timestamps
- Validate writes error logs to output directory
- SQL Server's own auditing captures DDL changes
- Git tracks all source code changes

### I — Information Disclosure

| ID | Threat | Attack Path | Likelihood | Impact | Score | Mitigation |
|----|--------|-------------|------------|--------|-------|------------|
| I-1 | Connection string exposure via CLI arguments | `--connection-string` visible in process list, shell history, CI/CD logs | 3 | 3 | **9** | **Partially mitigated.** Config file and environment-based alternatives exist, but CLI still accepts plaintext. No built-in redaction. |
| I-2 | Connection strings in config files | `sqlinliner.json` stores connection strings with embedded passwords; file not gitignored by default | 3 | 3 | **9** | **Partially mitigated.** Users can use Windows Authentication (no password). Config file not gitignored — could be committed accidentally. |
| I-3 | Session files contain view SQL and metadata | Session directories store full view SQL, execution plans, table names, machine/user names | 2 | 2 | 4 | Files are local to the user's machine. Default OS permissions apply. No automatic cleanup. |
| I-4 | Exception messages may leak paths or SQL | Error messages expose file paths and database errors to the user | 2 | 1 | 2 | Acceptable for a CLI tool. Errors are displayed to the authenticated user only. |
| I-5 | Benchmark reports contain environment info | HTML reports include `Environment.MachineName`, `Environment.UserName`, database version, table names | 2 | 2 | 4 | Reports are local files. Users should treat them as internal artifacts. |

**Countermeasures:**
- Windows Authentication / Integrated Security eliminates password exposure
- `SqlConnectionStringBuilder` normalizes connection strings (does not add passwords)
- Session files are local with OS-level access control
- **Gap:** `sqlinliner.json` is not in `.gitignore` by default — credential leak risk
- **Gap:** No automatic cleanup of session directories

### D — Denial of Service

| ID | Threat | Attack Path | Likelihood | Impact | Score | Mitigation |
|----|--------|-------------|------------|--------|-------|------------|
| D-1 | Deeply nested view chains cause stack overflow | Maliciously crafted view definitions with extreme nesting depth cause recursive inlining to fail | 1 | 2 | 2 | ScriptDom parsing has practical limits. Recursive inlining follows actual view references. |
| D-2 | Large view output overwhelms resources | Inlining a view with hundreds of nested references produces extremely large SQL output | 2 | 2 | 4 | This is expected behavior for complex views. Users can limit scope with `--view-name`. |
| D-3 | Validate/verify timeout abuse | Long-running validation queries consume database resources | 2 | 2 | 4 | Configurable `--timeout` (default 90s). Per-query timeout via `CommandTimeout`. Timeouts are reported, not retried. |
| D-4 | Session directory disk exhaustion | Many optimize sessions accumulate without cleanup | 2 | 1 | 2 | Directories are small (SQL files + HTML). No automatic cleanup, but manageable manually. |

**Countermeasures:**
- Configurable command timeouts for all database operations
- `--stop-on-error` flag to halt batch operations early
- Per-query timeout handling with graceful error reporting
- ScriptDom parsing limits prevent infinite recursion

### E — Elevation of Privilege

| ID | Threat | Attack Path | Likelihood | Impact | Score | Mitigation |
|----|--------|-------------|------------|--------|-------|------------|
| E-1 | SQL execution with connection credentials | Tool executes arbitrary SQL (CREATE VIEW, DROP VIEW, SELECT) with the privileges of the provided connection string | 2 | 3 | 6 | By design — tool requires database access. Users should use least-privilege accounts. DDL limited to view operations. |
| E-2 | SQL injection escalates to DB admin | If T-1 is exploited, injected SQL runs with full connection privileges | 1 | 4 | 4 | Mitigated by fixing T-1 (parameterized queries). Connection should use least-privilege account. |
| E-3 | Process.Start with UseShellExecute | `OptimizeSession.cs:255` opens a file with the default system handler via `Process.Start` | 1 | 2 | 2 | File path is generated by the tool (session directory). No user input in the path construction. |

**Countermeasures:**
- Tool operates with the privileges of the provided connection string — no privilege escalation within the tool
- `Process.Start` paths are tool-generated, not user-supplied
- Library build excludes all database-connected code
- **Recommendation:** Use dedicated SQL Server accounts with minimal permissions (VIEW DEFINITION, SELECT, CREATE VIEW, ALTER VIEW)

## 3. Risk Summary

### High Priority Threats (Score >= 8)

| ID | Threat | Score | Status |
|----|--------|-------|--------|
| T-1 | SQL injection via `--view-name` in `DatabaseConnection.cs` | 8 | Unmitigated |
| I-1 | Connection string exposure via CLI arguments | 9 | Partially mitigated |
| I-2 | Connection strings in config files (not gitignored) | 9 | Partially mitigated |

### Residual Risks

- **T-1 (SQL Injection):** The `--view-name` parameter flows directly into string-interpolated SQL. While the tool is a local CLI used by trusted developers/DBAs, this violates defense-in-depth. Fix: use parameterized queries (`new { viewName }`).
- **I-1/I-2 (Credential Exposure):** Connection strings with embedded passwords can leak via process lists, shell history, CI logs, or accidentally committed config files. The recommended approach is Windows Authentication (no password in connection string). Adding `sqlinliner.json` to the `.gitignore` template would reduce the config file risk.

## 4. Security Controls Summary

| Category | Implementation |
|----------|---------------|
| Authentication | Delegates to SQL Server authentication (Windows Auth or SQL Auth via connection string) |
| Authorization | Delegates to SQL Server permissions; tool requires VIEW DEFINITION, SELECT, and optionally CREATE/ALTER/DROP VIEW |
| Input Validation | ScriptDom AST parsing validates SQL structure; `SqlConnectionStringBuilder` validates connection strings |
| Output Encoding | SQL output generated by `Sql150ScriptGenerator` with proper escaping |
| Secrets Management | Connection strings via CLI args or config file; no built-in secrets vault integration |
| Audit Logging | Session logs with timestamps; `validate-errors.log` for batch operations |
| Error Handling | Exceptions caught at command level; error messages displayed to user |
| Dependency Security | CodeQL analysis in CI; no known vulnerable dependencies |
| Build Security | NuGet OIDC trusted publishing; trimmed single-file release builds |
| Data Protection | Session files use OS-level access control; SHA256 hash verification for edit detection |

## 5. Review History

| Version | Date | Reviewer | Changes |
|---------|------|----------|---------|
| v1 | 2026-02-28 | Claude Code (STRIDE analysis) | Initial threat model |

## 6. References

- [OWASP STRIDE](https://owasp.org/www-community/Threat_Modeling_Process#stride)
- [Microsoft Threat Modeling](https://learn.microsoft.com/en-us/azure/security/develop/threat-modeling-tool)
- [OWASP SQL Injection Prevention](https://cheatsheetseries.owasp.org/cheatsheets/SQL_Injection_Prevention_Cheat_Sheet.html)
- [SQL Server Security Best Practices](https://learn.microsoft.com/en-us/sql/relational-databases/security/sql-server-security-best-practices)
- [Dapper Parameterized Queries](https://www.learndapper.com/parameters)
