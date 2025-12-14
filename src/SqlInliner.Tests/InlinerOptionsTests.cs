using NUnit.Framework;

namespace SqlInliner.Tests;

public class InlinerOptionsTests
{
    [Test]
    public void DefaultOptions_StripUnusedColumnsIsTrue()
    {
        var options = new InlinerOptions();
        Assert.IsTrue(options.StripUnusedColumns);
    }

    [Test]
    public void DefaultOptions_StripUnusedJoinsIsFalse()
    {
        var options = new InlinerOptions();
        Assert.IsFalse(options.StripUnusedJoins);
    }

    [Test]
    public void RecommendedOptions_BothOptionsEnabled()
    {
        var options = InlinerOptions.Recommended();
        Assert.IsTrue(options.StripUnusedColumns);
        Assert.IsTrue(options.StripUnusedJoins);
    }

    [Test]
    public void SetStripUnusedColumns_CanBeDisabled()
    {
        var options = new InlinerOptions { StripUnusedColumns = false };
        Assert.IsFalse(options.StripUnusedColumns);
    }

    [Test]
    public void SetStripUnusedJoins_CanBeEnabled()
    {
        var options = new InlinerOptions { StripUnusedJoins = true };
        Assert.IsTrue(options.StripUnusedJoins);
    }
}
