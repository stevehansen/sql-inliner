using NUnit.Framework;
using Shouldly;

namespace SqlInliner.Tests;

public class DatabaseViewTests
{
    private DatabaseConnection connection;

    [SetUp]
    public void Setup()
    {
        connection = new();
    }

    [Test]
    public void CreateOrAlter_ReplacesCREATEVIEW()
    {
        const string viewSql = "CREATE VIEW dbo.V AS SELECT 1";
        var result = DatabaseView.CreateOrAlter(viewSql);
        result.ShouldStartWith("CREATE OR ALTER VIEW");
    }

    [Test]
    public void CreateOrAlter_WithLowerCase_Replaces()
    {
        const string viewSql = "create view dbo.V as select 1";
        var result = DatabaseView.CreateOrAlter(viewSql);
        result.ShouldStartWith("CREATE OR ALTER VIEW");
    }

    [Test]
    public void CreateOrAlter_WithMixedCase_Replaces()
    {
        const string viewSql = "Create View dbo.V As SELECT 1";
        var result = DatabaseView.CreateOrAlter(viewSql);
        result.ShouldStartWith("CREATE OR ALTER VIEW");
    }

    [Test]
    public void CreateOrAlter_WithExtraSpaces_Replaces()
    {
        const string viewSql = "CREATE    VIEW dbo.V AS SELECT 1";
        var result = DatabaseView.CreateOrAlter(viewSql);
        result.ShouldStartWith("CREATE OR ALTER VIEW");
    }

    [Test]
    public void CreateOrAlter_WithNewline_Replaces()
    {
        const string viewSql = "CREATE\nVIEW dbo.V AS SELECT 1";
        var result = DatabaseView.CreateOrAlter(viewSql);
        result.ShouldStartWith("CREATE OR ALTER VIEW");
    }

    [Test]
    public void CreateOrAlter_AlreadyCreateOrAlter_Unchanged()
    {
        const string viewSql = "CREATE OR ALTER VIEW dbo.V AS SELECT 1";
        var result = DatabaseView.CreateOrAlter(viewSql);
        result.ShouldBe(viewSql);
    }

    [Test]
    public void FromSql_ValidView_ReturnsViewWithNoErrors()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT 1 AS Col";
        var (view, errors) = DatabaseView.FromSql(connection, viewSql);
        view.ShouldNotBeNull();
        errors.Count.ShouldBe(0);
        view.ViewName.ShouldBe("[dbo].[VTest]");
    }

    [Test]
    public void FromSql_CreateOrAlterView_ReturnsViewWithNoErrors()
    {
        const string viewSql = "CREATE OR ALTER VIEW dbo.VTest AS SELECT 1 AS Col";
        var (view, errors) = DatabaseView.FromSql(connection, viewSql);
        view.ShouldNotBeNull();
        errors.Count.ShouldBe(0);
        view.ViewName.ShouldBe("[dbo].[VTest]");
    }

    [Test]
    public void FromSql_InvalidSyntax_ReturnsNullWithErrors()
    {
        const string viewSql = "CREATE OR VIEW dbo.VTest AS SELECT 1";
        var (view, errors) = DatabaseView.FromSql(connection, viewSql);
        view.ShouldBeNull();
        errors.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public void FromSql_ViewWithMultipleColumns_ParsesCorrectly()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT 1 AS Col1, 2 AS Col2, 3 AS Col3";
        var (view, errors) = DatabaseView.FromSql(connection, viewSql);
        view.ShouldNotBeNull();
        errors.Count.ShouldBe(0);
    }

    [Test]
    public void FromSql_ViewWithWhere_ParsesCorrectly()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT Id FROM dbo.MyTable WHERE IsActive = 1";
        var (view, errors) = DatabaseView.FromSql(connection, viewSql);
        view.ShouldNotBeNull();
        errors.Count.ShouldBe(0);
    }

    [Test]
    public void FromSql_ViewWithJoin_ParsesCorrectly()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT t1.Id FROM dbo.Table1 t1 INNER JOIN dbo.Table2 t2 ON t1.Id = t2.Id";
        var (view, errors) = DatabaseView.FromSql(connection, viewSql);
        view.ShouldNotBeNull();
        errors.Count.ShouldBe(0);
    }
}