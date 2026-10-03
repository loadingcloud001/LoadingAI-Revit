using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class SelectionGuardTests
{
    private static SelectionSummaryDto Selection(int count, int listed)
        => new(count, new[] { "Walls" }, Enumerable.Range(1, listed).Select(i => i.ToString()).ToArray());

    [Fact]
    public void ACompleteSelectionPasses()
        => Assert.Null(SelectionGuards.TruncationRefusal(Selection(3, 3)));

    [Fact]
    public void ATruncatedSelectionIsRefusedWithBothNumbers()
    {
        var refusal = SelectionGuards.TruncationRefusal(Selection(1200, 500));
        Assert.NotNull(refusal);
        Assert.Contains("1200", refusal);
        Assert.Contains("500", refusal);
    }

    [Fact]
    public void AnEmptySelectionIsNotATruncation()
        => Assert.Null(SelectionGuards.TruncationRefusal(Selection(0, 0)));

    private static async Task<ToolExecutionResult> Run(string tool, string json, SelectionSummaryDto selection)
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true, Selection = selection };
        var context = new FakePluginContext(bridge);
        var entries = ParamTools.Create(context)
            .Concat(ViewTools.Create(context))
            .Concat(GeometryTools.Create(context));
        return await entries.First(e => e.Definition.Function.Name == tool).ExecuteAsync(json);
    }

    [Theory]
    [InlineData("set_comments_on_selection", """{"text":"x"}""")]
    [InlineData("isolate_in_view", "{}")]
    [InlineData("hide_in_view", "{}")]
    [InlineData("override_color_in_view", """{"color":"red"}""")]
    [InlineData("delete_elements", "{}")]
    [InlineData("move_elements", """{"dx":1,"dy":0,"dz":0}""")]
    public async Task SelectionFallbackTools_RefuseATruncatedSelection(string tool, string json)
    {
        var result = await Run(tool, json, Selection(1200, 500));
        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Contains("1200", result.Text);
    }

    [Fact]
    public async Task ColorByParameterRefusesATruncatedSelection()
    {
        var result = await Run("color_by_parameter", """{"paramName":"Mark"}""", Selection(1200, 500));
        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Contains("1200", result.Text);
    }

    [Fact]
    public void Source_CopyAndReplaceReportProcessedAgainstTotal()
    {
        var src = AdapterSource.Containing("public Task<CommandResultDto> CopyParamsAsync");
        Assert.Contains("in the model", src);
        Assert.DoesNotContain("$\"Copied {upd}/{elems.Count}", src);
        Assert.DoesNotContain("$\"Replaced {upd}/{elems.Count}", src);
        Assert.Contains("return new ParamExportDto(cat, cols, rows, null, allIds.Count);", src);
    }
}
