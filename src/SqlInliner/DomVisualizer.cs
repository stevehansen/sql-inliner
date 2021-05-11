using System.Collections.Generic;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlInliner
{
    /// <summary>
    /// Helper class to visualize the ScriptDom tree for debugging purpose.
    /// </summary>
    internal sealed class DomVisualizer
    {
        private readonly StringBuilder result = new();

        public void Walk(TSqlFragment fragment) => Walk(fragment, "root");

        public void Walk(object fragment, string memberName)
        {
            if (fragment.GetType().BaseType?.Name != "Enum")
            {
                result.AppendLine("<" + fragment.GetType().Name + " memberName = '" + memberName + "'>");
            }
            else
            {
                result.AppendLine("<" + fragment.GetType().Name + "." + fragment + "/>");
                return;
            }

            var t = fragment.GetType();

            foreach (var pi in t.GetProperties())
            {
                if (pi.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                if (pi.PropertyType.BaseType is { Name: "ValueType" })
                {
                    result.Append("<" + pi.Name + ">" + pi.GetValue(fragment) + "</" + pi.Name + ">");
                    continue;
                }

                if (pi.PropertyType.Name.Contains(@"IList`1"))
                {
                    if ("ScriptTokenStream" != pi.Name)
                    {
                        var listMembers = (IEnumerable<object>)pi.GetValue(fragment)!;

                        foreach (var listItem in listMembers)
                        {
                            Walk(listItem, pi.Name);
                        }
                    }
                }
                else
                {
                    var childObj = pi.GetValue(fragment);

                    if (childObj != null)
                    {
                        if (childObj is string)
                        {
                            result.Append(pi.GetValue(fragment));
                        }
                        else
                        {
                            Walk(childObj, pi.Name);
                        }
                    }
                }
            }

            result.AppendLine("</" + fragment.GetType().Name + ">");
        }

        /// <inheritdoc />
        public override string ToString() => result.ToString();
    }
}