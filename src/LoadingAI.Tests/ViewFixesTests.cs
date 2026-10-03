using System.IO;
using System.Linq;
using LoadingAI.Plugins;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

/// <summary>
/// The adapter needs a live document, so the Revit calls are asserted on source (CONTRIBUTING.md) and
/// checked for real by <c>PluginFixesInRevitTests</c>. The plugin side runs against the fake bridge.
/// </summary>
public class ViewFixesTests
{
    private static string Views() => AdapterSource.Containing("public Task<CommandResultDto> OverrideColorInViewAsync");

    private static string ColourTools() => SourceSlice.Between(Views(),
        "public Task<CommandResultDto> OverrideColorInViewAsync", "public Task<CommandResultDto> CreateSheetAsync");

    [Fact]
    public void EveryColourToolPaintsASolidFill()
    {
        var src = Views();
        Assert.Equal(3, SourceSlice.Count(src, "ColorOverride(doc,"));
        Assert.Equal(1, SourceSlice.Count(src, "SetSurfaceForegroundPatternColor("));
        Assert.Contains("SetSurfaceForegroundPatternId(", src);
        Assert.Contains("pattern.IsSolidFill", src);
    }

    [Fact]
    public void EveryOverrideGoesThroughTheLedger()
    {
        var tools = ColourTools();
        Assert.DoesNotContain("view.SetElementOverrides(", tools);
        Assert.DoesNotContain("view.SetCategoryOverrides(", tools);
        Assert.Contains("ApplyElementOverride(", tools);
        Assert.Contains("ApplyCategoryOverride(", tools);
        Assert.Contains("OverrideLedger.Remember(", Views());
    }

    [Fact]
    public void TheMisleadingResetHintIsGone()
    {
        var src = Views();
        Assert.DoesNotContain("say 'reset view' (reset_temporary_view)", src);
        Assert.Contains("reset_view_overrides", src);
    }

    [Fact]
    public void TagAll_PlacesEachTagOnItsElement_AndReportsWhatFailed()
    {
        var slice = SourceSlice.Between(Views(),
            "public Task<CommandResultDto> TagAllInActiveViewAsync", "private const int MaxTagAll");
        Assert.DoesNotContain("XYZ.Zero", slice);
        Assert.Contains("TagPoint(el, view)", slice);
        Assert.Contains("tagged == 0", slice);
        Assert.Contains("tx.RollBack();", slice);
        Assert.Contains("reasons.Add(", slice);
        Assert.DoesNotContain("catch { }", slice.Substring(0, slice.IndexOf("tx.Commit();", System.StringComparison.Ordinal)));
    }

    [Fact]
    public void Dimension_UsesFaceReferences_NotTheWallElement()
    {
        var slice = SourceSlice.Between(Views(),
            "public Task<CommandResultDto> CreateDimensionBetweenWallsAsync", "public Task<CommandResultDto> TagAllInActiveViewAsync");
        Assert.DoesNotContain("new Reference(w1)", slice);
        Assert.DoesNotContain("new Reference(w2)", slice);
        Assert.Contains("FacingFace(", slice);
        Assert.Contains("not parallel", slice);
        Assert.Contains("HostObjectUtils.GetSideFaces(", Views());
    }

    [Fact]
    public void CreateSheet_FindsTitleBlocksByCategory_AndListsWhatExists()
    {
        var slice = SourceSlice.Between(Views(),
            "public Task<CommandResultDto> CreateSheetAsync", "public Task<CommandResultDto> PlaceViewOnSheetAsync");
        Assert.DoesNotContain("FamilyName.Contains(\"Title\")", slice);
        Assert.Contains("OST_TitleBlocks", slice);
        Assert.Contains("Available:", slice);
    }

    [Fact]
    public void PlaceView_ChecksItCanBePlaced_AndPlacesSchedules()
    {
        var slice = SourceSlice.Between(Views(),
            "public Task<CommandResultDto> PlaceViewOnSheetAsync", "public Task<CommandResultDto> DuplicateViewAsync");
        Assert.Contains("Viewport.CanAddViewToSheet(", slice);
        Assert.Contains("ScheduleSheetInstance.Create(", slice);
    }

    [Fact]
    public void ReloadLinks_RunsOutsideATransaction_AndCountsOnlyRealLoads()
    {
        var src = AdapterSource.Containing("public Task<CommandResultDto> ReloadLinksAsync");
        var slice = SourceSlice.Between(src, "public Task<CommandResultDto> ReloadLinksAsync", "public Task<CommandResultDto> AssignWorksetAsync");
        Assert.DoesNotContain("new Transaction(", slice);
        Assert.Contains("LinkLoadResultType.LinkLoaded", slice);
        Assert.Contains("$\"Reloaded {ok}/{links.Count} RVT links\"", slice);
        Assert.Contains("failed.Add(", slice);
    }

    [Fact]
    public void TheToolTextNoLongerCallsSavedOverridesTemporary()
    {
        var text = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "Plugins", "core-tools", "Core", "ViewTools.cs"));
        Assert.DoesNotContain("Set temporary color override", text);
        Assert.DoesNotContain("pyRevit: Tag All", text);
        Assert.True(SourceSlice.Count(text, "reset_view_overrides") >= 4, "three colour tools, reset_temporary_view and the new tool name it");
    }

    private static ToolEntry Tool(FakeRevitBridge bridge, string name)
        => ViewTools.Create(new FakePluginContext(bridge)).First(e => e.Definition.Function.Name == name);

    [Fact]
    public async System.Threading.Tasks.Task ResetViewOverrides_IsAnL2Write_ThatCallsTheBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = Tool(bridge, "reset_view_overrides");

        var result = await tool.ExecuteAsync("{}");

        Assert.Equal(ToolTier.L2, tool.Tier);
        Assert.Equal(1, bridge.ResetViewOverridesCallCount);
        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(3, result.ChangedCount);
    }

    [Fact]
    public async System.Threading.Tasks.Task ResetViewOverrides_WithoutAModel_FailsWithoutClaimingAWrite()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };

        var result = await Tool(bridge, "reset_view_overrides").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
    }

    [Fact]
    public void TheShippedManifestListsTheNewTool()
    {
        var json = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "Plugins", "core-tools", "plugin.json"));
        Assert.Contains("\"name\": \"reset_view_overrides\"", json);
    }

    [Fact]
    public void ReloadLinks_SaysItIsNotAnUndoableEdit()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = DocumentTools.Create(new FakePluginContext(bridge)).First(e => e.Definition.Function.Name == "reload_links");

        Assert.NotNull(tool.Describe);
        var summary = tool.Describe!("{}").GetAwaiter().GetResult();
        Assert.Contains("Ctrl+Z will not reverse", summary);
    }
}
