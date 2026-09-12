using System.IO;
using LoadingAI.Llm;
using Xunit;

namespace LoadingAI.Tests;

public class UsabilityContractTests
{
    [Fact]
    public void GetDocumentInfo_DoesNotOfferSave_SaveIsOwnL3Tool()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Tools", "Core", "QueryTools.cs"));
        Assert.DoesNotContain("save = new { type = \"boolean\"", src);
        Assert.Contains("Name = \"save_document\"", src);
        Assert.Contains("Tier = ToolTier.L3", src);
    }

    [Fact]
    public void HostPage_L3NativeConfirm_AppliesToEveryL3Tool()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "HostPage.xaml.cs"));
        Assert.DoesNotContain("name is \"delete_elements\" or \"move_elements\"", src);
        Assert.Contains("Confirm L3:", src);
    }

    [Fact]
    public void ToolGuards_SkipsL3_HostOwnsTheGate()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Tools", "ToolGuards.cs"));
        Assert.Contains("entry.Tier == ToolTier.L3", src);
    }

    [Fact]
    public void CompatWriteTools_DoNotShowTheirOwnTaskDialog()
    {
        var rotate = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Tools", "Compat", "GeometryCreateTools.cs"));
        var excel = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Tools", "Compat", "ParamExcelTools.cs"));
        Assert.DoesNotContain("new TaskDialog", rotate);
        Assert.DoesNotContain("new TaskDialog", excel);
    }

    [Fact]
    public void GeometryTools_DoNotShowTheirOwnTaskDialog()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Tools", "Core", "GeometryTools.cs"));
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
    public void SystemPrompt_TellsModelL3LivesInSettings()
    {
        Assert.Contains("Settings", AgentLoop.BaseSystemPrompt);
        Assert.Contains("L3", AgentLoop.BaseSystemPrompt);
        Assert.Contains("millimetres", AgentLoop.BaseSystemPrompt);
    }

    [Fact]
    public void SittingGaps_IsolateUsesSelection_RoomsOnBridge_ColorByAnyParam()
    {
        var view = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Tools", "Core", "ViewTools.cs"));
        Assert.Contains("uses current selection", view);
        Assert.Contains("Name = \"color_by_parameter\"", view);
        Assert.DoesNotContain("new TaskDialog", view);
        var query = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Tools", "Core", "QueryTools.cs"));
        Assert.Contains("Name = \"get_rooms\"", query);
        var bridge = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Revit", "IRevitBridge.cs"));
        Assert.Contains("GetRoomsAsync", bridge);
        var adapter = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Revit", "AdapterRevitBridge.cs"));
        Assert.Contains("GetRoomsAsync", adapter);
        var selector = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Llm", "ToolSelector.cs"));
        Assert.Contains("get_rooms", selector);
        Assert.Contains("color_by_parameter", selector);
        Assert.Contains("get_sheets_views", selector);
    }

    [Fact]
    public void OpeningTools_CutWallsByOpenings_AndFseVoidsToOpenings()
    {
        var all = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Tools", "AllTools.cs"));
        Assert.Contains("OpeningTools.Create", all);
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Tools", "Compat", "OpeningTools.cs"));
        Assert.Contains("Name = \"cut_walls_by_openings\"", src);
        Assert.Contains("Name = \"fse_voids_to_openings\"", src);
        Assert.Contains("uses current selection", src);
        Assert.Contains("familyName", src);
        Assert.Contains("levelName", src);
        Assert.Contains("LevelNames.Resolve", src);
        Assert.Contains("Did you mean", src);
        Assert.DoesNotContain("new TaskDialog", src);
        Assert.DoesNotContain("explorer", src);
        var selector = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Llm", "ToolSelector.cs"));
        Assert.Contains("opening", selector);
        Assert.Contains("fse", selector);
        Assert.DoesNotContain("cut_walls_by_openings", selector);
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
