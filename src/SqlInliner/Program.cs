#if !RELEASELIBRARY

using Microsoft.Data.SqlClient;
using System;
using System.CommandLine;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;

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
        var viewsConfigOption = new Option<FileInfo?>(new[] { "--views-config", "-vc" },
            "Optional path to a JSON file describing additional view definitions");
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
                viewsConfigOption,
                // TODO: DatabaseView.parser (hardcoded to TSql150Parser)
            };

        rootCommand.SetHandler(context =>
        {
            var parse = context.ParseResult;

            var connectionString = parse.GetValueForOption(connectionStringOption)!;
            var viewName = parse.GetValueForOption(viewNameOption);
            var viewPath = parse.GetValueForOption(viewPathOption);
            var stripUnusedColumns = parse.GetValueForOption(stripUnusedColumnsOption);
            var stripUnusedJoins = parse.GetValueForOption(stripUnusedJoinsOption);
            var generateCreateOrAlter = parse.GetValueForOption(generateCreateOrAlterOption);
            var outputPath = parse.GetValueForOption(outputPathOption);
            var logPath = parse.GetValueForOption(logPathOption);
            var viewsConfig = parse.GetValueForOption(viewsConfigOption);

            var cs = new SqlConnectionStringBuilder(connectionString);
            if (!cs.ContainsKey(nameof(cs.ApplicationName)))
            {
                cs.ApplicationName = ThisAssembly.AppName;
                connectionString = cs.ToString();
            }

            var connection = new DatabaseConnection(new SqlConnection(connectionString));

            if (viewsConfig != null)
            {
                var baseDir = viewsConfig.Directory?.FullName ?? Environment.CurrentDirectory;
                var data = File.ReadAllText(viewsConfig.FullName);
                var views = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(data) ?? new();
                foreach (var kvp in views)
                {
                    var path = kvp.Value;
                    if (!Path.IsPathRooted(path))
                        path = Path.Combine(baseDir, path);

                    var sql = File.ReadAllText(path);
                    connection.AddViewDefinition(DatabaseConnection.ParseObjectName(kvp.Key), sql);
                }
            }

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

        return rootCommand.Invoke(args);
    }
}


#endif