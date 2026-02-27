#if !RELEASELIBRARY

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlInliner.Optimize;

/// <summary>
/// Options for a batch-validation run.
/// </summary>
public sealed class ValidateSessionOptions
{
    /// <summary>Deploy each inlined view and run COUNT + EXCEPT validation.</summary>
    public bool Deploy { get; set; }

    /// <summary>Deploy each inlined view to check for SQL errors, but skip COUNT + EXCEPT comparison.</summary>
    public bool DeployOnly { get; set; }

    /// <summary>Save inlined SQL files to this directory.</summary>
    public string? OutputDir { get; set; }

    /// <summary>Halt on first failure instead of continuing.</summary>
    public bool StopOnError { get; set; }

    /// <summary>Only process matching views (exact name or SQL LIKE-style % wildcard).</summary>
    public string? Filter { get; set; }

    /// <summary>Query timeout in seconds for COUNT and EXCEPT queries (default: 90).</summary>
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>Whether any deploy mode is enabled.</summary>
    internal bool AnyDeploy => Deploy || DeployOnly;
}

/// <summary>
/// Per-view validation status.
/// </summary>
public enum ViewValidateStatus
{
    Pass,
    PassWithWarnings,
    Skipped,
    InlineError,
    ParseError,
    DeployError,
    ValidationFail,
    Timeout,
    Exception,
}

/// <summary>
/// Result of validating a single view.
/// </summary>
public sealed class ViewValidateResult
{
    public string ViewName { get; set; } = "";
    public ViewValidateStatus Status { get; set; }
    public TimeSpan Elapsed { get; set; }
    public int NestedViewCount { get; set; }
    public int ColumnsStripped { get; set; }
    public int JoinsStripped { get; set; }
    public int DerivedTablesFlattened { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public long? OriginalRowCount { get; set; }
    public long? InlinedRowCount { get; set; }
    public long? OnlyInOriginal { get; set; }
    public long? OnlyInInlined { get; set; }
}

/// <summary>
/// Drives the batch-validation workflow: iterate all views, inline each, optionally deploy + compare, report summary.
/// </summary>
public sealed class ValidateSession
{
    private readonly DatabaseConnection connection;
    private readonly InlinerOptions options;
    private readonly IConsoleWizard wizard;

    public ValidateSession(DatabaseConnection connection, InlinerOptions options, IConsoleWizard wizard)
    {
        this.connection = connection;
        this.options = options;
        this.wizard = wizard;
    }

