using System.Text.Json;
using LoadingAI.Protocol;

namespace LoadingAI.Tests;

/// <summary>Wire shapes added for the single in-chat gate and the change receipt (ADR-0017).</summary>
public class HostProtocolGateTests
{
    [Fact]
    public void ApproveTool_CarriesScopeReasonAndEditedArgs()
    {
        Assert.True(ToHostMessage.TryParse(
            """{"type":"approveTool","toolCallId":"c1","approved":true,"scope":"session"}""", out var m, out _));
        var a = Assert.IsType<ToHostMessage.ApproveTool>(m);
        Assert.True(a.Approved);
        Assert.Equal("session", a.Scope);
        Assert.Null(a.Reason);
        Assert.Null(a.ArgsJson);

        Assert.True(ToHostMessage.TryParse(
            """{"type":"approveTool","toolCallId":"c2","approved":false,"reason":"use level 2"}""", out m, out _));
        Assert.Equal("use level 2", Assert.IsType<ToHostMessage.ApproveTool>(m).Reason);

        Assert.True(ToHostMessage.TryParse(
            """{"type":"approveTool","toolCallId":"c3","approved":false,"args":{"level":2}}""", out m, out _));
        Assert.Equal("""{"level":2}""", Assert.IsType<ToHostMessage.ApproveTool>(m).ArgsJson);
    }

    [Fact]
    public void ApproveTool_AnOldPaneStillWorks()
    {
        Assert.True(ToHostMessage.TryParse("""{"type":"approveTool","toolCallId":"c1","approved":true}""", out var m, out _));
        var a = Assert.IsType<ToHostMessage.ApproveTool>(m);
        Assert.Null(a.Scope);
        Assert.Null(a.ArgsJson);
    }

    [Fact]
    public void Steer_ParsesText()
    {
        Assert.True(ToHostMessage.TryParse("""{"type":"steer","text":"skip doors"}""", out var m, out _));
        Assert.Equal("skip doors", Assert.IsType<ToHostMessage.Steer>(m).Text);
    }

    [Theory]
    [InlineData("selectElements")]
    [InlineData("showElements")]
    [InlineData("isolateElements")]
    public void ElementMessages_ParseNumbersAndNumericStrings_DropJunk(string kind)
    {
        Assert.True(ToHostMessage.TryParse($$"""{"type":"{{kind}}","ids":[12,"34",null,"x",5.5,true,99]}""", out var m, out _));
        var ids = m switch
        {
            ToHostMessage.SelectElements s => s.Ids,
            ToHostMessage.ShowElements s => s.Ids,
            ToHostMessage.IsolateElements s => s.Ids,
            _ => throw new Xunit.Sdk.XunitException("wrong type " + m)
        };
        Assert.Equal(new long[] { 12, 34, 99 }, ids);
    }

    [Fact]
    public void ElementMessages_AreCapped()
    {
        var ids = string.Join(",", Enumerable.Range(1, ToHostMessage.MaxElementIds + 50));
        Assert.True(ToHostMessage.TryParse($$"""{"type":"selectElements","ids":[{{ids}}]}""", out var m, out _));
        Assert.Equal(ToHostMessage.MaxElementIds, Assert.IsType<ToHostMessage.SelectElements>(m).Ids.Count);
    }

    [Fact]
    public void ElementMessages_WithoutIds_AreEmpty()
    {
        Assert.True(ToHostMessage.TryParse("""{"type":"showElements"}""", out var m, out _));
        Assert.Empty(Assert.IsType<ToHostMessage.ShowElements>(m).Ids);
    }

