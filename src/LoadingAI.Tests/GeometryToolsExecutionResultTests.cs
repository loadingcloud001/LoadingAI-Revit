using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

/// <summary>
/// Pins the geometry slice of core-tools. The two tools that matter most here are
/// <c>move_elements</c> and <c>delete_elements</c>: the review flagged both as destructive L3
/// with no behavioural coverage at all, so this is the first automated exercise either has
/// received. Both fall back to the current selection, and the empty-selection refusal is the
/// path that used to be reported as a committed write.
/// </summary>
public class GeometryToolsExecutionResultTests
{
    [Fact]
    public async Task DeleteWithEmptySelectionDoesNotClaimCommit()
    {
        var (bridge, context) = Setup(selection: Array.Empty<string>());

        var result = await CreateTool(context, "delete_elements").ExecuteAsync("{}");

        // "Please select elements first." has no "Failed:" prefix, so the legacy path read this
        // destructive tool as a successful delete and assimilated it into the turn group.
        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Please select elements first.", result.Text);
        Assert.Empty(bridge.DeleteElementsCalls);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task DeleteWithSelectionCommitsTheDeletedCount()
    {
        var (bridge, context) = Setup(selection: new[] { "401", "402", "403" });
        bridge.NextDeleteElementsResult = new CommandResultDto(true, "Deleted 3 element(s).", 3);

        var result = await CreateTool(context, "delete_elements").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(3, result.ChangedCount);
        Assert.Equal(new[] { "401", "402", "403" }, Assert.Single(bridge.DeleteElementsCalls));
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task DeleteFailureDoesNotClaimCommit()
    {
        var (bridge, context) = Setup(selection: new[] { "401" });
        bridge.NextDeleteElementsResult = new CommandResultDto(false, "Failed: element is pinned.");

        var result = await CreateTool(context, "delete_elements").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Failed: element is pinned.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task MoveWithEmptySelectionDoesNotClaimCommit()
    {
        var (bridge, context) = Setup(selection: Array.Empty<string>());

        var result = await CreateTool(context, "move_elements")
            .ExecuteAsync("""{"dx":100,"dy":0,"dz":0}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Please select elements first.", result.Text);
        Assert.Empty(bridge.MoveElementsCalls);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task MoveSuccessCommitsTheMovedCount()
    {
        var (bridge, context) = Setup(selection: new[] { "501", "502" });
        bridge.NextMoveElementsResult = new CommandResultDto(true, "Moved 2 element(s).", 2);

        var result = await CreateTool(context, "move_elements")
            .ExecuteAsync("""{"elementIds":["501","502"],"dx":100,"dy":0,"dz":0}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(2, result.ChangedCount);
        Assert.Single(bridge.MoveElementsCalls);
    }

    [Fact]
    public async Task CreateWallSuccessCommits()
    {
        var (bridge, context) = Setup();
        bridge.NextCreateWallResult = new CommandResultDto(true, "Created wall 601.", 1);

        var result = await CreateTool(context, "create_wall")
            .ExecuteAsync("""{"levelName":"Level 1","x1":0,"y1":0,"x2":5000,"y2":0}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(1, result.ChangedCount);
        Assert.Single(bridge.CreateWallCalls);
    }

    private static (FakeRevitBridge Bridge, FakePluginContext Context) Setup(
        bool hasDocument = true, IReadOnlyList<string>? selection = null)
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = hasDocument };
        bridge.Selection = new SelectionSummaryDto(
            selection?.Count ?? 0, Array.Empty<string>(), selection ?? Array.Empty<string>());
        return (bridge, new FakePluginContext(bridge));
    }

    private static ToolEntry CreateTool(FakePluginContext context, string name)
        => LoadingAI.Tools.GeometryTools.Create(context).Single(t => t.Definition.Function.Name == name);
}
