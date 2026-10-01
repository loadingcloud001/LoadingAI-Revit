using LoadingAI.Plugins.ExportTools;
using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

/// <summary>
/// Pins the status and write certainty export-tools reports through
/// <see cref="ToolEntry.StructuredHandler"/>. Two rules are under test: an export writes
/// files and never the Revit model, so it must never claim a commit; <c>purge_unused</c>
/// does write the model and must report exactly what it deleted, so the turn
/// TransactionGroup rolls back a clean failure but keeps a partial success.
/// </summary>
public class ExportToolsExecutionResultTests
{
    [Fact]
    public async Task ExportSuccessWritesFilesNotTheModel()
    {
        var (bridge, context) = Setup();
        bridge.NextExportPdfResult = new CommandResultDto(true, "Exported 2 sheet(s)/view(s) to PDF.", 2);

        var result = await CreateTool(context, "export_pdf").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.Equal(2, result.ChangedCount);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task ExportFailureIsNotReportedAsAWrite()
    {
        var (bridge, context) = Setup();
        bridge.NextExportPdfResult = new CommandResultDto(false, "Export failed: no plotter.");

        var result = await CreateTool(context, "export_pdf").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal("Export failed: no plotter.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task DwgExportFailureIsNotReportedAsAWrite()
    {
        var (bridge, context) = Setup();
        bridge.NextExportDwgResult = new CommandResultDto(false, "Export failed: no view.");

        var result = await CreateTool(context, "export_dwg").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task IfcExportFailureIsNotReportedAsAWrite()
    {
        var (bridge, context) = Setup();
        bridge.NextExportIfcResult = new CommandResultDto(false, "Export failed: no schema.");

        var result = await CreateTool(context, "export_ifc").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.False(result.IsSafeToAssimilate);
    }

    // The regression this whole migration exists for: a legacy string handler returned
    // "Please open a model first.", FromLegacy read that as Succeeded + Committed, and the
    // turn TransactionGroup assimilated it instead of rolling back.
    [Fact]
    public async Task PurgeWithoutADocumentDoesNotClaimCommit()
    {
        var (_, context) = Setup(hasDocument: false);

        var result = await CreateTool(context, "purge_unused").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Please open a model first.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task PurgePreviewNeverClaimsAWrite()
    {
        var (bridge, context) = Setup();
        bridge.NextPurgeUnusedResult = new PurgeUnusedResultDto(
            true, 3, 0, 0, new[] { "Walls/Basic Wall (101)" });

        var result = await CreateTool(context, "purge_unused").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.Equal(0, result.ChangedCount);
        Assert.Contains("Purge preview", result.Text);
    }

    [Fact]
    public async Task PurgeNothingUnusedReportsNoWrite()
    {
        var (bridge, context) = Setup();
        bridge.NextPurgeUnusedResult = new PurgeUnusedResultDto(true, 0, 0, 0, Array.Empty<string>());

        var result = await CreateTool(context, "purge_unused").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.Equal("Nothing to purge.", result.Text);
    }

    [Fact]
    public async Task PurgeDeleteReportsWhatItDeleted()
    {
        var (bridge, context) = Setup();
        bridge.NextPurgeUnusedResult = new PurgeUnusedResultDto(false, 5, 5, 2, Array.Empty<string>());

        var result = await CreateTool(context, "purge_unused").ExecuteAsync("""{"preview":false}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(5, result.ChangedCount);
        Assert.Equal("Purged 5 unused element(s) in 2 pass(es).", result.Text);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task PurgeThatDeletedNothingDoesNotClaimCommit()
    {
        var (bridge, context) = Setup();
        bridge.NextPurgeUnusedResult = new PurgeUnusedResultDto(false, 5, 0, 1, Array.Empty<string>());

        var result = await CreateTool(context, "purge_unused").ExecuteAsync("""{"preview":false}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.Equal(0, result.ChangedCount);
        Assert.Contains("Purged 0 unused element(s)", result.Text);
    }

    [Fact]
    public async Task PurgePartialKeepsSurvivorsAndNamesTheFailure()
    {
        var (bridge, context) = Setup();
        bridge.NextPurgeUnusedResult = new PurgeUnusedResultDto(
            false, 5, 3, 1, Array.Empty<string>(), "Two elements were pinned.");

        var result = await CreateTool(context, "purge_unused").ExecuteAsync("""{"preview":false}""");

        Assert.Equal(ToolExecutionStatus.Partial, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(3, result.ChangedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal("Two elements were pinned.", result.Text);
        Assert.True(result.IsSafeToAssimilate);
    }

    private static (FakeRevitBridge Bridge, FakePluginContext Context) Setup(bool hasDocument = true)
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = hasDocument };
        return (bridge, new FakePluginContext(bridge));
    }

    private static ToolEntry CreateTool(FakePluginContext context, string name)
        => new ExportToolsPlugin().CreateTools(context).Single(t => t.Definition.Function.Name == name);
}
