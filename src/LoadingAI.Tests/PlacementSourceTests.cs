using System.IO;
using System.Linq;
using LoadingAI.Plugins;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

/// <summary>
/// The adapter needs a live document, so these assert on its source (CONTRIBUTING.md) and the plugin
/// side with the fake bridge. The behaviour itself is checked by <c>PluginFixesInRevitTests</c>.
/// </summary>
public class PlacementSourceTests
{
    private static string Create() => AdapterSource.Containing("public Task<CommandResultDto> CreateFamilyInstanceAsync");

    private static string FamilyInstanceMethod() => SourceSlice.Between(Create(),
        "public Task<CommandResultDto> CreateFamilyInstanceAsync", "public Task<CommandResultDto> DuplicateElementAsync");

    private static string ArrayMethod() => SourceSlice.Between(Create(),
        "public Task<CommandResultDto> CreateFamilyInstanceArrayAsync", "public Task<CommandResultDto> RotateElementsAsync");

    [Fact]
    public void Activation_HappensInsideTheTransaction()
    {
        foreach (var slice in new[] { FamilyInstanceMethod(), ArrayMethod() })
        {
            var start = slice.IndexOf("tx.Start();", System.StringComparison.Ordinal);
            var activate = slice.IndexOf("Activate();", System.StringComparison.Ordinal);
            Assert.True(start >= 0 && activate > start, "Activate() must come after tx.Start().");
        }
    }

    [Fact]
    public void NoSilentFallbackToTheFirstLevelOrAnyFamily()
    {
        foreach (var slice in new[] { FamilyInstanceMethod(), ArrayMethod() })
        {
            Assert.DoesNotContain("lvl ??=", slice);
            Assert.DoesNotContain("level ??=", slice);
            Assert.Contains("ResolvePlacement(", slice);
        }

        var all = Create();
        Assert.Contains("LevelNames.ResolveStrict(", all);
        Assert.Contains("SymbolPick.Choose(", all);
        Assert.Contains("PlacementRules.Refusal(", all);
    }

    [Fact]
    public void ZIsAHeightAboveTheLevel()
    {
        Assert.Contains("lvl!.Elevation + Metric.MmToFt(z)", FamilyInstanceMethod());
        Assert.Contains("level!.Elevation", ArrayMethod());
    }

    [Fact]
    public void TheArrayCountIsRefusedNotClamped()
    {
        var slice = ArrayMethod();
        Assert.DoesNotContain("Polyfills.Clamp(count", slice);
        Assert.Contains("count must be between 1 and", slice);
    }

    [Fact]
    public void TheWallDefaultIsABasicTypeWithAHeightFromTheCaller()
    {
        var slice = SourceSlice.Between(Create(),
            "public Task<CommandResultDto> CreateWallAsync", "public Task<CommandResultDto> CreateFamilyInstanceAsync");
        Assert.Contains("WallKind.Basic", slice);
        Assert.Contains("heightMm ?? DefaultWallHeightMm", slice);
        Assert.DoesNotContain("level.Id, 10, 0", slice);
    }

    [Fact]
    public async System.Threading.Tasks.Task CreateWall_ForwardsTheHeight_AndOmittingItPassesNull()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = GeometryTools.Create(new FakePluginContext(bridge)).First(e => e.Definition.Function.Name == "create_wall");

        await tool.ExecuteAsync("""{"levelName":"Level 1","x1":0,"y1":0,"x2":4000,"y2":0,"heightMm":3500}""");
        await tool.ExecuteAsync("""{"levelName":"Level 1","x1":0,"y1":0,"x2":4000,"y2":0}""");

        Assert.Equal(new double?[] { 3500, null }, bridge.CreateWallHeights);
    }

    [Fact]
    public void TheToolSchemasTellTheModelWhatChanged()
    {
        var text = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "Plugins", "core-tools", "Core", "GeometryTools.cs"));
        Assert.Contains("heightMm = new { type = \"number\"", text);
        Assert.Contains("height above the level", text);
        Assert.Contains("Hosted families", text);
    }
}
