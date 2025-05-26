#if !RELEASELIBRARY

using Microsoft.Data.SqlClient;
using System;
using System.CommandLine;
using System.IO;

namespace SqlInliner;

internal static class Program
{
    private static int Main(string[] args)
    {
        var connectionStringOption = new Option<string>(new[] { "--connection-string", "-cs" }, "Contains the connection string to connect to the database");
        var viewNameOption = new Option<string>(new[] { "--view-name", "-vn" }, "The name of the view to inline");
        var viewPathOption = new Option<FileInfo>(new[] { "--view-path", "-vp" }, "The path of the view as a .sql file (including create statement)");
        var stripUnusedColumnsOption = new Option<bool>(new[] { "--strip-unused-columns", "-suc" }, () => true);
        var stripUnusedJoinsOption = new Option<bool>(new[] { "--strip-unused-joins", "-suj" });
        var generateCreateOrAlterOption = new Option<bool>("--generate-create-or-alter", () => true);
        var outputPathOption = new Option<FileInfo?>(new[] { "--output-path", "-op" }, "Optional path of the file to write the resulting SQL to");
        var logPathOption = new Option<FileInfo?>(new[] { "--log-path", "-lp" }, "Optional path of the file to write debug information to");
        var rootCommand = new RootCommand(ThisAssembly.AppName)
            {
                connectionStringOption,
                viewNameOption,
                viewPathOption,
                stripUnusedColumnsOption,
                stripUnusedJoinsOption,
                generateCreateOrAlterOption,
                outputPathOption,
                logPathOption,
                // TODO: DatabaseView.parser (hardcoded to TSql150Parser)
            };

        rootCommand.SetHandler((connectionString, viewName, viewPath, stripUnusedColumns, stripUnusedJoins, generateCreateOrAlter, outputPath, logPath) =>
        {
            var cs = new SqlConnectionStringBuilder(connectionString);
            if (!cs.ContainsKey(nameof(cs.ApplicationName)))
            {
                cs.ApplicationName = ThisAssembly.AppName;
                connectionString = cs.ToString();
            }

            var connection = new DatabaseConnection(new SqlConnection(connectionString));

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
            //return inliner.Errors.Count > 0 ? -1 : 0;
        }, connectionStringOption, viewNameOption, viewPathOption, stripUnusedColumnsOption, stripUnusedJoinsOption, generateCreateOrAlterOption, outputPathOption, logPathOption);

        return rootCommand.Invoke(args);
    }
}


#endif