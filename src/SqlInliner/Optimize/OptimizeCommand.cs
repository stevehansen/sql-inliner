#if !RELEASELIBRARY

using System;
using System.CommandLine;
using Microsoft.Data.SqlClient;

namespace SqlInliner.Optimize;

/// <summary>
/// System.CommandLine subcommand definition for the optimize workflow.
/// </summary>
public static class OptimizeCommand
{
    public static Command Create()
    {
        var connectionStringOption = new Option<string>("--connection-string", "-cs")
        {
            Description = "Connection string to the SQL Server database (required)",
            Required = true,
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
            var connectionString = parseResult.GetValue(connectionStringOption)!;
            var viewName = parseResult.GetValue(viewNameOption);

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
                var wizard = new ConsoleWizard();
                var session = new OptimizeSession(connection, wizard, Environment.CurrentDirectory);
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
