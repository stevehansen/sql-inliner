using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlInliner;

/// <summary>
/// Post-processing step that strips unused columns and LEFT JOINs inside nested
/// QueryDerivedTable nodes produced by inlining. Runs after <see cref="DatabaseViewInliner.Inline"/>
/// and before <see cref="DerivedTableFlattener"/>.
/// </summary>
internal sealed class DerivedTableStripper
{
    private readonly InlinerOptions options;

    public DerivedTableStripper(InlinerOptions options)
    {
        this.options = options;
    }

    /// <summary>
    /// Gets the total number of columns stripped from derived tables.
    /// </summary>
    public int TotalColumnsStripped { get; private set; }

    /// <summary>
    /// Gets the total number of joins stripped from derived tables.
    /// </summary>
    public int TotalJoinsStripped { get; private set; }

    /// <summary>
    /// Gets warnings generated during stripping.
    /// </summary>
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Strips unused columns and LEFT JOINs inside derived tables in the given AST tree.
    /// Iterates until no more stripping occurs (handles cascading effects).
    /// </summary>
    public void Strip(TSqlFragment tree)
    {
        bool changed;
        do
        {
            changed = false;
            var finder = new QuerySpecificationFinder();
            tree.Accept(finder);

            foreach (var querySpec in finder.QuerySpecifications)
            {
                if (StripInQuerySpecification(querySpec))
                    changed = true;
            }
        } while (changed);
    }

