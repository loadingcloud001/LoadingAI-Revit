using System.IO;
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

    // --- dryRun schema guard -------------------------------------------------
    // ChatSession skips BOTH L3 gates on a truthy dryRun (ADR-0004). That is only safe
    // because every tool that declares the key also honours it. A tool that declares
    // dryRun and ignores it lets a caller delete every confirm on a destructive write.

    [Fact]
    public void DryRunGuard_FlagsDeclarationWithoutHandling()
        => Assert.True(DryRunSchemaGuard.IsUnhandled("dryRun = new { type = \"boolean\" }"));

    [Fact]
    public void DryRunGuard_PassesIsDryRunHandling()
        => Assert.False(DryRunSchemaGuard.IsUnhandled(
            "dryRun = DryRunProperty(),\nDryRun = ToolPresets.IsDryRun(json),"));

    [Fact]
    public void DryRunGuard_PassesArgsBoolHandling()
        => Assert.False(DryRunSchemaGuard.IsUnhandled(
            "dryRun = new { type = \"boolean\" },\nargs.Bool(\"dryRun\")"));

    [Fact]
    public void DryRunGuard_IgnoresProseMentions()
        => Assert.False(DryRunSchemaGuard.IsUnhandled(
            "dryRun boolean counts only without changing the model"));

    [Fact]
    public void EverySchemaDeclaringDryRun_ImplementsDryRunShortCircuit()
    {
        var root = RepoRoot();
        var sources = Directory.EnumerateFiles(Path.Combine(root, "Plugins"), "*.cs", SearchOption.AllDirectories)
            .Select(f => (File: RelativePath(root, f), Text: File.ReadAllText(f)))
            .ToArray();

        var declaring = sources.Count(s => DryRunSchemaGuard.Declares(s.Text));
        var offenders = sources
            .Where(s => DryRunSchemaGuard.IsUnhandled(s.Text))
            .Select(s => s.File)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        // Non-vacuity: if the schema style changes and the detector stops matching, this guard
        // would otherwise pass while protecting nothing.
        Assert.True(declaring >= 2,
            $"The dryRun guard matched {declaring} declaring plugin files; expected at least 2. " +
            "Either the plugin schema style changed or the scan is vacuous.");

        Assert.True(offenders.Length == 0,
            "These plugin sources declare a dryRun schema property but never handle it. " +
            "ChatSession skips BOTH L3 gates on dryRun:true (ADR-0004), so an unhandled dryRun " +
            "silently removes every confirm on a destructive write: " + string.Join(", ", offenders));
    }

    // Path.GetRelativePath is not available on the .NET Framework target that Revit 2024 builds
    // against, so the path is trimmed by hand. Do not "simplify" this back.
    private static string RelativePath(string root, string full)
        => full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? full.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/')
            : full.Replace('\\', '/');

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
