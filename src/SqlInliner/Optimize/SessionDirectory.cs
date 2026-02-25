#if !RELEASELIBRARY

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SqlInliner.Optimize;

/// <summary>
/// Manages the session directory for an optimize session: file writes, hashing, and logging.
/// </summary>
public sealed class SessionDirectory
{
    private readonly StreamWriter? logWriter;

    public SessionDirectory(string viewName, string baseDirectory)
    {
        var safeName = viewName.Replace(".", "-").Replace("[", "").Replace("]", "");
        var timestamp = DateTime.Now.ToString("yyyyMMdd'T'HHmmss");
        DirectoryPath = Path.Combine(baseDirectory, $"optimize-{safeName}-{timestamp}");
        Directory.CreateDirectory(DirectoryPath);

        var logPath = Path.Combine(DirectoryPath, "session.log");
        logWriter = new StreamWriter(logPath, append: true) { AutoFlush = true };
        Log($"Session started for view {viewName}");
    }

    /// <summary>
    /// Gets the full path to the session directory.
    /// </summary>
    public string DirectoryPath { get; }

    /// <summary>
    /// Saves a SQL file for the specified iteration. Returns the full file path.
    /// </summary>
    public string SaveIteration(int iteration, string sql)
    {
        var fileName = $"iteration-{iteration}.sql";
        var path = Path.Combine(DirectoryPath, fileName);
        File.WriteAllText(path, sql);
        Log($"Saved {fileName} ({sql.Length} chars)");
        return path;
    }

    /// <summary>
    /// Saves the recommended SQL file. Returns the full file path.
    /// </summary>
    public string SaveRecommended(string sql)
    {
        var path = Path.Combine(DirectoryPath, "recommended.sql");
        File.WriteAllText(path, sql);
        Log($"Saved recommended.sql ({sql.Length} chars)");
        return path;
    }

    /// <summary>
    /// Saves an execution plan XML file. Returns the full file path.
    /// The .sqlplan extension opens directly in SSMS for visual plan comparison.
    /// </summary>
    public string SaveExecutionPlan(string label, string xmlPlan)
    {
        var path = Path.Combine(DirectoryPath, $"{label}.sqlplan");
        File.WriteAllText(path, xmlPlan);
        Log($"Saved {label}.sqlplan");
        return path;
    }

