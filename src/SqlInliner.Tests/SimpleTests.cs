using System.Linq;
using NUnit.Framework;
using Shouldly;

namespace SqlInliner.Tests;

public class SimpleTests
{
    private DatabaseConnection connection;

    private readonly InlinerOptions options = InlinerOptions.Recommended();

    [SetUp]
    public void Setup()
    {
        connection = new();
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"), "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.FirstName, p.LastName, p.IsActive FROM dbo.People p");
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeopleWithAliases"), "CREATE VIEW dbo.VPeopleWithAliases AS SELECT p.Id, p.FirstName FName, p.LastName LName, p.IsActive ActiveInd, unused_function(p.Id) UnusedFunction FROM dbo.People p INNER JOIN dbo.UnusedTable ON dbo.UnusedTable.Id = p.Id");
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VNestedPeople"), "CREATE VIEW dbo.VNestedPeople AS SELECT p.Id, p.FirstName, p.LastName, p.IsActive FROM dbo.VPeople p INNER JOIN dbo.VPeople p2 on p2.Id = p.Id");
    }

    [Test]
    public void InlineSimpleView()
    {
        const string viewSql = "CREATE VIEW dbo.VActivePeople AS SELECT p.Id, p.FirstName, p.LastName FROM dbo.VPeople p WHERE p.IsActive = 1";

        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.Count.ShouldBe(0);
        inliner.Sql.ShouldNotBe(viewSql);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        result.Sql.ShouldContain("dbo.People");
        result.ConvertedSql.ShouldContain("dbo.People");
        result.ConvertedSql.ShouldNotContain("dbo.VPeople");
        result.ConvertedSql.ShouldNotContain("UnusedFunction");
    }

    [Test]
    public void InlineSimpleViewWithAliases()
    {
        const string viewSql = "CREATE OR ALTER VIEW dbo.VActivePeople AS SELECT p.Id, p.FName, p.LName FROM dbo.VPeopleWithAliases p WHERE p.ActiveInd = 1";

        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.Count.ShouldBe(0);
        inliner.Sql.ShouldNotBe(viewSql);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        result.Sql.ShouldContain("dbo.People");
        result.ConvertedSql.ShouldContain("dbo.People");
        result.ConvertedSql.ShouldNotContain("dbo.VPeopleWithAliases");
        result.ConvertedSql.ShouldNotContain("UnusedFunction");
        result.ConvertedSql.ShouldNotContain("dbo.UnusedTable");
    }

    [Test]
    public void InlineSimpleViewWithColumnAliases()
    {
        const string viewSql = "CREATE VIEW dbo.VActivePeople AS SELECT p.Id, p.FirstName FName, p.LastName LName FROM dbo.VPeople p WHERE p.IsActive = 1";

        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.Count.ShouldBe(0);
        inliner.Sql.ShouldNotBe(viewSql);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        result.Sql.ShouldContain("dbo.People");
        result.ConvertedSql.ShouldContain("dbo.People");
        result.ConvertedSql.ShouldNotContain("dbo.VPeople");
        result.ConvertedSql.ShouldNotContain("UnusedFunction");
        result.ConvertedSql.ShouldContain("p.FirstName AS FName");
    }

    [Test]
    public void InlineSimpleViewWithAliasesWithColumnAliases()
    {
        const string viewSql = "CREATE VIEW dbo.VActivePeople AS SELECT ap.Id, ap.FName PersonFirstName, ap.LName PersonLastName FROM dbo.VPeopleWithAliases ap WHERE ap.ActiveInd = 1";

        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.Count.ShouldBe(0);
        inliner.Sql.ShouldNotBe(viewSql);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        result.Sql.ShouldContain("dbo.People");
        result.ConvertedSql.ShouldContain("dbo.People");
        result.ConvertedSql.ShouldNotContain("dbo.VPeopleWithAliases");
        result.ConvertedSql.ShouldNotContain("UnusedFunction");
        result.ConvertedSql.ShouldContain("ap.FName AS PersonFirstName");
    }

    [Test]
    public void InlineSimpleViewWithRemovedColumns()
    {
        const string viewSql = "CREATE VIEW dbo.VActivePeople AS SELECT p.Id, p.FirstName FROM dbo.VPeople p WHERE p.IsActive = 1";

        var inliner = new DatabaseViewInliner(connection, DatabaseView.CreateOrAlter(viewSql), options);
        inliner.Errors.Count.ShouldBe(0);
        inliner.Sql.ShouldNotBe(viewSql);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        result.Sql.ShouldContain("dbo.People");
        result.ConvertedSql.ShouldContain("dbo.People");
        result.ConvertedSql.ShouldNotContain("dbo.VPeople");
        result.ConvertedSql.ShouldNotContain("UnusedFunction");
        result.ConvertedSql.ShouldNotContain("LastName");
    }

