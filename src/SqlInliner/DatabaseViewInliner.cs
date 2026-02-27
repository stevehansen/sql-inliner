using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlInliner;

/// <summary>
/// Analyzes usage of nested views and inlines them as a sub-select, optionally will remove parts of the nested selects depending on the <see cref="InlinerOptions"/>.
/// </summary>
public sealed class DatabaseViewInliner
{
    private readonly DatabaseConnection connection;
    private readonly InlinerOptions options;

    /// <summary>
    /// Creates a new instance of <see cref="DatabaseViewInliner"/>.
    /// </summary>
    public DatabaseViewInliner(DatabaseConnection connection, string viewSql, InlinerOptions? options = null)
    {
        this.connection = connection;
        this.options = options ?? new();

        var sw = Stopwatch.StartNew();

        // Parse definition
        var (view, errors) = DatabaseView.FromSql(connection, viewSql);
        if (view == null)
        {
            // NOTE: Should not happen :)
            Sql = "/*\nFailed parsing query:\n" + string.Join("\n", errors.Select(e => e.Message)) + "\n\nOriginal query was kept:\n*/" + viewSql;
            return;
        }

        View = view;

        var tree = view.Tree;
        var references = view.References;

#if DEBUG
        var treeVisualizer = new DomVisualizer();
        treeVisualizer.Walk(tree);
#endif

        // Recursively check nested views (and track functions for information purpose)
        var referencedViews = references.Views;
        if (referencedViews.Count == 0)
        {
            // No nested views, return original SQL
            Sql = viewSql;
            return;
        }

        var knownViews = new Dictionary<string, DatabaseView>
        {
            { view.ViewName, view },
        };

        void AddView(NamedTableReference viewReference)
        {
            var viewName = viewReference.SchemaObject.GetName();
            if (!knownViews.ContainsKey(viewName))
            {
                var referencedDefinition = connection.GetViewDefinition(viewName);
                var (referencedView, _) = DatabaseView.FromSql(connection, referencedDefinition);
                if (referencedView == null)
                    return; // TODO: Handle as error?

                knownViews[viewName] = referencedView;

                foreach (var nestedViewReference in referencedView.References.Views)
                    AddView(nestedViewReference);
            }
        }

        foreach (var viewReference in referencedViews)
            AddView(viewReference);

        // Inline views
        Inline(view);

        // Optionally strip unused columns/joins inside nested derived tables
        if (this.options.StripUnusedColumns || this.options.StripUnusedJoins)
        {
            var stripper = new DerivedTableStripper(this.options);
            stripper.Strip(tree);
            TotalSelectColumnsStripped += stripper.TotalColumnsStripped;
            TotalJoinsStripped += stripper.TotalJoinsStripped;
            Warnings.AddRange(stripper.Warnings);
        }

        // Optionally flatten derived tables produced by inlining
        if (this.options.FlattenDerivedTables)
        {
            var flattener = new DerivedTableFlattener();
            TotalDerivedTablesFlattened = flattener.Flatten(tree);
            Warnings.AddRange(flattener.Warnings);
        }

        // Output result, starting with original SQL as comment + extra information from checking code (e.g. list used nested views and functions)

        new Sql150ScriptGenerator(GetOptions()).GenerateScript(tree, out var formattedSql);

        sw.Stop();

        var result = new InlinerResult(this, sw.Elapsed, viewSql, knownViews, formattedSql, this.options);

        Sql = result.Sql;
        Result = result;
    }

    /// <summary>
    /// Gets the view that was inlined.
    /// </summary>
    public DatabaseView? View { get; }

    /// <summary>
    /// Gets the errors that prevented us from converting the view.
    /// </summary>
    public List<string> Errors { get; } = new();

    /// <summary>
    /// Gets the warnings that were detected when converting the view.
    /// </summary>
    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Gets the total of <see cref="SelectElement"/> that we stripped.
    /// </summary>
    public int TotalSelectColumnsStripped { get; private set; }

    /// <summary>
    /// Gets the total of <see cref="NamedTableReference"/> that we stripped.
    /// </summary>
    public int TotalJoinsStripped { get; private set; }

    /// <summary>
    /// Gets the total of <see cref="QueryDerivedTable"/> nodes that were flattened into the outer query.
    /// </summary>
    public int TotalDerivedTablesFlattened { get; private set; }

    /// <summary>
    /// Gets the resulting SQL statement that should be used as replacement.
    /// </summary>
    public string Sql { get; }

    /// <summary>
    /// Gets the optional information for a successful inlining result.
    /// </summary>
    public InlinerResult? Result { get; }

