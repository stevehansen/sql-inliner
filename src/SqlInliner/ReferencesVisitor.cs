using System.Collections.Generic;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlInliner
{
    public sealed class ReferencesVisitor : TSqlFragmentVisitor
    {
        private readonly DatabaseConnection connection;

        private QuerySpecification? currentQuery;

        public ReferencesVisitor(DatabaseConnection connection)
        {
            this.connection = connection;
        }

        public ViewStatementBody? Body { get; set; }

        public List<ColumnReferenceExpression> ColumnReferences { get; } = new();

        public List<NamedTableReference> NamedTableReferences { get; } = new();

        public QuerySpecification? Query { get; set; }

        public List<NamedTableReference> Tables { get; } = new();

        public SchemaObjectName? ViewName { get; set; }

        public List<NamedTableReference> Views { get; } = new();

        /// <inheritdoc />
        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            if (node.MultiPartIdentifier != null)
                ColumnReferences.Add(node); // TODO: Remove known built-in function arguments, e.g. DATEADD(month, 1, t.Column) should only report t.Column

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
