using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlInliner;

/// <summary>
/// Post-processing step that flattens derived tables (subqueries) produced by inlining
/// into the outer query. Handles QuerySpecification subqueries with no GROUP BY,
/// HAVING, TOP, DISTINCT, or UNION — including single-table and multi-table (JOIN) cases.
/// </summary>
internal sealed class DerivedTableFlattener
{
    /// <summary>
    /// Gets warnings generated during flattening.
    /// </summary>
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// The root AST tree, used for tree-wide cross-scope reference checking.
    /// </summary>
    private TSqlFragment? _rootTree;

    /// <summary>
    /// Flattens eligible derived tables in the given AST tree. Returns the number of derived tables flattened.
    /// </summary>
    public int Flatten(TSqlFragment tree)
    {
        _rootTree = tree;
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

        // Detect column names shared by multiple QDTs in this scope — these are
        // ambiguous for 1-part (unqualified) matching and must be skipped
        var sharedColumnNames = CollectSharedQdtColumnNames(outerQuery.FromClause);

        for (var i = 0; i < outerQuery.FromClause.TableReferences.Count; i++)
        {
            var result = TryFlattenTableReference(outerQuery, outerQuery.FromClause.TableReferences[i], outerAliases, sharedColumnNames);
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
        QuerySpecification outerQuery, TableReference tableRef, HashSet<string> outerAliases,
        HashSet<string> sharedColumnNames, QualifiedJoin? parentJoin = null, bool isSecondTableRef = false)
    {
        switch (tableRef)
        {
            case QueryDerivedTable derivedTable:
            {
                // Skip flattening if we're on the preserved side of an outer join with inner WHERE —
                // moving the WHERE to the ON clause would change LEFT/RIGHT JOIN semantics
                if (parentJoin != null && ShouldSkipFlattenForJoinContext(parentJoin, isSecondTableRef, derivedTable))
                    return (null, 0);

                if (TryFlattenDerivedTable(outerQuery, derivedTable, outerAliases, sharedColumnNames, out var replacement, out var innerWhere))
                {
                    if (innerWhere != null)
                    {
                        if (parentJoin != null)
                            MergeIntoJoinCondition(parentJoin, innerWhere);
                        else
                            MergeWhereCondition(outerQuery, innerWhere);
                    }

                    return (replacement, 1);
                }

                return (null, 0);
            }

            case QualifiedJoin join:
            {
                var totalFlattened = 0;

                var firstResult = TryFlattenTableReference(outerQuery, join.FirstTableReference, outerAliases, sharedColumnNames, join, false);
                if (firstResult.Replacement != null)
                    join.FirstTableReference = firstResult.Replacement;
                totalFlattened += firstResult.FlattenedCount;

                var secondResult = TryFlattenTableReference(outerQuery, join.SecondTableReference, outerAliases, sharedColumnNames, join, true);
                if (secondResult.Replacement != null)
                    join.SecondTableReference = secondResult.Replacement;
                totalFlattened += secondResult.FlattenedCount;

                return (null, totalFlattened);
            }

            case UnqualifiedJoin unqualifiedJoin:
            {
                // CROSS APPLY / OUTER APPLY — recurse into both subtrees so that derived
                // tables nested inside (e.g., INNER JOINs before an OUTER APPLY) get processed.
                // UnqualifiedJoin has no SearchCondition, so parentJoin stays null for direct
                // children; their inner WHERE (if any) will go to the outer WHERE clause.
                var totalFlattened = 0;

                var firstResult = TryFlattenTableReference(outerQuery, unqualifiedJoin.FirstTableReference, outerAliases, sharedColumnNames);
                if (firstResult.Replacement != null)
                    unqualifiedJoin.FirstTableReference = firstResult.Replacement;
                totalFlattened += firstResult.FlattenedCount;

                var secondResult = TryFlattenTableReference(outerQuery, unqualifiedJoin.SecondTableReference, outerAliases, sharedColumnNames);
                if (secondResult.Replacement != null)
                    unqualifiedJoin.SecondTableReference = secondResult.Replacement;
                totalFlattened += secondResult.FlattenedCount;

                return (null, totalFlattened);
            }

            default:
                return (null, 0);
        }
    }

    /// <summary>
    /// Determines if flattening should be skipped because the inner WHERE clause cannot be
    /// safely placed after flattening. This occurs on the preserved side of an outer join
    /// (first ref of LEFT, second ref of RIGHT) and for FULL OUTER JOINs.
    /// </summary>
    private static bool ShouldSkipFlattenForJoinContext(QualifiedJoin join, bool isSecondTableRef, QueryDerivedTable derivedTable)
    {
        if (derivedTable.QueryExpression is not QuerySpecification { WhereClause: not null })
            return false;

        return join.QualifiedJoinType switch
        {
            QualifiedJoinType.LeftOuter => !isSecondTableRef,  // first ref is preserved
            QualifiedJoinType.RightOuter => isSecondTableRef,  // second ref is preserved
            QualifiedJoinType.FullOuter => true,               // neither side can be safely flattened
            _ => false                                         // INNER/CROSS: always safe
        };
    }

    /// <summary>
    /// Merges a boolean condition into a QualifiedJoin's ON clause using AND.
    /// </summary>
    private static void MergeIntoJoinCondition(QualifiedJoin join, BooleanExpression condition)
    {
        join.SearchCondition = new BooleanBinaryExpression
        {
            BinaryExpressionType = BooleanBinaryExpressionType.And,
            FirstExpression = new BooleanParenthesisExpression { Expression = join.SearchCondition },
            SecondExpression = new BooleanParenthesisExpression { Expression = condition },
        };
    }

    /// <summary>
    /// Attempts to flatten a single QueryDerivedTable into the outer query.
    /// Returns true if successful, with the replacement TableReference in <paramref name="replacement"/>.
    /// Handles both single-table and multi-table (JOIN) inner queries.
    /// </summary>
    private bool TryFlattenDerivedTable(QuerySpecification outerQuery, QueryDerivedTable derivedTable,
        HashSet<string> outerAliases, HashSet<string> sharedColumnNames, out TableReference? replacement, out BooleanExpression? innerWhereCondition)
    {
        replacement = null;
        innerWhereCondition = null;

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

        // Get inner FROM tree — must contain only NamedTableReference leaf nodes (no nested derived tables)
        var innerFromResult = GetInnerFromTree(innerQuery);
        if (innerFromResult == null)
            return false;

        var (innerFromTree, innerTableRefs) = innerFromResult.Value;

        // No SELECT * allowed
        if (innerQuery.SelectElements.Any(e => e is SelectStarExpression))
            return false;

        // Build column map: derivedAlias.columnName -> inner expression
        var columnMap = BuildColumnMap(innerQuery);
        if (columnMap == null)
            return false;

        // Check for cross-scope references: if the QDT's alias is referenced from a
        // join ON clause where the QDT is NOT a participant, flattening would break
        // the reference. SQL Server resolves QDT aliases across join scopes, but after
        // flattening to a table alias, the alias becomes scope-limited.
        // Also checks unqualified refs matching the column map — the rewriter would
        // qualify them as derivedAlias.col, creating a new cross-scope reference.
        // Search the ENTIRE tree, not just the immediate outerQuery — cross-scope refs
        // may exist at any ancestor scope.
        if (HasCrossScopeReferencesInTree(derivedAlias, columnMap))
            return false;

        // Check if any mapped complex expression is referenced by the outer query
        if (HasReferencedComplexExpressions(outerQuery, derivedAlias, columnMap, sharedColumnNames))
            return false;

        // Qualify unqualified column references in the inner query to prevent ambiguity
        // after promotion to the outer scope (where more tables are in play)
        if (innerTableRefs.Count == 1)
        {
            // Single-table: all unqualified refs must belong to this table — qualify them
            var tableAlias = innerTableRefs[0].Alias?.Value ?? innerTableRefs[0].SchemaObject.BaseIdentifier.Value;
            QualifyUnqualifiedColumnReferences(innerQuery, tableAlias);
        }
        else
        {
            // Multi-table: we can't resolve which table unqualified refs belong to — bail out
            if (HasUnqualifiedColumnReferences(innerQuery))
                return false;
        }

        // Alias handling: for single-table inner queries, preserve the derived table alias
        // (e.g., VNOKNVT, VVerlofAttest) for readability. For multi-table, resolve collisions normally.
        if (innerTableRefs.Count == 1)
        {
            var innerAlias = innerTableRefs[0].Alias?.Value ?? innerTableRefs[0].SchemaObject.BaseIdentifier.Value;
            if (!string.Equals(innerAlias, derivedAlias, StringComparison.OrdinalIgnoreCase))
                RenameAliasInFragment(innerQuery, innerAlias, derivedAlias);

            innerTableRefs[0].Alias ??= new Identifier();
            innerTableRefs[0].Alias.Value = derivedAlias;
            // derivedAlias is already in outerAliases (collected at the start)
        }
        else
        {
            // Build a complete rename map first, then apply all renames atomically
            // to prevent cascading corruption (e.g., c→c1 then c1→c11 affecting the first rename)
            var renameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var originalAliases = new List<(NamedTableReference TableRef, string OriginalAlias)>();

            foreach (var innerTableRef in innerTableRefs)
            {
                var innerAlias = innerTableRef.Alias?.Value ?? innerTableRef.SchemaObject.BaseIdentifier.Value;
                originalAliases.Add((innerTableRef, innerAlias));
                var resolvedAlias = ResolveAliasCollision(innerAlias, outerAliases, derivedAlias);
                if (resolvedAlias != innerAlias)
                    renameMap[innerAlias] = resolvedAlias;
                outerAliases.Add(resolvedAlias);
            }

            // Apply all renames simultaneously in a single pass
            if (renameMap.Count > 0)
            {
                var renamer = new MultiAliasRenamer(renameMap);
                innerQuery.Accept(renamer);
            }

            // Update table alias nodes using original alias values (not yet renamed by the visitor)
            foreach (var (tableRef, originalAlias) in originalAliases)
            {
                if (renameMap.TryGetValue(originalAlias, out var resolvedAlias))
                {
                    tableRef.Alias ??= new Identifier();
                    tableRef.Alias.Value = resolvedAlias;
                }
            }
        }

        // Snapshot inferred column names in the SELECT list before rewriting — when
        // the rewrite changes the last identifier (e.g., v.CompanyId → Companies_1.Id),
        // the inferred column name would silently change, breaking enclosing scopes.
        var selectNameSnapshots = SnapshotSelectColumnNames(outerQuery, derivedAlias, columnMap);

        // Rewrite outer column references: derivedAlias.col -> inner column's identifiers
        RewriteOuterColumnReferences(outerQuery, derivedAlias, columnMap, sharedColumnNames);

        // Restore column names where the inferred name changed
        RestoreSelectColumnNames(selectNameSnapshots);

        // Return inner WHERE condition for the caller to place appropriately
        // (into parent JOIN's ON clause, or into the outer WHERE for top-level FROM entries)
        innerWhereCondition = innerQuery.WhereClause?.SearchCondition;

        // Return the inner FROM tree as the replacement
        replacement = innerFromTree;
        return true;
    }

    /// <summary>
    /// Checks if the inner QuerySpecification is eligible for flattening.
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
    /// Extracts the inner FROM tree and all NamedTableReference leaf nodes within it.
    /// Returns null if the FROM clause has multiple cross-joined entries or contains
    /// non-NamedTableReference leaf nodes (e.g., nested derived tables).
    /// </summary>
    private static (TableReference FromTree, List<NamedTableReference> TableRefs)? GetInnerFromTree(QuerySpecification query)
    {
        if (query.FromClause.TableReferences.Count != 1)
            return null;

        var fromTree = query.FromClause.TableReferences[0];
        var tableRefs = new List<NamedTableReference>();

        if (!CollectNamedTableReferences(fromTree, tableRefs))
            return null;

        if (tableRefs.Count == 0)
            return null;

        return (fromTree, tableRefs);
    }

    private static bool CollectNamedTableReferences(TableReference tableRef, List<NamedTableReference> results)
    {
        switch (tableRef)
        {
            case NamedTableReference named:
                results.Add(named);
                return true;
            case QualifiedJoin join:
                return CollectNamedTableReferences(join.FirstTableReference, results) &&
                       CollectNamedTableReferences(join.SecondTableReference, results);
            default:
                return false;
        }
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
        string derivedAlias, Dictionary<string, ScalarExpression> columnMap, HashSet<string> sharedColumnNames)
    {
        var collector = new OuterScopeColumnReferenceCollector();
        outerQuery.Accept(collector);

        foreach (var colRef in collector.References)
        {
            var identCount = colRef.MultiPartIdentifier?.Identifiers.Count ?? 0;

            if (identCount >= 2 &&
                string.Equals(colRef.MultiPartIdentifier!.Identifiers[0].Value, derivedAlias, StringComparison.OrdinalIgnoreCase))
            {
                var colName = colRef.MultiPartIdentifier.Identifiers.Last().Value;
                if (columnMap.TryGetValue(colName, out var innerExpr) && innerExpr is not ColumnReferenceExpression)
                    return true;
            }
            else if (identCount == 1)
            {
                // Unqualified ref — if it matches a complex expression in the column map, bail out.
                // Skip if the column name is shared by multiple QDTs (ambiguous — may not belong to this QDT).
                var colName = colRef.MultiPartIdentifier!.Identifiers[0].Value;
                if (!sharedColumnNames.Contains(colName) &&
                    columnMap.TryGetValue(colName, out var innerExpr) && innerExpr is not ColumnReferenceExpression)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks all QuerySpecifications in the root tree for cross-scope references to the alias.
    /// This catches references at ancestor scopes — e.g., when VLanguages is inside VActivePersons's
    /// QDT but referenced from an outer view's aliased join ON clause.
    /// </summary>
    private bool HasCrossScopeReferencesInTree(string derivedAlias, Dictionary<string, ScalarExpression> columnMap)
    {
        if (_rootTree == null)
            return false;

        var finder = new QuerySpecificationFinder();
        _rootTree.Accept(finder);

        foreach (var querySpec in finder.QuerySpecifications)
        {
            if (querySpec.FromClause != null && HasCrossScopeReferences(querySpec.FromClause, derivedAlias, columnMap))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if any join ON clause that does NOT contain the QDT (identified by derivedAlias)
    /// has 2-part column references to the QDT's alias, or 1-part (unqualified) column
    /// references matching the QDT's column map. Unqualified refs are checked because the
    /// rewriter would qualify them as derivedAlias.col, creating a new cross-scope reference.
    /// </summary>
    private static bool HasCrossScopeReferences(FromClause fromClause, string derivedAlias,
        Dictionary<string, ScalarExpression> columnMap)
    {
        foreach (var tableRef in fromClause.TableReferences)
        {
            if (CheckForCrossScopeRefs(tableRef, derivedAlias, columnMap))
                return true;
        }
        return false;
    }

    private static bool CheckForCrossScopeRefs(TableReference tableRef, string derivedAlias,
        Dictionary<string, ScalarExpression> columnMap)
    {
        switch (tableRef)
        {
            case QualifiedJoin join:
            {
                bool qdtInSubtree = ContainsAlias(join, derivedAlias);

                if (!qdtInSubtree && join.SearchCondition != null)
                {
                    // This join doesn't contain the QDT. Check if its ON clause
                    // has 2-part references to the QDT's alias, or 1-part references
                    // matching the column map (which the rewriter would qualify).
                    var collector = new OuterScopeColumnReferenceCollector();
                    join.SearchCondition.Accept(collector);
                    foreach (var colRef in collector.References)
                    {
                        var mpi = colRef.MultiPartIdentifier;
                        if (mpi == null) continue;

                        if (mpi.Identifiers.Count >= 2 &&
                            string.Equals(mpi.Identifiers[0].Value, derivedAlias, StringComparison.OrdinalIgnoreCase))
                            return true;

                        if (mpi.Identifiers.Count == 1 &&
                            columnMap.ContainsKey(mpi.Identifiers[0].Value))
                            return true;
                    }
                }

                return CheckForCrossScopeRefs(join.FirstTableReference, derivedAlias, columnMap) ||
                       CheckForCrossScopeRefs(join.SecondTableReference, derivedAlias, columnMap);
            }
            case UnqualifiedJoin unqualifiedJoin:
                return CheckForCrossScopeRefs(unqualifiedJoin.FirstTableReference, derivedAlias, columnMap) ||
                       CheckForCrossScopeRefs(unqualifiedJoin.SecondTableReference, derivedAlias, columnMap);
            default:
                return false;
        }
    }

    /// <summary>
    /// Returns true if the table reference subtree contains a QDT or NamedTableReference
    /// with the given alias.
    /// </summary>
    private static bool ContainsAlias(TableReference tableRef, string alias)
    {
        switch (tableRef)
        {
            case QueryDerivedTable qdt:
                return string.Equals(qdt.Alias?.Value, alias, StringComparison.OrdinalIgnoreCase);
            case NamedTableReference named:
                var namedAlias = named.Alias?.Value ?? named.SchemaObject.BaseIdentifier.Value;
                return string.Equals(namedAlias, alias, StringComparison.OrdinalIgnoreCase);
            case QualifiedJoin join:
                return ContainsAlias(join.FirstTableReference, alias) ||
                       ContainsAlias(join.SecondTableReference, alias);
            case UnqualifiedJoin unqualifiedJoin:
                return ContainsAlias(unqualifiedJoin.FirstTableReference, alias) ||
                       ContainsAlias(unqualifiedJoin.SecondTableReference, alias);
            default:
                return false;
        }
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
    /// Qualifies all single-part (unqualified) column references in the inner query by
    /// prepending the given table alias. Prevents ambiguity when the inner query's tables
    /// are promoted into an outer scope with additional tables.
    /// </summary>
    private static void QualifyUnqualifiedColumnReferences(QuerySpecification innerQuery, string tableAlias)
    {
        var qualifier = new UnqualifiedColumnQualifier(tableAlias);
        innerQuery.Accept(qualifier);
    }

    /// <summary>
    /// Returns true if the inner query contains any single-part (unqualified) column references.
    /// </summary>
    private static bool HasUnqualifiedColumnReferences(QuerySpecification innerQuery)
    {
        var checker = new UnqualifiedColumnChecker();
        innerQuery.Accept(checker);
        return checker.Found;
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
    private void RewriteOuterColumnReferences(QuerySpecification outerQuery,
        string derivedAlias, Dictionary<string, ScalarExpression> columnMap, HashSet<string> sharedColumnNames)
    {
        // Collect outer-scope column references from all clauses
        // (does not descend into QueryDerivedTable nodes)
        var collector = new OuterScopeColumnReferenceCollector();

        foreach (var element in outerQuery.SelectElements)
            element.Accept(collector);
        outerQuery.WhereClause?.Accept(collector);
        outerQuery.FromClause?.Accept(collector);
        outerQuery.OrderByClause?.Accept(collector);
        outerQuery.GroupByClause?.Accept(collector);
        outerQuery.HavingClause?.Accept(collector);

        foreach (var colRef in collector.References)
        {
            var mpi = colRef.MultiPartIdentifier;
            if (mpi == null)
                continue;

            string? colName = null;

            if (mpi.Identifiers.Count >= 2 &&
                string.Equals(mpi.Identifiers[0].Value, derivedAlias, StringComparison.OrdinalIgnoreCase))
            {
                colName = mpi.Identifiers.Last().Value;
            }
            else if (mpi.Identifiers.Count == 1 && columnMap.ContainsKey(mpi.Identifiers[0].Value)
                     && !sharedColumnNames.Contains(mpi.Identifiers[0].Value))
            {
                // Unqualified ref matching a column in the derived table.
                // Skip if the column name is shared by multiple QDTs in scope — the ref
                // may belong to a sibling QDT (e.g., Code in phoneType's ON condition
                // should not be claimed by VLanguages when both derive from dbo.Codes).
                colName = mpi.Identifiers[0].Value;
            }

            if (colName == null || !columnMap.TryGetValue(colName, out var innerExpr))
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
    /// Captures the inferred column name (last identifier) of each unaliased SELECT element
    /// that references the given derived table alias, before column reference rewriting.
    /// </summary>
    private static List<(SelectScalarExpression Element, string OriginalName)> SnapshotSelectColumnNames(
        QuerySpecification outerQuery, string derivedAlias, Dictionary<string, ScalarExpression> columnMap)
    {
        var snapshots = new List<(SelectScalarExpression, string)>();

        foreach (var element in outerQuery.SelectElements)
        {
            if (element is not SelectScalarExpression { ColumnName: null, Expression: ColumnReferenceExpression colRef } scalar)
                continue;

            var mpi = colRef.MultiPartIdentifier;
            if (mpi == null)
                continue;

            if (mpi.Identifiers.Count >= 2 &&
                string.Equals(mpi.Identifiers[0].Value, derivedAlias, StringComparison.OrdinalIgnoreCase))
            {
                snapshots.Add((scalar, mpi.Identifiers.Last().Value));
            }
            else if (mpi.Identifiers.Count == 1 && columnMap.ContainsKey(mpi.Identifiers[0].Value))
            {
                snapshots.Add((scalar, mpi.Identifiers[0].Value));
            }
        }

        return snapshots;
    }

    /// <summary>
    /// After column reference rewriting, restores explicit aliases on SELECT elements
    /// where the inferred column name (last identifier) changed from the snapshot.
    /// </summary>
    private static void RestoreSelectColumnNames(List<(SelectScalarExpression Element, string OriginalName)> snapshots)
    {
        foreach (var (element, originalName) in snapshots)
        {
            if (element.Expression is not ColumnReferenceExpression colRef)
                continue;

            var currentName = colRef.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value;
            if (currentName != null && !string.Equals(originalName, currentName, StringComparison.OrdinalIgnoreCase))
            {
                element.ColumnName = new IdentifierOrValueExpression
                {
                    Identifier = new Identifier { Value = originalName },
                };
            }
        }
    }

    /// <summary>
    /// Merges a boolean condition into the outer query's WHERE clause using AND.
    /// Used for top-level FROM entries where there is no parent JOIN to merge into.
    /// </summary>
    private static void MergeWhereCondition(QuerySpecification outerQuery, BooleanExpression condition)
    {
        if (outerQuery.WhereClause == null)
        {
            outerQuery.WhereClause = new WhereClause { SearchCondition = condition };
        }
        else
        {
            outerQuery.WhereClause.SearchCondition = new BooleanBinaryExpression
            {
                BinaryExpressionType = BooleanBinaryExpressionType.And,
                FirstExpression = new BooleanParenthesisExpression
                {
                    Expression = outerQuery.WhereClause.SearchCondition,
                },
                SecondExpression = new BooleanParenthesisExpression
                {
                    Expression = condition,
                },
            };
        }
    }

    /// <summary>
    /// Detects column names that appear in more than one QueryDerivedTable within the FROM clause.
    /// These are ambiguous for 1-part (unqualified) matching: an unqualified "Code" might belong
    /// to any of the QDTs that expose a "Code" column.
    /// </summary>
    private static HashSet<string> CollectSharedQdtColumnNames(FromClause fromClause)
    {
        var columnCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void CollectFromTableRef(TableReference tableRef)
        {
            switch (tableRef)
            {
                case QueryDerivedTable { QueryExpression: QuerySpecification innerQuery }:
                    foreach (var element in innerQuery.SelectElements)
                    {
                        if (element is not SelectScalarExpression scalar)
                            continue;

                        string? name = null;
                        if (scalar.ColumnName != null)
                            name = scalar.ColumnName.Value;
                        else if (scalar.Expression is ColumnReferenceExpression colRef)
                            name = colRef.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value;

                        if (name != null)
                            columnCounts[name] = columnCounts.GetValueOrDefault(name) + 1;
                    }
                    break;
                case QualifiedJoin join:
                    CollectFromTableRef(join.FirstTableReference);
                    CollectFromTableRef(join.SecondTableReference);
                    break;
                case UnqualifiedJoin unqualifiedJoin:
                    CollectFromTableRef(unqualifiedJoin.FirstTableReference);
                    CollectFromTableRef(unqualifiedJoin.SecondTableReference);
                    break;
            }
        }

        foreach (var tableRef in fromClause.TableReferences)
            CollectFromTableRef(tableRef);

        var shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, count) in columnCounts)
        {
            if (count > 1)
                shared.Add(name);
        }

        return shared;
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
            case UnqualifiedJoin unqualifiedJoin:
                CollectAliasesFromTableReference(unqualifiedJoin.FirstTableReference, aliases);
                CollectAliasesFromTableReference(unqualifiedJoin.SecondTableReference, aliases);
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

    /// <summary>
    /// Visitor that renames column references using a batch rename map (oldAlias -> newAlias)
    /// in a single pass to prevent cascading corruption.
    /// </summary>
    private sealed class MultiAliasRenamer : TSqlFragmentVisitor
    {
        private readonly Dictionary<string, string> renameMap;

        public MultiAliasRenamer(Dictionary<string, string> renameMap)
        {
            this.renameMap = renameMap;
        }

        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            if (node.MultiPartIdentifier is { Identifiers.Count: > 0 } mpi &&
                renameMap.TryGetValue(mpi.Identifiers[0].Value, out var newAlias))
            {
                mpi.Identifiers[0].Value = newAlias;
            }

            base.ExplicitVisit(node);
        }
    }

    /// <summary>
    /// Visitor that qualifies single-part column references by prepending a table alias.
    /// </summary>
    private sealed class UnqualifiedColumnQualifier : TSqlFragmentVisitor
    {
        private readonly string tableAlias;

        public UnqualifiedColumnQualifier(string tableAlias)
        {
            this.tableAlias = tableAlias;
        }

        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            if (node.ColumnType == ColumnType.Regular &&
                node.MultiPartIdentifier is { Identifiers.Count: 1 })
            {
                node.MultiPartIdentifier.Identifiers.Insert(0, new Identifier { Value = tableAlias });
            }

            base.ExplicitVisit(node);
        }
    }

    /// <summary>
    /// Visitor that checks if any single-part (unqualified) column references exist.
    /// </summary>
    private sealed class UnqualifiedColumnChecker : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }

        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            if (node.ColumnType == ColumnType.Regular &&
                node.MultiPartIdentifier is { Identifiers.Count: 1 })
            {
                Found = true;
            }

            base.ExplicitVisit(node);
        }
    }
}