    /// <summary>
    /// Start from the specified <paramref name="view"/> and recursively inline any used views.
    /// </summary>
    private void Inline(DatabaseView view)
    {
        var toReplace = new Dictionary<TableReference, TableReference>();
        var toRemove = new List<TableReference>();

        var tree = view.Tree!;
        var references = view.References!;
        var referencedViews = references.Views;
        if (referencedViews.Count == 0)
        {
            DetectUnusedTablesToStrip(references, toRemove);

            var lastRemovalCount = toRemove.Count + 1;
            while (toRemove.Count > 0 && toRemove.Count < lastRemovalCount) // TODO: Optimize?
            {
                lastRemovalCount = toRemove.Count;
                tree.Accept(new TableInlineVisitor(toReplace, toRemove));
            }

            return;
        }

        var withoutAlias = referencedViews
            .Where(v => v.Alias == null)
            .ToArray();
        if (withoutAlias.Length > 0)
        {
            var withoutAliasInfo = string.Join("\n", withoutAlias.Select(a => " - " + a.SchemaObject.GetName()));
            Errors.Add($"Use of tables without using an alias in {view.ViewName}, aborting:\n{withoutAliasInfo}");
            return;
        }

        if (options.StripUnusedJoins)
        {
            var invalidColumnReferences = references.ColumnReferences
                .Where(it => it.MultiPartIdentifier.Count == 1)
                .ToArray();
            if (invalidColumnReferences.Length > 0)
            {
                // NOTE: Use of single part identifier will block stripping of joins
                var invalidColumnReferencesInfo = string.Join("\n", invalidColumnReferences.Select(a => " - " + string.Join(".", a.MultiPartIdentifier.Identifiers.Select(id => id.Value))));
                Warnings.Add($"Use of single part identifiers in {view.ViewName}:\n{invalidColumnReferencesInfo}");
            }
        }

        foreach (var referenced in referencedViews)
        {
            var viewName = referenced.SchemaObject.GetName();
            var alias = referenced.Alias.Value;

            var referencedDefinition = connection.GetViewDefinition(viewName);
            var (referencedView, _) = DatabaseView.FromSql(connection, referencedDefinition);
            var query = referencedView?.References.Query;
            if (referencedView == null || query == null)
            {
                Errors.Add($"Could not inline {viewName} {alias}, aborting.");
                return;
            }

            var queryExpression = referencedView.References!.Body!.SelectStatement.QueryExpression; // NOTE: Can be either query or a BinaryQueryExpression for an union

            if (options.StripUnusedColumns && !HasSelectStarForView(view.References!.Body!.SelectStatement.QueryExpression, referenced, alias))
            {
                // NOTE: Strip unused select columns from nested views

                // We are going to list all possible references to the current view, by alias or any single part identifiers.
                var columns = new HashSet<string>(references.ColumnReferences
                        .Where(c => c.MultiPartIdentifier.Count == 1 || c.MultiPartIdentifier[0].Value == alias)
                        .Select(c => c.MultiPartIdentifier.Identifiers.Last().Value)
                    , StringComparer.OrdinalIgnoreCase);

                var allSelectElements = new List<IList<SelectElement>>();

                void AddSelectElements(QueryExpression otherQuery)
                {
                    while (true)
                    {
                        switch (otherQuery)
                        {
                            case QuerySpecification specification:
                                allSelectElements.Add(specification.SelectElements);
                                break;

                            case BinaryQueryExpression binary:
                                AddSelectElements(binary.FirstQueryExpression);
                                otherQuery = binary.SecondQueryExpression;
                                continue;
                        }

                        break;
                    }
                }

                // A query expression can be a select statement (QuerySpecification) or combination (BinaryQueryExpression for a union, except or intersect)
                AddSelectElements(queryExpression);

                void RemoveAt(int idx)
                {
                    // Remove the specific index of the select elements on all query specifications
                    foreach (var select in allSelectElements)
                    {
                        var selectElement = select[idx];
                        select.RemoveAt(idx);
                        TotalSelectColumnsStripped++;
                        if (selectElement is SelectScalarExpression { Expression: ColumnReferenceExpression columnReference })
                            referencedView.References!.ColumnReferences.Remove(columnReference);
                    }
                }

                // The first query specification will have the correct column names or aliases that should be checked
                var selectElements = query.SelectElements;
                for (var i = selectElements.Count - 1; i >= 0; i--)
                {
                    if (selectElements[i] is SelectScalarExpression selectExpression)
                    {
                        if (selectExpression.ColumnName != null)
                        {
                            if (!columns.Contains(selectExpression.ColumnName.Value))
                                RemoveAt(i);
                        }
                        else if (selectExpression.Expression is ColumnReferenceExpression columnReference)
                        {
                            if (!columns.Contains(columnReference.MultiPartIdentifier.Identifiers.Last().Value))
                                RemoveAt(i);
                        }
                    }
                }

                switch (selectElements.Count)
                {
                    case 0:
                        // NOTE: This would be a weird case where we are using a qualified join but we aren't using anything in the ON condition (e.g. inner join view on 1=1)
                        Warnings.Add($"No columns are selected from {viewName} {alias} in {view.ViewName}");

                        selectElements.Add(new SelectScalarExpression
                        {
                            ColumnName = new()
                            {
                                Identifier = new()
                                {
                                    Value = "unused",
                                },
                            },
                            Expression = new NumericLiteral
                            {
                                Value = "0",
                            },
                        });
                        break;

                    case 1 when options.StripUnusedJoins:
                        // If the join has cardinality hints, verify removal is safe before stripping.
                        if (references.JoinHints.TryGetValue(referenced, out var viewHint))
                        {
                            references.JoinTypes.TryGetValue(referenced, out var viewJoinType);
                            if (!IsJoinSafeToRemove(viewHint, viewJoinType))
                            {
                                Warnings.Add($"Only 1 column is selected from {viewName} {alias} in {view.ViewName}, but join hint indicates removal is not safe.");
                                break;
                            }
                        }

                        // Check if the remaining column is used outside the join condition.
                        // The join key column may also appear in SELECT/WHERE — in that case
                        // we can't strip the view because the column is needed externally.
                        // Note: JoinConditions only stores second-table references. When the
                        // view is the first table in a join, check ALL references to the alias.
                        {
                            HashSet<ColumnReferenceExpression>? joinColumnRefs = null;
                            if (references.JoinConditions.TryGetValue(referenced, out var joinSearchCondition))
                                joinColumnRefs = CollectColumnReferences(joinSearchCondition);

                            var hasExternalRefs = references.ColumnReferences
                                .Where(c => joinColumnRefs == null || !joinColumnRefs.Contains(c))
                                .Any(c => c.MultiPartIdentifier.Count >= 2 &&
                                     string.Equals(c.MultiPartIdentifier[0].Value, alias, StringComparison.OrdinalIgnoreCase));
                            if (hasExternalRefs)
                            {
                                Warnings.Add($"Only 1 column is selected from {viewName} {alias} in {view.ViewName}, but it is referenced outside the join condition.");
                                break;
                            }
                        }

                        toRemove.Add(referenced);
                        TotalJoinsStripped++;
                        continue;

                    case 1:
                        Warnings.Add($"Only 1 column is selected from {viewName} {alias} in {view.ViewName}, enable StripUnusedJoins to remove these.");
                        break;
                }
            }

            // NOTE: Recursive inline views
            Inline(referencedView);

            if (Errors.Count > 0)
                return;

            toReplace[referenced] = new QueryDerivedTable
            {
                Alias = referenced.Alias,
                QueryExpression = queryExpression,
            };
        }

        DetectUnusedTablesToStrip(references, toRemove);

        // NOTE: Replace from/join with inner view
        tree.Accept(new TableInlineVisitor(toReplace, toRemove));

        var lastCount = toRemove.Count + 1;
        while (toRemove.Count > 0 && toRemove.Count < lastCount) // TODO: Optimize?
        {
            lastCount = toRemove.Count;
            tree.Accept(new TableInlineVisitor(toReplace, toRemove));
        }
    }

