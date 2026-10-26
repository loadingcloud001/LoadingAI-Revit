using LoadingAI.Gate;

namespace LoadingAI.Tests;

public class TurnChangeSetTests
{
    [Fact]
    public void Empty_HasNothingToReport()
    {
        var set = new TurnChangeSet();
        Assert.False(set.HasAny);
        Assert.True(set.Snapshot().IsEmpty);
    }

    [Fact]
    public void CountsAddedModifiedAndDeleted_ByCategory()
    {
        var set = new TurnChangeSet();
        set.Added(1, "Walls");
        set.Added(2, "Walls");
        set.Modified(3, "Walls");
        set.Modified(4, "Doors");
        set.Deleted(5, "Doors");

        var snap = set.Snapshot();
        Assert.Equal(2, snap.Added);
        Assert.Equal(2, snap.Modified);
        Assert.Equal(1, snap.Deleted);
        var walls = Assert.Single(snap.ByCategory, c => c.Category == "Walls");
        Assert.Equal((2, 1, 0), (walls.Added, walls.Modified, walls.Deleted));
        var doors = Assert.Single(snap.ByCategory, c => c.Category == "Doors");
        Assert.Equal((0, 1, 1), (doors.Added, doors.Modified, doors.Deleted));
    }

    [Fact]
    public void AddedThenModified_IsStillJustAdded()
    {
        var set = new TurnChangeSet();
        set.Added(10, "Floors");
        set.Modified(10, "Floors");
        set.Modified(10, "Floors");
        var snap = set.Snapshot();
        Assert.Equal(1, snap.Added);
        Assert.Equal(0, snap.Modified);
    }

    [Fact]
    public void ModifiedTwice_CountsOnce()
    {
        var set = new TurnChangeSet();
        set.Modified(7, "Beams");
        set.Modified(7, "Beams");
        Assert.Equal(1, set.Snapshot().Modified);
    }

    [Fact]
    public void AddedThenDeleted_LeavesNoTrace()
    {
        var set = new TurnChangeSet();
        set.Added(20, "Walls");
        set.Modified(20, "Walls");
        set.Deleted(20);
        Assert.False(set.HasAny);
        Assert.True(set.Snapshot().IsEmpty);
    }

    [Fact]
    public void ModifiedThenDeleted_IsDeleted_InItsOwnCategory()
    {
        var set = new TurnChangeSet();
        set.Modified(30, "Columns");
        set.Deleted(30);
        var snap = set.Snapshot();
        Assert.Equal(0, snap.Modified);
        Assert.Equal(1, snap.Deleted);
        Assert.Equal(1, Assert.Single(snap.ByCategory).Deleted);
        Assert.Equal("Columns", snap.ByCategory[0].Category);
    }

    [Fact]
    public void DeletedElementNeverComesBackAsModified()
    {
        var set = new TurnChangeSet();
        set.Deleted(40, "Walls");
        set.Modified(40, "Walls");
        var snap = set.Snapshot();
        Assert.Equal(1, snap.Deleted);
        Assert.Equal(0, snap.Modified);
    }

    [Fact]
    public void ARevitIdReusedAfterADelete_CountsAsANewElement()
    {
        var set = new TurnChangeSet();
        set.Deleted(50, "Walls");
        set.Added(50, "Walls");
        var snap = set.Snapshot();
        Assert.Equal(1, snap.Added);
        Assert.Equal(0, snap.Deleted);
    }

    [Fact]
    public void Ids_AreAddedAndModifiedOnly_Sorted_AndDeletedHaveNone()
    {
        var set = new TurnChangeSet();
        set.Modified(9, "Walls");
        set.Added(3, "Walls");
        set.Deleted(99, "Walls");
        var walls = Assert.Single(set.Snapshot().ByCategory);
        Assert.Equal(new long[] { 3, 9 }, walls.Ids);
    }

    [Fact]
    public void IdsAreCapped_ButCountsStayExact()
    {
        var set = new TurnChangeSet();
        for (long i = 1; i <= 25; i++) set.Added(i, "Walls");
        var snap = set.Snapshot(idCap: 10);
        Assert.Equal(25, snap.Added);
        Assert.True(snap.IdsTruncated);
        Assert.Equal(10, Assert.Single(snap.ByCategory).Ids.Count);
    }

    [Fact]
    public void BlankCategory_GoesToOther_AndOtherSortsLast()
    {
        var set = new TurnChangeSet();
        set.Added(1, null);
        set.Added(2, "  ");
        set.Added(3, "Walls");
        var snap = set.Snapshot();
        Assert.Equal(new[] { "Walls", "Other" }, snap.ByCategory.Select(c => c.Category).ToArray());
        Assert.Equal(2, snap.ByCategory[1].Added);
    }

    [Fact]
    public void BiggestCategoryComesFirst()
    {
        var set = new TurnChangeSet();
        set.Added(1, "Doors");
        for (long i = 10; i < 15; i++) set.Added(i, "Walls");
        Assert.Equal("Walls", set.Snapshot().ByCategory[0].Category);
    }

    [Fact]
    public void Reset_ForgetsEverything()
    {
        var set = new TurnChangeSet();
        set.Added(1, "Walls");
        set.Deleted(2, "Doors");
        set.Reset();
        Assert.False(set.HasAny);
        Assert.True(set.Snapshot().IsEmpty);
    }

    [Fact]
    public void AllIds_ListsEverySelectableIdOnce()
    {
        var set = new TurnChangeSet();
        set.Added(1, "Walls");
        set.Modified(2, "Doors");
        set.Deleted(3, "Doors");
        Assert.Equal(new long[] { 1, 2 }, set.Snapshot().AllIds.OrderBy(x => x).ToArray());
    }
}
