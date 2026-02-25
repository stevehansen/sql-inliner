using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlInliner;

/// <summary>
/// Keeps track of database related information.
/// </summary>
public sealed class DatabaseConnection
{
    // TODO: Wrap SchemaObjectName

    private readonly Dictionary<string, string> viewDefinitions = new();

    /// <summary>
    /// Creates a new instance of <see cref="DatabaseConnection"/> that will use the specified <paramref name="connection"/> to get information about views.
    /// </summary>
    public DatabaseConnection(IDbConnection connection)
    {
        Connection = connection;

        Views = connection
            .Query<ObjectIdentifier>("select schema_name(schema_id) [schema], name from sys.views where object_id not in (select object_id from sys.indexes)")
            .Select(it => ToObjectName(it.Schema, it.Name))
            .ToList();
    }

    /// <summary>
    /// Creates a new instance of <see cref="DatabaseConnection"/>.
    /// </summary>
    public DatabaseConnection()
    {
        Views = new();
    }

    /// <summary>
    /// Gets the optional database connection to fetch view information from.
    /// </summary>
    public IDbConnection? Connection { get; }

    /// <summary>
    /// Gets all the non-indexed views that are available in the databases. Is used to determines which table references should be inlined.
    /// </summary>
    public List<SchemaObjectName> Views { get; }

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
    public void AddViewDefinition(SchemaObjectName viewName, string viewSql)
    {
        viewDefinitions[viewName.GetName()] = viewSql;
        Views.Add(viewName);
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

    /// <summary>
    /// Returns the raw SQL definition of a view without extracting original SQL from markers.
    /// Returns <c>null</c> if the view is not found.
    /// </summary>
    public string? TryGetRawViewDefinition(string viewName)
    {
        if (viewDefinitions.TryGetValue(viewName, out var view))
            return view;

        if (Connection == null)
            return null;

        return Connection.Query<string>($"SELECT OBJECT_DEFINITION(object_id('{viewName}'))").FirstOrDefault();
    }

    /// <summary>
    /// Executes a scalar query and returns the result.
    /// </summary>
    public T ExecuteScalar<T>(string sql)
    {
        if (Connection == null)
            throw new InvalidOperationException("No database connection available.");

        return Connection.ExecuteScalar<T>(sql);
    }

    /// <summary>
    /// Executes a non-query SQL statement and returns the number of affected rows.
    /// </summary>
    public int ExecuteNonQuery(string sql)
    {
        if (Connection == null)
            throw new InvalidOperationException("No database connection available.");

        return Connection.Execute(sql);
    }

    // ReSharper disable once ClassNeverInstantiated.Local
    private sealed class ObjectIdentifier
    {
        public string Schema { get; set; } = null!;

        public string Name { get; set; } = null!;
    }

    /// <summary>
    /// Creates a <see cref="SchemaObjectName"/> for the specified <paramref name="schema"/> and <paramref name="name"/>.
    /// </summary>
    public static SchemaObjectName ToObjectName(string schema, string name)
    {
        var objectName = new SchemaObjectName();
        objectName.Identifiers.Add(new() { Value = schema });
        objectName.Identifiers.Add(new() { Value = name });
        return objectName;
    }

    /// <summary>
    /// Parses a string like "schema.name" or "name" into a <see cref="SchemaObjectName"/>.
    /// Bracket-quoted identifiers (e.g. [dbo].[VName]) are supported. Defaults to "dbo" schema when not specified.
    /// </summary>
    public static SchemaObjectName ParseObjectName(string name)
    {
        name = name.Replace("[", "").Replace("]", "");

        var dotIndex = name.LastIndexOf('.');
        if (dotIndex >= 0)
            return ToObjectName(name.Substring(0, dotIndex), name.Substring(dotIndex + 1));

        return ToObjectName("dbo", name);
    }
}