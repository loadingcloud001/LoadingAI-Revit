using System.Linq;
using LoadingAI.Revit;

namespace LoadingAI.Tests;

public class ViewOverrideLedgerTests
{
    [Fact]
    public void TheFirstValueWins_SoColouringTwiceStillRestoresTheOriginal()
    {
        var ledger = new ViewOverrideLedger<string>();
        ledger.Remember("7", ViewOverrideKey.Element("100"), "original");
        ledger.Remember("7", ViewOverrideKey.Element("100"), "red");

        var entry = Assert.Single(ledger.Snapshot("7"));
        Assert.Equal("e:100", entry.Key);
        Assert.Equal("original", entry.Value);
    }

    [Fact]
    public void AnElementAndACategoryWithTheSameNumberDoNotCollide()
    {
        var ledger = new ViewOverrideLedger<string>();
        ledger.Remember("7", ViewOverrideKey.Element("5"), "element");
        ledger.Remember("7", ViewOverrideKey.Category("5"), "category");

        Assert.Equal(2, ledger.Count("7"));
        Assert.True(ViewOverrideKey.IsCategory(ViewOverrideKey.Category("5")));
        Assert.False(ViewOverrideKey.IsCategory(ViewOverrideKey.Element("5")));
        Assert.Equal("5", ViewOverrideKey.IdOf(ViewOverrideKey.Category("5")));
    }

    [Fact]
    public void ViewsAreKeptApart_AndForgetDropsOnlyTheNamedOne()
    {
        var ledger = new ViewOverrideLedger<string>();
        ledger.Remember("7", ViewOverrideKey.Element("1"), "a");
        ledger.Remember("8", ViewOverrideKey.Element("1"), "b");

        ledger.Forget("7");

        Assert.Equal(0, ledger.Count("7"));
        Assert.Equal(1, ledger.Count("8"));
    }

    [Fact]
    public void TryTake_RemovesTheEntryAndSaysWhenThereWasNone()
    {
        var ledger = new ViewOverrideLedger<string>();
        ledger.Remember("7", ViewOverrideKey.Element("1"), "a");

        Assert.True(ledger.TryTake("7", ViewOverrideKey.Element("1"), out var before));
        Assert.Equal("a", before);
        Assert.False(ledger.TryTake("7", ViewOverrideKey.Element("1"), out _));
        Assert.Equal(0, ledger.Count("7"));
    }

    [Fact]
    public void ASnapshotIsACopy()
    {
        var ledger = new ViewOverrideLedger<string>();
        ledger.Remember("7", ViewOverrideKey.Element("1"), "a");
        var snapshot = ledger.Snapshot("7");

        ledger.Remember("7", ViewOverrideKey.Element("2"), "b");

        Assert.Single(snapshot);
        Assert.Equal(2, ledger.Snapshot("7").Count);
    }
}
