using LoadingAI.Revit;

namespace LoadingAI.Tests;

public class ExportPathsTests
{
    [Fact]
    public void DefaultWrite_IsUnderRoot()
    {
        var path = ExportPaths.ResolveWrite(null, "out.csv");
        Assert.True(ExportPaths.IsUnderRoot(path));
        Assert.EndsWith("out.csv", path);
    }

    [Fact]
    public void RejectsPathOutsideRoot()
    {
        Assert.Throws<InvalidOperationException>(() => ExportPaths.ResolveWrite(@"C:\Windows\Temp\evil.csv", "x.csv"));
    }

    [Fact]
    public void RelativeName_JoinsRoot()
    {
        var path = ExportPaths.ResolveWrite("nested\\a.csv", "x.csv");
        Assert.True(ExportPaths.IsUnderRoot(path));
    }

    [Fact]
    public void SanitizeFileName_StripsRvtAndInvalidChars()
    {
        Assert.Equal("My_Model", ExportPaths.SanitizeFileName("My:Model.rvt"));
        Assert.Equal("export", ExportPaths.SanitizeFileName("   ", "export"));
        Assert.Equal("ok-name", ExportPaths.SanitizeFileName("ok-name"));
    }
}
