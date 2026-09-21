using System.IO;
using System.Text.Json;
using Xunit;

namespace LoadingAI.Tests;

public class StructureCutsContractTests
{
    [Fact]
    public void PluginJson_DeclaresFiveL3SplitToolsDefaultOff()
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "structure-cuts", "plugin.json"));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("structure-cuts", root.GetProperty("id").GetString());
        Assert.True(root.GetProperty("verified").GetBoolean());

        var tools = root.GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal(5, tools.Count);

        var names = tools.Select(t => t.GetProperty("name").GetString()).ToHashSet();
        Assert.Contains("split_beams_by_columns", names);
        Assert.Contains("split_floors_by_beams", names);
        Assert.Contains("split_by_levels", names);
        Assert.Contains("split_by_grids", names);
        Assert.Contains("split_secondary_beams_by_main_beams", names);

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
    public void PluginJson_IsVersion201()
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "structure-cuts", "plugin.json"));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("2.0.1", doc.RootElement.GetProperty("version").GetString());
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
    public void HostShipsIsExternalInitPolyfill_ForNet48InitAccessors()
    {
        var root = RepoRoot();
        var polyfillPath = Path.Combine(root, "src", "LoadingAI", "IsExternalInit.cs");
        Assert.True(File.Exists(polyfillPath), "src/LoadingAI must ship its own IsExternalInit polyfill for Revit 2024 (net48).");

        var polyfill = File.ReadAllText(polyfillPath);
        Assert.Contains("#if NETFRAMEWORK", polyfill, StringComparison.Ordinal);
        Assert.Contains("namespace System.Runtime.CompilerServices", polyfill, StringComparison.Ordinal);
        Assert.Contains("class IsExternalInit", polyfill, StringComparison.Ordinal);

        // The relocated algorithms declare the init accessors now, so they carry the polyfill. The
        // plugin only consumes those properties through an object initializer, which needs none.
        Assert.False(File.Exists(Path.Combine(root, "Plugins", "structure-cuts", "IsExternalInit.cs")));

        var paramCopy = File.ReadAllText(Path.Combine(HostDir(root), "ParamCopy.cs"));
        Assert.Contains("get; init;", paramCopy, StringComparison.Ordinal);
    }

    [Fact]
    public void AlgorithmsLiveHostSide_NotUnderPlugins()
    {
        var root = RepoRoot();
        var hostDir = HostDir(root);
        foreach (var algorithm in new[]
        {
            "BeamSplit.cs",
            "CurveMemberSplit.cs",
            "FloorSplit.cs",
            "GeometryRevit.cs",
            "GridSplit.cs",
            "HostCurve.cs",
            "LevelSplit.cs",
            "ParamCopy.cs",
            "SplitElements.cs",
            "SplitScope.cs"
        })
        {
            Assert.True(File.Exists(Path.Combine(hostDir, algorithm)), $"{algorithm} must live host-side.");
        }

        // Only the handler is left under Plugins, which is what lets the plugin drop the Revit API.
        var pluginSources = Directory.GetFiles(Path.Combine(root, "Plugins", "structure-cuts"), "*.cs")
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal);
        Assert.Equal(new[] { "StructureCutsPlugin.cs" }, pluginSources);

        Assert.True(File.Exists(Path.Combine(root, "Plugins", "structure-cuts", "StructureCutsPlugin.cs")));
        Assert.True(File.Exists(Path.Combine(root, "src", "LoadingAI.RevitTests", "StructureCutsInRevitTests.cs")));
        var revitTests = File.ReadAllText(Path.Combine(root, "src", "LoadingAI.RevitTests", "LoadingAI.RevitTests.csproj"));
        Assert.Contains("Plugins\\structure-cuts\\StructureCutsPlugin.csproj", revitTests);
        Assert.False(File.Exists(Path.Combine(root, "src", "LoadingAI", "Tools", "StructureCutsPlugin.cs")));
        var hostTools = Directory.GetFiles(Path.Combine(root, "src", "LoadingAI", "Tools"), "*Split*.cs", SearchOption.AllDirectories);
        Assert.DoesNotContain(hostTools, f => Path.GetFileName(f).Contains("BeamSplit") || Path.GetFileName(f).Contains("FloorSplit"));
    }

    [Fact]
    public void SplitPaths_CallParamCopy_AndSkipExtension()
    {
        var root = RepoRoot();
        var hostDir = HostDir(root);
        var helper = File.ReadAllText(Path.Combine(hostDir, "ParamCopy.cs"));
        Assert.Contains("Extension", helper, StringComparison.Ordinal);
        Assert.Contains("ParamCopy.", File.ReadAllText(Path.Combine(hostDir, "CurveMemberSplit.cs")));
        Assert.Contains("ParamCopy.", File.ReadAllText(Path.Combine(hostDir, "FloorSplit.cs")));
    }

    [Fact]
    public void ChatPresets_PickBeamsFloorsColumns_HostOwnedAndOptional()
    {
        var root = RepoRoot();
        var pluginDir = Path.Combine(root, "Plugins", "structure-cuts");
        var hostDir = HostDir(root);
        var src = File.ReadAllText(Path.Combine(pluginDir, "StructureCutsPlugin.cs"));

        Assert.Contains("Kind = PresetFieldKind.PickElements", src, StringComparison.Ordinal);
        Assert.DoesNotContain("PresetFieldKind.PickBox", src, StringComparison.Ordinal);
        Assert.DoesNotContain("Required = true", src, StringComparison.Ordinal);

        Assert.Contains("SharedKnobs()", src, StringComparison.Ordinal);
        Assert.Contains("Id = \"scope\"", src, StringComparison.Ordinal);
        Assert.Contains("Id = \"continueFrom\"", src, StringComparison.Ordinal);
        Assert.Contains("Kind = PresetFieldKind.Select", src, StringComparison.Ordinal);
        var limitPreset = "new PresetField { Id = \"limit\", Label = \"Max elements\", Kind = PresetFieldKind.Number, Default = SplitOptions.DefaultLimit }";
        Assert.Contains(limitPreset, src, StringComparison.Ordinal);

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

        // The algorithms moved host-side, so the sweep has to cover both trees: plugin-only would
        // still pass after the move while proving nothing about the code that actually picks.
        // 11 algorithms (including BeamCut for secondary-by-main) plus the two helper types
        // lifted out of SplitOptions.cs and the outcome collector the Adapter reads counters through.
        Assert.Equal(14, Directory.GetFiles(hostDir, "*.cs").Length);
        foreach (var file in SweptSources(root))
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
        Assert.Contains(preset, src, StringComparison.Ordinal);
        Assert.Contains("dryRun = DryRunProperty()", src, StringComparison.Ordinal);
    }

    [Fact]
    public void DryRun_ReadsThroughSharedHelper_AndSkipsEveryWrite()
    {
        var root = RepoRoot();
        var pluginDir = Path.Combine(root, "Plugins", "structure-cuts");
        var hostDir = HostDir(root);
        var src = File.ReadAllText(Path.Combine(pluginDir, "StructureCutsPlugin.cs"));

        // Same helper the host gates the L3 confirm with, so both sides agree. The plugin reads it once,
        // in the Options factory that both split tools share, instead of parsing the flag per tool.
        Assert.Equal(1, CountOccurrences(src, "ToolPresets.IsDryRun(json)"));
        Assert.Contains("DryRun = ToolPresets.IsDryRun(json)", src, StringComparison.Ordinal);

        var beam = File.ReadAllText(Path.Combine(hostDir, "BeamSplit.cs"));
        var floor = File.ReadAllText(Path.Combine(hostDir, "FloorSplit.cs"));
        var levels = File.ReadAllText(Path.Combine(hostDir, "LevelSplit.cs"));
        var grids = File.ReadAllText(Path.Combine(hostDir, "GridSplit.cs"));

        Assert.Contains("Dry-run. ", beam, StringComparison.Ordinal);
        Assert.Contains("Dry-run. ", floor, StringComparison.Ordinal);
        Assert.Contains("Dry-run. ", levels, StringComparison.Ordinal);
        Assert.Contains("Dry-run. ", grids, StringComparison.Ordinal);

        // A dry run leaves before a transaction is even opened, so nothing that writes can be reached:
        // the only piece of the split that touches the document runs inside that transaction.
        AssertOrdered("BeamSplit.cs", beam,
            "if (options.DryRun)", "SplitElements.WriteTx(", "SplitAtCuts(doc,");
        AssertOrdered("FloorSplit.cs", floor,
            "if (options.DryRun)", "SplitElements.WriteTx(", "Write(doc, plan, options)");
        AssertOrdered("LevelSplit.cs", levels,
            "if (options.DryRun)", "SplitElements.WriteTx(", "SplitAtCuts(doc,");
        AssertOrdered("GridSplit.cs", grids,
            "if (options.DryRun)", "SplitElements.WriteTx(", "SplitAtCuts(doc,");

        // The only Transaction constructor in the split code is WriteTx. Tools must not open their own.
        Assert.Contains("new Transaction(", File.ReadAllText(Path.Combine(hostDir, "SplitElements.cs")), StringComparison.Ordinal);
        foreach (var file in SweptSources(root))
        {
            if (string.Equals(Path.GetFileName(file), "SplitElements.cs", StringComparison.Ordinal))
                continue;
            Assert.DoesNotContain("new Transaction(", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BothTools_ExposeUniqueMarkAndCopyParams_WithSharedDefaults()
    {
        var root = RepoRoot();
        var pluginDir = Path.Combine(root, "Plugins", "structure-cuts");
        var src = File.ReadAllText(Path.Combine(pluginDir, "StructureCutsPlugin.cs"));

        Assert.Contains("Id = \"uniqueMark\"", src, StringComparison.Ordinal);
        Assert.Contains("Id = \"copyParams\"", src, StringComparison.Ordinal);
        Assert.Contains("uniqueMark = UniqueMarkProperty()", src, StringComparison.Ordinal);
        Assert.Contains("copyParams = CopyParamsProperty()", src, StringComparison.Ordinal);

        // One shared Options factory, so the two tools cannot drift apart on the defaults.
        Assert.Contains("UniqueMark = args.Bool(\"uniqueMark\", false)", src, StringComparison.Ordinal);
        Assert.Contains("CopyParams = args.Bool(\"copyParams\", true)", src, StringComparison.Ordinal);

        // A slab that cannot be rebuilt exactly is left alone and named by reason, never guessed at,
        // and a member Revit can split itself is split rather than re-created.
        var floor = File.ReadAllText(Path.Combine(HostDir(root), "FloorSplit.cs"));
        Assert.Contains("GeometryRevit.TrySlabRegion", floor, StringComparison.Ordinal);
        Assert.Contains("SlopeArrowFor", floor, StringComparison.Ordinal);
        Assert.Contains("Floor.Create(doc,", floor, StringComparison.Ordinal);
        Assert.Contains("Instance parameters copied", floor, StringComparison.Ordinal);
        Assert.DoesNotContain("copied to new pieces", floor, StringComparison.OrdinalIgnoreCase);
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
        var hostDir = HostDir(RepoRoot());
        var beam = File.ReadAllText(Path.Combine(hostDir, "CurveMemberSplit.cs"));

        // Revit's own split keeps the member, its host and its parameters, so it runs before the
        // fallback, which re-creates pieces and has to copy parameters across by hand. Native Split
        // aborts Revit 2024–2027 on grouped/pinned/joined/non-line members, so CanNativeSplit
        // gates the call; SubTransaction still wraps Split for catchable failures.
        Assert.Contains("CanNativeSplit(FamilyInstance instance)", beam, StringComparison.Ordinal);
        Assert.Contains("CanNativeSplit(current)", beam, StringComparison.Ordinal);
        Assert.Contains("CanNativeSplit(instance, requireUnjoined: false)", beam, StringComparison.Ordinal);
        Assert.Contains("instance.CanSplit", beam, StringComparison.Ordinal);
        Assert.Contains("instance.Pinned", beam, StringComparison.Ordinal);
        Assert.Contains("instance.GroupId", beam, StringComparison.Ordinal);
        Assert.Contains("is not Line && location.Curve is not Arc", beam, StringComparison.Ordinal);
        Assert.Contains("Joined(location)", beam, StringComparison.Ordinal);
        Assert.Contains("get_ElementsAtJoin", beam, StringComparison.Ordinal);
        // Join-status throw must count as joined so native Split is never called on an unknown member.
        Assert.Contains("Unknown join status is treated as joined", beam, StringComparison.Ordinal);
        // Joined members unjoin first; Split runs only after joins are actually gone.
        // Framing joins that meet columns live in get_ElementsAtJoin; UnjoinGeometry is not enough.
        Assert.Contains("StructuralFramingUtils.DisallowJoinAtEnd", beam, StringComparison.Ordinal);
        Assert.Contains("JoinGeometryUtils.AreElementsJoined", beam, StringComparison.Ordinal);
        Assert.Contains("JoinGeometryUtils.UnjoinGeometry", beam, StringComparison.Ordinal);
        Assert.Contains("TryUnjoinEnds", beam, StringComparison.Ordinal);
        Assert.Contains("requireUnjoined: false", beam, StringComparison.Ordinal);
        AssertOrdered("CurveMemberSplit.cs", beam,
            "TryUnjoinEnds(doc, current, location)",
            ".Split(local)");
        Assert.Contains(".Split(local)", beam, StringComparison.Ordinal);
        Assert.Contains("SplitSequence.Build(", beam, StringComparison.Ordinal);
        Assert.Contains("originalStart = live.GetEndPoint(0)", beam, StringComparison.Ordinal);
        Assert.Contains("WriteTx", File.ReadAllText(Path.Combine(hostDir, "SplitElements.cs")), StringComparison.Ordinal);
        AssertOrdered("CurveMemberSplit.cs", beam,
            "TryNativeSplit(doc, plan, options, out var ids)",
            "Recreate(doc, plan, options)");

        // The native path is the one Revit keeps the parameters for, so the note that says parameters
        // were copied across belongs to the fallback.
        Assert.Contains("Native split preserved parameters.", beam, StringComparison.Ordinal);
        Assert.Contains("ParamCopy.Apply(snapshot, made, markFor)", beam, StringComparison.Ordinal);
        Assert.Contains("Instance parameters copied; {paramSkipped} parameter(s) skipped.", beam, StringComparison.Ordinal);
        Assert.DoesNotContain("copied to new pieces", beam, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unsupported", beam, StringComparison.Ordinal);

        // Nothing splits without a sub-transaction to undo it, and the document is never regenerated
        // from inside the outer transaction.
        Assert.Contains("new SubTransaction(doc)", beam, StringComparison.Ordinal);
        Assert.DoesNotContain("Regenerate()", beam, StringComparison.Ordinal);

        // Cut clustering lives in one place so Beam/Level/Grid cannot drift.
        Assert.Contains("public static List<double> DistinctCuts(", beam, StringComparison.Ordinal);
        Assert.DoesNotContain("private static List<double> DistinctCuts(", File.ReadAllText(Path.Combine(hostDir, "BeamSplit.cs")), StringComparison.Ordinal);
        Assert.Contains("CurveMemberSplit.DistinctCuts(", File.ReadAllText(Path.Combine(hostDir, "BeamSplit.cs")), StringComparison.Ordinal);
        Assert.Contains("CurveMemberSplit.DistinctCuts(", File.ReadAllText(Path.Combine(hostDir, "LevelSplit.cs")), StringComparison.Ordinal);
        Assert.Contains("CurveMemberSplit.DistinctCuts(", File.ReadAllText(Path.Combine(hostDir, "GridSplit.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void PointHostedColumns_AreSkippedAtPlanTime_NotWritten()
    {
        var root = RepoRoot();
        var hostDir = HostDir(root);
        var hostCurve = File.ReadAllText(Path.Combine(hostDir, "HostCurve.cs"));
        Assert.Contains("public static bool CanWrite(Element host)", hostCurve, StringComparison.Ordinal);
        Assert.Contains("LocationCurveOf(host) != null", hostCurve, StringComparison.Ordinal);

        // A synthesised vertical is only for sampling LocationPoint columns; write still needs a
        // LocationCurve, so level/grid plan must skip those hosts instead of over-promising.
        var levels = File.ReadAllText(Path.Combine(hostDir, "LevelSplit.cs"));
        var grids = File.ReadAllText(Path.Combine(hostDir, "GridSplit.cs"));
        Assert.Contains("HostCurve.CanWrite(host)", levels, StringComparison.Ordinal);
        Assert.Contains("HostCurve.CanWrite(host)", grids, StringComparison.Ordinal);
        AssertOrdered("LevelSplit.cs", levels, "if (!HostCurve.CanWrite(host))", "PlanCurve(host,");
        AssertOrdered("GridSplit.cs", grids, "if (!HostCurve.CanWrite(host))", "PlanCurve(host,");
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

    /// <summary>Where the split algorithms live after the Bridge port.</summary>
    private static string HostDir(string root) => Path.Combine(root, "src", "LoadingAI", "Revit", "StructureCuts");

    /// <summary>Every source file that can hold split code: the thin plugin plus the host algorithms.</summary>
    private static IEnumerable<string> SweptSources(string root) =>
        Directory.GetFiles(Path.Combine(root, "Plugins", "structure-cuts"), "*.cs")
            .Concat(Directory.GetFiles(HostDir(root), "*.cs"));

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
