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
        // The outer WHERE should reference the derived table's alias
        result.ConvertedSql.ShouldContain("v.Id > 10");
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
        result.ConvertedSql.ShouldContain("v.Active = 1");
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
        result.ConvertedSql.ShouldContain("v.Id > 10");
        result.ConvertedSql.ShouldContain("v.Active = 1");
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
        // v.FName should be rewritten to the underlying column using the derived alias
        result.ConvertedSql.ShouldContain("v.FirstName");
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
        // Single-table inner query now uses derived alias 'v', so no collision with outer 't'
        result.ConvertedSql.ShouldContain("v");
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
        // Inner WHERE should use derived alias (no collision since inner table gets 'v')
        result.ConvertedSql.ShouldContain("v.Active = 1");
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
        // JOIN condition should reference the derived alias (no collision since inner table gets 'v')
        result.ConvertedSql.ShouldContain("v.Id");
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

        // The derived alias 'v' is preserved for single-table inner queries
        result.ConvertedSql.ShouldContain("v.Id");
        result.ConvertedSql.ShouldContain("v.Name");
    }

    // ========================================================================
    // Regression: unqualified column references and GROUP BY rewriting
    // ========================================================================

    [Test]
    public void UnqualifiedColumnInInnerWhere_QualifiedAfterFlatten()
    {
        // Inner view has unqualified 'CodeType' in WHERE — should be qualified to 'c.CodeType'
        // to avoid ambiguity when promoted to outer scope with other tables
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VStatus"),
            "CREATE VIEW dbo.VStatus AS SELECT c.Id, c.Code FROM dbo.Codes c WHERE CodeType = 'STATUS'");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Code, t.Name FROM dbo.VStatus v INNER JOIN dbo.Other t ON t.Id = v.Id";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        // Unqualified 'CodeType' should be qualified after flattening using the derived alias
        result.ConvertedSql.ShouldNotContain("(CodeType");
        result.ConvertedSql.ShouldContain("v.CodeType");
    }

    [Test]
    public void MultipleViewsWithUnqualifiedWhere_AllQualified()
    {
        // Multiple inner views with unqualified WHERE refs — all should be qualified
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VStatus1"),
            "CREATE VIEW dbo.VStatus1 AS SELECT c.Id, c.Code FROM dbo.Codes c WHERE CodeType = 'STATUS'");
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VStatus2"),
            "CREATE VIEW dbo.VStatus2 AS SELECT c.Id, c.Code FROM dbo.Codes c WHERE CodeType = 'CARCAT'");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT a.Id, a.Code, b.Code AS Code2 FROM dbo.VStatus1 a INNER JOIN dbo.VStatus2 b ON b.Id = a.Id";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        // Both should be flattened with qualified WHERE conditions
        result.ConvertedSql.ShouldNotContain("(SELECT");
        inliner.TotalDerivedTablesFlattened.ShouldBe(2);
    }

    [Test]
    public void OuterGroupByReferencingDerivedTable_RewrittenCorrectly()
    {
        // Outer query has GROUP BY referencing a derived table column — must be rewritten
        // Use 2+ columns from the inner view to avoid StripUnusedJoins removing it
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.PersonId, p.Name FROM dbo.People p");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.PersonId, v.Name, COUNT(*) Cnt FROM dbo.VPeople v GROUP BY v.PersonId, v.Name";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        // GROUP BY should reference the derived table's alias
        result.ConvertedSql.ShouldContain("v.PersonId");
        result.ConvertedSql.ShouldContain("v.Name");
        result.ConvertedSql.ShouldNotContain("p.PersonId");
    }

    [Test]
    public void OuterHavingReferencingDerivedTable_RewrittenCorrectly()
    {
        // Use 2+ columns from the inner view to avoid StripUnusedJoins removing it
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.PersonId, p.Name FROM dbo.People p");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.PersonId, v.Name, COUNT(*) Cnt FROM dbo.VPeople v GROUP BY v.PersonId, v.Name HAVING COUNT(*) > 1";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        result.ConvertedSql.ShouldContain("v.PersonId");
        result.ConvertedSql.ShouldContain("v.Name");
    }

    [Test]
    public void MultiTableInnerQuery_UnqualifiedRefs_NotFlattened()
    {
        // Multi-table inner query with unqualified column reference — should NOT flatten
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VJoined"),
            "CREATE VIEW dbo.VJoined AS SELECT a.Id, b.Name FROM dbo.A a INNER JOIN dbo.B b ON a.Id = b.AId WHERE Active = 1");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VJoined v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        // Should NOT be flattened — unqualified 'Active' in multi-table context
        inliner.TotalDerivedTablesFlattened.ShouldBe(0);
    }

    // ========================================================================
    // Regression: inner WHERE must go into JOIN ON clause, not outer WHERE
    // ========================================================================

    [Test]
    public void LeftJoinDerivedTable_InnerWhereMergedIntoOnClause()
    {
        // When a derived table with a WHERE is on the optional side of a LEFT JOIN,
        // its WHERE must be merged into the JOIN's ON clause, not the outer WHERE.
        // Otherwise the LEFT JOIN effectively becomes an INNER JOIN.
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VStatus"),
            "CREATE VIEW dbo.VStatus AS SELECT c.Id, c.Code FROM dbo.Codes c WHERE c.CodeType = 'STATUS'");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT t.Id, t.Name, v.Code AS Status FROM dbo.Things t LEFT OUTER JOIN dbo.VStatus v ON t.StatusId = v.Id";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        // The WHERE condition must be in the ON clause, not a top-level WHERE
        result.ConvertedSql.ShouldNotContain("WHERE");
        result.ConvertedSql.ShouldContain("v.CodeType = 'STATUS'");
        // ON clause should contain both the original join condition and the inner WHERE
        result.ConvertedSql.ShouldContain("t.StatusId = v.Id");
    }

    [Test]
    public void InnerJoinDerivedTable_InnerWhereMergedIntoOnClause()
    {
        // For INNER JOINs, the WHERE can go to either ON or outer WHERE (equivalent),
        // but we prefer ON to prevent issues when nested inside outer JOINs.
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VStatus"),
            "CREATE VIEW dbo.VStatus AS SELECT c.Id, c.Code FROM dbo.Codes c WHERE c.CodeType = 'STATUS'");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT t.Id, t.Name, v.Code AS Status FROM dbo.Things t INNER JOIN dbo.VStatus v ON t.StatusId = v.Id";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        // The WHERE condition should be in the ON clause
        result.ConvertedSql.ShouldNotContain("WHERE");
        result.ConvertedSql.ShouldContain("v.CodeType = 'STATUS'");
    }

    [Test]
    public void MultipleLeftJoins_AllInnerWheresMergedIntoRespectiveOnClauses()
    {
        // Simulates the VIndienst pattern: multiple LEFT JOINs to simple code-table views
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VStatus"),
            "CREATE VIEW dbo.VStatus AS SELECT c.Id, c.Code, c.DisplayStatus FROM dbo.Codes c WHERE c.CodeType = 'STATUS'");
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VCarCat"),
            "CREATE VIEW dbo.VCarCat AS SELECT c.Id, c.Code FROM dbo.Codes c WHERE c.CodeType = 'CARCAT'");

        const string viewSql = @"CREATE VIEW dbo.VTest AS
            SELECT t.Id, v1.Code AS Status, v1.DisplayStatus, v2.Code AS CarCat
            FROM dbo.Things t
            LEFT OUTER JOIN dbo.VStatus v1 ON t.StatusId = v1.Id
            LEFT OUTER JOIN dbo.VCarCat v2 ON t.CarCatId = v2.Id";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        inliner.TotalDerivedTablesFlattened.ShouldBe(2);
        // Inner WHERE conditions should NOT appear in a top-level WHERE clause
        result.ConvertedSql.ShouldNotContain("WHERE");
    }

    [Test]
    public void PreservedSideOfLeftJoin_WithInnerWhere_NotFlattened()
    {
        // The first ref (preserved side) of a LEFT JOIN can't have its WHERE safely placed
        // in the ON clause — it would change semantics. So skip flattening.
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VActive"),
            "CREATE VIEW dbo.VActive AS SELECT p.Id, p.Name FROM dbo.People p WHERE p.Active = 1");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name, t.Code FROM dbo.VActive v LEFT OUTER JOIN dbo.Codes t ON v.Id = t.Id";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        // VActive is on the preserved (first) side of a LEFT JOIN with a WHERE clause —
        // it should NOT be flattened because moving WHERE to ON would change join behavior
        inliner.TotalDerivedTablesFlattened.ShouldBe(0);
    }

    [Test]
    public void TopLevelDerivedTable_InnerWhereStillGoesToOuterWhere()
    {
        // When a derived table is the sole FROM source (no parent JOIN),
        // its inner WHERE should go into the outer WHERE as before.
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.Name FROM dbo.People p WHERE p.Active = 1");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VPeople v";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        result.ConvertedSql.ShouldNotContain("(SELECT");
        // Inner WHERE should be in the outer WHERE, using the derived table's alias
        result.ConvertedSql.ShouldContain("WHERE");
        result.ConvertedSql.ShouldContain("v.Active = 1");
    }

    [Test]
    public void DerivedTablesInsideOuterApply_StillFlattened()
    {
        // OUTER APPLY (UnqualifiedJoin) should not block flattening of derived tables
        // in its subtrees — e.g., INNER JOINs before the OUTER APPLY.
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VStatus"),
            "CREATE VIEW dbo.VStatus AS SELECT c.Id, c.Code FROM dbo.Codes c WHERE c.CodeType = 'STATUS'");

        // Build: Things INNER JOIN VStatus ON ... OUTER APPLY (correlated subquery)
        const string viewSql = @"CREATE VIEW dbo.VTest AS
            SELECT t.Id, v.Code, sub.Val
            FROM dbo.Things t
            INNER JOIN dbo.VStatus v ON t.StatusId = v.Id
            OUTER APPLY (SELECT TOP 1 x.Val FROM dbo.Extras x WHERE x.ThingId = t.Id) sub";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        // VStatus should be flattened despite the OUTER APPLY in the tree
        result.ConvertedSql.ShouldNotContain("(CodeType");
        inliner.TotalDerivedTablesFlattened.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Test]
    public void FlattenInsideGroupByDerivedTablePreservesColumnAlias()
    {
        // When a view (VInner) is flattened inside a GROUP BY query (VGrouped),
        // and VInner.CompanyId maps to Companies_1.Id, the SELECT element
        // "v.CompanyId" is rewritten to "Companies_1.Id" — but the inferred column
        // name changes from CompanyId to Id. The outer query referencing cl.CompanyId
        // then breaks. The fix adds an explicit AS alias to preserve the original name.
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VInner"),
            @"CREATE VIEW dbo.VInner AS
              SELECT c.Id AS ParentId, c.Name AS ParentName, c2.Id AS CompanyId
              FROM dbo.Clusters cl
              INNER JOIN dbo.Companies c ON c.Id = cl.ParentId
              INNER JOIN dbo.Companies c2 ON c2.Id = cl.CompanyId");

        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VGrouped"),
            @"CREATE VIEW dbo.VGrouped AS
              SELECT v.CompanyId, string_agg(v.ParentName, ', ') AS ParentName2
              FROM dbo.VInner v
              GROUP BY v.CompanyId");

        const string viewSql = @"CREATE VIEW dbo.VTest AS
            SELECT t.Id, cl.ParentName2
            FROM dbo.Things t
            LEFT JOIN dbo.VGrouped cl ON t.CompanyId = cl.CompanyId";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        // VInner should be flattened inside VGrouped (it has no GROUP BY itself)
        inliner.TotalDerivedTablesFlattened.ShouldBeGreaterThanOrEqualTo(1);

        // The outer reference cl.CompanyId must still resolve — the derived table
        // must expose CompanyId (not just Id) as a column name.
        result.ConvertedSql.ShouldContain("cl.CompanyId");
    }

    // ========================================================================
    // Bug fix: Unqualified outer column references
    // ========================================================================

    [Test]
    public void UnqualifiedOuterRef_QualifiedAfterFlatten()
    {
        // Outer query uses unqualified refs (Id, Name) to columns from the derived table.
        // After flattening, these must be rewritten to the inner table's qualified refs.
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VInner"),
            "CREATE VIEW dbo.VInner AS SELECT p.Id, p.Name FROM dbo.People p");

        const string viewSql = @"CREATE VIEW dbo.VTest AS
            SELECT Id, Name
            FROM dbo.VInner dt
            INNER JOIN dbo.Other o ON o.PersonId = dt.Id";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        inliner.TotalDerivedTablesFlattened.ShouldBeGreaterThanOrEqualTo(1);

        // Unqualified refs should now be qualified with the inner table alias
        result.ConvertedSql.ShouldContain("dt.Id");
        result.ConvertedSql.ShouldContain("dt.Name");
    }

    [Test]
    public void UnqualifiedOuterRef_ComplexExpression_NotFlattened()
    {
        // Outer query uses unqualified ref to a computed column — flattening should be skipped.
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VComputed"),
            "CREATE VIEW dbo.VComputed AS SELECT p.Id, p.FirstName + ' ' + p.LastName AS FullName FROM dbo.People p");

        const string viewSql = @"CREATE VIEW dbo.VTest AS
            SELECT FullName
            FROM dbo.VComputed dt";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        // Should NOT flatten because FullName maps to a complex expression
        result.ConvertedSql.ShouldContain("dt");
    }

    [Test]
    public void CascadingAliasRename_NoCorruption()
    {
        // Inner view has tables with aliases c and c1. Outer already uses alias c.
        // The rename c→c1 and c1→c11 must not corrupt each other.
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VInner"),
            @"CREATE VIEW dbo.VInner AS
              SELECT c.Id, c.Name, c1.Code
              FROM dbo.Companies c
              INNER JOIN dbo.Codes c1 ON c1.CompanyId = c.Id");

        const string viewSql = @"CREATE VIEW dbo.VTest AS
            SELECT v.Id, v.Name, v.Code, c.Category
            FROM dbo.VInner v
            INNER JOIN dbo.Categories c ON c.CompanyId = v.Id";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        inliner.TotalDerivedTablesFlattened.ShouldBeGreaterThanOrEqualTo(1);

        // Verify the SQL doesn't contain corrupted aliases — every column ref
        // must use an alias that actually appears as a table alias in the FROM clause.
        // Specifically, c11 should not appear unless there's a table aliased as c11.
        var sql = result.ConvertedSql;
        if (sql.Contains("c11."))
        {
            // If c11 refs exist, there must be a matching "AS c11" alias
            sql.ShouldContain("AS c11");
        }
    }

    [Test]
    public void SameViewInlinedTwice_NoAliasCrossContamination()
    {
        // VLanguages is used twice: once directly in the outer query,
        // and once inside VPersonGsms (aliased as phoneType).
        // This mirrors the pattern where the same view appears under multiple aliases.
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VLanguages"),
            "CREATE VIEW dbo.VLanguages AS SELECT Id, Code, CodeType FROM dbo.Codes WHERE CodeType = 'LANGUAGE'");

        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VPersonGsms"),
            @"CREATE VIEW dbo.VPersonGsms AS
              SELECT PersonGsms.PersonId, PersonGsms.GsmNr, PersonGsms.DefaultInd
              FROM dbo.PersonGsms
              INNER JOIN dbo.VLanguages AS phoneType
              ON phoneType.CodeType = 'GSMTYPE' AND phoneType.Code = 'GSM Werk'
              AND PersonGsms.GsmType = phoneType.Id");

        const string viewSql = @"CREATE VIEW dbo.VTest AS
            SELECT Persons.Id, Persons.FirstName, VLanguages.Code AS Language, VPersonGsms.GsmNr AS GSM
            FROM dbo.Persons
            LEFT OUTER JOIN dbo.Countries ON Persons.Nationality = Countries.Id
            LEFT OUTER JOIN dbo.VLanguages
              ON Persons.Language = VLanguages.Id
            LEFT OUTER JOIN dbo.VPersonGsms
              ON Persons.Id = VPersonGsms.PersonId AND VPersonGsms.DefaultInd = 1
            WHERE Persons.ActiveInd = 1";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        var sql = result.ConvertedSql;

        // The phoneType join condition should reference phoneType, not VLanguages
        sql.ShouldNotContain("VLanguages.Code = 'GSM Werk'");
        sql.ShouldNotContain("VLanguages.Code = N'GSM Werk'");

        // phoneType alias should exist in the output
        sql.ShouldContain("phoneType");
    }

    [Test]
    public void UnqualifiedRefInJoinCondition_NotClaimedByWrongDerivedTable()
    {
        // Unqualified ref aliasing bug:
        // The outer view directly joins VLanguages and phoneType (both dbo.Codes).
        // The phoneType ON condition has an UNQUALIFIED "Code" reference.
        // When VLanguages is flattened first, its 1-part matching in RewriteOuterColumnReferences
        // incorrectly claims the unqualified "Code" (which belongs to phoneType) and rewrites it
        // to "l.Code" (VLanguages's alias).
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VLanguages"),
            "CREATE VIEW dbo.VLanguages AS SELECT Id, Code, CodeType FROM dbo.Codes WHERE CodeType = 'LANGUAGE'");

        // Outer view joins VLanguages directly, and also joins PersonGsms + VLanguages AS phoneType.
        // Critically, the phoneType ON condition has unqualified "Code" (not "phoneType.Code").
        const string viewSql = @"CREATE VIEW dbo.VTest AS
            SELECT p.Id, p.FirstName, l.Code AS Language, PersonGsms.GsmNr AS GSM
            FROM dbo.Persons AS p
            LEFT OUTER JOIN dbo.VLanguages AS l
              ON p.Language = l.Id
            LEFT OUTER JOIN dbo.PersonGsms
              INNER JOIN dbo.VLanguages AS phoneType
              ON phoneType.CodeType = 'GSMTYPE' AND Code = 'GSM Werk'
                 AND PersonGsms.GsmType = phoneType.Id
              ON p.Id = PersonGsms.PersonId AND PersonGsms.DefaultInd = 1";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        var sql = result.ConvertedSql;
        TestContext.Out.WriteLine("=== Generated SQL ===");
        TestContext.Out.WriteLine(sql);
        TestContext.Out.WriteLine("=== End SQL ===");

        // The unqualified "Code" should NOT be rewritten to "l.Code" (VLanguages alias)
        sql.ShouldNotContain("l.Code = 'GSM Werk'");
        sql.ShouldNotContain("l.Code = N'GSM Werk'");

        // phoneType alias should be present and Code should reference it
        sql.ShouldContain("phoneType");
    }

    [Test]
    public void CrossScopeRef_QdtNotFlattened_WhenReferencedFromSiblingJoinOnClause()
    {
        // Cross-scope reference bug: the original view has a
        // cross-scope reference (VLanguages.Code) in a nested inner join's ON clause,
        // referencing the outer VLanguages. SQL Server allows this with derived tables
        // but not with plain table aliases. So VLanguages must NOT be flattened.
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VLanguages"),
            "CREATE VIEW dbo.VLanguages AS SELECT Id, Code, CodeType FROM dbo.Codes WHERE CodeType = 'LANGUAGE'");

        const string viewSql = @"CREATE VIEW dbo.VTest AS
            SELECT p.Id, p.FirstName, VLanguages.Code AS Language, PersonGsms.GsmNr AS GSM
            FROM dbo.Persons AS p
            LEFT OUTER JOIN dbo.VLanguages
              ON p.Language = VLanguages.Id
            LEFT OUTER JOIN dbo.PersonGsms
              INNER JOIN dbo.VLanguages AS phoneType
              ON phoneType.CodeType = 'GSMTYPE' AND VLanguages.Code = 'GSM Werk'
                 AND PersonGsms.GsmType = phoneType.Id
              ON p.Id = PersonGsms.PersonId AND PersonGsms.DefaultInd = 1";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        var sql = result.ConvertedSql;

        // VLanguages must NOT be flattened (cross-scope ref from sibling join's ON clause)
        sql.ShouldContain(") AS VLanguages");

        // phoneType should still be flattened (no cross-scope issues)
        sql.ShouldContain("dbo.Codes AS phoneType");

        // The cross-scope reference VLanguages.Code should be preserved
        sql.ShouldContain("VLanguages.Code");
    }

    [Test]
    public void CrossScopeRef_UnqualifiedRef_NotFlattened_WhenMatchesColumnMapInCrossScopeOnClause()
    {
        // Unqualified cross-scope ref bug: VLanguages (view aliasing dbo.Codes) is in the
        // outer FROM clause. phoneType (also dbo.Codes) is in a nested INNER JOIN.
        // The INNER JOIN's ON clause has an unqualified "Code" that the flattener's rewriter
        // would incorrectly claim as VLanguages.Code, creating a cross-scope reference.
        // VLanguages must NOT be flattened when this pattern exists.
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VLanguages"),
            "CREATE VIEW dbo.VLanguages AS SELECT c.Id, c.Code, c.CodeType FROM dbo.Codes c WHERE c.CodeType = N'LANGUAGE'");

        const string viewSql = @"CREATE VIEW dbo.VTest AS
            SELECT p.Id, p.FirstName, VLanguages.Code AS Language, PersonGsms.GsmNr AS GSM
            FROM dbo.Persons AS p
            LEFT OUTER JOIN dbo.VLanguages
              ON p.Language = VLanguages.Id
            LEFT OUTER JOIN dbo.PersonGsms
              INNER JOIN dbo.Codes AS phoneType
              ON phoneType.CodeType = 'GSMTYPE' AND Code = 'GSM Werk'
                 AND PersonGsms.GsmType = phoneType.Id
              ON p.Id = PersonGsms.PersonId AND PersonGsms.DefaultInd = 1";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        var sql = result.ConvertedSql;

        // VLanguages must NOT be flattened — unqualified Code in cross-scope ON matches column map
        sql.ShouldContain(") AS VLanguages");

        // The unqualified Code should NOT be rewritten to VLanguages.Code
        sql.ShouldNotContain("VLanguages.Code = 'GSM Werk'");
        sql.ShouldNotContain("VLanguages.Code = N'GSM Werk'");
    }

    [Test]
    public void UnqualifiedOuterRef_InSelectAndWhere_BothRewritten()
    {
        // Unqualified refs in both SELECT and WHERE should both be rewritten.
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VInner"),
            "CREATE VIEW dbo.VInner AS SELECT p.Id, p.Name, p.ActiveInd FROM dbo.People p");

        const string viewSql = @"CREATE VIEW dbo.VTest AS
            SELECT Id, Name
            FROM dbo.VInner dt
            INNER JOIN dbo.Other o ON o.PersonId = dt.Id
            WHERE ActiveInd = 1";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        inliner.TotalDerivedTablesFlattened.ShouldBeGreaterThanOrEqualTo(1);

        // Both SELECT and WHERE refs should be qualified
        result.ConvertedSql.ShouldContain("dt.Id");
        result.ConvertedSql.ShouldContain("dt.Name");
        result.ConvertedSql.ShouldContain("dt.ActiveInd");
    }

    // ========================================================================
    // Regression: OUTER APPLY lateral refs must prevent column stripping
    // ========================================================================

    [Test]
    public void OuterApply_LateralRef_PreventsColumnStripping()
    {
        // Reproduction of VRegistrations bug: an OUTER APPLY subquery references
        // Registrations.CompanyId via a lateral reference, but the DerivedTableStripper
        // doesn't look inside APPLY subqueries and strips CompanyId.
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VRegistrations"),
            @"CREATE VIEW dbo.VRegistrations AS
              SELECT Persons.Id, Persons.Name, PersonContracts.CompanyId
              FROM dbo.Persons
              INNER JOIN dbo.PersonContracts ON Persons.Id = PersonContracts.PersonId");

        const string viewSql = @"CREATE VIEW dbo.VTest AS
            SELECT r.Id, r.Name, AccountNr.IBAN
            FROM dbo.VRegistrations r
            OUTER APPLY (SELECT TOP 1 AccountNrs.IBAN
                         FROM dbo.CompanyAccountNrs
                         INNER JOIN dbo.AccountNrs ON CompanyAccountNrs.AccountNrId = AccountNrs.Id
                         WHERE r.CompanyId = CompanyAccountNrs.CompanyId) AS AccountNr";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0, $"Errors: {string.Join("; ", inliner.Errors)}");

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        // CompanyId must be preserved in the derived table — it's needed by the OUTER APPLY
        result.ConvertedSql.ShouldContain("CompanyId");
        result.ConvertedSql.ShouldContain("r.CompanyId");
    }

    // ========================================================================
    // Regression: case-1 join stripping must not remove views used in SELECT
    // ========================================================================

    [Test]
    public void JoinStripping_Case1_KeepsViewUsedInSelect_SecondTable()
    {
        // VCompanies is the SECOND table in the join — tests JoinConditions path.
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VCompanies"),
            "CREATE VIEW dbo.VCompanies AS SELECT c.Id, c.Name FROM dbo.Companies c");

        const string viewSql = @"CREATE VIEW dbo.VTest AS
            SELECT VCompanies.Id, GroupCV.Company, GroupCV.TotalCV
            FROM (SELECT CompanyId, MAX(Company) AS Company, COUNT(*) AS TotalCV
                  FROM dbo.SomeTable GROUP BY CompanyId) AS GroupCV
            LEFT OUTER JOIN dbo.VCompanies ON GroupCV.CompanyId = VCompanies.Id";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0);

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        // VCompanies must NOT be stripped — its Id column is used in the SELECT
        result.ConvertedSql.ShouldContain("VCompanies");
    }

    [Test]
    public void JoinStripping_Case1_KeepsViewUsedInSelect_FirstTable()
    {
        // Reproduction of VCountCV bug: VCompanies is the FIRST table in the join
        // (no JoinConditions entry). The case-1 check must still detect external refs.
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VCompanies"),
            "CREATE VIEW dbo.VCompanies AS SELECT c.Id, c.Name FROM dbo.Companies c");

        const string viewSql = @"CREATE VIEW dbo.VTest AS
            SELECT VCompanies.Id, GroupCV.Company, GroupCV.TotalCV
            FROM dbo.VCompanies
            LEFT OUTER JOIN (SELECT CompanyId, MAX(Company) AS Company, COUNT(*) AS TotalCV
                  FROM dbo.SomeTable GROUP BY CompanyId) AS GroupCV
                  ON GroupCV.CompanyId = VCompanies.Id";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0, $"Errors: {string.Join("; ", inliner.Errors)}");

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        // VCompanies must NOT be stripped — its Id column is used in the SELECT
        result.ConvertedSql.ShouldContain("VCompanies");
    }

    [Test]
    public void JoinStripping_Case1_KeepsViewUsedInSelect_WithNestedView()
    {
        // Same as JoinStripping_Case1_KeepsViewUsedInSelect but with a view inside the
        // derived table (matching real VCountCV structure: GroupCV wraps VCvs view).
        // This puts TWO views in references.Views, testing that the loop correctly
        // handles both without interfering.
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VCompanies"),
            "CREATE VIEW dbo.VCompanies AS SELECT c.Id, c.Name FROM dbo.Companies c");
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VCvs"),
            @"CREATE VIEW dbo.VCvs AS
              SELECT pc.CompanyId, c.Name AS Company
              FROM dbo.PersonCVs pc
              INNER JOIN dbo.Companies c ON pc.CompanyId = c.Id");

        const string viewSql = @"CREATE VIEW dbo.VTest AS
            SELECT VCompanies.Id, GroupCV.Company, GroupCV.TotalCV
            FROM (SELECT CompanyId, MAX(Company) AS Company, COUNT(*) AS TotalCV
                  FROM dbo.VCvs AS VCvs GROUP BY CompanyId) AS GroupCV
            LEFT OUTER JOIN dbo.VCompanies ON GroupCV.CompanyId = VCompanies.Id";

        var inliner = new DatabaseViewInliner(connection, viewSql, FlattenOptions());
        inliner.Errors.Count.ShouldBe(0, $"Errors: {string.Join("; ", inliner.Errors)}");

        var result = inliner.Result;
        result.ShouldNotBeNull();
        AssertValidSql(result.ConvertedSql);

        // VCompanies must NOT be stripped — its Id column is used in the SELECT
        result.ConvertedSql.ShouldContain("VCompanies");
    }
}
