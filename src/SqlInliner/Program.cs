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
        var connectionStringOption = new Option<string>("--connection-string", "-cs") { Description = "Contains the connection string to connect to the database" };
        var viewNameOption = new Option<string>("--view-name", "-vn") { Description = "The name of the view to inline" };
        var viewPathOption = new Option<FileInfo>("--view-path", "-vp") { Description = "The path of the view as a .sql file (including create statement)" };
        var stripUnusedColumnsOption = new Option<bool>("--strip-unused-columns", "-suc") { DefaultValueFactory = _ => true };
        var stripUnusedJoinsOption = new Option<bool>("--strip-unused-joins", "-suj");
        var generateCreateOrAlterOption = new Option<bool>("--generate-create-or-alter") { DefaultValueFactory = _ => true };
        var outputPathOption = new Option<FileInfo?>("--output-path", "-op") { Description = "Optional path of the file to write the resulting SQL to" };
        var logPathOption = new Option<FileInfo?>("--log-path", "-lp") { Description = "Optional path of the file to write debug information to" };
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

        rootCommand.SetAction(parseResult =>
        {
            var connectionString = parseResult.GetValue(connectionStringOption);
            var viewName = parseResult.GetValue(viewNameOption);
            var viewPath = parseResult.GetValue(viewPathOption);
            var stripUnusedColumns = parseResult.GetValue(stripUnusedColumnsOption);
            var stripUnusedJoins = parseResult.GetValue(stripUnusedJoinsOption);
            var generateCreateOrAlter = parseResult.GetValue(generateCreateOrAlterOption);
            var outputPath = parseResult.GetValue(outputPathOption);
            var logPath = parseResult.GetValue(logPathOption);

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
        });

        return rootCommand.Parse(args).Invoke();
    }
}


#endif
