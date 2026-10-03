using System.IO;
using LoadingAI.Plugins.Core;

namespace LoadingAI.Tests;

public class PluginRemovalTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "lai-remove-root");
    private static readonly string[] Roots = { Root };

    [Fact]
    public void AllowsADirectChildOfAPluginRoot()
        => Assert.True(PluginLoader.CanRemove("hello", Path.Combine(Root, "hello"), Roots, out _));

    [Fact]
    public void RefusesTheCorePlugin()
    {
        Assert.False(PluginLoader.CanRemove("core-tools", Path.Combine(Root, "core-tools"), Roots, out var why));
        Assert.Contains("core", why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesThePluginRootItself_LooseManifestCase()
    {
        Assert.False(PluginLoader.CanRemove("loose", Root, Roots, out var why));
        Assert.Contains("root", why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesTrailingSeparatorOnTheRoot()
        => Assert.False(PluginLoader.CanRemove("loose", Root + Path.DirectorySeparatorChar, Roots, out _));

    [Theory]
    [InlineData("nested")]
    [InlineData("outside")]
    [InlineData("")]
    public void RefusesAnythingElse(string kind)
    {
        var folder = kind switch
        {
            "nested" => Path.Combine(Root, "a", "b"),
            "outside" => Path.Combine(Path.GetTempPath(), "somewhere-else"),
            _ => ""
        };
        Assert.False(PluginLoader.CanRemove("x", folder, Roots, out _));
    }
}
