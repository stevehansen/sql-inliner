#if !RELEASELIBRARY

using System;
using System.CommandLine;
using System.IO;
using Microsoft.Data.SqlClient;

namespace SqlInliner.Optimize;

/// <summary>
/// System.CommandLine subcommand definition for the batch-validate workflow.
/// </summary>
public static class ValidateCommand
{
    public static Command Create(Option<FileInfo?> configOption)
    {
        var connectionStringOption = new Option<string?>("--connection-string", "-cs")
        {
            Description = "Connection string to the SQL Server database. Can also be provided via config file.",
        };
        var deployOption = new Option<bool>("--deploy", "-d")
        {
            Description = "Deploy each inlined view and run COUNT + EXCEPT validation against the original.",
        };
        var outputDirOption = new Option<DirectoryInfo?>("--output-dir", "-o")
        {
            Description = "Save inlined SQL files to a directory.",
        };
        var stopOnErrorOption = new Option<bool>("--stop-on-error")
        {
            Description = "Halt on first failure instead of continuing through all views.",
        };
        var filterOption = new Option<string?>("--filter", "-f")
        {
            Description = "Only process matching views. Supports exact name or SQL LIKE-style % wildcard (e.g. dbo.V%).",
        };
        var stripUnusedColumnsOption = new Option<bool>("--strip-unused-columns", "-suc")
        {
            DefaultValueFactory = _ => true,
            Description = "Remove columns from nested views that the outer view does not reference.",
        };
        var stripUnusedJoinsOption = new Option<bool>("--strip-unused-joins", "-suj")
        {
            Description = "Remove joins whose tables contribute no columns to the result.",
        };
        var aggressiveJoinStrippingOption = new Option<bool>("--aggressive-join-stripping")
        {
            Description = "Exclude join-condition column references from the usage count.",
        };
        var flattenDerivedTablesOption = new Option<bool>("--flatten-derived-tables", "-fdt")
        {
            Description = "Flatten derived tables (subqueries) produced by inlining into the outer query.",
        };

        var command = new Command("validate", "Batch-validate all views: inline each view and report pass/fail, optionally deploy and run COUNT + EXCEPT comparisons")
        {
            connectionStringOption,
            deployOption,
            outputDirOption,
            stopOnErrorOption,
            filterOption,
            stripUnusedColumnsOption,
            stripUnusedJoinsOption,
            aggressiveJoinStrippingOption,
            flattenDerivedTablesOption,
        };

        command.SetAction(parseResult =>
        {
            var configFile = parseResult.GetValue(configOption);
            var connectionString = parseResult.GetValue(connectionStringOption);

            // Load config file
            var config = InlinerConfig.TryLoad(configFile?.FullName);

            // Apply config defaults for connection string
            if (string.IsNullOrEmpty(connectionString))
                connectionString = config?.ConnectionString;

            if (string.IsNullOrEmpty(connectionString))
            {
                Console.Error.WriteLine("Error: --connection-string is required (via CLI or config file).");
                return;
            }

            var csb = new SqlConnectionStringBuilder(connectionString);
            if (!csb.ContainsKey(nameof(csb.ApplicationName)))
            {
                csb.ApplicationName = ThisAssembly.AppName;
                connectionString = csb.ToString();
            }

            var sqlConnection = new SqlConnection(connectionString);
            sqlConnection.Open();

            try
            {
                var connection = new DatabaseConnection(sqlConnection);

                // Register views from config
                config?.RegisterViews(connection);

                // Resolve boolean options: CLI > config > default
                var options = new InlinerOptions
                {
                    StripUnusedColumns = Program.ResolveOption(parseResult, stripUnusedColumnsOption, config?.StripUnusedColumns),
                    StripUnusedJoins = Program.ResolveOption(parseResult, stripUnusedJoinsOption, config?.StripUnusedJoins),
                    AggressiveJoinStripping = Program.ResolveOption(parseResult, aggressiveJoinStrippingOption, config?.AggressiveJoinStripping),
                    FlattenDerivedTables = Program.ResolveOption(parseResult, flattenDerivedTablesOption, config?.FlattenDerivedTables),
                };

                var sessionOptions = new ValidateSessionOptions
                {
                    Deploy = parseResult.GetValue(deployOption),
                    OutputDir = parseResult.GetValue(outputDirOption)?.FullName,
                    StopOnError = parseResult.GetValue(stopOnErrorOption),
                    Filter = parseResult.GetValue(filterOption),
                };

                var wizard = new ConsoleWizard();
                var session = new ValidateSession(connection, options, wizard);
                session.Run(sessionOptions);
            }
            finally
            {
                sqlConnection.Close();
            }
        });

        return command;
    }
}

#endif
