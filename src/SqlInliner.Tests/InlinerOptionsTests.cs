using NUnit.Framework;
using Shouldly;

namespace SqlInliner.Tests;

public class InlinerOptionsTests
{
    [Test]
    public void DefaultOptions_StripUnusedColumnsIsTrue()
    {
        var options = new InlinerOptions();
        options.StripUnusedColumns.ShouldBeTrue();
    }

    [Test]
    public void DefaultOptions_StripUnusedJoinsIsFalse()
    {
        var options = new InlinerOptions();
        options.StripUnusedJoins.ShouldBeFalse();
    }

    [Test]
    public void RecommendedOptions_BothOptionsEnabled()
    {
        var options = InlinerOptions.Recommended();
        options.StripUnusedColumns.ShouldBeTrue();
        options.StripUnusedJoins.ShouldBeTrue();
    }

    [Test]
    public void SetStripUnusedColumns_CanBeDisabled()
    {
        var options = new InlinerOptions { StripUnusedColumns = false };
        options.StripUnusedColumns.ShouldBeFalse();
    }

    [Test]
    public void SetStripUnusedJoins_CanBeEnabled()
    {
        var options = new InlinerOptions { StripUnusedJoins = true };
        options.StripUnusedJoins.ShouldBeTrue();
    }

    [Test]
    public void ToMetadataString_ProducesExpectedFormat()
    {
        var options = new InlinerOptions
        {
            StripUnusedColumns = true,
            StripUnusedJoins = true,
            AggressiveJoinStripping = false,
        };
        options.ToMetadataString().ShouldBe("StripUnusedColumns=True, StripUnusedJoins=True, AggressiveJoinStripping=False");
    }

    [Test]
    public void TryParseFromMetadata_ParsesValidOptionsLine()
    {
        const string sql = "/*\n-- Options: StripUnusedColumns=True, StripUnusedJoins=True, AggressiveJoinStripping=True\n*/\nCREATE VIEW dbo.V AS SELECT 1";

        var options = InlinerOptions.TryParseFromMetadata(sql);

        options.ShouldNotBeNull();
        options.StripUnusedColumns.ShouldBeTrue();
        options.StripUnusedJoins.ShouldBeTrue();
        options.AggressiveJoinStripping.ShouldBeTrue();
    }

    [Test]
    public void TryParseFromMetadata_ReturnsNullWhenNoOptionsLine()
    {
        const string sql = "CREATE VIEW dbo.V AS SELECT 1";

        InlinerOptions.TryParseFromMetadata(sql).ShouldBeNull();
    }

    [Test]
    public void TryParseFromMetadata_IgnoresUnknownKeys()
    {
        const string sql = "/*\n-- Options: StripUnusedColumns=False, FutureOption=True, StripUnusedJoins=True\n*/\nCREATE VIEW dbo.V AS SELECT 1";

        var options = InlinerOptions.TryParseFromMetadata(sql);

        options.ShouldNotBeNull();
        options.StripUnusedColumns.ShouldBeFalse();
        options.StripUnusedJoins.ShouldBeTrue();
    }

    [Test]
    public void TryParseFromMetadata_RoundTrip()
    {
        var original = new InlinerOptions
        {
            StripUnusedColumns = false,
            StripUnusedJoins = true,
            AggressiveJoinStripping = true,
        };

        var sql = $"/*\n-- Options: {original.ToMetadataString()}\n*/\nCREATE VIEW dbo.V AS SELECT 1";
        var parsed = InlinerOptions.TryParseFromMetadata(sql);

        parsed.ShouldNotBeNull();
        parsed.StripUnusedColumns.ShouldBe(original.StripUnusedColumns);
        parsed.StripUnusedJoins.ShouldBe(original.StripUnusedJoins);
        parsed.AggressiveJoinStripping.ShouldBe(original.AggressiveJoinStripping);
    }
}