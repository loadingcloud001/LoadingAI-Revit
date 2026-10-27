using System.IO;
using System.Linq;
using LoadingAI.Llm;
using Xunit;

namespace LoadingAI.Tests;

public class UsabilityContractTests
{
    [Fact]
    public void GetDocumentInfo_DoesNotOfferSave_SaveIsOwnL3Tool()
    {
        var query = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Core", "QueryTools.cs"));
        Assert.DoesNotContain("save = new { type = \"boolean\"", query);
        Assert.DoesNotContain("Name = \"save_document\"", query);
        var save = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Core", "DocumentTools.cs"));
        Assert.Contains("Name = \"save_document\"", save);
        Assert.Contains("Tier = ToolTier.L3", save);
    }

    [Fact]
    public void Gate_AppliesToEveryL3Tool_ByTierNotByName()
    {
        var src = UiSrc();
        Assert.DoesNotContain("name is \"delete_elements\" or \"move_elements\"", src);
        Assert.DoesNotContain("Confirm L3:", src);
        Assert.Contains("GatePolicy.Decide(", src);
        Assert.Contains("_approvals.ReviewAsync(", src);
        var policy = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Gate", "GatePolicy.cs"));
        Assert.Contains("r.Tier == ToolTier.L1", policy);
        Assert.DoesNotContain("delete_elements", policy);
    }

    [Fact]
    public void Gate_SkipsTheCardForDryRun_ViaGenericArgCheck()
    {
        var src = UiSrc();

        // The dry-run test is the shared merged-JSON helper, not a tool-name allowlist.
        Assert.Contains("DryRun = ToolPresets.IsDryRun(argsJson)", src, StringComparison.Ordinal);
        foreach (var name in new[] { "split_beams_by_columns", "split_floors_by_beams", "split_by_levels", "split_by_grids" })
            Assert.DoesNotContain(name, src, StringComparison.Ordinal);

        // The policy runs a dry run at every level, before it looks at the level.
        var policy = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Gate", "GatePolicy.cs"));
        var dry = policy.IndexOf("if (r.DryRun) return GateDecision.Run;", StringComparison.Ordinal);
        var level = policy.IndexOf("PermissionLevels.EffectiveFor", StringComparison.Ordinal);
        Assert.True(dry >= 0 && dry < level);

        // A dry run never opens the turn group: only a real L2 or L3 write does.
        Assert.Contains("entry.Tier == ToolTier.L3 && !dryRun", src, StringComparison.Ordinal);
    }

