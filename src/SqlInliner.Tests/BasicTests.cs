using NUnit.Framework;

namespace SqlInliner.Tests;

public class BasicTests
{
    private DatabaseConnection connection;

    [SetUp]
    public void Setup()
    {
        connection = new();
    }

    [Test]
    public void InvalidSql()
    {
        const string viewSql = "CREATE OR VIEW dbo.X AS SELECT 0";

        var (view, errors) = DatabaseView.FromSql(connection, viewSql);

        Assert.IsNull(view);
        Assert.AreNotEqual(0, errors.Count);
    }

    [Test]
    public void InvalidSqlInliner()
    {
        const string viewSql = "CREATE OR VIEW dbo.X AS SELECT 0";

        var inliner = new DatabaseViewInliner(connection, viewSql);
        Assert.IsNull(inliner.View);
        Assert.AreNotEqual(viewSql, inliner.Sql);
    }

    [Test]
    public void ReturnOriginalIfNoViewsAreReferenced()
    {
        const string viewSql = "CREATE VIEW dbo.X AS SELECT 0";

        var inliner = new DatabaseViewInliner(connection, viewSql);
        Assert.AreEqual(0, inliner.Errors.Count);
        Assert.AreEqual(viewSql, inliner.Sql);
        Assert.IsNotNull(inliner.View);
    }

    [Test]
    public void EmptyConnection()
    {
        Assert.AreEqual(0, connection.Views.Count);
        Assert.IsNull(connection.Connection);
    }
}