using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

/// <summary>
/// Pins the status and write certainty core-tools' param slice reports through
/// <see cref="ToolEntry.StructuredHandler"/>. The nine param tools all return
/// CommandResultDto, whose Ok and AffectedCount most handlers discarded in favour of
/// result.Message, so FromLegacy judged them from the prose. A read-only export was
/// reported as a committed model write, and precondition refusals ("Please select elements
/// first.", "Provide elementIds.", an invalid date) have no "Failed:" prefix and so read as
/// successful commits that force a turn TransactionGroup to assimilate a run that never
/// wrote anything.
/// </summary>
public class ParamToolsExecutionResultTests
{
    [Fact]
    public async Task SetParameterByIdSuccessKeepsItsCustomTextAndCommits()
    {
        var (bridge, context) = Setup();
        bridge.NextSetParameterResult = new CommandResultDto(true, "Updated 1 element(s).", 1);

        var result = await CreateTool(context, "set_parameter_by_id")
            .ExecuteAsync("""{"elementId":"101","paramName":"Comments","value":"hello"}""");

        // The tool composes its own success sentence rather than echoing result.Message, and
        // the migration must not change that text.
        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(1, result.ChangedCount);
        Assert.Contains("Comments", result.Text);
        Assert.Contains("hello", result.Text);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task SetParameterByIdFailureDoesNotClaimCommit()
    {
        var (bridge, context) = Setup();
        bridge.NextSetParameterResult = new CommandResultDto(false, "Failed: parameter is read-only.");

        var result = await CreateTool(context, "set_parameter_by_id")
            .ExecuteAsync("""{"elementId":"101","paramName":"Comments","value":"x"}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Failed: parameter is read-only.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task SetCommentsOnEmptySelectionDoesNotClaimCommit()
    {
        var (bridge, context) = Setup(); // empty selection
        bridge.NextSetParameterResult = new CommandResultDto(true, "Updated 0 element(s).", 0);

        var result = await CreateTool(context, "set_comments_on_selection")
            .ExecuteAsync("""{"text":"note"}""");

        // "Please select elements first." has no "Failed:" prefix, so the legacy path read it as
        // a successful committed write.
        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Please select elements first.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task SetCommentsOnSelectionCommitsTheCount()
    {
        var (bridge, context) = Setup(selection: new[] { "201", "202" });
        bridge.NextSetParameterResult = new CommandResultDto(true, "Updated 2 element(s).", 2);

        var result = await CreateTool(context, "set_comments_on_selection")
            .ExecuteAsync("""{"text":"note"}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(2, result.ChangedCount);
    }

    // FindElementsFilteredAsync has no result override and its fake default is Total = 0, so
    // batch_set_parameter always takes the no-match path. "No elements matched filter for
    // Walls." has no "Failed:" prefix, so the legacy path read it as a successful commit.
    [Fact]
    public async Task BatchSetParameterNoMatchDoesNotClaimCommit()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "batch_set_parameter")
            .ExecuteAsync("""{"category":"Walls","targetParam":"Comments","targetValue":"x"}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Contains("No elements matched", result.Text);
        Assert.Empty(bridge.SetParameterCalls);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task RenameElementsSuccessCommits()
    {
        var (bridge, context) = Setup();
        bridge.NextRenameElementsResult = new CommandResultDto(true, "Renamed 3 element(s).", 3);

        var result = await CreateTool(context, "rename_elements")
            .ExecuteAsync("""{"elementIds":["1","2","3"],"newNames":["A","B","C"]}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(3, result.ChangedCount);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task RenameElementsMismatchedNamesDoesNotClaimCommit()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "rename_elements")
            .ExecuteAsync("""{"elementIds":["1","2","3"],"newNames":["A"]}""");

        // "newNames length must match elementIds." has no "Failed:" prefix.
        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Contains("must match", result.Text);
        Assert.Empty(bridge.RenameElementsCalls);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task ApplyConstructionDatesBadDateDoesNotClaimCommit()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "apply_construction_dates")
            .ExecuteAsync("""{"startDate":"not-a-date"}""");

        // The invalid-date message has no "Failed:" prefix.
        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Contains("Invalid startDate", result.Text);
        Assert.Empty(bridge.ApplyConstructionDatesCalls);
        Assert.False(result.IsSafeToAssimilate);
    }

    // export_params_to_excel is L1 and only writes a file, so it is a read as far as the model is
    // concerned. The legacy path reported it Committed because it returned a bare success string.
    [Fact]
    public async Task ExportParamsIsNoWrite()
    {
        var (bridge, context) = Setup();
        bridge.NextExportParams = new ParamExportDto("Walls", new[] { "Comments" }, new[] { new ParamExportRowDto("1", new[] { "x" }) });

        var result = await CreateTool(context, "export_params_to_excel")
            .ExecuteAsync("""{"category":"Walls","filePath":""}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.Equal(0, result.ChangedCount);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task ImportParamsMissingFileDoesNotClaimCommit()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "import_excel_to_params")
            .ExecuteAsync("""{"filePath":"definitely-missing.csv","paramName":"Comments"}""");

        // "File not found:" has no "Failed:" prefix.
        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Contains("File not found", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task CopyParamsSuccessCommits()
    {
        var (bridge, context) = Setup();
        bridge.NextCopyParams = new CommandResultDto(true, "Copied 4 value(s).", 4);

        var result = await CreateTool(context, "copy_params")
            .ExecuteAsync("""{"category":"Walls","sourceParam":"Mark","targetParam":"Comments"}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(4, result.ChangedCount);
    }

    [Fact]
    public async Task ReplaceTextFailureDoesNotClaimCommit()
    {
        var (bridge, context) = Setup();
        bridge.NextReplaceText = new CommandResultDto(false, "Failed: parameter is read-only.");

        var result = await CreateTool(context, "replace_text")
            .ExecuteAsync("""{"category":"Walls","paramName":"Mark","pattern":"A","replacement":"B"}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task NumberingAutoSuccessCommits()
    {
        var (bridge, context) = Setup();
        bridge.NextNumberElements = new CommandResultDto(true, "Numbered 6 element(s).", 6);

        var result = await CreateTool(context, "numbering_auto").ExecuteAsync("""{"prefix":"W-"}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(6, result.ChangedCount);
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
        => LoadingAI.Tools.ParamTools.Create(context).Single(t => t.Definition.Function.Name == name);
}