    private void DetectUnusedTablesToStrip(ReferencesVisitor references, List<TableReference> toRemove)
    {
        if (!options.StripUnusedJoins)
            return;

        foreach (var referenced in references.Tables)
        {
            var alias = referenced.Alias?.Value ?? referenced.SchemaObject.BaseIdentifier.Value;
            TryStripUnusedJoin(references, toRemove, referenced, alias);
        }

        foreach (var referenced in references.DerivedTables)
        {
            if (referenced.Alias != null)
                TryStripUnusedJoin(references, toRemove, referenced, referenced.Alias.Value);
        }
    }

    private void TryStripUnusedJoin(ReferencesVisitor references, List<TableReference> toRemove, TableReference referenced, string alias)
    {
        // Exclude column references from the join condition when it's safe to do so.
        // For LEFT/RIGHT OUTER JOINs this is always safe — they can't reduce rows.
        // For INNER JOINs, only exclude when AggressiveJoinStripping is opted in,
        // because the ON clause may act as a filter that affects row counts.
        HashSet<ColumnReferenceExpression>? joinConditionRefs = null;
        if (references.JoinConditions.TryGetValue(referenced, out var searchCondition))
        {
            var isOuterJoin = references.JoinTypes.TryGetValue(referenced, out var joinType)
                && joinType is QualifiedJoinType.LeftOuter or QualifiedJoinType.RightOuter or QualifiedJoinType.FullOuter;

            if (isOuterJoin || options.AggressiveJoinStripping)
                joinConditionRefs = CollectColumnReferences(searchCondition);
        }

        var columns = references.ColumnReferences
            .Where(c => joinConditionRefs == null || !joinConditionRefs.Contains(c))
            .Where(c => c.MultiPartIdentifier.Count == 1 || c.MultiPartIdentifier[0].Value == alias)
            .Select(c => c.MultiPartIdentifier.Identifiers.Last().Value);

        // When we exclude join condition refs, the threshold drops to 0 (no external usage).
        // Without exclusion, the original threshold of 1 accounts for the join key reference.
        var threshold = joinConditionRefs != null ? 0 : 1;
        if (columns.Count() <= threshold)
        {
            // If the join has cardinality hints, verify removal is safe before stripping.
            if (references.JoinHints.TryGetValue(referenced, out var hint))
            {
                references.JoinTypes.TryGetValue(referenced, out var joinType);
                if (!IsJoinSafeToRemove(hint, joinType))
                    return;
            }
            toRemove.Add(referenced);
            TotalJoinsStripped++;
        }
    }

