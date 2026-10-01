using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

/// <summary>
/// Pins the status and write certainty core-tools' document slice reports through
/// <see cref="ToolEntry.StructuredHandler"/>. All three tools return CommandResultDto, whose
/// <c>Ok</c> and <c>AffectedCount</c> the legacy handlers discarded in favour of
/// <c>result.Message</c>, so FromLegacy judged them from the prose alone. That is how a save
/// that never happened, a reload that reloaded 2 of 5 links, and a rejected argument all read as
/// successful committed writes.
/// </summary>
public class DocumentToolsExecutionResultTests
{
    [Fact]
    public async Task SaveDocumentWithoutAModelDoesNotClaimCommit()
    {
        var (_, context) = Setup(hasDocument: false);

        var result = await CreateTool(context, "save_document").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Please open a model first.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    // An unsaved document has no path to save to. The message has no "Failed:" prefix, so the
    // legacy path read it as a successful write.
    [Fact]
    public async Task SaveDocumentWithNoPathDoesNotClaimCommit()
    {
        var (_, context) = Setup(saved: false);

        var result = await CreateTool(context, "save_document").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Contains("no path", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task SaveDocumentSuccessCommits()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "save_document").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Contains("Saved HQ", result.Text);
        Assert.Equal(1, bridge.SaveDocumentCallCount);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task ReloadLinksSuccessCarriesTheReloadedCount()
    {
        var (bridge, context) = Setup();
        bridge.NextReloadLinksResult = new CommandResultDto(true, "Reloaded 4/5 RVT links", 4);

        var result = await CreateTool(context, "reload_links").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(4, result.ChangedCount);
        Assert.Equal("Reloaded 4/5 RVT links", result.Text);
        Assert.True(result.IsSafeToAssimilate);
    }

    // A partial reload is Ok=true, so it is a commit. CommandResultDto carries no requested total,
    // so the 4/5 partial lives only in the text and cannot be reported as a structured Partial
    // without parsing prose. The same ceiling formwork has.
    [Fact]
    public async Task ReloadLinksPartialStaysACommitAndTheTextCarriesTheTotal()
    {
        var (bridge, context) = Setup();
        bridge.NextReloadLinksResult = new CommandResultDto(true, "Reloaded 2/5 RVT links", 2);

        var result = await CreateTool(context, "reload_links").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(2, result.ChangedCount);
        Assert.Contains("2/5", result.Text);
    }

    [Fact]
    public async Task ReloadLinksWithoutAModelDoesNotClaimCommit()
    {
        var (_, context) = Setup(hasDocument: false);

        var result = await CreateTool(context, "reload_links").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Please open a model first.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task ReloadLinksFailureDoesNotClaimCommit()
    {
        var (bridge, context) = Setup();
        bridge.NextReloadLinksResult = new CommandResultDto(false, "Failed: transaction aborted.");

        var result = await CreateTool(context, "reload_links").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task WorksetAssignSuccessCarriesTheAssignedCount()
    {
        var (bridge, context) = Setup(workshared: true);
        bridge.NextAssignWorksetResult = new CommandResultDto(true, "Assigned 3/4 to workset \"SITE\"", 3);

        var result = await CreateTool(context, "workset_assign")
            .ExecuteAsync("""{"elementIds":["1","2","3","4"],"worksetName":"SITE"}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(3, result.ChangedCount);
        var call = Assert.Single(bridge.AssignWorksetCalls);
        Assert.Equal(new[] { "1", "2", "3", "4" }, call.Ids);
        Assert.Equal("SITE", call.WorksetName);
    }

    [Fact]
    public async Task WorksetAssignOnANonWorksharedDocumentDoesNotClaimCommit()
    {
        var (_, context) = Setup();

        // The fake refuses a non-workshared document before the result override is consulted.
        var result = await CreateTool(context, "workset_assign")
            .ExecuteAsync("""{"elementIds":["1"],"worksetName":"SITE"}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Document is not workshared.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task WorksetAssignMissingWorksetDoesNotClaimCommit()
    {
        var (bridge, context) = Setup(workshared: true);
        bridge.NextAssignWorksetResult = new CommandResultDto(false, "Workset NOPE not found.");

        var result = await CreateTool(context, "workset_assign")
            .ExecuteAsync("""{"elementIds":["1"],"worksetName":"NOPE"}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Workset NOPE not found.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    // The legacy catch returned "Invalid args: {msg}" with no "Failed:" prefix, so a malformed
    // argument read as a committed write. ToolArgumentValidator normally rejects it first; this
    // pins the handler guard for a direct call that still slips through.
    [Fact]
    public async Task WorksetAssignMalformedArgsDoesNotClaimCommit()
    {
        var (bridge, context) = Setup(workshared: true);

        var result = await CreateTool(context, "workset_assign")
            .ExecuteAsync("""{"elementIds":"1","worksetName":"SITE"}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Empty(bridge.AssignWorksetCalls);
        Assert.False(result.IsSafeToAssimilate);
    }

    private static (FakeRevitBridge Bridge, FakePluginContext Context) Setup(
        bool hasDocument = true, bool workshared = false, bool saved = true)
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = hasDocument };
        bridge.ModelSummary = new ModelSummaryDto(
            "HQ", saved ? @"C:\models\hq.rvt" : "", saved, 12, 34, 5, 6);
        bridge.SyncInfo = new SyncInfoDto(@"\\central\hq.rvt", false, workshared, "alice", 2);
        return (bridge, new FakePluginContext(bridge));
    }

    private static ToolEntry CreateTool(FakePluginContext context, string name)
        => LoadingAI.Tools.DocumentTools.Create(context).Single(t => t.Definition.Function.Name == name);
}
