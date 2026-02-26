#if !RELEASELIBRARY

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SqlInliner.Optimize;

/// <summary>
/// Options for a batch-validation run.
/// </summary>
public sealed class ValidateSessionOptions
{
    /// <summary>Deploy each inlined view and run COUNT + EXCEPT validation.</summary>
    public bool Deploy { get; set; }

    /// <summary>Save inlined SQL files to this directory.</summary>
    public string? OutputDir { get; set; }

    /// <summary>Halt on first failure instead of continuing.</summary>
    public bool StopOnError { get; set; }

    /// <summary>Only process matching views (exact name or SQL LIKE-style % wildcard).</summary>
    public string? Filter { get; set; }
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
        Regex? filterRegex = null;
        if (!string.IsNullOrEmpty(sessionOptions.Filter))
        {
            filterRegex = BuildFilterRegex(sessionOptions.Filter);
            allViews = allViews.Where(v => filterRegex.IsMatch(StripBrackets(v.GetName()))).ToList();
        }

        // Prepare output directory
        if (sessionOptions.OutputDir != null)
            Directory.CreateDirectory(sessionOptions.OutputDir);

        wizard.Info($"Options: {options.ToMetadataString()}");
        wizard.Info($"Views to process: {allViews.Count}");
        if (sessionOptions.Deploy)
            wizard.Info("Deploy + validate: enabled");
        wizard.Info("");

        for (var i = 0; i < allViews.Count; i++)
        {
            var viewObject = allViews[i];
            var viewName = viewObject.GetName();
            var result = new ViewValidateResult { ViewName = viewName };
            var sw = Stopwatch.StartNew();

            wizard.Info($"[{i + 1}/{allViews.Count}] {viewName}...");

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

                    // Save to output directory
                    if (sessionOptions.OutputDir != null)
                    {
                        var fileName = viewName.Replace("[", "").Replace("]", "").Replace(".", "_") + ".sql";
                        var filePath = Path.Combine(sessionOptions.OutputDir, fileName);
                        File.WriteAllText(filePath, inliner.Result.ConvertedSql);
                    }

                    // Deploy + validate
                    if (sessionOptions.Deploy && !IsFailure(result.Status))
                    {
                        DeployAndValidate(viewObject, inliner, result);
                    }
                }
            }
            catch (Exception ex)
            {
                result.Status = ViewValidateStatus.Exception;
                result.Errors.Add(ex.Message);
            }

            result.Elapsed = sw.Elapsed;
            results.Add(result);

            // Print single-line status
            PrintViewStatus(result);

            // Stop on error if requested
            if (sessionOptions.StopOnError && IsFailure(result.Status))
            {
                wizard.Error("Stopping due to --stop-on-error.");
                break;
            }
        }

        totalSw.Stop();

        // Print summary
        PrintSummary(results, totalSw.Elapsed);

        return results;
    }

    private void DeployAndValidate(Microsoft.SqlServer.TransactSql.ScriptDom.SchemaObjectName viewObject, DatabaseViewInliner inliner, ViewValidateResult result)
    {
        var schema = viewObject.SchemaIdentifier?.Value ?? "dbo";
        var baseName = viewObject.BaseIdentifier.Value;
        var validateViewName = $"{baseName}_Validate";

        try
        {
            // Rename the inlined SQL to _Validate
            var renamedSql = OptimizeSession.RenameView(inliner.Result!.ConvertedSql, schema, validateViewName);

            // Deploy
            connection.ExecuteNonQuery(renamedSql);

            try
            {
                // COUNT comparison
                var queryRunner = new QueryRunner(connection);
                var originalCount = queryRunner.GetRowCount(schema, baseName);
                var inlinedCount = queryRunner.GetRowCount(schema, validateViewName);

                result.OriginalRowCount = originalCount;
                result.InlinedRowCount = inlinedCount;

                // EXCEPT comparison
                var (onlyInOriginal, onlyInInlined) = queryRunner.RunExceptComparison(schema, baseName, validateViewName);
                result.OnlyInOriginal = onlyInOriginal;
                result.OnlyInInlined = onlyInInlined;

                if (originalCount != inlinedCount || onlyInOriginal != 0 || onlyInInlined != 0)
                {
                    result.Status = ViewValidateStatus.ValidationFail;
                    result.Errors.Add($"Row count: original={originalCount:N0} inlined={inlinedCount:N0}, EXCEPT: {onlyInOriginal}/{onlyInInlined}");
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
        else if (result.Status == ViewValidateStatus.Skipped)
            wizard.Info(line);
        else
            wizard.Success(line);
    }

    private void PrintSummary(List<ViewValidateResult> results, TimeSpan totalElapsed)
    {
        wizard.Info("");
        wizard.Info("=== Validation Summary ===");
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
