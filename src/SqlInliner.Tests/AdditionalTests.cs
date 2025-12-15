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
}