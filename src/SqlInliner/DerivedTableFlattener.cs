using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlInliner;

/// <summary>
/// Post-processing step that flattens simple derived tables (subqueries) produced by inlining
/// into the outer query. Phase 1 handles single-table QuerySpecification with no GROUP BY,
/// HAVING, TOP, DISTINCT, or UNION.
/// </summary>
internal sealed class DerivedTableFlattener
{
    /// <summary>
    /// Gets warnings generated during flattening.
    /// </summary>
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Flattens eligible derived tables in the given AST tree. Returns the number of derived tables flattened.
    /// </summary>
    public int Flatten(TSqlFragment tree)
    {
        var totalFlattened = 0;

        // Keep iterating until no more flattening occurs (handles nested derived tables)
        bool changed;
        do
        {
            changed = false;
            var finder = new QuerySpecificationFinder();
            tree.Accept(finder);

            foreach (var querySpec in finder.QuerySpecifications)
            {
                var count = FlattenInQuerySpecification(querySpec);
                if (count > 0)
                {
                    totalFlattened += count;
                    changed = true;
                }
            }
        } while (changed);

        return totalFlattened;
    }

    /// <summary>
    /// Attempts to flatten derived tables within a single QuerySpecification's FROM/JOIN tree.
    /// </summary>
    private int FlattenInQuerySpecification(QuerySpecification outerQuery)
    {
        var flattened = 0;

        // Collect all aliases used in the outer scope (excluding derived table aliases)
        var outerAliases = CollectOuterAliases(outerQuery);

        // Walk the FROM/JOIN tree looking for QueryDerivedTable nodes
        if (outerQuery.FromClause == null)
            return 0;

        for (var i = 0; i < outerQuery.FromClause.TableReferences.Count; i++)
        {
            var result = TryFlattenTableReference(outerQuery, outerQuery.FromClause.TableReferences[i], outerAliases);
            if (result.Replacement != null)
            {
                outerQuery.FromClause.TableReferences[i] = result.Replacement;
                flattened += result.FlattenedCount;
            }
            else
            {
                flattened += result.FlattenedCount;
            }
        }

        return flattened;
    }

    private (TableReference? Replacement, int FlattenedCount) TryFlattenTableReference(
        QuerySpecification outerQuery, TableReference tableRef, HashSet<string> outerAliases)
    {
        switch (tableRef)
        {
            case QueryDerivedTable derivedTable:
                if (TryFlattenDerivedTable(outerQuery, derivedTable, outerAliases, out var replacement))
                    return (replacement, 1);
                return (null, 0);

            case QualifiedJoin join:
            {
                var totalFlattened = 0;

                var firstResult = TryFlattenTableReference(outerQuery, join.FirstTableReference, outerAliases);
                if (firstResult.Replacement != null)
                {
                    join.FirstTableReference = firstResult.Replacement;
                    totalFlattened += firstResult.FlattenedCount;
                }
                else
                {
                    totalFlattened += firstResult.FlattenedCount;
                }

                var secondResult = TryFlattenTableReference(outerQuery, join.SecondTableReference, outerAliases);
                if (secondResult.Replacement != null)
                {
                    join.SecondTableReference = secondResult.Replacement;
                    totalFlattened += secondResult.FlattenedCount;
                }
                else
                {
                    totalFlattened += secondResult.FlattenedCount;
                }

                return (null, totalFlattened);
            }

            default:
                return (null, 0);
        }
    }

