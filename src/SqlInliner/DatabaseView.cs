using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlInliner;

/// <summary>
/// Describes a SQL view with information about it.
/// </summary>
[DebuggerDisplay("{" + nameof(ViewName) + "}")]
public sealed class DatabaseView
{
    /// <summary>
    /// Marker that is added inside the comment to indicate the begin of the original SQL view definition.
    /// </summary>
    public const string BeginOriginal = "-- BEGIN ORIGINAL SQL VIEW --";

    /// <summary>
    /// Marker that is added inside the comment to indicate the end of the original SQL view definition.
    /// </summary>
    public const string EndOriginal = "-- END ORIGINAL SQL VIEW --";

    private static readonly TSql150Parser parser = new(true, SqlEngineType.All); // TODO: Configure which parser to use?

    private DatabaseView(TSqlFragment tree, ReferencesVisitor references)
    {
        Tree = tree;
        References = references;
        ViewName = references.ViewName!.GetName();
    }

    /// <summary>
    /// Gets the two-part quoted view name.
    /// </summary>
    public string ViewName { get; }

    /// <summary>
    /// Gets the parsed tree of the view.
    /// </summary>
    public TSqlFragment Tree { get; }

    /// <summary>
    /// Gets additional information about the parsed tree.
    /// </summary>
    public ReferencesVisitor References { get; }

    /// <summary>
    /// Creates a <see cref="DatabaseView"/> instance for the specified <paramref name="viewSql"/>.
    /// </summary>
    public static (DatabaseView?, IList<ParseError>) FromSql(DatabaseConnection connection, string viewSql)
    {
        using var input = new StringReader(viewSql);
        var tree = parser.Parse(input, out var errors);

        if (errors.Count == 0)
        {
            var references = new ReferencesVisitor(connection);
            tree.Accept(references);

            // TODO: Verify that we have all required properties on the ReferencesVisitor

            var usedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var reference in references.NamedTableReferences)
            {
                if (reference.Alias != null)
                    usedAliases.Add(reference.Alias.Value);
            }

            foreach (var view in references.Views)
            {
                if (view.Alias == null)
                {
                    var implicitAlias = view.SchemaObject.BaseIdentifier.Value;
                    if (!usedAliases.Contains(implicitAlias))
                    {
                        view.Alias = new Identifier { Value = implicitAlias };
                        usedAliases.Add(implicitAlias);
                    }
                }
            }

            foreach (var columnReference in references.ColumnReferences)
            {
                if (columnReference.MultiPartIdentifier.Count == 3)
                    columnReference.MultiPartIdentifier.Identifiers.RemoveAt(0);
            }

            return (new(tree, references), errors);
        }

        return (null, errors);
    }

    /// <summary>
    /// Converts a CREATE VIEW statement in a CREATE OR ALTER VIEW statement.
    /// </summary>
    public static string CreateOrAlter(string viewSql)
    {
        return Regex.Replace(viewSql, @"\bCREATE\b\s+VIEW", "CREATE OR ALTER VIEW", RegexOptions.IgnoreCase);
    }
}