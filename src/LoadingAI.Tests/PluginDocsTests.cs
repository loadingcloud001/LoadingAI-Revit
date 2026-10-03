using System.IO;

namespace LoadingAI.Tests;

public class PluginDocsTests
{
    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { AdapterSource.RepoRoot() }.Concat(parts).ToArray()));

    [Theory]
    [InlineData("Plugins", "README.md")]
    [InlineData("CONTRIBUTING.md")]
    public void Docs_DoNotClaimTheTrustPromptIsMissing(params string[] path)
    {
        var text = Read(path);
        Assert.DoesNotContain("is stage 2 and is not implemented yet", text);
        Assert.Contains("Trust this plugin", text);
    }

    [Theory]
    [InlineData("0010-plugin-load-allowlist.md")]
    [InlineData("0011-plugin-trust-prompt.md")]
    public void ShippedAdrsAreAccepted(string file)
        => Assert.Contains("- Status: Accepted", Read("docs", "adr", file));

    [Fact]
    public void AdrIndexMatchesTheFiles()
    {
        var index = Read("docs", "adr", "README.md");
        Assert.Contains("| [0010](0010-plugin-load-allowlist.md) | Plugin load SHA-256 allowlist (refuse unknown AppData DLL) | Accepted |", index);
        Assert.Contains("| [0011](0011-plugin-trust-prompt.md) | Plugin trust prompt (user-initiated allowlist write) | Accepted |", index);
        Assert.Contains("| [0013](0013-qs-formwork-structured-results.md) | qs-formwork structured results and preset forms | Accepted |", index);
        Assert.Contains("0015-confirm-l2-parameter-writes.md", index);
    }

    [Fact]
    public void PluginContractComment_NoLongerMentionsRawAccess()
        => Assert.DoesNotContain("raw access", Read("src", "LoadingAI.Core", "Plugins", "IRevitToolPlugin.cs"));

    [Fact]
    public void CompatFolderIsDocsOnly()
    {
        var dir = Path.Combine(AdapterSource.RepoRoot(), "src", "LoadingAI", "Plugins", "Compat");
        Assert.False(File.Exists(Path.Combine(dir, "manifest.json")), "nothing reads Compat/manifest.json");
        Assert.DoesNotContain("37 new + core 28", Read("src", "LoadingAI", "Plugins", "Compat", "README.md"));
    }

    [Fact]
    public void ExampleHello_TeachesTheCurrentInstallPath_AndDoesNotShipItsOwnCore()
    {
        var cs = Read("Plugins", "example.hello", "HelloPlugin.cs");
        Assert.Contains("Plugins\\dev", cs);
        Assert.Contains("Trust this plugin", cs);
        Assert.DoesNotContain("and restart Revit", cs);

        var csproj = Read("Plugins", "example.hello", "HelloPlugin.csproj");
        Assert.Contains("<CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>", csproj);
        Assert.Contains("<Private>false</Private>", csproj);
    }

    [Fact]
    public void RootReadme_DoesNotDescribeRevitUtilAsFullAccess()
        => Assert.DoesNotContain("Parse helpers for FullAccess plugins", Read("README.md"));
}
