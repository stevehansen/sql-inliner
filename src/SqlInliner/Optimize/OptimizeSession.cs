#if !RELEASELIBRARY

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SqlInliner.Optimize;

/// <summary>
/// Drives the interactive optimization workflow step by step.
/// </summary>
public sealed class OptimizeSession
{
    private readonly DatabaseConnection connection;
    private readonly IConsoleWizard wizard;
    private readonly QueryRunner queryRunner;
    private readonly string baseDirectory;
    private readonly InlinerOptions? configOptions;

    private string schema = null!;
    private string viewName = null!;
    private string fullViewName = null!;
    private string inlinedViewName = null!;
    private InlinerOptions currentOptions = null!;
    private SessionDirectory session = null!;
    private int iteration;
    private string? lastConvertedSql;
    private string? lastFilePath;
    private bool inlinedViewDeployed;

    public OptimizeSession(DatabaseConnection connection, IConsoleWizard wizard, string baseDirectory, InlinerOptions? configOptions = null)
    {
        this.connection = connection;
        this.wizard = wizard;
        queryRunner = new QueryRunner(connection);
        this.baseDirectory = baseDirectory;
        this.configOptions = configOptions;
    }

    /// <summary>
    /// Runs the full interactive optimization workflow.
    /// </summary>
    public void Run(string? viewNameArg)
    {
        // Step 1: Connect & Warn
        StepConnectAndWarn();

        // Step 2: Select View
        StepSelectView(viewNameArg);

        // Step 3-7: Inline → Review → Deploy → Validate → Iterate loop
        // Use config options if provided, otherwise use defaults
        currentOptions = configOptions != null
            ? new InlinerOptions
            {
                StripUnusedColumns = configOptions.StripUnusedColumns,
                StripUnusedJoins = configOptions.StripUnusedJoins,
                AggressiveJoinStripping = configOptions.AggressiveJoinStripping,
                FlattenDerivedTables = configOptions.FlattenDerivedTables,
            }
            : new InlinerOptions
            {
                StripUnusedColumns = true,
                StripUnusedJoins = false,
                AggressiveJoinStripping = false,
                FlattenDerivedTables = false,
            };

        // Check for existing _Inlined view and restore saved options
        var inlinedObjectName = DatabaseConnection.ToObjectName(schema, inlinedViewName);
        if (connection.IsView(inlinedObjectName))
        {
            var rawDef = connection.TryGetRawViewDefinition($"[{schema}].[{inlinedViewName}]");
            if (rawDef != null)
            {
                var savedOptions = InlinerOptions.TryParseFromMetadata(rawDef);
                if (savedOptions != null)
                {
                    currentOptions = savedOptions;
                    wizard.Info($"Loaded options from existing [{schema}].[{inlinedViewName}]:");
                    wizard.Info($"  {savedOptions.ToMetadataString()}");
                }
            }
        }
        session = new SessionDirectory(viewName, baseDirectory);

        wizard.Info($"Session directory: {session.DirectoryPath}");
        wizard.Info("");

        try
        {
            var continueIterating = true;
            while (continueIterating)
            {
                iteration++;

                // Step 3: Inline
                StepInline();
                if (lastConvertedSql == null)
                    break;

                // Step 4: Review
                StepReview();

                // Step 5: Deploy
                var deployed = StepDeploy();

                // Step 6: Validate (only if deployed)
                if (deployed)
                    StepValidate();

                // Step 7: Iterate?
                continueIterating = StepIterate();
            }

            // Step 8: Benchmark (optional)
            if (inlinedViewDeployed)
                StepBenchmark();

            // Step 9: Summary & Cleanup
            StepSummary();
        }
        finally
        {
            session.Close();
        }
    }