    /// <summary>
    /// Saves a self-contained HTML benchmark report. Returns the full file path.
    /// </summary>
    public string SaveBenchmarkReport(
        string originalViewName,
        string inlinedViewName,
        string optionsDescription,
        BenchmarkResult originalBench,
        BenchmarkResult inlinedBench)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>Benchmark — {Esc(originalViewName)} vs {Esc(inlinedViewName)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body{font-family:system-ui,-apple-system,sans-serif;max-width:900px;margin:2rem auto;padding:0 1rem;color:#1a1a1a;background:#fafafa}");
        sb.AppendLine("h1{font-size:1.4rem;margin-bottom:.2rem}");
        sb.AppendLine("h2{font-size:1.1rem;margin-top:2rem;border-bottom:1px solid #ddd;padding-bottom:.3rem}");
        sb.AppendLine(".meta{color:#666;font-size:.85rem;margin-bottom:1.5rem}");
        sb.AppendLine("table{border-collapse:collapse;width:100%;margin:.5rem 0 1rem}");
        sb.AppendLine("th,td{text-align:left;padding:.45rem .7rem;border:1px solid #ddd;font-size:.9rem}");
        sb.AppendLine("th{background:#f0f0f0;font-weight:600}");
        sb.AppendLine("td.num{text-align:right;font-variant-numeric:tabular-nums}");
        sb.AppendLine(".better{color:#16a34a}.worse{color:#dc2626}.neutral{color:#666}");
        sb.AppendLine(".plans{margin-top:1.5rem;font-size:.9rem}");
        sb.AppendLine(".plans a{color:#2563eb}");
        sb.AppendLine("</style></head><body>");

        // Header
        sb.AppendLine($"<h1>Benchmark Report</h1>");
        sb.AppendLine($"<div class=\"meta\">{Esc(originalViewName)} vs {Esc(inlinedViewName)} &mdash; {DateTime.Now:yyyy-MM-dd HH:mm}<br>Options: {Esc(optionsDescription)}</div>");

        // Summary table
        sb.AppendLine("<h2>Performance Summary</h2>");
        sb.AppendLine("<table><tr><th>Metric</th><th>Original</th><th>Inlined</th><th>Change</th></tr>");
        AppendMetricRow(sb, "CPU time (ms)", originalBench.CpuTimeMs, inlinedBench.CpuTimeMs);
        AppendMetricRow(sb, "Elapsed time (ms)", originalBench.ElapsedTimeMs, inlinedBench.ElapsedTimeMs);
        AppendMetricRow(sb, "Logical reads", originalBench.LogicalReads, inlinedBench.LogicalReads);
        sb.AppendLine("</table>");

        // Per-table IO breakdown
        var origByTable = originalBench.TableStats.ToDictionary(t => t.TableName, t => t);
        var inlinedByTable = inlinedBench.TableStats.ToDictionary(t => t.TableName, t => t);
        var allTables = new HashSet<string>(origByTable.Keys);
        allTables.UnionWith(inlinedByTable.Keys);

        var tableRows = new List<(string Name, long OrigReads, long InlinedReads)>();
        foreach (var table in allTables)
        {
            origByTable.TryGetValue(table, out var orig);
            inlinedByTable.TryGetValue(table, out var inl);
            var origReads = orig?.LogicalReads ?? 0;
            var inlinedReads = inl?.LogicalReads ?? 0;
            if (origReads == 0 && inlinedReads == 0)
                continue;
            tableRows.Add((table, origReads, inlinedReads));
        }

        if (tableRows.Count > 0)
        {
            tableRows.Sort((a, b) => b.InlinedReads.CompareTo(a.InlinedReads));

            sb.AppendLine("<h2>Per-Table IO Breakdown</h2>");
            sb.AppendLine("<table><tr><th>Table</th><th>Orig Reads</th><th>Inlined Reads</th><th>Change</th></tr>");
            foreach (var (name, origReads, inlinedReads) in tableRows)
                AppendMetricRow(sb, name, origReads, inlinedReads);
            sb.AppendLine("</table>");
        }

        // Execution plan links
        var hasOrigPlan = originalBench.ExecutionPlanXml != null;
        var hasInlinedPlan = inlinedBench.ExecutionPlanXml != null;
        if (hasOrigPlan || hasInlinedPlan)
        {
            sb.AppendLine("<div class=\"plans\"><strong>Execution plans</strong> (open in SSMS):<br>");
            if (hasOrigPlan)
                sb.AppendLine("<a href=\"plan-original.sqlplan\">plan-original.sqlplan</a><br>");
            if (hasInlinedPlan)
                sb.AppendLine("<a href=\"plan-inlined.sqlplan\">plan-inlined.sqlplan</a>");
            sb.AppendLine("</div>");
        }

        sb.AppendLine("</body></html>");

        var path = Path.Combine(DirectoryPath, "benchmark.html");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        Log("Saved benchmark.html");
        return path;
    }

    private static void AppendMetricRow(StringBuilder sb, string label, long original, long inlined)
    {
        var change = FormatChange(original, inlined);
        var css = original == inlined ? "neutral" : inlined > original ? "worse" : "better";
        sb.AppendLine($"<tr><td>{Esc(label)}</td><td class=\"num\">{original:N0}</td><td class=\"num\">{inlined:N0}</td><td class=\"{css}\">{change}</td></tr>");
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

    private static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);

    /// <summary>
    /// Computes a SHA256 hash of the file at the specified path.
    /// </summary>
    public static string ComputeHash(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Appends a log entry with timestamp.
    /// </summary>
    public void Log(string message)
    {
        logWriter?.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    /// <summary>
    /// Closes the log writer.
    /// </summary>
    public void Close()
    {
        Log("Session ended");
        logWriter?.Dispose();
    }
}

#endif
