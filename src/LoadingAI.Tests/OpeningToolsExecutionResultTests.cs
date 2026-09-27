using LoadingAI.Plugins.OpeningTools;
using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class OpeningToolsExecutionResultTests
{
    [Fact]
    public async Task PlaceSuccessReturnsStructuredCommittedResult()
    {
        var (bridge, context) = Setup();
        bridge.NextPlaceOpeningsResult = new OpeningPlaceResultDto(
            "Placed 2. Punched 2. Failed 0.", 2, 2, 0, 0, 0, new[] { "1", "2" }, Array.Empty<string>());

        var result = await CreateTool(context, "create_openings_from_clash").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(2, result.ChangedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task PlacePartialPreservesErrorAndReport()
    {
        var (bridge, context) = Setup();
        bridge.NextPlaceOpeningsResult = new OpeningPlaceResultDto(
            "Placed 1. Failed 1.", 1, 1, 1, 0, 0, new[] { "1" }, Array.Empty<string>(), "One host failed.");

        var result = await CreateTool(context, "create_openings_from_clash").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Partial, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(1, result.ChangedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains("One host failed.", result.Text);
        Assert.Contains("Placed 1. Failed 1.", result.Text);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task PlaceAllFailedDoesNotClaimCommit()
    {
        var (bridge, context) = Setup();
        bridge.NextPlaceOpeningsResult = new OpeningPlaceResultDto(
            "Placed 0. Failed 2.", 0, 0, 2, 0, 0, Array.Empty<string>(), Array.Empty<string>(), "All hosts failed.");

        var result = await CreateTool(context, "create_openings_from_clash").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal(2, result.FailedCount);
        Assert.Contains("All hosts failed.", result.Text);
        Assert.Contains("Placed 0. Failed 2.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task CutPartialReturnsStructuredCommittedResult()
    {
        var (bridge, context) = Setup();
        bridge.NextCutHostsResult = new OpeningCutResultDto(
            "Cut 1. Failed 1.", 1, 0, 0, 1, "One cut failed.");

        var result = await CreateTool(context, "cut_hosts_by_openings").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Partial, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(1, result.ChangedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains("One cut failed.", result.Text);
        Assert.Contains("Cut 1. Failed 1.", result.Text);
    }

    [Fact]
    public async Task DryRunReturnsNoWrite()
    {
        var (bridge, context) = Setup();
        bridge.NextOpeningProbeResult = new OpeningPlanResultDto("Planned 2.", 2, 1, 0, Array.Empty<OpeningPlanHitDto>());

        var result = await CreateTool(context, "create_openings_from_clash").ExecuteAsync("""{"dryRun":true}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.Contains("Planned 2.", result.Text);
        Assert.Contains("Dry run", result.Text);
    }

    private static (FakeRevitBridge Bridge, FakePluginContext Context) Setup()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        return (bridge, new FakePluginContext(bridge));
    }

    private static ToolEntry CreateTool(FakePluginContext context, string name)
        => new OpeningToolsPlugin().CreateTools(context).Single(t => t.Definition.Function.Name == name);
}

