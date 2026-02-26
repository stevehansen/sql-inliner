#if !RELEASELIBRARY

using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// Saves a self-contained HTML benchmark report with dark theme, execution plan comparison,
    /// and interactive sortable tables. Returns the full file path.
    /// </summary>
    public string SaveBenchmarkReport(
        string originalViewName,
        string inlinedViewName,
        string optionsDescription,
        BenchmarkResult originalBench,
        BenchmarkResult inlinedBench,
        string? serverVersion = null,
        string? databaseName = null)
    {
        var inv = CultureInfo.InvariantCulture;
        var origPlan = ExecutionPlanSummary.TryParse(originalBench.ExecutionPlanXml);
        var inlinedPlan = ExecutionPlanSummary.TryParse(inlinedBench.ExecutionPlanXml);

        var sb = new StringBuilder();

        // --- HTML head ---
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine($"<title>Benchmark — {Esc(originalViewName)} vs {Esc(inlinedViewName)}</title>");
        sb.AppendLine("<link href=\"https://fonts.googleapis.com/css2?family=JetBrains+Mono:wght@400;500;600;700&family=Outfit:wght@300;400;500;600;700&display=swap\" rel=\"stylesheet\">");
        sb.AppendLine("<style>");
        sb.AppendLine(ReportCss);
        sb.AppendLine("</style></head><body>");
        sb.AppendLine("<div class=\"container\">");

        // --- Header ---
        sb.AppendLine("<header>");
        sb.AppendLine("<h1>SQL Inliner &mdash; Benchmark Report</h1>");
        sb.AppendLine($"<p class=\"subtitle\">{Esc(originalViewName)} vs {Esc(inlinedViewName)}</p>");
        sb.AppendLine("<div class=\"meta-grid\">");
        AppendMetaItem(sb, "Date", DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        AppendMetaItem(sb, "Machine", $"{Environment.MachineName} ({Environment.UserName})");
        if (!string.IsNullOrEmpty(databaseName))
            AppendMetaItem(sb, "Database", databaseName);
        if (!string.IsNullOrEmpty(serverVersion))
        {
            // Extract first line of @@VERSION (e.g., "Microsoft SQL Server 2022 (RTM-CU18) ...")
            var versionLine = serverVersion.Contains('\n')
                ? serverVersion.Substring(0, serverVersion.IndexOf('\n')).Trim()
                : serverVersion.Trim();
            AppendMetaItem(sb, "Server", versionLine);
        }
        AppendMetaItem(sb, "Options", optionsDescription);
        sb.AppendLine("</div></header>");

        // --- Performance Summary (stat cards) ---
        sb.AppendLine("<section class=\"card\">");
        sb.AppendLine("<div class=\"card-header\"><h2>Performance Summary</h2></div>");
        sb.AppendLine("<div class=\"card-body\"><div class=\"stat-grid\">");
        AppendStatCard(sb, "CPU Time", $"{originalBench.CpuTimeMs.ToString("N0", inv)} ms",
            $"{inlinedBench.CpuTimeMs.ToString("N0", inv)} ms",
            FormatChangePct(originalBench.CpuTimeMs, inlinedBench.CpuTimeMs),
            ChangeClass(originalBench.CpuTimeMs, inlinedBench.CpuTimeMs));
        AppendStatCard(sb, "Elapsed Time", $"{originalBench.ElapsedTimeMs.ToString("N0", inv)} ms",
            $"{inlinedBench.ElapsedTimeMs.ToString("N0", inv)} ms",
            FormatChangePct(originalBench.ElapsedTimeMs, inlinedBench.ElapsedTimeMs),
            ChangeClass(originalBench.ElapsedTimeMs, inlinedBench.ElapsedTimeMs));
        AppendStatCard(sb, "Logical Reads", originalBench.LogicalReads.ToString("N0", inv),
            inlinedBench.LogicalReads.ToString("N0", inv),
            FormatChangePct(originalBench.LogicalReads, inlinedBench.LogicalReads),
            ChangeClass(originalBench.LogicalReads, inlinedBench.LogicalReads));
        sb.AppendLine("</div></div></section>");

        // --- Execution Plan Comparison ---
        if (origPlan != null || inlinedPlan != null)
        {
            sb.AppendLine("<section class=\"card\">");
            sb.AppendLine("<div class=\"card-header togglable\" onclick=\"toggleSection('plan-section')\">");
            sb.AppendLine("<h2>Execution Plan Comparison</h2><span class=\"toggle-icon\" id=\"plan-section-icon\">&#9660;</span>");
            sb.AppendLine("</div>");
            sb.AppendLine("<div class=\"card-body\" id=\"plan-section\">");
            sb.AppendLine("<table><thead><tr><th>Metric</th><th>Original</th><th>Inlined</th><th>Delta</th></tr></thead><tbody>");

            if (origPlan != null && inlinedPlan != null)
            {
                AppendPlanRow(sb, "Estimated Cost", origPlan.EstimatedCost.ToString("N3", inv),
                    inlinedPlan.EstimatedCost.ToString("N3", inv),
                    FormatChangePctDouble(origPlan.EstimatedCost, inlinedPlan.EstimatedCost),
                    ChangeClassDouble(origPlan.EstimatedCost, inlinedPlan.EstimatedCost));
                AppendPlanRow(sb, "Estimated Rows", origPlan.EstimatedRows.ToString("N1", inv),
                    inlinedPlan.EstimatedRows.ToString("N1", inv), "", "neutral");
                AppendPlanRow(sb, "Compile Time", $"{origPlan.CompileTimeMs.ToString("N0", inv)} ms",
                    $"{inlinedPlan.CompileTimeMs.ToString("N0", inv)} ms",
                    FormatChangePct(origPlan.CompileTimeMs, inlinedPlan.CompileTimeMs),
                    ChangeClass(origPlan.CompileTimeMs, inlinedPlan.CompileTimeMs));
                AppendPlanRow(sb, "Compile CPU", $"{origPlan.CompileCpuMs.ToString("N0", inv)} ms",
                    $"{inlinedPlan.CompileCpuMs.ToString("N0", inv)} ms",
                    FormatChangePct(origPlan.CompileCpuMs, inlinedPlan.CompileCpuMs),
                    ChangeClass(origPlan.CompileCpuMs, inlinedPlan.CompileCpuMs));
                AppendPlanRow(sb, "Compile Memory", FormatKB(origPlan.CompileMemoryKB),
                    FormatKB(inlinedPlan.CompileMemoryKB),
                    FormatChangePct(origPlan.CompileMemoryKB, inlinedPlan.CompileMemoryKB),
                    ChangeClass(origPlan.CompileMemoryKB, inlinedPlan.CompileMemoryKB));
                AppendPlanRow(sb, "Memory Grant", FormatKB(origPlan.MemoryGrantKB),
                    FormatKB(inlinedPlan.MemoryGrantKB),
                    FormatChangePct(origPlan.MemoryGrantKB, inlinedPlan.MemoryGrantKB),
                    ChangeClass(origPlan.MemoryGrantKB, inlinedPlan.MemoryGrantKB));
                AppendPlanRow(sb, "Max Memory Used", FormatKB(origPlan.MaxUsedMemoryKB),
                    FormatKB(inlinedPlan.MaxUsedMemoryKB),
                    FormatChangePct(origPlan.MaxUsedMemoryKB, inlinedPlan.MaxUsedMemoryKB),
                    ChangeClass(origPlan.MaxUsedMemoryKB, inlinedPlan.MaxUsedMemoryKB));
                AppendPlanRow(sb, "Cached Plan Size", $"{origPlan.CachedPlanSizeKB.ToString("N0", inv)} KB",
                    $"{inlinedPlan.CachedPlanSizeKB.ToString("N0", inv)} KB",
                    FormatChangePct(origPlan.CachedPlanSizeKB, inlinedPlan.CachedPlanSizeKB),
                    ChangeClass(origPlan.CachedPlanSizeKB, inlinedPlan.CachedPlanSizeKB));
                AppendPlanRow(sb, "Parallelism", origPlan.DegreeOfParallelism.ToString(),
                    inlinedPlan.DegreeOfParallelism.ToString(), "", "neutral");

                var origOpt = origPlan.OptimizationLevel ?? "—";
                if (origPlan.EarlyAbortReason != null)
                    origOpt += $" ({origPlan.EarlyAbortReason})";
                var inlOpt = inlinedPlan.OptimizationLevel ?? "—";
                if (inlinedPlan.EarlyAbortReason != null)
                    inlOpt += $" ({inlinedPlan.EarlyAbortReason})";
                AppendPlanRow(sb, "Optimizer", origOpt, inlOpt, "", "neutral");

                AppendPlanRow(sb, "Plan Operators", origPlan.TotalOperators.ToString("N0", inv),
                    inlinedPlan.TotalOperators.ToString("N0", inv),
                    FormatChangePct(origPlan.TotalOperators, inlinedPlan.TotalOperators),
                    ChangeClass(origPlan.TotalOperators, inlinedPlan.TotalOperators));
                AppendPlanRow(sb, "CE Version", origPlan.CardinalityEstimatorVersion.ToString(),
                    inlinedPlan.CardinalityEstimatorVersion.ToString(), "", "neutral");
            }
            else
            {
                var plan = origPlan ?? inlinedPlan!;
                var label = origPlan != null ? "Original" : "Inlined";
                sb.AppendLine($"<tr><td colspan=\"4\" class=\"note\">Only {label} plan available</td></tr>");
            }

            sb.AppendLine("</tbody></table>");

            // Wait statistics sub-section
            var origWaits = origPlan?.WaitStats ?? new List<WaitStatEntry>();
            var inlinedWaits = inlinedPlan?.WaitStats ?? new List<WaitStatEntry>();
            if (origWaits.Count > 0 || inlinedWaits.Count > 0)
            {
                sb.AppendLine("<h3>Wait Statistics</h3>");
                sb.AppendLine("<table><thead><tr><th>Wait Type</th><th>Original</th><th>Inlined</th><th>Delta</th></tr></thead><tbody>");

                var origWaitDict = origWaits.ToDictionary(w => w.WaitType, w => w);
                var inlWaitDict = inlinedWaits.ToDictionary(w => w.WaitType, w => w);
                var allWaitTypes = new HashSet<string>(origWaitDict.Keys);
                allWaitTypes.UnionWith(inlWaitDict.Keys);

                foreach (var wt in allWaitTypes.OrderByDescending(w =>
                    Math.Max(origWaitDict.ContainsKey(w) ? origWaitDict[w].WaitTimeMs : 0,
                             inlWaitDict.ContainsKey(w) ? inlWaitDict[w].WaitTimeMs : 0)))
                {
                    origWaitDict.TryGetValue(wt, out var ow);
                    inlWaitDict.TryGetValue(wt, out var iw);
                    var origMs = ow?.WaitTimeMs ?? 0;
                    var inlMs = iw?.WaitTimeMs ?? 0;
                    var origStr = ow != null ? $"{ow.WaitTimeMs.ToString("N0", inv)} ms &times; {ow.WaitCount.ToString("N0", inv)}" : "&mdash;";
                    var inlStr = iw != null ? $"{iw.WaitTimeMs.ToString("N0", inv)} ms &times; {iw.WaitCount.ToString("N0", inv)}" : "&mdash;";
                    AppendPlanRow(sb, Esc(wt), origStr, inlStr,
                        FormatChangePct(origMs, inlMs), ChangeClass(origMs, inlMs), raw: true);
                }

                sb.AppendLine("</tbody></table>");
            }

            // Execution plan file links
            var hasOrigPlan = originalBench.ExecutionPlanXml != null;
            var hasInlinedPlan = inlinedBench.ExecutionPlanXml != null;
            if (hasOrigPlan || hasInlinedPlan)
            {
                sb.AppendLine("<div class=\"plan-links\">");
                sb.AppendLine("<span class=\"plan-links-label\">Open in SSMS:</span>");
                if (hasOrigPlan)
                    sb.AppendLine("<a href=\"plan-original.sqlplan\">plan-original.sqlplan</a>");
                if (hasInlinedPlan)
                    sb.AppendLine("<a href=\"plan-inlined.sqlplan\">plan-inlined.sqlplan</a>");
                sb.AppendLine("</div>");
            }

            sb.AppendLine("</div></section>");
        }

        // --- Per-Table IO Breakdown ---
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

            sb.AppendLine("<section class=\"card\">");
            sb.AppendLine("<div class=\"card-header togglable\" onclick=\"toggleSection('io-section')\">");
            sb.AppendLine("<h2>Per-Table IO Breakdown</h2><span class=\"toggle-icon\" id=\"io-section-icon\">&#9660;</span>");
            sb.AppendLine("</div>");
            sb.AppendLine("<div class=\"card-body\" id=\"io-section\">");
            sb.AppendLine("<div class=\"filter-bar\"><label><input type=\"checkbox\" id=\"changesOnly\" onchange=\"filterChanges()\"> Show changes only</label></div>");
            sb.AppendLine("<table id=\"io-table\"><thead><tr>");
            sb.AppendLine("<th class=\"sortable\" onclick=\"sortTable('io-table',0,'string')\">Table <span class=\"sort-arrow\"></span></th>");
            sb.AppendLine("<th class=\"sortable num\" onclick=\"sortTable('io-table',1,'number')\">Orig Reads <span class=\"sort-arrow\"></span></th>");
            sb.AppendLine("<th class=\"sortable num\" onclick=\"sortTable('io-table',2,'number')\">Inlined Reads <span class=\"sort-arrow\"></span></th>");
            sb.AppendLine("<th class=\"sortable num\" onclick=\"sortTable('io-table',3,'number')\">Change <span class=\"sort-arrow\"></span></th>");
            sb.AppendLine("</tr></thead><tbody>");

            foreach (var (name, origReads, inlinedReads) in tableRows)
            {
                var changePct = FormatChangePct(origReads, inlinedReads);
                var css = ChangeClass(origReads, inlinedReads);
                var changed = origReads != inlinedReads;
                sb.AppendLine($"<tr data-changed=\"{(changed ? "1" : "0")}\">" +
                    $"<td>{Esc(name)}</td>" +
                    $"<td class=\"num\">{origReads.ToString("N0", inv)}</td>" +
                    $"<td class=\"num\">{inlinedReads.ToString("N0", inv)}</td>" +
                    $"<td class=\"num {css}\" data-sort-value=\"{ChangeSortValue(origReads, inlinedReads).ToString(inv)}\">{changePct}</td></tr>");
            }

            sb.AppendLine("</tbody></table>");
            sb.AppendLine("</div></section>");
        }

        // --- Footer ---
        sb.AppendLine("<footer>Generated by <strong>sql-inliner</strong></footer>");
        sb.AppendLine("</div>"); // .container

        // --- JavaScript ---
        sb.AppendLine("<script>");
        sb.AppendLine(ReportJs);
        sb.AppendLine("</script>");

        sb.AppendLine("</body></html>");

        var path = Path.Combine(DirectoryPath, "benchmark.html");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        Log("Saved benchmark.html");
        return path;
    }

    // --- HTML helpers ---

    private static void AppendMetaItem(StringBuilder sb, string label, string value)
    {
        sb.AppendLine($"<div class=\"meta-item\"><span class=\"meta-label\">{Esc(label)}</span><span class=\"meta-value\">{Esc(value)}</span></div>");
    }

    private static void AppendStatCard(StringBuilder sb, string label, string origValue, string inlinedValue, string change, string changeCss)
    {
        sb.AppendLine($"<div class=\"stat-card {changeCss}-border\">");
        sb.AppendLine($"<div class=\"stat-label\">{Esc(label)}</div>");
        sb.AppendLine($"<div class=\"stat-values\"><span class=\"stat-orig\">{Esc(origValue)}</span><span class=\"stat-arrow\">&rarr;</span><span class=\"stat-inl\">{Esc(inlinedValue)}</span></div>");
        sb.AppendLine($"<div class=\"stat-change {changeCss}\">{Esc(change)}</div>");
        sb.AppendLine("</div>");
    }

    private static void AppendPlanRow(StringBuilder sb, string label, string origValue, string inlinedValue, string change, string changeCss, bool raw = false)
    {
        var origCell = raw ? origValue : Esc(origValue);
        var inlinedCell = raw ? inlinedValue : Esc(inlinedValue);
        sb.AppendLine($"<tr><td>{Esc(label)}</td><td class=\"num\">{origCell}</td><td class=\"num\">{inlinedCell}</td><td class=\"num {changeCss}\">{Esc(change)}</td></tr>");
    }

    private static string FormatChangePct(long original, long inlined)
    {
        if (original == 0)
            return inlined == 0 ? "0%" : "+inf";
        var pct = ((double)inlined - original) / original * 100;
        if (pct < -0.05) return string.Format(CultureInfo.InvariantCulture, "{0:+0.0;-0.0}%", pct);
        if (pct > 0.05) return string.Format(CultureInfo.InvariantCulture, "+{0:0.0}%", pct);
        return "0%";
    }

    private static string FormatChangePctDouble(double original, double inlined)
    {
        if (original == 0)
            return inlined == 0 ? "0%" : "+inf";
        var pct = (inlined - original) / original * 100;
        if (pct < -0.05) return string.Format(CultureInfo.InvariantCulture, "{0:+0.0;-0.0}%", pct);
        if (pct > 0.05) return string.Format(CultureInfo.InvariantCulture, "+{0:0.0}%", pct);
        return "0%";
    }

    private static string ChangeClass(long original, long inlined) =>
        original == inlined ? "neutral" : inlined < original ? "better" : "worse";

    private static string ChangeClassDouble(double original, double inlined)
    {
        var diff = Math.Abs(inlined - original);
        if (diff < 0.001) return "neutral";
        return inlined < original ? "better" : "worse";
    }

    private static double ChangeSortValue(long original, long inlined)
    {
        if (original == 0) return inlined == 0 ? 0 : double.MaxValue;
        return ((double)inlined - original) / original * 100;
    }

    private static string FormatKB(long kb)
    {
        var inv = CultureInfo.InvariantCulture;
        if (kb >= 1024)
            return $"{(kb / 1024.0).ToString("N1", inv)} MB";
        return $"{kb.ToString("N0", inv)} KB";
    }

    private static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);

    // --- Embedded CSS ---
    private const string ReportCss = @"