    /// <summary>
    /// Attempts to flatten a single QueryDerivedTable into the outer query.
    /// Returns true if successful, with the replacement NamedTableReference in <paramref name="replacement"/>.
    /// </summary>
    private bool TryFlattenDerivedTable(QuerySpecification outerQuery, QueryDerivedTable derivedTable,
        HashSet<string> outerAliases, out NamedTableReference? replacement)
    {
        replacement = null;

        // Must have an alias
        if (derivedTable.Alias == null)
            return false;

        var derivedAlias = derivedTable.Alias.Value;

        // Inner query must be a simple QuerySpecification (not UNION/EXCEPT/INTERSECT)
        if (derivedTable.QueryExpression is not QuerySpecification innerQuery)
            return false;

        // Eligibility checks
        if (!IsEligibleForFlattening(innerQuery))
            return false;

        // Must have exactly one NamedTableReference in FROM (no JOINs)
        var innerTableRef = GetSingleNamedTableReference(innerQuery);
        if (innerTableRef == null)
            return false;

        // No SELECT * allowed
        if (innerQuery.SelectElements.Any(e => e is SelectStarExpression))
            return false;

        // Build column map: derivedAlias.columnName -> inner expression
        var columnMap = BuildColumnMap(innerQuery);
        if (columnMap == null)
            return false;

        // Check if any mapped complex expression is referenced by the outer query
        if (HasReferencedComplexExpressions(outerQuery, derivedAlias, columnMap))
            return false;

        // Alias collision detection and resolution
        var innerAlias = innerTableRef.Alias?.Value ?? innerTableRef.SchemaObject.BaseIdentifier.Value;
        var resolvedAlias = ResolveAliasCollision(innerAlias, outerAliases, derivedAlias);
        if (resolvedAlias != innerAlias)
        {
            // Rename all references to the inner alias within the inner query
            RenameAliasInFragment(innerQuery, innerAlias, resolvedAlias);

            // Update the inner table's alias
            innerTableRef.Alias ??= new Identifier();
            innerTableRef.Alias.Value = resolvedAlias;

            // Update column map to reflect renamed alias
            foreach (var key in columnMap.Keys.ToList())
            {
                if (columnMap[key] is ColumnReferenceExpression colRef &&
                    colRef.MultiPartIdentifier.Identifiers.Count > 1 &&
                    string.Equals(colRef.MultiPartIdentifier.Identifiers[0].Value, innerAlias, StringComparison.OrdinalIgnoreCase))
                {
                    // Already renamed in-place by RenameAliasInFragment
                }
            }

            innerAlias = resolvedAlias;
        }

        // Register the new alias in outer scope
        outerAliases.Add(innerAlias);

        // Rewrite outer column references: derivedAlias.col -> inner column's identifiers
        RewriteOuterColumnReferences(outerQuery, derivedAlias, columnMap);

        // Merge WHERE clauses
        MergeWhereClause(outerQuery, innerQuery);

        // Return the inner table reference as the replacement
        replacement = innerTableRef;
        return true;
    }

    /// <summary>
    /// Checks if the inner QuerySpecification is eligible for Phase 1 flattening.
    /// </summary>
    private static bool IsEligibleForFlattening(QuerySpecification query)
    {
        if (query.GroupByClause != null)
            return false;
        if (query.HavingClause != null)
            return false;
        if (query.TopRowFilter != null)
            return false;
        if (query.UniqueRowFilter != UniqueRowFilter.NotSpecified)
            return false; // DISTINCT
        if (query.FromClause == null)
            return false;

        return true;
    }

    /// <summary>
    /// Returns the single NamedTableReference from the inner query's FROM clause,
    /// or null if the FROM has multiple tables or JOINs.
    /// </summary>
    private static NamedTableReference? GetSingleNamedTableReference(QuerySpecification query)
    {
        if (query.FromClause.TableReferences.Count != 1)
            return null;

        return query.FromClause.TableReferences[0] as NamedTableReference;
    }

    /// <summary>
    /// Builds a map of column name -> inner ScalarExpression for each SelectScalarExpression in the inner query.
    /// Returns null if any column name cannot be determined.
    /// </summary>
    private static Dictionary<string, ScalarExpression>? BuildColumnMap(QuerySpecification innerQuery)
    {
        var map = new Dictionary<string, ScalarExpression>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in innerQuery.SelectElements)
        {
            if (element is not SelectScalarExpression scalar)
                return null; // Unexpected element type

            string? columnName;
            if (scalar.ColumnName != null)
                columnName = scalar.ColumnName.Value;
            else if (scalar.Expression is ColumnReferenceExpression colRef)
                columnName = colRef.MultiPartIdentifier.Identifiers.Last().Value;
            else
                return null; // Complex expression without alias — can't determine column name

            if (columnName == null)
                return null;

            map[columnName] = scalar.Expression;
        }

