using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class ToolEntryFieldsTests
{
    [Fact]
    public void Result_CarriesImagesAndIds_WithoutChangingText()
    {
        var r = ToolExecutionResult.Succeeded("Snapshot of Level 2") with
        {
            Images = new[] { new ToolImage("/tmp/a.png", "image/png", 1024, 768) },
            ResultIds = new[] { "101", "102" }
        };
        Assert.Equal("Snapshot of Level 2", r.Text);
        Assert.Single(r.Images!);
        Assert.Equal(new[] { "101", "102" }, r.ResultIds);
    }

    [Fact]
    public void Entry_NewFlags_DefaultOff()
    {
        var e = new ToolEntry();
        Assert.False(e.NeedsVision);
        Assert.False(e.NeverAllowList);
    }
}