:root {
  --bg: #0f1117;
  --surface: #171a23;
  --surface-alt: #1c2030;
  --border: #272b3a;
  --border-light: #323750;
  --text: #e2e4ea;
  --text-dim: #7d829a;
  --text-mid: #a0a5ba;
  --blue: #4e8fff;
  --green: #34d399;
  --amber: #fbbf24;
  --red: #f87171;
  --purple: #a78bfa;
  --teal: #2dd4bf;
}

* { margin: 0; padding: 0; box-sizing: border-box; }

body {
  background: var(--bg);
  color: var(--text);
  font-family: 'Outfit', system-ui, -apple-system, sans-serif;
  min-height: 100vh;
  padding: 48px 24px;
  line-height: 1.6;
}

.container { max-width: 960px; margin: 0 auto; }

/* Header */
header { text-align: center; margin-bottom: 40px; }

header h1 {
  font-size: 1.8rem;
  font-weight: 700;
  letter-spacing: -0.02em;
  margin-bottom: 6px;
  background: linear-gradient(135deg, var(--blue), var(--teal));
  -webkit-background-clip: text;
  -webkit-text-fill-color: transparent;
  background-clip: text;
}

header .subtitle {
  color: var(--text-dim);
  font-size: 0.88rem;
  font-weight: 300;
  font-family: 'JetBrains Mono', monospace;
  margin-bottom: 20px;
}

