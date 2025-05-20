using NUnit.Framework;

namespace SqlInliner.Tests;

public class ComplexTests
{
    private DatabaseConnection connection;
    private readonly InlinerOptions options = InlinerOptions.Recommended();

    [SetUp]
    public void Setup()
    {
        connection = new();
        // Add any common view definitions needed for complex tests here
        connection.AddViewDefinition("dbo.BaseTable", "CREATE VIEW dbo.BaseTable AS SELECT Id, Name, Value FROM dbo.ActualTable");
        connection.AddViewDefinition("dbo.Level1View", "CREATE VIEW dbo.Level1View AS SELECT Id, Name, Value FROM dbo.BaseTable WHERE Value > 10");
        connection.AddViewDefinition("dbo.Level2View", "CREATE VIEW dbo.Level2View AS SELECT Id, Name FROM dbo.Level1View WHERE Name LIKE 'A%'");
        connection.AddViewDefinition("dbo.Level3View", "CREATE VIEW dbo.Level3View AS SELECT Id FROM dbo.Level2View");

        // Definitions for JOIN tests
        connection.AddViewDefinition("dbo.TableA", "CREATE VIEW dbo.TableA AS SELECT Id TA_Id, Name TA_Name, Value TA_Value FROM dbo.ActualTableA");
        connection.AddViewDefinition("dbo.TableB", "CREATE VIEW dbo.TableB AS SELECT Id TB_Id, TA_Id TB_TA_Id, Description TB_Description FROM dbo.ActualTableB");
    }

    // Add test methods here
    [Test]
    public void InlineMultiLevelNestedView()
    {
        const string viewSql = "CREATE VIEW dbo.TestView AS SELECT Id FROM dbo.Level3View WHERE Id < 100";
        var inliner = new Inliner(viewSql, "dbo.TestView", connection, options);
        var result = inliner.Inline();

        Assert.That(inliner.Errors.Count, Is.EqualTo(0));
        Assert.That(inliner.Sql, Is.Not.EqualTo(viewSql)); // Original SQL should be modified

        Assert.That(result.Sql, Does.Contain("dbo.ActualTable").IgnoreCase);
        Assert.That(result.ConvertedSql, Does.Contain("dbo.ActualTable").IgnoreCase);

        Assert.That(result.ConvertedSql, Does.Not.Contain("dbo.BaseTable").IgnoreCase);
        Assert.That(result.ConvertedSql, Does.Not.Contain("dbo.Level1View").IgnoreCase);
        Assert.That(result.ConvertedSql, Does.Not.Contain("dbo.Level2View").IgnoreCase);
        Assert.That(result.ConvertedSql, Does.Not.Contain("dbo.Level3View").IgnoreCase);
        
        // Assert that the final inlined SQL correctly reflects the conditions from all views
        Assert.That(result.ConvertedSql, Does.Match(@"Value\s*>\s*10").IgnoreCase);
        Assert.That(result.ConvertedSql, Does.Match(@"Name\s*LIKE\s*N?'A%'").IgnoreCase);
        Assert.That(result.ConvertedSql, Does.Match(@"Id\s*<\s*100").IgnoreCase);
    }

    [Test]
    public void InlineViewWithInnerJoin()
    {
        const string viewSql = "CREATE VIEW dbo.TestInnerJoin AS SELECT a.TA_Name, b.TB_Description FROM dbo.TableA a INNER JOIN dbo.TableB b ON a.TA_Id = b.TB_TA_Id";
        var inliner = new Inliner(viewSql, "dbo.TestInnerJoin", connection, options);
        var result = inliner.Inline();

        Assert.That(inliner.Errors.Count, Is.EqualTo(0));
        Assert.That(inliner.Sql, Is.Not.EqualTo(viewSql));

        Assert.That(result.Sql, Does.Contain("dbo.ActualTableA").IgnoreCase);
        Assert.That(result.Sql, Does.Contain("dbo.ActualTableB").IgnoreCase);
        Assert.That(result.ConvertedSql, Does.Contain("dbo.ActualTableA").IgnoreCase);
        Assert.That(result.ConvertedSql, Does.Contain("dbo.ActualTableB").IgnoreCase);

        Assert.That(result.ConvertedSql, Does.Not.Contain("dbo.TableA").IgnoreCase);
        Assert.That(result.ConvertedSql, Does.Not.Contain("dbo.TableB").IgnoreCase);

        Assert.That(result.ConvertedSql, Does.Contain("INNER JOIN").IgnoreCase);
    }

