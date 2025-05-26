using NUnit.Framework;

namespace SqlInliner.Tests;

public class AdditionalTests
{
    [Test]
    public void CreateOrAlterReplacesCreateView()
    {
        const string viewSql = "create view dbo.V as select 1";
        var result = DatabaseView.CreateOrAlter(viewSql);
        StringAssert.StartsWith("CREATE OR ALTER VIEW", result);
    }

    [Test]
    public void AddViewDefinitionUpdatesConnection()
    {
        var connection = new DatabaseConnection();
        var viewName = DatabaseConnection.ToObjectName("dbo", "VTest");
        const string definition = "CREATE VIEW dbo.VTest AS SELECT 1";
        connection.AddViewDefinition(viewName, definition);

        Assert.IsTrue(connection.IsView(viewName));
        Assert.AreEqual(definition, connection.GetViewDefinition(viewName.GetName()));
    }

    [Test]
    public void RecommendedOptionsEnableStripUnusedJoins()
    {
        var options = InlinerOptions.Recommended();
        Assert.IsTrue(options.StripUnusedColumns);
        Assert.IsTrue(options.StripUnusedJoins);
    }

    [Test]
    public void ViewWithoutAliasGetsDefaultAlias()
    {
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"), "CREATE VIEW dbo.VPeople AS SELECT Id FROM dbo.People");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT Id FROM dbo.VPeople";
        var (view, errors) = DatabaseView.FromSql(connection, viewSql);
        Assert.AreEqual(0, errors.Count);
        Assert.IsNotNull(view);
        var referenced = view!.References.Views[0];
        Assert.IsNotNull(referenced.Alias);
        Assert.AreEqual("VPeople", referenced.Alias!.Value);
    }
}