.meta-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  justify-content: center;
}

.meta-item {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 6px 14px;
  font-size: 0.75rem;
}

.meta-label {
  color: var(--text-dim);
  font-weight: 500;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.65rem;
}

.meta-value {
  color: var(--text-mid);
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.72rem;
}

/* Cards */
.card {
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 12px;
  margin-bottom: 24px;
  overflow: hidden;
}

.card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px 22px;
  border-bottom: 1px solid var(--border);
}

.card-header.togglable { cursor: pointer; user-select: none; }
.card-header.togglable:hover { background: rgba(255,255,255,0.015); }

.card-header h2 {
  font-size: 1rem;
  font-weight: 600;
  color: var(--text);
}

.card-header h3, h3 {
  font-size: 0.88rem;
  font-weight: 500;
  color: var(--text-mid);
  margin: 18px 0 10px;
}

.toggle-icon {
  color: var(--text-dim);
  font-size: 0.7rem;
  transition: transform 0.2s;
}

.toggle-icon.collapsed { transform: rotate(-90deg); }

.card-body { padding: 18px 22px; }
.card-body.collapsed { display: none; }

/* Stat cards */
.stat-grid {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: 14px;
}

.stat-card {
  background: var(--surface-alt);
  border-radius: 10px;
  padding: 18px;
  border-left: 3px solid var(--border-light);
}

