using System.IO;
using LoadingAI.Llm;
using Xunit;

namespace LoadingAI.Tests;

public class UsabilityContractTests
{
    [Fact]
    public void GetDocumentInfo_DoesNotOfferSave_SaveIsOwnL3Tool()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Core", "QueryTools.cs"));
        Assert.DoesNotContain("save = new { type = \"boolean\"", src);
        Assert.Contains("Name = \"save_document\"", src);
        Assert.Contains("Tier = ToolTier.L3", src);
    }

    [Fact]
    public void HostPage_L3NativeConfirm_AppliesToEveryL3Tool()
    {
        var src = UiSrc();
        Assert.DoesNotContain("name is \"delete_elements\" or \"move_elements\"", src);
        Assert.Contains("Confirm L3:", src);
    }

    [Fact]
    public void HostPage_SkipsL3ConfirmForDryRun_ViaGenericArgCheck()
    {
        var src = UiSrc();

        // Both the preset branch and the non-preset L3 branch must honour dryRun.
        Assert.Equal(2, CountOccurrences(src, "entry.Tier == ToolTier.L3 && !ToolPresets.IsDryRun(argsJson)"));
        Assert.Contains("ToolPresets.IsDryRun", src, StringComparison.Ordinal);

        // A generic merged-JSON check, not a tool-name allowlist.
        foreach (var name in new[] { "split_beams_by_columns", "split_floors_by_beams" })
            Assert.DoesNotContain(name, src, StringComparison.Ordinal);

        // The native confirm itself is unchanged and still the single L3 gate.
        Assert.Contains("ConfirmL3NativeAsync", src, StringComparison.Ordinal);
        Assert.Contains("Confirm L3:", src, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolGuards_SkipsL3_HostOwnsTheGate()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Tools", "ToolGuards.cs"));
        Assert.Contains("entry.Tier == ToolTier.L3", src);
    }

    [Fact]
    public void ToolEntry_PresetsDefaultEmpty_ExistingPluginsUnchanged()
    {
        var registry = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Tools", "ToolRegistry.cs"));
        Assert.Contains("IReadOnlyList<PresetField> Presets", registry);
        Assert.Contains("Array.Empty<PresetField>()", registry);
        foreach (var rel in new[]
        {
            Path.Combine("Plugins", "example.hello", "HelloPlugin.cs"),
            Path.Combine("Plugins", "qs-formwork", "QsFormworkPlugin.cs"),
        })
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot(), rel));
            Assert.DoesNotContain("Presets =", src);
        }
    }

    [Fact]
    public void ToolGuards_SkipsL2WrapWhenPresetsExist()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Tools", "ToolGuards.cs"));
        Assert.Contains("ToolPresets.HasPresets(entry)", src);
        Assert.Contains("Presets = entry.Presets", src);
    }

    [Fact]
    public void HostPage_PresetMessages_SkipL3WebCardKeepNativeConfirm()
    {
        var src = UiSrc();
        Assert.Contains("type = \"tool_preset\"", src);
        Assert.Contains("case \"submitPreset\"", src);
        Assert.Contains("case \"presetPick\"", src);
        Assert.Contains("type = \"presetPickResult\"", src);
        Assert.Contains("ConfirmL3NativeAsync", src);
        Assert.Contains("Confirm L3:", src);
        Assert.Contains("ToolPresets.HasPresets(entry)", src);
        Assert.Contains("DenyAllPending", src);
        var guards = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Tools", "ToolGuards.cs"));
        Assert.Contains("Chat preset form is the confirm", guards);
    }

    [Fact]
    public void PresetPick_IsHostOwned_IRevitBridgeUnchanged()
    {
        var pick = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Revit", "PresetPickHost.cs"));
        Assert.Contains("PickObjects", pick);
        Assert.Contains("PickBox", pick);
        Assert.Contains("SetElementIds", pick);
        Assert.Contains("FtToMm", pick);
        var bridge = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Revit", "IRevitBridge.cs"));
        Assert.DoesNotContain("PickObjects", bridge);
        Assert.DoesNotContain("PickBox", bridge);
        Assert.DoesNotContain("Preset", bridge);
        var web = File.ReadAllText(Path.Combine(RepoRoot(), "web", "src", "App.tsx"));
        Assert.Contains("submitPreset", web);
        Assert.Contains("presetPick", web);
        Assert.Contains("Confirm", web);
        Assert.Contains("setPendingPreset(cur =>", web);
        Assert.Contains("text.includes(cur.toolName)", web);
    }

    [Fact]
    public void CompatWriteTools_DoNotShowTheirOwnTaskDialog()
    {
        var rotate = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Compat", "GeometryCreateTools.cs"));
        var excel = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Compat", "ParamExcelTools.cs"));
        Assert.DoesNotContain("new TaskDialog", rotate);
        Assert.DoesNotContain("new TaskDialog", excel);
    }

    [Fact]
    public void GeometryTools_DoNotShowTheirOwnTaskDialog()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Core", "GeometryTools.cs"));
        Assert.DoesNotContain("new TaskDialog", src);
        Assert.DoesNotContain(") ft?", src);
        Assert.Contains("uses current selection", src);
    }

    [Fact]
    public void AllToolScripts_DoNotShowTheirOwnTaskDialog()
    {
        var root = RepoRoot();
        var files = Directory.GetFiles(Path.Combine(root, "src", "LoadingAI", "Tools"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(root, "Plugins"), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.EndsWith("ToolGuards.cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(files);
        foreach (var f in files)
        {
            var src = File.ReadAllText(f);
            Assert.False(src.Contains("new TaskDialog"), f + " must not show its own TaskDialog");
        }
    }

    [Fact]
    public void GearChrome_HostsPluginToggles()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "web", "src", "App.tsx"));
        Assert.Contains("PluginShelf", app);
        Assert.Contains("listPlugins", app);
        Assert.Contains("type === 'plugins'", app);
        Assert.Contains("isBuiltinProvider(activeProvider", app);
        Assert.DoesNotContain("MiniMax", app);
        Assert.DoesNotContain("minimaxi", app);
        var shelf = File.ReadAllText(Path.Combine(RepoRoot(), "web", "src", "plugins", "PluginShelf.tsx"));
        Assert.Contains("setPluginTierEnabled", shelf);
        Assert.DoesNotContain("saveToolSettings", shelf);
        Assert.DoesNotContain(">Save<", shelf);
        Assert.Contains("immediately", shelf);
        Assert.Contains("L3", shelf);
    }

    [Fact]
    public void IsToolEnabled_UsesDefaultWhenUnset()
    {
        var s = new AppSettings();
        Assert.False(ProviderSettings.IsToolEnabled(s, "delete_elements", defaultEnabled: false));
        Assert.True(ProviderSettings.IsToolEnabled(s, "get_model_summary", defaultEnabled: true));
        s.Plugins["delete_elements"] = true;
        Assert.True(ProviderSettings.IsToolEnabled(s, "delete_elements", defaultEnabled: false));
    }

    [Fact]
    public void ProgressBar_IsHostOwnedAndReusable()
    {
        var root = RepoRoot();
        var ctx = File.ReadAllText(Path.Combine(root, "src", "LoadingAI.Core", "Plugins", "IPluginContext.cs"));
        Assert.Contains("IProgressReporter Progress", ctx);
        var host = UiSrc();
        Assert.Contains("type = \"progress\"", host);
        Assert.Contains("ProgressReporter.Push", host);
        var loop = File.ReadAllText(Path.Combine(root, "src", "LoadingAI.Core", "Llm", "AgentLoop.cs"));
        Assert.Contains("Action<ProgressSnapshot>? onProgress", loop);
        Assert.Contains("ProgressSnapshot.ForTool", loop);
        var bridge = File.ReadAllText(Path.Combine(root, "web", "src", "bridge.ts"));
        Assert.Contains("type: 'progress'", bridge);
        Assert.Contains("ProgressPayload", bridge);
        var app = File.ReadAllText(Path.Combine(root, "web", "src", "App.tsx"));
        Assert.Contains("function ProgressBar", app);
        Assert.Contains("msg.type === 'progress'", app);
    }

    [Fact]
    public void SystemPrompt_TellsModelL3LivesInSettings()
    {
        Assert.Contains("Settings", AgentLoop.BaseSystemPrompt);
        Assert.Contains("L3", AgentLoop.BaseSystemPrompt);
        Assert.Contains("millimetres", AgentLoop.BaseSystemPrompt);
    }

    [Fact]
    public void SittingGaps_IsolateUsesSelection_RoomsOnBridge_ColorByAnyParam()
    {
        var view = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Core", "ViewTools.cs"));
        Assert.Contains("uses current selection", view);
        Assert.Contains("Name = \"color_by_parameter\"", view);
        Assert.DoesNotContain("new TaskDialog", view);
        var query = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Core", "QueryTools.cs"));
        Assert.Contains("Name = \"get_rooms\"", query);
        var bridge = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Revit", "IRevitBridge.cs"));
        Assert.Contains("GetRoomsAsync", bridge);
        var adapter = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Revit", "AdapterRevitBridge.cs"));
        Assert.Contains("GetRoomsAsync", adapter);
        var selector = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Llm", "ToolSelector.cs"));
        Assert.Contains("get_rooms", selector);
        Assert.Contains("color_by_parameter", selector);
        Assert.Contains("get_sheets_views", selector);
        Assert.Contains("DefaultMax = 24", selector);
    }

    private static string UiSrc()
    {
        var dir = Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui");
        return string.Join("\n", Directory.GetFiles(dir, "*.cs").Select(File.ReadAllText));
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
