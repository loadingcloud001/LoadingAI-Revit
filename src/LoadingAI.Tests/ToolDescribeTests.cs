using System.IO;
using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class ToolDescribeTests
{
    private static ToolEntry Param(string name, FakeRevitBridge bridge)
        => ParamTools.Create(new FakePluginContext(bridge)).First(e => e.Definition.Function.Name == name);

    private static FakeRevitBridge Open() => new() { HasActiveDocument = true };

    [Theory]
    [InlineData("set_parameter_by_id")]
    [InlineData("set_comments_on_selection")]
    [InlineData("copy_params")]
    [InlineData("replace_text")]
    [InlineData("numbering_auto")]
    public void TheFiveParameterWriters_AreConfirmedAndDescribed(string tool)
    {
        var entry = Param(tool, Open());
        Assert.Equal(ToolTier.L2, entry.Tier);
        Assert.False(entry.RequiresWriteConfirm, tool + " must get the host confirm");
        Assert.NotNull(entry.Describe);
    }

    [Fact]
    public void IsolateInView_StaysQuiet()
    {
        var entry = ViewTools.Create(new FakePluginContext(Open())).First(e => e.Definition.Function.Name == "isolate_in_view");
        Assert.True(entry.RequiresWriteConfirm);
    }

    [Fact]
    public async Task CopyParams_SaysWhatWouldChange()
    {
        var bridge = Open();
        bridge.NextExportParams = new ParamExportDto("Doors", new[] { "Comments", "Mark" }, new[]
        {
            new ParamExportRowDto("1", new[] { "A-01", "" }),
            new ParamExportRowDto("2", new[] { "A-02", "A-02" }),
            new ParamExportRowDto("3", new[] { "", "X" })
        }, null, 3);

        var text = await Param("copy_params", bridge).Describe!(
            """{"category":"Doors","sourceParam":"Comments","targetParam":"Mark"}""");

        Assert.NotNull(text);
        Assert.Contains("1 of 3", text);
        Assert.Contains("\"\" → \"A-01\"", text);
    }

    [Fact]
    public async Task ReplaceText_PreviewsTheRegexAndFlagsASlowPattern()
    {
        var bridge = Open();
        bridge.NextExportParams = new ParamExportDto("Doors", new[] { "Mark" }, new[]
        {
            new ParamExportRowDto("1", new[] { "D-1" }),
            new ParamExportRowDto("2", new[] { "W-2" })
        }, null, 2);
        var tool = Param("replace_text", bridge);

        var ok = await tool.Describe!("""{"category":"Doors","paramName":"Mark","pattern":"^D-","replacement":"DR-"}""");
        Assert.Contains("1 of 2", ok);
        Assert.Contains("\"D-1\" → \"DR-1\"", ok);

        var bad = await tool.Describe!("""{"category":"Doors","paramName":"Mark","pattern":"([x","replacement":"y"}""");
        Assert.Contains("cannot run", bad);
    }

    [Fact]
    public async Task SetComments_CountsTheSelection()
    {
        var bridge = Open();
        bridge.Selection = new SelectionSummaryDto(2, new[] { "Walls", "Doors" }, new[] { "1", "2" });
        var text = await Param("set_comments_on_selection", bridge).Describe!("""{"text":"checked"}""");
        Assert.Contains("2 selected", text);
        Assert.Contains("\"checked\"", text);
        Assert.Contains("overwritten", text);
    }

    [Fact]
    public async Task NumberingAuto_NamesTheFirstAndLastMark()
    {
        var bridge = Open();
        bridge.NextFind = new FindResultDto(12, 0, null, Array.Empty<ElementSummaryDto>());
        var text = await Param("numbering_auto", bridge).Describe!("""{"category":"Doors","prefix":"D-","start":5,"pad":3}""");
        Assert.Contains("12 Doors", text);
        Assert.Contains("D-005", text);
        Assert.Contains("D-016", text);
    }

    [Fact]
    public async Task SetParameterById_ShowsBeforeAndAfter()
    {
        var bridge = Open();
        bridge.NextElementDetails = new ElementDetailsDto("101", "Basic Wall", "Walls", "Generic - 200", "Level 1", "-",
            new[] { new ParameterValueDto("Comments", "old", false, "String") }, 1, null);
        var text = await Param("set_parameter_by_id", bridge).Describe!(
            """{"elementId":"101","paramName":"Comments","value":"new"}""");
        Assert.Contains("\"old\" → \"new\"", text);
        Assert.Contains("Walls", text);
    }

    [Fact]
    public void ConfirmBody_PrefersTheSummary()
        => Assert.Equal("Set 3", ToolGuards.ConfirmBody("Set 3", "{}"));

    [Fact]
    public void ConfirmBody_FallsBackToTheArguments()
    {
        var body = ToolGuards.ConfirmBody(null, """{"a":1}""");
        Assert.Contains("""{"a":1}""", body);
        Assert.Contains("undoable transaction", body);
    }

    [Fact]
    public async Task DescribeAsync_SwallowsAThrow_AndTruncates()
    {
        var thrower = new ToolEntry { Describe = _ => throw new InvalidOperationException("boom") };
        Assert.Null(await ToolGuards.DescribeAsync(thrower, "{}"));

        var longText = new ToolEntry { Describe = _ => Task.FromResult<string?>(new string('x', 5000)) };
        var summary = await ToolGuards.DescribeAsync(longText, "{}");
        Assert.NotNull(summary);
        Assert.True(summary!.Length < 1300);
    }

    [Fact]
    public void EveryToolEntryProperty_IsCopiedByOverlayTagsAndApplyConfirm()
    {
        var loader = Read("src", "LoadingAI", "Plugins", "Core", "PluginLoader.cs");
        var overlay = Slice(loader, "internal static IEnumerable<ToolEntry> OverlayTags", "public static string Classify");
        var guards = Read("src", "LoadingAI", "Tools", "ToolGuards.cs");
        var apply = Slice(guards, "public static ToolEntry ApplyConfirm", "public static IEnumerable<ToolEntry> ApplyAll");

        foreach (var prop in typeof(ToolEntry).GetProperties().Where(p => p.SetMethod != null))
        {
            Assert.True(overlay.Contains(prop.Name + " ="), "OverlayTags drops " + prop.Name);
            Assert.True(apply.Contains(prop.Name + " ="), "ApplyConfirm drops " + prop.Name);
        }
    }

    [Fact]
    public void AsQuery_KeepsDescribe()
        => Assert.Contains("Describe = entry.Describe", Read("Plugins", "core-tools", "Core", "QueryTools.cs"));

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { AdapterSource.RepoRoot() }.Concat(parts).ToArray()));

    private static string Slice(string src, string start, string end)
    {
        var a = src.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, start);
        var b = src.IndexOf(end, a + start.Length, StringComparison.Ordinal);
        Assert.True(b > a, end);
        return src.Substring(a, b - a);
    }
}
