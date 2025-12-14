using NUnit.Framework;

namespace SqlInliner.Tests;

public class InlinerResultTests
{
    private DatabaseConnection connection;
    private readonly InlinerOptions options = InlinerOptions.Recommended();

    [SetUp]
    public void Setup()
    {
        connection = new();
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"), 
            "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.FirstName, p.LastName FROM dbo.People p");
    }

    [Test]
    public void Result_ContainsOriginalSql()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id FROM dbo.VPeople p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.IsNotNull(inliner.Result);
        Assert.IsTrue(inliner.Result.Sql.Contains(DatabaseView.BeginOriginal));
        Assert.IsTrue(inliner.Result.Sql.Contains(DatabaseView.EndOriginal));
        Assert.IsTrue(inliner.Result.Sql.Contains(viewSql));
    }

    [Test]
    public void Result_ContainsConvertedSql()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id FROM dbo.VPeople p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.IsNotNull(inliner.Result);
        Assert.IsNotNull(inliner.Result.ConvertedSql);
        Assert.IsTrue(inliner.Result.ConvertedSql.Contains("dbo.People"));
    }

    [Test]
    public void Result_ContainsReferencedViews()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id FROM dbo.VPeople p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.IsNotNull(inliner.Result);
        Assert.Greater(inliner.Result.KnownViews.Count, 0);
        Assert.IsTrue(inliner.Result.Sql.Contains("Referenced views"));
    }

    [Test]
    public void Result_ContainsGeneratedTimestamp()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id FROM dbo.VPeople p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.IsNotNull(inliner.Result);
        Assert.IsTrue(inliner.Result.Sql.Contains("Generated on"));
    }

    [Test]
    public void Result_ContainsElapsedTime()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id FROM dbo.VPeople p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.IsNotNull(inliner.Result);
        Assert.Greater(inliner.Result.Elapsed.TotalMilliseconds, 0);
    }

    [Test]
    public void Result_ContainsStrippedCounts()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeopleExtra"), 
            "CREATE VIEW dbo.VPeopleExtra AS SELECT p.Id, p.FirstName, p.LastName, p.Extra1, p.Extra2 FROM dbo.People p");
        
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id FROM dbo.VPeopleExtra p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.IsNotNull(inliner.Result);
        Assert.IsTrue(inliner.Result.Sql.Contains("Removed:"));
        Assert.IsTrue(inliner.Result.Sql.Contains("select columns"));
    }

    [Test]
    public void Result_ContainsWarningsSection()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id FROM dbo.VPeople p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.IsNotNull(inliner.Result);
        Assert.IsTrue(inliner.Result.Sql.Contains("Warnings"));
    }

    [Test]
    public void Result_ContainsErrorsSection()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id FROM dbo.VPeople p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.IsNotNull(inliner.Result);
        Assert.IsTrue(inliner.Result.Sql.Contains("Errors"));
    }

    [Test]
    public void Result_WithNoViews_IsNull()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT 1 AS Col";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.IsNull(inliner.Result);
    }

    [Test]
    public void Result_KnownViewsIncludesMainView()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id FROM dbo.VPeople p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.IsNotNull(inliner.Result);
        Assert.IsTrue(inliner.Result.KnownViews.ContainsKey("[dbo].[VTest]"));
    }

    [Test]
    public void Result_KnownViewsIncludesReferencedViews()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id FROM dbo.VPeople p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        Assert.IsNotNull(inliner.Result);
        Assert.IsTrue(inliner.Result.KnownViews.ContainsKey("[dbo].[VPeople]"));
    }
}
