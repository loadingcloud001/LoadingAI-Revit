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
    public void PluginJson_IsVersion130()
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "structure-cuts", "plugin.json"));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("1.3.0", doc.RootElement.GetProperty("version").GetString());
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

    [Fact]
    public void ChatPresets_PickBeamsFloorsColumns_HostOwnedAndOptional()
    {
        var root = RepoRoot();
        var pluginDir = Path.Combine(root, "Plugins", "structure-cuts");
        var src = File.ReadAllText(Path.Combine(pluginDir, "StructureCutsPlugin.cs"));

        Assert.Contains("Kind = PresetFieldKind.PickElements", src, StringComparison.Ordinal);
        Assert.DoesNotContain("PresetFieldKind.PickBox", src, StringComparison.Ordinal);
        Assert.DoesNotContain("Required = true", src, StringComparison.Ordinal);
        Assert.DoesNotContain("Id = \"limit\"", src, StringComparison.Ordinal);

        foreach (var expected in new[]
        {
            "Id = \"beamIds\", Label = \"Beams\"",
            "Id = \"floorIds\", Label = \"Floors\"",
            "Id = \"columnIds\", Label = \"Columns\"",
            "Category = \"beams\"",
            "Category = \"floors\"",
            "Category = \"OST_StructuralColumns\""
        })
        {
            Assert.Contains(expected, src, StringComparison.Ordinal);
        }

        foreach (var file in Directory.GetFiles(pluginDir, "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("PickObjects", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Selection.Pick", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DryRunPreset_IsOptionalBooleanDefaultFalse()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "structure-cuts", "StructureCutsPlugin.cs"));
        var preset = "new PresetField { Id = \"dryRun\", Label = \"Dry-run (count only)\", Kind = PresetFieldKind.Boolean, Default = false }";
        Assert.Equal(2, CountOccurrences(src, preset));
        Assert.Contains("dryRun = new { type = \"boolean\"", src, StringComparison.Ordinal);
    }

    [Fact]
    public void DryRun_ReadsThroughSharedHelper_AndSkipsEveryWrite()
    {
        var root = RepoRoot();
        var pluginDir = Path.Combine(root, "Plugins", "structure-cuts");
        var src = File.ReadAllText(Path.Combine(pluginDir, "StructureCutsPlugin.cs"));

        // Same helper the host gates the L3 confirm with, so both sides agree.
        Assert.Equal(2, CountOccurrences(src, "ToolPresets.IsDryRun(json)"));

        var beam = File.ReadAllText(Path.Combine(pluginDir, "BeamSplit.cs"));
        var floor = File.ReadAllText(Path.Combine(pluginDir, "FloorSplit.cs"));

        Assert.Contains("bool dryRun = false", beam, StringComparison.Ordinal);
        Assert.Contains("bool dryRun = false", floor, StringComparison.Ordinal);
        Assert.Contains("Dry-run. ", beam, StringComparison.Ordinal);
        Assert.Contains("Dry-run. ", floor, StringComparison.Ordinal);

        // The transaction is only created on the write path, and the floor dry-run branch
        // returns before ParamCopy.Take / CreateFloor / doc.Delete.
        Assert.Contains("dryRun ? null : new Transaction(", beam, StringComparison.Ordinal);
        Assert.Contains("dryRun ? null : new Transaction(", floor, StringComparison.Ordinal);
        Assert.Contains("if (dryRun && error == \"dryrun\")", floor, StringComparison.Ordinal);

        var dryStart = floor.IndexOf("if (dryRun)", StringComparison.Ordinal);
        var writeStart = floor.IndexOf("var snap = ParamCopy.Take", StringComparison.Ordinal);
        Assert.True(dryStart >= 0 && writeStart > dryStart, "FloorSplit must branch on dryRun before the write path.");
        var dryBranch = floor.Substring(dryStart, writeStart - dryStart);
        Assert.Contains("\"dryrun\"", dryBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void PluginJson_DescribesDryRunOnBothTools()
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "structure-cuts", "plugin.json"));
        using var doc = JsonDocument.Parse(json);
        foreach (var tool in doc.RootElement.GetProperty("tools").EnumerateArray())
        {
            var desc = tool.GetProperty("description").GetString() ?? "";
            Assert.Contains("dryRun", desc, StringComparison.Ordinal);
            Assert.Contains("count only", desc, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
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
