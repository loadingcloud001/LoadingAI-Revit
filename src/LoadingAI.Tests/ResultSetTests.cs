using System.Text.Json;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class ResultSetTests
{
    [Fact]
    public void Save_NumbersSets_AndCapsCount()
    {
        var s = new ResultSetStore();
        for (int i = 0; i < 22; i++) s.Save(new[] { i.ToString() });
        Assert.Equal(ResultSetStore.MaxSets, s.Handles.Count);
        Assert.False(s.TryGet("@r1", out _));
        Assert.True(s.TryGet("@r22", out var ids));
        Assert.Equal(new[] { "21" }, ids);
    }

    [Fact]
    public void Save_CapsTotalIds_DroppingOldest()
    {
        var s = new ResultSetStore();
        s.Save(Enumerable.Range(0, 30000).Select(i => i.ToString()).ToList());
        s.Save(Enumerable.Range(0, 30000).Select(i => i.ToString()).ToList());
        Assert.False(s.TryGet("@r1", out _));
        Assert.True(s.TryGet("@r2", out _));
    }

    [Fact]
    public void Save_EmptyReturnsNull() => Assert.Null(new ResultSetStore().Save(Array.Empty<string>()));

    [Theory]
    [InlineData("{\"elementIds\":\"@r1\"}")]
    [InlineData("{\"elementIds\":[\"@r1\"]}")]
    public void Expand_ArrayOrString(string args)
    {
        var s = new ResultSetStore();
        s.Save(new[] { "7", "8" });
        Assert.True(ResultSetExpander.TryExpand(args, s, out var outJson, out var err), err);
        using var d = JsonDocument.Parse(outJson);
        Assert.Equal(new[] { "7", "8" }, d.RootElement.GetProperty("elementIds").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public void Expand_MixedArray_KeepsLiteralIds()
    {
        var s = new ResultSetStore();
        s.Save(new[] { "7", "8" });
        ResultSetExpander.TryExpand("{\"elementIds\":[\"5\",\"@r1\",\"7\"]}", s, out var outJson, out _);
        Assert.Equal("{\"elementIds\":[\"5\",\"7\",\"8\"]}", outJson);
    }

    [Fact]
    public void Expand_SingleElementId_OnlyFromAOneIdSet()
    {
        var s = new ResultSetStore();
        s.Save(new[] { "9" });
        s.Save(new[] { "1", "2" });
        Assert.True(ResultSetExpander.TryExpand("{\"elementId\":\"@r1\"}", s, out var ok, out _));
        Assert.Equal("{\"elementId\":\"9\"}", ok);
        Assert.False(ResultSetExpander.TryExpand("{\"elementId\":\"@r2\"}", s, out _, out var err));
        Assert.Contains("2 elements", err);
    }

    [Fact]
    public void Expand_UnknownHandle_NamesTheSavedSets()
    {
        var s = new ResultSetStore();
        s.Save(new[] { "1" });
        Assert.False(ResultSetExpander.TryExpand("{\"elementIds\":\"@r9\"}", s, out _, out var err));
        Assert.Contains("@r9", err);
        Assert.Contains("@r1 (1)", err);
    }

    [Fact]
    public void Expand_NoHandles_ReturnsInputUnchanged()
    {
        const string args = "{\"elementIds\":[\"1\"],\"comment\":\"@r1 is text\"}";
        Assert.True(ResultSetExpander.TryExpand(args, new ResultSetStore(), out var outJson, out _));
        Assert.Equal(args, outJson);
    }
}