    [Fact]
    public void ToolReview_SerializesCamelCase_AndOmitsNulls()
    {
        var review = new FromHostMessage.ReviewPayload(
            "c1", "delete_elements", "L3", true, "Delete elements.",
            "Delete 3 walls", new[] { new FromHostMessage.PreviewRowPayload("Ids", "3 items") },
            new[] { "Cannot be undone after you save." }, CanAlways: false, CanSession: false, Args: "{}");
        using var doc = JsonDocument.Parse(FromHostMessage.Serialize(new FromHostMessage.ToolReview(review)));
        var root = doc.RootElement;
        Assert.Equal("tool_review", root.GetProperty("type").GetString());
        var r = root.GetProperty("review");
        Assert.Equal("c1", r.GetProperty("toolCallId").GetString());
        Assert.True(r.GetProperty("destructive").GetBoolean());
        Assert.False(r.GetProperty("canAlways").GetBoolean());
        Assert.Equal("3 items", r.GetProperty("rows")[0].GetProperty("detail").GetString());
        Assert.False(r.TryGetProperty("fields", out _));
        Assert.False(r.TryGetProperty("edited", out _));
    }

    [Fact]
    public void TurnLifecycleMessages_Serialize()
    {
        using var start = JsonDocument.Parse(FromHostMessage.Serialize(new FromHostMessage.TurnStart("h1", "build", "autoEdit")));
        Assert.Equal("turn_start", start.RootElement.GetProperty("type").GetString());
        Assert.Equal("autoEdit", start.RootElement.GetProperty("level").GetString());

        using var end = JsonDocument.Parse(FromHostMessage.Serialize(new FromHostMessage.TurnEnd("h1", "done", RolledBack: true)));
        Assert.True(end.RootElement.GetProperty("rolledBack").GetBoolean());

        using var toolEnd = JsonDocument.Parse(FromHostMessage.Serialize(new FromHostMessage.ToolEnd("c1", true, "ran", "Set 3", "Set 3 parameters.", 420)));
        Assert.Equal("tool_end", toolEnd.RootElement.GetProperty("type").GetString());
        Assert.Equal(420, toolEnd.RootElement.GetProperty("ms").GetInt64());
        Assert.False(toolEnd.RootElement.TryGetProperty("partial", out _));
    }

    [Fact]
    public void TurnChanges_SerializesTheCategories()
    {
        var msg = new FromHostMessage.TurnChanges("h1", 2, 1, 0, null, new[]
        {
            new FromHostMessage.CategoryChangesPayload("Walls", 2, 1, 0, new long[] { 5, 6, 7 })
        });
        using var doc = JsonDocument.Parse(FromHostMessage.Serialize(msg));
        var cat = doc.RootElement.GetProperty("byCategory")[0];
        Assert.Equal("Walls", cat.GetProperty("category").GetString());
        Assert.Equal(3, cat.GetProperty("ids").GetArrayLength());
        Assert.False(doc.RootElement.TryGetProperty("idsTruncated", out _));
    }

    [Fact]
    public void SteerAck_DeliveredAtIsAbsentWhenUndelivered()
    {
        using var late = JsonDocument.Parse(FromHostMessage.Serialize(new FromHostMessage.SteerAck("skip doors")));
        Assert.False(late.RootElement.TryGetProperty("deliveredAt", out _));
        using var ok = JsonDocument.Parse(FromHostMessage.Serialize(new FromHostMessage.SteerAck("skip doors", 1730000000000)));
        Assert.Equal(1730000000000, ok.RootElement.GetProperty("deliveredAt").GetInt64());
    }

    [Fact]
    public void HostCapsAndTheme_Serialize()
    {
        using var caps = JsonDocument.Parse(FromHostMessage.Serialize(new FromHostMessage.HostCaps(new[] { "singleGate", "autoEdit" })));
        Assert.Equal("host_caps", caps.RootElement.GetProperty("type").GetString());
        Assert.Equal(2, caps.RootElement.GetProperty("caps").GetArrayLength());

        using var theme = JsonDocument.Parse(FromHostMessage.Serialize(new FromHostMessage.Theme(true)));
        Assert.True(theme.RootElement.GetProperty("dark").GetBoolean());
    }

    [Fact]
    public void KindLists_HaveNoDuplicatesAndTheNewCounts()
    {
        Assert.Equal(25, ToHostKind.All.Count);
        Assert.Equal(ToHostKind.All.Count, ToHostKind.All.Distinct().Count());
        Assert.Equal(33, FromHostKind.All.Count);
        Assert.Equal(FromHostKind.All.Count, FromHostKind.All.Distinct().Count());
    }
}
