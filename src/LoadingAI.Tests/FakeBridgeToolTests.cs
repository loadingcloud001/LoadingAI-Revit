using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

/// <summary>
/// Tool-handler tests against <see cref="FakeRevitBridge"/>.
/// These run in GitHub-hosted CI — they never start Revit.
/// Call tool Create* directly; do not go through PluginLoader (TaskDialog confirm).
/// </summary>
public class FakeBridgeToolTests
{
    [Fact]
    public async Task GetModelSummary_NoDocument_AsksToOpenModel()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = QueryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "get_model_summary");

        var text = await tool.Handler("{}");

        Assert.Equal("Please open a model first.", text);
    }

    [Fact]
    public async Task GetModelSummary_OpenDocument_FormatsCounts()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            ModelSummary = new("HQ", @"C:\models\hq.rvt", true, 12, 34, 5, 6)
        };
        var tool = QueryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "get_model_summary");

        var text = await tool.Handler("{}");

        Assert.Equal("Model: HQ\nPath: C:\\models\\hq.rvt\nViews: 12\nWalls: 34\nDoors: 5\nWindows: 6", text);
    }

    [Fact]
    public async Task IsolateInView_EmptyJsonAndEmptySelection_AsksToSelect()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            Selection = new(0, Array.Empty<string>(), Array.Empty<string>())
        };
        var tool = ViewTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "isolate_in_view");

        var text = await tool.Handler("{}");

        Assert.Equal("Please select elements first.", text);
        Assert.Empty(bridge.IsolateCalls);
    }

    [Fact]
    public async Task IsolateInView_WithElementIds_ReturnsBridgeMessage()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "isolate_in_view");

        var text = await tool.Handler("""{"elementIds":["101","102"]}""");

        Assert.Contains("Isolated 2 elements", text);
        Assert.Single(bridge.IsolateCalls);
        Assert.Equal(new[] { "101", "102" }, bridge.IsolateCalls[0].Ids);
        Assert.True(bridge.IsolateCalls[0].Isolate);
    }

    [Fact]
    public async Task SetParameterById_Failure_ReturnsBridgeMessage()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            NextSetParameterResult = new(false, "Parameter 'Mark' is read-only.")
        };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "set_parameter_by_id");

        var text = await tool.Handler("""{"elementId":"42","paramName":"Mark","value":"A1"}""");

        Assert.Equal("Parameter 'Mark' is read-only.", text);
        Assert.Single(bridge.SetParameterCalls);
    }

    [Fact]
    public async Task SetParameterById_Success_FormatsConfirmation()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "set_parameter_by_id");

        var text = await tool.Handler("""{"elementId":"42","paramName":"Comments","value":"ok"}""");

        Assert.Equal("Set Comments=\"ok\" on 42.", text);
        Assert.Equal(("42", "Comments", "ok"), bridge.SetParameterCalls.Single());
    }

    [Fact]
    public async Task SetParameterById_NoDocument_ReturnsBridgeMessage()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "set_parameter_by_id");

        var text = await tool.Handler("""{"elementId":"42","paramName":"Comments","value":"ok"}""");

        Assert.Equal("Please open a model first.", text);
        Assert.Empty(bridge.SetParameterCalls);
    }
}
