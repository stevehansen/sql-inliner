using System.Diagnostics;
using Microsoft.SqlServer.TransactSql.ScriptDom;

[assembly: DebuggerDisplay("[Value={Value}, QuoteType={QuoteType}]", Target = typeof(Identifier))]
[assembly: DebuggerDisplay("[SchemaIdentifier={SchemaIdentifier.Value}, BaseIdentifier={BaseIdentifier.Value}, Count={Count}]", Target = typeof(SchemaObjectName))]
[assembly: DebuggerDisplay("[Count={Count}, Identifiers={Identifiers}]", Target = typeof(MultiPartIdentifier))]
[assembly: DebuggerDisplay("[MultiPartIdentifier={MultiPartIdentifier}, ColumnType={ColumnType}]", Target = typeof(ColumnReferenceExpression))]
[assembly: DebuggerDisplay("[SchemaObject={SchemaObject}, Alias={Alias.Value}]", Target = typeof(NamedTableReference))]