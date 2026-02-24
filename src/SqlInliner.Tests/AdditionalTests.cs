using NUnit.Framework;
using Shouldly;

namespace SqlInliner.Tests;

public class AdditionalTests
{
    [Test]
    public void CreateOrAlterReplacesCreateView()
    {
        const string viewSql = "create view dbo.V as select 1";
        var result = DatabaseView.CreateOrAlter(viewSql);
        result.ShouldStartWith("CREATE OR ALTER VIEW");
    }

    [Test]
    public void AddViewDefinitionUpdatesConnection()
    {
        var connection = new DatabaseConnection();
        var viewName = DatabaseConnection.ToObjectName("dbo", "VTest");
        const string definition = "CREATE VIEW dbo.VTest AS SELECT 1";
        connection.AddViewDefinition(viewName, definition);

        connection.IsView(viewName).ShouldBeTrue();
        connection.GetViewDefinition(viewName.GetName()).ShouldBe(definition);
    }

    [Test]
    public void RecommendedOptionsEnableStripUnusedJoins()
    {
        var options = InlinerOptions.Recommended();
        options.StripUnusedColumns.ShouldBeTrue();
        options.StripUnusedJoins.ShouldBeTrue();
    }

    [Test]
    public void ViewWithoutAliasGetsDefaultAlias()
    {
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"), "CREATE VIEW dbo.VPeople AS SELECT Id FROM dbo.People");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT Id FROM dbo.VPeople";
        var (view, errors) = DatabaseView.FromSql(connection, viewSql);
        errors.Count.ShouldBe(0);
        view.ShouldNotBeNull();
        var referenced = view!.References.Views[0];
        referenced.Alias.ShouldNotBeNull();
        referenced.Alias!.Value.ShouldBe("VPeople");
    }

    [Test]
    public void SameViewInMultipleSubqueriesGetsImplicitAlias()
    {
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VStatus"),
            "CREATE VIEW dbo.VStatus AS SELECT Id, Code FROM dbo.Statuses");

        // VStatus is referenced twice in separate subqueries — both without explicit alias
        const string viewSql = @"
            CREATE VIEW dbo.VTest AS
            SELECT
                (SELECT TOP 1 VStatus.Code FROM dbo.VStatus WHERE VStatus.Id = t.StatusA) AS StatusA,
                (SELECT TOP 1 VStatus.Code FROM dbo.VStatus WHERE VStatus.Id = t.StatusB) AS StatusB
            FROM dbo.Items t";

        var (view, errors) = DatabaseView.FromSql(connection, viewSql);
        errors.Count.ShouldBe(0);
        view.ShouldNotBeNull();

        // Both references should get an implicit alias
        foreach (var v in view!.References.Views)
            v.Alias.ShouldNotBeNull();

        // Should inline without errors
        var inliner = new DatabaseViewInliner(connection, viewSql, InlinerOptions.Recommended());
        inliner.Errors.ShouldBeEmpty();
    }

    [Test]
    public void StripUnusedJoin_TableWithExplicitAlias()
    {
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VItems"),
            "CREATE VIEW dbo.VItems AS SELECT a.Id, a.Name FROM dbo.A a INNER JOIN dbo.B b ON a.BId = b.Id");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VItems v";

        var inliner = new DatabaseViewInliner(connection, viewSql, InlinerOptions.Recommended());
        inliner.Errors.ShouldBeEmpty();
        inliner.Result.ShouldNotBeNull();
        // B is unused (only referenced in its own join condition) and should be stripped
        inliner.Result!.ConvertedSql.ShouldNotContain("dbo.B");
        inliner.Result.ConvertedSql.ShouldContain("dbo.A");
    }

    [Test]
    public void StripUnusedJoin_TableWithImplicitAlias()
    {
        var connection = new DatabaseConnection();
        // View uses dbo.B without explicit alias — implicit alias "B" should still allow stripping
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VItems"),
            "CREATE VIEW dbo.VItems AS SELECT A.Id, A.Name FROM dbo.A INNER JOIN dbo.B ON A.BId = B.Id");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VItems v";

        var inliner = new DatabaseViewInliner(connection, viewSql, InlinerOptions.Recommended());
        inliner.Errors.ShouldBeEmpty();
        inliner.Result.ShouldNotBeNull();
        inliner.Result!.ConvertedSql.ShouldNotContain("dbo.B");
        inliner.Result.ConvertedSql.ShouldContain("dbo.A");
    }

    [Test]
    public void StripUnusedJoin_SameTableInMainAndSubquery_ExplicitAlias()
    {
        var connection = new DatabaseConnection();
        // Inner view: main query joins B (unused in SELECT), subquery also references B
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VItems"),
            @"CREATE VIEW dbo.VItems AS
              SELECT a.Id, a.Name,
                     (SELECT TOP 1 b2.Code FROM dbo.B b2 WHERE b2.Id = a.BId) AS BCode
              FROM dbo.A a INNER JOIN dbo.B b ON a.BId = b.Id");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.BCode FROM dbo.VItems v";

        var inliner = new DatabaseViewInliner(connection, viewSql, InlinerOptions.Recommended());
        inliner.Errors.ShouldBeEmpty();
        inliner.Result.ShouldNotBeNull();
        // The main-query B join is unused in SELECT, but the subquery's B keeps a reference
        // alive via the flat column list. Conservative behavior: B may or may not be stripped,
        // but the inliner must not error out.
        inliner.Result!.ConvertedSql.ShouldContain("dbo.A");
    }

    [Test]
    public void StripUnusedJoin_SameTableInMainAndSubquery_ImplicitAlias()
    {
        var connection = new DatabaseConnection();
        // Same scenario but without explicit aliases on B
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VItems"),
            @"CREATE VIEW dbo.VItems AS
              SELECT A.Id, A.Name,
                     (SELECT TOP 1 B.Code FROM dbo.B WHERE B.Id = A.BId) AS BCode
              FROM dbo.A INNER JOIN dbo.B ON A.BId = B.Id");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.BCode FROM dbo.VItems v";

        var inliner = new DatabaseViewInliner(connection, viewSql, InlinerOptions.Recommended());
        inliner.Errors.ShouldBeEmpty();
        inliner.Result.ShouldNotBeNull();
        inliner.Result!.ConvertedSql.ShouldContain("dbo.A");
    }
}