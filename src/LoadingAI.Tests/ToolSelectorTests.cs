using LoadingAI.Llm;

namespace LoadingAI.Tests;

public class ToolSelectorTests
{
    private static ToolDefinition T(string name, string desc = "") => new()
    {
        Function = new ToolFunction { Name = name, Description = desc }
    };

    [Fact]
    public void UnderLimit_ReturnsAll()
    {
        var tools = Enumerable.Range(0, 10).Select(i => T($"tool_{i}")).ToList();
        Assert.Equal(10, ToolSelector.Select(tools, "hello").Count);
    }

    [Fact]
    public void OverLimit_KeepsRelevantAndCore()
    {
        var tools = new List<ToolDefinition> { T("get_model_summary"), T("zzz_unrelated", "nothing") };
        for (int i = 0; i < 40; i++) tools.Add(T($"noise_{i}", "noise"));
        tools.Add(T("create_wall", "Create a wall"));
        var selected = ToolSelector.Select(tools, "please create a wall", 8);
        Assert.True(selected.Count <= 8);
        Assert.Contains(selected, t => t.Function.Name == "get_model_summary");
        Assert.Contains(selected, t => t.Function.Name == "create_wall");
    }

    [Fact]
    public void Score_FullNameBeatsUnrelated()
    {
        var wall = T("create_wall", "Create wall");
        var noise = T("zzz_unrelated", "nothing");
        Assert.True(ToolSelector.Score(wall, "create wall") > ToolSelector.Score(noise, "create wall"));
    }
}
