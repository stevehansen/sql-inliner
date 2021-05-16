using System.Linq;
using NUnit.Framework;

namespace SqlInliner.Tests
{
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
            Assert.AreEqual(0, inliner.Errors.Count);
            Assert.AreNotEqual(viewSql, inliner.Sql);

            var result = inliner.Result;
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Sql.Contains("dbo.People"));
            Assert.IsTrue(result.ConvertedSql.Contains("dbo.People"));
            Assert.IsFalse(result.ConvertedSql.Contains("dbo.VPeople"));
            Assert.IsFalse(result.ConvertedSql.Contains("UnusedFunction"));
        }

        [Test]
        public void InlineSimpleViewWithAliases()
        {
            const string viewSql = "CREATE OR ALTER VIEW dbo.VActivePeople AS SELECT p.Id, p.FName, p.LName FROM dbo.VPeopleWithAliases p WHERE p.ActiveInd = 1";

            var inliner = new DatabaseViewInliner(connection, viewSql, options);
            Assert.AreEqual(0, inliner.Errors.Count);
            Assert.AreNotEqual(viewSql, inliner.Sql);

            var result = inliner.Result;
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Sql.Contains("dbo.People"));
            Assert.IsTrue(result.ConvertedSql.Contains("dbo.People"));
            Assert.IsFalse(result.ConvertedSql.Contains("dbo.VPeopleWithAliases"));
            Assert.IsFalse(result.ConvertedSql.Contains("UnusedFunction"));
            Assert.IsFalse(result.ConvertedSql.Contains("dbo.UnusedTable"));
        }

        [Test]
        public void InlineSimpleViewWithColumnAliases()
        {
            const string viewSql = "CREATE VIEW dbo.VActivePeople AS SELECT p.Id, p.FirstName FName, p.LastName LName FROM dbo.VPeople p WHERE p.IsActive = 1";

            var inliner = new DatabaseViewInliner(connection, viewSql, options);
            Assert.AreEqual(0, inliner.Errors.Count);
            Assert.AreNotEqual(viewSql, inliner.Sql);

            var result = inliner.Result;
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Sql.Contains("dbo.People"));
            Assert.IsTrue(result.ConvertedSql.Contains("dbo.People"));
            Assert.IsFalse(result.ConvertedSql.Contains("dbo.VPeople"));
            Assert.IsFalse(result.ConvertedSql.Contains("UnusedFunction"));
            Assert.IsTrue(result.ConvertedSql.Contains("p.FirstName AS FName"));
        }

        [Test]
        public void InlineSimpleViewWithAliasesWithColumnAliases()
        {
            const string viewSql = "CREATE VIEW dbo.VActivePeople AS SELECT ap.Id, ap.FName PersonFirstName, ap.LName PersonLastName FROM dbo.VPeopleWithAliases ap WHERE ap.ActiveInd = 1";

            var inliner = new DatabaseViewInliner(connection, viewSql, options);
            Assert.AreEqual(0, inliner.Errors.Count);
            Assert.AreNotEqual(viewSql, inliner.Sql);

            var result = inliner.Result;
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Sql.Contains("dbo.People"));
            Assert.IsTrue(result.ConvertedSql.Contains("dbo.People"));
            Assert.IsFalse(result.ConvertedSql.Contains("dbo.VPeopleWithAliases"));
            Assert.IsFalse(result.ConvertedSql.Contains("UnusedFunction"));
            Assert.IsTrue(result.ConvertedSql.Contains("ap.FName AS PersonFirstName"));
        }

        [Test]
        public void InlineSimpleViewWithRemovedColumns()
        {
            const string viewSql = "CREATE VIEW dbo.VActivePeople AS SELECT p.Id, p.FirstName FROM dbo.VPeople p WHERE p.IsActive = 1";

            var inliner = new DatabaseViewInliner(connection, DatabaseView.CreateOrAlter(viewSql), options);
            Assert.AreEqual(0, inliner.Errors.Count);
            Assert.AreNotEqual(viewSql, inliner.Sql);

            var result = inliner.Result;
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Sql.Contains("dbo.People"));
            Assert.IsTrue(result.ConvertedSql.Contains("dbo.People"));
            Assert.IsFalse(result.ConvertedSql.Contains("dbo.VPeople"));
            Assert.IsFalse(result.ConvertedSql.Contains("UnusedFunction"));
            Assert.IsFalse(result.ConvertedSql.Contains("LastName"));
        }

        [Test]
        public void InlineNestedView()
        {
            const string viewSql = "CREATE VIEW dbo.VActivePeople AS SELECT p.Id, p.FirstName, p.LastName FROM dbo.VNestedPeople p WHERE p.IsActive = 1";

            var inliner = new DatabaseViewInliner(connection, viewSql, options);
            Assert.AreEqual(0, inliner.Errors.Count);
            Assert.AreNotEqual(viewSql, inliner.Sql);

            var result = inliner.Result;
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Sql.Contains("dbo.People"));
            Assert.IsTrue(result.ConvertedSql.Contains("dbo.People"));
            Assert.IsFalse(result.ConvertedSql.Contains("dbo.VNestedPeople"));
            Assert.IsFalse(result.ConvertedSql.Contains("UnusedFunction"));
            Assert.IsFalse(result.ConvertedSql.Contains("p2"));
        }

        [Test]
        public void InlineNestedViewKeepUnusedJoins()
        {
            const string viewSql = "CREATE OR ALTER VIEW dbo.VActivePeople AS SELECT p.Id, p.FirstName, p.LastName FROM dbo.VNestedPeople p WHERE p.IsActive = 1";

            var inliner = new DatabaseViewInliner(connection, viewSql);
            Assert.AreEqual(0, inliner.Errors.Count);
            Assert.AreNotEqual(viewSql, inliner.Sql);

            var result = inliner.Result;
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Sql.Contains("dbo.People"));
            Assert.IsTrue(result.ConvertedSql.Contains("dbo.People"));
            Assert.IsFalse(result.ConvertedSql.Contains("dbo.VNestedPeople"));
            Assert.IsFalse(result.ConvertedSql.Contains("UnusedFunction"));
            Assert.IsTrue(result.ConvertedSql.Contains("p2"));
        }

        [Test]
        public void WarningForSinglePartIdentifiers()
        {
            const string viewSql = "CREATE OR ALTER VIEW dbo.VActivePeople AS SELECT Id, FirstName, LastName FROM dbo.VNestedPeople";

            var inliner = new DatabaseViewInliner(connection, viewSql, options);
            Assert.AreEqual(0, inliner.Errors.Count);
            Assert.AreNotEqual(0, inliner.Warnings.Count);
            Assert.AreNotEqual(viewSql, inliner.Sql);
        }

        [Test]
        public void CountColumnReferences()
        {
            const string viewSql = "CREATE VIEW dbo.VActivePeople AS SELECT p.Id, p.FirstName, p.LastName, 'hardcoded' Ignored FROM dbo.VPeople p WHERE p.IsActive = 1";

            var (view, errors) = DatabaseView.FromSql(connection, viewSql);
            Assert.AreEqual(0, errors.Count);
            Assert.IsNotNull(view);

            Assert.AreEqual(4, view.References.ColumnReferences.Count);
        }

        [Test]
        public void CountColumnReferencesSkipParametersToIgnore()
        {
            const string viewSql = "CREATE VIEW dbo.VActivePeople AS SELECT CONVERT(varchar, p.Id) Id, dateadd(day, 1, p.DayOfBirth) DayOfBirth, CAST(10.5 AS INT) Number FROM dbo.VPeople p";

            var (view, errors) = DatabaseView.FromSql(connection, viewSql);
            Assert.AreEqual(0, errors.Count);
            Assert.IsNotNull(view);

            Assert.AreEqual(2, view.References.ColumnReferences.Count(r => r.MultiPartIdentifier[0].Value == "p"));
            Assert.AreEqual(2, view.References.ColumnReferences.Count);
        }
    }
}