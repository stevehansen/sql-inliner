using Microsoft.SqlServer.TransactSql.ScriptDom;
using NUnit.Framework;
using Shouldly;

namespace SqlInliner.Tests;

public class JoinHintTests
{
    private readonly InlinerOptions options = InlinerOptions.Recommended();

    [Test]
    public void LeftJoinUniqueHint_StrippedWhenUnused()
    {
        // LEFT JOIN with @join:unique is safe to remove — at most 1 match, all left-side rows preserved.
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VItems"),
            "CREATE VIEW dbo.VItems AS SELECT a.Id, a.Name, b.Code FROM dbo.A a LEFT JOIN /* @join:unique */ dbo.B b ON a.BId = b.Id");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VItems v";

        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.ShouldBeEmpty();
        inliner.Result.ShouldNotBeNull();
        inliner.Result!.ConvertedSql.ShouldNotContain("dbo.B");
        inliner.Result.ConvertedSql.ShouldContain("dbo.A");
    }

    [Test]
    public void InnerJoinUniqueRequiredHint_StrippedWhenUnused()
    {
        // INNER JOIN with @join:unique @join:required is safe — exactly 1 match per row.
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VItems"),
            "CREATE VIEW dbo.VItems AS SELECT a.Id, a.Name, b.Code FROM dbo.A a INNER JOIN /* @join:unique @join:required */ dbo.B b ON a.BId = b.Id");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VItems v";

        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.ShouldBeEmpty();
        inliner.Result.ShouldNotBeNull();
        inliner.Result!.ConvertedSql.ShouldNotContain("dbo.B");
        inliner.Result.ConvertedSql.ShouldContain("dbo.A");
    }

    [Test]
    public void InnerJoinUniqueOnly_KeptWhenUnused()
    {
        // INNER JOIN with @join:unique but NOT @join:required — removing could filter rows.
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VItems"),
            "CREATE VIEW dbo.VItems AS SELECT a.Id, a.Name, b.Code FROM dbo.A a INNER JOIN /* @join:unique */ dbo.B b ON a.BId = b.Id");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VItems v";

        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.ShouldBeEmpty();
        inliner.Result.ShouldNotBeNull();
        // B should be KEPT because hint says it's not safe to remove an INNER JOIN without @required
        inliner.Result!.ConvertedSql.ShouldContain("dbo.B");
        inliner.Result.ConvertedSql.ShouldContain("dbo.A");
    }

    [Test]
    public void NoHint_StrippedWhenUnused_BackwardCompat()
    {
        // Without hints, existing behavior: unused joins are still stripped.
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VItems"),
            "CREATE VIEW dbo.VItems AS SELECT a.Id, a.Name FROM dbo.A a INNER JOIN dbo.B b ON a.BId = b.Id");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VItems v";

        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.ShouldBeEmpty();
        inliner.Result.ShouldNotBeNull();
        // Backward compat: no hint → old behavior → stripped
        inliner.Result!.ConvertedSql.ShouldNotContain("dbo.B");
        inliner.Result.ConvertedSql.ShouldContain("dbo.A");
    }

    [Test]
    public void LeftJoinUniqueHint_KeptWhenUsed()
    {
        // Even with @join:unique, if columns ARE used, the join is kept.
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VItems"),
            "CREATE VIEW dbo.VItems AS SELECT a.Id, b.Code FROM dbo.A a LEFT JOIN /* @join:unique */ dbo.B b ON a.BId = b.Id");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Code FROM dbo.VItems v";

        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.ShouldBeEmpty();
        inliner.Result.ShouldNotBeNull();
        inliner.Result!.ConvertedSql.ShouldContain("dbo.B");
        inliner.Result.ConvertedSql.ShouldContain("dbo.A");
    }

    [Test]
    public void HintAfterAlias_Parsed()
    {
        // Hint comment placed after the table alias (before ON) should also work.
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VItems"),
            "CREATE VIEW dbo.VItems AS SELECT a.Id, a.Name, b.Code FROM dbo.A a LEFT JOIN dbo.B b /* @join:unique */ ON a.BId = b.Id");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VItems v";

        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.ShouldBeEmpty();
        inliner.Result.ShouldNotBeNull();
        inliner.Result!.ConvertedSql.ShouldNotContain("dbo.B");
        inliner.Result.ConvertedSql.ShouldContain("dbo.A");
    }

    [Test]
    public void SeparateHintComments_BothParsed()
    {
        // Two separate comments for @join:unique and @join:required.
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VItems"),
            "CREATE VIEW dbo.VItems AS SELECT a.Id, a.Name, b.Code FROM dbo.A a INNER JOIN /* @join:unique */ /* @join:required */ dbo.B b ON a.BId = b.Id");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VItems v";

        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.ShouldBeEmpty();
        inliner.Result.ShouldNotBeNull();
        // Both hints present → safe to remove INNER JOIN
        inliner.Result!.ConvertedSql.ShouldNotContain("dbo.B");
        inliner.Result.ConvertedSql.ShouldContain("dbo.A");
    }

    [Test]
    public void MultipleJoins_IndependentHints()
    {
        // Two joins with different hints — each handled independently.
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VItems"),
            @"CREATE VIEW dbo.VItems AS
              SELECT a.Id, a.Name, b.Code, c.Value
              FROM dbo.A a
              LEFT JOIN /* @join:unique */ dbo.B b ON a.BId = b.Id
              INNER JOIN /* @join:unique */ dbo.C c ON a.CId = c.Id");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VItems v";

        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.ShouldBeEmpty();
        inliner.Result.ShouldNotBeNull();
        // B: LEFT + @unique → safe, stripped
        inliner.Result!.ConvertedSql.ShouldNotContain("dbo.B");
        // C: INNER + @unique (no @required) → not safe, kept
        inliner.Result.ConvertedSql.ShouldContain("dbo.C");
        inliner.Result.ConvertedSql.ShouldContain("dbo.A");
    }

    [Test]
    public void HintOnViewReference_StrippedWhenSafe()
    {
        // Hints work on view references (in the outer view's joins), not just table references.
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VLookup"),
            "CREATE VIEW dbo.VLookup AS SELECT l.Id, l.Code FROM dbo.Lookup l");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT a.Id FROM dbo.A a LEFT JOIN /* @join:unique */ dbo.VLookup vl ON a.LookupId = vl.Id";

        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.ShouldBeEmpty();
        inliner.Result.ShouldNotBeNull();
        // VLookup is LEFT JOINed with @unique, no columns used → safe to remove
        inliner.Result!.ConvertedSql.ShouldNotContain("VLookup");
        inliner.Result.ConvertedSql.ShouldNotContain("dbo.Lookup");
        inliner.Result.ConvertedSql.ShouldContain("dbo.A");
    }

    [Test]
    public void HintOnViewReference_InnerJoinWithoutRequired_Kept()
    {
        // View reference with INNER JOIN @unique (no @required) should be kept.
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VLookup"),
            "CREATE VIEW dbo.VLookup AS SELECT l.Id, l.Code FROM dbo.Lookup l");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT a.Id FROM dbo.A a INNER JOIN /* @join:unique */ dbo.VLookup vl ON a.LookupId = vl.Id";

        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.ShouldBeEmpty();
        inliner.Result.ShouldNotBeNull();
        // INNER + @unique without @required → not safe → kept
        inliner.Result!.ConvertedSql.ShouldContain("dbo.Lookup");
        inliner.Result.ConvertedSql.ShouldContain("dbo.A");
        inliner.Warnings.ShouldContain(w => w.Contains("not safe"));
    }

    [Test]
    public void ParseJoinHints_CaseInsensitive()
    {
        // Hints should be parsed case-insensitively.
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VItems"),
            "CREATE VIEW dbo.VItems AS SELECT a.Id, a.Name, b.Code FROM dbo.A a LEFT JOIN /* @JOIN:UNIQUE */ dbo.B b ON a.BId = b.Id");

        const string viewSql = "CREATE VIEW dbo.VTest AS SELECT v.Id, v.Name FROM dbo.VItems v";

        var inliner = new DatabaseViewInliner(connection, viewSql, options);
        inliner.Errors.ShouldBeEmpty();
        inliner.Result.ShouldNotBeNull();
        inliner.Result!.ConvertedSql.ShouldNotContain("dbo.B");
        inliner.Result.ConvertedSql.ShouldContain("dbo.A");
    }

    [Test]
    public void JoinHintsParsedFromReferencesVisitor()
    {
        // Verify that hint parsing populates the JoinHints and JoinTypes dictionaries.
        var connection = new DatabaseConnection();
        const string viewSql = @"CREATE VIEW dbo.VTest AS
            SELECT a.Id, b.Code
            FROM dbo.A a
            LEFT JOIN /* @join:unique @join:required */ dbo.B b ON a.BId = b.Id";

        var (view, errors) = DatabaseView.FromSql(connection, viewSql);
        errors.Count.ShouldBe(0);
        view.ShouldNotBeNull();

        view!.References.Tables.Count.ShouldBe(2);
        var tableB = view.References.Tables[1]; // B is the second table
        tableB.SchemaObject.BaseIdentifier.Value.ShouldBe("B");

        view.References.JoinHints.ShouldContainKey(tableB);
        view.References.JoinHints[tableB].ShouldBe(JoinHint.Unique | JoinHint.Required);
        view.References.JoinTypes[tableB].ShouldBe(QualifiedJoinType.LeftOuter);
    }
}
