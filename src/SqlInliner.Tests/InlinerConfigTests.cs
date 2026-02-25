using System.IO;
using NUnit.Framework;
using Shouldly;

namespace SqlInliner.Tests;

public class InlinerConfigTests
{
    private string tempDir;

    [SetUp]
    public void Setup()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "sqlinliner-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, true);
    }

    [Test]
    public void Load_FullConfig_AllFieldsParsed()
    {
        var configPath = Path.Combine(tempDir, "sqlinliner.json");
        File.WriteAllText(configPath, """
        {
            "connectionString": "Server=.;Database=Test",
            "stripUnusedColumns": false,
            "stripUnusedJoins": true,
            "aggressiveJoinStripping": true,
            "generateCreateOrAlter": false,
            "views": {
                "dbo.VPeople": "VPeople.sql"
            }
        }
        """);

        var config = InlinerConfig.Load(configPath);

        config.ConnectionString.ShouldBe("Server=.;Database=Test");
        config.StripUnusedColumns.ShouldBe(false);
        config.StripUnusedJoins.ShouldBe(true);
        config.AggressiveJoinStripping.ShouldBe(true);
        config.GenerateCreateOrAlter.ShouldBe(false);
        config.Views.ShouldNotBeNull();
        config.Views!.Count.ShouldBe(1);
        config.Views["dbo.VPeople"].ShouldBe("VPeople.sql");
    }

    [Test]
    public void Load_PartialConfig_UnsetFieldsAreNull()
    {
        var configPath = Path.Combine(tempDir, "sqlinliner.json");
        File.WriteAllText(configPath, """
        {
            "connectionString": "Server=.;Database=Test"
        }
        """);

        var config = InlinerConfig.Load(configPath);

        config.ConnectionString.ShouldBe("Server=.;Database=Test");
        config.StripUnusedColumns.ShouldBeNull();
        config.StripUnusedJoins.ShouldBeNull();
        config.AggressiveJoinStripping.ShouldBeNull();
        config.GenerateCreateOrAlter.ShouldBeNull();
        config.Views.ShouldBeNull();
    }

    [Test]
    public void Load_ViewsOnly_NoConnectionString()
    {
        var configPath = Path.Combine(tempDir, "sqlinliner.json");
        File.WriteAllText(configPath, """
        {
            "views": {
                "dbo.VPeople": "VPeople.sql",
                "dbo.VOrders": "nested/VOrders.sql"
            }
        }
        """);

        var config = InlinerConfig.Load(configPath);

        config.ConnectionString.ShouldBeNull();
        config.Views.ShouldNotBeNull();
        config.Views!.Count.ShouldBe(2);
    }

    [Test]
    public void Load_EmptyConfig_AllFieldsNull()
    {
        var configPath = Path.Combine(tempDir, "sqlinliner.json");
        File.WriteAllText(configPath, "{}");

        var config = InlinerConfig.Load(configPath);

        config.ConnectionString.ShouldBeNull();
        config.StripUnusedColumns.ShouldBeNull();
        config.Views.ShouldBeNull();
    }

    [Test]
    public void Load_TrailingCommasAllowed()
    {
        var configPath = Path.Combine(tempDir, "sqlinliner.json");
        File.WriteAllText(configPath, """
        {
            "connectionString": "Server=.",
            "stripUnusedJoins": true,
        }
        """);

        var config = InlinerConfig.Load(configPath);

        config.ConnectionString.ShouldBe("Server=.");
        config.StripUnusedJoins.ShouldBe(true);
    }

    [Test]
    public void Load_CommentsAllowed()
    {
        var configPath = Path.Combine(tempDir, "sqlinliner.json");
        File.WriteAllText(configPath, """
        {
            // This is a comment
            "connectionString": "Server=."
        }
        """);

        var config = InlinerConfig.Load(configPath);

        config.ConnectionString.ShouldBe("Server=.");
    }

    [Test]
    public void Load_BaseDirectorySetToConfigFileDirectory()
    {
        var subDir = Path.Combine(tempDir, "subdir");
        Directory.CreateDirectory(subDir);
        var configPath = Path.Combine(subDir, "sqlinliner.json");
        File.WriteAllText(configPath, "{}");

        var config = InlinerConfig.Load(configPath);

        config.BaseDirectory.ShouldBe(subDir);
    }

    [Test]
    public void TryLoad_ExplicitPath_LoadsConfig()
    {
        var configPath = Path.Combine(tempDir, "custom.json");
        File.WriteAllText(configPath, """{ "connectionString": "Server=." }""");

        var config = InlinerConfig.TryLoad(configPath);

        config.ShouldNotBeNull();
        config!.ConnectionString.ShouldBe("Server=.");
    }

    [Test]
    public void TryLoad_NoPathNoDefault_ReturnsNull()
    {
        // Use a directory that definitely doesn't contain sqlinliner.json
        var oldDir = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(tempDir);
            var config = InlinerConfig.TryLoad(null);
            config.ShouldBeNull();
        }
        finally
        {
            Directory.SetCurrentDirectory(oldDir);
        }
    }

    [Test]
    public void RegisterViews_LoadsFilesAndRegistersWithConnection()
    {
        // Create view SQL files
        File.WriteAllText(Path.Combine(tempDir, "VPeople.sql"),
            "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.FirstName FROM dbo.People p");

        var subDir = Path.Combine(tempDir, "nested");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "VOrders.sql"),
            "CREATE VIEW dbo.VOrders AS SELECT o.Id, o.Total FROM dbo.Orders o");

        var configPath = Path.Combine(tempDir, "sqlinliner.json");
        File.WriteAllText(configPath, """
        {
            "views": {
                "dbo.VPeople": "VPeople.sql",
                "dbo.VOrders": "nested/VOrders.sql"
            }
        }
        """);

        var config = InlinerConfig.Load(configPath);
        var connection = new DatabaseConnection();
        config.RegisterViews(connection);

        connection.Views.Count.ShouldBe(2);
        connection.IsView(DatabaseConnection.ToObjectName("dbo", "VPeople")).ShouldBeTrue();
        connection.IsView(DatabaseConnection.ToObjectName("dbo", "VOrders")).ShouldBeTrue();
    }

    [Test]
    public void RegisterViews_NullViews_DoesNothing()
    {
        var configPath = Path.Combine(tempDir, "sqlinliner.json");
        File.WriteAllText(configPath, "{}");

        var config = InlinerConfig.Load(configPath);
        var connection = new DatabaseConnection();
        config.RegisterViews(connection);

        connection.Views.Count.ShouldBe(0);
    }

    [Test]
    public void ViewsFromConfig_CanBeInlined()
    {
        // Register a nested view via config, then inline a view that references it
        File.WriteAllText(Path.Combine(tempDir, "VPeople.sql"),
            "CREATE VIEW dbo.VPeople AS SELECT p.Id, p.FirstName, p.LastName FROM dbo.People p");

        var configPath = Path.Combine(tempDir, "sqlinliner.json");
        File.WriteAllText(configPath, """
        {
            "views": {
                "dbo.VPeople": "VPeople.sql"
            }
        }
        """);

        var config = InlinerConfig.Load(configPath);
        var connection = new DatabaseConnection();
        config.RegisterViews(connection);

        const string outerViewSql = "CREATE VIEW dbo.VActivePeople AS SELECT p.Id, p.FirstName FROM dbo.VPeople p";
        var inliner = new DatabaseViewInliner(connection, outerViewSql, InlinerOptions.Recommended());

        inliner.Errors.Count.ShouldBe(0);
        var result = inliner.Result;
        result.ShouldNotBeNull();
        result.ConvertedSql.ShouldContain("dbo.People");
        result.ConvertedSql.ShouldNotContain("dbo.VPeople");
    }
}
