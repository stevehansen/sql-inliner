#if !RELEASELIBRARY

using System;
using System.CommandLine;
using System.IO;
using Microsoft.Data.SqlClient;

namespace SqlInliner.Optimize;

/// <summary>
/// System.CommandLine subcommand definition for the analyze workflow.
/// </summary>
public static class AnalyzeCommand
{
    public static Command Create(Option<FileInfo?> configOption)
    {
        var connectionStringOption = new Option<string?>("--connection-string", "-cs")
        {
            Description = "Connection string to the SQL Server database. Can also be provided via config file.",
        };
        var filterOption = new Option<string?>("--filter", "-f")
        {
            Description = "Only process matching views. Supports exact name or SQL LIKE-style % wildcard (e.g. dbo.V%).",
        };
        var daysOption = new Option<int>("--days")
        {
            DefaultValueFactory = _ => 30,
            Description = "Query Store lookback period in days (default: 30).",
        };
        var minExecutionsOption = new Option<int>("--min-executions")
        {
            DefaultValueFactory = _ => 5,
            Description = "Minimum execution count for Query Store stats (default: 5).",
        };
        var topOption = new Option<int?>("--top")
        {
            Description = "Limit output to the top N candidates by score.",
        };
        var generateScriptOption = new Option<bool>("--generate-script")
        {
            Description = "Generate a SQL Server stored procedure for extracting analyze data, instead of running analysis.",
        };
        var fromFileOption = new Option<FileInfo?>("--from-file")
        {
            Description = "Load analyze data from a previously exported JSON file (offline mode, no database required).",
        };
        var outputPathOption = new Option<FileInfo?>("--output-path", "-op")
        {
            Description = "Write output to a file instead of the console (used with --generate-script).",
        };

        var command = new Command("analyze", "Analyze views to identify inlining candidates: ranks by nesting depth, Query Store stats, and inlined status")
        {
            connectionStringOption,
            filterOption,
            daysOption,
            minExecutionsOption,
            topOption,
            generateScriptOption,
            fromFileOption,
            outputPathOption,
        };

        command.SetAction(parseResult =>
        {
            var configFile = parseResult.GetValue(configOption);
            var connectionString = parseResult.GetValue(connectionStringOption);
            var generateScript = parseResult.GetValue(generateScriptOption);
            var fromFile = parseResult.GetValue(fromFileOption);
            var outputPath = parseResult.GetValue(outputPathOption);

            // Load config file
            var config = InlinerConfig.TryLoad(configFile?.FullName);

            // Apply config defaults for connection string
            if (string.IsNullOrEmpty(connectionString))
                connectionString = config?.ConnectionString;

            // Mutual exclusion validation
            if (generateScript && fromFile != null)
            {
                Console.Error.WriteLine("Error: --generate-script and --from-file cannot be used together.");
                return;
            }

            if (fromFile != null && !string.IsNullOrEmpty(connectionString))
            {
                Console.Error.WriteLine("Error: --from-file and --connection-string cannot be used together (offline mode needs no database).");
                return;
            }

            var sessionOptions = new AnalyzeSessionOptions
            {
                Filter = parseResult.GetValue(filterOption),
                Days = parseResult.GetValue(daysOption),
                MinExecutions = parseResult.GetValue(minExecutionsOption),
                Top = parseResult.GetValue(topOption),
            };

            // Mode 1: Generate extraction script
            if (generateScript)
            {
                var script = AnalyzeSession.GenerateScript(sessionOptions.Days, sessionOptions.MinExecutions);

                if (outputPath != null)
                {
                    File.WriteAllText(outputPath.FullName, script);
                    Console.WriteLine($"Extraction script written to: {outputPath.FullName}");
                }
                else
                {
                    Console.Write(script);
                }

                return;
            }

            // Mode 2: Offline analysis from file
            if (fromFile != null)
            {
                if (!fromFile.Exists)
                {
                    Console.Error.WriteLine($"Error: File not found: {fromFile.FullName}");
                    return;
                }

                var wizard = new ConsoleWizard();
                var session = new AnalyzeSession(wizard);
                session.RunFromFile(fromFile.FullName, sessionOptions);
                return;
            }

            // Mode 3: Live database analysis (existing behavior)
            if (string.IsNullOrEmpty(connectionString))
            {
                Console.Error.WriteLine("Error: --connection-string is required (via CLI or config file). Use --from-file for offline analysis or --generate-script to create an extraction script.");
                return;
            }

            var store = CredentialStoreFactory.Create(out _);
            connectionString = ConnectionStringHelper.Resolve(connectionString, store);

            var sqlConnection = new SqlConnection(connectionString);
            sqlConnection.Open();

            try
            {
                var connection = new DatabaseConnection(sqlConnection);

                // Register views from config
                config?.RegisterViews(connection);

                var wizard = new ConsoleWizard();
                var session = new AnalyzeSession(connection, wizard);
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
