﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlInliner
{
    /// <summary>
    /// Analyzes usage of nested views and inlines them as a sub-select, optionally will remove parts of the nested selects depending on the <see cref="InlinerOptions"/>.
    /// </summary>
    public class DatabaseViewInliner
    {
        private readonly DatabaseConnection connection;
        private readonly InlinerOptions options;

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

            // Output result, starting with original SQL as comment + extra information from checking code (e.g. list used nested views and functions)

            new Sql150ScriptGenerator(GetOptions()).GenerateScript(tree, out var formattedSql);

            sw.Stop();

            // TODO: Convert to StringBuilder?
            Sql = $"/*\n-- Generated on {DateTime.Now:G} by {Program.AppName} in {sw.Elapsed}\n{DatabaseView.BeginOriginal}\n{viewSql}\n{DatabaseView.EndOriginal}\n\n-- Referenced views ({knownViews.Count}):\n{string.Join("\n", knownViews.Keys)}\n\n-- Removed: {TotalSelectColumnsStripped} select columns and {TotalJoinsStripped} joins\n\n-- Warnings ({Warnings.Count}):\n{string.Join("\n", Warnings)}\n\n-- Errors ({Errors.Count}):\n{string.Join("\n", Errors)}\n\n*/\n{formattedSql}\n\n";
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
        /// Gets the resulting SQL statement.
        /// </summary>
        public string Sql { get; }

        /// <summary>
        /// Start from the specified <paramref name="view"/> and recursively inline any used views.
        /// </summary>
        private void Inline(DatabaseView view)
        {
            var tree = view.Tree!;
            var references = view.References!;
            var referencedViews = references.Views;
            if (referencedViews.Count == 0)
                return;

            var withoutAlias = referencedViews
                .Where(v => v.Alias == null)
                .ToArray();
            if (withoutAlias.Length > 0)
            {
                var withoutAliasInfo = string.Join("\n", withoutAlias.Select(a => " - " + a.SchemaObject.GetName()));
                Errors.Add($"Use of tables without using an alias in {view.ViewName}, aborting:\n{withoutAliasInfo}");
                return;
            }

            // TODO: ScriptDom will report some parameters as column references, e.g. DATEADD(month, 1, t.Column) will report both "month" and "t.Column"
            //var invalidColumnReferences = references.ColumnReferences
            //    .Where(it => it.MultiPartIdentifier.Count == 1)
            //    .ToArray();
            //if (invalidColumnReferences.Length > 0)
            //{
            //    // NOTE: Use of multipart identifier is needed for now
            //    var invalidColumnReferencesInfo = string.Join("\n", invalidColumnReferences.Select(a => " - " + string.Join(".", a.MultiPartIdentifier.Identifiers.Select(id => id.Value))));
            //    Warnings.Add($"Use of multipart identifiers is needed in {view.ViewName}, aborting:\n{invalidColumnReferencesInfo}");
            //    return;
            //}

            var toReplace = new Dictionary<TableReference, TableReference>();
            var toRemove = new List<TableReference>();
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

                if (options.StripUnusedColumns)
                {
                    // NOTE: Strip unused select columns from nested views

                    // We are going to list all possible references to the current view, by alias or any single part identifiers.
                    var columns = references.ColumnReferences
                        .Where(c => c.MultiPartIdentifier.Count == 1 || c.MultiPartIdentifier[0].Value == alias)
                        .Select(c => c.MultiPartIdentifier.Identifiers.Last().Value)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

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

                        case 1 when options.StripUnusedJoins: // TODO: Allow configuration per specific join/alias, to keep certain joins that would be used as filter
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

            if (options.StripUnusedJoins)
            {
                // TODO: Remove unused table joins
            }

            // NOTE: Replace from/join with inner view
            tree.Accept(new TableInlineVisitor(toReplace, toRemove));
        }

        private static SqlScriptGeneratorOptions GetOptions()
        {
            return new()
            {
                NewLineBeforeJoinClause = false,
            }; // TODO: Configure?
        }
    }
}