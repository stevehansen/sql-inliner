using NUnit.Framework;
using Shouldly;

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

        view.ShouldBeNull();
        errors.Count.ShouldNotBe(0);
    }

    [Test]
    public void InvalidSqlInliner()
    {
        const string viewSql = "CREATE OR VIEW dbo.X AS SELECT 0";

        var inliner = new DatabaseViewInliner(connection, viewSql);
        inliner.View.ShouldBeNull();
        inliner.Sql.ShouldNotBe(viewSql);
    }

    [Test]
    public void ReturnOriginalIfNoViewsAreReferenced()
    {
        const string viewSql = "CREATE VIEW dbo.X AS SELECT 0";

        var inliner = new DatabaseViewInliner(connection, viewSql);
        inliner.Errors.Count.ShouldBe(0);
        inliner.Sql.ShouldBe(viewSql);
        inliner.View.ShouldNotBeNull();
    }

    [Test]
    public void EmptyConnection()
    {
        connection.Views.Count.ShouldBe(0);
        connection.Connection.ShouldBeNull();
    }
}