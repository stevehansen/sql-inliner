using System;
using System.Collections.Generic;

namespace SqlInliner;

/// <summary>
/// Gets extra information from inlining the SQL view.
/// </summary>
public sealed class InlinerResult
{
    internal InlinerResult(DatabaseViewInliner inliner, TimeSpan elapsed, string originalSql, Dictionary<string, DatabaseView> knownViews, string convertedSql, InlinerOptions options)
    {
        Elapsed = elapsed;
        KnownViews = knownViews;
        ConvertedSql = convertedSql;

        MetadataComment = $"/*\n-- Generated on {DateTime.Now:G} by {ThisAssembly.AppName} in {elapsed}\n{DatabaseView.BeginOriginal}\n{originalSql}\n{DatabaseView.EndOriginal}\n\n-- Options: {options.ToMetadataString()}\n\n-- Referenced views ({knownViews.Count}):\n{string.Join("\n", knownViews.Keys)}\n\n-- Removed: {inliner.TotalSelectColumnsStripped} select columns and {inliner.TotalJoinsStripped} joins\n\n-- Warnings ({inliner.Warnings.Count}):\n{string.Join("\n", inliner.Warnings)}\n\n-- Errors ({inliner.Errors.Count}):\n{string.Join("\n", inliner.Errors)}\n\n*/\n";
        Sql = MetadataComment + convertedSql + "\n\n";
    }

    /// <summary>
    /// Gets the total time that it took to inline the view statement.
    /// </summary>
    public TimeSpan Elapsed { get; }

    /// <summary>
    /// Gets the name of views (including self) that are/were referenced by the view.
    /// </summary>
    public Dictionary<string, DatabaseView> KnownViews { get; }

    /// <summary>
    /// Gets the converted sql containing only the inlined create view statement.
    /// </summary>
    public string ConvertedSql { get; }

    /// <summary>
    /// Gets the metadata comment block containing original SQL, options, referenced views, and statistics.
    /// </summary>
    public string MetadataComment { get; }

    /// <summary>
    /// Gets the converted sql that can be used as a replacement for the create view statement.
    /// </summary>
    public string Sql { get; }
}