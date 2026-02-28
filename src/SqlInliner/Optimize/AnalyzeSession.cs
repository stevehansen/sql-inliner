#if !RELEASELIBRARY

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dapper;

namespace SqlInliner.Optimize;

/// <summary>
/// Options for an analyze run.
/// </summary>
public sealed class AnalyzeSessionOptions
{
    /// <summary>Only process matching views (exact name or SQL LIKE-style % wildcard).</summary>
    public string? Filter { get; set; }

    /// <summary>Query Store lookback period in days.</summary>
    public int Days { get; set; } = 30;

    /// <summary>Minimum execution count for Query Store stats.</summary>
    public int MinExecutions { get; set; } = 5;

    /// <summary>Limit output to the top N candidates.</summary>
    public int? Top { get; set; }
}

/// <summary>
/// Per-view analysis result combining dependency depth and Query Store statistics.
/// </summary>
public sealed class ViewAnalyzeResult
{
    public string SchemaName { get; set; } = "";
    public string ViewName { get; set; } = "";
    public int NestingDepth { get; set; }
    public int DirectViewRefs { get; set; }
    public int TransitiveViewCount { get; set; }
    public long ExecutionCount { get; set; }
    public double AvgDurationMs { get; set; }
    public double MaxDurationMs { get; set; }
    public double TotalCpuMs { get; set; }
    public double TotalLogicalReadsK { get; set; }
    public bool IsInlined { get; set; }
    public double Score { get; set; }

    public string FullName => $"{SchemaName}.{ViewName}";
}

// --- JSON export DTOs ---

/// <summary>
/// Root envelope for the analyze data export JSON format.
/// </summary>
public sealed class AnalyzeDataExport
{
    public int Version { get; set; } = 1;
    public DateTime GeneratedUtc { get; set; }
    public string ServerVersion { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public AnalyzeDataParameters Parameters { get; set; } = new();
    public bool QueryStoreEnabled { get; set; }
    public List<AnalyzeDataView> Views { get; set; } = new();
    public List<AnalyzeDataQueryStoreRow> QueryStoreStats { get; set; } = new();
}

/// <summary>
/// Parameters used when the data was collected.
/// </summary>
public sealed class AnalyzeDataParameters
{
    public int Days { get; set; }
    public int MinExecutions { get; set; }
}

/// <summary>
/// View dependency data in the export format.
/// </summary>
public sealed class AnalyzeDataView
{
    public string SchemaName { get; set; } = "";
    public string ViewName { get; set; } = "";
    public int NestingDepth { get; set; }
    public int DirectViewRefs { get; set; }
    public int TransitiveViewCount { get; set; }
    public bool IsInlined { get; set; }
}

/// <summary>
/// Query Store statistics row in the export format.
/// </summary>
public sealed class AnalyzeDataQueryStoreRow
{
    public string QuerySqlText { get; set; } = "";
    public long ExecutionCount { get; set; }
    public double AvgDurationUs { get; set; }
    public double MaxDurationUs { get; set; }
    public double TotalCpuUs { get; set; }
    public double TotalLogicalReads { get; set; }
}

/// <summary>
/// Drives the analyze workflow: query dependency depth, Query Store stats, detect inlined status, score and rank.
/// </summary>
public sealed class AnalyzeSession
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly DatabaseConnection? connection;
    private readonly IConsoleWizard wizard;

    public AnalyzeSession(DatabaseConnection connection, IConsoleWizard wizard)
    {
        this.connection = connection;
        this.wizard = wizard;
    }

    /// <summary>
    /// Constructor for offline mode (no database connection).
    /// </summary>
    public AnalyzeSession(IConsoleWizard wizard)
    {
        this.wizard = wizard;
    }

