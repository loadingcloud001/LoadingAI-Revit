using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

/// <summary>
/// Pins the capture_view tool of core-tools. It is the only core-tools tool that hands the model
/// an image, so it is the only one that needs vision and the only one whose result carries
/// images. It reads the model and never changes it: a bridge error is a failed read with no image,
/// not a write that was rolled back.
/// </summary>
public class CaptureViewToolTests
{
    private static (ToolEntry Tool, FakeRevitBridge Bridge) Make()
    {
        var bridge = new FakeRevitBridge();
        var ctx = new FakePluginContext(bridge);
        var tool = ViewTools.Create(ctx).Single(t => t.Definition.Function.Name == "capture_view");
        return (tool, bridge);
    }

    [Fact]
    public async Task ReturnsTextAndImage_AndNeedsVision()
    {
        var (tool, bridge) = Make();
        Assert.True(tool.NeedsVision);
        Assert.Equal(ToolTier.L1, tool.Tier);
        var r = await tool.ExecuteAsync("{}");
        Assert.Equal(ToolExecutionStatus.Succeeded, r.Status);
        Assert.Contains("Snapshot of Level 2", r.Text);
        Assert.Equal("/tmp/fake.png", Assert.Single(r.Images!).Path);
        Assert.Equal(1024, bridge.Captures.Single().Size);
    }

    [Fact]
    public async Task IdsDefaultToIsolate_AndSizeIsClamped()
    {
        var (tool, bridge) = Make();
        await tool.ExecuteAsync("""{"elementIds":["5","6"],"size":4000}""");
        var c = bridge.Captures.Single();
        Assert.True(c.Isolate);
        Assert.Equal(1600, c.Size);
    }

    [Fact]
    public async Task BridgeError_IsAFailedReadWithNoImage()
    {
        var (tool, bridge) = Make();
        bridge.NextSnapshot = new SnapshotDto(null, 0, 0, "Sched", "Cannot take a snapshot of Schedule 'Sched'.");
        var r = await tool.ExecuteAsync("{}");
        Assert.Equal(ToolExecutionStatus.Failed, r.Status);
        Assert.Null(r.Images);
    }
}
