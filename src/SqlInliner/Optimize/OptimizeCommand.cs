#if !RELEASELIBRARY

using System;
using System.CommandLine;
using System.IO;
using Microsoft.Data.SqlClient;

namespace SqlInliner.Optimize;

/// <summary>
/// System.CommandLine subcommand definition for the optimize workflow.
/// </summary>
public static class OptimizeCommand
{
    public static Command Create(Option<FileInfo?> configOption)
    {
        var connectionStringOption = new Option<string?>("--connection-string", "-cs")
        {
            Description = "Connection string to the SQL Server database. Can also be provided via config file.",
        };
        var viewNameOption = new Option<string?>("--view-name", "-vn")
        {
            Description = "Fully qualified view name (e.g. dbo.VPeople). If omitted, you will be prompted.",
        };

        var command = new Command("optimize", "Interactive optimization wizard: inline, deploy, validate, and benchmark a view against a backup database")
        {
            connectionStringOption,
            viewNameOption,
        };

        command.SetAction(parseResult =>
        {
            var configFile = parseResult.GetValue(configOption);
            var connectionString = parseResult.GetValue(connectionStringOption);
            var viewName = parseResult.GetValue(viewNameOption);

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

                // Build initial options from config (nullable bools → concrete defaults)
                InlinerOptions? configOptions = null;
                if (config != null)
                {
                    configOptions = new InlinerOptions
                    {
                        StripUnusedColumns = config.StripUnusedColumns ?? true,
                        StripUnusedJoins = config.StripUnusedJoins ?? false,
                        AggressiveJoinStripping = config.AggressiveJoinStripping ?? false,
                        FlattenDerivedTables = config.FlattenDerivedTables ?? false,
                    };
                }

                var wizard = new ConsoleWizard();
                var session = new OptimizeSession(connection, wizard, Environment.CurrentDirectory, configOptions);
                session.Run(viewName);
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
