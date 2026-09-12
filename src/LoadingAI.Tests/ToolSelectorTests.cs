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
    public void ChineseDelete_KeepsDeleteElements()
    {
        var tools = new List<ToolDefinition> { T("delete_elements", "Delete elements by Id") };
        for (int i = 0; i < 40; i++) tools.Add(T($"noise_{i}", "noise"));
        var selected = ToolSelector.Select(tools, "刪掉選取", 8);
        Assert.Contains(selected, t => t.Function.Name == "delete_elements");
    }

    [Fact]
    public void ChineseSave_KeepsSaveDocument()
    {
        var tools = new List<ToolDefinition> { T("save_document", "Save the Revit document") };
        for (int i = 0; i < 40; i++) tools.Add(T($"noise_{i}", "noise"));
        var selected = ToolSelector.Select(tools, "存檔", 8);
        Assert.Contains(selected, t => t.Function.Name == "save_document");
    }

    [Fact]
    public void ChineseMove_KeepsMoveElements()
    {
        var tools = new List<ToolDefinition> { T("move_elements", "Move elements by vector (mm)") };
        for (int i = 0; i < 40; i++) tools.Add(T($"noise_{i}", "noise"));
        var selected = ToolSelector.Select(tools, "把選取的牆往 Y 移 300mm", 8);
        Assert.Contains(selected, t => t.Function.Name == "move_elements");
    }

    [Fact]
    public void ChineseIsolate_KeepsIsolateInView()
    {
        var tools = new List<ToolDefinition> { T("isolate_in_view", "Temporarily isolate elements") };
        for (int i = 0; i < 40; i++) tools.Add(T($"noise_{i}", "noise"));
        var selected = ToolSelector.Select(tools, "隔離這些", 8);
        Assert.Contains(selected, t => t.Function.Name == "isolate_in_view");
    }

    [Fact]
    public void ChineseRooms_KeepsGetRooms()
    {
        var tools = new List<ToolDefinition> { T("get_rooms", "List rooms with area") };
        for (int i = 0; i < 40; i++) tools.Add(T($"noise_{i}", "noise"));
        var selected = ToolSelector.Select(tools, "有哪些房間", 8);
        Assert.Contains(selected, t => t.Function.Name == "get_rooms");
    }

    [Fact]
    public void ChineseColorByParam_KeepsColorByParameter()
    {
        var tools = new List<ToolDefinition> { T("color_by_parameter", "Color elements by any parameter") };
        for (int i = 0; i < 40; i++) tools.Add(T($"noise_{i}", "noise"));
        var selected = ToolSelector.Select(tools, "依 Mark 上色", 8);
        Assert.Contains(selected, t => t.Function.Name == "color_by_parameter");
    }

    [Fact]
    public void ChineseFormwork_KeepsCreateFormworkPanels()
    {
        var tools = new List<ToolDefinition> { T("create_formwork_panels", "Generate formwork DirectShape panels") };
        for (int i = 0; i < 40; i++) tools.Add(T($"noise_{i}", "noise"));
        var selected = ToolSelector.Select(tools, "產生模板", 8);
        Assert.Contains(selected, t => t.Function.Name == "create_formwork_panels");
    }
}
