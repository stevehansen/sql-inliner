#if !RELEASELIBRARY

using System;
using System.Collections.Generic;
using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SqlInliner.Optimize;

/// <summary>
/// Results from a benchmark run.
/// </summary>
public sealed class BenchmarkResult
{
    public long CpuTimeMs { get; set; }
    public long ElapsedTimeMs { get; set; }
    public long LogicalReads { get; set; }
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
            var sql = $@"
SET STATISTICS TIME ON;
SET STATISTICS IO ON;
SELECT * FROM [{schema}].[{viewName}];
SET STATISTICS TIME OFF;
SET STATISTICS IO OFF;";

            using var cmd = sqlConnection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = commandTimeoutSeconds;

            using var reader = cmd.ExecuteReader();
            while (reader.Read()) { } // consume all rows
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
        // Example: "Table 'People'. Scan count 1, logical reads 234, ..."
        var match = Regex.Match(message, @"logical reads\s+(\d+)");
        if (match.Success)
            result.LogicalReads += long.Parse(match.Groups[1].Value);
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
