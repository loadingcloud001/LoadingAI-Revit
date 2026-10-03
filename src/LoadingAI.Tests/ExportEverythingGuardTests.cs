using System.Linq;
using System.Text.Json;
using LoadingAI.Plugins.ExportTools;
using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class ExportEverythingGuardTests
{
    private static ToolEntry Pdf(FakeRevitBridge bridge)
        => new ExportToolsPlugin().CreateTools(new FakePluginContext(bridge)).First(t => t.Definition.Function.Name == "export_pdf");

    [Fact]
    public async Task NoSheetIds_WithoutConfirmAll_IsRefusedBeforeTheBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };

        var result = await Pdf(bridge).ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.Contains("confirmAll", result.Text);
        Assert.Empty(bridge.ExportPdfCalls);
    }

    [Fact]
    public async Task ConfirmAll_ExportsEverySheet()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };

        var result = await Pdf(bridge).ExecuteAsync("""{"confirmAll":true}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Null(Assert.Single(bridge.ExportPdfCalls).SheetIds);
    }

    [Fact]
    public async Task ExplicitSheetIds_NeedNoConfirmation()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };

        await Pdf(bridge).ExecuteAsync("""{"sheetIds":["101"]}""");

        Assert.Equal(new[] { "101" }, Assert.Single(bridge.ExportPdfCalls).SheetIds);
    }

    [Fact]
    public async Task ABadFolder_IsStillReportedFirst()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var outside = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LoadingAI.Tests", "outside-root");

        var result = await Pdf(bridge).ExecuteAsync($$"""{"folder":{{JsonSerializer.Serialize(outside)}}}""");

        Assert.StartsWith("Failed: Folder must be under", result.Text);
    }

    [Fact]
    public void TheSchemaOffersConfirmAll()
        => Assert.Contains("confirmAll", JsonSerializer.Serialize(Pdf(new FakeRevitBridge()).Definition.Function.Parameters));
}
