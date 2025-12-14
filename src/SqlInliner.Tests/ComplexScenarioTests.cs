using NUnit.Framework;

namespace SqlInliner.Tests;

public class ComplexScenarioTests
{
    private DatabaseConnection connection;
    private readonly InlinerOptions options = InlinerOptions.Recommended();

    [SetUp]
    public void Setup()
    {
        connection = new();
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"), 
            "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.FirstName, p.LastName, p.IsActive FROM dbo.People p");
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VOrders"), 
            "CREATE VIEW dbo.VOrders AS SELECT o.Id, o.PersonId, o.Amount, o.OrderDate FROM dbo.Orders o");
    }

    [Test]
    public void ViewWithLeftOuterJoin_Inlines()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id, p.FirstName, o.Amount FROM dbo.VPeople p LEFT OUTER JOIN dbo.VOrders o ON p.Id = o.PersonId";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.AreEqual(0, inliner.Errors.Count);
        Assert.IsTrue(inliner.Result.ConvertedSql.Contains("dbo.People"));
        Assert.IsTrue(inliner.Result.ConvertedSql.Contains("dbo.Orders"));
        Assert.IsFalse(inliner.Result.ConvertedSql.Contains("dbo.VPeople"));
        Assert.IsFalse(inliner.Result.ConvertedSql.Contains("dbo.VOrders"));
    }

    [Test]
    public void ViewWithRightOuterJoin_Inlines()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id, p.FirstName, o.Amount FROM dbo.VPeople p RIGHT OUTER JOIN dbo.VOrders o ON p.Id = o.PersonId";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.AreEqual(0, inliner.Errors.Count);
        Assert.IsTrue(inliner.Result.ConvertedSql.Contains("dbo.People"));
        Assert.IsTrue(inliner.Result.ConvertedSql.Contains("dbo.Orders"));
    }

    [Test]
    public void ViewWithFullOuterJoin_Inlines()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id, p.FirstName, o.Amount FROM dbo.VPeople p FULL OUTER JOIN dbo.VOrders o ON p.Id = o.PersonId";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.AreEqual(0, inliner.Errors.Count);
        Assert.IsTrue(inliner.Result.ConvertedSql.Contains("dbo.People"));
        Assert.IsTrue(inliner.Result.ConvertedSql.Contains("dbo.Orders"));
    }

    [Test]
    public void ViewWithMultipleJoins_Inlines()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VProducts"), 
            "CREATE VIEW dbo.VProducts AS SELECT pr.Id, pr.Name, pr.Price FROM dbo.Products pr");

        const string viewSql = @"CREATE VIEW dbo.VTest AS 
            SELECT p.Id, p.FirstName, o.Amount, pr.Name 
            FROM dbo.VPeople p 
            INNER JOIN dbo.VOrders o ON p.Id = o.PersonId
            INNER JOIN dbo.VProducts pr ON o.Id = pr.Id";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.AreEqual(0, inliner.Errors.Count);
        Assert.IsTrue(inliner.Result.ConvertedSql.Contains("dbo.People"));
        Assert.IsTrue(inliner.Result.ConvertedSql.Contains("dbo.Orders"));
        Assert.IsTrue(inliner.Result.ConvertedSql.Contains("dbo.Products"));
    }

    [Test]
    public void ThreeLevelNesting_Inlines()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VLevel2"), 
            "CREATE VIEW dbo.VLevel2 AS SELECT p.Id, p.FirstName FROM dbo.VPeople p");
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VLevel3"), 
            "CREATE VIEW dbo.VLevel3 AS SELECT l2.Id, l2.FirstName FROM dbo.VLevel2 l2");

        const string viewSql = "CREATE VIEW dbo.VLevel4 AS SELECT l3.Id FROM dbo.VLevel3 l3";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.AreEqual(0, inliner.Errors.Count);
        Assert.IsTrue(inliner.Result.ConvertedSql.Contains("dbo.People"));
        Assert.IsFalse(inliner.Result.ConvertedSql.Contains("VLevel2"));
        Assert.IsFalse(inliner.Result.ConvertedSql.Contains("VLevel3"));
    }

    [Test]
    public void ViewWithSubquery_Inlines()
    {
        const string viewSql = @"CREATE VIEW dbo.VTest AS 
            SELECT p.Id, p.FirstName, 
                   (SELECT COUNT(*) FROM dbo.VOrders o WHERE o.PersonId = p.Id) OrderCount
            FROM dbo.VPeople p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.AreEqual(0, inliner.Errors.Count);
        Assert.IsTrue(inliner.Result.ConvertedSql.Contains("dbo.People"));
    }

    [Test]
    public void ViewWithGroupBy_Inlines()
    {
        const string viewSql = @"CREATE VIEW dbo.VTest AS 
            SELECT p.Id, p.FirstName, COUNT(*) OrderCount
            FROM dbo.VPeople p 
            INNER JOIN dbo.VOrders o ON p.Id = o.PersonId
            GROUP BY p.Id, p.FirstName";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.AreEqual(0, inliner.Errors.Count);
        Assert.IsTrue(inliner.Result.ConvertedSql.Contains("dbo.People"));
    }

    [Test]
    public void ViewWithOrderBy_Inlines()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT TOP 100 p.Id, p.FirstName FROM dbo.VPeople p ORDER BY p.FirstName";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.AreEqual(0, inliner.Errors.Count);
        Assert.IsTrue(inliner.Result.ConvertedSql.Contains("dbo.People"));
    }

    [Test]
    public void ViewWithCaseStatement_Inlines()
    {
        const string viewSql = @"CREATE VIEW dbo.VTest AS 
            SELECT p.Id, 
                   CASE WHEN p.IsActive = 1 THEN 'Active' ELSE 'Inactive' END Status
            FROM dbo.VPeople p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.AreEqual(0, inliner.Errors.Count);
        Assert.IsTrue(inliner.Result.ConvertedSql.Contains("dbo.People"));
    }

    [Test]
    public void ViewWithDistinct_Inlines()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT DISTINCT p.LastName FROM dbo.VPeople p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.AreEqual(0, inliner.Errors.Count);
        Assert.IsTrue(inliner.Result.ConvertedSql.Contains("dbo.People"));
        Assert.IsTrue(inliner.Result.ConvertedSql.Contains("DISTINCT"));
    }

    [Test]
    public void ViewWithMultipleWhereClauses_Inlines()
    {
        const string viewSql = @"CREATE VIEW dbo.VTest AS 
            SELECT p.Id, p.FirstName 
            FROM dbo.VPeople p 
            WHERE p.IsActive = 1 AND p.LastName IS NOT NULL";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.AreEqual(0, inliner.Errors.Count);
        Assert.IsTrue(inliner.Result.ConvertedSql.Contains("dbo.People"));
    }
}
