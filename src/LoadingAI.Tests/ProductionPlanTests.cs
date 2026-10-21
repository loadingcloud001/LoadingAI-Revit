using System.IO;
using LoadingAI.Plugins.QsFormwork;
using LoadingAI.Plugins.ViewTools;
using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class ProductionPlanTests
{
    [Fact]
    public async Task IssueCheck_FormatsCountsAndDoesNotWrite()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        bridge.NextIssueAudit = new IssueAuditDto(
            2, 1, 0, 3, 1, 0, 4, 0,
            new[] { new IssueAuditItemDto("view-not-on-sheet", "10", "Level 1", "FloorPlan") });
        var tool = QueryTools.Create(new FakePluginContext(bridge)).Single(t => t.Definition.Function.Name == "audit_issue_set");

        var result = await tool.ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.Contains("Issue check. No model changes.", result.Text);
        Assert.Contains("Views not on a sheet: 2", result.Text);
        Assert.Contains("view-not-on-sheet 10 Level 1 FloorPlan", result.Text);
        Assert.Contains(tool.Presets, p => p.Id == "category");
    }

    [Fact]
    public async Task SheetSetDryRun_ListsRowsAndDoesNotCreate()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = new ViewToolsPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_sheet_set");

        var result = await tool.ExecuteAsync("""
            {"sheetNumbers":["A101","A102"],"sheetNames":["Plan","Section"],"viewIds":["11","12"],"dryRun":true}
            """);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.Contains("Dry run: 2 sheet(s). No model changes.", result.Text);
        Assert.Contains("A101 Plan <- view 11", result.Text);
        Assert.Empty(bridge.CreateSheetCalls);
    }

    [Fact]
    public async Task SheetSet_PlacesEachViewOnTheNewSheet()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = new ViewToolsPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_sheet_set");

        var result = await tool.ExecuteAsync("""
            {"sheetNumbers":["A101"],"sheetNames":["Plan"],"viewIds":["11"],"x":100,"y":200}
            """);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal("11", bridge.PlaceViewOnSheetCalls[0].ViewId);
        Assert.Equal("1", bridge.PlaceViewOnSheetCalls[0].SheetId);
        Assert.Equal(100, bridge.PlaceViewOnSheetCalls[0].XMm);
    }

    [Fact]
    public async Task NumberingDryRun_DoesNotClaimACommit()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        bridge.NextNumberElements = new CommandResultDto(true, "Dry run: 1 Mark value(s). No model changes.\n9: D1 -> D-001");
        var tool = ParamTools.Create(new FakePluginContext(bridge)).Single(t => t.Definition.Function.Name == "numbering_auto");

        var result = await tool.ExecuteAsync("""{"category":"Rooms","order":"left-to-right","dryRun":true,"allInCategory":true}""");

        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.Contains("No model changes", result.Text);
        Assert.Equal("left-to-right", bridge.NumberElementsCalls[0].Order);
        Assert.True(bridge.NumberElementsCalls[0].DryRun);
    }

    [Fact]
    public async Task NumberingWholeCategoryFromTheForm_RefusesUntilOptedIn()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ParamTools.Create(new FakePluginContext(bridge)).Single(t => t.Definition.Function.Name == "numbering_auto");

        var result = await tool.ExecuteAsync("""{"category":"Rooms","allInCategory":false}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Contains("All in this category", result.Text);
        Assert.Empty(bridge.NumberElementsCalls);
    }

    [Fact]
    public async Task FormworkDryRun_NamesHostsAndRefusesAPanelCount()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = new QsFormworkPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_formwork_panels");

        var result = await tool.ExecuteAsync("""{"elementIds":["4","5"],"faceTypes":["Wall_Exterior"],"dryRun":true}""");

        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.Contains("2 host(s)", result.Text);
        Assert.Contains("Faces: Wall_Exterior.", result.Text);
        Assert.Contains("Panel count is not known until a real run.", result.Text);
        Assert.Contains("No model changes", result.Text);
        Assert.Empty(bridge.FormworkPanelsCalls);
    }

    [Fact]
    public async Task ImportDryRun_ListsNothingAndDoesNotCommit()
    {
        Directory.CreateDirectory(ExportPaths.Root);
        var path = Path.Combine(ExportPaths.Root, "preview-param-excel.csv");
        File.WriteAllText(path, "Id,Comments\n42,hello\n");
        try
        {
            var bridge = new FakeRevitBridge { HasActiveDocument = true };
            var tool = ParamTools.Create(new FakePluginContext(bridge))
                .Single(t => t.Definition.Function.Name == "import_excel_to_params");

            var result = await tool.ExecuteAsync($$"""{"filePath":"preview-param-excel.csv","paramName":"Comments","dryRun":true}""");

            Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
            Assert.StartsWith("No model changes", result.Text);
            Assert.True(bridge.ImportParamsCalls.Single().DryRun);
            Assert.Contains(tool.Presets, p => p.Id == "dryRun" && Equals(p.Default, true));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ImportWithoutDryRun_StillRecordsAWrite()
    {
        Directory.CreateDirectory(ExportPaths.Root);
        var path = Path.Combine(ExportPaths.Root, "commit-param-excel.csv");
        File.WriteAllText(path, "Id,Comments\n42,hello\n");
        try
        {
            var bridge = new FakeRevitBridge { HasActiveDocument = true };
            var tool = ParamTools.Create(new FakePluginContext(bridge))
                .Single(t => t.Definition.Function.Name == "import_excel_to_params");

            var result = await tool.ExecuteAsync($$"""{"filePath":"commit-param-excel.csv","paramName":"Comments"}""");

            Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
            Assert.False(bridge.ImportParamsCalls.Single().DryRun);
            Assert.Contains("Imported 1/1", result.Text);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task CopyAndReplaceDryRun_DoNotCommit()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tools = ParamTools.Create(new FakePluginContext(bridge)).ToList();
        var copy = tools.Single(t => t.Definition.Function.Name == "copy_params");
        var replace = tools.Single(t => t.Definition.Function.Name == "replace_text");

        var copied = await copy.ExecuteAsync("""{"category":"Walls","sourceParam":"Mark","targetParam":"Comments","dryRun":true}""");
        var replaced = await replace.ExecuteAsync("""{"category":"Doors","paramName":"Mark","pattern":"A","replacement":"B","dryRun":true}""");

        Assert.Equal(ToolWriteCertainty.NoWrite, copied.WriteCertainty);
        Assert.Equal(ToolWriteCertainty.NoWrite, replaced.WriteCertainty);
        Assert.StartsWith("No model changes", copied.Text);
        Assert.StartsWith("No model changes", replaced.Text);
        Assert.True(bridge.CopyParamsCalls.Single().DryRun);
        Assert.True(bridge.ReplaceTextCalls.Single().DryRun);
        Assert.Contains(copy.Presets, p => p.Id == "sourceParam");
        Assert.Contains(replace.Presets, p => p.Id == "pattern");
    }

    [Fact]
    public async Task CopyWithoutDryRun_StillRecordsAWrite()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ParamTools.Create(new FakePluginContext(bridge)).Single(t => t.Definition.Function.Name == "copy_params");

        var result = await tool.ExecuteAsync("""{"category":"Walls","sourceParam":"Mark","targetParam":"Comments"}""");

        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.False(bridge.CopyParamsCalls.Single().DryRun);
    }

    [Fact]
    public async Task DuplicateView_EmptyPickUsesTheActiveView()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewTools.Create(new FakePluginContext(bridge)).Single(t => t.Definition.Function.Name == "duplicate_view_ex");

        var result = await tool.ExecuteAsync("""{"withDetailing":true}""");

        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal("", bridge.DuplicateViewCalls.Single().ViewId);
        Assert.True(bridge.DuplicateViewCalls.Single().WithDetailing);
        Assert.Contains(tool.Presets, p => p.Id == "viewIds" && p.Kind == PresetFieldKind.PickElements);
    }

    [Fact]
    public async Task ViewTemplateDryRun_UsesTheNameAndDoesNotCommit()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewTools.Create(new FakePluginContext(bridge)).Single(t => t.Definition.Function.Name == "set_view_template");

        var result = await tool.ExecuteAsync("""{"viewIds":["11","12"],"templateName":"Working","dryRun":true}""");

        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.StartsWith("No model changes", result.Text);
        Assert.Equal("Working", bridge.SetViewTemplateCalls.Single().TemplateName);
        Assert.True(bridge.SetViewTemplateCalls.Single().DryRun);
        Assert.Equal(new[] { "11", "12" }, bridge.SetViewTemplateCalls.Single().ViewIds);
        Assert.Contains(tool.Presets, p => p.Id == "templateName" && p.Kind == PresetFieldKind.String);
    }

    [Fact]
    public async Task ViewTemplateWithoutDryRun_StillRecordsAWrite()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewTools.Create(new FakePluginContext(bridge)).Single(t => t.Definition.Function.Name == "set_view_template");

        var result = await tool.ExecuteAsync("""{"viewId":"11","templateId":"99"}""");

        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.False(bridge.SetViewTemplateCalls.Single().DryRun);
        Assert.Equal("99", bridge.SetViewTemplateCalls.Single().TemplateId);
    }

    [Fact]
    public async Task CropAndDimension_TakePicksInsteadOfRawIds()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tools = ViewTools.Create(new FakePluginContext(bridge)).ToList();
        var crop = tools.Single(t => t.Definition.Function.Name == "set_crop_box");
        var dimension = tools.Single(t => t.Definition.Function.Name == "create_dimension");

        await crop.ExecuteAsync("""{"viewIds":["11","12"],"enabled":false}""");
        var dim = await dimension.ExecuteAsync("""{"wallIds1":["1"],"wallIds2":["2"]}""");

        Assert.Equal(new[] { "11", "12" }, bridge.SetCropBoxCalls.Single().ViewIds);
        Assert.False(bridge.SetCropBoxCalls.Single().Enabled);
        Assert.Equal(ToolWriteCertainty.Committed, dim.WriteCertainty);
        Assert.Equal("1", bridge.CreateDimensionCalls.Single().WallId1);
        Assert.Equal("2", bridge.CreateDimensionCalls.Single().WallId2);
        Assert.Null(bridge.CreateDimensionCalls.Single().ViewId);
        Assert.Contains(crop.Presets, p => p.Kind == PresetFieldKind.PickElements);
        Assert.Contains(dimension.Presets, p => p.Id == "wallIds1" && p.Kind == PresetFieldKind.PickElements);
    }

    [Fact]
    public async Task TagAllDryRun_DoesNotCommit()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewTools.Create(new FakePluginContext(bridge)).Single(t => t.Definition.Function.Name == "tag_all");

        var result = await tool.ExecuteAsync("""{"category":"Doors","dryRun":true}""");

        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.StartsWith("No model changes", result.Text);
        Assert.Equal("Doors", bridge.TagAllCalls.Single());
        Assert.True(bridge.TagAllDryRun.Single());
        Assert.Contains(tool.Presets, p => p.Id == "dryRun");
    }

    [Fact]
    public void ScheduleRead_HasANameFormAndNoDryRun()
    {
        var tool = new ViewToolsPlugin().CreateTools(new FakePluginContext(new FakeRevitBridge()))
            .Single(t => t.Definition.Function.Name == "get_schedule_data");

        Assert.Contains(tool.Presets, p => p.Id == "name" && p.Kind == PresetFieldKind.String);
        Assert.Contains(tool.Presets, p => p.Id == "rowLimit" && p.Kind == PresetFieldKind.Number);
        Assert.DoesNotContain(tool.Presets, p => p.Id == "dryRun");
    }

    [Fact]
    public void AdapterDryRun_ReturnsBeforeTheTransaction()
    {
        var import = MethodSlice(AdapterSource.Containing("public Task<CommandResultDto> ImportParamsAsync"),
            "public Task<CommandResultDto> ImportParamsAsync", "public Task<CommandResultDto> CopyParamsAsync");
        var copy = MethodSlice(AdapterSource.Containing("public Task<CommandResultDto> CopyParamsAsync"),
            "public Task<CommandResultDto> CopyParamsAsync", "public Task<CommandResultDto> ReplaceTextInParamAsync");
        var replace = MethodSlice(AdapterSource.Containing("public Task<CommandResultDto> ReplaceTextInParamAsync"),
            "public Task<CommandResultDto> ReplaceTextInParamAsync", "public Task<CommandResultDto> NumberElementsAsync");
        var tag = MethodSlice(AdapterSource.Containing("public Task<CommandResultDto> TagAllInActiveViewAsync"),
            "public Task<CommandResultDto> TagAllInActiveViewAsync", "private const int MaxTagAll");
        var template = MethodSlice(AdapterSource.Containing("public Task<CommandResultDto> SetViewTemplateAsync"),
            "public Task<CommandResultDto> SetViewTemplateAsync", "public Task<CommandResultDto> SetCropBoxAsync");
        var duplicate = MethodSlice(AdapterSource.Containing("public Task<CommandResultDto> DuplicateViewAsync"),
            "public Task<CommandResultDto> DuplicateViewAsync", "public Task<CommandResultDto> SetViewTemplateAsync");

        AssertBeforeTransaction(import);
        AssertBeforeTransaction(copy);
        AssertBeforeTransaction(replace);
        AssertBeforeTransaction(tag);
        Assert.Contains("v.IsTemplate && string.Equals(v.Name", AdapterSource.Containing("public Task<CommandResultDto> SetViewTemplateAsync"));
        Assert.Equal(1, SourceSlice.Count(template, "new Transaction("));
        Assert.Contains("ActiveView", duplicate);
        Assert.Contains("string.IsNullOrWhiteSpace(viewId)", duplicate);
        Assert.Contains("TryPreviewSet", import);
        Assert.DoesNotContain("TrySetParameter", import.Substring(0, import.IndexOf("new Transaction(", StringComparison.Ordinal)));
    }

    private static string MethodSlice(string src, string start, string end)
    {
        var at = src.IndexOf(start, StringComparison.Ordinal);
        var to = src.IndexOf(end, at + start.Length, StringComparison.Ordinal);
        return src.Substring(at, to - at);
    }

    private static void AssertBeforeTransaction(string method)
    {
        var dry = method.IndexOf("if (dryRun)", StringComparison.Ordinal);
        var tx = method.IndexOf("new Transaction(", StringComparison.Ordinal);
        Assert.True(dry >= 0 && tx > dry, method.Substring(0, Math.Min(80, method.Length)));
    }
}
