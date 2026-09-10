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
    public void OpeningPlugin_IsIndependentPackage()
    {
        var root = RepoRoot();
        var csproj = File.ReadAllText(Path.Combine(root, "Plugins", "opening-tools", "OpeningToolsPlugin.csproj"));
        Assert.DoesNotContain("LoadingAI.csproj", csproj);
        Assert.Contains("LoadingAI.Core.csproj", csproj);
        Assert.Contains("LoadingAI.RevitUtil.csproj", csproj);
        Assert.Contains("LoadingAI_Opening_Horizontal_Rectangle.rfa", csproj);
        Assert.Contains("LoadingAI_Opening_Horizontal_Circle.rfa", csproj);
        Assert.Contains("LoadingAI_Opening_Vertical_Rectangle.rfa", csproj);
        Assert.Contains("LoadingAI_Opening_Vertical_Circle.rfa", csproj);
        Assert.True(File.Exists(Path.Combine(root, "Plugins", "opening-tools", "LoadingAI_Opening_Horizontal_Rectangle.rfa")));
        Assert.True(File.Exists(Path.Combine(root, "Plugins", "opening-tools", "LoadingAI_Opening_Horizontal_Circle.rfa")));
        Assert.True(File.Exists(Path.Combine(root, "Plugins", "opening-tools", "LoadingAI_Opening_Vertical_Rectangle.rfa")));
        Assert.True(File.Exists(Path.Combine(root, "Plugins", "opening-tools", "LoadingAI_Opening_Vertical_Circle.rfa")));
        Assert.False(File.Exists(Path.Combine(root, "Plugins", "opening-tools", "LoadingAI_Opening_Horizontal.rfa")));
        Assert.False(File.Exists(Path.Combine(root, "Plugins", "opening-tools", "LoadingAI_Opening_Vertical.rfa")));
        var src = File.ReadAllText(Path.Combine(root, "Plugins", "opening-tools", "OpeningToolsPlugin.cs"));
        Assert.Contains("create_openings_from_clash", src);
        Assert.Contains("cut_hosts_by_openings", src);
        Assert.DoesNotContain("new TaskDialog", src);
        Assert.DoesNotContain("fse_voids", src);
        Assert.DoesNotContain("NewOpening", src);
        var json = File.ReadAllText(Path.Combine(root, "Plugins", "opening-tools", "plugin.json"));
        Assert.Contains("\"id\": \"opening-tools\"", json);
        Assert.Contains("\"tier\": \"L3\"", json);
        var hostTools = Directory.GetFiles(Path.Combine(root, "src", "LoadingAI", "Tools"), "*.cs", SearchOption.AllDirectories);
        foreach (var f in hostTools)
            Assert.DoesNotContain("create_openings_from_clash", File.ReadAllText(f));
    }

    [Fact]
    public void OpeningPlugin_ClashPlacesVoidInHostDocument()
    {
        var root = RepoRoot();
        var dir = Path.Combine(root, "Plugins", "opening-tools");
        var all = string.Join("\n", Directory.GetFiles(dir, "*.cs").Select(File.ReadAllText));
        Assert.Contains("uses current selection", all);
        Assert.Contains("mepIds", all);
        Assert.Contains("hostIds", all);
        Assert.Contains("clearanceMm", all);
        Assert.Contains("familyName", all);
        Assert.Contains("levelName", all);
        Assert.Contains("GetTotalTransform", all);
        Assert.Contains("CreateTransformed", all);
        Assert.Contains("GetLinkDocument", all);
        Assert.Contains("LinkedElementId", all);
        Assert.Contains("GetReferences", all);
        Assert.Contains("AddMepFromLink", all);
        Assert.Contains("do not ask to bind links", all);
        Assert.Contains("Please select a wall, floor, beam, column, or roof", all);
        Assert.Contains("on host(s)", all);
        Assert.Contains("ScopeFail", all);
        Assert.Contains("OP_SourceId", all);
        Assert.Contains("OP_HostId", all);
        Assert.Contains("OP_HostLinked", all);
        Assert.Contains("OP_Shape", all);
        Assert.Contains("LevelNames.Resolve", all);
        Assert.Contains("Please select MEP elements first.", all);
        Assert.Contains("FamilyContract", all);
        Assert.Contains("instance params", File.ReadAllText(Path.Combine(dir, "FamilyContract.cs")));
        Assert.Contains("OpValue", File.ReadAllText(Path.Combine(dir, "FamilyContract.cs")));
        Assert.DoesNotContain("Width={w}", File.ReadAllText(Path.Combine(dir, "FamilyContract.cs")));
        Assert.DoesNotContain("new TaskDialog", all);
        Assert.DoesNotContain("NewOpening", all);
        Assert.DoesNotContain("fse_voids", all);
    }

    [Fact]
    public void OpeningPlugin_CutsLocalHostsOnly()
    {
        var root = RepoRoot();
        var dir = Path.Combine(root, "Plugins", "opening-tools");
        var all = string.Join("\n", Directory.GetFiles(dir, "*.cs").Select(File.ReadAllText));
        Assert.Contains("cut_hosts_by_openings", all);
        Assert.Contains("InstanceVoidCutUtils", all);
        Assert.Contains("CanBeCutWithVoid", all);
        Assert.Contains("AddInstanceVoidCut", all);
        Assert.Contains("host is linked — not cut", all);
        Assert.DoesNotContain("new TaskDialog", all);
        Assert.DoesNotContain("NewOpening", all);
    }

    [Fact]
    public void OpeningPlugin_ReviewFixes_NoLinkScanSolidOnlyFloorFrame()
    {
        var root = RepoRoot();
        var dir = Path.Combine(root, "Plugins", "opening-tools");
        var probe = File.ReadAllText(Path.Combine(dir, "ClashProbe.cs"));
        var place = File.ReadAllText(Path.Combine(dir, "PlaceOpenings.cs"));
        var cut = File.ReadAllText(Path.Combine(dir, "CutLocal.cs"));

        Assert.DoesNotContain("foreach (var mep in Collector(ldoc, MepCats))", probe);
        Assert.Contains("AddMepFromLink", probe);
        Assert.Contains("ScopeFail", probe);
        Assert.Contains("section box", probe);
        Assert.Contains("mep.Element.Document", probe);
        Assert.DoesNotContain("else if (mepBox != null && BoxOf(host", probe);
        Assert.Contains("is Floor", probe);
        Assert.Contains("XYZ.BasisY", probe);
        Assert.DoesNotContain("static IEnumerable<Element> Collector(", probe);

        var start = place.IndexOf("tx.Start()", StringComparison.Ordinal);
        var activate = place.IndexOf("Activate()", StringComparison.Ordinal);
        var regen = place.IndexOf("Regenerate()", StringComparison.Ordinal);
        Assert.True(start >= 0 && activate > start, "FamilySymbol.Activate must run inside the transaction");
        Assert.True(regen > activate, "Regenerate must follow Activate in the same transaction");
        Assert.Contains("GetSideFaces", place);
        Assert.Contains("GetTopFaces", place);
        Assert.Contains("ComputeReferences = true", place);
        Assert.Contains("NewFamilyInstance(face, loc, dir, symbol)", place);
        Assert.Contains("StructuralType.NonStructural", place);
        Assert.Contains("RotateElement", place);
        Assert.Contains("WorkPlaneBased", place);

        Assert.Contains("SolidIntersects", cut);
        Assert.DoesNotContain("hostsHint.Count > 0 ? hostsHint : HostsFor", cut);
    }

    [Fact]
    public void OpeningPlugin_AllCases_HostMepFamilyAndCut()
    {
        var root = RepoRoot();
        var dir = Path.Combine(root, "Plugins", "opening-tools");
        var probe = File.ReadAllText(Path.Combine(dir, "ClashProbe.cs"));
        var place = File.ReadAllText(Path.Combine(dir, "PlaceOpenings.cs"));
        var cut = File.ReadAllText(Path.Combine(dir, "CutLocal.cs"));
        var plugin = File.ReadAllText(Path.Combine(dir, "OpeningToolsPlugin.cs"));

        Assert.Contains("OST_FlexDuct", probe);
        Assert.Contains("OST_FlexPipe", probe);
        Assert.Contains("OST_StructuralColumns", probe);
        Assert.Contains("AddMepByIdFromLinks", probe);
        Assert.Contains("SnapToHost", probe);
        Assert.Contains("Evaluate(t, false)", probe);
        Assert.Contains("never a free XYZ", probe);
        Assert.Contains("InsideHost", probe);
        Assert.Contains("WorldMinMax", probe);
        Assert.Contains("Do not OfPoint Min through Transform", probe);
        Assert.Contains("do not reject Z", probe);
        Assert.Contains("ModelCentroid", probe);
        Assert.Contains("CreateTransformed", probe);
        Assert.Contains("GetInstanceGeometry", probe);
        Assert.DoesNotContain("geo.GetTransformed", probe);
        Assert.Contains("the model that owns this instance", probe);
        Assert.Contains("ClashPoint", probe);
        Assert.Contains("TryCurveHit", probe);
        Assert.Contains("LooksDoubled", probe);
        Assert.Contains("BoxOnceHit", probe);
        Assert.Contains("CreateTransformed(toHost)", probe);
        Assert.Contains("trust curve/bbox-once", probe);

        Assert.Contains("ShareWidth", place);
        Assert.Contains("AlreadyPlaced", place);
        Assert.Contains("OneLevelBasedHosted", place);
        Assert.Contains("WorkPlaneBased", place);
        Assert.Contains(" ft)", place);
        Assert.Contains("OpValue", place);

        Assert.Contains("OpeningsForHosts", cut);
        Assert.Contains("AllPluginOpenings", cut);
        Assert.Contains("OpValue", cut);
        Assert.Contains("IncludeNonVisibleObjects = true", cut);

        Assert.Contains("clearanceMm\", 25", plugin);
    }

    [Fact]
    public void OpeningPlugin_PlaceAndPunchInOneTransaction()
    {
        var root = RepoRoot();
        var dir = Path.Combine(root, "Plugins", "opening-tools");
        var probe = File.ReadAllText(Path.Combine(dir, "ClashProbe.cs"));
        var place = File.ReadAllText(Path.Combine(dir, "PlaceOpenings.cs"));
        var cut = File.ReadAllText(Path.Combine(dir, "CutLocal.cs"));
        var plugin = File.ReadAllText(Path.Combine(dir, "OpeningToolsPlugin.cs"));
        var json = File.ReadAllText(Path.Combine(dir, "plugin.json"));

        Assert.Contains("never snap a far centroid onto a wall end", probe);
        Assert.Contains("host.UniqueId", probe);
        Assert.Contains("mep.Element.UniqueId", probe);
        Assert.Contains("PointHost", probe);

        Assert.Contains("CutLocal.TryPunch", place);
        Assert.Contains("GetCuttingVoidInstances", cut);
        Assert.Contains("no-punch", place);
        Assert.Contains("place all, then regenerate, then cut all", place);
        Assert.Contains("Punched", place);
        Assert.Contains("TryPunch", place);
        Assert.Contains("PinToPointHost", place);
        Assert.Contains("MoveElement", place);
        Assert.Contains("INSTANCE_FREE_HOST_OFFSET_PARAM", place);
        Assert.Contains("Outer Width", place);
        Assert.Contains("Outer Height", place);
        Assert.Contains("el.Parameters", place);
        Assert.Contains("ToUpperInvariant", place);
        Assert.Contains("opening bottom is the family origin", place);
        Assert.Contains("HeightFt / 2", place);
        Assert.Contains("LoadingAI_Opening_Horizontal", place);
        Assert.Contains("LoadingAI_Opening_Vertical", place);
        Assert.Contains("FamilyNameFor(hostNeed, familyName, hit.Shape)", place);
        Assert.Contains("none this run", place);
        Assert.DoesNotContain("circle origin is pipe center", place);
        Assert.Contains("IsVerticalHost", place);
        Assert.DoesNotContain("var symbol = FamilyContract.Find(doc, familyName, shape);", place);
        Assert.Contains("FamilyContract.Find(doc, familyName, hit.Shape, host)", place);
        Assert.Contains("if (host is Floor || host is RoofBase) return;", place);
        Assert.Contains("PinVerticalToHostTop", place);
        Assert.Contains("TryLoad", place);
        Assert.Contains("FromMinutes(10)", plugin);
        Assert.Contains("BoxesOverlap", probe);

        var contract = File.ReadAllText(Path.Combine(dir, "FamilyContract.cs"));
        Assert.Contains("IsValidObject", contract);
        Assert.Contains(".rfa", contract);
        Assert.Contains("LoadFamily", contract);
        Assert.Contains("LoadingAI_Opening_Horizontal", contract);
        Assert.Contains("LoadingAI_Opening_Vertical", contract);
        Assert.Contains("LoadingAI_Opening_Horizontal_Rectangle", contract);
        Assert.Contains("LoadingAI_Opening_Horizontal_Circle", contract);
        Assert.Contains("LoadingAI_Opening_Vertical_Rectangle", contract);
        Assert.Contains("LoadingAI_Opening_Vertical_Circle", contract);
        Assert.Contains("IsPluginFamily", contract);
        Assert.Contains("IsVerticalHost", contract);
        Assert.Contains("host is Floor || host is RoofBase", contract);
        Assert.Contains("IsPluginFamily", cut);

        Assert.Contains("GetCuttingVoidInstances", cut);
        Assert.Contains("TryPunch", cut);
        Assert.Contains("no-punch", cut);

        Assert.Contains("Place and punch", plugin);
        Assert.Contains("one transaction", plugin);
        Assert.Contains("Repair", plugin);
        Assert.Contains("Place and punch", json);
        Assert.Contains("Repair", json);
        Assert.DoesNotContain("NewOpening", place + cut + plugin);
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
