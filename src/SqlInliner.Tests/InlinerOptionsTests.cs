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
}