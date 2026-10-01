using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

/// <summary>
/// Pins the view slice of core-tools. Every one of the 13 view tools is a write returning
/// CommandResultDto, so Ok decides commit vs not-committed. The distinctive behaviour here is the
/// selection fallback: isolate, hide and override resolve the current selection, and if it is
/// empty they refuse before any transaction. That refusal, like every parse guard in this slice,
/// has no "Failed:" prefix and so used to read as a successful committed write.
///
/// Named CoreViewTools to keep it distinct from ViewToolsExecutionResultTests, which covers the
/// separate view-tools plugin (LoadingAI.Plugins.ViewTools).
/// </summary>
public class CoreViewToolsExecutionResultTests
{
    [Fact]
    public async Task IsolateWithEmptySelectionDoesNotClaimCommit()
    {
        var (bridge, context) = Setup(selection: Array.Empty<string>());

        var result = await CreateTool(context, "isolate_in_view").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Please select elements first.", result.Text);
        Assert.Empty(bridge.IsolateCalls);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task IsolateWithSelectionCommits()
    {
        var (bridge, context) = Setup(selection: new[] { "301", "302" });
        bridge.NextIsolateResult = new CommandResultDto(true, "Isolated 2 element(s).", 2);

        var result = await CreateTool(context, "isolate_in_view").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(2, result.ChangedCount);
        Assert.Single(bridge.IsolateCalls);
        Assert.True(result.IsSafeToAssimilate);
    }

    // ResetViewVisibilityAsync has no result override, so its Ok=false path is the closed model.
    [Fact]
    public async Task ResetTemporaryViewWithoutAModelDoesNotClaimCommit()
    {
        var (_, context) = Setup(hasDocument: false);

        var result = await CreateTool(context, "reset_temporary_view").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Please open a model first.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task TagAllSuccessCommits()
    {
        var (bridge, context) = Setup();
        bridge.NextTagAllResult = new CommandResultDto(true, "Tagged 9 element(s).", 9);

        var result = await CreateTool(context, "tag_all").ExecuteAsync("""{"category":"Walls"}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(9, result.ChangedCount);
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
        => LoadingAI.Tools.ViewTools.Create(context).Single(t => t.Definition.Function.Name == name);
}
