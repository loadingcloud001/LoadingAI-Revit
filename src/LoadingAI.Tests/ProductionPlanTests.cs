using LoadingAI.Plugins.QsFormwork;
using LoadingAI.Plugins.ViewTools;
using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class ProductionPlanTests
{
    [Fact]
    public async Task IssueCheck_FormatsCountsAndDoesNotWrite()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        bridge.NextIssueAudit = new IssueAuditDto(
            2, 1, 0, 3, 1, 0, 4, 0,
            new[] { new IssueAuditItemDto("view-not-on-sheet", "10", "Level 1", "FloorPlan") });
        var tool = QueryTools.Create(new FakePluginContext(bridge)).Single(t => t.Definition.Function.Name == "audit_issue_set");

        var result = await tool.ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.Contains("Issue check. No model changes.", result.Text);
        Assert.Contains("Views not on a sheet: 2", result.Text);
        Assert.Contains("view-not-on-sheet 10 Level 1 FloorPlan", result.Text);
        Assert.Contains(tool.Presets, p => p.Id == "category");
    }

    [Fact]
    public async Task SheetSetDryRun_ListsRowsAndDoesNotCreate()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = new ViewToolsPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_sheet_set");

        var result = await tool.ExecuteAsync("""
            {"sheetNumbers":["A101","A102"],"sheetNames":["Plan","Section"],"viewIds":["11","12"],"dryRun":true}
            """);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.Contains("Dry run: 2 sheet(s). No model changes.", result.Text);
        Assert.Contains("A101 Plan <- view 11", result.Text);
        Assert.Empty(bridge.CreateSheetCalls);
    }

    [Fact]
    public async Task SheetSet_PlacesEachViewOnTheNewSheet()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = new ViewToolsPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_sheet_set");

        var result = await tool.ExecuteAsync("""
            {"sheetNumbers":["A101"],"sheetNames":["Plan"],"viewIds":["11"],"x":100,"y":200}
            """);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal("11", bridge.PlaceViewOnSheetCalls[0].ViewId);
        Assert.Equal("1", bridge.PlaceViewOnSheetCalls[0].SheetId);
        Assert.Equal(100, bridge.PlaceViewOnSheetCalls[0].XMm);
    }

    [Fact]
    public async Task NumberingDryRun_DoesNotClaimACommit()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        bridge.NextNumberElements = new CommandResultDto(true, "Dry run: 1 Mark value(s). No model changes.\n9: D1 -> D-001");
        var tool = ParamTools.Create(new FakePluginContext(bridge)).Single(t => t.Definition.Function.Name == "numbering_auto");

        var result = await tool.ExecuteAsync("""{"category":"Rooms","order":"left-to-right","dryRun":true,"allInCategory":true}""");

        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.Contains("No model changes", result.Text);
        Assert.Equal("left-to-right", bridge.NumberElementsCalls[0].Order);
        Assert.True(bridge.NumberElementsCalls[0].DryRun);
    }

    [Fact]
    public async Task NumberingWholeCategoryFromTheForm_RefusesUntilOptedIn()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ParamTools.Create(new FakePluginContext(bridge)).Single(t => t.Definition.Function.Name == "numbering_auto");

        var result = await tool.ExecuteAsync("""{"category":"Rooms","allInCategory":false}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Contains("All in this category", result.Text);
        Assert.Empty(bridge.NumberElementsCalls);
    }

    [Fact]
    public async Task FormworkDryRun_NamesHostsAndRefusesAPanelCount()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = new QsFormworkPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_formwork_panels");

        var result = await tool.ExecuteAsync("""{"elementIds":["4","5"],"faceTypes":["Wall_Exterior"],"dryRun":true}""");

        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.Contains("2 host(s)", result.Text);
        Assert.Contains("Faces: Wall_Exterior.", result.Text);
        Assert.Contains("Panel count is not known until a real run.", result.Text);
        Assert.Contains("No model changes", result.Text);
        Assert.Empty(bridge.FormworkPanelsCalls);
    }
}