    /// <summary>
    /// Determines whether a join with the specified hint and type can be safely removed
    /// without affecting the query's row count.
    /// </summary>
    private static bool IsJoinSafeToRemove(JoinHint hint, QualifiedJoinType joinType)
    {
        if (!hint.HasFlag(JoinHint.Unique))
            return false; // May fan out — not safe

        return joinType switch
        {
            // LEFT JOIN + unique: at most 1 match, all left-side rows preserved
            QualifiedJoinType.LeftOuter => true,
            // INNER JOIN + unique + required: exactly 1 match per row, no filtering
            QualifiedJoinType.Inner when hint.HasFlag(JoinHint.Required) => true,
            // Other cases (INNER without required, RIGHT, FULL) are not safe
            _ => false,
        };
    }

    private static HashSet<ColumnReferenceExpression> CollectColumnReferences(TSqlFragment fragment)
    {
        var collector = new ColumnReferenceCollector();
        fragment.Accept(collector);
        return collector.References;
    }

    private sealed class ColumnReferenceCollector : TSqlFragmentVisitor
    {
        public HashSet<ColumnReferenceExpression> References { get; } = new();

        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            if (node.MultiPartIdentifier != null)
                References.Add(node);
            base.ExplicitVisit(node);
        }
    }

    /// <summary>
    /// Checks whether any QuerySpecification in the query expression tree has a SelectStarExpression
    /// that references the given view (by alias or by being in the FROM clause for bare *).
    /// </summary>
    private static bool HasSelectStarForView(QueryExpression queryExpression, NamedTableReference viewRef, string alias)
    {
        switch (queryExpression)
        {
            case QuerySpecification spec:
                return QuerySpecHasSelectStarForView(spec, viewRef, alias);
            case BinaryQueryExpression binary:
                return HasSelectStarForView(binary.FirstQueryExpression, viewRef, alias) ||
                       HasSelectStarForView(binary.SecondQueryExpression, viewRef, alias);
            default:
                return false;
        }
    }

    private static bool QuerySpecHasSelectStarForView(QuerySpecification spec, NamedTableReference viewRef, string alias)
    {
        foreach (var element in spec.SelectElements)
        {
            if (element is SelectStarExpression star)
            {
                if (star.Qualifier == null || star.Qualifier.Identifiers.Count == 0)
                {
                    // Bare * — check if the view is in this QS's FROM clause
                    if (spec.FromClause != null && FromClauseContains(spec.FromClause, viewRef))
                        return true;
                }
                else
                {
                    // Qualified alias.* — check if last identifier matches the alias
                    var lastId = star.Qualifier.Identifiers[star.Qualifier.Identifiers.Count - 1].Value;
                    if (string.Equals(lastId, alias, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        return false;
    }

    private static bool FromClauseContains(FromClause fromClause, TableReference target)
    {
        foreach (var tableRef in fromClause.TableReferences)
        {
            if (TableReferenceContains(tableRef, target))
                return true;
        }
        return false;
    }

    private static bool TableReferenceContains(TableReference tableRef, TableReference target)
    {
        if (ReferenceEquals(tableRef, target))
            return true;

        switch (tableRef)
        {
            case QualifiedJoin join:
                return TableReferenceContains(join.FirstTableReference, target) ||
                       TableReferenceContains(join.SecondTableReference, target);
            case UnqualifiedJoin unqualifiedJoin:
                return TableReferenceContains(unqualifiedJoin.FirstTableReference, target) ||
                       TableReferenceContains(unqualifiedJoin.SecondTableReference, target);
            default:
                return false;
        }
    }

    private static SqlScriptGeneratorOptions GetOptions()
    {
        return new()
        {
            NewLineBeforeJoinClause = false,
        }; // TODO: Configure?
    }
}