    /// <summary>
    /// Runs the batch validation and returns per-view results.
    /// </summary>
    public List<ViewValidateResult> Run(ValidateSessionOptions sessionOptions)
    {
        var results = new List<ViewValidateResult>();
        var totalSw = Stopwatch.StartNew();

        // Get all views sorted alphabetically
        var allViews = connection.Views
            .OrderBy(v => v.GetName(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Apply filter
        if (!string.IsNullOrEmpty(sessionOptions.Filter))
        {
            var filterRegex = BuildFilterRegex(sessionOptions.Filter);
            allViews = allViews.Where(v => filterRegex.IsMatch(StripBrackets(v.GetName()))).ToList();
        }

        // Prepare output directory
        if (sessionOptions.OutputDir != null)
            Directory.CreateDirectory(sessionOptions.OutputDir);

        wizard.Info($"Options: {options.ToMetadataString()}");
        wizard.Info($"Views to process: {allViews.Count}");
        if (sessionOptions.Deploy)
            wizard.Info("Deploy + compare: enabled");
        else if (sessionOptions.DeployOnly)
            wizard.Info("Deploy only (no COUNT/EXCEPT): enabled");
        wizard.Info("");

        // Phase 1: Inline all views (fast pass)
        var inlineResults = new List<(SchemaObjectName ViewObject, ViewValidateResult Result, string? ConvertedSql)>();

        for (var i = 0; i < allViews.Count; i++)
        {
            var viewObject = allViews[i];
            var viewName = viewObject.GetName();
            var result = new ViewValidateResult { ViewName = viewName };
            var sw = Stopwatch.StartNew();
            string? convertedSql = null;

            try
            {
                var viewSql = connection.GetViewDefinition(viewName);
                viewSql = DatabaseView.CreateOrAlter(viewSql);

                var inliner = new DatabaseViewInliner(connection, viewSql, options);

                if (inliner.Errors.Count > 0)
                {
                    result.Status = ViewValidateStatus.InlineError;
                    result.Errors.AddRange(inliner.Errors);
                }
                else if (inliner.Result == null)
                {
                    result.Status = ViewValidateStatus.Skipped;
                }
                else
                {
                    result.NestedViewCount = inliner.Result.KnownViews.Count - 1; // exclude self
                    result.ColumnsStripped = inliner.TotalSelectColumnsStripped;
                    result.JoinsStripped = inliner.TotalJoinsStripped;
                    result.DerivedTablesFlattened = inliner.TotalDerivedTablesFlattened;
                    result.Warnings.AddRange(inliner.Warnings);

                    result.Status = inliner.Warnings.Count > 0
                        ? ViewValidateStatus.PassWithWarnings
                        : ViewValidateStatus.Pass;

                    convertedSql = inliner.Result.ConvertedSql;

                    // Save to output directory
                    if (sessionOptions.OutputDir != null)
                    {
                        var fileName = viewName.Replace("[", "").Replace("]", "").Replace(".", "_") + ".sql";
                        var filePath = Path.Combine(sessionOptions.OutputDir, fileName);
                        File.WriteAllText(filePath, convertedSql);
                    }
                }
            }
            catch (Exception ex)
            {
                result.Status = ViewValidateStatus.Exception;
                result.Errors.Add(ex.Message);
            }

            result.Elapsed = sw.Elapsed;
            inlineResults.Add((viewObject, result, convertedSql));

            // When not deploying, print each view's status as we go
            if (!sessionOptions.AnyDeploy)
            {
                results.Add(result);
                wizard.Info($"[{i + 1}/{allViews.Count}] {viewName}...");
                PrintViewStatus(result);

                if (sessionOptions.StopOnError && IsFailure(result.Status))
                {
                    wizard.Error("Stopping due to --stop-on-error.");
                    break;
                }
            }
        }

        // Phase 2: Deploy (if requested)
        if (sessionOptions.AnyDeploy)
        {
            // Separate skipped/errored from deployable
            var skipped = inlineResults.Where(r => r.Result.Status == ViewValidateStatus.Skipped).ToList();
            var errors = inlineResults.Where(r => IsFailure(r.Result.Status)).ToList();
            var deployable = inlineResults.Where(r => !IsFailure(r.Result.Status) && r.Result.Status != ViewValidateStatus.Skipped).ToList();

            wizard.Info($"Inline complete: {deployable.Count} to deploy, {skipped.Count} skipped, {errors.Count} errors");
            wizard.Info("");

            // Add skipped/errored results
            foreach (var (_, result, _) in skipped)
                results.Add(result);
            foreach (var (_, result, _) in errors)
                results.Add(result);

            // Deploy each view that passed inlining
            for (var i = 0; i < deployable.Count; i++)
            {
                var (viewObject, result, convertedSql) = deployable[i];
                var sw = Stopwatch.StartNew();

                wizard.Info($"[{i + 1}/{deployable.Count}] {result.ViewName}...");

                // Reopen connection if it was broken by a previous error
                if (connection.Connection?.State == ConnectionState.Broken || connection.Connection?.State == ConnectionState.Closed)
                {
                    try { connection.Connection.Close(); } catch { }
                    connection.Connection.Open();
                }

                if (sessionOptions.Deploy)
                    DeployAndCompare(viewObject, convertedSql!, result, sessionOptions.TimeoutSeconds);
                else
                    DeployOnly(viewObject, convertedSql!, result);

                result.Elapsed += sw.Elapsed;
                results.Add(result);
                PrintViewStatus(result);

                if (sessionOptions.StopOnError && IsFailure(result.Status))
                {
                    wizard.Error("Stopping due to --stop-on-error.");
                    break;
                }
            }
        }

        totalSw.Stop();

        // Print summary
        PrintSummary(results, totalSw.Elapsed);

        // Write error report for failures (includes inlined SQL for easy debugging)
        var allFailures = inlineResults.Where(r => IsFailure(r.Result.Status) || r.Result.Status == ViewValidateStatus.Timeout).ToList();
        if (allFailures.Count > 0)
        {
            var reportPath = "validate-errors.log";
            using var writer = new StreamWriter(reportPath);
            writer.WriteLine($"=== Validation Error Report ===");
            writer.WriteLine($"Generated: {DateTime.Now:G}");
            writer.WriteLine($"Options: {options.ToMetadataString()}");
            writer.WriteLine($"Total: {results.Count} views, {allFailures.Count} failure(s)/timeout(s)");
            writer.WriteLine();

            foreach (var (viewObject, result, convertedSql) in allFailures)
            {
                writer.WriteLine(new string('=', 80));
                writer.WriteLine($"View: {result.ViewName}");
                writer.WriteLine($"Status: {result.Status}");
                writer.WriteLine($"Elapsed: {result.Elapsed}");
                if (result.Errors.Count > 0)
                {
                    writer.WriteLine("Errors:");
                    foreach (var error in result.Errors)
                        writer.WriteLine($"  {error}");
                }
                if (convertedSql != null)
                {
                    writer.WriteLine();
                    writer.WriteLine("--- Inlined SQL ---");
                    writer.WriteLine(convertedSql);
                }
                writer.WriteLine();
            }

            wizard.Info($"Error report written to: {Path.GetFullPath(reportPath)}");
        }

        return results;
    }

    private void DeployAndCompare(SchemaObjectName viewObject, string convertedSql, ViewValidateResult result, int timeoutSeconds)
    {
        var schema = viewObject.SchemaIdentifier?.Value ?? "dbo";
        var baseName = viewObject.BaseIdentifier.Value;
        var validateViewName = $"{baseName}_Validate";

        try
        {
            // Rename the inlined SQL to _Validate
            var renamedSql = OptimizeSession.RenameView(convertedSql, schema, validateViewName);

            // Deploy
            connection.ExecuteNonQuery(renamedSql);

            try
            {
                var queryRunner = new QueryRunner(connection, timeoutSeconds);

                // COUNT inlined first (should be faster)
                try
                {
                    result.InlinedRowCount = queryRunner.GetRowCount(schema, validateViewName);
                }
                catch (SqlException ex) when (ex.Number == -2)
                {
                    result.Status = ViewValidateStatus.Timeout;
                    result.Errors.Add($"COUNT on inlined view timed out after {timeoutSeconds}s");
                }

                if (result.Status != ViewValidateStatus.Timeout)
                {
                    try
                    {
                        result.OriginalRowCount = queryRunner.GetRowCount(schema, baseName);
                    }
                    catch (SqlException ex) when (ex.Number == -2)
                    {
                        result.Status = ViewValidateStatus.Timeout;
                        result.Errors.Add($"COUNT on original timed out after {timeoutSeconds}s (inlined count: {result.InlinedRowCount:N0})");
                    }
                }

                // EXCEPT comparison
                if (result.Status != ViewValidateStatus.Timeout)
                {
                    try
                    {
                        var (onlyInOriginal, onlyInInlined) = queryRunner.RunExceptComparison(schema, baseName, validateViewName);
                        result.OnlyInOriginal = onlyInOriginal;
                        result.OnlyInInlined = onlyInInlined;
                    }
                    catch (SqlException ex) when (ex.Number == -2)
                    {
                        result.Status = ViewValidateStatus.Timeout;
                        result.Errors.Add($"EXCEPT comparison timed out after {timeoutSeconds}s (counts: original={result.OriginalRowCount:N0} inlined={result.InlinedRowCount:N0})");
                    }
                }

                if (result.Status != ViewValidateStatus.Timeout)
                {
                    if (result.OriginalRowCount != result.InlinedRowCount || result.OnlyInOriginal != 0 || result.OnlyInInlined != 0)
                    {
                        result.Status = ViewValidateStatus.ValidationFail;
                        result.Errors.Add($"Row count: original={result.OriginalRowCount:N0} inlined={result.InlinedRowCount:N0}, EXCEPT: {result.OnlyInOriginal}/{result.OnlyInInlined}");
                    }
                }
            }
            finally
            {
                // Always drop the _Validate view
                try
                {
                    connection.ExecuteNonQuery($"DROP VIEW [{schema}].[{validateViewName}]");
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }
        catch (SqlException ex) when (ex.Number == -2)
        {
            result.Status = ViewValidateStatus.Timeout;
            if (result.InlinedRowCount.HasValue)
                result.Errors.Add($"Timed out after {timeoutSeconds}s (inlined count: {result.InlinedRowCount:N0})");
            else
                result.Errors.Add($"Timed out after {timeoutSeconds}s");
        }
        catch (Exception ex)
        {
            result.Status = ViewValidateStatus.DeployError;
            result.Errors.Add(ex.Message);
        }
    }

    private void DeployOnly(SchemaObjectName viewObject, string convertedSql, ViewValidateResult result)
    {
        var schema = viewObject.SchemaIdentifier?.Value ?? "dbo";
        var baseName = viewObject.BaseIdentifier.Value;
        var validateViewName = $"{baseName}_Validate";

        try
        {
            var renamedSql = OptimizeSession.RenameView(convertedSql, schema, validateViewName);
            connection.ExecuteNonQuery(renamedSql);

            try
            {
                connection.ExecuteNonQuery($"DROP VIEW [{schema}].[{validateViewName}]");
            }
            catch
            {
                // Best effort cleanup
            }
        }
        catch (Exception ex)
        {
            result.Status = ViewValidateStatus.DeployError;
            result.Errors.Add(ex.Message);
        }
    }

    private void PrintViewStatus(ViewValidateResult result)
    {
        var elapsed = result.Elapsed.TotalSeconds < 1
            ? $"{result.Elapsed.TotalMilliseconds:N0}ms"
            : $"{result.Elapsed.TotalSeconds:N1}s";

        var detail = result.Status switch
        {
            ViewValidateStatus.Pass => $"cols={result.ColumnsStripped} joins={result.JoinsStripped} flat={result.DerivedTablesFlattened}",
            ViewValidateStatus.PassWithWarnings => $"{result.Warnings.Count} warning(s)",
            ViewValidateStatus.Skipped => "no nested views",
            _ => result.Errors.Count > 0 ? result.Errors[0] : "",
        };

        // Truncate long detail
        if (detail.Length > 100)
            detail = detail.Substring(0, 97) + "...";

        var line = $"  {result.Status,-20} ({elapsed}) {detail}";

        if (IsFailure(result.Status))
            wizard.Error(line);
        else if (result.Status == ViewValidateStatus.PassWithWarnings)
            wizard.Warn(line);
        else if (result.Status == ViewValidateStatus.Timeout)
            wizard.Warn(line);
        else if (result.Status == ViewValidateStatus.Skipped)
            wizard.Info(line);
        else
            wizard.Success(line);
    }

    private void PrintSummary(List<ViewValidateResult> results, TimeSpan totalElapsed)
    {
        wizard.Info("");
        wizard.Info("=== Validation Summary ===");
        wizard.Info($"Options: {options.ToMetadataString()}");
        wizard.Info($"Total: {results.Count} views in {FormatElapsed(totalElapsed)}");
        wizard.Info("");

        // Status counts table
        var statusGroups = results
            .GroupBy(r => r.Status)
            .OrderBy(g => g.Key)
            .Select(g => new[] { g.Key.ToString(), g.Count().ToString() })
            .ToList();

        wizard.WriteTable(new[] { "Status", "Count" }, statusGroups);

        // Failures
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

        // Timeouts
        var timeouts = results.Where(r => r.Status == ViewValidateStatus.Timeout).ToList();
        if (timeouts.Count > 0)
        {
            wizard.Info("");
            wizard.Warn($"--- {timeouts.Count} Timeout(s) ---");
            foreach (var t in timeouts)
            {
                var detail = t.Errors.Count > 0 ? t.Errors[0] : "";
                wizard.Warn($"  {t.ViewName}: {detail}");
            }
        }

        // Warnings
        var warnings = results.Where(r => r.Status == ViewValidateStatus.PassWithWarnings).ToList();
        if (warnings.Count > 0)
        {
            wizard.Info("");
            wizard.Warn($"--- {warnings.Count} View(s) with Warnings ---");
            foreach (var w in warnings)
                wizard.Warn($"  {w.ViewName}: {w.Warnings.Count} warning(s)");
        }

        wizard.Info("");
    }

    internal static bool IsFailure(ViewValidateStatus status)
    {
        return status is ViewValidateStatus.InlineError
            or ViewValidateStatus.ParseError
            or ViewValidateStatus.DeployError
            or ViewValidateStatus.ValidationFail
            or ViewValidateStatus.Exception;
    }

    internal static Regex BuildFilterRegex(string filter)
    {
        // Normalize: remove brackets
        var normalized = filter.Replace("[", "").Replace("]", "");

        if (normalized.Contains('%'))
        {
            // SQL LIKE-style: translate % to .*
            var pattern = "^" + Regex.Escape(normalized).Replace("%", ".*") + "$";
            return new Regex(pattern, RegexOptions.IgnoreCase);
        }

        // Exact match (bracket-insensitive)
        return new Regex("^" + Regex.Escape(normalized) + "$", RegexOptions.IgnoreCase);
    }

    internal static string StripBrackets(string name) => name.Replace("[", "").Replace("]", "");

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalMinutes >= 1)
            return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
        return $"{elapsed.TotalSeconds:N1}s";
    }
}

#endif
