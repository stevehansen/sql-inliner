using System.Collections.Generic;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlInliner
{
    /// <summary>
    /// This visitor is used to replace usages of the view to an inline select query (e.g. "inner join view v" becomes "inner join (select x from table) v") and to remove unused joins.
    /// </summary>
    /// <remarks>
    /// ScriptDom nodes have no concept of a parent node so we always need to start from the parent and check for the specific children.
    /// </remarks>
    public sealed class TableInlineVisitor : TSqlFragmentVisitor
    {
        private readonly Dictionary<TableReference, TableReference> toReplace;
        private readonly List<TableReference> toRemove;

        public TableInlineVisitor(Dictionary<TableReference, TableReference> toReplace, List<TableReference> toRemove)
        {
            this.toReplace = toReplace;
            this.toRemove = toRemove;
        }

        /// <inheritdoc />
        public override void ExplicitVisit(FromClause node)
        {
            // Handle the case where the view that we need to replace is used directly in the from statement, e.g. select v.x from view v
            // TODO: Removing the view from the from statement is probably not possible

            for (var i = 0; i < node.TableReferences.Count; i++)
            {
                if (toRemove.Count > 0)
                    node.TableReferences[i] = RemoveReference(node.TableReferences[i]);

                if (toReplace.TryGetValue(node.TableReferences[i], out var replacement))
                {
                    toReplace.Remove(node.TableReferences[i]);
                    node.TableReferences[i] = replacement;
                }
            }

            base.ExplicitVisit(node);
        }

        /// <inheritdoc />
        public override void ExplicitVisit(JoinTableReference node) // TODO: Not used?
        {
            if (toRemove.Count > 0)
            {
                node.FirstTableReference = RemoveReference(node.FirstTableReference);
                node.SecondTableReference = RemoveReference(node.SecondTableReference);
            }

            if (toReplace.TryGetValue(node.FirstTableReference, out var replacement))
            {
                toReplace.Remove(node.FirstTableReference);
                node.FirstTableReference = replacement;
            }

            if (toReplace.TryGetValue(node.SecondTableReference, out replacement))
            {
                toReplace.Remove(node.SecondTableReference);
                node.SecondTableReference = replacement;
            }

            base.ExplicitVisit(node);
        }

        /// <inheritdoc />
        public override void ExplicitVisit(QualifiedJoin node)
        {
            if (toRemove.Count > 0)
            {
                node.FirstTableReference = RemoveReference(node.FirstTableReference);
                node.SecondTableReference = RemoveReference(node.SecondTableReference);
            }

            if (toReplace.TryGetValue(node.FirstTableReference, out var replacement))
            {
                toReplace.Remove(node.FirstTableReference);
                node.FirstTableReference = replacement;
            }

            if (toReplace.TryGetValue(node.SecondTableReference, out replacement))
            {
                toReplace.Remove(node.SecondTableReference);
                node.SecondTableReference = replacement;
            }

            base.ExplicitVisit(node);
        }

        private TableReference RemoveReference(TableReference tableReference)
        {
            // We start from the top reference and will either remove the first or second side if needed, e.g. view1 v1 join view1 v2 will be replace by either side if we need to remove it

            if (tableReference is JoinTableReference join)
            {
                if (toRemove.Contains(join.FirstTableReference))
                {
                    toRemove.Remove(join.FirstTableReference);
                    return join.SecondTableReference;
                }

                if (toRemove.Contains(join.SecondTableReference))
                {
                    toRemove.Remove(join.SecondTableReference);
                    return join.FirstTableReference;
                }
            }

            return tableReference;
        }
    }
}