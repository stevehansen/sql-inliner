using NUnit.Framework;
using Shouldly;

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
        inliner.Result.ShouldNotBeNull();
        inliner.Result.Sql.ShouldContain(DatabaseView.BeginOriginal);
        inliner.Result.Sql.ShouldContain(DatabaseView.EndOriginal);
        inliner.Result.Sql.ShouldContain(viewSql);
    }

    [Test]
    public void Result_ContainsConvertedSql()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id FROM dbo.VPeople p";
        
        var options = InlinerOptions.Recommended();
        options.StripUnusedJoins = false;
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Result.ShouldNotBeNull();
        inliner.Result.ConvertedSql.ShouldNotBeNull();
        inliner.Result.ConvertedSql.ShouldContain("dbo.People");
    }

    [Test]
    public void Result_ContainsReferencedViews()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id FROM dbo.VPeople p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Result.ShouldNotBeNull();
        inliner.Result.KnownViews.Count.ShouldBeGreaterThan(0);
        inliner.Result.Sql.ShouldContain("Referenced views");
    }

    [Test]
    public void Result_ContainsGeneratedTimestamp()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id FROM dbo.VPeople p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Result.ShouldNotBeNull();
        inliner.Result.Sql.ShouldContain("Generated on");
    }

    [Test]
    public void Result_ContainsElapsedTime()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id FROM dbo.VPeople p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Result.ShouldNotBeNull();
        inliner.Result.Elapsed.TotalMilliseconds.ShouldBeGreaterThan(0);
    }

    [Test]
    public void Result_ContainsStrippedCounts()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeopleExtra"), 
            "CREATE VIEW dbo.VPeopleExtra AS SELECT p.Id, p.FirstName, p.LastName, p.Extra1, p.Extra2 FROM dbo.People p");
        
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id FROM dbo.VPeopleExtra p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Result.ShouldNotBeNull();
        inliner.Result.Sql.ShouldContain("Removed:");
        inliner.Result.Sql.ShouldContain("select columns");
    }

    [Test]
    public void Result_ContainsWarningsSection()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id FROM dbo.VPeople p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Result.ShouldNotBeNull();
        inliner.Result.Sql.ShouldContain("Warnings");
    }

    [Test]
    public void Result_ContainsErrorsSection()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id FROM dbo.VPeople p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Result.ShouldNotBeNull();
        inliner.Result.Sql.ShouldContain("Errors");
    }

    [Test]
    public void Result_WithNoViews_IsNull()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT 1 AS Col";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Result.ShouldBeNull();
    }

    [Test]
    public void Result_KnownViewsIncludesMainView()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id FROM dbo.VPeople p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Result.ShouldNotBeNull();
        inliner.Result.KnownViews.ShouldContainKey("[dbo].[VTest]");
    }

    [Test]
    public void Result_KnownViewsIncludesReferencedViews()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id FROM dbo.VPeople p";
        
        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Result.ShouldNotBeNull();
        inliner.Result.KnownViews.ShouldContainKey("[dbo].[VPeople]");
    }
}