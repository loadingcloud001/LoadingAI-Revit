using System.IO;
using Xunit;

namespace LoadingAI.Tests;

public class PluginProjectContractTests
{
    [Theory]
    [InlineData("Plugins/export-tools/ExportToolsPlugin.csproj")]
    [InlineData("Plugins/view-tools/ViewToolsPlugin.csproj")]
    [InlineData("Plugins/modeling-tools/ModelingToolsPlugin.csproj")]
    [InlineData("Plugins/opening-tools/OpeningToolsPlugin.csproj")]
    public void ShippedPlugin_DoesNotReferenceHost(string relative)
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));
        Assert.DoesNotContain("src\\LoadingAI\\LoadingAI.csproj", csproj);
        Assert.DoesNotContain("src/LoadingAI/LoadingAI.csproj", csproj);
        Assert.Contains("LoadingAI.Core.csproj", csproj);
        Assert.Contains("LoadingAI.RevitUtil.csproj", csproj);
    }

    [Fact]
    public void HelloPlugin_IsBridgeOnly()
    {
        var root = RepoRoot();
        var csproj = File.ReadAllText(Path.Combine(root, "Plugins", "example.hello", "HelloPlugin.csproj"));
        var src = File.ReadAllText(Path.Combine(root, "Plugins", "example.hello", "HelloPlugin.cs"));
        Assert.DoesNotContain("LoadingAI.csproj", csproj);
        Assert.Contains("LoadingAI.Core.csproj", csproj);
        Assert.DoesNotContain("IRevitFullAccess", src);
        Assert.Contains("context.Bridge", src);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LoadingAI.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find LoadingAI.sln from " + AppContext.BaseDirectory);
    }
}
