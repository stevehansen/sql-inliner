using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Shouldly;
using SqlInliner.Optimize;

namespace SqlInliner.Tests;

/// <summary>
/// Mock wizard that returns predetermined answers for testing.
/// </summary>
internal sealed class MockWizard : IConsoleWizard
{
    private readonly Queue<bool> confirmAnswers = new();
    private readonly Queue<int> chooseAnswers = new();
    private readonly Queue<string?> promptAnswers = new();

    public List<string> InfoMessages { get; } = new();
    public List<string> WarnMessages { get; } = new();
    public List<string> ErrorMessages { get; } = new();
    public List<string> SuccessMessages { get; } = new();

    public void QueueConfirm(params bool[] answers)
    {
        foreach (var a in answers) confirmAnswers.Enqueue(a);
    }

    public void QueueChoose(params int[] answers)
    {
        foreach (var a in answers) chooseAnswers.Enqueue(a);
    }

    public void QueuePrompt(params string?[] answers)
    {
        foreach (var a in answers) promptAnswers.Enqueue(a);
    }

    public bool Confirm(string message, bool defaultValue = false)
    {
        InfoMessages.Add($"[Confirm] {message}");
        return confirmAnswers.Count > 0 ? confirmAnswers.Dequeue() : defaultValue;
    }

    public int Choose(string message, IReadOnlyList<string> options)
    {
        InfoMessages.Add($"[Choose] {message}");
        return chooseAnswers.Count > 0 ? chooseAnswers.Dequeue() : 0;
    }

    public string? Prompt(string message)
    {
        InfoMessages.Add($"[Prompt] {message}");
        return promptAnswers.Count > 0 ? promptAnswers.Dequeue() : null;
    }

    public void Info(string message) => InfoMessages.Add(message);
    public void Warn(string message) => WarnMessages.Add(message);
    public void Error(string message) => ErrorMessages.Add(message);
    public void Success(string message) => SuccessMessages.Add(message);

    public void WriteTable(string[] headers, IReadOnlyList<string[]> rows)
    {
        InfoMessages.Add($"[Table] {string.Join(", ", headers)}");
        foreach (var row in rows)
            InfoMessages.Add($"  {string.Join(", ", row)}");
    }

    public void WaitForEnter(string message)
    {
        InfoMessages.Add($"[WaitForEnter] {message}");
    }
}

public class OptimizeSessionTests
{
    [Test]
    public void RenameView_ReplacesCREATE_OR_ALTER_VIEW_Name()
    {
        const string sql = "CREATE OR ALTER VIEW [dbo].[VPeople] AS SELECT p.Id FROM dbo.People p";
        var result = OptimizeSession.RenameView(sql, "dbo", "VPeople_Inlined");
        result.ShouldStartWith("CREATE OR ALTER VIEW [dbo].[VPeople_Inlined]");
        result.ShouldContain("dbo.People");
    }

    [Test]
    public void RenameView_HandlesUnbracketedNames()
    {
        const string sql = "CREATE OR ALTER VIEW dbo.VPeople AS SELECT p.Id FROM dbo.People p";
        var result = OptimizeSession.RenameView(sql, "dbo", "VPeople_Inlined");
        result.ShouldStartWith("CREATE OR ALTER VIEW [dbo].[VPeople_Inlined]");
    }

    [Test]
    public void RenameView_DoesNotAffectColumnReferences()
    {
        const string sql = "CREATE OR ALTER VIEW [dbo].[VPeople] AS SELECT p.Id, p.VPeople FROM dbo.People p";
        var result = OptimizeSession.RenameView(sql, "dbo", "VPeople_Inlined");
        result.ShouldContain("p.VPeople");
    }

    [Test]
    public void UserDeclinesBackupConfirmation_ThrowsOperationCanceled()
    {
        var wizard = new MockWizard();
        wizard.QueueConfirm(false); // decline backup confirmation

        var connection = new DatabaseConnection();
        var session = new OptimizeSession(connection, wizard, System.IO.Path.GetTempPath());

        Should.Throw<System.OperationCanceledException>(() => session.Run("dbo.VTest"));
    }

    [Test]
    public void SessionFlowReachesInlineStep()
    {
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VInner"),
            "CREATE VIEW dbo.VInner AS SELECT p.Id, p.Name FROM dbo.People p");
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VOuter"),
            "CREATE VIEW dbo.VOuter AS SELECT i.Id, i.Name FROM dbo.VInner i");

        var wizard = new MockWizard();
        wizard.QueueConfirm(
            true,   // backup confirmation
            false,  // don't open editor
            false   // don't deploy (skip since no real DB)
        );
        wizard.QueueChoose(0); // continue to summary

        var session = new OptimizeSession(connection, wizard, System.IO.Path.GetTempPath());

        session.Run("dbo.VOuter");

        // Should have produced success messages about inlining
        wizard.SuccessMessages.ShouldContain(m => m.Contains("Inlined successfully"));
        // Should have created session directory info
        wizard.InfoMessages.ShouldContain(m => m.Contains("Session directory:"));
    }

    [Test]
    public void SessionPicksUpSavedOptionsFromExistingInlinedView()
    {
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VInner"),
            "CREATE VIEW dbo.VInner AS SELECT p.Id, p.Name FROM dbo.People p");
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VOuter"),
            "CREATE VIEW dbo.VOuter AS SELECT i.Id, i.Name FROM dbo.VInner i");

        // Register an existing _Inlined view with metadata containing saved options
        var metadataSql = "/*\n-- Options: StripUnusedColumns=True, StripUnusedJoins=True, AggressiveJoinStripping=True, FlattenDerivedTables=True\n*/\nCREATE OR ALTER VIEW [dbo].[VOuter_Inlined] AS SELECT i.Id FROM dbo.VInner i";
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VOuter_Inlined"),
            metadataSql);

        var wizard = new MockWizard();
        wizard.QueueConfirm(
            true,   // backup confirmation
            false,  // don't open editor
            false   // don't deploy
        );
        wizard.QueueChoose(0); // continue to summary

        var session = new OptimizeSession(connection, wizard, System.IO.Path.GetTempPath());
        session.Run("dbo.VOuter");

        // Should report loaded options
        wizard.InfoMessages.ShouldContain(m => m.Contains("Loaded options from existing"));
        wizard.InfoMessages.ShouldContain(m => m.Contains("AggressiveJoinStripping=True"));
    }

    [Test]
    public void SessionReportsNoNestedViews()
    {
        var connection = new DatabaseConnection();
        connection.AddViewDefinition(
            DatabaseConnection.ToObjectName("dbo", "VSimple"),
            "CREATE VIEW dbo.VSimple AS SELECT p.Id FROM dbo.People p");

        var wizard = new MockWizard();
        wizard.QueueConfirm(
            true,  // backup confirmation
            false  // don't open editor
        );
        wizard.QueueChoose(0); // continue to summary

        var session = new OptimizeSession(connection, wizard, System.IO.Path.GetTempPath());
        session.Run("dbo.VSimple");

        wizard.WarnMessages.ShouldContain(m => m.Contains("nothing to inline"));
    }
}
