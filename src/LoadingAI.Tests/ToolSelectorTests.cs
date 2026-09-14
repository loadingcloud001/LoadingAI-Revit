using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    [Fact]
    public void SplitBeams_KeepsSplitBeamsByColumns()
    {
        var tools = new List<ToolDefinition>
        {
            T("split_beams_by_columns", "Split structural beams at columns")
        };
        for (int i = 0; i < 40; i++) tools.Add(T($"noise_{i}", "noise"));
        var selected = ToolSelector.Select(tools, "split beams by columns", 8);
        Assert.Contains(selected, t => t.Function.Name == "split_beams_by_columns");
    }

    [Fact]
    public void SplitFloors_KeepsSplitFloorsByBeams()
    {
        var tools = new List<ToolDefinition>
        {
            T("split_floors_by_beams", "Split floors along beam edges")
        };
        for (int i = 0; i < 40; i++) tools.Add(T($"noise_{i}", "noise"));
        var selected = ToolSelector.Select(tools, "split floors by beams", 8);
        Assert.Contains(selected, t => t.Function.Name == "split_floors_by_beams");
    }

    [Fact]
    public void Formwork_KeepsCreateFormworkPanels()
    {
        var tools = new List<ToolDefinition> { T("create_formwork_panels", "Generate formwork DirectShape panels") };
        for (int i = 0; i < 40; i++) tools.Add(T($"noise_{i}", "noise"));
        var selected = ToolSelector.Select(tools, "create formwork", 8);
        Assert.Contains(selected, t => t.Function.Name == "create_formwork_panels");
    }

    private static readonly string[] AlwaysKeep =
    {
        "get_model_summary", "find_elements_by_filter", "get_element_details",
        "get_selection_summary", "get_active_view_info", "get_visible_elements_in_view",
        "hide_in_view", "isolate_in_view", "reset_temporary_view",
        "delete_elements", "move_elements", "save_document"
    };

    [Fact]
    public void RealSurface_UnrelatedPrompt_KeepsAlwaysKeepAndCap()
    {
        var tools = RealSurface();
        Assert.True(tools.Count > ToolSelector.DefaultMax, $"real surface {tools.Count} should exceed DefaultMax");
        var selected = ToolSelector.Select(tools, "hello there, just chatting");
        Assert.True(selected.Count <= ToolSelector.DefaultMax);
        Assert.Equal(ToolSelector.DefaultMax, selected.Count);
        foreach (var name in AlwaysKeep)
            Assert.Contains(selected, t => t.Function.Name == name);
    }

    [Theory]
    [InlineData("split all beams by columns", "split_beams_by_columns")]
    [InlineData("split the slab at the beams", "split_floors_by_beams")]
    [InlineData("split walls by levels", "split_by_levels")]
    [InlineData("split floors by grids", "split_by_grids")]
    [InlineData("create formwork", "create_formwork_panels")]
    [InlineData("export a pdf of the sheets", "export_pdf")]
    [InlineData("purge unused families", "purge_unused")]
    [InlineData("load this family into the model", "load_family")]
    [InlineData("create a 3d view", "create_3d_view")]
    [InlineData("what rooms are there", "get_rooms")]
    [InlineData("color by Mark", "color_by_parameter")]
    [InlineData("list sheets and views", "get_sheets_views")]
    public void RealSurface_KeepsNamedTool(string prompt, string toolName)
    {
        var tools = RealSurface();
        Assert.Contains(tools, t => t.Function.Name == toolName);
        var selected = ToolSelector.Select(tools, prompt);
        Assert.True(selected.Count <= ToolSelector.DefaultMax);
        Assert.Contains(selected, t => t.Function.Name == toolName);
    }

    private static List<ToolDefinition> RealSurface()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toolsDir = Path.Combine(RepoRoot(), "src", "LoadingAI", "Tools");
        foreach (var file in Directory.GetFiles(toolsDir, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"Name\s*=\s*""([a-z0-9_]+)"""))
                names.Add(m.Groups[1].Value);
        }
        foreach (var json in Directory.GetFiles(Path.Combine(RepoRoot(), "Plugins"), "plugin.json", SearchOption.AllDirectories))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            if (!doc.RootElement.TryGetProperty("tools", out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var t in arr.EnumerateArray())
            {
                if (t.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                    names.Add(name);
            }
        }
        return names.Select(n => T(n)).ToList();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LoadingAI.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find LoadingAI.sln from " + AppContext.BaseDirectory);
    }
}
