#if !RELEASELIBRARY

using Microsoft.Data.SqlClient;
using SqlInliner.Optimize;
using System;
using System.CommandLine;
using System.IO;

namespace SqlInliner;

internal static class Program
{
    private static int Main(string[] args)
    {
        var configOption = new Option<FileInfo?>("--config", "-c") { Description = "Path to a sqlinliner.json configuration file. Auto-discovers sqlinliner.json in the current directory if not specified", Recursive = true };
        var connectionStringOption = new Option<string>("--connection-string", "-cs") { Description = "Connection string to the SQL Server database" };
        var viewNameOption = new Option<string>("--view-name", "-vn") { Description = "Fully qualified name of the view to inline (e.g. dbo.MyView)" };
        var viewPathOption = new Option<FileInfo>("--view-path", "-vp") { Description = "Path to a .sql file containing a CREATE VIEW statement" };
        var stripUnusedColumnsOption = new Option<bool>("--strip-unused-columns", "-suc") { DefaultValueFactory = _ => true, Description = "Remove columns from nested views that the outer view does not reference" };
        var stripUnusedJoinsOption = new Option<bool>("--strip-unused-joins", "-suj") { Description = "Remove joins whose tables contribute no columns to the result. Use @join:unique / @join:required hints on JOINs to allow safe removal (see README)" };
        var aggressiveJoinStrippingOption = new Option<bool>("--aggressive-join-stripping") { Description = "Exclude join-condition column references from the usage count. Allows stripping joins where the table only appears in its own ON clause. Use with care: can change results for INNER JOINs where the ON clause filters rows" };
        var flattenDerivedTablesOption = new Option<bool>("--flatten-derived-tables", "-fdt") { Description = "Flatten simple derived tables (subqueries) produced by inlining into the outer query. Experimental: Phase 1 handles single-table subqueries without GROUP BY, HAVING, TOP, DISTINCT, or UNION" };
        var generateCreateOrAlterOption = new Option<bool>("--generate-create-or-alter") { DefaultValueFactory = _ => true, Description = "Wrap output in a CREATE OR ALTER VIEW statement" };
        var outputPathOption = new Option<FileInfo?>("--output-path", "-op") { Description = "Write the resulting SQL to a file instead of the console" };
        var logPathOption = new Option<FileInfo?>("--log-path", "-lp") { Description = "Write warnings, errors, and timing info to a file" };

        var description = $"""
            {ThisAssembly.AppName} — Optimizes SQL Server views by inlining nested views into a single flattened query.

            At least one of --view-name or --view-path is required.
            When both are supplied, --view-path provides the main view while nested views are fetched from the database via --connection-string.

            Examples:
              sqlinliner -cs "Server=.;Database=Test;Integrated Security=true" -vn "dbo.VHeavy" --strip-unused-joins
              sqlinliner -vp "./views/MyView.sql" --strip-unused-joins
              sqlinliner -vp "./views/MyView.sql" --generate-create-or-alter false
              sqlinliner -c sqlinliner.json -vn dbo.VHeavy

            Join hints — annotate JOINs in your SQL to enable safe join removal:
              LEFT JOIN /* @join:unique */ dbo.Address a ON a.PersonId = p.Id
              INNER JOIN /* @join:unique @join:required */ dbo.Status s ON s.Id = p.StatusId

            For programmatic usage, install the SqlInliner.Library NuGet package:
              dotnet add package SqlInliner.Library
            """;

        var rootCommand = new RootCommand(description)
            {
                configOption,
                connectionStringOption,
                viewNameOption,
                viewPathOption,
                stripUnusedColumnsOption,
                stripUnusedJoinsOption,
                aggressiveJoinStrippingOption,
                flattenDerivedTablesOption,
                generateCreateOrAlterOption,
                outputPathOption,
                logPathOption,
            };

        rootCommand.Add(OptimizeCommand.Create(configOption));

        rootCommand.SetAction(parseResult =>
        {
            var configFile = parseResult.GetValue(configOption);
            var connectionString = parseResult.GetValue(connectionStringOption);
            var viewName = parseResult.GetValue(viewNameOption);
            var viewPath = parseResult.GetValue(viewPathOption);

            // Load config file
            var config = InlinerConfig.TryLoad(configFile?.FullName);

            // Apply config defaults for connection string
            if (string.IsNullOrEmpty(connectionString))
                connectionString = config?.ConnectionString;

            // Resolve boolean options: CLI > config > default
            var stripUnusedColumns = ResolveOption(parseResult, stripUnusedColumnsOption, config?.StripUnusedColumns);
            var stripUnusedJoins = ResolveOption(parseResult, stripUnusedJoinsOption, config?.StripUnusedJoins);
            var aggressiveJoinStripping = ResolveOption(parseResult, aggressiveJoinStrippingOption, config?.AggressiveJoinStripping);
            var flattenDerivedTables = ResolveOption(parseResult, flattenDerivedTablesOption, config?.FlattenDerivedTables);
            var generateCreateOrAlter = ResolveOption(parseResult, generateCreateOrAlterOption, config?.GenerateCreateOrAlter);
            var outputPath = parseResult.GetValue(outputPathOption);
            var logPath = parseResult.GetValue(logPathOption);

            // Check for required view input
            if (string.IsNullOrEmpty(viewName) && viewPath == null)
            {
                Console.Error.WriteLine("Error: At least --view-name or --view-path is required.");
                Console.Error.WriteLine("Run 'sqlinliner --help' for usage information.");
                return;
            }

            // Create connection — only requires a SQL connection if a connection string is available
            DatabaseConnection connection;
            if (!string.IsNullOrEmpty(connectionString))
            {
                var cs = new SqlConnectionStringBuilder(connectionString);
                if (!cs.ContainsKey(nameof(cs.ApplicationName)))
                {
                    cs.ApplicationName = ThisAssembly.AppName;
                    connectionString = cs.ToString();
                }
                connection = new DatabaseConnection(new SqlConnection(connectionString));
            }
            else
            {
                connection = new DatabaseConnection();
            }

            // Register views from config
            config?.RegisterViews(connection);

            string viewSql;
            if (!string.IsNullOrEmpty(viewName))
                viewSql = connection.GetViewDefinition(viewName);
            else if (viewPath != null)
                viewSql = File.ReadAllText(viewPath.FullName);
            else
                throw new InvalidOperationException("At least --view-name or --view-path is required.");

            if (generateCreateOrAlter)
                viewSql = DatabaseView.CreateOrAlter(viewSql);

            var inliner = new DatabaseViewInliner(connection, viewSql, new()
            {
                StripUnusedColumns = stripUnusedColumns,
                StripUnusedJoins = stripUnusedJoins,
                AggressiveJoinStripping = aggressiveJoinStripping,
                FlattenDerivedTables = flattenDerivedTables,
            });

            if (outputPath != null)
                File.WriteAllText(outputPath.FullName, inliner.Sql);
            else
                Console.WriteLine(inliner.Sql);

            if (logPath != null)
            {
                var result = inliner.Result;
                var log = $"Elapsed: {result?.Elapsed}\n" +
                          $"Warnings ({inliner.Warnings.Count}):\n{string.Join("\n", inliner.Warnings)}\n" +
                          $"Errors ({inliner.Errors.Count}):\n{string.Join("\n", inliner.Errors)}\n";
                File.WriteAllText(logPath.FullName, log);
            }
        });

        if (args.Length == 0)
            return rootCommand.Parse(["--help"]).Invoke();

        return rootCommand.Parse(args).Invoke();
    }

    /// <summary>
    /// Resolves a boolean option value with precedence: CLI > config > default.
    /// </summary>
    private static bool ResolveOption(ParseResult parseResult, Option<bool> option, bool? configValue)
    {
        // If user explicitly provided the option on CLI, use that value
        if (parseResult.GetResult(option) != null)
            return parseResult.GetValue(option);

        // Fall back to config value if available
        if (configValue.HasValue)
            return configValue.Value;

        // Fall back to option's default value
        return parseResult.GetValue(option);
    }
}


#endif
