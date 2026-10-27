using System.IO;
using LoadingAI.Llm;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class ImageDataTests
{
    [Fact]
    public void ToAttachment_MakesADataUrl()
    {
        var f = Path.GetTempFileName();
        File.WriteAllBytes(f, new byte[] { 1, 2, 3 });
        var a = ImageData.ToAttachment(new ToolImage(f, "image/png", 1, 1));
        Assert.Equal("data:image/png;base64,AQID", a!.DataUrl);
        File.Delete(f);
    }

    [Fact]
    public void ToAttachment_MissingOrTooLarge_IsNull()
    {
        Assert.Null(ImageData.ToAttachment(new ToolImage("/no/such.png", "image/png", 1, 1)));
        var f = Path.GetTempFileName();
        File.WriteAllBytes(f, new byte[ImageData.MaxBytes + 1]);
        Assert.Null(ImageData.ToAttachment(new ToolImage(f, "image/png", 1, 1)));
        File.Delete(f);
    }
}