        return map;
    }

    /// <summary>
    /// Checks if the outer query references any column from the derived table where
    /// the mapped inner expression is not a simple ColumnReferenceExpression.
    /// </summary>
    private static bool HasReferencedComplexExpressions(QuerySpecification outerQuery,
        string derivedAlias, Dictionary<string, ScalarExpression> columnMap)
    {
        var collector = new OuterScopeColumnReferenceCollector();
        outerQuery.Accept(collector);

        foreach (var colRef in collector.References)
        {
            if (colRef.MultiPartIdentifier?.Identifiers.Count >= 2 &&
                string.Equals(colRef.MultiPartIdentifier.Identifiers[0].Value, derivedAlias, StringComparison.OrdinalIgnoreCase))
            {
                var colName = colRef.MultiPartIdentifier.Identifiers.Last().Value;
                if (columnMap.TryGetValue(colName, out var innerExpr) && innerExpr is not ColumnReferenceExpression)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves alias collision by appending incrementing numbers.
    /// </summary>
    private static string ResolveAliasCollision(string innerAlias, HashSet<string> outerAliases, string derivedAlias)
    {
        if (!outerAliases.Contains(innerAlias) ||
            string.Equals(innerAlias, derivedAlias, StringComparison.OrdinalIgnoreCase))
            return innerAlias;

        var baseName = innerAlias;
        var counter = 1;
        while (outerAliases.Contains(baseName + counter))
            counter++;

        return baseName + counter;
    }

    /// <summary>
    /// Renames all column references within a fragment that use the old alias to the new alias.
    /// </summary>
    private static void RenameAliasInFragment(TSqlFragment fragment, string oldAlias, string newAlias)
    {
        var renamer = new AliasRenamer(oldAlias, newAlias);
        fragment.Accept(renamer);
    }

    /// <summary>
    /// Rewrites column references in the outer query that point to the derived table alias
    /// to point to the inner table's columns instead.
    /// </summary>
    private static void RewriteOuterColumnReferences(QuerySpecification outerQuery,
        string derivedAlias, Dictionary<string, ScalarExpression> columnMap)
    {
        // We need to rewrite references in:
        // 1. SELECT elements
        // 2. WHERE clause
        // 3. JOIN conditions (in the FROM/JOIN tree)
        // 4. ORDER BY (if present)

        // Collect outer-scope column references only (does not descend into QueryDerivedTable nodes)
        var collector = new OuterScopeColumnReferenceCollector();

        // Visit SELECT elements
        foreach (var element in outerQuery.SelectElements)
            element.Accept(collector);

        // Visit WHERE
        outerQuery.WhereClause?.Accept(collector);

        // Visit FROM (for JOIN conditions — stops at QueryDerivedTable boundaries)
        outerQuery.FromClause?.Accept(collector);

        // Visit ORDER BY
        outerQuery.OrderByClause?.Accept(collector);

        foreach (var colRef in collector.References)
        {
            if (colRef.MultiPartIdentifier is not { Identifiers.Count: >= 2 } mpi)
                continue;

            if (!string.Equals(mpi.Identifiers[0].Value, derivedAlias, StringComparison.OrdinalIgnoreCase))
                continue;

            var colName = mpi.Identifiers.Last().Value;
            if (!columnMap.TryGetValue(colName, out var innerExpr))
                continue;

            if (innerExpr is ColumnReferenceExpression innerColRef)
            {
                // Replace the multi-part identifier with the inner column's identifiers
                mpi.Identifiers.Clear();
                foreach (var id in innerColRef.MultiPartIdentifier.Identifiers)
                {
                    mpi.Identifiers.Add(new Identifier { Value = id.Value, QuoteType = id.QuoteType });
                }
            }
        }
    }

    /// <summary>
    /// Merges the inner query's WHERE clause into the outer query's WHERE clause using AND.
    /// </summary>
    private static void MergeWhereClause(QuerySpecification outerQuery, QuerySpecification innerQuery)
    {
        if (innerQuery.WhereClause == null)
            return;

        var innerCondition = innerQuery.WhereClause.SearchCondition;

        if (outerQuery.WhereClause == null)
        {
            outerQuery.WhereClause = new WhereClause { SearchCondition = innerCondition };
        }
        else
        {
            // Wrap both sides in parentheses for precedence safety and AND them together
            outerQuery.WhereClause.SearchCondition = new BooleanBinaryExpression
            {
                BinaryExpressionType = BooleanBinaryExpressionType.And,
                FirstExpression = new BooleanParenthesisExpression
                {
                    Expression = outerQuery.WhereClause.SearchCondition,
                },
                SecondExpression = new BooleanParenthesisExpression
                {
                    Expression = innerCondition,
                },
            };
        }
    }

    /// <summary>
    /// Collects all aliases in the outer scope (table aliases, derived table aliases).
    /// </summary>
    private static HashSet<string> CollectOuterAliases(QuerySpecification outerQuery)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (outerQuery.FromClause == null)
            return aliases;

        foreach (var tableRef in outerQuery.FromClause.TableReferences)
            CollectAliasesFromTableReference(tableRef, aliases);

        return aliases;
    }

    private static void CollectAliasesFromTableReference(TableReference tableRef, HashSet<string> aliases)
    {
        switch (tableRef)
        {
            case NamedTableReference named:
                var alias = named.Alias?.Value ?? named.SchemaObject.BaseIdentifier.Value;
                aliases.Add(alias);
                break;
            case QueryDerivedTable derived:
                if (derived.Alias != null)
                    aliases.Add(derived.Alias.Value);
                break;
            case QualifiedJoin join:
                CollectAliasesFromTableReference(join.FirstTableReference, aliases);
                CollectAliasesFromTableReference(join.SecondTableReference, aliases);
                break;
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

    /// <summary>
    /// Visitor that renames all column references using the old alias to the new alias.
    /// </summary>
    private sealed class AliasRenamer : TSqlFragmentVisitor
    {
        private readonly string oldAlias;
        private readonly string newAlias;

        public AliasRenamer(string oldAlias, string newAlias)
        {
            this.oldAlias = oldAlias;
            this.newAlias = newAlias;
        }

        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            if (node.MultiPartIdentifier is { Identifiers.Count: > 0 } mpi &&
                string.Equals(mpi.Identifiers[0].Value, oldAlias, StringComparison.OrdinalIgnoreCase))
            {
                mpi.Identifiers[0].Value = newAlias;
            }

            base.ExplicitVisit(node);
        }
    }
}
