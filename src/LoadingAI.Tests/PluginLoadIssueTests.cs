using System.IO;
using LoadingAI.Llm;
using LoadingAI.Plugins.Core;
using LoadingAI.Tools;
using Xunit;

namespace LoadingAI.Tests;

public class PluginLoadIssueTests
{
    [Fact]
    public void Classify_CoreMismatch_HintsRebuild()
    {
        var hint = PluginLoader.Classify("Could not load type 'LoadingAI.Geometry.PlanarTilt' from assembly 'LoadingAI.Core'");
        Assert.Equal("built against a different LoadingAI.Core — close Revit, rebuild and reinstall", hint);
    }

    [Fact]
    public void Classify_Unrelated_IsEmpty()
    {
        Assert.Equal("", PluginLoader.Classify("file not found"));
        Assert.Equal("", PluginLoader.Classify(""));
        Assert.Equal("", PluginLoader.Classify(null!));
    }

    [Fact]
    public void FormatLoaderExceptions_JoinsLoaderMessages()
    {
        var inner = new FileNotFoundException("Could not load type 'LoadingAI.Geometry.PlanarTilt'");
        var ex = new System.Reflection.ReflectionTypeLoadException(new Type?[] { null }, new Exception?[] { inner }, "partial type load");
        var detail = PluginLoader.FormatLoaderExceptions(ex);
        Assert.Contains("partial type load", detail);
        Assert.Contains("Could not load type 'LoadingAI.Geometry.PlanarTilt'", detail);
        Assert.Equal("built against a different LoadingAI.Core — close Revit, rebuild and reinstall", PluginLoader.Classify(detail));
    }

    [Fact]
    public void PluginLoader_NoToolsPath_RecordsIssue()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Plugins", "Core", "PluginLoader.cs"));
        Assert.Contains("if (tools.Count == 0)", src);
        Assert.Contains("no IRevitToolPlugin types found", src);
        Assert.Contains("plugin produced no tools", src);
        Assert.Contains("RecordIssue(folder, meta.Id, Path.GetFileName(dll), summary", src);
        Assert.Contains("loose manifest failed", src);
        Assert.Contains("DLL not on allowlist", src);
        Assert.Contains("record the SHA-256", src);
        Assert.Contains("put the plugin in {DevPluginsDir}", src);
        Assert.DoesNotContain(@"put the plugin in Plugins\dev", src);
        Assert.DoesNotContain("Directory.CreateDirectory(DevPluginsDir)", src);
    }

    [Fact]
    public void PluginLoader_AllowlistRefusal_CarriesSha256()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Plugins", "Core", "PluginLoader.cs"));
        Assert.Contains("if (!PluginAllowlist.Current.IsAllowed(dll, out var sha, out var why))", src);
        Assert.Contains(
            "RecordIssue(folder, meta.Id, Path.GetFileName(dll), \"DLL not on allowlist\", why, AllowlistRefusalHint, sha)",
            src);
    }

    [Fact]
    public void AllowlistRefusalHint_NamesAbsoluteDevPluginsDir()
    {
        var hint = PluginLoader.AllowlistRefusalHint;
        Assert.True(Path.IsPathRooted(PluginLoader.DevPluginsDir));
        Assert.EndsWith(Path.Combine("Plugins", "dev"), PluginLoader.DevPluginsDir);
        Assert.Contains(PluginLoader.DevPluginsDir, hint);
        Assert.Contains("record the SHA-256", hint);
    }

    [Fact]
    public void HostAndWeb_SurfaceLoadIssues()
    {
        var host = UiSrc();
        Assert.Contains("new FromHostMessage.Plugins", host);
        Assert.Contains("PluginLoader.Issues", host);
        Assert.Contains("EnvironmentWarnings", host);
        Assert.Contains("ApplyPluginLoadWarnings", host);

        var loop = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Llm", "AgentLoop.cs"));
        Assert.Contains("do not claim the capability does not exist", loop);
        Assert.Contains("These plugins failed to load", loop);

        var app = File.ReadAllText(Path.Combine(RepoRoot(), "web", "src", "App.tsx"));
        Assert.Contains("loadIssues", app);
        Assert.Contains("setLoadIssues", app);

        var shelf = File.ReadAllText(Path.Combine(RepoRoot(), "web", "src", "plugins", "PluginShelf.tsx"));
        Assert.Contains("loadIssues", shelf);
        Assert.Contains("failed to load", shelf);
        Assert.Contains("PluginLoadIssue", shelf);
    }

    private static string UiSrc()
    {
        var dir = Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui");
        return string.Join("\n", Directory.GetFiles(dir, "*.cs").Select(File.ReadAllText));
    }

    [Fact]
    public void BuildSystemPrompt_AppendsEnvironmentWarnings()
    {
        var tools = new List<ToolDefinition>
        {
            new() { Function = new ToolFunction { Name = "get_model_summary", Description = "Summary." } }
        };
        var prompt = AgentLoop.BuildSystemPrompt(tools, "structure-cuts: no IRevitToolPlugin types found");
        Assert.Contains("These plugins failed to load: structure-cuts: no IRevitToolPlugin types found", prompt);
        Assert.Contains("do not claim the capability does not exist", prompt);
        Assert.Contains("get_model_summary", prompt);

        var clean = AgentLoop.BuildSystemPrompt(tools);
        Assert.DoesNotContain("These plugins failed to load", clean);
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
