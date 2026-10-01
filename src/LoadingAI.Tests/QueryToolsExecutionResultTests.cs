using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

/// <summary>
/// Every one of the 24 query tools is an L1 read, so none can write the model. The slice had no
/// write-honesty bug to fix; this pins the property that makes it safe: a query reports
/// <c>NoWrite</c> on success and never a commit, and a read that reached nothing is
/// <c>Failed + NoWrite</c> rather than a fabricated success. <c>NoWrite</c> rather than
/// <c>NotCommitted</c> because a read opens no transaction to commit or roll back.
/// </summary>
public class QueryToolsExecutionResultTests
{
    [Fact]
    public async Task ModelSummarySuccessIsNoWrite()
    {
        var (bridge, context) = Setup();
        bridge.ModelSummary = new ModelSummaryDto("HQ", @"C:\models\hq.rvt", true, 12, 34, 5, 6);

        var result = await CreateTool(context, "get_model_summary").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.Equal(0, result.ChangedCount);
        Assert.Contains("HQ", result.Text);
    }

    [Fact]
    public async Task ModelSummaryWithoutAModelIsFailedButNeverAWrite()
    {
        var (_, context) = Setup(hasDocument: false);

        var result = await CreateTool(context, "get_model_summary").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        // A read opened no transaction, so this is NoWrite, not NotCommitted.
        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.Equal("Please open a model first.", result.Text);
    }

    // No query in the slice may ever report a commit, whatever it returns, and each must carry
    // a structured handler. GetWallMetrics and GetWarnings have no settable fake result (they
    // always return null / empty), so the success-path assertion lives on get_model_summary,
    // which the fake does let us drive through ModelSummary.
    [Fact]
    public async Task EveryQueryToolIsL1AndCarriesAStructuredHandler()
    {
        var (_, context) = Setup();
        foreach (var tool in LoadingAI.Tools.QueryTools.Create(context))
        {
            Assert.Equal(ToolTier.L1, tool.Tier);
            Assert.NotNull(tool.StructuredHandler);
        }
    }

    private static (FakeRevitBridge Bridge, FakePluginContext Context) Setup(bool hasDocument = true)
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = hasDocument };
        return (bridge, new FakePluginContext(bridge));
    }

    private static ToolEntry CreateTool(FakePluginContext context, string name)
        => LoadingAI.Tools.QueryTools.Create(context).Single(t => t.Definition.Function.Name == name);
}
