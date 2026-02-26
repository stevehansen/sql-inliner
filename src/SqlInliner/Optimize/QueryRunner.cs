#if !RELEASELIBRARY

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace SqlInliner.Optimize;

/// <summary>
/// Per-table IO statistics from SET STATISTICS IO.
/// </summary>
public sealed class TableIOStats
{
    public string TableName { get; set; } = "";
    public long ScanCount { get; set; }
    public long LogicalReads { get; set; }
    public long PhysicalReads { get; set; }
    public long ReadAheadReads { get; set; }
}

/// <summary>
/// Results from a benchmark run.
/// </summary>
public sealed class BenchmarkResult
{
    public long CpuTimeMs { get; set; }
    public long ElapsedTimeMs { get; set; }
    public long LogicalReads { get; set; }
    public List<TableIOStats> TableStats { get; set; } = new();
    public string? ExecutionPlanXml { get; set; }
}

/// <summary>
/// A single wait type entry from an execution plan's WaitStats.
/// </summary>
public sealed class WaitStatEntry
{
    public string WaitType { get; set; } = "";
    public long WaitTimeMs { get; set; }
    public long WaitCount { get; set; }
}

/// <summary>
/// Key metrics extracted from a SQL Server execution plan XML (.sqlplan).
/// </summary>
public sealed class ExecutionPlanSummary
{
    public double EstimatedCost { get; set; }
    public double EstimatedRows { get; set; }
    public int DegreeOfParallelism { get; set; }
    public long MemoryGrantKB { get; set; }
    public long MaxUsedMemoryKB { get; set; }
    public long CachedPlanSizeKB { get; set; }
    public long CompileTimeMs { get; set; }
    public long CompileCpuMs { get; set; }
    public long CompileMemoryKB { get; set; }
    public string? OptimizationLevel { get; set; }
    public string? EarlyAbortReason { get; set; }
    public int CardinalityEstimatorVersion { get; set; }
    public List<WaitStatEntry> WaitStats { get; set; } = new();
    public int TotalOperators { get; set; }

    /// <summary>
    /// Attempts to parse key metrics from a SQL Server execution plan XML string.
    /// Returns null if the XML is null/empty or cannot be parsed.
    /// </summary>
    public static ExecutionPlanSummary? TryParse(string? xml)
    {
        if (string.IsNullOrEmpty(xml))
            return null;

        try
        {
            var doc = XDocument.Parse(xml);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

            var stmt = doc.Descendants(ns + "StmtSimple").FirstOrDefault();
            var queryPlan = doc.Descendants(ns + "QueryPlan").FirstOrDefault();
            var memGrant = doc.Descendants(ns + "MemoryGrantInfo").FirstOrDefault();

            if (stmt == null || queryPlan == null)
                return null;

            var summary = new ExecutionPlanSummary
            {
                EstimatedCost = ParseDouble(stmt, "StatementSubTreeCost"),
                EstimatedRows = ParseDouble(stmt, "StatementEstRows"),
                OptimizationLevel = (string?)stmt.Attribute("StatementOptmLevel"),
                EarlyAbortReason = (string?)stmt.Attribute("StatementOptmEarlyAbortReason"),
                CardinalityEstimatorVersion = ParseInt(stmt, "CardinalityEstimationModelVersion"),

                DegreeOfParallelism = ParseInt(queryPlan, "DegreeOfParallelism"),
                MemoryGrantKB = ParseLong(queryPlan, "MemoryGrant"),
                CachedPlanSizeKB = ParseLong(queryPlan, "CachedPlanSize"),
                CompileTimeMs = ParseLong(queryPlan, "CompileTime"),
                CompileCpuMs = ParseLong(queryPlan, "CompileCPU"),
                CompileMemoryKB = ParseLong(queryPlan, "CompileMemory"),
            };

            if (memGrant != null)
            {
                summary.MaxUsedMemoryKB = ParseLong(memGrant, "MaxUsedMemory");
                // Use GrantedMemory if MemoryGrant wasn't on QueryPlan
                if (summary.MemoryGrantKB == 0)
                    summary.MemoryGrantKB = ParseLong(memGrant, "GrantedMemory");
            }

            // Wait statistics
            foreach (var wait in doc.Descendants(ns + "Wait"))
            {
                summary.WaitStats.Add(new WaitStatEntry
                {
                    WaitType = (string?)wait.Attribute("WaitType") ?? "",
                    WaitTimeMs = ParseLong(wait, "WaitTimeMs"),
                    WaitCount = ParseLong(wait, "WaitCount"),
                });
            }

            // Count total operators (RelOp nodes)
            summary.TotalOperators = doc.Descendants(ns + "RelOp").Count();

            return summary;
        }
        catch
        {
            return null;
        }
    }

