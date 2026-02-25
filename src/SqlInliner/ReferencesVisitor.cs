using System;
using System.Collections.Generic;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlInliner;

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

    /// <summary>
    /// Gets all the derived table references (inline subqueries) that are the second table in a qualified join.
    /// </summary>
    public List<QueryDerivedTable> DerivedTables { get; } = new();

    /// <summary>
    /// Maps table references that are the second table in a qualified join to their join's search condition.
    /// </summary>
    public Dictionary<TableReference, BooleanExpression> JoinConditions { get; } = new();

    /// <summary>
    /// Maps table references that are the second table in a qualified join to their join type (Inner, LeftOuter, etc.).
    /// </summary>
    public Dictionary<TableReference, QualifiedJoinType> JoinTypes { get; } = new();

    /// <summary>
    /// Maps table references that are the second table in a qualified join to any <see cref="JoinHint"/> parsed from SQL comments.
    /// </summary>
    public Dictionary<TableReference, JoinHint> JoinHints { get; } = new();

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
    public override void ExplicitVisit(QualifiedJoin node)
    {
        base.ExplicitVisit(node);

        var secondTable = node.SecondTableReference;
        if (secondTable is QueryDerivedTable derivedTable)
            DerivedTables.Add(derivedTable);
        else if (secondTable is not NamedTableReference)
            return;

        JoinConditions[secondTable] = node.SearchCondition;
        JoinTypes[secondTable] = node.QualifiedJoinType;

        var hints = ParseJoinHints(node);
        if (hints != JoinHint.None)
            JoinHints[secondTable] = hints;
    }

    /// <summary>
    /// Scans the token stream between the first table reference and the search condition
    /// of a <see cref="QualifiedJoin"/> for SQL comments containing join hints.
    /// </summary>
    private static JoinHint ParseJoinHints(QualifiedJoin node)
    {
        var hints = JoinHint.None;
        var tokens = node.ScriptTokenStream;
        if (tokens == null)
            return hints;

        // Scan tokens between the end of the first table reference and the start of the
        // search condition. This range covers the JOIN keyword, any hint comments, and the
        // second table reference — but not tokens from nested joins or the ON clause body.
        var startIndex = node.FirstTableReference.LastTokenIndex + 1;
        var endIndex = node.SearchCondition?.FirstTokenIndex ?? node.LastTokenIndex + 1;

        for (var i = startIndex; i < endIndex; i++)
        {
            var token = tokens[i];
            if (token.TokenType is TSqlTokenType.MultilineComment or TSqlTokenType.SingleLineComment)
            {
                var text = token.Text;
                if (text.IndexOf(JoinHintMarkers.Unique, StringComparison.OrdinalIgnoreCase) >= 0)
                    hints |= JoinHint.Unique;
                if (text.IndexOf(JoinHintMarkers.Required, StringComparison.OrdinalIgnoreCase) >= 0)
                    hints |= JoinHint.Required;
            }
        }

        return hints;
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