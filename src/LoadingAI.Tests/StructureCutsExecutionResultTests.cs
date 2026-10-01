using LoadingAI.Plugins.StructureCuts;
using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

/// <summary>
/// Pins the status and write certainty structure-cuts reports through
/// <see cref="ToolEntry.StructuredHandler"/>. SplitResultDto already carries Created, Openings
/// and Skipped, so unlike the other plugins the counters were always available; the legacy
/// string path just discarded them. Two bugs matter here: "Please open a model first." and a
/// dry run both read as Succeeded + Committed, and a dry run is a query that must never start
/// a turn TransactionGroup.
/// </summary>
public class StructureCutsExecutionResultTests
{
    [Fact]
    public async Task NoDocumentDoesNotClaimCommit()
    {
        var (_, context) = Setup(hasDocument: false);

        var result = await CreateTool(context, "split_beams_by_columns").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Please open a model first.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    // A dry run short-circuits before any transaction and reports every counter at zero, so it
    // is a read. Reporting it Committed would start a turn TransactionGroup for a query.
    [Fact]
    public async Task DryRunIsNoWrite()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "split_beams_by_columns")
            .ExecuteAsync("""{"dryRun":true}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.Equal(0, result.ChangedCount);
        Assert.True(Assert.Single(bridge.SplitBeamsCalls).DryRun);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task SuccessfulSplitCarriesTheCreatedCount()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "split_beams_by_columns").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(3, result.ChangedCount);
        Assert.False(Assert.Single(bridge.SplitBeamsCalls).DryRun);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task SplitThatChangedNothingIsNoWrite()
    {
        var (bridge, context) = Setup();
        bridge.NextSplitBeamsResult = new SplitResultDto(
            "Split 0 elements. Skipped 5: all grouped or pinned.", 0, 0, 5, Array.Empty<string>());

        var result = await CreateTool(context, "split_beams_by_columns").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.Equal(0, result.ChangedCount);
        Assert.Contains("Skipped 5", result.Text);
    }

    // Skipped is an algorithm outcome, not a failure: a grouped, pinned or unsplittable member is
    // deliberately left alone and named in the report. The DTO carries no failure count at all,
    // so inventing one from Skipped would be a lie, and a run that did split something must not
    // be downgraded to Partial just because other members were left alone.
    [Fact]
    public async Task SkipsDoNotTurnARealSplitIntoAPartial()
    {
        var (bridge, context) = Setup();
        bridge.NextSplitBeamsResult = new SplitResultDto(
            "Split 2 beams into 5 pieces. Skipped 1: pinned.", 2, 0, 1, new[] { "7001", "7002" });

        var result = await CreateTool(context, "split_beams_by_columns").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(2, result.ChangedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Contains("Skipped 1", result.Text);
        Assert.True(result.IsSafeToAssimilate);
    }

    // Openings count as model changes too: a floor split that also punched column openings
    // really did write, even if no host was replaced by more than one piece.
    [Fact]
    public async Task OpeningsAloneStillCountAsAWrite()
    {
        var (bridge, context) = Setup();
        bridge.NextSplitFloorsResult = new SplitResultDto(
            "Openings: 4. No slab needed splitting.", 0, 4, 3, Array.Empty<string>());

        var result = await CreateTool(context, "split_floors_by_beams").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(4, result.ChangedCount);
        Assert.Single(bridge.SplitFloorsCalls);
    }

    // The adapter's catch returns Failed: {message} as the Report with Error left null, and every
    // counter zero. Legacy FromLegacy caught this by prefix; the structured path must too.
    [Fact]
    public async Task SplitCrashIsNotReportedAsACommit()
    {
        var (bridge, context) = Setup();
        bridge.NextSplitBeamsResult = new SplitResultDto(
            "Failed: native Split aborted the document.", 0, 0, 0, Array.Empty<string>());

        var result = await CreateTool(context, "split_beams_by_columns").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task ExplicitErrorDoesNotClaimCommit()
    {
        var (bridge, context) = Setup();
        bridge.NextSplitBeamsResult = new SplitResultDto(
            "Split 0.", 0, 0, 0, Array.Empty<string>(), "No hosts found for scope=selection.");

        var result = await CreateTool(context, "split_beams_by_columns").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("No hosts found for scope=selection.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    private static (FakeRevitBridge Bridge, FakePluginContext Context) Setup(bool hasDocument = true)
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = hasDocument };
        return (bridge, new FakePluginContext(bridge));
    }

    private static ToolEntry CreateTool(FakePluginContext context, string name)
        => new StructureCutsPlugin().CreateTools(context).Single(t => t.Definition.Function.Name == name);
}
