using System.IO;
using System.Text.Json;
using Xunit;

namespace LoadingAI.Tests;

public class StructureCutsContractTests
{
    [Fact]
    public void PluginJson_DeclaresTwoL3SplitToolsDefaultOff()
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "structure-cuts", "plugin.json"));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("structure-cuts", root.GetProperty("id").GetString());
        Assert.True(root.GetProperty("verified").GetBoolean());

        var tools = root.GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal(2, tools.Count);

        var names = tools.Select(t => t.GetProperty("name").GetString()).ToHashSet();
        Assert.Contains("split_beams_by_columns", names);
        Assert.Contains("split_floors_by_beams", names);

        foreach (var t in tools)
        {
            Assert.Equal("L3", t.GetProperty("tier").GetString());
            Assert.False(t.GetProperty("enabledByDefault").GetBoolean());
            var desc = t.GetProperty("description").GetString() ?? "";
            Assert.Contains("split", desc, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("instance parameters", desc, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void PluginJson_IsVersion111()
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "structure-cuts", "plugin.json"));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("1.1.1", doc.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void PluginClass_ReportsSameVersionAsPluginJson()
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "structure-cuts", "plugin.json"));
        using var doc = JsonDocument.Parse(json);
        var declared = doc.RootElement.GetProperty("version").GetString();

        var source = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "structure-cuts", "StructureCutsPlugin.cs"));
        Assert.Contains($"public string Version => \"{declared}\";", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PluginShipsIsExternalInitPolyfill_ForNet48InitAccessors()
    {
        var root = RepoRoot();
        var polyfillPath = Path.Combine(root, "Plugins", "structure-cuts", "IsExternalInit.cs");
        Assert.True(File.Exists(polyfillPath), "structure-cuts must ship its own IsExternalInit polyfill for Revit 2024 (net48).");

        var polyfill = File.ReadAllText(polyfillPath);
        Assert.Contains("#if NETFRAMEWORK", polyfill, StringComparison.Ordinal);
        Assert.Contains("namespace System.Runtime.CompilerServices", polyfill, StringComparison.Ordinal);
        Assert.Contains("class IsExternalInit", polyfill, StringComparison.Ordinal);

        var paramCopy = File.ReadAllText(Path.Combine(root, "Plugins", "structure-cuts", "ParamCopy.cs"));
        Assert.Contains("get; init;", paramCopy, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceLivesUnderPlugins_NotHostTools()
    {
        var root = RepoRoot();
        Assert.True(File.Exists(Path.Combine(root, "Plugins", "structure-cuts", "StructureCutsPlugin.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Plugins", "structure-cuts", "BeamSplit.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Plugins", "structure-cuts", "FloorSplit.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Plugins", "structure-cuts", "ParamCopy.cs")));
        Assert.False(File.Exists(Path.Combine(root, "src", "LoadingAI", "Tools", "StructureCutsPlugin.cs")));
        var hostTools = Directory.GetFiles(Path.Combine(root, "src", "LoadingAI", "Tools"), "*Split*.cs", SearchOption.AllDirectories);
        Assert.DoesNotContain(hostTools, f => Path.GetFileName(f).Contains("BeamSplit") || Path.GetFileName(f).Contains("FloorSplit"));
    }

    [Fact]
    public void SplitPaths_CallParamCopy_AndSkipExtension()
    {
        var root = RepoRoot();
        var helper = File.ReadAllText(Path.Combine(root, "Plugins", "structure-cuts", "ParamCopy.cs"));
        Assert.Contains("Extension", helper, StringComparison.Ordinal);
        Assert.Contains("ParamCopy.", File.ReadAllText(Path.Combine(root, "Plugins", "structure-cuts", "BeamSplit.cs")));
        Assert.Contains("ParamCopy.", File.ReadAllText(Path.Combine(root, "Plugins", "structure-cuts", "FloorSplit.cs")));
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
