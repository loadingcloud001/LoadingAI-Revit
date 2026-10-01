using System.IO;
using LoadingAI.Plugins.QsFormwork;
using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

/// <summary>
/// Pins the status and write certainty qs-formwork reports through
/// <see cref="ToolEntry.StructuredHandler"/>. All four tools are L3, so a real report is a
/// commit. The result DTOs are only (Report, Error) and carry no counts, so these tests also
/// lock the limit of what the plugin can honestly claim: a null DTO or the host's
/// Please open a model first. report is not-committed, everything else is committed, and no
/// changed count is asserted because none is available.
/// </summary>
public class QsFormworkExecutionResultTests
{
    [Fact]
    public async Task SetupSuccessCommits()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "formwork_setup").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Contains("Parameters bound", result.Text);
        Assert.Single(bridge.FormworkSetupCalls);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task SetupWithoutADocumentDoesNotClaimCommit()
    {
        var (_, context) = Setup(hasDocument: false);

        var result = await CreateTool(context, "formwork_setup").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Please open a model first.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task CreatePanelsSuccessCommits()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "create_formwork_panels")
            .ExecuteAsync("""{"elementIds":["100","101"]}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        var call = Assert.Single(bridge.FormworkPanelsCalls);
        Assert.Equal(new[] { "100", "101" }, call.Ids);
        Assert.True(result.IsSafeToAssimilate);
    }

    // No ids and no selection: the tool never reaches the Bridge, so nothing is committed.
    [Fact]
    public async Task CreatePanelsWithNothingSelectedDoesNotClaimCommit()
    {
        var (bridge, context) = Setup(selection: Array.Empty<string>());

        var result = await CreateTool(context, "create_formwork_panels").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Please select elements first.", result.Text);
        Assert.Empty(bridge.FormworkPanelsCalls);
        Assert.False(result.IsSafeToAssimilate);
    }

    // When no ids are passed it falls back to the current selection, so the same call commits.
    [Fact]
    public async Task CreatePanelsFallsBackToSelection()
    {
        var (bridge, context) = Setup(selection: new[] { "200" });

        var result = await CreateTool(context, "create_formwork_panels").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        var call = Assert.Single(bridge.FormworkPanelsCalls);
        Assert.Equal(new[] { "200" }, call.Ids);
    }

    [Fact]
    public async Task CreatePanelsWithoutADocumentDoesNotClaimCommit()
    {
        var (_, context) = Setup(hasDocument: false, selection: new[] { "200" });

        var result = await CreateTool(context, "create_formwork_panels")
            .ExecuteAsync("""{"elementIds":["200"]}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Please open a model first.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task CleanupStatsCommits()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "cleanup_formwork").ExecuteAsync("""{"action":"stats"}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        var call = Assert.Single(bridge.CleanupFormworkCalls);
        Assert.Equal("stats", call.Action);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task CleanupWithoutADocumentDoesNotClaimCommit()
    {
        var (_, context) = Setup(hasDocument: false);

        var result = await CreateTool(context, "cleanup_formwork").ExecuteAsync("""{"action":"stats"}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Please open a model first.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task CopyParentParamsSuccessCommits()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "copy_formwork_parent_params")
            .ExecuteAsync("""{"elementIds":["300"],"paramNames":["FW_ConcreteGrade"]}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        var call = Assert.Single(bridge.CopyFormworkParentParamsCalls);
        Assert.Equal(new[] { "FW_ConcreteGrade" }, call.Names);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task CopyParentParamsWithoutADocumentDoesNotClaimCommit()
    {
        var (_, context) = Setup(hasDocument: false);

        var result = await CreateTool(context, "copy_formwork_parent_params")
            .ExecuteAsync("""{"elementIds":["300"],"paramNames":["FW_ConcreteGrade"]}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Please open a model first.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    // ---- Task 1: a caught exception is returned as Report = "Failed: ..." by FormworkTools.
    // The legacy FromLegacy classified that prefix as Failed; FromFormwork must too, or a
    // formwork crash is reported as a successful committed write and the turn group assimilates it.
    [Fact]
    public async Task FormworkCrashIsNotReportedAsACommit()
    {
        var (bridge, context) = Setup();
        bridge.NextFormworkPanelsResult =
            new FormworkPanelsResultDto("Failed: geometry could not be built.");

        var result = await CreateTool(context, "create_formwork_panels")
            .ExecuteAsync("""{"elementIds":["100"]}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Failed: geometry could not be built.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    // ---- Task 2: FormworkCleanup rolls its transaction back on these two paths and then
    // returns a plain sentence. The host now prefixes them with Failed: so one classifier
    // rule covers every rollback, instead of a list of sentences living in the plugin.
    [Theory]
    [InlineData("Failed: Unknown action. Use stats, delete_all, delete_by_type, or cleanup_orphans.")]
    [InlineData("Failed: faceType is required for delete_by_type.")]
    public async Task CleanupThatRolledBackIsNotReportedAsACommit(string report)
    {
        var (bridge, context) = Setup();
        bridge.NextCleanupFormworkResult = new CleanupResultDto(report);

        var result = await CreateTool(context, "cleanup_formwork")
            .ExecuteAsync("""{"action":"delete_all"}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal(report, result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    // Guard against over-matching: only the Failed: prefix means a crash. A report that merely
    // mentions a failure count mid-sentence is still a real write, and must stay a commit.
    [Fact]
    public async Task ReportThatMentionsFailuresWithoutThePrefixStaysACommit()
    {
        var (bridge, context) = Setup();
        bridge.NextFormworkPanelsResult =
            new FormworkPanelsResultDto("Formwork panels: selected 1, candidates 4, created 4, updated 0, duplicates 0, coverage skipped 1, panel overlap skipped 0, trimmed 0, obsolete deleted 0, failed 1.");

        var result = await CreateTool(context, "create_formwork_panels")
            .ExecuteAsync("""{"elementIds":["100"]}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.True(result.IsSafeToAssimilate);
    }

    // The plugin derives not-committed from the Failed: prefix, so the two rollback paths in
    // FormworkCleanup.Run must carry it. There is no behavioural seam: FormworkCleanup.Run
    // needs a live Revit Application, so this is the only way to pin the contract on hosted CI.
    // Source_ prefixed per CONTRIBUTING: a failure here means the invariant moved, not that
    // behaviour broke.
    [Fact]
    public void Source_FormworkCleanupRollbackPathsCarryTheFailedPrefix()
    {
        var src = File.ReadAllText(Path.Combine(
            AdapterSource.RepoRoot(), "src", "LoadingAI", "Revit", "Formwork", "FormworkTools.cs"));
        var start = src.IndexOf("internal static class FormworkCleanup", StringComparison.Ordinal);
        Assert.True(start >= 0, "FormworkCleanup not found");
        var cleanup = src.Substring(start);

        Assert.Contains("Failed: faceType is required for delete_by_type.", cleanup);
        Assert.Contains("Failed: Unknown action.", cleanup);
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
        => new QsFormworkPlugin().CreateTools(context).Single(t => t.Definition.Function.Name == name);
}
