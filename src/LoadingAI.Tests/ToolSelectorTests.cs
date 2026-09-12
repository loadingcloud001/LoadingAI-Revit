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

    [Fact]
    public void Delete_KeepsDeleteElements()
    {
        var tools = new List<ToolDefinition> { T("delete_elements", "Delete elements by Id") };
        for (int i = 0; i < 40; i++) tools.Add(T($"noise_{i}", "noise"));
        var selected = ToolSelector.Select(tools, "delete the selection", 8);
        Assert.Contains(selected, t => t.Function.Name == "delete_elements");
    }

    [Fact]
    public void Save_KeepsSaveDocument()
    {
        var tools = new List<ToolDefinition> { T("save_document", "Save the Revit document") };
        for (int i = 0; i < 40; i++) tools.Add(T($"noise_{i}", "noise"));
        var selected = ToolSelector.Select(tools, "save the document", 8);
        Assert.Contains(selected, t => t.Function.Name == "save_document");
    }

    [Fact]
    public void Move_KeepsMoveElements()
    {
        var tools = new List<ToolDefinition> { T("move_elements", "Move elements by vector (mm)") };
        for (int i = 0; i < 40; i++) tools.Add(T($"noise_{i}", "noise"));
        var selected = ToolSelector.Select(tools, "move the selected walls 300mm along Y", 8);
        Assert.Contains(selected, t => t.Function.Name == "move_elements");
    }

    [Fact]
    public void Isolate_KeepsIsolateInView()
    {
        var tools = new List<ToolDefinition> { T("isolate_in_view", "Temporarily isolate elements") };
        for (int i = 0; i < 40; i++) tools.Add(T($"noise_{i}", "noise"));
        var selected = ToolSelector.Select(tools, "isolate these", 8);
        Assert.Contains(selected, t => t.Function.Name == "isolate_in_view");
    }

    [Fact]
    public void Rooms_KeepsGetRooms()
    {
        var tools = new List<ToolDefinition> { T("get_rooms", "List rooms with area") };
        for (int i = 0; i < 40; i++) tools.Add(T($"noise_{i}", "noise"));
        var selected = ToolSelector.Select(tools, "what rooms are there", 8);
        Assert.Contains(selected, t => t.Function.Name == "get_rooms");
    }

    [Fact]
    public void ColorByParam_KeepsColorByParameter()
    {
        var tools = new List<ToolDefinition> { T("color_by_parameter", "Color elements by any parameter") };
        for (int i = 0; i < 40; i++) tools.Add(T($"noise_{i}", "noise"));
        var selected = ToolSelector.Select(tools, "color by Mark", 8);
        Assert.Contains(selected, t => t.Function.Name == "color_by_parameter");
    }
}
