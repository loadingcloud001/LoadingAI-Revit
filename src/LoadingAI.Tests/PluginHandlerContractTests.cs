using System.IO;

namespace LoadingAI.Tests;

public class PluginHandlerContractTests
{
    [Fact]
    public void OverlayTags_PreservesStructuredHandler()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Plugins", "Core", "PluginLoader.cs"));
        var slice = Slice(src, "internal static IEnumerable<ToolEntry> OverlayTags", "public static string Classify");
        Assert.Contains("StructuredHandler = e.StructuredHandler", slice);
    }

    [Fact]
    public void PluginTools_AreNotWrappedByAnyDialog_TheirHandlersRunAsWritten()
    {
        // ADR-0017: the host asks once, in chat, before the handler is called. Nothing wraps a handler.
        var guards = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Tools", "ToolGuards.cs"));
        Assert.DoesNotContain("Handler =", guards);
        Assert.DoesNotContain("TaskDialog", guards);
        var loader = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Plugins", "Core", "PluginLoader.cs"));
        Assert.DoesNotContain("ToolGuards.Apply", loader);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LoadingAI.sln")))
            dir = dir.Parent!;
        return dir!.FullName;
    }

    private static string Slice(string src, string start, string end)
    {
        var a = src.IndexOf(start, StringComparison.Ordinal);
        var b = src.IndexOf(end, a + start.Length, StringComparison.Ordinal);
        return src.Substring(a, b - a);
    }
}
