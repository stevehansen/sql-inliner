#if !RELEASELIBRARY

using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SqlInliner
{
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
            var rootCommand = new RootCommand(ThisAssembly.AppName)
            {
                connectionStringOption,
                viewNameOption,
                viewPathOption,
                stripUnusedColumnsOption,
                stripUnusedJoinsOption,
                generateCreateOrAlterOption,
                // TODO: DatabaseView.parser (hardcoded to TSql150Parser)
            };

            rootCommand.SetHandler((connectionString, viewName, viewPath, stripUnusedColumns, stripUnusedJoins, generateCreateOrAlter) =>
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

                Console.WriteLine(inliner.Sql);
                //return inliner.Errors.Count > 0 ? -1 : 0;
            }, connectionStringOption, viewNameOption, viewPathOption, stripUnusedColumnsOption, stripUnusedJoinsOption, generateCreateOrAlterOption);

            return rootCommand.Invoke(args);
        }
    }
}

#endif