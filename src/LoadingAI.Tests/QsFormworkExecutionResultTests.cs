using System.IO;
using LoadingAI.Plugins.QsFormwork;
using LoadingAI.Revit;
using LoadingAI.Revit.Formwork;
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

    // ---- Task 4: counts cross the boundary, so status is derived from numbers rather than
    // from the wording of a report. This is what makes Partial reportable for formwork at all.
    [Fact]
    public async Task CreatePanelsPartialKeepsCreatedAndNamesTheFailure()
    {
        var (bridge, context) = Setup();
        bridge.NextFormworkPanelsResult = new FormworkPanelsResultDto(
            "Formwork panels: selected 1, candidates 6, created 4, updated 0, duplicates 0, "
            + "coverage skipped 1, panel overlap skipped 0, trimmed 0, obsolete deleted 0, failed 2. "
            + "Failures: face_unbuildable=2.",
            Created: 4,
            Failed: 2);

        var result = await CreateTool(context, "create_formwork_panels")
            .ExecuteAsync("""{"elementIds":["100","101"]}""");

        Assert.Equal(ToolExecutionStatus.Partial, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(4, result.ChangedCount);
        Assert.Equal(2, result.FailedCount);
        Assert.Contains("face_unbuildable=2", result.Text);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task CreatePanelsAllFailedDoesNotClaimCommit()
    {
        var (bridge, context) = Setup();
        bridge.NextFormworkPanelsResult = new FormworkPanelsResultDto(
            "Formwork panels: selected 1, candidates 3, created 0, updated 0, duplicates 0, "
            + "coverage skipped 0, panel overlap skipped 0, trimmed 0, obsolete deleted 0, failed 3. "
            + "Failures: face_unbuildable=3.",
            Failed: 3);

        var result = await CreateTool(context, "create_formwork_panels")
            .ExecuteAsync("""{"elementIds":["100"]}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal(0, result.ChangedCount);
        Assert.Equal(3, result.FailedCount);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task CreatePanelsSuccessCarriesChangedCount()
    {
        var (bridge, context) = Setup();
        bridge.NextFormworkPanelsResult =
            new FormworkPanelsResultDto("Formwork panels: created 4.", Created: 4);

        var result = await CreateTool(context, "create_formwork_panels")
            .ExecuteAsync("""{"elementIds":["100"]}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(4, result.ChangedCount);
        Assert.Equal(0, result.FailedCount);
    }

    // stats never opens a Transaction (FormworkCleanup.Run returns before tx.Start), so it is
    // a read. Reporting it Committed would start a turn TransactionGroup for a query.
    [Fact]
    public async Task CleanupStatsIsNoWrite()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "cleanup_formwork")
            .ExecuteAsync("""{"action":"stats"}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        var call = Assert.Single(bridge.CleanupFormworkCalls);
        Assert.Equal("stats", call.Action);
        Assert.True(result.IsSafeToAssimilate);
    }

    // A delete opens a transaction, so it stays a commit even when it happens to delete nothing.
    [Fact]
    public async Task CleanupDeleteStaysACommit()
    {
        var (bridge, context) = Setup();
        bridge.NextCleanupFormworkResult = new CleanupResultDto("Deleted 0 formwork panel(s).");

        var result = await CreateTool(context, "cleanup_formwork")
            .ExecuteAsync("""{"action":"delete_all"}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Single(bridge.CleanupFormworkCalls);
    }

    // ---- Task 5: schema hardening. ToolArgumentValidator rejects these before approval, so the
    // model never reaches the unknown-action branch and cannot send a thickness the host would
    // silently honour regardless of what the generator can actually build.
    [Fact]
    public async Task CleanupRejectsUnknownAction()
    {
        var (_, context) = Setup();

        var result = await CreateTool(context, "cleanup_formwork")
            .ExecuteAsync("""{"action":"destroy_everything"}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        // action is a declared property with an enum, so the failure is a value error rather
        // than an unknown property; either way it is refused before the handler runs.
        Assert.Contains("action", result.Text);
        Assert.Contains("must be one of", result.Text);
        Assert.Contains("cleanup_orphans", result.Text);
    }

    [Fact]
    public async Task CreatePanelsRejectsFaceTypeOutsideTheGeneratorList()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "create_formwork_panels")
            .ExecuteAsync("""{"elementIds":["100"],"faceTypes":["Roof_Top"]}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Contains("faceTypes", result.Text);
        Assert.Empty(bridge.FormworkPanelsCalls);
    }

    [Fact]
    public async Task CreatePanelsRejectsThicknessOutsideBounds()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "create_formwork_panels")
            .ExecuteAsync("""{"elementIds":["100"],"thicknessMm":500}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Contains("thicknessMm", result.Text);
        Assert.Empty(bridge.FormworkPanelsCalls);
    }

    [Fact]
    public async Task CreatePanelsAcceptsThicknessInsideBounds()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "create_formwork_panels")
            .ExecuteAsync("""{"elementIds":["100"],"thicknessMm":75}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        var call = Assert.Single(bridge.FormworkPanelsCalls);
        Assert.Equal(75.0, call.ThicknessMm);
    }

    // The plugin must build its enum from FormworkDefaults.AllFaceTypes rather than a private
    // copy: if it copied the array it would drift the day a face type is added host-side, and
    // the model would be offered a value the schema silently refuses. Every Core entry must be
    // accepted, which fails the moment the two lists diverge.
    [Fact]
    public async Task EveryCoreFaceTypeIsAccepted()
    {
        foreach (var faceType in FormworkDefaults.AllFaceTypes)
        {
            var (bridge, context) = Setup();
            var result = await CreateTool(context, "create_formwork_panels")
                .ExecuteAsync($$"""{"elementIds":["100"],"faceTypes":["{{faceType}}"]}""");

            Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
            Assert.Single(bridge.FormworkPanelsCalls);
        }
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
