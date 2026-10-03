using System.Linq;
using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class TransformTests
{
    [Fact]
    public void TheBridgeTakesMillimetres_LikeEveryOtherMethod()
    {
        var names = typeof(IRevitElementCommand).GetMethod("MoveElementsAsync")!.GetParameters().Select(p => p.Name);
        Assert.Equal(new[] { "elementIds", "dxMm", "dyMm", "dzMm", "ct" }, names);
    }

    [Fact]
    public async System.Threading.Tasks.Task MoveElements_PassesMillimetresThrough_NotFeet()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = GeometryTools.Create(new FakePluginContext(bridge)).First(e => e.Definition.Function.Name == "move_elements");

        await tool.ExecuteAsync("""{"elementIds":["10","11"],"dx":1000,"dy":0,"dz":250}""");

        var call = Assert.Single(bridge.MoveElementsCalls);
        Assert.Equal((1000d, 0d, 250d), (call.Dx, call.Dy, call.Dz));
    }

    [Fact]
    public void Move_MovesTheWholeSetInOneCall()
    {
        var src = AdapterSource.Containing("public Task<CommandResultDto> MoveElementsAsync");
        var slice = SourceSlice.Between(src, "public Task<CommandResultDto> MoveElementsAsync", "public Task<CommandResultDto> ChangeElementTypeAsync");
        Assert.Contains("ElementTransformUtils.MoveElements(doc, ids, vec);", slice);
        Assert.DoesNotContain("ElementTransformUtils.MoveElement(", slice);
        Assert.DoesNotContain("foreach", slice);
        Assert.Contains("Metric.MmToFt(dxMm)", slice);
        Assert.DoesNotContain(" ft", slice);
        Assert.Contains(" mm.", slice);
    }

    [Fact]
    public void Rotate_RotatesTheWholeSetInOneCall_AndNamesTheCentreInMillimetres()
    {
        var src = AdapterSource.Containing("public Task<CommandResultDto> RotateElementsAsync");
        var slice = SourceSlice.Between(src, "public Task<CommandResultDto> RotateElementsAsync", "public Task<CommandResultDto> MirrorElementsAsync");
        Assert.Contains("ElementTransformUtils.RotateElements(doc, eids, axis, rad);", slice);
        Assert.DoesNotContain("ElementTransformUtils.RotateElement(", slice);
        Assert.Contains("Metric.FtToMm(origin.X)", slice);
    }

    [Fact]
    public void Mirror_CountsTheElementsItMirrored_NotTheCopiesItDidNotMake()
    {
        var src = AdapterSource.Containing("public Task<CommandResultDto> MirrorElementsAsync");
        var slice = SourceSlice.Between(src, "public Task<CommandResultDto> MirrorElementsAsync", "public Task<CommandResultDto> SplitWallAsync");
        Assert.Contains("in place", slice);
        Assert.Contains("eids.Count);", slice);
        Assert.DoesNotContain("new elements", slice);
        Assert.DoesNotContain("newIds.Count", slice);
    }
}