    [Test]
    public void InlineNestedView()
    {
        const string viewSql = "CREATE VIEW dbo.VActivePeople AS SELECT p.Id, p.FirstName, p.LastName FROM dbo.VNestedPeople p WHERE p.IsActive = 1";

        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.Count.ShouldBe(0);
        inliner.Sql.ShouldNotBe(viewSql);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        result.Sql.ShouldContain("dbo.People");
        result.ConvertedSql.ShouldContain("dbo.People");
        result.ConvertedSql.ShouldNotContain("dbo.VNestedPeople");
        result.ConvertedSql.ShouldNotContain("UnusedFunction");
        result.ConvertedSql.ShouldNotContain("p2");
    }

    [Test]
    public void InlineNestedViewKeepUnusedJoins()
    {
        const string viewSql = "CREATE OR ALTER VIEW dbo.VActivePeople AS SELECT p.Id, p.FirstName, p.LastName FROM dbo.VNestedPeople p WHERE p.IsActive = 1";

        var inliner = new DatabaseViewInliner(connection, viewSql);
        inliner.Errors.Count.ShouldBe(0);
        inliner.Sql.ShouldNotBe(viewSql);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        result.Sql.ShouldContain("dbo.People");
        result.ConvertedSql.ShouldContain("dbo.People");
        result.ConvertedSql.ShouldNotContain("dbo.VNestedPeople");
        result.ConvertedSql.ShouldNotContain("UnusedFunction");
        result.ConvertedSql.ShouldContain("p2");
    }

    [Test]
    public void WarningForSinglePartIdentifiers()
    {
        const string viewSql = "CREATE OR ALTER VIEW dbo.VActivePeople AS SELECT Id, FirstName, LastName FROM dbo.VNestedPeople";

        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.Count.ShouldBe(0);
        inliner.Warnings.Count.ShouldNotBe(0);
        inliner.Sql.ShouldNotBe(viewSql);
    }

    [Test]
    public void CountColumnReferences()
    {
        const string viewSql = "CREATE VIEW dbo.VActivePeople AS SELECT p.Id, p.FirstName, p.LastName, 'hardcoded' Ignored FROM dbo.VPeople p WHERE p.IsActive = 1";

        var (view, errors) = DatabaseView.FromSql(connection, viewSql);
        errors.Count.ShouldBe(0);
        view.ShouldNotBeNull();

        view.References.ColumnReferences.Count.ShouldBe(4);
    }

    [Test]
    public void StripJoinWithMultipleConditionsWhenUnused()
    {
        // A join with multiple ON conditions (e.g. AND b.Type = 'B') where the joined table
        // is only referenced in its own ON clause should still be stripped.
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VFilteredJoin"),
            "CREATE VIEW dbo.VFilteredJoin AS SELECT a.Id, a.Name FROM dbo.A a INNER JOIN dbo.B b ON a.BId = b.Id AND b.Type = 'B'");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT fj.Id, fj.Name FROM dbo.VFilteredJoin fj";

        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        result.ConvertedSql.ShouldNotContain("dbo.B");
        result.ConvertedSql.ShouldContain("dbo.A");
    }

    [Test]
    public void KeepJoinWithMultipleConditionsWhenUsedInSelect()
    {
        // When the joined table is also referenced in the SELECT, it should NOT be stripped.
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VFilteredJoinUsed"),
            "CREATE VIEW dbo.VFilteredJoinUsed AS SELECT a.Id, b.Name FROM dbo.A a INNER JOIN dbo.B b ON a.BId = b.Id AND b.Type = 'B'");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT fj.Id, fj.Name FROM dbo.VFilteredJoinUsed fj";

        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        result.ConvertedSql.ShouldContain("dbo.B");
        result.ConvertedSql.ShouldContain("dbo.A");
    }

    [Test]
    public void KeepJoinWithMultipleConditionsWhenUsedInFollowingJoin()
    {
        // When the joined table is referenced in a following join's ON clause, it should NOT be stripped.
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VFilteredJoinChained"),
            "CREATE VIEW dbo.VFilteredJoinChained AS SELECT a.Id, c.Value FROM dbo.A a INNER JOIN dbo.B b ON a.BId = b.Id AND b.Type = 'B' INNER JOIN dbo.C c ON c.BId = b.Id");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT fj.Id, fj.Value FROM dbo.VFilteredJoinChained fj";

        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        result.ConvertedSql.ShouldContain("dbo.B");
        result.ConvertedSql.ShouldContain("dbo.A");
        result.ConvertedSql.ShouldContain("dbo.C");
    }

    [Test]
    public void CountColumnReferencesSkipParametersToIgnore()
    {
        const string viewSql = "CREATE VIEW dbo.VActivePeople AS SELECT CONVERT(varchar, p.Id) Id, dateadd(day, 1, p.DayOfBirth) DayOfBirth, CAST(10.5 AS INT) Number FROM dbo.VPeople p";

        var (view, errors) = DatabaseView.FromSql(connection, viewSql);
        errors.Count.ShouldBe(0);
        view.ShouldNotBeNull();

        view.References.ColumnReferences.Count(r => r.MultiPartIdentifier[0].Value == "p").ShouldBe(2);
        view.References.ColumnReferences.Count.ShouldBe(2);
    }
}