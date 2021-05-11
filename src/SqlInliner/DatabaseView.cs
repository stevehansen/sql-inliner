using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlInliner
{
    /// <summary>
    /// Describes a SQL view with information about it.
    /// </summary>
    [DebuggerDisplay("{" + nameof(ViewName) + "}")]
    public class DatabaseView
    {
        public const string BeginOriginal = "-- BEGIN ORIGINAL SQL VIEW --";
        public const string EndOriginal = "-- END ORIGINAL SQL VIEW --";

        private static readonly TSql150Parser parser = new(true, SqlEngineType.All);

        private DatabaseView(TSqlFragment tree, ReferencesVisitor references)
        {
            Tree = tree;
            References = references;
            ViewName = references.ViewName!.GetName();
        }

        public string ViewName { get; }

        public TSqlFragment Tree { get; }

        public ReferencesVisitor References { get; }

        public static (DatabaseView?, IList<ParseError>) FromSql(DatabaseConnection connection, string viewSql)
        {
            using var input = new StringReader(viewSql);
            var tree = parser.Parse(input, out var errors);

            if (errors.Count == 0)
            {
                var references = new ReferencesVisitor(connection);
                tree.Accept(references);

                // TODO: Verify that we have all required properties on the ReferencesVisitor

                foreach (var view in references.Views)
                    view.Alias ??= view.SchemaObject.BaseIdentifier; // TODO: Use something else?

                foreach (var columnReference in references.ColumnReferences)
                {
                    if (columnReference.MultiPartIdentifier.Count == 3)
                        columnReference.MultiPartIdentifier.Identifiers.RemoveAt(0);
                }

                return (new(tree, references), errors);
            }

            return (null, errors);
        }
    }
}