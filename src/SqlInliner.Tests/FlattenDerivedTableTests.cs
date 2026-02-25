using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using NUnit.Framework;
using Shouldly;

namespace SqlInliner.Tests;

public class FlattenDerivedTableTests
{
    private static readonly TSql150Parser Parser = new(true, SqlEngineType.All);

    private DatabaseConnection connection;

    private InlinerOptions FlattenOptions() => new()
    {
        StripUnusedColumns = true,
        StripUnusedJoins = true,
        FlattenDerivedTables = true,
    };

    [SetUp]
    public void Setup()
    {
        connection = new();
    }

    /// <summary>
    /// Asserts that the given SQL string is valid T-SQL by parsing it and checking for errors.
    /// </summary>
    private static void AssertValidSql(string sql)
    {
        using var reader = new StringReader(sql);
        Parser.Parse(reader, out var errors);
        errors.Count.ShouldBe(0, $"SQL parse errors:\n{string.Join("\n", errors.Select(e => $"  Line {e.Line}: {e.Message}"))}");
    }

    // ========================================================================
    // Flattenable cases (should flatten)
    // ========================================================================

    [Test]
    public void SimpleSingleTableDerivedTable()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.Name FROM dbo.People p");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VPeople v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        // Should be flattened — no derived table subquery
        result.ConvertedSql.ShouldNotContain("(SELECT");
        result.ConvertedSql.ShouldContain("dbo.People");
        inliner.TotalDerivedTablesFlattened.ShouldBeGreaterThan(0);
    }

    [Test]
    public void WithOuterWhereClause()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.Name FROM dbo.People p");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VPeople v WHERE v.Id > 10";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        // The outer WHERE should reference the inner table's alias
        result.ConvertedSql.ShouldContain("p.Id > 10");
    }

    [Test]
    public void WithInnerWhereClause()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.Name FROM dbo.People p WHERE p.Active = 1");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VPeople v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        result.ConvertedSql.ShouldContain("p.Active = 1");
    }

    [Test]
    public void WithBothInnerAndOuterWhere()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.Name FROM dbo.People p WHERE p.Active = 1");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VPeople v WHERE v.Id > 10";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        // Both conditions should be merged with AND
        result.ConvertedSql.ShouldContain("p.Id > 10");
        result.ConvertedSql.ShouldContain("p.Active = 1");
        result.ConvertedSql.ShouldContain("AND");
    }

    [Test]
    public void WithColumnAliasesInInnerSelect()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.FirstName FName FROM dbo.People p");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.FName FROM dbo.VPeople v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        // v.FName should be rewritten to p.FirstName
        result.ConvertedSql.ShouldContain("p.FirstName");
    }

    [Test]
    public void MultipleFlattenableDerivedTablesInJoin()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.Name FROM dbo.People p");
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VAddresses"),
            "CREATE VIEW dbo.VAddresses AS SELECT a.Id, a.PersonId, a.City FROM dbo.Addresses a");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id, p.Name, a.City FROM dbo.VPeople p INNER JOIN dbo.VAddresses a ON a.PersonId = p.Id";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        result.ConvertedSql.ShouldContain("dbo.People");
        result.ConvertedSql.ShouldContain("dbo.Addresses");
        inliner.TotalDerivedTablesFlattened.ShouldBe(2);
    }

    [Test]
    public void OneFlattenableOneNot_PartialFlatten()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.Name FROM dbo.People p");
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VGrouped"),
            "CREATE VIEW dbo.VGrouped AS SELECT g.PersonId, COUNT(*) Cnt FROM dbo.Grouped g GROUP BY g.PersonId");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id, p.Name, g.Cnt FROM dbo.VPeople p INNER JOIN dbo.VGrouped g ON g.PersonId = p.Id";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        // VPeople should be flattened, VGrouped should remain as derived table
        result.ConvertedSql.ShouldContain("dbo.People");
        inliner.TotalDerivedTablesFlattened.ShouldBe(1);
    }

    [Test]
    public void PreservesOuterColumnAliases()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.Name FROM dbo.People p");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id AS PersonId, v.Name AS PersonName FROM dbo.VPeople v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        result.ConvertedSql.ShouldContain("AS PersonId");
        result.ConvertedSql.ShouldContain("AS PersonName");
    }

    [Test]
    public void SchemaQualifiedInnerTable()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.Name FROM dbo.People p");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VPeople v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldContain("dbo.People");
    }

    [Test]
    public void EndToEnd_RegisterViews_Inline_Flatten_NoDerivedTables()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VBase"),
            "CREATE VIEW dbo.VBase AS SELECT t.Id, t.Val FROM dbo.BaseTable t");
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VMiddle"),
            "CREATE VIEW dbo.VMiddle AS SELECT b.Id, b.Val FROM dbo.VBase b WHERE b.Val > 0");

        const string viewSql = "CREATE VIEW dbo.VTop AS SELECT m.Id, m.Val FROM dbo.VMiddle m WHERE m.Id < 100";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        // All derived tables should be flattened
        result.ConvertedSql.ShouldNotContain("(SELECT");
        result.ConvertedSql.ShouldContain("dbo.BaseTable");
        inliner.TotalDerivedTablesFlattened.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Test]
    public void NestedViews_ThreeLevels_FullyFlattened()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "V1"),
            "CREATE VIEW dbo.V1 AS SELECT t.Id, t.Name FROM dbo.Table1 t");
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "V2"),
            "CREATE VIEW dbo.V2 AS SELECT v.Id, v.Name FROM dbo.V1 v");
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "V3"),
            "CREATE VIEW dbo.V3 AS SELECT v.Id, v.Name FROM dbo.V2 v");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.V3 v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        result.ConvertedSql.ShouldContain("dbo.Table1");
    }

    // ========================================================================
    // Non-flattenable cases (should remain as derived tables)
    // ========================================================================

    [Test]
    public void UnionInnerQuery_NotFlattened()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VUnion"),
            "CREATE VIEW dbo.VUnion AS SELECT t.Id FROM dbo.Table1 t UNION ALL SELECT t.Id FROM dbo.Table2 t");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id FROM dbo.VUnion v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        // Should NOT be flattened — UNION is not eligible
        inliner.TotalDerivedTablesFlattened.ShouldBe(0);
    }

    [Test]
    public void GroupByInnerQuery_NotFlattened()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VGrouped"),
            "CREATE VIEW dbo.VGrouped AS SELECT t.Category, COUNT(*) Cnt FROM dbo.Items t GROUP BY t.Category");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Category, v.Cnt FROM dbo.VGrouped v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        inliner.TotalDerivedTablesFlattened.ShouldBe(0);
    }

    [Test]
    public void HavingInnerQuery_NotFlattened()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VHaving"),
            "CREATE VIEW dbo.VHaving AS SELECT t.Category, COUNT(*) Cnt FROM dbo.Items t GROUP BY t.Category HAVING COUNT(*) > 1");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Category, v.Cnt FROM dbo.VHaving v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        inliner.TotalDerivedTablesFlattened.ShouldBe(0);
    }

    [Test]
    public void TopInnerQuery_NotFlattened()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VTop"),
            "CREATE VIEW dbo.VTop AS SELECT TOP 10 t.Id, t.Name FROM dbo.Items t");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VTop v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        inliner.TotalDerivedTablesFlattened.ShouldBe(0);
    }

    [Test]
    public void DistinctInnerQuery_NotFlattened()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VDistinct"),
            "CREATE VIEW dbo.VDistinct AS SELECT DISTINCT t.Category FROM dbo.Items t");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Category FROM dbo.VDistinct v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        inliner.TotalDerivedTablesFlattened.ShouldBe(0);
    }

    [Test]
    public void InnerJoin_DerivedTableFlattened()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VJoined"),
            "CREATE VIEW dbo.VJoined AS SELECT a.Id, b.Name FROM dbo.A a INNER JOIN dbo.B b ON a.Id = b.AId");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VJoined v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        result.ConvertedSql.ShouldContain("dbo.A");
        result.ConvertedSql.ShouldContain("dbo.B");
        inliner.TotalDerivedTablesFlattened.ShouldBe(1);
    }

    [Test]
    public void SelectStarInInnerQuery_NotFlattened()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VStar"),
            "CREATE VIEW dbo.VStar AS SELECT * FROM dbo.Items t");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id FROM dbo.VStar v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        inliner.TotalDerivedTablesFlattened.ShouldBe(0);
    }

    [Test]
    public void ComplexExpressionColumnReferencedByOuter_NotFlattened()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VComplex"),
            "CREATE VIEW dbo.VComplex AS SELECT t.Id, CASE WHEN t.Active = 1 THEN 'Yes' ELSE 'No' END ActiveLabel FROM dbo.Items t");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.ActiveLabel FROM dbo.VComplex v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        // Complex expression referenced by outer — should not flatten
        inliner.TotalDerivedTablesFlattened.ShouldBe(0);
    }

    [Test]
    public void FlattenDisabled_NotFlattened()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.Name FROM dbo.People p");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VPeople v";

        var options = InlinerOptions.Recommended();
        options.FlattenDerivedTables = false;

        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        // Should NOT be flattened because option is disabled
        inliner.TotalDerivedTablesFlattened.ShouldBe(0);
    }

    // ========================================================================
    // Alias collision cases
    // ========================================================================

    [Test]
    public void AliasCollision_InnerAliasRenamedToAvoidConflict()
    {
        // Inner view uses alias 't', outer query also has a table 't'
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT t.Id, t.Name FROM dbo.People t");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name, t.Val, t.Extra FROM dbo.VPeople v INNER JOIN dbo.Other t ON t.Id = v.Id";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        // Inner alias 't' should be renamed to 't1' to avoid conflict
        result.ConvertedSql.ShouldContain("t1");
        result.ConvertedSql.ShouldContain("dbo.People");
        inliner.TotalDerivedTablesFlattened.ShouldBe(1);
    }

    [Test]
    public void MultipleAliasCollisions_IncrementingRenames()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "V1"),
            "CREATE VIEW dbo.V1 AS SELECT t.Id, t.Name FROM dbo.T1 t");
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "V2"),
            "CREATE VIEW dbo.V2 AS SELECT t.Val, t.Code FROM dbo.T2 t");

        // Outer query has 't' already, both inner views use 't'
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT a.Id, a.Name, b.Val, b.Code, t.X FROM dbo.V1 a INNER JOIN dbo.V2 b ON b.Val = a.Id INNER JOIN dbo.Other t ON t.Id = a.Id";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        // Both inner 't' aliases should be renamed
        result.ConvertedSql.ShouldContain("t1");
        result.ConvertedSql.ShouldContain("t2");
        inliner.TotalDerivedTablesFlattened.ShouldBe(2);
    }

    [Test]
    public void AliasCollision_ResolvedInWhereClause()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT t.Id, t.Name FROM dbo.People t WHERE t.Active = 1");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name, t.Val, t.Extra FROM dbo.VPeople v INNER JOIN dbo.Other t ON t.Id = v.Id";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        // Inner WHERE should use renamed alias
        result.ConvertedSql.ShouldContain("t1.Active = 1");
    }

    [Test]
    public void AliasCollision_ResolvedInJoinCondition()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT t.Id, t.Name FROM dbo.People t");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name, t.Val, t.Extra FROM dbo.VPeople v INNER JOIN dbo.Other t ON t.PeopleId = v.Id";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        // JOIN condition should reference renamed alias
        result.ConvertedSql.ShouldContain("t1.Id");
        result.ConvertedSql.ShouldContain("dbo.People");
    }

    // ========================================================================
    // SQL validity tests
    // ========================================================================

    [Test]
    public void Flattened_OutputIsValidSql_SimpleCase()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.Name, p.Age FROM dbo.People p WHERE p.Active = 1");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VPeople v WHERE v.Age > 18";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);
    }

    [Test]
    public void Flattened_OutputIsValidSql_JoinCase()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.Name FROM dbo.People p");
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VOrders"),
            "CREATE VIEW dbo.VOrders AS SELECT o.Id, o.PersonId, o.Total FROM dbo.Orders o");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Name, o.Total FROM dbo.VPeople p INNER JOIN dbo.VOrders o ON o.PersonId = p.Id WHERE o.Total > 100";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);
    }

    [Test]
    public void Flattened_OutputIsValidSql_NestedCase()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "V1"),
            "CREATE VIEW dbo.V1 AS SELECT t.Id, t.Name FROM dbo.Table1 t WHERE t.Active = 1");
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "V2"),
            "CREATE VIEW dbo.V2 AS SELECT v.Id, v.Name FROM dbo.V1 v WHERE v.Id > 0");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.V2 v WHERE v.Name IS NOT NULL";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);
    }

    // ========================================================================
    // Metadata and statistics tests
    // ========================================================================

    [Test]
    public void MetadataContainsFlattenedCount()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.Name FROM dbo.People p");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VPeople v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        result.Sql.ShouldContain("flattened");
        result.Sql.ShouldContain("derived tables");
    }

    [Test]
    public void OptionsMetadata_IncludesFlattenDerivedTables()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.Name FROM dbo.People p");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VPeople v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Result.ShouldNotBeNull();
        inliner.Result.Sql.ShouldContain("FlattenDerivedTables=True");
    }

    // ========================================================================
    // Edge cases
    // ========================================================================

    [Test]
    public void NoViewReferences_FlattenDoesNothing()
    {
        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT 1 AS Col";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Result.ShouldBeNull(); // No views to inline
        inliner.TotalDerivedTablesFlattened.ShouldBe(0);
    }

    [Test]
    public void ComplexExpressionColumnNotReferencedByOuter_Flattened()
    {
        // If the complex expression column is NOT referenced by the outer query,
        // it gets stripped by column stripping, so flattening can proceed.
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VComplex"),
            "CREATE VIEW dbo.VComplex AS SELECT t.Id, t.Name, CASE WHEN t.Active = 1 THEN 'Yes' ELSE 'No' END ActiveLabel FROM dbo.Items t");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VComplex v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        // ActiveLabel is stripped, so the remaining derived table should be flattenable
        result.ConvertedSql.ShouldNotContain("(SELECT");
        result.ConvertedSql.ShouldContain("dbo.Items");
        inliner.TotalDerivedTablesFlattened.ShouldBe(1);
    }

    [Test]
    public void ExceptInnerQuery_NotFlattened()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VExcept"),
            "CREATE VIEW dbo.VExcept AS SELECT t.Id FROM dbo.Table1 t EXCEPT SELECT t.Id FROM dbo.Table2 t");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id FROM dbo.VExcept v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        inliner.TotalDerivedTablesFlattened.ShouldBe(0);
    }

    [Test]
    public void IntersectInnerQuery_NotFlattened()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VIntersect"),
            "CREATE VIEW dbo.VIntersect AS SELECT t.Id FROM dbo.Table1 t INTERSECT SELECT t.Id FROM dbo.Table2 t");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id FROM dbo.VIntersect v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        inliner.TotalDerivedTablesFlattened.ShouldBe(0);
    }

    // ========================================================================
    // Phase 2: Inner JOINs (multi-table derived tables)
    // ========================================================================

    [Test]
    public void InnerJoinWithWhere_WhereClauseMerged()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VJoined"),
            "CREATE VIEW dbo.VJoined AS SELECT a.Id, b.Name FROM dbo.A a INNER JOIN dbo.B b ON a.Id = b.AId WHERE a.Active = 1");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VJoined v WHERE v.Id > 10";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        result.ConvertedSql.ShouldContain("a.Active = 1");
        result.ConvertedSql.ShouldContain("a.Id > 10");
        result.ConvertedSql.ShouldContain("AND");
    }

    [Test]
    public void InnerJoin_AliasCollisionWithOuter()
    {
        // Inner view uses aliases 'a' and 'b', outer query already has table 'a'
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VJoined"),
            "CREATE VIEW dbo.VJoined AS SELECT a.Id, b.Name FROM dbo.X a INNER JOIN dbo.Y b ON a.Id = b.XId");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name, a.Extra FROM dbo.VJoined v INNER JOIN dbo.Z a ON a.VId = v.Id";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        // Inner alias 'a' should be renamed to 'a1' to avoid conflict with outer 'a'
        result.ConvertedSql.ShouldContain("a1");
        result.ConvertedSql.ShouldContain("dbo.X");
        result.ConvertedSql.ShouldContain("dbo.Y");
        inliner.TotalDerivedTablesFlattened.ShouldBe(1);
    }

    [Test]
    public void MultiWayInnerJoin_Flattened()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "V3Way"),
            "CREATE VIEW dbo.V3Way AS SELECT a.Id, b.Name, c.Code FROM dbo.A a INNER JOIN dbo.B b ON a.Id = b.AId INNER JOIN dbo.C c ON a.Id = c.AId");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name, v.Code FROM dbo.V3Way v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        result.ConvertedSql.ShouldContain("dbo.A");
        result.ConvertedSql.ShouldContain("dbo.B");
        result.ConvertedSql.ShouldContain("dbo.C");
        inliner.TotalDerivedTablesFlattened.ShouldBe(1);
    }

    [Test]
    public void LeftJoinInInnerQuery_Flattened()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VLeftJoined"),
            "CREATE VIEW dbo.VLeftJoined AS SELECT a.Id, a.Name, b.City FROM dbo.People a LEFT JOIN dbo.Addresses b ON b.PersonId = a.Id");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name, v.City FROM dbo.VLeftJoined v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        result.ConvertedSql.ShouldContain("dbo.People");
        result.ConvertedSql.ShouldContain("dbo.Addresses");
        result.ConvertedSql.ShouldContain("LEFT");
        inliner.TotalDerivedTablesFlattened.ShouldBe(1);
    }

    [Test]
    public void InnerJoinAndOuterJoin_BothFlattened()
    {
        // Inner view has a JOIN, outer query also joins another view (single-table)
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VJoined"),
            "CREATE VIEW dbo.VJoined AS SELECT a.Id, b.Name FROM dbo.A a INNER JOIN dbo.B b ON a.Id = b.AId");
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VSingle"),
            "CREATE VIEW dbo.VSingle AS SELECT c.Id, c.Val FROM dbo.C c");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT j.Id, j.Name, s.Val FROM dbo.VJoined j INNER JOIN dbo.VSingle s ON s.Id = j.Id";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        result.ConvertedSql.ShouldContain("dbo.A");
        result.ConvertedSql.ShouldContain("dbo.B");
        result.ConvertedSql.ShouldContain("dbo.C");
        inliner.TotalDerivedTablesFlattened.ShouldBe(2);
    }

    [Test]
    public void InnerJoin_MultipleAliasCollisions()
    {
        // Inner view uses 't' and 'u', outer has both 't' and 'u' already
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VJoined"),
            "CREATE VIEW dbo.VJoined AS SELECT t.Id, u.Name FROM dbo.X t INNER JOIN dbo.Y u ON t.Id = u.XId");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name, t.Val, u.Code FROM dbo.VJoined v INNER JOIN dbo.Other1 t ON t.Id = v.Id INNER JOIN dbo.Other2 u ON u.Id = v.Id";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        // Both inner aliases should be renamed
        result.ConvertedSql.ShouldContain("t1");
        result.ConvertedSql.ShouldContain("u1");
        result.ConvertedSql.ShouldContain("dbo.X");
        result.ConvertedSql.ShouldContain("dbo.Y");
        inliner.TotalDerivedTablesFlattened.ShouldBe(1);
    }

    [Test]
    public void InnerJoin_ComplexExpressionReferenced_NotFlattened()
    {
        // Inner view has a JOIN but also a complex expression that the outer query references
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VJoined"),
            "CREATE VIEW dbo.VJoined AS SELECT a.Id, CASE WHEN b.Active = 1 THEN 'Yes' ELSE 'No' END ActiveLabel FROM dbo.A a INNER JOIN dbo.B b ON a.Id = b.AId");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.ActiveLabel FROM dbo.VJoined v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        // Complex expression referenced by outer — should not flatten
        inliner.TotalDerivedTablesFlattened.ShouldBe(0);
    }

    [Test]
    public void InnerJoin_GroupBy_NotFlattened()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VGroupedJoin"),
            "CREATE VIEW dbo.VGroupedJoin AS SELECT a.Category, COUNT(b.Id) Cnt FROM dbo.A a INNER JOIN dbo.B b ON a.Id = b.AId GROUP BY a.Category");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Category, v.Cnt FROM dbo.VGroupedJoin v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        inliner.TotalDerivedTablesFlattened.ShouldBe(0);
    }

    [Test]
    public void LeftJoin_DerivedTableFlattened()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.Name FROM dbo.People p");
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VAddresses"),
            "CREATE VIEW dbo.VAddresses AS SELECT a.Id, a.PersonId, a.City FROM dbo.Addresses a");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT p.Id, p.Name, a.City FROM dbo.VPeople p LEFT JOIN dbo.VAddresses a ON a.PersonId = p.Id";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        result.ConvertedSql.ShouldContain("dbo.People");
        result.ConvertedSql.ShouldContain("dbo.Addresses");
    }

    [Test]
    public void NoAliasCollision_AliasPreserved()
    {
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.Name FROM dbo.People p");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VPeople v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        // 'p' is the inner alias and should be kept since there's no collision
        result.ConvertedSql.ShouldContain("p.Id");
        result.ConvertedSql.ShouldContain("p.Name");
    }
}
