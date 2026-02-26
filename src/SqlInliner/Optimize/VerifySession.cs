#if !RELEASELIBRARY

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Data.SqlClient;

namespace SqlInliner.Optimize;

/// <summary>
/// Options for a verify run.
/// </summary>
public sealed class VerifySessionOptions
{
    /// <summary>Only process matching views (exact name or SQL LIKE-style % wildcard).</summary>
    public string? Filter { get; set; }

    /// <summary>Halt on first failure instead of continuing.</summary>
    public bool StopOnError { get; set; }

    /// <summary>Query timeout in seconds for COUNT and EXCEPT queries (default: 120).</summary>
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// Per-view verify status.
/// </summary>
public enum ViewVerifyStatus
{
    Pass,
    Skipped,
    DeployError,
    ValidationFail,
    Timeout,
    Exception,
}

/// <summary>
/// Result of verifying a single view.
/// </summary>
public sealed class ViewVerifyResult
{
    public string ViewName { get; set; } = "";
    public ViewVerifyStatus Status { get; set; }
    public TimeSpan Elapsed { get; set; }
    public long? OriginalRowCount { get; set; }
    public long? InlinedRowCount { get; set; }
    public long? OnlyInOriginal { get; set; }
    public long? OnlyInInlined { get; set; }
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Drives the verify workflow: auto-detect inlined views, extract embedded originals, deploy + compare, report summary.
/// </summary>
public sealed class VerifySession
{
    private readonly DatabaseConnection connection;
    private readonly IConsoleWizard wizard;

    public VerifySession(DatabaseConnection connection, IConsoleWizard wizard)
    {
        this.connection = connection;
        this.wizard = wizard;
    }

    /// <summary>
    /// Extracts the original SQL from between <see cref="DatabaseView.BeginOriginal"/> and <see cref="DatabaseView.EndOriginal"/> markers.
    /// Returns <c>null</c> if no markers are found.
    /// </summary>
    internal static string? ExtractOriginalSql(string rawDefinition)
    {
        var startIdx = rawDefinition.IndexOf(DatabaseView.BeginOriginal, StringComparison.Ordinal);
        if (startIdx < 0)
            return null;

        var endIdx = rawDefinition.IndexOf(DatabaseView.EndOriginal, StringComparison.Ordinal);
        if (endIdx < 0)
            return null;

        startIdx += DatabaseView.BeginOriginal.Length;
        if (startIdx >= endIdx)
            return null;

        return rawDefinition.Substring(startIdx, endIdx - startIdx).Trim();
    }

