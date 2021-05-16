using System.Collections.Generic;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlInliner
{
    /// <summary>
    /// Gets additional information about a SQL view parsed tree
    /// </summary>
    public sealed class ReferencesVisitor : TSqlFragmentVisitor
    {
        private readonly DatabaseConnection connection;

        private QuerySpecification? currentQuery;

        internal ReferencesVisitor(DatabaseConnection connection)
        {
            this.connection = connection;
        }

        /// <summary>
        /// Gets the body of the CREATE VIEW or CREATE OR ALTER VIEW statement.
        /// </summary>
        public ViewStatementBody? Body { get; set; }

        /// <summary>
        /// Gets all the column references inside the body.
        /// </summary>
        public List<ColumnReferenceExpression> ColumnReferences { get; } = new();

        /// <summary>
        /// Gets all the named table references (e.g. tables and views) inside the body.
        /// </summary>
        public List<NamedTableReference> NamedTableReferences { get; } = new();

        /// <summary>
        /// Gets the first specification (i.e. a SELECT x FROM y statement) of the view of the CREATE VIEW or CREATE OR ALTER VIEW statement
        /// </summary>
        public QuerySpecification? Query { get; set; }

        /// <summary>
        /// Gets all the table references inside the body.
        /// </summary>
        public List<NamedTableReference> Tables { get; } = new();

        /// <summary>
        /// Gets the name of the view of the CREATE VIEW or CREATE OR ALTER VIEW statement
        /// </summary>
        public SchemaObjectName? ViewName { get; set; }

        /// <summary>
        /// Gets all the view references inside the body.
        /// </summary>
        public List<NamedTableReference> Views { get; } = new();

        /// <inheritdoc />
        public override void ExplicitVisit(FunctionCall node)
        {
            base.ExplicitVisit(node);

            // NOTE: Remove known built-in function arguments, e.g. DATEADD(month, 1, t.Column) should only report t.Column
            if (node.FunctionName?.QuoteType == QuoteType.NotQuoted && ParametersToIgnore.HasIgnoredParameters(node.FunctionName.Value, out var indexes))
            {
                foreach (var idx in indexes!)
                {
                    if (node.Parameters[idx] is ColumnReferenceExpression columnReference)
                        ColumnReferences.Remove(columnReference);
                }
            }
        }

        /// <inheritdoc />
        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            if (node.MultiPartIdentifier != null)
                ColumnReferences.Add(node);

            base.ExplicitVisit(node);
        }

        /// <inheritdoc />
        public override void ExplicitVisit(CreateOrAlterViewStatement node)
        {
            ViewName = node.SchemaObjectName;
            Body = node;

            base.ExplicitVisit(node);
        }

        /// <inheritdoc />
        public override void ExplicitVisit(CreateViewStatement node)
        {
            ViewName = node.SchemaObjectName;
            Body = node;

            base.ExplicitVisit(node);
        }

        /// <inheritdoc />
        public override void ExplicitVisit(NamedTableReference node)
        {
            NamedTableReferences.Add(node);

            if (connection.IsView(node.SchemaObject))
                Views.Add(node);
            else
                Tables.Add(node);

            base.ExplicitVisit(node);
        }

        /// <inheritdoc />
        public override void ExplicitVisit(QuerySpecification node) // TODO: Handle UNION
        {
            Query ??= node;

            // TODO: Process nested queries?
            var previous = currentQuery;
            currentQuery = node;

            base.ExplicitVisit(node);

            currentQuery = previous;
        }
    }
}
