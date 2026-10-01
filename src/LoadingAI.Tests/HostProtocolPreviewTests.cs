using System.IO;
using System.Text.Json;
using LoadingAI.Protocol;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class HostProtocolPreviewTests
{
    private sealed class Throwing
    {
        public override string ToString() => throw new InvalidOperationException("unrenderable");
    }

    [Fact]
    public void PresetPayload_SerializesPreviewWhenPresent()
    {
        var preset = new FromHostMessage.PresetPayload(
            "call1",
            "create_formwork_panels",
            "L3",
            "Generate formwork panels",
            Array.Empty<FromHostMessage.PresetFieldPayload>(),
            null,
            new FromHostMessage.PreviewPayload(
                "4 settings to confirm",
                new[] { new FromHostMessage.PreviewRowPayload("Sheet thickness (mm)", "18") },
                new[] { "Existing panels will be deleted first." }));

        var json = FromHostMessage.Serialize(new FromHostMessage.ToolPreset(preset));

        using var doc = JsonDocument.Parse(json);
        var preview = doc.RootElement.GetProperty("preset").GetProperty("preview");
        Assert.Equal("4 settings to confirm", preview.GetProperty("summary").GetString());
        Assert.Equal("Sheet thickness (mm)", preview.GetProperty("rows")[0].GetProperty("label").GetString());
        Assert.Equal("18", preview.GetProperty("rows")[0].GetProperty("detail").GetString());
        Assert.Equal("Existing panels will be deleted first.", preview.GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public void PresetPayload_OmitsPreviewWhenAbsent()
    {
        var preset = new FromHostMessage.PresetPayload(
            "call1", "cleanup_formwork", "L3", "d",
            Array.Empty<FromHostMessage.PresetFieldPayload>(),
            null);

        var json = FromHostMessage.Serialize(new FromHostMessage.ToolPreset(preset));

        using var doc = JsonDocument.Parse(json);
        Assert.False(
            doc.RootElement.GetProperty("preset").TryGetProperty("preview", out _),
            "a null preview must not be written; the pane reads absence as no-preview-available");
    }

    [Fact]
    public void PresetPayload_PreviewParameterIsLast_SoExistingCallSitesDoNotRenumber()
    {
        // Existing positional call sites pass 6 arguments. A new parameter appended with a
        // default keeps them compiling; inserting it earlier would silently rebind every one.
        var preset = new FromHostMessage.PresetPayload(
            "id", "tool", "L2", "d",
            Array.Empty<FromHostMessage.PresetFieldPayload>(),
            new Dictionary<string, object?>());

        Assert.Null(preset.Preview);
    }

    [Fact]
    public void TryBuildPreview_ReturnsPreviewWhenRenderable()
    {
        var fields = new[]
        {
            new PresetField { Id = "thicknessMm", Label = "Sheet thickness (mm)", Kind = PresetFieldKind.Number, Default = (int)18 }
        };

        var preview = ToolPresets.TryBuildPreview(fields, null);

        Assert.NotNull(preview);
        Assert.Equal("1 setting to confirm", preview!.Summary);
    }

    [Fact]
    public void TryBuildPreview_ReturnsNullWhenRenderingThrows()
    {
        // A preview is enrichment. A field whose value cannot be rendered must not take the
        // preset form down with it.
        var fields = new[]
        {
            new PresetField { Id = "x", Label = "X", Kind = PresetFieldKind.String, Default = new Throwing() }
        };

        Assert.Null(ToolPresets.TryBuildPreview(fields, null));
    }

    [Fact]
    public void TryBuildPreview_DegradesToFieldDefaultsOnBadJson()
    {
        // Malformed args must not cost the user the preview. Prefill is already defensive about
        // unparseable JSON, so the preview falls back to the declared field defaults.
        var preview = ToolPresets.TryBuildPreview(
            new[] { new PresetField { Id = "x", Label = "X", Kind = PresetFieldKind.String, Default = "draft" } },
            "not json");

        Assert.NotNull(preview);
        Assert.Equal("draft", preview!.Rows.Single().Detail);
    }

    // ApprovalGate is internal and drives a concrete WebHostBridge (WebView2), so it is pinned
    // the way the rest of the host gate code is pinned: by source contract. See
    // UsabilityContractTests and ChatSessionContractTests.
    [Fact]
    public void ApprovalGate_BuildsPreviewWithFaultIsolatedHelper_AndPassesItToPresetPayload()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "ApprovalGate.cs"));
        var awaitPreset = Slice(src,
            "AwaitPresetAsync(ToolEntry entry, string name, string argsJson)",
            "AwaitL3WebAsync(");

        Assert.Contains("TryBuildPreview(entry.Presets, argsJson)", awaitPreset);
        Assert.Contains("preview", awaitPreset);
    }

    private static string Slice(string src, string start, string end)
    {
        var i = src.IndexOf(start, StringComparison.Ordinal);
        Assert.True(i >= 0, $"marker not found: {start}");
        var j = src.IndexOf(end, i, StringComparison.Ordinal);
        Assert.True(j > i, $"marker not found: {end}");
        return src.Substring(i, j - i);
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