    /// <summary>
    /// Walks the FROM/JOIN tree of a QuerySpecification looking for QueryDerivedTable nodes to strip.
    /// </summary>
    private bool StripInQuerySpecification(QuerySpecification outerQuery)
    {
        if (outerQuery.FromClause == null)
            return false;

        var changed = false;

        for (var i = 0; i < outerQuery.FromClause.TableReferences.Count; i++)
        {
            if (StripInTableReference(outerQuery, outerQuery.FromClause.TableReferences[i]))
                changed = true;

            // Handle join removal: strip LEFT OUTER JOINs to derived tables that are unused
            if (options.StripUnusedJoins && outerQuery.FromClause.TableReferences[i] is QualifiedJoin join)
            {
                var result = TryRemoveUnusedJoinsInTree(outerQuery, join);
                if (result != null)
                {
                    outerQuery.FromClause.TableReferences[i] = result;
                    changed = true;
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// Recursively walks a table reference tree to find and strip derived tables.
    /// </summary>
    private bool StripInTableReference(QuerySpecification outerQuery, TableReference tableRef)
    {
        switch (tableRef)
        {
            case QueryDerivedTable derivedTable:
                return TryStripDerivedTable(outerQuery, derivedTable);

            case QualifiedJoin join:
            {
                var changed = false;
                if (StripInTableReference(outerQuery, join.FirstTableReference))
                    changed = true;
                if (StripInTableReference(outerQuery, join.SecondTableReference))
                    changed = true;
                return changed;
            }

            case UnqualifiedJoin unqualifiedJoin:
            {
                var changed = false;
                if (StripInTableReference(outerQuery, unqualifiedJoin.FirstTableReference))
                    changed = true;
                if (StripInTableReference(outerQuery, unqualifiedJoin.SecondTableReference))
                    changed = true;
                return changed;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Attempts to strip unused columns and LEFT JOINs from a single derived table.
    /// </summary>
    private bool TryStripDerivedTable(QuerySpecification outerQuery, QueryDerivedTable derivedTable)
    {
        var changed = false;

        if (options.StripUnusedColumns)
        {
            if (TryStripColumnsFromDerivedTable(outerQuery, derivedTable))
                changed = true;
        }

        if (options.StripUnusedJoins)
        {
            if (TryStripJoinsInsideDerivedTable(derivedTable))
                changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Strips unused columns from a derived table based on what the outer query references.
    /// </summary>
    private bool TryStripColumnsFromDerivedTable(QuerySpecification outerQuery, QueryDerivedTable derivedTable)
    {
        // Must have an alias
        if (derivedTable.Alias == null)
            return false;

        var derivedAlias = derivedTable.Alias.Value;

        // Get the inner query expression (may be QuerySpecification or BinaryQueryExpression)
        var queryExpression = derivedTable.QueryExpression;

        // Find the first QuerySpecification to check eligibility and get column names
        var firstQuery = GetFirstQuerySpecification(queryExpression);
        if (firstQuery == null)
            return false;

        // Skip if the inner query has features that make column stripping unsafe
        if (!IsEligibleForColumnStripping(firstQuery))
            return false;

        // Collect what the outer scope references via this derived table's alias
        var usedColumns = CollectUsedColumns(outerQuery, derivedAlias);

        // Collect all SELECT element lists across UNION/EXCEPT/INTERSECT branches
        var allSelectElements = new List<IList<SelectElement>>();
        CollectSelectElements(queryExpression, allSelectElements);

        if (allSelectElements.Count == 0)
            return false;

        // Use the first query's SELECT to determine column names
        var selectElements = firstQuery.SelectElements;

        // First pass: identify which columns to strip
        var indexesToStrip = new List<int>();
        for (var i = selectElements.Count - 1; i >= 0; i--)
        {
            if (selectElements[i] is SelectScalarExpression selectExpression)
            {
                string? columnName = null;
                if (selectExpression.ColumnName != null)
                    columnName = selectExpression.ColumnName.Value;
                else if (selectExpression.Expression is ColumnReferenceExpression columnReference)
                    columnName = columnReference.MultiPartIdentifier.Identifiers.Last().Value;

                if (columnName != null && !usedColumns.Contains(columnName))
                    indexesToStrip.Add(i);
            }
        }

        // If ALL columns would be stripped, skip — leave the derived table as-is.
        // The join stripper can remove the entire derived table if appropriate.
        if (indexesToStrip.Count == 0 || indexesToStrip.Count >= selectElements.Count)
            return false;

        // Second pass: actually strip
        foreach (var i in indexesToStrip)
        {
            foreach (var elements in allSelectElements)
            {
                if (i < elements.Count)
                {
                    elements.RemoveAt(i);
                    TotalColumnsStripped++;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Collects column names that the outer query references from a derived table via its alias.
    /// Includes single-part identifiers conservatively (they could reference any table).
    /// </summary>
    private static HashSet<string> CollectUsedColumns(QuerySpecification outerQuery, string derivedAlias)
    {
        var collector = new OuterScopeColumnReferenceCollector();

        // Collect from all outer clauses
        foreach (var element in outerQuery.SelectElements)
            element.Accept(collector);
        outerQuery.WhereClause?.Accept(collector);
        outerQuery.FromClause?.Accept(collector);
        outerQuery.OrderByClause?.Accept(collector);
        outerQuery.GroupByClause?.Accept(collector);
        outerQuery.HavingClause?.Accept(collector);

        var usedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var colRef in collector.References)
        {
            var identifiers = colRef.MultiPartIdentifier?.Identifiers;
            if (identifiers == null)
                continue;

            if (identifiers.Count == 1)
            {
                // Single-part identifier — conservatively treat as potentially referencing this derived table
                usedColumns.Add(identifiers[0].Value);
            }
            else if (identifiers.Count >= 2 &&
                     string.Equals(identifiers[0].Value, derivedAlias, StringComparison.OrdinalIgnoreCase))
            {
                usedColumns.Add(identifiers.Last().Value);
            }
        }

        return usedColumns;
    }

    /// <summary>
    /// Strips unused LEFT OUTER JOINs inside a derived table's inner query.
    /// Only strips LEFT OUTER JOINs where the second table has zero column references
    /// outside its own ON condition within the inner scope.
    /// </summary>
    private bool TryStripJoinsInsideDerivedTable(QueryDerivedTable derivedTable)
    {
        var firstQuery = GetFirstQuerySpecification(derivedTable.QueryExpression);
        if (firstQuery?.FromClause == null)
            return false;

        var changed = false;
        bool iterationChanged;
        do
        {
            iterationChanged = false;

            for (var i = 0; i < firstQuery.FromClause.TableReferences.Count; i++)
            {
                if (firstQuery.FromClause.TableReferences[i] is QualifiedJoin join)
                {
                    var result = TryRemoveUnusedJoinsInInnerTree(firstQuery, join);
                    if (result != null)
                    {
                        firstQuery.FromClause.TableReferences[i] = result;
                        iterationChanged = true;
                        changed = true;
                    }
                }
            }
        } while (iterationChanged);

        return changed;
    }

    /// <summary>
    /// Walks a QualifiedJoin tree inside a derived table, looking for LEFT OUTER JOINs
    /// whose second table reference has no column references in the inner scope
    /// (excluding its own ON condition).
    /// </summary>
    private TableReference? TryRemoveUnusedJoinsInInnerTree(QuerySpecification innerQuery, QualifiedJoin join)
    {
        // Recurse into sub-joins first
        if (join.FirstTableReference is QualifiedJoin firstJoin)
        {
            var result = TryRemoveUnusedJoinsInInnerTree(innerQuery, firstJoin);
            if (result != null)
                join.FirstTableReference = result;
        }

        if (join.SecondTableReference is QualifiedJoin secondJoin)
        {
            var result = TryRemoveUnusedJoinsInInnerTree(innerQuery, secondJoin);
            if (result != null)
                join.SecondTableReference = result;
        }

        // Only strip LEFT OUTER JOINs to QueryDerivedTable nodes (inlined views).
        // NamedTableReference joins were already evaluated by the inlining logic
        // which has access to join hints — we should not re-evaluate those.
        if (join.QualifiedJoinType != QualifiedJoinType.LeftOuter)
            return null;
        if (join.SecondTableReference is not QueryDerivedTable)
            return null;

        // Get the alias of the second table reference
        var secondAlias = GetTableReferenceAlias(join.SecondTableReference);
        if (secondAlias == null)
            return null;

        // Collect all column references in the inner scope (stops at nested QDT boundaries)
        var innerCollector = new OuterScopeColumnReferenceCollector();

        // Collect from all inner query clauses
        foreach (var element in innerQuery.SelectElements)
            element.Accept(innerCollector);
        innerQuery.WhereClause?.Accept(innerCollector);
        innerQuery.OrderByClause?.Accept(innerCollector);
        innerQuery.GroupByClause?.Accept(innerCollector);
        innerQuery.HavingClause?.Accept(innerCollector);

        // Collect from the FROM clause but exclude the current join's ON condition
        CollectFromClauseRefsExcludingCondition(innerQuery.FromClause, join.SearchCondition, innerCollector);

        // Check if any references point to the second table's alias
        var hasReferences = innerCollector.References.Any(colRef =>
        {
            var identifiers = colRef.MultiPartIdentifier?.Identifiers;
            if (identifiers == null || identifiers.Count < 2)
                return identifiers is { Count: 1 }; // Single-part — conservatively keep
            return string.Equals(identifiers[0].Value, secondAlias, StringComparison.OrdinalIgnoreCase);
        });

        if (!hasReferences)
        {
            TotalJoinsStripped++;
            return join.FirstTableReference; // Remove the second table, keep the first
        }

        return null;
    }

    /// <summary>
    /// Walks a QualifiedJoin tree in the outer query, looking for LEFT OUTER JOINs to derived tables
    /// whose alias has no column references in the outer scope (excluding its own ON condition).
    /// </summary>
    private TableReference? TryRemoveUnusedJoinsInTree(QuerySpecification outerQuery, QualifiedJoin join)
    {
        // Recurse into sub-joins first
        if (join.FirstTableReference is QualifiedJoin firstJoin)
        {
            var result = TryRemoveUnusedJoinsInTree(outerQuery, firstJoin);
            if (result != null)
                join.FirstTableReference = result;
        }

        if (join.SecondTableReference is QualifiedJoin secondJoin)
        {
            var result = TryRemoveUnusedJoinsInTree(outerQuery, secondJoin);
            if (result != null)
                join.SecondTableReference = result;
        }

        // Only strip LEFT OUTER JOINs to derived tables
        if (join.QualifiedJoinType != QualifiedJoinType.LeftOuter)
            return null;

        if (join.SecondTableReference is not QueryDerivedTable { Alias: not null } derivedTable)
            return null;

        var derivedAlias = derivedTable.Alias.Value;

        // Collect outer-scope column references excluding this join's ON condition
        var collector = new OuterScopeColumnReferenceCollector();
        foreach (var element in outerQuery.SelectElements)
            element.Accept(collector);
        outerQuery.WhereClause?.Accept(collector);
        outerQuery.OrderByClause?.Accept(collector);
        outerQuery.GroupByClause?.Accept(collector);
        outerQuery.HavingClause?.Accept(collector);
        CollectFromClauseRefsExcludingCondition(outerQuery.FromClause, join.SearchCondition, collector);

        var hasReferences = collector.References.Any(colRef =>
        {
            var identifiers = colRef.MultiPartIdentifier?.Identifiers;
            if (identifiers == null)
                return false;
            if (identifiers.Count == 1)
                return true; // Single-part — conservatively keep
            return string.Equals(identifiers[0].Value, derivedAlias, StringComparison.OrdinalIgnoreCase);
        });

        if (!hasReferences)
        {
            TotalJoinsStripped++;
            return join.FirstTableReference;
        }

        return null;
    }

    /// <summary>
    /// Collects column references from the FROM clause tree, but excludes references
    /// within a specific search condition (the ON clause being evaluated for stripping).
    /// </summary>
    private static void CollectFromClauseRefsExcludingCondition(
        FromClause? fromClause, BooleanExpression? excludeCondition, OuterScopeColumnReferenceCollector collector)
    {
        if (fromClause == null)
            return;

        foreach (var tableRef in fromClause.TableReferences)
            CollectTableRefRefsExcludingCondition(tableRef, excludeCondition, collector);
    }

    private static void CollectTableRefRefsExcludingCondition(
        TableReference tableRef, BooleanExpression? excludeCondition, OuterScopeColumnReferenceCollector collector)
    {
        switch (tableRef)
        {
            case QualifiedJoin join:
                CollectTableRefRefsExcludingCondition(join.FirstTableReference, excludeCondition, collector);
                CollectTableRefRefsExcludingCondition(join.SecondTableReference, excludeCondition, collector);
                if (join.SearchCondition != null && !ReferenceEquals(join.SearchCondition, excludeCondition))
                    join.SearchCondition.Accept(collector);
                break;

            case UnqualifiedJoin unqualifiedJoin:
                CollectTableRefRefsExcludingCondition(unqualifiedJoin.FirstTableReference, excludeCondition, collector);
                CollectTableRefRefsExcludingCondition(unqualifiedJoin.SecondTableReference, excludeCondition, collector);
                break;

            // NamedTableReference and QueryDerivedTable don't have column refs to collect at this level
        }
    }

    /// <summary>
    /// Gets the alias of a table reference (works for NamedTableReference and QueryDerivedTable).
    /// </summary>
    private static string? GetTableReferenceAlias(TableReference tableRef)
    {
        return tableRef switch
        {
            NamedTableReference named => named.Alias?.Value ?? named.SchemaObject?.BaseIdentifier?.Value,
            QueryDerivedTable derived => derived.Alias?.Value,
            _ => null,
        };
    }

    /// <summary>
    /// Gets the first QuerySpecification from a query expression (handles BinaryQueryExpression).
    /// </summary>
    private static QuerySpecification? GetFirstQuerySpecification(QueryExpression queryExpression)
    {
        while (true)
        {
            switch (queryExpression)
            {
                case QuerySpecification spec:
                    return spec;
                case BinaryQueryExpression binary:
                    queryExpression = binary.FirstQueryExpression;
                    continue;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Checks if a QuerySpecification is eligible for column stripping.
    /// </summary>
    private static bool IsEligibleForColumnStripping(QuerySpecification query)
    {
        // Skip SELECT *
        if (query.SelectElements.Any(e => e is SelectStarExpression))
            return false;
        // Skip DISTINCT
        if (query.UniqueRowFilter != UniqueRowFilter.NotSpecified)
            return false;
        // Skip TOP
        if (query.TopRowFilter != null)
            return false;
        // Skip GROUP BY
        if (query.GroupByClause != null)
            return false;
        // Skip HAVING
        if (query.HavingClause != null)
            return false;

        return true;
    }

    /// <summary>
    /// Collects all SELECT element lists from a query expression tree (handles UNION/EXCEPT/INTERSECT).
    /// </summary>
    private static void CollectSelectElements(QueryExpression queryExpression, List<IList<SelectElement>> result)
    {
        while (true)
        {
            switch (queryExpression)
            {
                case QuerySpecification spec:
                    result.Add(spec.SelectElements);
                    return;
                case BinaryQueryExpression binary:
                    CollectSelectElements(binary.FirstQueryExpression, result);
                    queryExpression = binary.SecondQueryExpression;
                    continue;
                default:
                    return;
            }
        }
    }

    /// <summary>
    /// Visitor that collects all QuerySpecification nodes in the AST.
    /// </summary>
    private sealed class QuerySpecificationFinder : TSqlFragmentVisitor
    {
        public List<QuerySpecification> QuerySpecifications { get; } = new();

        public override void ExplicitVisit(QuerySpecification node)
        {
            QuerySpecifications.Add(node);
            base.ExplicitVisit(node);
        }
    }

    /// <summary>
    /// Visitor that collects ColumnReferenceExpression nodes in the outer scope only.
    /// Does NOT descend into QueryDerivedTable nodes to avoid capturing inner-scope references.
    /// </summary>
    private sealed class OuterScopeColumnReferenceCollector : TSqlFragmentVisitor
    {
        public List<ColumnReferenceExpression> References { get; } = new();

        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            if (node.MultiPartIdentifier != null)
                References.Add(node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(QueryDerivedTable node)
        {
            // Do NOT call base — stop traversal at derived table boundaries
        }
    }
}
