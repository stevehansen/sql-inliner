using Microsoft.SqlServer.TransactSql.ScriptDom;
using NUnit.Framework;
using Shouldly;

namespace SqlInliner.Tests;

public class SchemaObjectNameTests
{
    [Test]
    public void GetName_WithSchemaAndTable_ReturnsQuotedName()
    {
        var objectName = DatabaseConnection.ToObjectName("dbo", "MyTable");
        var result = objectName.GetName();
        result.ShouldBe("[dbo].[MyTable]");
    }

    [Test]
    public void GetName_WithCustomSchema_ReturnsQuotedName()
    {
        var objectName = DatabaseConnection.ToObjectName("custom", "MyView");
        var result = objectName.GetName();
        result.ShouldBe("[custom].[MyView]");
    }

    [Test]
    public void GetName_WithSpecialCharactersInName_ReturnsQuotedName()
    {
        var objectName = DatabaseConnection.ToObjectName("dbo", "My View With Spaces");
        var result = objectName.GetName();
        result.ShouldBe("[dbo].[My View With Spaces]");
    }

    [Test]
    public void GetName_WithoutSchema_UsesDefaultDbo()
    {
        var objectName = new SchemaObjectName();
        objectName.Identifiers.Add(new Identifier { Value = "MyTable" });
        var result = objectName.GetName();
        result.ShouldBe("[dbo].[MyTable]");
    }

    [Test]
    public void ToObjectName_CreatesObjectWithTwoIdentifiers()
    {
        var objectName = DatabaseConnection.ToObjectName("schema", "table");
        objectName.Identifiers.Count.ShouldBe(2);
        objectName.Identifiers[0].Value.ShouldBe("schema");
        objectName.Identifiers[1].Value.ShouldBe("table");
    }
}