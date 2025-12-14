using NUnit.Framework;

namespace SqlInliner.Tests;

public class DatabaseConnectionTests
{
    [Test]
    public void EmptyConnection_HasNoViews()
    {
        var connection = new DatabaseConnection();
        Assert.AreEqual(0, connection.Views.Count);
    }

    [Test]
    public void EmptyConnection_HasNullConnection()
    {
        var connection = new DatabaseConnection();
        Assert.IsNull(connection.Connection);
    }

    [Test]
    public void AddViewDefinition_AddsToViews()
    {
        var connection = new DatabaseConnection();
        var viewName = DatabaseConnection.ToObjectName("dbo", "VTest");
        const string definition = "CREATE VIEW dbo.VTest AS SELECT 1";
        
        connection.AddViewDefinition(viewName, definition);
        
        Assert.AreEqual(1, connection.Views.Count);
    }

    [Test]
    public void AddViewDefinition_CanRetrieveDefinition()
    {
        var connection = new DatabaseConnection();
        var viewName = DatabaseConnection.ToObjectName("dbo", "VTest");
        const string definition = "CREATE VIEW dbo.VTest AS SELECT 1";
        
        connection.AddViewDefinition(viewName, definition);
        var retrieved = connection.GetViewDefinition(viewName.GetName());
        
        Assert.AreEqual(definition, retrieved);
    }

    [Test]
    public void AddViewDefinition_MultipleViews_AllAccessible()
    {
        var connection = new DatabaseConnection();
        var view1 = DatabaseConnection.ToObjectName("dbo", "VTest1");
        var view2 = DatabaseConnection.ToObjectName("dbo", "VTest2");
        const string def1 = "CREATE VIEW dbo.VTest1 AS SELECT 1";
        const string def2 = "CREATE VIEW dbo.VTest2 AS SELECT 2";
        
        connection.AddViewDefinition(view1, def1);
        connection.AddViewDefinition(view2, def2);
        
        Assert.AreEqual(2, connection.Views.Count);
        Assert.AreEqual(def1, connection.GetViewDefinition(view1.GetName()));
        Assert.AreEqual(def2, connection.GetViewDefinition(view2.GetName()));
    }

    [Test]
    public void IsView_ViewExists_ReturnsTrue()
    {
        var connection = new DatabaseConnection();
        var viewName = DatabaseConnection.ToObjectName("dbo", "VTest");
        connection.AddViewDefinition(viewName, "CREATE VIEW dbo.VTest AS SELECT 1");
        
        Assert.IsTrue(connection.IsView(viewName));
    }

    [Test]
    public void IsView_ViewDoesNotExist_ReturnsFalse()
    {
        var connection = new DatabaseConnection();
        var viewName = DatabaseConnection.ToObjectName("dbo", "VTest");
        
        Assert.IsFalse(connection.IsView(viewName));
    }

    [Test]
    public void IsView_DifferentSchema_ReturnsFalse()
    {
        var connection = new DatabaseConnection();
        var viewName1 = DatabaseConnection.ToObjectName("dbo", "VTest");
        var viewName2 = DatabaseConnection.ToObjectName("other", "VTest");
        connection.AddViewDefinition(viewName1, "CREATE VIEW dbo.VTest AS SELECT 1");
        
        Assert.IsFalse(connection.IsView(viewName2));
    }

    [Test]
    public void AddViewDefinition_UpdatesExistingView()
    {
        var connection = new DatabaseConnection();
        var viewName = DatabaseConnection.ToObjectName("dbo", "VTest");
        const string def1 = "CREATE VIEW dbo.VTest AS SELECT 1";
        const string def2 = "CREATE VIEW dbo.VTest AS SELECT 2";
        
        connection.AddViewDefinition(viewName, def1);
        connection.AddViewDefinition(viewName, def2);
        
        Assert.AreEqual(def2, connection.GetViewDefinition(viewName.GetName()));
    }

    [Test]
    public void ToObjectName_CreatesValidSchemaObjectName()
    {
        var objectName = DatabaseConnection.ToObjectName("myschema", "myview");
        
        Assert.IsNotNull(objectName);
        Assert.AreEqual("myschema", objectName.SchemaIdentifier.Value);
        Assert.AreEqual("myview", objectName.BaseIdentifier.Value);
    }
}
