using System.Linq;
using NUnit.Framework;
using Shouldly;
using SqlInliner.Optimize;

namespace SqlInliner.Tests;

public class VerifySessionTests
{
    private const string InnerViewSql = "CREATE VIEW dbo.VInner AS SELECT p.Id, p.Name FROM dbo.People p";
    private const string OuterViewSql = "CREATE VIEW dbo.VOuter AS SELECT i.Id, i.Name FROM dbo.VInner i";

    /// <summary>
    /// Builds a fake "inlined" view definition with BEGIN/END ORIGINAL markers wrapping the given original SQL.
    /// </summary>
    private static string BuildInlinedSql(string originalSql, string viewName)
    {
        return $"/*\n{DatabaseView.BeginOriginal}\n{originalSql}\n{DatabaseView.EndOriginal}\n*/\nCREATE OR ALTER VIEW {viewName} AS SELECT p.Id, p.Name FROM dbo.People p";
    }

    [Test]
    public void SkipsViewsWithoutMarkers()
    {
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VPlain1"),
            "CREATE VIEW dbo.VPlain1 AS SELECT p.Id FROM dbo.People p");
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VPlain2"),
            "CREATE VIEW dbo.VPlain2 AS SELECT p.Id FROM dbo.People p");

        var wizard = new MockWizard();
        var session = new VerifySession(connection, wizard);
        var results = session.Run(new VerifySessionOptions());

        // No views have markers, so none should be processed
        results.Count.ShouldBe(0);
    }

    [Test]
    public void DetectsInlinedViews()
    {
        var connection = new DatabaseConnection();
        // Register a view with markers (simulating a deployed inlined view)
        var inlinedSql = BuildInlinedSql(OuterViewSql, "[dbo].[VOuter]");
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VOuter"),
            inlinedSql);

        var wizard = new MockWizard();
        var session = new VerifySession(connection, wizard);
        var results = session.Run(new VerifySessionOptions());

        // The view has markers, so it should be detected (not skipped)
        results.Count.ShouldBe(1);
        results[0].ViewName.ShouldContain("VOuter");
        // Without a real DB, it will get an Exception status — but it's NOT skipped
        results[0].Status.ShouldNotBe(ViewVerifyStatus.Skipped);
    }

    [Test]
    public void SkipsInlinedCompanionViews()
    {
        var connection = new DatabaseConnection();
        // Register VPeople (with markers) — the canonical inlined view
        var inlinedSql = BuildInlinedSql(OuterViewSql, "[dbo].[VPeople]");
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VPeople"),
            inlinedSql);

        // Register VPeople_Inlined (with markers) — the companion copy
        var companionSql = BuildInlinedSql(OuterViewSql, "[dbo].[VPeople_Inlined]");
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VPeople_Inlined"),
            companionSql);

        var wizard = new MockWizard();
        var session = new VerifySession(connection, wizard);
        var results = session.Run(new VerifySessionOptions());

        // Only VPeople should be processed, VPeople_Inlined should be filtered out
        results.Count.ShouldBe(1);
        results[0].ViewName.ShouldContain("VPeople");
        results[0].ViewName.ShouldNotContain("_Inlined");
    }

    [Test]
    public void ExtractOriginalSql_WithMarkers()
    {
        var rawDef = $"/*\n{DatabaseView.BeginOriginal}\nCREATE VIEW dbo.V AS SELECT 1\n{DatabaseView.EndOriginal}\n*/\nCREATE OR ALTER VIEW dbo.V AS SELECT 1";
        var result = VerifySession.ExtractOriginalSql(rawDef);

        result.ShouldNotBeNull();
        result.ShouldContain("CREATE VIEW dbo.V AS SELECT 1");
    }

    [Test]
    public void ExtractOriginalSql_NoMarkers()
    {
        var rawDef = "CREATE VIEW dbo.V AS SELECT 1";
        var result = VerifySession.ExtractOriginalSql(rawDef);

        result.ShouldBeNull();
    }

    [Test]
    public void FilterRestrictsViews()
    {
        var connection = new DatabaseConnection();
        var inlinedA = BuildInlinedSql("CREATE VIEW dbo.VA AS SELECT 1", "[dbo].[VA]");
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VA"), inlinedA);

        var inlinedB = BuildInlinedSql("CREATE VIEW dbo.VB AS SELECT 1", "[dbo].[VB]");
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "VB"), inlinedB);

        var wizard = new MockWizard();
        var session = new VerifySession(connection, wizard);
        var results = session.Run(new VerifySessionOptions { Filter = "dbo.VA" });

        results.Count.ShouldBe(1);
        results[0].ViewName.ShouldContain("VA");
    }

    [Test]
    public void SummaryShowsStatusCounts()
    {
        var connection = new DatabaseConnection();
        var inlinedSql = BuildInlinedSql(OuterViewSql, "[dbo].[VOuter]");
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VOuter"),
            inlinedSql);

        var wizard = new MockWizard();
        var session = new VerifySession(connection, wizard);
        session.Run(new VerifySessionOptions());

        wizard.InfoMessages.ShouldContain(m => m.Contains("Verify Summary"));
        wizard.InfoMessages.ShouldContain(m => m.Contains("Total:"));
    }

    [Test]
    public void StopOnErrorHalts()
    {
        var connection = new DatabaseConnection();
        // Both views have markers but will fail (no DB) — first failure should halt
        var inlinedA = BuildInlinedSql("CREATE VIEW dbo.AFirst AS SELECT 1", "[dbo].[AFirst]");
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "AFirst"), inlinedA);

        var inlinedB = BuildInlinedSql("CREATE VIEW dbo.BSecond AS SELECT 1", "[dbo].[BSecond]");
        connection.AddViewDefinition(DatabaseConnection.ToObjectName("dbo", "BSecond"), inlinedB);

        var wizard = new MockWizard();
        var session = new VerifySession(connection, wizard);
        var results = session.Run(new VerifySessionOptions { StopOnError = true });

        // Should stop after first failure
        results.Count.ShouldBe(1);
        results[0].ViewName.ShouldContain("AFirst");
    }
}
