using System.IO;
using Xunit;

namespace LoadingAI.Tests;

public class PluginProjectContractTests
{
    [Theory]
    [InlineData("Plugins/core-tools/CoreToolsPlugin.csproj")]
    [InlineData("Plugins/export-tools/ExportToolsPlugin.csproj")]
    [InlineData("Plugins/view-tools/ViewToolsPlugin.csproj")]
    [InlineData("Plugins/modeling-tools/ModelingToolsPlugin.csproj")]
    [InlineData("Plugins/structure-cuts/StructureCutsPlugin.csproj")]
    [InlineData("Plugins/qs-formwork/QsFormworkPlugin.csproj")]
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

    [Fact]
    public void NonFullAccessPlugins_DoNotMentionAutodeskRevit()
    {
        var pluginsDir = Path.Combine(RepoRoot(), "Plugins");
        Assert.True(Directory.Exists(pluginsDir), "Plugins/ missing");
        foreach (var dir in Directory.GetDirectories(pluginsDir))
        {
            var csFiles = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);
            if (csFiles.Length == 0) continue;
            var combined = string.Join("\n", csFiles.Select(File.ReadAllText));
            if (combined.Contains("IRevitFullAccess", StringComparison.Ordinal))
                continue;

            Assert.DoesNotContain("Autodesk.Revit", combined);
            foreach (var csprojPath in Directory.GetFiles(dir, "*.csproj", SearchOption.AllDirectories))
            {
                var csproj = File.ReadAllText(csprojPath);
                Assert.DoesNotContain("RevitAPI", csproj);
                Assert.DoesNotContain("Nice3point.Revit", csproj);
            }
        }
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
