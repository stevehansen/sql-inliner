using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlInliner
{
    /// <summary>
    /// Keeps track of database related information.
    /// </summary>
    public class DatabaseConnection
    {
        private readonly Dictionary<string, string> viewDefinitions = new();

        public DatabaseConnection(SqlConnection connection)
        {
            Connection = connection;

            Views = connection
                .Query<ObjectIdentifier>("select schema_name(schema_id) [schema], name from sys.views where object_id not in (select object_id from sys.indexes)")
                .Select(it =>
                {
                    var objectName = new SchemaObjectName();
                    objectName.Identifiers.Add(new() { Value = it.Schema });
                    objectName.Identifiers.Add(new() { Value = it.Name });
                    return objectName;
                })
                .ToArray();
        }

        public SqlConnection Connection { get; }

        /// <summary>
        /// Gets all the non-indexed views that are available in the databases. Is used to determines which table references should be inlined.
        /// </summary>
        public SchemaObjectName[] Views { get; }

        /// <summary>
        /// Checks if the specified <paramref name="objectName"/> is a non-indexed view.
        /// </summary>
        public bool IsView(SchemaObjectName objectName)
        {
            var name = objectName.GetName();
            return Views.Any(it => it.GetName() == name); // TODO: Lookup from dictionary?
        }

        /// <summary>
        /// Can be used to give the SQL for a specified view instead of using the definition from the database.
        /// </summary>
        public void AddViewDefinition(string viewName, string viewSql)
        {
            //var objectName = new SchemaObjectName();

            viewDefinitions[viewName] = viewSql;

            // TODO: Add to Views
        }

        /// <summary>
        /// Gets the original SQL definition of the specified view.
        /// </summary>
        public string GetViewDefinition(string viewName)
        {
            if (!viewDefinitions.TryGetValue(viewName, out var view))
            {
                view = Connection.Query<string>($"SELECT OBJECT_DEFINITION(object_id('{viewName}'))").First();

                var originalStart = view.IndexOf(DatabaseView.BeginOriginal, StringComparison.Ordinal);
                if (originalStart > 0)
                {
                    var originalEnd = view.IndexOf(DatabaseView.EndOriginal, StringComparison.Ordinal);
                    if (originalEnd > 0)
                    {
                        originalStart += DatabaseView.BeginOriginal.Length;

                        //view = view[originalStart..originalEnd].Trim();
                        view = view.Substring(originalStart,  originalEnd - originalStart).Trim();
                    }
                }

                viewDefinitions[viewName] = view;
            }

            return view;
        }

        // ReSharper disable once ClassNeverInstantiated.Local
        private sealed class ObjectIdentifier
        {
            public string Schema { get; set; } = null!;

            public string Name { get; set; } = null!;
        }
    }
}