    private static double ParseDouble(XElement el, string attr)
    {
        var val = (string?)el.Attribute(attr);
        return val != null && double.TryParse(val, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    private static int ParseInt(XElement el, string attr)
    {
        var val = (string?)el.Attribute(attr);
        return val != null && int.TryParse(val, out var i) ? i : 0;
    }

    private static long ParseLong(XElement el, string attr)
    {
        var val = (string?)el.Attribute(attr);
        return val != null && long.TryParse(val, out var l) ? l : 0;
    }
}

/// <summary>
/// Executes validation and benchmark queries against the database.
/// </summary>
public sealed class QueryRunner
{
    private readonly DatabaseConnection connection;
    private readonly int commandTimeoutSeconds;

    public QueryRunner(DatabaseConnection connection, int commandTimeoutSeconds = 600)
    {
        this.connection = connection;
        this.commandTimeoutSeconds = commandTimeoutSeconds;
    }

    /// <summary>
    /// Returns the row count for the specified view.
    /// </summary>
    public long GetRowCount(string schema, string viewName)
    {
        var sql = $"SELECT COUNT_BIG(*) FROM [{schema}].[{viewName}]";
        return ExecuteScalarWithTimeout<long>(sql);
    }

    /// <summary>
    /// Runs EXCEPT in both directions to find row differences between two views.
    /// Returns (onlyInOriginal, onlyInInlined).
    /// </summary>
    public (long OnlyInOriginal, long OnlyInInlined) RunExceptComparison(string schema, string originalView, string inlinedView)
    {
        var sqlOriginalOnly = $@"
SELECT COUNT_BIG(*) FROM (
    SELECT * FROM [{schema}].[{originalView}]
    EXCEPT
    SELECT * FROM [{schema}].[{inlinedView}]
) diff";

        var sqlInlinedOnly = $@"
SELECT COUNT_BIG(*) FROM (
    SELECT * FROM [{schema}].[{inlinedView}]
    EXCEPT
    SELECT * FROM [{schema}].[{originalView}]
) diff";

        var onlyInOriginal = ExecuteScalarWithTimeout<long>(sqlOriginalOnly);
        var onlyInInlined = ExecuteScalarWithTimeout<long>(sqlInlinedOnly);
        return (onlyInOriginal, onlyInInlined);
    }

    /// <summary>
    /// Runs a SELECT with SET STATISTICS TIME/IO ON and parses the results from InfoMessage events.
    /// </summary>
    public BenchmarkResult RunBenchmark(string schema, string viewName)
    {
        var sqlConnection = connection.Connection as SqlConnection
            ?? throw new InvalidOperationException("Benchmark requires a SqlConnection.");

        var result = new BenchmarkResult();
        var messages = new List<string>();

        void OnInfoMessage(object sender, SqlInfoMessageEventArgs e)
        {
            messages.Add(e.Message);
        }

        sqlConnection.InfoMessage += OnInfoMessage;
        try
        {
            // SET LANGUAGE ensures statistics messages are in English regardless of server locale
            // SET STATISTICS XML returns the actual execution plan as an additional result set
            var sql = $@"
SET LANGUAGE us_english;
SET STATISTICS TIME ON;
SET STATISTICS IO ON;
SET STATISTICS XML ON;
SELECT * FROM [{schema}].[{viewName}];
SET STATISTICS XML OFF;
SET STATISTICS TIME OFF;
SET STATISTICS IO OFF;";

            using var cmd = sqlConnection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = commandTimeoutSeconds;

            using var reader = cmd.ExecuteReader();
            while (reader.Read()) { } // consume data rows

            // STATISTICS XML returns the execution plan as an additional result set with a single XML column
            if (reader.NextResult() && reader.Read())
                result.ExecutionPlanXml = reader.GetString(0);

            while (reader.NextResult()) { } // advance past remaining result sets to flush statistics messages
        }
        finally
        {
            sqlConnection.InfoMessage -= OnInfoMessage;
        }

        // Parse statistics from messages
        foreach (var msg in messages)
        {
            ParseStatisticsTime(msg, result);
            ParseStatisticsIO(msg, result);
        }

        return result;
    }

    private static void ParseStatisticsTime(string message, BenchmarkResult result)
    {
        // Example: "SQL Server Execution Times:\n   CPU time = 123 ms,  elapsed time = 456 ms."
        var cpuMatch = Regex.Match(message, @"CPU time\s*=\s*(\d+)\s*ms");
        var elapsedMatch = Regex.Match(message, @"elapsed time\s*=\s*(\d+)\s*ms");

        if (cpuMatch.Success)
            result.CpuTimeMs = Math.Max(result.CpuTimeMs, long.Parse(cpuMatch.Groups[1].Value));
        if (elapsedMatch.Success)
            result.ElapsedTimeMs = Math.Max(result.ElapsedTimeMs, long.Parse(elapsedMatch.Groups[1].Value));
    }

    private static void ParseStatisticsIO(string message, BenchmarkResult result)
    {
        // Example: "Table 'People'. Scan count 1, logical reads 234, physical reads 0, read-ahead reads 0, ..."
        var match = Regex.Match(message,
            @"Table '(?<table>[^']+)'.*?" +
            @"Scan count (?<scan>\d+).*?" +
            @"logical reads (?<logical>\d+).*?" +
            @"physical reads (?<physical>\d+).*?" +
            @"read-ahead reads (?<readahead>\d+)");

        if (match.Success)
        {
            var stats = new TableIOStats
            {
                TableName = match.Groups["table"].Value,
                ScanCount = long.Parse(match.Groups["scan"].Value),
                LogicalReads = long.Parse(match.Groups["logical"].Value),
                PhysicalReads = long.Parse(match.Groups["physical"].Value),
                ReadAheadReads = long.Parse(match.Groups["readahead"].Value),
            };

            result.TableStats.Add(stats);
            result.LogicalReads += stats.LogicalReads;
        }
    }

    /// <summary>
    /// Returns the SQL Server version string and current database name.
    /// </summary>
    public (string Version, string Database) GetServerInfo()
    {
        var version = ExecuteScalarWithTimeout<string>("SELECT @@VERSION");
        var database = ExecuteScalarWithTimeout<string>("SELECT DB_NAME()");
        return (version, database);
    }

    private T ExecuteScalarWithTimeout<T>(string sql)
    {
        if (connection.Connection == null)
            throw new InvalidOperationException("No database connection available.");

        using var cmd = connection.Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = commandTimeoutSeconds;
        var result = cmd.ExecuteScalar();
        return (T)Convert.ChangeType(result!, typeof(T));
    }
}

#endif