.stat-card.better-border { border-left-color: var(--green); }
.stat-card.worse-border { border-left-color: var(--red); }
.stat-card.neutral-border { border-left-color: var(--border-light); }

.stat-label {
  font-size: 0.72rem;
  font-weight: 500;
  color: var(--text-dim);
  text-transform: uppercase;
  letter-spacing: 0.05em;
  margin-bottom: 10px;
}

.stat-values {
  display: flex;
  align-items: baseline;
  gap: 8px;
  font-family: 'JetBrains Mono', monospace;
  margin-bottom: 6px;
}

.stat-orig {
  font-size: 0.92rem;
  color: var(--text-mid);
}

.stat-arrow {
  font-size: 0.75rem;
  color: var(--text-dim);
}

.stat-inl {
  font-size: 0.92rem;
  font-weight: 600;
  color: var(--text);
}

.stat-change {
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.78rem;
  font-weight: 600;
}

/* Tables */
table {
  border-collapse: collapse;
  width: 100%;
  margin: 8px 0;
}

thead th {
  text-align: left;
  padding: 8px 12px;
  font-size: 0.72rem;
  font-weight: 600;
  color: var(--text-dim);
  text-transform: uppercase;
  letter-spacing: 0.04em;
  border-bottom: 1px solid var(--border);
  font-family: 'JetBrains Mono', monospace;
  white-space: nowrap;
}

