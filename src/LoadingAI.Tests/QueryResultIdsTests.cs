using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

/// <summary>
/// A query that found elements says which ones. The ids travel on the result, so the host can
/// save them as a result set (@r1) and the model can come back to them without repeating the
/// query. The ids are the whole match, not the page the text shows.
/// </summary>
public class QueryResultIdsTests
{
    [Fact]
    public async Task FindElements_ReturnsAllIds_NotJustThePage()
    {
        var bridge = new FakeRevitBridge();
        bridge.NextFind = new FindResultDto(3, 0, null,
            new[] { new ElementSummaryDto("1", "Walls", "", "", "a") },
            AllIds: new[] { "1", "2", "3" });
        var tool = QueryTools.Create(new FakePluginContext(bridge)).Single(t => t.Definition.Function.Name == "find_elements_by_filter");

        var r = await tool.ExecuteAsync("{\"category\":\"Walls\",\"limit\":1}");

        Assert.Equal(new[] { "1", "2", "3" }, r.ResultIds);
    }

    [Fact]
    public async Task Selection_ReturnsItsIds()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            Selection = new SelectionSummaryDto(2, new[] { "Walls" }, new[] { "5", "6" })
        };
        var tool = QueryTools.Create(new FakePluginContext(bridge)).Single(t => t.Definition.Function.Name == "get_selection_summary");

        var r = await tool.ExecuteAsync("{}");

        Assert.Equal(new[] { "5", "6" }, r.ResultIds);
    }
}
