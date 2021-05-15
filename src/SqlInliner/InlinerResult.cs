using System;
using System.Collections.Generic;

namespace SqlInliner
{
    public sealed class InlinerResult
    {
        public InlinerResult(DatabaseViewInliner inliner, TimeSpan elapsed, string originalSql, Dictionary<string, DatabaseView> knownViews, string convertedSql)
        {
            Elapsed = elapsed;
            KnownViews = knownViews;
            ConvertedSql = convertedSql;

            // TODO: Convert to StringBuilder?
            Sql = $"/*\n-- Generated on {DateTime.Now:G} by {ThisAssembly.AppName} in {elapsed}\n{DatabaseView.BeginOriginal}\n{originalSql}\n{DatabaseView.EndOriginal}\n\n-- Referenced views ({knownViews.Count}):\n{string.Join("\n", knownViews.Keys)}\n\n-- Removed: {inliner.TotalSelectColumnsStripped} select columns and {inliner.TotalJoinsStripped} joins\n\n-- Warnings ({inliner.Warnings.Count}):\n{string.Join("\n", inliner.Warnings)}\n\n-- Errors ({inliner.Errors.Count}):\n{string.Join("\n", inliner.Errors)}\n\n*/\n{convertedSql}\n\n";
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
        /// Gets the converted sql that can be used as a replacement for the create view statement.
        /// </summary>
        public string Sql { get; }
    }
}