using LoadingAI.Llm;

namespace LoadingAI.Tests;

public class ToolSelectorZhTests
{
    private static ToolDefinition Tool(string name)
        => new() { Function = new ToolFunction { Name = name, Description = name.Replace('_', ' ') + "." } };

    private static List<ToolDefinition> Catalog()
    {
        var tools = new List<ToolDefinition>
        {
            Tool("get_model_summary"), Tool("get_selection_summary"), Tool("get_element_details"),
            Tool("split_beams_by_columns"), Tool("export_pdf"), Tool("delete_elements"),
            Tool("get_warnings"), Tool("create_openings_from_clash"), Tool("cleanup_formwork"),
            Tool("get_rooms"), Tool("set_comments_on_selection")
        };
        for (var i = 0; i < 30; i++) tools.Add(Tool($"filler_tool_{i:00}"));
        return tools;
    }

    private static List<string> Pick(string text)
        => ToolSelector.Select(Catalog(), text, 6).Select(t => t.Function.Name).ToList();

    [Theory]
    [InlineData("將樑喺柱位分割", "split_beams_by_columns")]
    [InlineData("把梁在柱位拆分", "split_beams_by_columns")]
    [InlineData("匯出PDF圖紙", "export_pdf")]
    [InlineData("导出 pdf", "export_pdf")]
    [InlineData("刪除選取的牆", "delete_elements")]
    [InlineData("有幾多警告", "get_warnings")]
    [InlineData("有幾多房間", "get_rooms")]
    [InlineData("幫我開孔洞口", "create_openings_from_clash")]
    [InlineData("清理模板", "cleanup_formwork")]
    public void AChineseRequestReachesTheRightTool(string text, string expected)
        => Assert.Contains(expected, Pick(text));

    [Fact]
    public void ExpandAliases_KeepsTheOriginalTextAndAddsEnglishKeywords()
    {
        var expanded = ToolSelector.ExpandAliases("匯出牆");
        Assert.StartsWith("匯出牆", expanded);
        Assert.Contains("export", expanded);
        Assert.Contains("wall", expanded);
    }

    [Fact]
    public void EnglishAndEmptyTextAreUntouched()
    {
        Assert.Equal("split the beams", ToolSelector.ExpandAliases("split the beams"));
        Assert.Equal("", ToolSelector.ExpandAliases(""));
    }

    [Fact]
    public void English_StillSelectsAsBefore()
        => Assert.Contains("split_beams_by_columns", Pick("split the beams at the columns"));
}