    [Test]
    public void InlineViewWithLeftJoin()
    {
        const string viewSql = "CREATE VIEW dbo.TestLeftJoin AS SELECT a.TA_Name, b.TB_Description FROM dbo.TableA a LEFT JOIN dbo.TableB b ON a.TA_Id = b.TB_TA_Id";
        var inliner = new Inliner(viewSql, "dbo.TestLeftJoin", connection, options);
        var result = inliner.Inline();

        Assert.That(inliner.Errors.Count, Is.EqualTo(0));
        Assert.That(inliner.Sql, Is.Not.EqualTo(viewSql));

        Assert.That(result.Sql, Does.Contain("dbo.ActualTableA").IgnoreCase);
        Assert.That(result.Sql, Does.Contain("dbo.ActualTableB").IgnoreCase);
        Assert.That(result.ConvertedSql, Does.Contain("dbo.ActualTableA").IgnoreCase);
        Assert.That(result.ConvertedSql, Does.Contain("dbo.ActualTableB").IgnoreCase);

        Assert.That(result.ConvertedSql, Does.Not.Contain("dbo.TableA").IgnoreCase);
        Assert.That(result.ConvertedSql, Does.Not.Contain("dbo.TableB").IgnoreCase);

        Assert.That(result.ConvertedSql, Does.Contain("LEFT JOIN").IgnoreCase);
    }

    [Test]
    public void InlineViewWithRightJoin()
    {
        const string viewSql = "CREATE VIEW dbo.TestRightJoin AS SELECT a.TA_Name, b.TB_Description FROM dbo.TableA a RIGHT JOIN dbo.TableB b ON a.TA_Id = b.TB_TA_Id";
        var inliner = new Inliner(viewSql, "dbo.TestRightJoin", connection, options);
        var result = inliner.Inline();

        Assert.That(inliner.Errors.Count, Is.EqualTo(0));
        Assert.That(inliner.Sql, Is.Not.EqualTo(viewSql));

        Assert.That(result.Sql, Does.Contain("dbo.ActualTableA").IgnoreCase);
        Assert.That(result.Sql, Does.Contain("dbo.ActualTableB").IgnoreCase);
        Assert.That(result.ConvertedSql, Does.Contain("dbo.ActualTableA").IgnoreCase);
        Assert.That(result.ConvertedSql, Does.Contain("dbo.ActualTableB").IgnoreCase);

        Assert.That(result.ConvertedSql, Does.Not.Contain("dbo.TableA").IgnoreCase);
        Assert.That(result.ConvertedSql, Does.Not.Contain("dbo.TableB").IgnoreCase);

        Assert.That(result.ConvertedSql, Does.Contain("RIGHT JOIN").IgnoreCase);
    }

    [Test]
    public void InlineViewWithFullOuterJoin()
    {
        const string viewSql = "CREATE VIEW dbo.TestFullOuterJoin AS SELECT a.TA_Name, b.TB_Description FROM dbo.TableA a FULL OUTER JOIN dbo.TableB b ON a.TA_Id = b.TB_TA_Id";
        var inliner = new Inliner(viewSql, "dbo.TestFullOuterJoin", connection, options);
        var result = inliner.Inline();

        Assert.That(inliner.Errors.Count, Is.EqualTo(0));
        Assert.That(inliner.Sql, Is.Not.EqualTo(viewSql));

        Assert.That(result.Sql, Does.Contain("dbo.ActualTableA").IgnoreCase);
        Assert.That(result.Sql, Does.Contain("dbo.ActualTableB").IgnoreCase);
        Assert.That(result.ConvertedSql, Does.Contain("dbo.ActualTableA").IgnoreCase);
        Assert.That(result.ConvertedSql, Does.Contain("dbo.ActualTableB").IgnoreCase);

        Assert.That(result.ConvertedSql, Does.Not.Contain("dbo.TableA").IgnoreCase);
        Assert.That(result.ConvertedSql, Does.Not.Contain("dbo.TableB").IgnoreCase);

        Assert.That(result.ConvertedSql, Does.Contain("FULL OUTER JOIN").IgnoreCase);
    }
}