th.sortable { cursor: pointer; user-select: none; }
th.sortable:hover { color: var(--blue); }

.sort-arrow {
  font-size: 0.6rem;
  opacity: 0.4;
}

th.sort-asc .sort-arrow::after { content: ' \25B2'; opacity: 1; }
th.sort-desc .sort-arrow::after { content: ' \25BC'; opacity: 1; }

td {
  padding: 7px 12px;
  font-size: 0.82rem;
  border-bottom: 1px solid rgba(255,255,255,0.03);
  color: var(--text-mid);
}

td.num, th.num {
  text-align: right;
  font-family: 'JetBrains Mono', monospace;
  font-variant-numeric: tabular-nums;
}

tr:hover td { background: rgba(255,255,255,0.015); }

/* Change colors */
.better { color: var(--green); }
.worse { color: var(--red); }
.neutral { color: var(--text-dim); }

/* Filter bar */
.filter-bar {
  margin-bottom: 12px;
  font-size: 0.8rem;
  color: var(--text-dim);
}

.filter-bar label { cursor: pointer; display: flex; align-items: center; gap: 6px; }
.filter-bar input[type='checkbox'] { accent-color: var(--blue); }

/* Plan links */
.plan-links {
  margin-top: 16px;
  padding: 12px 14px;
  border-radius: 8px;
  background: var(--surface-alt);
  font-size: 0.8rem;
  display: flex;
  align-items: center;
  gap: 14px;
  flex-wrap: wrap;
}

