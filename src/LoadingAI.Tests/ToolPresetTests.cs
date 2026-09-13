using System.Text.Json;
using LoadingAI.Llm;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class ToolPresetTests
{
    private static ToolEntry Entry(params PresetField[] fields) => new()
    {
        Definition = new ToolDefinition { Function = new ToolFunction { Name = "demo" } },
        Handler = _ => Task.FromResult("ok"),
        Presets = fields
    };

    [Fact]
    public void HasPresets_FalseWhenEmpty()
    {
        Assert.False(ToolPresets.HasPresets(Entry()));
        Assert.True(ToolPresets.HasPresets(Entry(new PresetField { Id = "x", Kind = PresetFieldKind.String })));
    }

    [Fact]
    public void Prefill_UsesDefaultThenArgsWin()
    {
        var fields = new[]
        {
            new PresetField { Id = "offsetMm", Kind = PresetFieldKind.Number, Default = 20 },
            new PresetField { Id = "name", Kind = PresetFieldKind.String, Default = "A" }
        };
        var values = ToolPresets.Prefill(fields, "{\"offsetMm\":50}");
        Assert.Equal(50, ((JsonElement)values["offsetMm"]!).GetInt32());
        Assert.Equal("A", values["name"]?.ToString());
    }

    [Fact]
    public void Merge_FormWinsOverArgs()
    {
        var merged = ToolPresets.Merge("{\"offsetMm\":20,\"keep\":true}", "{\"offsetMm\":40}");
        using var doc = JsonDocument.Parse(merged);
        Assert.Equal(40, doc.RootElement.GetProperty("offsetMm").GetInt32());
        Assert.True(doc.RootElement.GetProperty("keep").GetBoolean());
    }

    [Fact]
    public void Merge_EmptyOverlayKeepsArgs()
    {
        var merged = ToolPresets.Merge("{\"a\":1}", null);
        using var doc = JsonDocument.Parse(merged);
        Assert.Equal(1, doc.RootElement.GetProperty("a").GetInt32());
    }

    [Fact]
    public void KindName_IsEnumString()
    {
        Assert.Equal("PickElements", ToolPresets.KindName(PresetFieldKind.PickElements));
        Assert.Equal("PickBox", ToolPresets.KindName(PresetFieldKind.PickBox));
        Assert.Equal("Boolean", ToolPresets.KindName(PresetFieldKind.Boolean));
    }

    [Theory]
    [InlineData("{\"dryRun\":true}", true)]
    [InlineData("{\"dryRun\":\"true\"}", true)]
    [InlineData("{\"dryRun\":\"TRUE\"}", true)]
    [InlineData("{\"dryRun\":1}", true)]
    [InlineData("{\"dryRun\":\"1\"}", true)]
    [InlineData("{\"dryRun\":false}", false)]
    [InlineData("{\"dryRun\":0}", false)]
    [InlineData("{\"dryRun\":\"no\"}", false)]
    [InlineData("{\"dryRun\":null}", false)]
    [InlineData("{\"other\":true}", false)]
    [InlineData("{}", false)]
    [InlineData("not json", false)]
    [InlineData(null, false)]
    public void IsDryRun_OnlyTrueForTruthyDryRun(string? json, bool expected)
        => Assert.Equal(expected, ToolPresets.IsDryRun(json));

    [Fact]
    public void IsDryRun_MatchesPlainTrueLiteral_ForHostPluginAgreement()
    {
        // The plugin reads the same helper, so a truthy value must survive the host's merge step.
        var merged = ToolPresets.Merge("{\"dryRun\":true}", null);
        Assert.True(ToolPresets.IsDryRun(merged));
    }
}
