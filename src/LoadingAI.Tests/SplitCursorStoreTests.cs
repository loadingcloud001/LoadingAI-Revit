using System;
using System.Collections.Generic;
using System.Linq;
using LoadingAI.Revit.StructureCuts;

namespace LoadingAI.Tests;

public class SplitCursorStoreTests
{
    private static List<string> Ids(int n) => Enumerable.Range(1, n).Select(i => i.ToString()).ToList();

    [Fact]
    public void ThePageIsASliceOfTheCollectedList()
    {
        var page = new SplitCursorStore().Page("k", 0, 5, () => Ids(12));

        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, page.Ids);
        Assert.Equal(0, page.Offset);
        Assert.Equal(12, page.Total);
        Assert.False(page.Reused);
    }

    [Fact]
    public void TheNextCallPagesThroughTheFrozenList_EvenWhenTheModelChangedUnderIt()
    {
        var store = new SplitCursorStore();
        store.Page("k", 0, 5, () => Ids(12));

        // After a write run five hosts were re-created. A list collected afresh would now be shorter in
        // front, and an offset of 5 into it would skip five hosts that were never processed.
        var collectedAgain = 0;
        var page = store.Page("k", 5, 5, () => { collectedAgain++; return Ids(7); });

        Assert.Equal(new[] { "6", "7", "8", "9", "10" }, page.Ids);
        Assert.Equal(0, collectedAgain);
        Assert.True(page.Reused);
        Assert.Equal(12, page.Total);
    }

    [Fact]
    public void OffsetZeroAlwaysStartsAFreshList()
    {
        var store = new SplitCursorStore();
        store.Page("k", 0, 5, () => Ids(12));

        var page = store.Page("k", 0, 5, () => Ids(20));

        Assert.Equal(20, page.Total);
        Assert.False(page.Reused);
    }

    [Fact]
    public void AnOffsetWithNoListCollectsOne()
    {
        var page = new SplitCursorStore().Page("k", 5, 5, () => Ids(12));

        Assert.Equal(5, page.Offset);
        Assert.Equal(new[] { "6", "7", "8", "9", "10" }, page.Ids);
        Assert.False(page.Reused);
    }

    [Fact]
    public void AnOffsetPastTheEndGivesAnEmptyPageAtTheEnd()
    {
        var store = new SplitCursorStore();
        store.Page("k", 0, 5, () => Ids(12));

        var page = store.Page("k", 99, 5, () => Ids(12));

        Assert.Empty(page.Ids);
        Assert.Equal(12, page.Offset);
    }

    [Fact]
    public void ALimitOfZeroOrLessGivesAnEmptyPage()
        => Assert.Empty(new SplitCursorStore().Page("k", 0, 0, () => Ids(3)).Ids);

    [Fact]
    public void ADifferentCommandScopeDocumentOrIdListHasItsOwnKey()
    {
        var none = new string[0];
        var baseKey = SplitCursorStore.KeyFor("split_by_levels", "m.rvt", "all", none);
        Assert.NotEqual(baseKey, SplitCursorStore.KeyFor("split_by_grids", "m.rvt", "all", none));
        Assert.NotEqual(baseKey, SplitCursorStore.KeyFor("split_by_levels", "n.rvt", "all", none));
        Assert.NotEqual(baseKey, SplitCursorStore.KeyFor("split_by_levels", "m.rvt", "selection", none));
        Assert.NotEqual(baseKey, SplitCursorStore.KeyFor("split_by_levels", "m.rvt", "all", new[] { "1" }));
        Assert.Equal(baseKey, SplitCursorStore.KeyFor("split_by_levels", "m.rvt", "all", none));
    }

    [Fact]
    public void AListExpiresAfterItsLifetime_AndUsingItKeepsItAlive()
    {
        var now = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        var store = new SplitCursorStore(() => now, TimeSpan.FromMinutes(30));
        store.Page("k", 0, 5, () => Ids(12));

        now = now.AddMinutes(20);
        Assert.True(store.Page("k", 5, 5, () => Ids(12)).Reused);

        now = now.AddMinutes(20);
        Assert.True(store.Page("k", 10, 5, () => Ids(12)).Reused);

        now = now.AddMinutes(31);
        var collected = 0;
        var page = store.Page("k", 5, 5, () => { collected++; return Ids(9); });
        Assert.Equal(1, collected);
        Assert.False(page.Reused);
        Assert.Equal(9, page.Total);
    }

    [Fact]
    public void TheOldestListsGoFirstWhenTheStoreIsFull()
    {
        var now = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        var store = new SplitCursorStore(() => now, TimeSpan.FromMinutes(30), capacity: 2);
        store.Page("a", 0, 1, () => Ids(3));
        now = now.AddSeconds(1);
        store.Page("b", 0, 1, () => Ids(3));
        now = now.AddSeconds(1);
        store.Page("c", 0, 1, () => Ids(3));

        Assert.True(store.Page("c", 1, 1, () => Ids(3)).Reused);
        Assert.False(store.Page("a", 1, 1, () => Ids(3)).Reused);
    }
}

public class SplitForecastTests
{
    [Fact]
    public void NothingToSplitSaysNothing()
        => Assert.Equal("", SplitForecast.Note(0, 0, 0));

    [Fact]
    public void InPlaceOnly_DoesNotWarnAboutDependents()
        => Assert.Equal(" In place: 5, re-created: 0.", SplitForecast.Note(5, 0, 0));

    [Fact]
    public void ReCreatedMembers_NameWhatTheyTakeWithThem()
    {
        var note = SplitForecast.Note(3, 2, 7);

        Assert.Contains("In place: 3", note);
        Assert.Contains("re-created: 2", note);
        Assert.Contains("deletes 7 dependent element(s)", note);
    }

    [Fact]
    public void ReCreatedWithNoDependents_AddsNoWarning()
        => Assert.Equal(" In place: 0, re-created: 3.", SplitForecast.Note(0, 3, 0));
}