    /// <summary>
    /// Runs the verify workflow and returns per-view results.
    /// </summary>
    public List<ViewVerifyResult> Run(VerifySessionOptions sessionOptions)
    {
        var results = new List<ViewVerifyResult>();
        var totalSw = Stopwatch.StartNew();

        // Get all views sorted alphabetically
        var allViews = connection.Views
            .OrderBy(v => v.GetName(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Apply filter
        if (!string.IsNullOrEmpty(sessionOptions.Filter))
        {
            var filterRegex = ValidateSession.BuildFilterRegex(sessionOptions.Filter);
            allViews = allViews.Where(v => filterRegex.IsMatch(ValidateSession.StripBrackets(v.GetName()))).ToList();
        }

        // Build candidate list: views with markers
        var candidates = new List<(Microsoft.SqlServer.TransactSql.ScriptDom.SchemaObjectName ViewObject, string RawDefinition, string OriginalSql)>();
        var candidateBaseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var viewObject in allViews)
        {
            var viewName = viewObject.GetName();
            var rawDef = connection.TryGetRawViewDefinition(viewName);
            if (rawDef == null)
                continue;

            var originalSql = ExtractOriginalSql(rawDef);
            if (originalSql == null)
                continue;

            candidates.Add((viewObject, rawDef, originalSql));

            // Track base name (without _Inlined suffix)
            var baseName = viewObject.BaseIdentifier.Value;
            if (baseName.EndsWith("_Inlined", StringComparison.OrdinalIgnoreCase))
                baseName = baseName.Substring(0, baseName.Length - "_Inlined".Length);
            candidateBaseNames.Add(baseName);
        }

        // Filter out _Inlined companion views when the base view is also a candidate
        candidates = candidates.Where(c =>
        {
            var name = c.ViewObject.BaseIdentifier.Value;
            if (name.EndsWith("_Inlined", StringComparison.OrdinalIgnoreCase))
            {
                var baseName = name.Substring(0, name.Length - "_Inlined".Length);
                var schema = c.ViewObject.SchemaIdentifier?.Value ?? "dbo";
                // Check if the base view (same schema) is also in our candidate set
                return !candidates.Any(other =>
                    other.ViewObject.BaseIdentifier.Value.Equals(baseName, StringComparison.OrdinalIgnoreCase) &&
                    (other.ViewObject.SchemaIdentifier?.Value ?? "dbo").Equals(schema, StringComparison.OrdinalIgnoreCase));
            }
            return true;
        }).ToList();

        wizard.Info($"Views with markers: {candidates.Count}");
        wizard.Info("");

        for (var i = 0; i < candidates.Count; i++)
        {
            var (viewObject, rawDef, originalSql) = candidates[i];
            var viewName = viewObject.GetName();
            var result = new ViewVerifyResult { ViewName = viewName };
            var sw = Stopwatch.StartNew();

            wizard.Info($"[{i + 1}/{candidates.Count}] {viewName}...");

            try
            {
                var schema = viewObject.SchemaIdentifier?.Value ?? "dbo";
                var baseName = viewObject.BaseIdentifier.Value;
                var originalViewName = $"{baseName}_Original";

                // Prepare the original SQL as a CREATE OR ALTER VIEW with _Original suffix
                var createSql = DatabaseView.CreateOrAlter(originalSql);
                var renamedSql = OptimizeSession.RenameView(createSql, schema, originalViewName);

                // Deploy the original as a temp view
                connection.ExecuteNonQuery(renamedSql);

                try
                {
                    var queryRunner = new QueryRunner(connection, sessionOptions.TimeoutSeconds);

                    // COUNT inlined first — it should be faster (that's the point of inlining).
                    // If it succeeds but the original times out, we still learn something useful.
                    try
                    {
                        result.InlinedRowCount = queryRunner.GetRowCount(schema, baseName);
                    }
                    catch (SqlException ex) when (ex.Number == -2)
                    {
                        result.Status = ViewVerifyStatus.Timeout;
                        result.Errors.Add($"COUNT on inlined view timed out after {sessionOptions.TimeoutSeconds}s");
                    }

                    if (result.Status != ViewVerifyStatus.Timeout)
                    {
                        try
                        {
                            result.OriginalRowCount = queryRunner.GetRowCount(schema, originalViewName);
                        }
                        catch (SqlException ex) when (ex.Number == -2)
                        {
                            result.Status = ViewVerifyStatus.Timeout;
                            result.Errors.Add($"COUNT on original timed out after {sessionOptions.TimeoutSeconds}s (inlined count: {result.InlinedRowCount:N0})");
                        }
                    }

                    // EXCEPT comparison
                    if (result.Status != ViewVerifyStatus.Timeout)
                    {
                        try
                        {
                            var (onlyInOriginal, onlyInInlined) = queryRunner.RunExceptComparison(schema, originalViewName, baseName);
                            result.OnlyInOriginal = onlyInOriginal;
                            result.OnlyInInlined = onlyInInlined;
                        }
                        catch (SqlException ex) when (ex.Number == -2)
                        {
                            result.Status = ViewVerifyStatus.Timeout;
                            result.Errors.Add($"EXCEPT comparison timed out after {sessionOptions.TimeoutSeconds}s (counts: original={result.OriginalRowCount:N0} inlined={result.InlinedRowCount:N0})");
                        }
                    }

                    if (result.Status != ViewVerifyStatus.Timeout)
                    {
                        if (result.OriginalRowCount != result.InlinedRowCount || result.OnlyInOriginal != 0 || result.OnlyInInlined != 0)
                        {
                            result.Status = ViewVerifyStatus.ValidationFail;
                            result.Errors.Add($"Row count: original={result.OriginalRowCount:N0} inlined={result.InlinedRowCount:N0}, EXCEPT: {result.OnlyInOriginal}/{result.OnlyInInlined}");
                        }
                        else
                        {
                            result.Status = ViewVerifyStatus.Pass;
                        }
                    }
                }
                finally
                {
                    // Always drop the _Original view
                    try
                    {
                        connection.ExecuteNonQuery($"DROP VIEW [{schema}].[{originalViewName}]");
                    }
                    catch
                    {
                        // Best effort cleanup
                    }
                }
            }
            catch (SqlException ex) when (ex.Number == -2)
            {
                result.Status = ViewVerifyStatus.Timeout;
                result.Errors.Add($"Query timed out after {sessionOptions.TimeoutSeconds}s");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("No database connection"))
            {
                // No DB connection (e.g. unit tests with parameterless DatabaseConnection)
                result.Status = ViewVerifyStatus.Exception;
                result.Errors.Add(ex.Message);
            }
            catch (Exception ex)
            {
                result.Status = ViewVerifyStatus.DeployError;
                result.Errors.Add(ex.Message);
            }

            result.Elapsed = sw.Elapsed;
            results.Add(result);

            PrintViewStatus(result);

            // Stop on error if requested (timeouts don't halt)
            if (sessionOptions.StopOnError && IsFailure(result.Status) && result.Status != ViewVerifyStatus.Timeout)
            {
                wizard.Error("Stopping due to --stop-on-error.");
                break;
            }
        }

        totalSw.Stop();
        PrintSummary(results, totalSw.Elapsed);

        return results;
    }

    private void PrintViewStatus(ViewVerifyResult result)
    {
        var elapsed = result.Elapsed.TotalSeconds < 1
            ? $"{result.Elapsed.TotalMilliseconds:N0}ms"
            : $"{result.Elapsed.TotalSeconds:N1}s";

        var detail = result.Status switch
        {
            ViewVerifyStatus.Pass => $"rows={result.InlinedRowCount:N0}",
            ViewVerifyStatus.Skipped => "no markers",
            _ => result.Errors.Count > 0 ? result.Errors[0] : "",
        };

        if (detail.Length > 100)
            detail = detail.Substring(0, 97) + "...";

        var line = $"  {result.Status,-20} ({elapsed}) {detail}";

        if (IsFailure(result.Status))
            wizard.Error(line);
        else if (result.Status == ViewVerifyStatus.Timeout)
            wizard.Warn(line);
        else if (result.Status == ViewVerifyStatus.Skipped)
            wizard.Info(line);
        else
            wizard.Success(line);
    }

    private void PrintSummary(List<ViewVerifyResult> results, TimeSpan totalElapsed)
    {
        wizard.Info("");
        wizard.Info("=== Verify Summary ===");
        wizard.Info($"Total: {results.Count} views in {FormatElapsed(totalElapsed)}");
        wizard.Info("");

        var statusGroups = results
            .GroupBy(r => r.Status)
            .OrderBy(g => g.Key)
            .Select(g => new[] { g.Key.ToString(), g.Count().ToString() })
            .ToList();

        wizard.WriteTable(new[] { "Status", "Count" }, statusGroups);

        var failures = results.Where(r => IsFailure(r.Status)).ToList();
        if (failures.Count > 0)
        {
            wizard.Info("");
            wizard.Error($"--- {failures.Count} Failure(s) ---");
            foreach (var f in failures)
            {
                var errorMsg = f.Errors.Count > 0 ? f.Errors[0] : "";
                if (errorMsg.Length > 120)
                    errorMsg = errorMsg.Substring(0, 117) + "...";
                wizard.Error($"  {f.ViewName} ({f.Status}): {errorMsg}");
            }
        }

        var timeouts = results.Where(r => r.Status == ViewVerifyStatus.Timeout).ToList();
        if (timeouts.Count > 0)
        {
            wizard.Info("");
            wizard.Warn($"--- {timeouts.Count} Timeout(s) ---");
            foreach (var t in timeouts)
                wizard.Warn($"  {t.ViewName}");
        }

        wizard.Info("");
    }

    internal static bool IsFailure(ViewVerifyStatus status)
    {
        return status is ViewVerifyStatus.DeployError
            or ViewVerifyStatus.ValidationFail
            or ViewVerifyStatus.Exception;
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalMinutes >= 1)
            return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
        return $"{elapsed.TotalSeconds:N1}s";
    }
}

#endif
