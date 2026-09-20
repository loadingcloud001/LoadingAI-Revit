using System.IO;
using LoadingAI.Revit;

namespace LoadingAI.Tests;

public class FamilyPathsTests
{
    [Fact]
    public void Root_IsAppDataLoadingAIFamilies()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LoadingAI", "families");
        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(FamilyPaths.Root));
        Assert.NotEqual(ExportPaths.Root, FamilyPaths.Root);
    }

    [Fact]
    public void RejectsPathOutsideRoot()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => FamilyPaths.ResolveRead(@"C:\Windows\Temp\evil.rfa"));
        Assert.Equal($"Path must be under {FamilyPaths.Root}", ex.Message);
    }

    [Fact]
    public void RelativeName_JoinsRoot()
    {
        var path = FamilyPaths.ResolveRead("nested\\a.rfa");
        Assert.True(FamilyPaths.IsUnderRoot(path));
        Assert.EndsWith("a.rfa", path);
    }

    [Fact]
    public void TempPath_IsOutsideRoot()
    {
        var temp = Path.Combine(Path.GetTempPath(), "family.rfa");
        Assert.False(FamilyPaths.IsUnderRoot(temp));
        Assert.Throws<InvalidOperationException>(() => FamilyPaths.ResolveRead(temp));
    }
}