    private void StepConnectAndWarn()
    {
        wizard.Info("");
        wizard.Warn("=== BACKUP DATABASE ONLY ===");
        wizard.Warn("This tool will CREATE and ALTER views on the connected database.");
        wizard.Warn("Only run this against a BACKUP or DEVELOPMENT database, never production.");
        wizard.Info("");

        if (!wizard.Confirm("I confirm this is a backup/development database. Continue?"))
            throw new OperationCanceledException("User declined backup confirmation.");
    }

    private void StepSelectView(string? viewNameArg)
    {
        if (!string.IsNullOrEmpty(viewNameArg))
        {
            ParseViewName(viewNameArg);
        }
        else
        {
            var input = wizard.Prompt("Enter the fully qualified view name (e.g. dbo.VPeople)");
            if (string.IsNullOrEmpty(input))
                throw new OperationCanceledException("No view name provided.");
            ParseViewName(input);
        }

        // Validate view exists
        var objectName = DatabaseConnection.ToObjectName(schema, viewName);
        if (!connection.IsView(objectName))
            throw new InvalidOperationException($"View [{schema}].[{viewName}] was not found in the database.");

        inlinedViewName = $"{viewName}_Inlined";

        // Show metadata
        var viewSql = connection.GetViewDefinition(fullViewName);
        var (view, _) = DatabaseView.FromSql(connection, viewSql);
        var nestedCount = view?.References.Views.Count ?? 0;

        wizard.Info("");
        wizard.Success($"Found view [{schema}].[{viewName}]");
        wizard.Info($"  SQL length:    {viewSql.Length:N0} characters");
        wizard.Info($"  Nested views:  {nestedCount}");
        wizard.Info("");
    }

    private void ParseViewName(string input)
    {
        // Handle [schema].[name], schema.name, or just name (default to dbo)
        var cleaned = input.Replace("[", "").Replace("]", "").Trim();
        var parts = cleaned.Split('.');
        if (parts.Length >= 2)
        {
            schema = parts[parts.Length - 2];
            viewName = parts[parts.Length - 1];
        }
        else
        {
            schema = "dbo";
            viewName = parts[0];
        }

        fullViewName = $"[{schema}].[{viewName}]";
    }

    private void StepInline()
    {
        wizard.Info($"--- Iteration {iteration} ---");
        wizard.Info($"Options: {currentOptions.ToMetadataString()}");
        wizard.Info("");

        var viewSql = connection.GetViewDefinition(fullViewName);
        viewSql = DatabaseView.CreateOrAlter(viewSql);

        var inliner = new DatabaseViewInliner(connection, viewSql, currentOptions);

        if (inliner.Errors.Count > 0)
        {
            wizard.Error("Inlining failed with errors:");
            foreach (var error in inliner.Errors)
                wizard.Error($"  {error}");
            lastConvertedSql = null;
            return;
        }

        if (inliner.Warnings.Count > 0)
        {
            wizard.Warn($"Inlining completed with {inliner.Warnings.Count} warning(s):");
            foreach (var warning in inliner.Warnings)
                wizard.Warn($"  {warning}");
        }

        var result = inliner.Result;
        if (result == null)
        {
            wizard.Warn("No nested views found — nothing to inline.");
            lastConvertedSql = null;
            return;
        }

        // Rename view to _Inlined, prepend metadata comment
        var renamedSql = RenameView(result.ConvertedSql, schema, inlinedViewName);
        lastConvertedSql = result.MetadataComment + renamedSql;
        wizard.Success($"Inlined successfully. Stripped {inliner.TotalSelectColumnsStripped} columns and {inliner.TotalJoinsStripped} joins, flattened {inliner.TotalDerivedTablesFlattened} derived tables.");
        wizard.Info($"  Referenced views: {result.KnownViews.Count}");
        wizard.Info($"  Elapsed: {result.Elapsed}");

        lastFilePath = session.SaveIteration(iteration, lastConvertedSql);
        wizard.Info($"  Saved to: {lastFilePath}");
        wizard.Info("");
    }

