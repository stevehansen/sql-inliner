using System.IO;
using System.Linq;
using NUnit.Framework;
using Shouldly;
using SqlInliner.Optimize;

namespace SqlInliner.Tests;

public class ValidateSessionTests
{
    [Test]
    public void ProcessesAllViews()
    {
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VInner"),
            "CREATE VIEW dbo.VInner AS SELECT p.Id, p.Name FROM dbo.People p");
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VOuter"),
            "CREATE VIEW dbo.VOuter AS SELECT i.Id, i.Name FROM dbo.VInner i");
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VSimple1"),
            "CREATE VIEW dbo.VSimple1 AS SELECT p.Id FROM dbo.People p");
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VSimple2"),
            "CREATE VIEW dbo.VSimple2 AS SELECT p.Id FROM dbo.People p");

        var wizard = new MockWizard();
        var session = new ValidateSession(connection, new InlinerOptions(), wizard);
        var results = session.Run(new ValidateSessionOptions());

        results.Count.ShouldBe(4);
        results.Count(r => r.Status == ViewValidateStatus.Pass).ShouldBe(1); // VOuter
        results.Count(r => r.Status == ViewValidateStatus.Skipped).ShouldBe(3); // VInner, VSimple1, VSimple2
    }

    [Test]
    public void FilterRestrictsViews()
    {
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT p.Id FROM dbo.People p");
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VOrders"),
            "CREATE VIEW dbo.VOrders AS SELECT o.Id FROM dbo.Orders o");

        var wizard = new MockWizard();
        var session = new ValidateSession(connection, new InlinerOptions(), wizard);
        var results = session.Run(new ValidateSessionOptions { Filter = "dbo.VPeople" });

        results.Count.ShouldBe(1);
        results[0].ViewName.ShouldContain("VPeople");
    }

    [Test]
    public void FilterWithWildcard()
    {
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VPeople"),
            "CREATE VIEW dbo.VPeople AS SELECT p.Id FROM dbo.People p");
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VPeopleExtra"),
            "CREATE VIEW dbo.VPeopleExtra AS SELECT p.Id FROM dbo.People p");
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VOrders"),
            "CREATE VIEW dbo.VOrders AS SELECT o.Id FROM dbo.Orders o");

        var wizard = new MockWizard();
        var session = new ValidateSession(connection, new InlinerOptions(), wizard);
        var results = session.Run(new ValidateSessionOptions { Filter = "dbo.VPeople%" });

        results.Count.ShouldBe(2);
        results.ShouldAllBe(r => r.ViewName.Contains("VPeople"));
    }

    [Test]
    public void StopOnErrorHalts()
    {
        var connection = new DatabaseConnection();
        // Register VNoDefn as a view with no definition — GetViewDefinition will throw
        // when the inliner tries to resolve it
        connection.Views.Add(DatabaseConnection.ToObjectName("dbo", "VNoDefn"));
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "ABroken"),
            "CREATE VIEW dbo.ABroken AS SELECT x.Id FROM dbo.VNoDefn x");
        // This view would normally succeed, but we should stop before reaching it
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VGood"),
            "CREATE VIEW dbo.VGood AS SELECT p.Id FROM dbo.People p");

        var wizard = new MockWizard();
        var session = new ValidateSession(connection, new InlinerOptions(), wizard);
        var results = session.Run(new ValidateSessionOptions { StopOnError = true });

        // ABroken sorts before VGood, so it should fail and stop
        results.Count.ShouldBe(1);
        results[0].ViewName.ShouldContain("ABroken");
        ValidateSession.IsFailure(results[0].Status).ShouldBeTrue();
    }

    [Test]
    public void InliningErrorCaptured()
    {
        var connection = new DatabaseConnection();
        // Register VNoDefn as a view with no definition — GetViewDefinition will throw
        // when the inliner tries to resolve it, testing the exception capture path
        connection.Views.Add(DatabaseConnection.ToObjectName("dbo", "VNoDefn"));
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VBroken"),
            "CREATE VIEW dbo.VBroken AS SELECT x.Id FROM dbo.VNoDefn x");

        var wizard = new MockWizard();
        var session = new ValidateSession(connection, new InlinerOptions(), wizard);
        var results = session.Run(new ValidateSessionOptions());

        // VBroken should be captured as an error, not thrown as an unhandled exception
        var broken = results.First(r => r.ViewName.Contains("VBroken"));
        ValidateSession.IsFailure(broken.Status).ShouldBeTrue();
        broken.Errors.ShouldNotBeEmpty();
    }

    [Test]
    public void OutputDirectorySavesFiles()
    {
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VInner"),
            "CREATE VIEW dbo.VInner AS SELECT p.Id, p.Name FROM dbo.People p");
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VOuter"),
            "CREATE VIEW dbo.VOuter AS SELECT i.Id, i.Name FROM dbo.VInner i");

        var tempDir = Path.Combine(Path.GetTempPath(), $"validate-test-{System.Guid.NewGuid():N}");
        try
        {
            var wizard = new MockWizard();
            var session = new ValidateSession(connection, new InlinerOptions(), wizard);
            session.Run(new ValidateSessionOptions { OutputDir = tempDir });

            // VOuter should have been inlined and saved; VInner has no nested views so is skipped
            var files = Directory.GetFiles(tempDir, "*.sql");
            files.Length.ShouldBe(1);
            files[0].ShouldContain("VOuter");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public void SummaryShowsStatusCounts()
    {
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VInner"),
            "CREATE VIEW dbo.VInner AS SELECT p.Id FROM dbo.People p");
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VOuter"),
            "CREATE VIEW dbo.VOuter AS SELECT i.Id FROM dbo.VInner i");

        var wizard = new MockWizard();
        var session = new ValidateSession(connection, new InlinerOptions(), wizard);
        session.Run(new ValidateSessionOptions());

        wizard.InfoMessages.ShouldContain(m => m.Contains("Validation Summary"));
        wizard.InfoMessages.ShouldContain(m => m.Contains("Total:"));
    }

    [Test]
    public void BuildFilterRegex_ExactMatch()
    {
        var regex = ValidateSession.BuildFilterRegex("dbo.VPeople");
        // In Run(), brackets are stripped before matching, so test against unbracketed names
        regex.IsMatch("dbo.VPeople").ShouldBeTrue();
        regex.IsMatch("dbo.VOrders").ShouldBeFalse();
    }

    [Test]
    public void BuildFilterRegex_ExactMatch_BracketedFilter()
    {
        // Filter with brackets — BuildFilterRegex strips them
        var regex = ValidateSession.BuildFilterRegex("[dbo].[VPeople]");
        regex.IsMatch("dbo.VPeople").ShouldBeTrue();
        regex.IsMatch("dbo.VOrders").ShouldBeFalse();
    }

    [Test]
    public void BuildFilterRegex_WildcardMatch()
    {
        var regex = ValidateSession.BuildFilterRegex("dbo.V%");
        regex.IsMatch("dbo.VPeople").ShouldBeTrue();
        regex.IsMatch("dbo.VOrders").ShouldBeTrue();
        regex.IsMatch("dbo.TPeople").ShouldBeFalse();
    }
}