.plan-links-label {
  color: var(--text-dim);
  font-weight: 500;
}

.plan-links a {
  color: var(--blue);
  text-decoration: none;
  font-family: 'JetBrains Mono', monospace;
  font-size: 0.78rem;
}

.plan-links a:hover { text-decoration: underline; }

/* Note cell */
td.note { color: var(--text-dim); font-style: italic; text-align: center; }

/* Footer */
footer {
  text-align: center;
  color: var(--text-dim);
  font-size: 0.72rem;
  margin-top: 32px;
  padding-bottom: 24px;
}

footer strong { color: var(--text-mid); font-weight: 500; }

/* Responsive */
@media (max-width: 640px) {
  body { padding: 24px 12px; }
  .stat-grid { grid-template-columns: 1fr; }
  header h1 { font-size: 1.3rem; }
  .meta-grid { flex-direction: column; align-items: center; }
}

/* Animations */
.card { animation: fadeIn 0.3s ease both; }
.card:nth-child(1) { animation-delay: 0.05s; }
.card:nth-child(2) { animation-delay: 0.1s; }
.card:nth-child(3) { animation-delay: 0.15s; }

@keyframes fadeIn {
  from { opacity: 0; transform: translateY(6px); }
  to { opacity: 1; transform: translateY(0); }
}
";

    // --- Embedded JavaScript ---
    private const string ReportJs = @"