    private void StepReview()
    {
        if (lastFilePath == null || lastConvertedSql == null)
            return;

        if (!wizard.Confirm("Open generated SQL in your default editor for review?"))
            return;

        var hashBefore = SessionDirectory.ComputeHash(lastFilePath);

        try
        {
            Process.Start(new ProcessStartInfo(lastFilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            wizard.Warn($"Could not open editor: {ex.Message}");
            return;
        }

        wizard.WaitForEnter("Press Enter when done reviewing...");

        var hashAfter = SessionDirectory.ComputeHash(lastFilePath);
        if (hashBefore != hashAfter)
        {
            wizard.Warn("The file was modified externally. Manual edits are not supported.");
            if (wizard.Confirm("Regenerate the file from the original inlining result?", defaultValue: true))
            {
                File.WriteAllText(lastFilePath, lastConvertedSql);
                wizard.Info("File regenerated.");
            }
        }
    }

    private bool StepDeploy()
    {
        if (lastConvertedSql == null)
            return false;

        wizard.Info($"Ready to deploy as [{schema}].[{inlinedViewName}]");
        if (!wizard.Confirm("Execute CREATE OR ALTER VIEW on the database?"))
        {
            wizard.Info("Skipped deployment.");
            return false;
        }

        try
        {
            wizard.Info("Deploying...");
            connection.ExecuteNonQuery(lastConvertedSql);
            inlinedViewDeployed = true;
            wizard.Success($"View [{schema}].[{inlinedViewName}] deployed successfully.");
            session.Log($"Deployed iteration {iteration} as [{schema}].[{inlinedViewName}]");
            wizard.Info("");
            return true;
        }
        catch (Exception ex)
        {
            wizard.Error($"Deployment failed: {ex.Message}");
            session.Log($"Deployment failed: {ex.Message}");
            return false;
        }
    }

    private void StepValidate()
    {
        if (!wizard.Confirm("Run validation (COUNT + EXCEPT comparison)?", defaultValue: true))
            return;

        wizard.Info("Running validation... this may take a while.");

        try
        {
            // COUNT comparison
            var originalCount = queryRunner.GetRowCount(schema, viewName);
            var inlinedCount = queryRunner.GetRowCount(schema, inlinedViewName);

            wizard.WriteTable(
                new[] { "View", "Row Count" },
                new[]
                {
                    new[] { $"[{schema}].[{viewName}]", originalCount.ToString("N0") },
                    new[] { $"[{schema}].[{inlinedViewName}]", inlinedCount.ToString("N0") },
                });

            if (originalCount != inlinedCount)
            {
                wizard.Error($"Row count MISMATCH: original={originalCount:N0}, inlined={inlinedCount:N0}");
                session.Log($"Validation FAILED: row count mismatch ({originalCount} vs {inlinedCount})");
            }
            else
            {
                wizard.Success($"Row counts match: {originalCount:N0}");
            }

            // EXCEPT comparison
            wizard.Info("Running EXCEPT comparison...");
            var (onlyInOriginal, onlyInInlined) = queryRunner.RunExceptComparison(schema, viewName, inlinedViewName);

            if (onlyInOriginal == 0 && onlyInInlined == 0)
            {
                wizard.Success("EXCEPT comparison: PASS — results are identical.");
                session.Log($"Validation PASSED (iteration {iteration})");
            }
            else
            {
                wizard.Error($"EXCEPT comparison: FAIL — {onlyInOriginal} rows only in original, {onlyInInlined} rows only in inlined.");
                session.Log($"Validation FAILED: EXCEPT shows {onlyInOriginal}/{onlyInInlined} diff rows");
            }
        }
        catch (Exception ex)
        {
            wizard.Error($"Validation error: {ex.Message}");
            session.Log($"Validation error: {ex.Message}");
        }

        wizard.Info("");
    }

    private bool StepIterate()
    {
        var choice = wizard.Choose("What would you like to do?", new[]
        {
            "Continue to benchmark/summary",
            "Toggle strip-unused-joins and re-inline",
            "Toggle aggressive-join-stripping and re-inline",
            "Toggle flatten-derived-tables and re-inline",
            "Re-inline with current options",
        });

        switch (choice)
        {
            case 0:
                return false;
            case 1:
                currentOptions.StripUnusedJoins = !currentOptions.StripUnusedJoins;
                wizard.Info($"StripUnusedJoins is now {currentOptions.StripUnusedJoins}");
                return true;
            case 2:
                currentOptions.AggressiveJoinStripping = !currentOptions.AggressiveJoinStripping;
                wizard.Info($"AggressiveJoinStripping is now {currentOptions.AggressiveJoinStripping}");
                return true;
            case 3:
                currentOptions.FlattenDerivedTables = !currentOptions.FlattenDerivedTables;
                wizard.Info($"FlattenDerivedTables is now {currentOptions.FlattenDerivedTables}");
                return true;
            case 4:
                return true;
            default:
                return false;
        }
    }

    private void StepBenchmark()
    {
        if (!wizard.Confirm("Run performance benchmark (SET STATISTICS TIME/IO + execution plan)?"))
            return;

        wizard.Info("Running benchmark... this may take a while.");

        try
        {
            // Warmup run: execute both views once to populate caches and compile execution plans,
            // ensuring a fair comparison (otherwise the first/new view runs cold).
            wizard.Info("Warming up caches...");
            queryRunner.RunBenchmark(schema, viewName);
            queryRunner.RunBenchmark(schema, inlinedViewName);

            wizard.Info($"Benchmarking [{schema}].[{viewName}]...");
            var originalBench = queryRunner.RunBenchmark(schema, viewName);

            wizard.Info($"Benchmarking [{schema}].[{inlinedViewName}]...");
            var inlinedBench = queryRunner.RunBenchmark(schema, inlinedViewName);

            // Summary table
            wizard.Info("");
            wizard.WriteTable(
                new[] { "Metric", "Original", "Inlined", "Change" },
                new[]
                {
                    new[] { "CPU time (ms)", originalBench.CpuTimeMs.ToString("N0"), inlinedBench.CpuTimeMs.ToString("N0"), FormatChange(originalBench.CpuTimeMs, inlinedBench.CpuTimeMs) },
                    new[] { "Elapsed (ms)", originalBench.ElapsedTimeMs.ToString("N0"), inlinedBench.ElapsedTimeMs.ToString("N0"), FormatChange(originalBench.ElapsedTimeMs, inlinedBench.ElapsedTimeMs) },
                    new[] { "Logical reads", originalBench.LogicalReads.ToString("N0"), inlinedBench.LogicalReads.ToString("N0"), FormatChange(originalBench.LogicalReads, inlinedBench.LogicalReads) },
                });

            // Per-table IO comparison
            if (originalBench.TableStats.Count > 0 || inlinedBench.TableStats.Count > 0)
            {
                wizard.Info("");
                wizard.Info("Per-table IO breakdown:");

                var origByTable = originalBench.TableStats.ToDictionary(t => t.TableName, t => t);
                var inlinedByTable = inlinedBench.TableStats.ToDictionary(t => t.TableName, t => t);
                var allTables = new HashSet<string>(origByTable.Keys);
                allTables.UnionWith(inlinedByTable.Keys);

                var rows = new List<string[]>();
                foreach (var table in allTables)
                {
                    origByTable.TryGetValue(table, out var orig);
                    inlinedByTable.TryGetValue(table, out var inlined);
                    var origReads = orig?.LogicalReads ?? 0;
                    var inlinedReads = inlined?.LogicalReads ?? 0;

                    // Skip worktables with 0 reads on both sides
                    if (origReads == 0 && inlinedReads == 0)
                        continue;

                    rows.Add(new[]
                    {
                        table,
                        origReads.ToString("N0"),
                        inlinedReads.ToString("N0"),
                        FormatChange(origReads, inlinedReads),
                    });
                }

                // Sort by inlined reads descending (heaviest tables first)
                rows.Sort((a, b) =>
                {
                    var readsA = long.Parse(a[2], System.Globalization.NumberStyles.Number);
                    var readsB = long.Parse(b[2], System.Globalization.NumberStyles.Number);
                    return readsB.CompareTo(readsA);
                });

                wizard.WriteTable(
                    new[] { "Table", "Orig Reads", "Inlined Reads", "Change" },
                    rows);
            }

            // Save execution plans
            if (originalBench.ExecutionPlanXml != null)
            {
                var path = session.SaveExecutionPlan("plan-original", originalBench.ExecutionPlanXml);
                wizard.Info($"Original execution plan: {path}");
            }
            if (inlinedBench.ExecutionPlanXml != null)
            {
                var path = session.SaveExecutionPlan("plan-inlined", inlinedBench.ExecutionPlanXml);
                wizard.Info($"Inlined execution plan:  {path}");
            }

            // Gather server info for the report
            string? serverVersion = null, databaseName = null;
            try
            {
                (serverVersion, databaseName) = queryRunner.GetServerInfo();
            }
            catch { /* non-critical — report works without it */ }

            // Save HTML report
            var reportPath = session.SaveBenchmarkReport(
                $"[{schema}].[{viewName}]",
                $"[{schema}].[{inlinedViewName}]",
                currentOptions.ToMetadataString(),
                originalBench,
                inlinedBench,
                serverVersion,
                databaseName);
            wizard.Info($"Benchmark report: {reportPath}");

            session.Log($"Benchmark: original CPU={originalBench.CpuTimeMs}ms elapsed={originalBench.ElapsedTimeMs}ms reads={originalBench.LogicalReads}");
            session.Log($"Benchmark: inlined CPU={inlinedBench.CpuTimeMs}ms elapsed={inlinedBench.ElapsedTimeMs}ms reads={inlinedBench.LogicalReads}");
        }
        catch (Exception ex)
        {
            wizard.Error($"Benchmark error: {ex.Message}");
            session.Log($"Benchmark error: {ex.Message}");
        }

        wizard.Info("");
    }

    private void StepSummary()
    {
        wizard.Info("=== Summary ===");
        wizard.Info($"Session directory: {session.DirectoryPath}");
        wizard.Info($"Total iterations: {iteration}");

        if (lastConvertedSql != null)
        {
            var recommendedPath = session.SaveRecommended(lastConvertedSql);
            wizard.Info($"Recommended SQL: {recommendedPath}");
        }

        if (inlinedViewDeployed)
        {
            wizard.Info("");
            wizard.Warn("Cleanup: the inlined view is still deployed on the database.");
            wizard.Info($"To remove it, run: DROP VIEW [{schema}].[{inlinedViewName}]");
            wizard.Info("(This tool does NOT execute DROP statements automatically.)");
        }

        wizard.Info("");
        wizard.Success("Done!");
    }

    /// <summary>
    /// Renames the view in a CREATE OR ALTER VIEW statement.
    /// </summary>
    public static string RenameView(string sql, string schema, string newName)
    {
        return Regex.Replace(
            sql,
            @"(CREATE\s+OR\s+ALTER\s+VIEW\s+)(\[.+?\]\.\[.+?\]|\S+\.\S+)",
            $"$1[{schema}].[{newName}]",
            RegexOptions.IgnoreCase);
    }

    private static string FormatChange(long original, long inlined)
    {
        if (original == 0)
            return inlined == 0 ? "0%" : "+inf";

        var pct = ((double)inlined - original) / original * 100;
        return pct switch
        {
            < 0 => $"{pct:+0.0;-0.0}%",
            > 0 => $"+{pct:0.0}%",
            _ => "0%",
        };
    }
}

#endif
