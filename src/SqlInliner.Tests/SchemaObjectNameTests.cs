using Microsoft.SqlServer.TransactSql.ScriptDom;
using NUnit.Framework;

namespace SqlInliner.Tests;

public class SchemaObjectNameTests
{
    [Test]
    public void GetName_WithSchemaAndTable_ReturnsQuotedName()
    {
        var objectName = DatabaseConnection.ToObjectName("dbo", "MyTable");
        var result = objectName.GetName();
        Assert.AreEqual("[dbo].[MyTable]", result);
    }

    [Test]
    public void GetName_WithCustomSchema_ReturnsQuotedName()
    {
        var objectName = DatabaseConnection.ToObjectName("custom", "MyView");
        var result = objectName.GetName();
        Assert.AreEqual("[custom].[MyView]", result);
    }

    [Test]
    public void GetName_WithSpecialCharactersInName_ReturnsQuotedName()
    {
        var objectName = DatabaseConnection.ToObjectName("dbo", "My View With Spaces");
        var result = objectName.GetName();
        Assert.AreEqual("[dbo].[My View With Spaces]", result);
    }

    [Test]
    public void GetName_WithoutSchema_UsesDefaultDbo()
    {
        var objectName = new SchemaObjectName();
        objectName.Identifiers.Add(new Identifier { Value = "MyTable" });
        var result = objectName.GetName();
        Assert.AreEqual("[dbo].[MyTable]", result);
    }

    [Test]
    public void ToObjectName_CreatesObjectWithTwoIdentifiers()
    {
        var objectName = DatabaseConnection.ToObjectName("schema", "table");
        Assert.AreEqual(2, objectName.Identifiers.Count);
        Assert.AreEqual("schema", objectName.Identifiers[0].Value);
        Assert.AreEqual("table", objectName.Identifiers[1].Value);
    }
}
