using System.ComponentModel;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlInliner
{
    /// <summary>
    /// Provides additional extension methods for <see cref="SchemaObjectName"/>.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class SchemaObjectNameEx
    {
        /// <summary>
        /// Converts the <see cref="SchemaObjectName"/> to a 2-part quoted identifier, e.g. Schema.View becomes [Schema].[View] and Table becomes [dbo].[Table]
        /// </summary>
        public static string GetName(this SchemaObjectName objectName)
        {
            // TODO: Configure default schema name instead of hard-coding dbo
            return Identifier.EncodeIdentifier(objectName.SchemaIdentifier?.Value ?? "dbo") + "." + Identifier.EncodeIdentifier(objectName.BaseIdentifier.Value);
        }
    }
}