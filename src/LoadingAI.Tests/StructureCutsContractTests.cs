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
    public void PluginJson_IsVersion150()
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "structure-cuts", "plugin.json"));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("1.5.0", doc.RootElement.GetProperty("version").GetString());
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
        Assert.True(File.Exists(Path.Combine(root, "Plugins", "structure-cuts", "SplitOptions.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Plugins", "structure-cuts", "GeometryRevit.cs")));
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

        // The element cap is host-owned too: a Number field both tools share, clamped by the plugin.
        var limitPreset = "new PresetField { Id = \"limit\", Label = \"Max elements\", Kind = PresetFieldKind.Number, Default = SplitOptions.DefaultLimit }";
        Assert.Equal(2, CountOccurrences(src, limitPreset));

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

        // Same helper the host gates the L3 confirm with, so both sides agree. The plugin reads it once,
        // in the Options factory that both split tools share, instead of parsing the flag per tool.
        Assert.Equal(1, CountOccurrences(src, "ToolPresets.IsDryRun(json)"));
        Assert.Contains("DryRun = ToolPresets.IsDryRun(json)", src, StringComparison.Ordinal);

        var beam = File.ReadAllText(Path.Combine(pluginDir, "BeamSplit.cs"));
        var floor = File.ReadAllText(Path.Combine(pluginDir, "FloorSplit.cs"));

        Assert.Contains("Dry-run. ", beam, StringComparison.Ordinal);
        Assert.Contains("Dry-run. ", floor, StringComparison.Ordinal);

        // A dry run leaves before a transaction is even opened, so nothing that writes can be reached:
        // the only piece of the split that touches the document runs inside that transaction.
        AssertOrdered("BeamSplit.cs", beam,
            "if (options.DryRun)", "new Transaction(", "SplitAtCuts(doc, plan, options)");
        AssertOrdered("FloorSplit.cs", floor,
            "if (options.DryRun)", "new Transaction(", "Write(doc, plan, options)");
    }

    [Fact]
    public void BothTools_ExposeUniqueMarkAndCopyParams_WithSharedDefaults()
    {
        var root = RepoRoot();
        var pluginDir = Path.Combine(root, "Plugins", "structure-cuts");
        var src = File.ReadAllText(Path.Combine(pluginDir, "StructureCutsPlugin.cs"));

        Assert.Equal(2, CountOccurrences(src, "Id = \"uniqueMark\""));
        Assert.Equal(2, CountOccurrences(src, "Id = \"copyParams\""));
        Assert.Contains("uniqueMark = new { type = \"boolean\"", src, StringComparison.Ordinal);
        Assert.Contains("copyParams = new { type = \"boolean\"", src, StringComparison.Ordinal);

        // One shared Options factory, so the two tools cannot drift apart on the defaults.
        Assert.Contains("UniqueMark = args.Bool(\"uniqueMark\", false)", src, StringComparison.Ordinal);
        Assert.Contains("CopyParams = args.Bool(\"copyParams\", true)", src, StringComparison.Ordinal);

        // A slab that cannot be rebuilt exactly is left alone and named by reason, never guessed at,
        // and a member Revit can split itself is split rather than re-created.
        var floor = File.ReadAllText(Path.Combine(pluginDir, "FloorSplit.cs"));
        Assert.Contains("GeometryRevit.TrySlabRegion", floor, StringComparison.Ordinal);
        Assert.Contains("SlopeArrowFor", floor, StringComparison.Ordinal);
        Assert.Contains("Floor.Create(doc,", floor, StringComparison.Ordinal);
        Assert.DoesNotContain("unsupported", floor, StringComparison.Ordinal);

        // Each reason a slab is left alone is named, so a run says what it could not do and why.
        foreach (var reason in new[]
        {
            "\"shape-edited\"",
            "\"no-planar-face\"",
            "\"sloped-mismatch\"",
            "\"no-level\"",
            "\"no-regions\"",
            "\"unchanged\""
        })
        {
            Assert.Contains(reason, floor, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BeamSplit_UsesRevitNativeSplitBeforeRecreatingPieces()
    {
        var beam = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "structure-cuts", "BeamSplit.cs"));

        // Revit's own split keeps the member, its host and its parameters, so it runs before the
        // fallback, which re-creates pieces and has to copy parameters across by hand.
        Assert.Contains("instance.CanSplit", beam, StringComparison.Ordinal);
        Assert.Contains(".Split(local)", beam, StringComparison.Ordinal);
        Assert.Contains("SplitSequence.Build(", beam, StringComparison.Ordinal);
        AssertOrdered("BeamSplit.cs", beam,
            "TryNativeSplit(doc, plan, options, out var ids)",
            "Recreate(doc, plan, options)");

        // The native path is the one Revit keeps the parameters for, so the note that says parameters
        // were copied across belongs to the fallback.
        Assert.Contains("Native split preserved parameters.", beam, StringComparison.Ordinal);
        Assert.Contains("ParamCopy.Apply(snapshot, made, markFor)", beam, StringComparison.Ordinal);
        Assert.DoesNotContain("unsupported", beam, StringComparison.Ordinal);

        // Nothing splits without a sub-transaction to undo it, and the document is never regenerated
        // from inside the outer transaction.
        Assert.Contains("new SubTransaction(doc)", beam, StringComparison.Ordinal);
        Assert.DoesNotContain("Regenerate()", beam, StringComparison.Ordinal);
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

    private static void AssertOrdered(string file, string text, params string[] needles)
    {
        var cursor = -1;
        foreach (var needle in needles)
        {
            var index = text.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(index > cursor, $"{file}: expected '{needle}' after the previous step.");
            cursor = index;
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