function toggleSection(id) {
  var body = document.getElementById(id);
  var icon = document.getElementById(id + '-icon');
  if (!body) return;
  body.classList.toggle('collapsed');
  if (icon) icon.classList.toggle('collapsed');
}

function sortTable(tableId, colIdx, type) {
  var table = document.getElementById(tableId);
  if (!table) return;
  var thead = table.querySelector('thead');
  var tbody = table.querySelector('tbody');
  var rows = Array.from(tbody.querySelectorAll('tr'));
  var th = thead.querySelectorAll('th')[colIdx];

  // Determine direction
  var asc = !th.classList.contains('sort-asc');

  // Clear other sort states
  thead.querySelectorAll('th').forEach(function(h) {
    h.classList.remove('sort-asc', 'sort-desc');
  });
  th.classList.add(asc ? 'sort-asc' : 'sort-desc');

  rows.sort(function(a, b) {
    var aCell = a.cells[colIdx];
    var bCell = b.cells[colIdx];
    var aVal, bVal;

    if (type === 'number') {
      // Use data-sort-value if present, otherwise parse the text
      aVal = aCell.hasAttribute('data-sort-value')
        ? parseFloat(aCell.getAttribute('data-sort-value'))
        : parseFloat(aCell.textContent.replace(/[^0-9.\-]/g, '')) || 0;
      bVal = bCell.hasAttribute('data-sort-value')
        ? parseFloat(bCell.getAttribute('data-sort-value'))
        : parseFloat(bCell.textContent.replace(/[^0-9.\-]/g, '')) || 0;
    } else {
      aVal = aCell.textContent.trim().toLowerCase();
      bVal = bCell.textContent.trim().toLowerCase();
    }

    if (aVal < bVal) return asc ? -1 : 1;
    if (aVal > bVal) return asc ? 1 : -1;
    return 0;
  });

  rows.forEach(function(row) { tbody.appendChild(row); });
}

function filterChanges() {
  var cb = document.getElementById('changesOnly');
  var table = document.getElementById('io-table');
  if (!cb || !table) return;
  var rows = table.querySelectorAll('tbody tr');
  rows.forEach(function(row) {
    if (cb.checked && row.getAttribute('data-changed') === '0') {
      row.style.display = 'none';
    } else {
      row.style.display = '';
    }
  });
}
";

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
