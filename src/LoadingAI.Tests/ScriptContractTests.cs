using System.IO;
using System.Linq;
using Xunit;

namespace LoadingAI.Tests;

/// <summary>
/// Pack/CI/release scripts. Core obj file-locks on the runner unless
/// SkipCopyAddin + SkipHostPluginBuild + -m:1 are on every sln build.
/// </summary>
public class ScriptContractTests
{
    [Fact]
    public void PackAddin_BuildsSerializedAndShipsLoader()
    {
        var ps1 = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "pack-addin.ps1"));
        Assert.Contains("-p:SkipCopyAddin=true", ps1);
        Assert.Contains("-p:SkipHostPluginBuild=true", ps1);
        Assert.Contains("-m:1", ps1);
        Assert.Contains("WebView2Loader.dll", ps1);
        Assert.Contains("Install.ps1", ps1);
        Assert.Contains("RevitAiPoc.addin", ps1);
        Assert.Contains("export-tools", ps1);
        Assert.Contains("view-tools", ps1);
        Assert.Contains("modeling-tools", ps1);
    }

    [Fact]
    public void CiYml_BuildAndTest_SerializeMsBuild()
    {
        var yml = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "ci.yml"));
        Assert.Contains("-p:SkipCopyAddin=true", yml);
        Assert.Contains("-p:SkipHostPluginBuild=true", yml);
        Assert.Contains(
            "dotnet build LoadingAI.sln -c Release -p:RevitVersion=${{ matrix.revit }} --no-restore -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true -m:1",
            yml);
        Assert.Contains(
            "dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj -c Release -p:RevitVersion=2027 -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true -m:1",
            yml);
    }

    [Fact]
    public void ReleaseYml_BuildAndPack_SerializeMsBuild()
    {
        var yml = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "release.yml"));
        Assert.Contains("-p:SkipCopyAddin=true", yml);
        Assert.Contains("-p:SkipHostPluginBuild=true", yml);
        Assert.Contains("-m:1", yml);
        Assert.Contains("pack-addin.ps1", yml);
        Assert.Contains("-SkipBuild", yml);
        Assert.Contains("LOADINGAI_DEFAULT_KEY", yml);
        Assert.Contains("LOADINGAI_DEFAULT_KEY is empty", yml);
    }

    [Fact]
    public void CoreCsproj_GenDefaultLlmKey_FailsClosed()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "LoadingAI.Core.csproj"));
        Assert.DoesNotContain("IgnoreExitCode", csproj);
        Assert.Contains("gen-default-llm-key.ps1", csproj);
    }

    [Fact]
    public void GenDefaultLlmKey_ReadsSaltFromDefaultLlmCs()
    {
        var cs = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Llm", "DefaultLlm.cs"));
        var ps1 = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "gen-default-llm-key.ps1"));
        const string salt = "LoadingAI.DefaultLlm.v1";
        Assert.Contains($"\"{salt}\"", cs);
        Assert.Contains("DefaultLlm.cs", ps1);
        Assert.Contains("Salt", ps1);
        Assert.DoesNotContain($"\"{salt}\"", ps1);
    }

    [Fact]
    public void CopyTargets_HonorSkipCopyAddin()
    {
        var host = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "LoadingAI.csproj"));
        Assert.Contains("Condition=\"'$(SkipCopyAddin)' != 'true'\"", host);
        Assert.Contains("Name=\"CopyWeb\" AfterTargets=\"CopyAddin\" Condition=\"'$(SkipCopyAddin)' != 'true'", host);
        foreach (var rel in new[]
        {
            "Plugins/export-tools/ExportToolsPlugin.csproj",
            "Plugins/view-tools/ViewToolsPlugin.csproj",
            "Plugins/modeling-tools/ModelingToolsPlugin.csproj"
        })
        {
            var csproj = File.ReadAllText(Path.Combine(RepoRoot(), rel.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Contains("Condition=\"'$(SkipCopyAddin)' != 'true'\"", csproj);
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