    [Fact]
    public void WebPane_StopAndClearUnmountTheReviewCard_AndBlockALateConfirm()
    {
        var reducer = WebFile("state", "reducer.ts");

        // Review Focus 4. Stop and Clear must take the review card away. The card only mounts while
        // a review is pending, so clearing the pending review unmounts Cancel/Confirm with it.
        Assert.Contains("case 'stop': return finishTurn(s, 'cancelled');", reducer, StringComparison.Ordinal);
        Assert.Contains("pendingReview: null", PaneSlice(reducer, "function finishTurn(", "function plainTodos("), StringComparison.Ordinal);
        Assert.Contains("pendingReview: null", PaneSlice(reducer, "case 'clear': {", "case 'settingsLocal'"), StringComparison.Ordinal);
        Assert.Contains("{reviewStep && pending && (", WebFile("chat", "Turn.tsx"), StringComparison.Ordinal);

        // And a confirm arriving after either is a no-op rather than a submission.
        var submit = PaneSlice(WebFile("state", "usePane.ts"), "const submitPreset = useCallback(", "const pick = useCallback(");
        Assert.Contains("if (!pending) return;", submit, StringComparison.Ordinal);

        // ADR-014: one confirm surface. Only the review card may submit a Run form.
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot(), "web", "src"), "*.tsx", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "ReviewCard.tsx") continue;
            Assert.DoesNotContain("onSubmitPreset(true", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WebPane_ReviewCardReadsLiveValues_NotOnlyTheHostSnapshot()
    {
        var card = WebFile("chat", "ReviewCard.tsx");
        var form = card.Substring(card.IndexOf("function PresetForm(", StringComparison.Ordinal));

        // The summary above the form is what a person confirms against. It has to follow the form,
        // or it can read "delete off" over a checked Delete box.
        Assert.Contains("previewRows(preset, values)", form, StringComparison.Ordinal);
        Assert.Contains("previewWarnings(preset, values)", form, StringComparison.Ordinal);

        // Long forms (structure-cuts ships a seven-knob form) must not push Cancel/Confirm/Stop out
        // of reach on a docked pane.
        Assert.Contains("overflowY", form, StringComparison.Ordinal);
    }

    private static string WebFile(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot(), "web", "src" }.Concat(parts).ToArray()));

    // Every non-test source file of the pane, for checks that do not care which file holds a string.
    private static string WebSrc()
        => string.Join("\n", Directory.GetFiles(Path.Combine(RepoRoot(), "web", "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".ts") || f.EndsWith(".tsx")) && !f.EndsWith(".test.ts"))
            .Select(File.ReadAllText));

    private static string PaneSlice(string src, string start, string end)
    {
        var i = src.IndexOf(start, StringComparison.Ordinal);
        Assert.True(i >= 0, $"marker not found: {start}");
        var j = src.IndexOf(end, i + start.Length, StringComparison.Ordinal);
        Assert.True(j > i, $"marker not found: {end}");
        return src.Substring(i, j - i);
    }

    [Fact]
    public void ToolGuards_HasNoGate_HostOwnsIt()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Tools", "ToolGuards.cs"));
        Assert.DoesNotContain("AutoApproveGate", src);
        Assert.DoesNotContain("TaskDialog", src);
        Assert.Contains("DescribeAsync", src);
    }

    [Fact]
    public void AutoApprove_IsAUserSwitchAndDoesNotDeleteElementsToClearADialog()
    {
        var failures = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Revit", "AutoApproveFailures.cs"));
        Assert.Contains("HasActiveTurnGroup", failures);
        Assert.Contains("GetProcessingResult() != FailureProcessingResult.Continue", failures);
        Assert.Contains("AutoApproveGate.Enabled", failures);
        Assert.Contains("FailureResolutionType.DetachElements", failures);
        Assert.DoesNotContain("FailureResolutionType.DeleteElements", failures);
        Assert.DoesNotContain("FailureResolutionType.CreateElements", failures);
        var session = UiSrc();
        Assert.Contains("agent.AutoApprove = ProviderSettings.LevelOf(_settings) == PermissionLevels.Bypass", session);
        Assert.DoesNotContain("GetProperty(\"autoApprove\")", session);
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "App.cs"));
        Assert.Contains("FailuresProcessing += AutoApproveFailures.OnFailures", app);
        // The pane switch is Bypass: it asks once in the pane, and a stored level never restores it.
        var permissions = WebFile("lib", "permissions.ts");
        Assert.Contains("return level === 'bypass';", permissions);
        Assert.Contains("if (!pick || pick.id === 'bypass') return DEFAULT_LEVEL;", permissions);
        var pane = WebFile("App.tsx");
        Assert.Contains("Turn on {info.label}", pane);
        Assert.DoesNotContain("window.confirm", WebSrc());
    }

    [Fact]
    public void ToolEntry_PresetsDefaultEmpty_WhenNoneAreDeclared()
    {
        var registry = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Tools", "ToolRegistry.cs"));
        Assert.Contains("IReadOnlyList<PresetField> Presets", registry);
        Assert.Contains("Array.Empty<PresetField>()", registry);

        // Pinned on example.hello only. qs-formwork adopted presets under ADR-0013, so the
        // assertion no longer lists every shipped plugin: the invariant here is that a tool
        // which declares no Presets initializer still gets an empty list, not that no plugin may
        // ever adopt a host-owned form.
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "example.hello", "HelloPlugin.cs"));
        Assert.DoesNotContain("Presets =", src);
    }

    [Fact]
    public void Gate_NeverAllowListsAToolThatHasARunForm()
    {
        var policy = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Gate", "GatePolicy.cs"));
        Assert.Contains("!r.HasPresets", policy);
        Assert.Contains("!r.Destructive", policy);
        var gate = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "ApprovalGate.cs"));
        Assert.Contains("Plain = !ToolPresets.HasPresets(entry);", gate);
        Assert.Contains("GatePolicy.CanOfferAlways(Entry.Destructive, !Plain, Entry.NeverAllowList)", gate);
    }

    [Fact]
    public void HostPage_PresetMessages_UseTheSingleReviewCard()
    {
        var src = UiSrc();
        var protocol = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Protocol", "HostProtocol.cs"));
        Assert.Contains("new FromHostMessage.ToolReview", src);
        Assert.Contains("ToHostMessage.SubmitPreset", src);
        Assert.Contains("ToHostMessage.PresetPick", src);
        Assert.Contains("FromHostMessage.PresetPickResult", src);
        Assert.Contains("FromHostKind.ToolReview", protocol);
        Assert.Contains("SubmitPreset = \"submitPreset\"", protocol);
        Assert.Contains("PresetPick = \"presetPick\"", protocol);
        Assert.Contains("ToolPresets.HasPresets(entry)", src);
        Assert.Contains("DenyAllPending", src);
        // ADR-0017: one card, no second surface. The old two-step posts and the native dialog are gone.
        Assert.DoesNotContain("new FromHostMessage.ToolPreset", src);
        Assert.DoesNotContain("new FromHostMessage.ToolApproval", src);
        Assert.DoesNotContain("Confirm L3:", src);
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
        var pane = WebFile("state", "usePane.ts");
        Assert.Contains("type: 'submitPreset'", pane);
        Assert.Contains("type: 'presetPick'", pane);
        Assert.Contains("Pick in Revit", WebFile("chat", "ReviewCard.tsx"));
        // A tool result for the reviewed step closes its card.
        Assert.Contains("s1.pendingReview.stepId === target.id", WebFile("state", "reducer.ts"));
    }

    [Fact]
    public void CompatWriteTools_DoNotShowTheirOwnTaskDialog()
    {
        var geo = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Core", "GeometryTools.cs"));
        var param = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Core", "ParamTools.cs"));
        var view = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Core", "ViewTools.cs"));
        var document = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Core", "DocumentTools.cs"));
        Assert.DoesNotContain("new TaskDialog", geo);
        Assert.DoesNotContain("new TaskDialog", param);
        Assert.DoesNotContain("new TaskDialog", view);
        Assert.DoesNotContain("new TaskDialog", document);
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
        var app = WebSrc();
        Assert.Contains("PluginShelf", app);
        Assert.Contains("listPlugins", app);
        Assert.Contains("case 'plugins'", app);
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
        Assert.Contains("FromHostMessage.Progress", host);
        Assert.Contains("ProgressReporter.Push", host);
        var loop = File.ReadAllText(Path.Combine(root, "src", "LoadingAI.Core", "Llm", "AgentLoop.cs"));
        Assert.Contains("Action<ProgressSnapshot>? onProgress", loop);
        Assert.Contains("ProgressSnapshot.ForTool", loop);
        var bridge = File.ReadAllText(Path.Combine(root, "web", "src", "bridge.ts"));
        Assert.Contains("type: 'progress'", bridge);
        Assert.Contains("ProgressPayload", bridge);
        var app = File.ReadAllText(Path.Combine(root, "web", "src", "App.tsx"));
        Assert.Contains("status-bar", app);
        Assert.Contains("case 'progress'", File.ReadAllText(Path.Combine(root, "web", "src", "state", "reducer.ts")));
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
        Assert.False(File.Exists(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Compat", "ViewColorTools.cs")));
        var query = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Core", "QueryTools.cs"));
        Assert.Contains("Name = \"get_rooms\"", query);
        var bridge = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Revit", "IRevitBridge.cs"));
        Assert.Contains("GetRoomsAsync", bridge);
        var adapter = AdapterSource.All();
        Assert.Contains("GetRoomsAsync", adapter);
        var selector = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Llm", "ToolSelector.cs"));
        Assert.Contains("get_rooms", selector);
        Assert.Contains("color_by_parameter", selector);
        Assert.Contains("get_sheets_views", selector);
        Assert.Contains("DefaultMax = 24", selector);
    }

    [Fact]
    public void SittingGaps_QueryHealthOnBridge_CompatFileGone()
    {
        var query = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Core", "QueryTools.cs"));
        Assert.Contains("Name = \"get_worksets\"", query);
        Assert.Contains("Name = \"get_phases\"", query);
        Assert.Contains("Name = \"get_design_options\"", query);
        Assert.Contains("Name = \"get_sheets_views\"", query);
        Assert.Contains("Name = \"get_family_types\"", query);
        Assert.Contains("Name = \"get_materials\"", query);
        Assert.Contains("Name = \"get_room_area\"", query);
        Assert.Contains("Name = \"get_clash_by_bb\"", query);
        Assert.Contains("Name = \"get_model_health_score\"", query);
        Assert.DoesNotContain("new TaskDialog", query);
        Assert.DoesNotContain("AllTools.ParseEid", query);
        Assert.DoesNotContain("AllTools.ParseCategory", query);
        Assert.DoesNotContain("RequiresWriteConfirm", query);
        Assert.False(File.Exists(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Compat", "QueryHealthTools.cs")));
        var bridge = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Revit", "IRevitBridge.cs"));
        Assert.Contains("GetWorksetsAsync", bridge);
        Assert.Contains("GetClashByBoundingBoxAsync", bridge);
        Assert.Contains("GetModelHealthScoreAsync", bridge);
        var adapter = AdapterSource.All();
        Assert.Contains("GetWorksetsAsync", adapter);
        Assert.Contains("GetClashByBoundingBoxAsync", adapter);
        Assert.Contains("GetModelHealthScoreAsync", adapter);
    }

    [Fact]
    public void SittingGaps_ParamMutateOnBridge_CompatFileGone()
    {
        var param = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Core", "ParamTools.cs"));
        Assert.Contains("Name = \"rename_elements\"", param);
        Assert.Contains("Name = \"apply_construction_dates\"", param);
        Assert.Contains("RequiresWriteConfirm = true", param);
        Assert.DoesNotContain("new TaskDialog", param);
        Assert.DoesNotContain("AllTools.ParseEid", param);
        Assert.False(File.Exists(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Compat", "ParamMutateTools.cs")));
        var bridge = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Revit", "IRevitBridge.cs"));
        Assert.Contains("RenameElementsAsync", bridge);
        Assert.Contains("ApplyConstructionDatesAsync", bridge);
        var adapter = AdapterSource.All();
        Assert.Contains("RenameElementsAsync", adapter);
        Assert.Contains("ApplyConstructionDatesAsync", adapter);
    }

    [Fact]
    public void SittingGaps_ParamExcelOnBridge_CompatFileGone()
    {
        var param = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Core", "ParamTools.cs"));
        Assert.Contains("Name = \"export_params_to_excel\"", param);
        Assert.Contains("Name = \"import_excel_to_params\"", param);
        Assert.Contains("Name = \"copy_params\"", param);
        Assert.Contains("Name = \"replace_text\"", param);
        Assert.Contains("Name = \"numbering_auto\"", param);
        Assert.DoesNotContain("new TaskDialog", param);
        Assert.DoesNotContain("AllTools.ParseEid", param);
        Assert.DoesNotContain("AllTools.ParseCategory", param);
        Assert.False(File.Exists(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Compat", "ParamExcelTools.cs")));
        var bridge = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Revit", "IRevitBridge.cs"));
        Assert.Contains("ExportParamsAsync", bridge);
        Assert.Contains("ImportParamsAsync", bridge);
        Assert.Contains("CopyParamsAsync", bridge);
        Assert.Contains("ReplaceTextInParamAsync", bridge);
        Assert.Contains("NumberElementsAsync", bridge);
        var adapter = AdapterSource.All();
        Assert.Contains("ExportParamsAsync", adapter);
        Assert.Contains("ImportParamsAsync", adapter);
        Assert.Contains("CopyParamsAsync", adapter);
        Assert.Contains("ReplaceTextInParamAsync", adapter);
        Assert.Contains("NumberElementsAsync", adapter);
    }

    [Fact]
    public void SittingGaps_SheetDocOnBridge_CompatFileGone()
    {
        var view = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Core", "ViewTools.cs"));
        Assert.Contains("Name = \"create_sheet\"", view);
        Assert.Contains("Name = \"place_view_on_sheet\"", view);
        Assert.Contains("Name = \"duplicate_view_ex\"", view);
        Assert.Contains("Name = \"set_view_template\"", view);
        Assert.Contains("Name = \"set_crop_box\"", view);
        Assert.Contains("Name = \"create_dimension\"", view);
        Assert.Contains("Name = \"tag_all\"", view);
        Assert.DoesNotContain("new TaskDialog", view);
        Assert.DoesNotContain("AllTools.ParseEid", view);
        Assert.DoesNotContain("AllTools.ParseCategory", view);
        Assert.False(File.Exists(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Compat", "SheetDocTools.cs")));
        var bridge = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Revit", "IRevitBridge.cs"));
        Assert.Contains("CreateSheetAsync", bridge);
        Assert.Contains("PlaceViewOnSheetAsync", bridge);
        Assert.Contains("DuplicateViewAsync", bridge);
        Assert.Contains("SetViewTemplateAsync", bridge);
        Assert.Contains("SetCropBoxAsync", bridge);
        Assert.Contains("CreateDimensionBetweenWallsAsync", bridge);
        Assert.Contains("TagAllInActiveViewAsync", bridge);
        var adapter = AdapterSource.All();
        Assert.Contains("CreateSheetAsync", adapter);
        Assert.Contains("PlaceViewOnSheetAsync", adapter);
        Assert.Contains("TagAllInActiveViewAsync", adapter);
        Assert.Contains("Metric.MmToFt", adapter);
    }

    [Fact]
    public void SittingGaps_WorksharingOnBridge_CompatFileGone()
    {
        var query = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Core", "QueryTools.cs"));
        Assert.Contains("Name = \"sync_info\"", query);
        Assert.Contains("Name = \"audit_warnings_fix\"", query);
        Assert.DoesNotContain("new TaskDialog", query);
        Assert.DoesNotContain("AllTools.ParseEid", query);
        var document = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Core", "DocumentTools.cs"));
        Assert.Contains("Name = \"reload_links\"", document);
        Assert.Contains("Name = \"workset_assign\"", document);
        Assert.DoesNotContain("new TaskDialog", document);
        Assert.DoesNotContain("AllTools.ParseEid", document);
        Assert.False(File.Exists(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Compat", "WorksharingTools.cs")));
        var bridge = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Revit", "IRevitBridge.cs"));
        Assert.Contains("GetWarningsRawAsync", bridge);
        Assert.Contains("ReloadLinksAsync", bridge);
        Assert.Contains("AssignWorksetAsync", bridge);
        var adapter = AdapterSource.All();
        Assert.Contains("GetWarningsRawAsync", adapter);
        Assert.Contains("ReloadLinksAsync", adapter);
        Assert.Contains("AssignWorksetAsync", adapter);
    }

    [Fact]
    public void SittingGaps_IfcCostOnBridge_CompatFileGone()
    {
        var query = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Core", "QueryTools.cs"));
        Assert.Contains("Name = \"quantity_takeoff_by_category\"", query);
        Assert.Contains("GetQuantityTakeoffAsync", query);
        Assert.DoesNotContain("new TaskDialog", query);
        Assert.DoesNotContain("AllTools.ParseCategory", query);
        Assert.DoesNotContain("AllTools.ParseEid", query);
        Assert.False(File.Exists(Path.Combine(RepoRoot(), "Plugins", "core-tools", "Compat", "IfcCostTools.cs")));
        var pluginAllTools = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "core-tools", "AllTools.cs"));
        Assert.DoesNotContain("IfcCostTools", pluginAllTools);
        var bridge = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI.Core", "Revit", "IRevitBridge.cs"));
        Assert.Contains("GetQuantityTakeoffAsync", bridge);
        var adapter = AdapterSource.All();
        Assert.Contains("GetQuantityTakeoffAsync", adapter);
        Assert.Contains("ParseCategory", adapter);
    }

    [Fact]
    public void SittingGaps_CoreToolsIsBridgeOnly_FullAccessTypeGone()
    {
        var root = RepoRoot();
        Assert.False(File.Exists(Path.Combine(root, "Plugins", "core-tools", "CoreToolsFullAccessPlugin.cs")));
        var pluginAllTools = File.ReadAllText(Path.Combine(root, "Plugins", "core-tools", "AllTools.cs"));
        Assert.DoesNotContain("CreateFullAccess", pluginAllTools);
        Assert.DoesNotContain("Autodesk.Revit", pluginAllTools);
        var csproj = File.ReadAllText(Path.Combine(root, "Plugins", "core-tools", "CoreToolsPlugin.csproj"));
        Assert.DoesNotContain("LoadingAI.RevitUtil.csproj", csproj);
        Assert.DoesNotContain("Nice3point.Revit", csproj);
        var combined = string.Join("\n", Directory.GetFiles(Path.Combine(root, "Plugins", "core-tools"), "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("IRevitFullAccess", combined);
        Assert.DoesNotContain("Autodesk.Revit", combined);
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
