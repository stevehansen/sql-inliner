#if !RELEASELIBRARY

using System;
using System.CommandLine;
using System.IO;
using Microsoft.Data.SqlClient;

namespace SqlInliner.Optimize;

/// <summary>
/// System.CommandLine subcommand definition for the verify workflow.
/// </summary>
public static class VerifyCommand
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
        var stopOnErrorOption = new Option<bool>("--stop-on-error")
        {
            Description = "Halt on first failure instead of continuing through all views.",
        };
        var timeoutOption = new Option<int>("--timeout", "-t")
        {
            DefaultValueFactory = _ => 120,
            Description = "Query timeout in seconds for COUNT and EXCEPT queries (default: 120).",
        };

        var command = new Command("verify", "Verify deployed inlined views against their embedded originals: auto-detect markers, deploy original as temp view, run COUNT + EXCEPT comparisons")
        {
            connectionStringOption,
            filterOption,
            stopOnErrorOption,
            timeoutOption,
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

            var store = CredentialStoreFactory.Create(out _);
            connectionString = ConnectionStringHelper.Resolve(connectionString, store);

            var sqlConnection = new SqlConnection(connectionString);
            sqlConnection.Open();

            try
            {
                var connection = new DatabaseConnection(sqlConnection);

                // Register views from config
                config?.RegisterViews(connection);

                var sessionOptions = new VerifySessionOptions
                {
                    Filter = parseResult.GetValue(filterOption),
                    StopOnError = parseResult.GetValue(stopOnErrorOption),
                    TimeoutSeconds = parseResult.GetValue(timeoutOption),
                };

                var wizard = new ConsoleWizard();
                var session = new VerifySession(connection, wizard);
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
