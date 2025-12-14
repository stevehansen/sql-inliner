using NUnit.Framework;

namespace SqlInliner.Tests;

public class ErrorHandlingTests
{
    private DatabaseConnection connection;
    private readonly InlinerOptions options = InlinerOptions.Recommended();

    [SetUp]
    public void Setup()
    {
        connection = new();
    }

    [Test]
    public void InvalidSql_ReturnsErrorsInSql()
    {
        const string viewSql = "CREATE OR VIEW dbo.X AS SELECT 0";

        var inliner = new DatabaseViewInliner(connection, viewSql);
        Assert.IsNull(inliner.View);
        Assert.IsTrue(inliner.Sql.Contains("Failed parsing query"));
    }

    [Test]
    public void ViewWithoutAlias_AddsError()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"), "CREATE VIEW dbo.VPeople AS SELECT Id FROM dbo.People");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT Id FROM dbo.VPeople";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.Greater(inliner.Errors.Count, 0);
        Assert.IsTrue(inliner.Errors[0].Contains("without using an alias"));
    }

    [Test]
    public void MultipleViewsWithoutAlias_ListsAllInError()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"), "CREATE VIEW dbo.VPeople AS SELECT Id FROM dbo.People");
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VOrders"), "CREATE VIEW dbo.VOrders AS SELECT Id FROM dbo.Orders");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT VPeople.Id, VOrders.Id FROM dbo.VPeople INNER JOIN dbo.VOrders ON VPeople.Id = VOrders.PersonId";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.Greater(inliner.Errors.Count, 0);
    }

    [Test]
    public void SinglePartIdentifier_AddsWarning()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"), "CREATE VIEW dbo.VPeople AS SELECT Id, Name FROM dbo.People");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT Id FROM dbo.VPeople p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.Greater(inliner.Warnings.Count, 0);
        Assert.IsTrue(inliner.Warnings[0].Contains("single part identifiers"));
    }

    [Test]
    public void NoColumnsSelected_AddsWarning()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"), "CREATE VIEW dbo.VPeople AS SELECT Id, Name FROM dbo.People");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT 1 AS Val FROM dbo.Table t INNER JOIN dbo.VPeople p ON 1=1";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.Greater(inliner.Warnings.Count, 0);
    }

    [Test]
    public void OnlyOneColumnSelected_AddsWarningWhenJoinsNotStripped()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"), "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.Name FROM dbo.People p");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id FROM dbo.VPeople p WHERE p.Id = 1";
        
        var inliner = new DatabaseViewInliner(connection, viewSql);
        Assert.Greater(inliner.Warnings.Count, 0);
        Assert.IsTrue(inliner.Warnings[0].Contains("Only 1 column"));
    }

    [Test]
    public void ValidSql_NoErrors()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"), "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.Name FROM dbo.People p");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id, p.Name FROM dbo.VPeople p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.AreEqual(0, inliner.Errors.Count);
    }

    [Test]
    public void ValidSqlWithAlias_NoErrors()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"), "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.Name FROM dbo.People p");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id PersonId, v.Name PersonName FROM dbo.VPeople v";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.AreEqual(0, inliner.Errors.Count);
    }
}
