using NUnit.Framework;
using Shouldly;

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
        inliner.View.ShouldBeNull();
        inliner.Sql.ShouldContain("Failed parsing query");
    }

    [Test]
    public void ViewWithoutAlias_Success()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"), "CREATE VIEW dbo.VPeople AS SELECT Id FROM dbo.People");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT Id FROM dbo.VPeople";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.Count.ShouldBe(0);
    }

    [Test]
    public void MultipleViewsWithoutAlias_Success()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"), "CREATE VIEW dbo.VPeople AS SELECT Id FROM dbo.People");
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VOrders"), "CREATE VIEW dbo.VOrders AS SELECT Id FROM dbo.Orders");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT VPeople.Id, VOrders.Id FROM dbo.VPeople INNER JOIN dbo.VOrders ON VPeople.Id = VOrders.PersonId";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.Count.ShouldBe(0);
    }

    [Test]
    public void SinglePartIdentifier_AddsWarning()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"), "CREATE VIEW dbo.VPeople AS SELECT Id, Name FROM dbo.People");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT Id FROM dbo.VPeople p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Warnings.Count.ShouldBeGreaterThan(0);
        inliner.Warnings[0].ShouldContain("single part identifiers");
    }

    [Test]
    public void NoColumnsSelected_AddsWarning()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"), "CREATE VIEW dbo.VPeople AS SELECT Id, Name FROM dbo.People p");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT 1 AS Val FROM dbo.MyTable t INNER JOIN dbo.VPeople ON 1=1";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Warnings.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public void OnlyOneColumnSelected_AddsWarningWhenJoinsNotStripped()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"), "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.Name FROM dbo.People p");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id FROM dbo.VPeople p WHERE p.Id = 1";
        
        var inliner = new DatabaseViewInliner(connection, viewSql);
        inliner.Warnings.Count.ShouldBeGreaterThan(0);
        inliner.Warnings[0].ShouldContain("Only 1 column");
    }

    [Test]
    public void ValidSql_NoErrors()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"), "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.Name FROM dbo.People p");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id, p.Name FROM dbo.VPeople p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.Count.ShouldBe(0);
    }

    [Test]
    public void ValidSqlWithAlias_NoErrors()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"), "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.Name FROM dbo.People p");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id PersonId, v.Name PersonName FROM dbo.VPeople v";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.Count.ShouldBe(0);
    }
}