    /// <summary>
    /// Runs the analysis against a live database and returns per-view results sorted by score descending.
    /// </summary>
    public List<ViewAnalyzeResult> Run(AnalyzeSessionOptions options)
    {
        var sw = Stopwatch.StartNew();

        if (connection?.Connection == null)
            throw new InvalidOperationException("No database connection available.");

        // Phase 1: View dependency analysis
        wizard.Info("Phase 1: Analyzing view dependencies...");
        var depResults = QueryViewDependencies();
        wizard.Info($"  Found {depResults.Count} views with nested view references.");

        // Apply filter
        if (!string.IsNullOrEmpty(options.Filter))
        {
            var filterRegex = ValidateSession.BuildFilterRegex(options.Filter);
            depResults = depResults
                .Where(r => filterRegex.IsMatch($"{r.SchemaName}.{r.ViewName}"))
                .ToList();
            wizard.Info($"  After filter: {depResults.Count} views.");
        }

        // Phase 2: Query Store statistics
        wizard.Info("Phase 2: Querying Query Store statistics...");
        var queryStoreAvailable = IsQueryStoreEnabled();
        var queryStoreStats = new Dictionary<string, QueryStoreViewStats>(StringComparer.OrdinalIgnoreCase);

        if (queryStoreAvailable)
        {
            var queryRows = QueryQueryStoreRows(options.Days, options.MinExecutions);
            var viewNames = depResults.Select(v => v.ViewName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            queryStoreStats = MatchQueryStoreToViews(viewNames, queryRows);
            wizard.Info($"  Matched stats for {queryStoreStats.Count} views.");
        }
        else
        {
            wizard.Warn("Query Store is not enabled on this database. Scoring will use dependency depth only.");
        }

        // Phase 3: Inlined status detection
        wizard.Info("Phase 3: Detecting already-inlined views...");
        var results = new List<ViewAnalyzeResult>();

        foreach (var dep in depResults)
        {
            var fullName = $"{dep.SchemaName}.{dep.ViewName}";
            var result = new ViewAnalyzeResult
            {
                SchemaName = dep.SchemaName,
                ViewName = dep.ViewName,
                NestingDepth = dep.NestingDepth,
                DirectViewRefs = dep.DirectViewRefs,
                TransitiveViewCount = dep.TransitiveViewCount,
            };

            // Check inlined status
            var rawDef = connection.TryGetRawViewDefinition(fullName);
            if (rawDef != null)
                result.IsInlined = rawDef.Contains(DatabaseView.BeginOriginal);

            // Apply Query Store stats
            if (queryStoreStats.TryGetValue(dep.ViewName, out var stats))
            {
                result.ExecutionCount = stats.ExecutionCount;
                result.AvgDurationMs = stats.AvgDurationUs / 1000.0;
                result.MaxDurationMs = stats.MaxDurationUs / 1000.0;
                result.TotalCpuMs = stats.TotalCpuUs / 1000.0;
                result.TotalLogicalReadsK = stats.TotalLogicalReads / 1000.0;
            }

            // Compute score
            result.Score = ComputeScore(result);
            results.Add(result);
        }

        // Sort by score descending
        results = results.OrderByDescending(r => r.Score).ToList();

        sw.Stop();
        wizard.Info($"Analysis complete in {FormatElapsed(sw.Elapsed)}.");
        wizard.Info("");

        // Display results
        ScoreAndDisplay(results, queryStoreAvailable, options, connection.Views.Count);

        return results;
    }

    /// <summary>
    /// Runs the analysis from a previously exported JSON file (offline mode, no database required).
    /// </summary>
    public List<ViewAnalyzeResult> RunFromFile(string filePath, AnalyzeSessionOptions options)
    {
        var sw = Stopwatch.StartNew();

        wizard.Info($"Loading analyze data from: {filePath}");
        var json = File.ReadAllText(filePath);
        var export = JsonSerializer.Deserialize<AnalyzeDataExport>(json, JsonReadOptions);

        if (export == null)
            throw new InvalidOperationException("Failed to deserialize analyze data file.");

        if (export.Version > 1)
            wizard.Warn($"Warning: Data file version {export.Version} is newer than supported (1). Some fields may be ignored.");

        // Print metadata
        wizard.Info($"  Server:    {export.ServerVersion}");
        wizard.Info($"  Database:  {export.DatabaseName}");
        wizard.Info($"  Generated: {export.GeneratedUtc:u}");
        wizard.Info($"  Params:    --days {export.Parameters.Days} --min-executions {export.Parameters.MinExecutions}");
        wizard.Info("");

        // Convert export views to dependency rows for filtering
        var depResults = export.Views;

        // Apply filter
        if (!string.IsNullOrEmpty(options.Filter))
        {
            var filterRegex = ValidateSession.BuildFilterRegex(options.Filter);
            depResults = depResults
                .Where(r => filterRegex.IsMatch($"{r.SchemaName}.{r.ViewName}"))
                .ToList();
            wizard.Info($"After filter: {depResults.Count} views.");
        }

        wizard.Info($"Found {depResults.Count} views with nested view references.");

        // Match Query Store stats
        var queryStoreAvailable = export.QueryStoreEnabled && export.QueryStoreStats.Count > 0;
        var queryStoreStats = new Dictionary<string, QueryStoreViewStats>(StringComparer.OrdinalIgnoreCase);

        if (queryStoreAvailable)
        {
            var viewNames = depResults.Select(v => v.ViewName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var queryRows = export.QueryStoreStats.Select(r => new QueryStoreRow
            {
                QuerySqlText = r.QuerySqlText,
                ExecutionCount = r.ExecutionCount,
                AvgDurationUs = r.AvgDurationUs,
                MaxDurationUs = r.MaxDurationUs,
                TotalCpuUs = r.TotalCpuUs,
                TotalLogicalReads = r.TotalLogicalReads,
            }).ToList();
            queryStoreStats = MatchQueryStoreToViews(viewNames, queryRows);
            wizard.Info($"Matched Query Store stats for {queryStoreStats.Count} views.");
        }
        else
        {
            wizard.Warn("No Query Store data in export. Scoring will use dependency depth only.");
        }

        // Build results
        var results = new List<ViewAnalyzeResult>();

        foreach (var dep in depResults)
        {
            var result = new ViewAnalyzeResult
            {
                SchemaName = dep.SchemaName,
                ViewName = dep.ViewName,
                NestingDepth = dep.NestingDepth,
                DirectViewRefs = dep.DirectViewRefs,
                TransitiveViewCount = dep.TransitiveViewCount,
                IsInlined = dep.IsInlined,
            };

            // Apply Query Store stats
            if (queryStoreStats.TryGetValue(dep.ViewName, out var stats))
            {
                result.ExecutionCount = stats.ExecutionCount;
                result.AvgDurationMs = stats.AvgDurationUs / 1000.0;
                result.MaxDurationMs = stats.MaxDurationUs / 1000.0;
                result.TotalCpuMs = stats.TotalCpuUs / 1000.0;
                result.TotalLogicalReadsK = stats.TotalLogicalReads / 1000.0;
            }

            result.Score = ComputeScore(result);
            results.Add(result);
        }

        results = results.OrderByDescending(r => r.Score).ToList();

        sw.Stop();
        wizard.Info($"Analysis complete in {FormatElapsed(sw.Elapsed)}.");
        wizard.Info("");

        // Display results (totalViews = export.Views.Count for skip summary)
        ScoreAndDisplay(results, queryStoreAvailable, options, export.Views.Count);

        return results;
    }

    /// <summary>
    /// Generates a self-documented SQL Server stored procedure for extracting analyze data.
    /// </summary>
    public static string GenerateScript(int defaultDays = 30, int defaultMinExecutions = 5)
    {
        return $@"-- =============================================================================
-- SqlInliner Analyze Data Extraction Script
-- =============================================================================
--
-- Purpose:
--   Extracts view dependency and Query Store data for offline analysis with
--   the sqlinliner analyze command. Run this on your production or restored
--   database, save the output, then analyze without a live connection.
--
-- Usage:
--   1. Run this script in SSMS or sqlcmd to create the stored procedure
--   2. Execute the procedure to extract data:
--
--      -- Result sets (for SSMS grid view / copy-paste):
--      EXEC dbo.SqlInliner_ExtractAnalyzeData
--          @Days = {defaultDays}, @MinExecutions = {defaultMinExecutions}, @OutputFormat = 'ResultSets';
--
--      -- JSON output (for sqlinliner --from-file):
--      EXEC dbo.SqlInliner_ExtractAnalyzeData
--          @Days = {defaultDays}, @MinExecutions = {defaultMinExecutions}, @OutputFormat = 'Json';
--
--   3. Save JSON output to a file (e.g. analyze-data.json)
--   4. Run: sqlinliner analyze --from-file analyze-data.json
--
-- Parameters:
--   @Days           INT          Query Store lookback period (default: {defaultDays})
--   @MinExecutions  INT          Minimum execution count (default: {defaultMinExecutions})
--   @OutputFormat   VARCHAR(20)  'ResultSets' or 'Json' (default: 'ResultSets')
--
-- Output (ResultSets mode):
--   Result Set 1: View dependencies (SchemaName, ViewName, NestingDepth, DirectViewRefs, TransitiveViewCount, IsInlined)
--   Result Set 2: Query Store stats (QuerySqlText, ExecutionCount, AvgDurationUs, MaxDurationUs, TotalCpuUs, TotalLogicalReads)
--   Result Set 3: Metadata (ServerVersion, DatabaseName, QueryStoreEnabled)
--
-- Output (Json mode):
--   Single NVARCHAR(MAX) column with JSON envelope matching sqlinliner --from-file format
--
-- Safety:
--   Read-only. Only queries sys.* catalog views and OBJECT_DEFINITION().
--   No data modifications, no locks, no temp table DDL outside this session.
-- =============================================================================

IF OBJECT_ID('dbo.SqlInliner_ExtractAnalyzeData', 'P') IS NOT NULL
    DROP PROCEDURE dbo.SqlInliner_ExtractAnalyzeData;
GO

CREATE PROCEDURE dbo.SqlInliner_ExtractAnalyzeData
    @Days INT = {defaultDays},
    @MinExecutions INT = {defaultMinExecutions},
    @OutputFormat VARCHAR(20) = 'ResultSets'
AS
BEGIN
    SET NOCOUNT ON;

    -- =========================================================================
    -- Step 1: Collect view dependencies
    -- =========================================================================
    PRINT 'Step 1: Analyzing view dependencies...';

    CREATE TABLE #ViewDeps (
        SchemaName NVARCHAR(128),
        ViewName NVARCHAR(128),
        NestingDepth INT,
        DirectViewRefs INT,
        TransitiveViewCount INT,
        IsInlined BIT
    );

    ;WITH ViewDeps AS (
        SELECT DISTINCT d.referencing_id AS parent_id, d.referenced_id AS child_id
        FROM sys.sql_expression_dependencies d
        WHERE d.referenced_id IS NOT NULL
          AND OBJECTPROPERTY(d.referencing_id, 'IsView') = 1
          AND OBJECTPROPERTY(d.referenced_id, 'IsView') = 1
          AND d.referencing_id != d.referenced_id
    ),
    RecursiveDeps AS (
        SELECT parent_id AS root_id, child_id, 1 AS depth FROM ViewDeps
        UNION ALL
        SELECT rd.root_id, vd.child_id, rd.depth + 1
        FROM RecursiveDeps rd
        JOIN ViewDeps vd ON rd.child_id = vd.parent_id
        WHERE rd.depth < 20 AND vd.child_id != rd.root_id
    )
    INSERT INTO #ViewDeps (SchemaName, ViewName, NestingDepth, DirectViewRefs, TransitiveViewCount, IsInlined)
    SELECT
        SCHEMA_NAME(v.schema_id) AS SchemaName,
        v.name AS ViewName,
        ISNULL(MAX(rd.depth), 0) AS NestingDepth,
        (SELECT COUNT(DISTINCT child_id) FROM ViewDeps WHERE parent_id = v.object_id) AS DirectViewRefs,
        ISNULL(COUNT(DISTINCT rd.child_id), 0) AS TransitiveViewCount,
        CASE WHEN CHARINDEX('{DatabaseView.BeginOriginal}', ISNULL(OBJECT_DEFINITION(v.object_id), '')) > 0
             THEN 1 ELSE 0 END AS IsInlined
    FROM sys.views v
    LEFT JOIN RecursiveDeps rd ON rd.root_id = v.object_id
    WHERE v.object_id NOT IN (SELECT object_id FROM sys.indexes)
    GROUP BY v.object_id, v.schema_id, v.name
    HAVING ISNULL(MAX(rd.depth), 0) > 0
    OPTION (MAXRECURSION 20);

    PRINT '  Found ' + CAST((SELECT COUNT(*) FROM #ViewDeps) AS VARCHAR(10)) + ' views with nested view references.';

    -- =========================================================================
    -- Step 2: Collect Query Store statistics
    -- =========================================================================
    PRINT 'Step 2: Checking Query Store availability...';

    DECLARE @QueryStoreEnabled BIT = 0;
    BEGIN TRY
        SELECT @QueryStoreEnabled = CASE WHEN actual_state IN (1, 2) THEN 1 ELSE 0 END
        FROM sys.database_query_store_options;
    END TRY
    BEGIN CATCH
        SET @QueryStoreEnabled = 0;
    END CATCH;

    CREATE TABLE #QueryStoreStats (
        QuerySqlText NVARCHAR(MAX),
        ExecutionCount BIGINT,
        AvgDurationUs FLOAT,
        MaxDurationUs FLOAT,
        TotalCpuUs FLOAT,
        TotalLogicalReads FLOAT
    );

    IF @QueryStoreEnabled = 1
    BEGIN
        PRINT '  Query Store is enabled. Collecting stats for last ' + CAST(@Days AS VARCHAR(10)) + ' days...';

        INSERT INTO #QueryStoreStats (QuerySqlText, ExecutionCount, AvgDurationUs, MaxDurationUs, TotalCpuUs, TotalLogicalReads)
        SELECT
            qt.query_sql_text AS QuerySqlText,
            SUM(rs.count_executions) AS ExecutionCount,
            AVG(rs.avg_duration) AS AvgDurationUs,
            MAX(rs.max_duration) AS MaxDurationUs,
            SUM(CAST(rs.avg_cpu_time AS FLOAT) * rs.count_executions) AS TotalCpuUs,
            SUM(CAST(rs.avg_logical_io_reads AS FLOAT) * rs.count_executions) AS TotalLogicalReads
        FROM sys.query_store_runtime_stats rs
        JOIN sys.query_store_plan p ON rs.plan_id = p.plan_id
        JOIN sys.query_store_query q ON p.query_id = q.query_id
        JOIN sys.query_store_query_text qt ON q.query_text_id = qt.query_text_id
        JOIN sys.query_store_runtime_stats_interval rsi ON rs.runtime_stats_interval_id = rsi.runtime_stats_interval_id
        WHERE rsi.start_time >= DATEADD(DAY, -@Days, GETUTCDATE())
        GROUP BY qt.query_sql_text
        HAVING SUM(rs.count_executions) >= @MinExecutions;

        PRINT '  Collected ' + CAST((SELECT COUNT(*) FROM #QueryStoreStats) AS VARCHAR(10)) + ' query groups.';
    END
    ELSE
    BEGIN
        PRINT '  Query Store is not enabled. Skipping.';
    END;

    -- =========================================================================
    -- Step 3: Return results
    -- =========================================================================
    IF LOWER(@OutputFormat) = 'json'
    BEGIN
        PRINT 'Step 3: Generating JSON output...';

        DECLARE @Json NVARCHAR(MAX);
        DECLARE @ViewsJson NVARCHAR(MAX);
        DECLARE @StatsJson NVARCHAR(MAX);

        -- Build views JSON
        SELECT @ViewsJson = ISNULL((
            SELECT SchemaName AS schemaName, ViewName AS viewName,
                   NestingDepth AS nestingDepth, DirectViewRefs AS directViewRefs,
                   TransitiveViewCount AS transitiveViewCount,
                   CAST(IsInlined AS BIT) AS isInlined
            FROM #ViewDeps
            ORDER BY NestingDepth DESC, TransitiveViewCount DESC
            FOR JSON PATH
        ), '[]');

        -- Build query store stats JSON
        SELECT @StatsJson = ISNULL((
            SELECT QuerySqlText AS querySqlText, ExecutionCount AS executionCount,
                   AvgDurationUs AS avgDurationUs, MaxDurationUs AS maxDurationUs,
                   TotalCpuUs AS totalCpuUs, TotalLogicalReads AS totalLogicalReads
            FROM #QueryStoreStats
            FOR JSON PATH
        ), '[]');

        -- Assemble full JSON envelope
        SET @Json = '{{' + CHAR(13) + CHAR(10)
            + '  ""version"": 1,' + CHAR(13) + CHAR(10)
            + '  ""generatedUtc"": ""' + CONVERT(VARCHAR(30), GETUTCDATE(), 127) + 'Z"",' + CHAR(13) + CHAR(10)
            + '  ""serverVersion"": ""' + REPLACE(@@VERSION, '""', '\""') + '"",' + CHAR(13) + CHAR(10)
            + '  ""databaseName"": ""' + DB_NAME() + '"",' + CHAR(13) + CHAR(10)
            + '  ""parameters"": {{ ""days"": ' + CAST(@Days AS VARCHAR(10)) + ', ""minExecutions"": ' + CAST(@MinExecutions AS VARCHAR(10)) + ' }},' + CHAR(13) + CHAR(10)
            + '  ""queryStoreEnabled"": ' + CASE WHEN @QueryStoreEnabled = 1 THEN 'true' ELSE 'false' END + ',' + CHAR(13) + CHAR(10)
            + '  ""views"": ' + @ViewsJson + ',' + CHAR(13) + CHAR(10)
            + '  ""queryStoreStats"": ' + @StatsJson + CHAR(13) + CHAR(10)
            + '}}';

        SELECT @Json AS JsonOutput;
    END
    ELSE
    BEGIN
        PRINT 'Step 3: Returning result sets...';

        -- Result Set 1: View dependencies
        SELECT SchemaName, ViewName, NestingDepth, DirectViewRefs, TransitiveViewCount, IsInlined
        FROM #ViewDeps
        ORDER BY NestingDepth DESC, TransitiveViewCount DESC;

        -- Result Set 2: Query Store stats
        SELECT QuerySqlText, ExecutionCount, AvgDurationUs, MaxDurationUs, TotalCpuUs, TotalLogicalReads
        FROM #QueryStoreStats;

        -- Result Set 3: Metadata
        SELECT
            @@VERSION AS ServerVersion,
            DB_NAME() AS DatabaseName,
            @QueryStoreEnabled AS QueryStoreEnabled,
            @Days AS Days,
            @MinExecutions AS MinExecutions;
    END;

    -- Cleanup
    DROP TABLE #ViewDeps;
    DROP TABLE #QueryStoreStats;

    PRINT 'Done.';
END;
GO

-- Quick test: run with defaults in ResultSets mode
-- EXEC dbo.SqlInliner_ExtractAnalyzeData;

-- Run in JSON mode for sqlinliner --from-file:
-- EXEC dbo.SqlInliner_ExtractAnalyzeData @OutputFormat = 'Json';
";
    }

    // --- Shared logic ---

    /// <summary>
    /// Matches Query Store rows to view names using regex and aggregates per-view stats.
    /// Used by both Run() (live) and RunFromFile() (offline).
    /// </summary>
    internal static Dictionary<string, QueryStoreViewStats> MatchQueryStoreToViews(
        List<string> viewNames, List<QueryStoreRow> queryRows)
    {
        if (queryRows.Count == 0 || viewNames.Count == 0)
            return new Dictionary<string, QueryStoreViewStats>(StringComparer.OrdinalIgnoreCase);

        var escapedNames = viewNames.Select(Regex.Escape);
        var pattern = new Regex(@"\b(" + string.Join("|", escapedNames) + @")\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var statsDict = new Dictionary<string, QueryStoreViewStats>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in queryRows)
        {
            if (string.IsNullOrEmpty(row.QuerySqlText))
                continue;

            var matches = pattern.Matches(row.QuerySqlText);
            var matchedViews = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in matches)
                matchedViews.Add(m.Value);

            foreach (var viewName in matchedViews)
            {
                if (!statsDict.TryGetValue(viewName, out var stats))
                {
                    stats = new QueryStoreViewStats();
                    statsDict[viewName] = stats;
                }

                stats.ExecutionCount += row.ExecutionCount;
                stats.AvgDurationUs = stats.AvgDurationUs == 0
                    ? row.AvgDurationUs
                    : (stats.AvgDurationUs + row.AvgDurationUs) / 2; // running average
                stats.MaxDurationUs = Math.Max(stats.MaxDurationUs, row.MaxDurationUs);
                stats.TotalCpuUs += row.TotalCpuUs;
                stats.TotalLogicalReads += row.TotalLogicalReads;
            }
        }

        return statsDict;
    }

    /// <summary>
    /// Scores results and displays candidates/already-inlined tables.
    /// Used by both Run() and RunFromFile().
    /// </summary>
    private void ScoreAndDisplay(List<ViewAnalyzeResult> results, bool queryStoreAvailable,
        AnalyzeSessionOptions options, int totalViews)
    {
        var candidates = results.Where(r => !r.IsInlined).ToList();
        var alreadyInlined = results.Where(r => r.IsInlined).ToList();

        if (options.Top.HasValue)
            candidates = candidates.Take(options.Top.Value).ToList();

        // Candidates table
        if (candidates.Count > 0)
        {
            wizard.Info($"=== Candidates ({candidates.Count}) ===");
            wizard.Info("");

            var headers = queryStoreAvailable
                ? new[] { "#", "View", "Depth", "Views", "Execs", "Avg(ms)", "Max(ms)", "CPU(s)", "Reads(K)", "Score" }
                : new[] { "#", "View", "Depth", "Views", "Score" };

            var rows = candidates.Select((r, i) =>
            {
                if (queryStoreAvailable)
                {
                    return new[]
                    {
                        (i + 1).ToString(),
                        r.FullName,
                        r.NestingDepth.ToString(),
                        r.TransitiveViewCount.ToString(),
                        r.ExecutionCount > 0 ? r.ExecutionCount.ToString("N0") : "-",
                        r.AvgDurationMs > 0 ? r.AvgDurationMs.ToString("N1") : "-",
                        r.MaxDurationMs > 0 ? r.MaxDurationMs.ToString("N0") : "-",
                        r.TotalCpuMs > 0 ? (r.TotalCpuMs / 1000.0).ToString("N1") : "-",
                        r.TotalLogicalReadsK > 0 ? r.TotalLogicalReadsK.ToString("N0") : "-",
                        r.Score.ToString("N1"),
                    };
                }

                return new[]
                {
                    (i + 1).ToString(),
                    r.FullName,
                    r.NestingDepth.ToString(),
                    r.TransitiveViewCount.ToString(),
                    r.Score.ToString("N1"),
                };
            }).ToList();

            wizard.WriteTable(headers, rows);
        }
        else
        {
            wizard.Info("No candidates found (all nested views are already inlined or filtered out).");
        }

        // Already inlined table
        if (alreadyInlined.Count > 0)
        {
            wizard.Info("");
            wizard.Info($"=== Already Inlined ({alreadyInlined.Count}) ===");
            wizard.Info("");

            var headers = new[] { "View", "Depth", "Views" };
            var rows = alreadyInlined.Select(r => new[]
            {
                r.FullName,
                r.NestingDepth.ToString(),
                r.TransitiveViewCount.ToString(),
            }).ToList();

            wizard.WriteTable(headers, rows);
        }

        // Summary for views with no nesting
        var noNesting = totalViews - results.Count;
        if (noNesting > 0)
        {
            wizard.Info("");
            wizard.Info($"Skipped {noNesting} views with no nested view references.");
        }

        wizard.Info("");
    }

    // --- Database query methods ---

    private List<ViewDependencyRow> QueryViewDependencies()
    {
        const string sql = @"
;WITH ViewDeps AS (
    SELECT DISTINCT d.referencing_id AS parent_id, d.referenced_id AS child_id
    FROM sys.sql_expression_dependencies d
    WHERE d.referenced_id IS NOT NULL
      AND OBJECTPROPERTY(d.referencing_id, 'IsView') = 1
      AND OBJECTPROPERTY(d.referenced_id, 'IsView') = 1
      AND d.referencing_id != d.referenced_id
),
RecursiveDeps AS (
    SELECT parent_id AS root_id, child_id, 1 AS depth FROM ViewDeps
    UNION ALL
    SELECT rd.root_id, vd.child_id, rd.depth + 1
    FROM RecursiveDeps rd
    JOIN ViewDeps vd ON rd.child_id = vd.parent_id
    WHERE rd.depth < 20 AND vd.child_id != rd.root_id
)
SELECT
    SCHEMA_NAME(v.schema_id) AS SchemaName, v.name AS ViewName,
    ISNULL(MAX(rd.depth), 0) AS NestingDepth,
    (SELECT COUNT(DISTINCT child_id) FROM ViewDeps WHERE parent_id = v.object_id) AS DirectViewRefs,
    ISNULL(COUNT(DISTINCT rd.child_id), 0) AS TransitiveViewCount
FROM sys.views v
LEFT JOIN RecursiveDeps rd ON rd.root_id = v.object_id
WHERE v.object_id NOT IN (SELECT object_id FROM sys.indexes)
GROUP BY v.object_id, v.schema_id, v.name
HAVING ISNULL(MAX(rd.depth), 0) > 0
ORDER BY NestingDepth DESC, TransitiveViewCount DESC
OPTION (MAXRECURSION 20)";

        return connection!.Connection!.Query<ViewDependencyRow>(sql).ToList();
    }

    private bool IsQueryStoreEnabled()
    {
        try
        {
            var state = connection!.Connection!.QueryFirstOrDefault<int?>(
                "SELECT actual_state FROM sys.database_query_store_options");
            return state is 1 or 2; // READ_WRITE or READ_ONLY
        }
        catch
        {
            return false;
        }
    }

    private List<QueryStoreRow> QueryQueryStoreRows(int days, int minExecutions)
    {
        const string sql = @"
SELECT qt.query_sql_text AS QuerySqlText,
    SUM(rs.count_executions) AS ExecutionCount,
    AVG(rs.avg_duration) AS AvgDurationUs,
    MAX(rs.max_duration) AS MaxDurationUs,
    SUM(CAST(rs.avg_cpu_time AS FLOAT) * rs.count_executions) AS TotalCpuUs,
    SUM(CAST(rs.avg_logical_io_reads AS FLOAT) * rs.count_executions) AS TotalLogicalReads
FROM sys.query_store_runtime_stats rs
JOIN sys.query_store_plan p ON rs.plan_id = p.plan_id
JOIN sys.query_store_query q ON p.query_id = q.query_id
JOIN sys.query_store_query_text qt ON q.query_text_id = qt.query_text_id
JOIN sys.query_store_runtime_stats_interval rsi ON rs.runtime_stats_interval_id = rsi.runtime_stats_interval_id
WHERE rsi.start_time >= DATEADD(DAY, -@Days, GETUTCDATE())
GROUP BY qt.query_sql_text
HAVING SUM(rs.count_executions) >= @MinExecutions";

        return connection!.Connection!.Query<QueryStoreRow>(sql, new { Days = days, MinExecutions = minExecutions }).ToList();
    }

    internal static double ComputeScore(ViewAnalyzeResult result)
    {
        var depthScore = 3.0 * Math.Log(1 + result.NestingDepth, 2) * Math.Log(1 + result.TransitiveViewCount, 2);
        var execScore = 2.0 * Math.Log10(1 + result.ExecutionCount);
        var costScore = 1.0 * Math.Log10(1 + result.TotalCpuMs + result.TotalLogicalReadsK);
        return depthScore + execScore + costScore;
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalMinutes >= 1)
            return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
        return $"{elapsed.TotalSeconds:N1}s";
    }

    // --- Internal DTOs ---

    private sealed class ViewDependencyRow
    {
        public string SchemaName { get; set; } = "";
        public string ViewName { get; set; } = "";
        public int NestingDepth { get; set; }
        public int DirectViewRefs { get; set; }
        public int TransitiveViewCount { get; set; }
    }

    internal sealed class QueryStoreRow
    {
        public string? QuerySqlText { get; set; }
        public long ExecutionCount { get; set; }
        public double AvgDurationUs { get; set; }
        public double MaxDurationUs { get; set; }
        public double TotalCpuUs { get; set; }
        public double TotalLogicalReads { get; set; }
    }

    internal sealed class QueryStoreViewStats
    {
        public long ExecutionCount { get; set; }
        public double AvgDurationUs { get; set; }
        public double MaxDurationUs { get; set; }
        public double TotalCpuUs { get; set; }
        public double TotalLogicalReads { get; set; }
    }
}

#endif
