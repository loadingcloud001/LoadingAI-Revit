using System.IO;
using LoadingAI.Llm;
using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

/// <summary>
/// Tool-handler tests against <see cref="FakeRevitBridge"/>.
/// These run in GitHub-hosted CI — they never start Revit.
/// Call tool Create* directly; do not go through PluginLoader (TaskDialog confirm).
/// </summary>
public class FakeBridgeToolTests
{
    [Fact]
    public async Task GetModelSummary_NoDocument_AsksToOpenModel()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = QueryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "get_model_summary");

        var text = await tool.Handler("{}");

        Assert.Equal("Please open a model first.", text);
    }

    [Fact]
    public async Task GetModelSummary_OpenDocument_FormatsCounts()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            ModelSummary = new("HQ", @"C:\models\hq.rvt", true, 12, 34, 5, 6)
        };
        var tool = QueryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "get_model_summary");

        var text = await tool.Handler("{}");

        Assert.Equal("Model: HQ\nPath: C:\\models\\hq.rvt\nViews: 12\nWalls: 34\nDoors: 5\nWindows: 6", text);
    }

    [Fact]
    public async Task IsolateInView_EmptyJsonAndEmptySelection_AsksToSelect()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            Selection = new(0, Array.Empty<string>(), Array.Empty<string>())
        };
        var tool = ViewTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "isolate_in_view");

        var text = await tool.Handler("{}");

        Assert.Equal("Please select elements first.", text);
        Assert.Empty(bridge.IsolateCalls);
    }

    [Fact]
    public async Task IsolateInView_WithElementIds_ReturnsBridgeMessage()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "isolate_in_view");

        var text = await tool.Handler("""{"elementIds":["101","102"]}""");

        Assert.Contains("Isolated 2 elements", text);
        Assert.Single(bridge.IsolateCalls);
        Assert.Equal(new[] { "101", "102" }, bridge.IsolateCalls[0].Ids);
        Assert.True(bridge.IsolateCalls[0].Isolate);
    }

    [Fact]
    public async Task OverrideColorInView_EmptyJsonAndEmptySelection_AsksToSelect()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            Selection = new(0, Array.Empty<string>(), Array.Empty<string>())
        };
        var ctx = new FakePluginContext(bridge);
        Assert.Null(ctx.FullAccess);
        var tool = ViewTools.Create(ctx)
            .Single(t => t.Definition.Function.Name == "override_color_in_view");

        var text = await tool.Handler("{}");

        Assert.Equal("Please select elements first.", text);
        Assert.Empty(bridge.OverrideColorCalls);
        Assert.Equal(ToolTier.L2, tool.Tier);
        Assert.False(tool.RequiresWriteConfirm);
    }

    [Fact]
    public async Task OverrideColorInView_WithElementIds_ReturnsBridgeMessage()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "override_color_in_view");

        var text = await tool.Handler("""{"elementIds":["101","102"],"color":"blue"}""");

        Assert.Contains("Set blue override for 2 element(s)", text);
        Assert.Single(bridge.OverrideColorCalls);
        Assert.Equal(new[] { "101", "102" }, bridge.OverrideColorCalls[0].Ids);
        Assert.Equal("blue", bridge.OverrideColorCalls[0].Color);
        Assert.False(bridge.OverrideColorCalls[0].Reset);
    }

    [Fact]
    public async Task ColorByConstructionSchedule_InvalidDate_DoesNotCallBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "color_by_construction_schedule");

        var text = await tool.Handler("""{"startDate":"not-a-date"}""");

        Assert.Equal("Invalid startDate 'not-a-date'.", text);
        Assert.Empty(bridge.ColorByScheduleCalls);
        Assert.Equal(ToolTier.L2, tool.Tier);
        Assert.False(tool.RequiresWriteConfirm);
    }

    [Fact]
    public async Task ColorByConstructionSchedule_Success_WithoutFullAccess()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var ctx = new FakePluginContext(bridge);
        Assert.Null(ctx.FullAccess);
        var tool = ViewTools.Create(ctx)
            .Single(t => t.Definition.Function.Name == "color_by_construction_schedule");

        var text = await tool.Handler("""{"startDate":"2024-01-08","palette":"heat"}""");

        Assert.Contains("by construction schedule", text);
        Assert.Contains("2024-01-08", text);
        Assert.Contains("Palette heat", text);
        Assert.Single(bridge.ColorByScheduleCalls);
        Assert.Equal("2024-01-08", bridge.ColorByScheduleCalls[0].StartDate);
        Assert.Equal(14, bridge.ColorByScheduleCalls[0].IntervalDays);
    }

    [Fact]
    public async Task ColorByParameter_EmptyParamName_DoesNotCallBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "color_by_parameter");

        var text = await tool.Handler("{}");

        Assert.Equal("Provide paramName.", text);
        Assert.Empty(bridge.ColorByParameterCalls);
        Assert.Equal(ToolTier.L2, tool.Tier);
        Assert.False(tool.RequiresWriteConfirm);
    }

    [Fact]
    public async Task ColorByParameter_Selection_WithoutFullAccess()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            Selection = new(2, new[] { "Walls" }, new[] { "101", "102" })
        };
        var ctx = new FakePluginContext(bridge);
        Assert.Null(ctx.FullAccess);
        var tool = ViewTools.Create(ctx)
            .Single(t => t.Definition.Function.Name == "color_by_parameter");

        var text = await tool.Handler("""{"paramName":"Mark"}""");

        Assert.Contains("by 'Mark'", text);
        Assert.Contains("Palette rainbow", text);
        Assert.Single(bridge.ColorByParameterCalls);
        Assert.Equal("Mark", bridge.ColorByParameterCalls[0].ParamName);
        Assert.Equal(new[] { "101", "102" }, bridge.ColorByParameterCalls[0].Ids);
    }

    [Fact]
    public async Task ColorByParameter_NoDocument_ReturnsBridgeMessage()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = ViewTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "color_by_parameter");

        var text = await tool.Handler("""{"paramName":"Mark","category":"Walls"}""");

        Assert.Equal("Please open a model first.", text);
        Assert.Single(bridge.ColorByParameterCalls);
    }

    [Fact]
    public async Task CreateSheet_InvalidJson_ReturnsInvalidArgs_DoesNotCallBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_sheet");

        var text = await tool.Handler("");

        Assert.StartsWith("Invalid args:", text);
        Assert.Empty(bridge.CreateSheetCalls);
        Assert.Equal(ToolTier.L3, tool.Tier);
        Assert.True(tool.RequiresWriteConfirm);
    }

    [Fact]
    public async Task CreateSheet_NoDocument_ReturnsBridgeMessage()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = ViewTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_sheet");

        var text = await tool.Handler("""{"sheetNumber":"A101","sheetName":"Plans"}""");

        Assert.Equal("Please open a model first.", text);
        Assert.Single(bridge.CreateSheetCalls);
        Assert.Equal("A101", bridge.CreateSheetCalls[0].SheetNumber);
        Assert.Equal("Plans", bridge.CreateSheetCalls[0].SheetName);
        Assert.Null(bridge.CreateSheetCalls[0].TitleBlockName);
    }

    [Fact]
    public async Task CreateSheet_Success_WithoutFullAccess()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var ctx = new FakePluginContext(bridge);
        Assert.Null(ctx.FullAccess);
        var tool = ViewTools.Create(ctx)
            .Single(t => t.Definition.Function.Name == "create_sheet");

        var text = await tool.Handler("""{"sheetNumber":"A101","sheetName":"Plans","titleBlockName":"A1"}""");

        Assert.Equal("Created sheet 1 A101 \"Plans\"", text);
        Assert.Single(bridge.CreateSheetCalls);
        Assert.Equal("A1", bridge.CreateSheetCalls[0].TitleBlockName);
    }

    [Fact]
    public async Task PlaceViewOnSheet_NoDocument_ReturnsBridgeMessage()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = ViewTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "place_view_on_sheet");

        var text = await tool.Handler("""{"viewId":"10","sheetId":"20","x":304.8,"y":609.6}""");

        Assert.Equal("Please open a model first.", text);
        Assert.Single(bridge.PlaceViewOnSheetCalls);
        Assert.Equal("10", bridge.PlaceViewOnSheetCalls[0].ViewId);
        Assert.Equal("20", bridge.PlaceViewOnSheetCalls[0].SheetId);
        Assert.Equal(304.8, bridge.PlaceViewOnSheetCalls[0].XMm);
        Assert.Equal(609.6, bridge.PlaceViewOnSheetCalls[0].YMm);
        Assert.Equal(ToolTier.L3, tool.Tier);
        Assert.False(tool.RequiresWriteConfirm);
    }

    [Fact]
    public async Task PlaceViewOnSheet_Success_PassesMillimetres_MessageInFeet()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "place_view_on_sheet");

        var text = await tool.Handler("""{"viewId":"10","sheetId":"20","x":304.8,"y":609.6}""");

        Assert.Equal("Placed view 10 on sheet 20 at (1,2) viewport 9", text);
        Assert.Single(bridge.PlaceViewOnSheetCalls);
        Assert.Equal(304.8, bridge.PlaceViewOnSheetCalls[0].XMm);
        Assert.Equal(609.6, bridge.PlaceViewOnSheetCalls[0].YMm);
    }

    [Fact]
    public async Task DuplicateViewEx_WithoutDetailing_PreservesTrailingSpace()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "duplicate_view_ex");

        var text = await tool.Handler("""{"viewId":"11"}""");

        Assert.Equal("Duplicated 11 -> 2 ", text);
        Assert.Single(bridge.DuplicateViewCalls);
        Assert.False(bridge.DuplicateViewCalls[0].WithDetailing);
        Assert.Equal(ToolTier.L2, tool.Tier);
        Assert.False(tool.RequiresWriteConfirm);
    }

    [Fact]
    public async Task DuplicateViewEx_WithDetailing()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "duplicate_view_ex");

        var text = await tool.Handler("""{"viewId":"11","withDetailing":true}""");

        Assert.Equal("Duplicated 11 -> 2 with detailing", text);
        Assert.True(bridge.DuplicateViewCalls[0].WithDetailing);
    }

    [Fact]
    public async Task SetViewTemplate_EmptyTemplateId_Clears()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "set_view_template");

        var text = await tool.Handler("""{"viewId":"11","templateId":""}""");

        Assert.Equal("Cleared template for 11", text);
        Assert.Single(bridge.SetViewTemplateCalls);
        Assert.Equal("", bridge.SetViewTemplateCalls[0].TemplateId);
        Assert.Equal(ToolTier.L2, tool.Tier);
        Assert.False(tool.RequiresWriteConfirm);
    }

    [Fact]
    public async Task SetViewTemplate_SetsId()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "set_view_template");

        var text = await tool.Handler("""{"viewId":"11","templateId":"99"}""");

        Assert.Equal("Set template 99 for 11", text);
        Assert.Equal("99", bridge.SetViewTemplateCalls[0].TemplateId);
    }

    [Fact]
    public async Task SetCropBox_OnAndOff()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var on = ViewTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "set_crop_box");

        var onText = await on.Handler("""{"viewId":"11","enabled":true}""");
        var offText = await on.Handler("""{"viewId":"11","enabled":false}""");

        Assert.Equal("Set crop on for 11", onText);
        Assert.Equal("Set crop off for 11", offText);
        Assert.Equal(2, bridge.SetCropBoxCalls.Count);
        Assert.True(bridge.SetCropBoxCalls[0].Enabled);
        Assert.False(bridge.SetCropBoxCalls[1].Enabled);
        Assert.Equal(ToolTier.L2, on.Tier);
        Assert.False(on.RequiresWriteConfirm);
    }

    [Fact]
    public async Task CreateDimension_NoDocument_ReturnsBridgeMessage()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = ViewTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_dimension");

        var text = await tool.Handler("""{"wallId1":"1","wallId2":"2"}""");

        Assert.Equal("Please open a model first.", text);
        Assert.Single(bridge.CreateDimensionCalls);
        Assert.Equal("1", bridge.CreateDimensionCalls[0].WallId1);
        Assert.Equal("2", bridge.CreateDimensionCalls[0].WallId2);
        Assert.Null(bridge.CreateDimensionCalls[0].ViewId);
        Assert.Equal(ToolTier.L3, tool.Tier);
        Assert.False(tool.RequiresWriteConfirm);
    }

    [Fact]
    public async Task CreateDimension_Success()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_dimension");

        var text = await tool.Handler("""{"wallId1":"1","wallId2":"2","viewId":"11"}""");

        Assert.Equal("Created dimension 3 between 1 and 2 in Level 1", text);
        Assert.Equal("11", bridge.CreateDimensionCalls[0].ViewId);
    }

    [Fact]
    public async Task TagAll_NoDocument_ReturnsBridgeMessage()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = ViewTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "tag_all");

        var text = await tool.Handler("""{"category":"Walls"}""");

        Assert.Equal("Please open a model first.", text);
        Assert.Single(bridge.TagAllCalls);
        Assert.Equal("Walls", bridge.TagAllCalls[0]);
        Assert.Equal(ToolTier.L3, tool.Tier);
        Assert.False(tool.RequiresWriteConfirm);
    }

    [Fact]
    public async Task TagAll_Success_WithoutFullAccess()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var ctx = new FakePluginContext(bridge);
        Assert.Null(ctx.FullAccess);
        var tool = ViewTools.Create(ctx)
            .Single(t => t.Definition.Function.Name == "tag_all");

        var text = await tool.Handler("""{"category":"Doors"}""");

        Assert.Equal("Tagged 0/0 Doors in Level 1", text);
        Assert.Single(bridge.TagAllCalls);
        Assert.Equal("Doors", bridge.TagAllCalls[0]);
    }

    [Fact]
    public async Task SetParameterById_Failure_ReturnsBridgeMessage()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            NextSetParameterResult = new(false, "Parameter 'Mark' is read-only.")
        };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "set_parameter_by_id");

        var text = await tool.Handler("""{"elementId":"42","paramName":"Mark","value":"A1"}""");

        Assert.Equal("Parameter 'Mark' is read-only.", text);
        Assert.Single(bridge.SetParameterCalls);
    }

    [Fact]
    public async Task SetParameterById_Success_FormatsConfirmation()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "set_parameter_by_id");

        var text = await tool.Handler("""{"elementId":"42","paramName":"Comments","value":"ok"}""");

        Assert.Equal("Set Comments=\"ok\" on 42.", text);
        Assert.Equal(("42", "Comments", "ok"), bridge.SetParameterCalls.Single());
    }

    [Fact]
    public async Task SetParameterById_NoDocument_ReturnsBridgeMessage()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "set_parameter_by_id");

        var text = await tool.Handler("""{"elementId":"42","paramName":"Comments","value":"ok"}""");

        Assert.Equal("Please open a model first.", text);
        Assert.Empty(bridge.SetParameterCalls);
    }

    [Fact]
    public async Task RenameElements_EmptyIds_DoesNotCallBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "rename_elements");

        var text = await tool.Handler("""{"elementIds":[]}""");

        Assert.Equal("Provide elementIds.", text);
        Assert.Empty(bridge.RenameElementsCalls);
        Assert.True(tool.RequiresWriteConfirm);
        Assert.Equal(ToolTier.L3, tool.Tier);
    }

    [Fact]
    public async Task RenameElements_NamesLengthMismatch_DoesNotCallBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "rename_elements");

        var text = await tool.Handler("""{"elementIds":["1","2"],"newNames":["A"]}""");

        Assert.Equal("newNames length must match elementIds.", text);
        Assert.Empty(bridge.RenameElementsCalls);
    }

    [Fact]
    public async Task RenameElements_NoPattern_DoesNotCallBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "rename_elements");

        var text = await tool.Handler("""{"elementIds":["1"]}""");

        Assert.Equal("Provide newNames or newNamePattern.", text);
        Assert.Empty(bridge.RenameElementsCalls);
    }

    [Fact]
    public async Task RenameElements_Success_WithoutFullAccess()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "rename_elements");

        var text = await tool.Handler("""{"elementIds":["1","2"],"newNames":["A","B"]}""");

        Assert.Equal("Renamed 2/2 element(s).", text);
        Assert.Single(bridge.RenameElementsCalls);
        Assert.Equal(new[] { "1", "2" }, bridge.RenameElementsCalls[0].Ids);
        Assert.Null(new FakePluginContext(bridge).FullAccess);
    }

    [Fact]
    public async Task RenameElements_NoDocument_AsksToOpenModel()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "rename_elements");

        var text = await tool.Handler("""{"elementIds":["1"],"newNamePattern":"T-{i}"}""");

        Assert.Equal("Please open a model first.", text);
        Assert.Empty(bridge.RenameElementsCalls);
    }

    [Fact]
    public async Task ApplyConstructionDates_InvalidDate_DoesNotCallBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "apply_construction_dates");

        var text = await tool.Handler("""{"startDate":"not-a-date"}""");

        Assert.Equal("Invalid startDate 'not-a-date'. Use YYYY-MM-DD or YYYYMMDD.", text);
        Assert.Empty(bridge.ApplyConstructionDatesCalls);
        Assert.True(tool.RequiresWriteConfirm);
        Assert.Equal(ToolTier.L3, tool.Tier);
    }

    [Fact]
    public async Task ApplyConstructionDates_Success_WithoutFullAccess()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "apply_construction_dates");

        var text = await tool.Handler("""{"startDate":"2024-01-08"}""");

        Assert.Contains("bottom-up", text);
        Assert.Contains("Construction Date", text);
        Assert.Contains("2024-01-08", text);
        Assert.Single(bridge.ApplyConstructionDatesCalls);
        Assert.Equal(14, bridge.ApplyConstructionDatesCalls[0].IntervalDays);
        Assert.Null(new FakePluginContext(bridge).FullAccess);
    }

    [Fact]
    public async Task ApplyConstructionDates_NoDocument_AsksToOpenModel()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "apply_construction_dates");

        var text = await tool.Handler("""{"startDate":"2024-01-08"}""");

        Assert.Equal("Please open a model first.", text);
        Assert.Empty(bridge.ApplyConstructionDatesCalls);
    }

    [Fact]
    public async Task ExportParams_NoDocument_AsksToOpenModel()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "export_params_to_excel");

        var text = await tool.Handler("""{"category":"Walls"}""");

        Assert.Equal("Please open a model first.", text);
        Assert.Empty(bridge.ExportParamsCalls);
        Assert.Equal(ToolTier.L1, tool.Tier);
        Assert.False(tool.RequiresWriteConfirm);
        Assert.Null(new FakePluginContext(bridge).FullAccess);
    }

    [Fact]
    public async Task ExportParams_UnknownCategory_ReturnsErrorWithoutFailedPrefix()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            NextExportParams = new ParamExportDto("Foo", new[] { "Comments", "Mark" }, Array.Empty<ParamExportRowDto>(), "Unknown category Foo")
        };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "export_params_to_excel");

        var text = await tool.Handler("""{"category":"Foo"}""");

        Assert.Equal("Unknown category Foo", text);
        Assert.DoesNotContain("Failed:", text);
        Assert.Single(bridge.ExportParamsCalls);
        Assert.Equal("Foo", bridge.ExportParamsCalls[0].Category);
    }

    [Fact]
    public async Task ExportParams_InvalidJson_DoesNotCallBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "export_params_to_excel");

        var text = await tool.Handler("{");

        Assert.StartsWith("Invalid args:", text);
        Assert.Empty(bridge.ExportParamsCalls);
    }

    [Fact]
    public async Task ExportParams_WritesCsvUnderExportsRoot()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            NextExportParams = new ParamExportDto(
                "Walls",
                new[] { "Comments", "Mark" },
                new[] { new ParamExportRowDto("42", new[] { "ok", "A1" }) })
        };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "export_params_to_excel");

        var text = await tool.Handler("""{"category":"Walls","paramNames":"Comments,Mark"}""");

        Assert.StartsWith("Exported 1 Walls to ", text);
        Assert.Contains("Columns: Comments, Mark", text);
        Assert.Contains("First 3:", text);
        Assert.Contains("42", text);
        Assert.Contains("A1", text);
        Assert.True(ExportPaths.IsUnderRoot(text.Split('\n')[0].Substring("Exported 1 Walls to ".Length)));
        Assert.Single(bridge.ExportParamsCalls);
        Assert.Equal(new[] { "Comments", "Mark" }, bridge.ExportParamsCalls[0].Columns);
        Assert.Equal(200, bridge.ExportParamsCalls[0].Limit);
    }

    [Fact]
    public async Task ImportExcel_FileNotFound_DoesNotCallBridge()
    {
        var missing = Path.Combine(ExportPaths.Root, "missing-param-excel.csv");
        if (File.Exists(missing)) File.Delete(missing);
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "import_excel_to_params");

        var text = await tool.Handler($$"""{"filePath":"missing-param-excel.csv","paramName":"Comments"}""");

        Assert.StartsWith("File not found:", text);
        Assert.Empty(bridge.ImportParamsCalls);
        Assert.True(tool.RequiresWriteConfirm);
        Assert.Equal(ToolTier.L3, tool.Tier);
    }

    [Fact]
    public async Task ImportExcel_PathOutsideRoot_DoesNotCallBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "import_excel_to_params");

        var text = await tool.Handler("""{"filePath":"C:\\Windows\\Temp\\evil.csv","paramName":"Comments"}""");

        Assert.Contains("Path must be under", text);
        Assert.DoesNotContain("Failed:", text);
        Assert.Empty(bridge.ImportParamsCalls);
    }

    [Fact]
    public async Task ImportExcel_EmptyFile_DoesNotCallBridge()
    {
        Directory.CreateDirectory(ExportPaths.Root);
        var path = Path.Combine(ExportPaths.Root, "empty-param-excel.csv");
        File.WriteAllText(path, "Id,Comments\n");
        try
        {
            var bridge = new FakeRevitBridge { HasActiveDocument = true };
            var tool = ParamTools.Create(new FakePluginContext(bridge))
                .Single(t => t.Definition.Function.Name == "import_excel_to_params");

            var text = await tool.Handler($$"""{"filePath":"empty-param-excel.csv","paramName":"Comments"}""");

            Assert.Equal("File empty or no data rows", text);
            Assert.Empty(bridge.ImportParamsCalls);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ImportExcel_Success_PassesRowsAndSourcePath()
    {
        Directory.CreateDirectory(ExportPaths.Root);
        var path = Path.Combine(ExportPaths.Root, "import-param-excel.csv");
        File.WriteAllText(path, "Id,Comments\n42,hello\n");
        try
        {
            var bridge = new FakeRevitBridge { HasActiveDocument = true };
            var tool = ParamTools.Create(new FakePluginContext(bridge))
                .Single(t => t.Definition.Function.Name == "import_excel_to_params");

            var text = await tool.Handler($$"""{"filePath":"import-param-excel.csv","paramName":"Comments"}""");

            Assert.Contains("Imported 1/1 (0 skipped) Comments from ", text);
            Assert.Contains(path, text);
            Assert.Single(bridge.ImportParamsCalls);
            Assert.Equal("Comments", bridge.ImportParamsCalls[0].ParamName);
            Assert.Equal("42", bridge.ImportParamsCalls[0].Rows[0].Id);
            Assert.Equal("hello", bridge.ImportParamsCalls[0].Rows[0].Value);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task CopyParams_Success_WithoutFullAccess()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "copy_params");

        var text = await tool.Handler("""{"category":"Walls","sourceParam":"Comments","targetParam":"Mark"}""");

        Assert.Equal("Copied 0/0 Comments->Mark for Walls", text);
        Assert.Single(bridge.CopyParamsCalls);
        Assert.Equal(200, bridge.CopyParamsCalls[0].Limit);
        Assert.True(tool.RequiresWriteConfirm);
        Assert.Equal(ToolTier.L2, tool.Tier);
        Assert.Null(new FakePluginContext(bridge).FullAccess);
    }

    [Fact]
    public async Task CopyParams_NoDocument_DoesNotRecordCall()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "copy_params");

        var text = await tool.Handler("""{"category":"Walls","sourceParam":"Comments","targetParam":"Mark"}""");

        Assert.Equal("Please open a model first.", text);
        Assert.Empty(bridge.CopyParamsCalls);
    }

    [Fact]
    public async Task ReplaceText_Success_WithoutFullAccess()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "replace_text");

        var text = await tool.Handler("""{"category":"Walls","paramName":"Comments","pattern":"old","replacement":"new"}""");

        Assert.Equal("Replaced 0/0 Comments /old/ -> new", text);
        Assert.Single(bridge.ReplaceTextCalls);
        Assert.True(tool.RequiresWriteConfirm);
        Assert.Equal(ToolTier.L2, tool.Tier);
    }

    [Fact]
    public async Task NumberingAuto_InvalidJson_StillCallsBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "numbering_auto");

        var text = await tool.Handler("{");

        Assert.Equal("Numbered 0/0 Mark from 001", text);
        Assert.Single(bridge.NumberElementsCalls);
        Assert.Null(bridge.NumberElementsCalls[0].Category);
        Assert.Equal("", bridge.NumberElementsCalls[0].Prefix);
        Assert.Equal(1, bridge.NumberElementsCalls[0].Start);
        Assert.Equal(3, bridge.NumberElementsCalls[0].Pad);
        Assert.True(tool.RequiresWriteConfirm);
        Assert.Equal(ToolTier.L2, tool.Tier);
    }

    [Fact]
    public async Task NumberingAuto_NoDocument_DoesNotRecordCall()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = ParamTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "numbering_auto");

        var text = await tool.Handler("""{"category":"Walls"}""");

        Assert.Equal("Please open a model first.", text);
        Assert.Empty(bridge.NumberElementsCalls);
    }

    [Fact]
    public async Task CreateWall_NoDocument_AsksToOpenModel()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = GeometryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_wall");

        var text = await tool.Handler("""{"levelName":"Level 1","x1":0,"y1":0,"x2":4000,"y2":0}""");

        Assert.Equal("Please open a model first.", text);
        Assert.Single(bridge.CreateWallCalls);
        Assert.Null(new FakePluginContext(bridge).FullAccess);
    }

    [Fact]
    public async Task CreateWall_Success_WithoutFullAccess_PassesMillimetres()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var ctx = new FakePluginContext(bridge);
        Assert.Null(ctx.FullAccess);
        var tool = GeometryTools.Create(ctx)
            .Single(t => t.Definition.Function.Name == "create_wall");

        var text = await tool.Handler("""{"levelName":"Level 1","x1":0,"y1":0,"x2":4000,"y2":0}""");

        Assert.Contains("Created wall 101 on Level 1", text);
        Assert.Contains("mm", text);
        Assert.Single(bridge.CreateWallCalls);
        Assert.Equal(0, bridge.CreateWallCalls[0].X1);
        Assert.Equal(4000, bridge.CreateWallCalls[0].X2);
        Assert.Equal("Level 1", bridge.CreateWallCalls[0].LevelName);
    }

    [Fact]
    public async Task CreateFamilyInstance_Success_WithoutFullAccess()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = GeometryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_family_instance");

        var text = await tool.Handler("""{"symbolName":"Single-Flush","x":1000,"y":2000}""");

        Assert.Contains("Created instance 202 'Single-Flush'", text);
        Assert.Contains("mm", text);
        Assert.Single(bridge.CreateFamilyInstanceCalls);
        Assert.Equal(1000, bridge.CreateFamilyInstanceCalls[0].X);
        Assert.Equal(2000, bridge.CreateFamilyInstanceCalls[0].Y);
    }

    [Fact]
    public async Task DuplicateElement_Success_WithoutFullAccess()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = GeometryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "duplicate_element");

        var text = await tool.Handler("""{"elementId":"42"}""");

        Assert.Equal("Duplicated 42 -> 303", text);
        Assert.Equal("42", bridge.DuplicateElementCalls.Single());
    }

    [Fact]
    public async Task CreateFloorByOutline_NoDocument_AsksToOpenModel()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = GeometryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_floor_by_outline");

        var text = await tool.Handler("""{"levelName":"Level 1","x1":0,"y1":0,"x2":4000,"y2":3000}""");

        Assert.Equal("Please open a model first.", text);
        Assert.Single(bridge.CreateFloorCalls);
        Assert.Equal(4000, bridge.CreateFloorCalls[0].X2);
        Assert.Null(new FakePluginContext(bridge).FullAccess);
    }

    [Fact]
    public async Task CreateFloorByOutline_Success_WithoutFullAccess_PassesMillimetres()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = GeometryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_floor_by_outline");

        var text = await tool.Handler("""{"levelName":"Level 1","x1":0,"y1":0,"x2":4000,"y2":3000}""");

        Assert.Contains("Created floor 401 on Level 1", text);
        Assert.Contains("(0,0)-(4000,3000)", text);
        Assert.Single(bridge.CreateFloorCalls);
        Assert.Equal(0, bridge.CreateFloorCalls[0].X1);
        Assert.Equal(4000, bridge.CreateFloorCalls[0].X2);
        Assert.Equal("Level 1", bridge.CreateFloorCalls[0].LevelName);
    }

    [Fact]
    public async Task CreateRoof_Success_WithoutFullAccess_PassesMillimetres()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = GeometryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_roof");

        var text = await tool.Handler("""{"levelName":"Level 1","x1":0,"y1":0,"x2":8000,"y2":6000}""");

        Assert.Equal("Created roof 501 on Level 1", text);
        Assert.Single(bridge.CreateRoofCalls);
        Assert.Equal(8000, bridge.CreateRoofCalls[0].X2);
        Assert.Equal(6000, bridge.CreateRoofCalls[0].Y2);
    }

    [Fact]
    public async Task CreateGridLevel_KindTypeFallback_AndDefaultElevation()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = GeometryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_grid_level");

        var grid = await tool.Handler("""{"type":"grid","name":"A","x1":0,"y1":0,"x2":10000,"y2":0}""");
        var level = await tool.Handler("""{"kind":"level","name":"Level 2","elevation":3600}""");

        Assert.Contains("Created grid 602 \"A\"", grid);
        Assert.Contains("Created level 601 \"Level 2\" elev 3600", level);
        Assert.Equal(2, bridge.CreateGridLevelCalls.Count);
        Assert.Equal("grid", bridge.CreateGridLevelCalls[0].Kind);
        Assert.Equal(10, bridge.CreateGridLevelCalls[0].Elevation);
        Assert.Equal("level", bridge.CreateGridLevelCalls[1].Kind);
        Assert.Equal(3600, bridge.CreateGridLevelCalls[1].Elevation);
    }

    [Fact]
    public async Task CreateFamilyInstanceArray_Success_WithoutFullAccess_PassesMillimetres()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = GeometryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_family_instance_array");

        var text = await tool.Handler("""{"symbolName":"Chair","startX":0,"startY":0,"endX":5000,"endY":0,"count":3}""");

        Assert.Contains("Created 3 Chair array", text);
        Assert.Single(bridge.CreateFamilyInstanceArrayCalls);
        Assert.Equal(0, bridge.CreateFamilyInstanceArrayCalls[0].StartX);
        Assert.Equal(5000, bridge.CreateFamilyInstanceArrayCalls[0].EndX);
        Assert.Equal(3, bridge.CreateFamilyInstanceArrayCalls[0].Count);
    }

    [Fact]
    public async Task RotateElements_Success_WithoutFullAccess_PassesMillimetres()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = GeometryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "rotate_elements");

        var text = await tool.Handler("""{"elementIds":["11","12"],"angleDegrees":90,"originX":1000,"originY":2000}""");

        Assert.Contains("Rotated 2 elements 90", text);
        Assert.Single(bridge.RotateElementsCalls);
        Assert.Equal(new[] { "11", "12" }, bridge.RotateElementsCalls[0].Ids);
        Assert.Equal(90, bridge.RotateElementsCalls[0].AngleDegrees);
        Assert.Equal(1000, bridge.RotateElementsCalls[0].OriginX);
        Assert.Equal(2000, bridge.RotateElementsCalls[0].OriginY);
        Assert.True(tool.RequiresWriteConfirm);
    }

    [Fact]
    public async Task MirrorElements_Success_WithoutFullAccess_PassesMillimetres()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = GeometryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "mirror_elements");

        var text = await tool.Handler("""{"elementIds":["21"],"x1":0,"y1":0,"x2":4000,"y2":0}""");

        Assert.Contains("Mirrored 1", text);
        Assert.Single(bridge.MirrorElementsCalls);
        Assert.Equal(4000, bridge.MirrorElementsCalls[0].X2);
        Assert.False(tool.RequiresWriteConfirm);
    }

    [Fact]
    public async Task SplitWall_NotImplemented_PassesMillimetres()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = GeometryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "split_wall");

        var text = await tool.Handler("""{"wallId":"55","x":1500,"y":0}""");

        Assert.Contains("Split not yet implemented for 55 at (1500,0)", text);
        Assert.Single(bridge.SplitWallCalls);
        Assert.Equal("55", bridge.SplitWallCalls[0].WallId);
        Assert.Equal(1500, bridge.SplitWallCalls[0].X);
    }

    [Fact]
    public async Task JoinGeometry_IsL2_WithoutFullAccess()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = GeometryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "join_geometry");

        var text = await tool.Handler("""{"elementId1":"1","elementId2":"2"}""");

        Assert.Equal("Joined 1 and 2", text);
        Assert.Equal(ToolTier.L2, tool.Tier);
        Assert.Single(bridge.JoinGeometryCalls);
        Assert.True(bridge.JoinGeometryCalls[0].Join);
    }

    [Fact]
    public async Task SaveDocument_NoDocument_AsksToOpenModel()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = DocumentTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "save_document");

        var text = await tool.Handler("{}");

        Assert.Equal("Please open a model first.", text);
        Assert.Equal(1, bridge.SaveDocumentCallCount);
        Assert.Null(new FakePluginContext(bridge).FullAccess);
        Assert.True(tool.RequiresWriteConfirm);
        Assert.Equal(ToolTier.L3, tool.Tier);
    }

    [Fact]
    public async Task SaveDocument_UnsavedPath_FailsWithoutFullAccess()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            ModelSummary = new("Untitled", "", false, 0, 0, 0, 0)
        };
        var tool = DocumentTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "save_document");

        var text = await tool.Handler("{}");

        Assert.Equal("Cannot save: document has no path (unsaved).", text);
        Assert.Equal(1, bridge.SaveDocumentCallCount);
    }

    [Fact]
    public async Task SaveDocument_Success_WithoutFullAccess_KeepsWorksharingText()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            ModelSummary = new("HQ", @"C:\models\hq.rvt", true, 12, 34, 5, 6),
            SyncInfo = new(@"\\central\hq.rvt", false, true, "alice", 2)
        };
        var tool = DocumentTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "save_document");

        var text = await tool.Handler("{}");

        Assert.Equal(@"Saved HQ to C:\models\hq.rvt (Workshared) central: \\central\hq.rvt", text);
        Assert.Equal(1, bridge.SaveDocumentCallCount);
    }

    [Fact]
    public async Task ReloadLinks_NoDocument_ReturnsBridgeMessage()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = DocumentTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "reload_links");

        var text = await tool.Handler("{}");

        Assert.Equal("Please open a model first.", text);
        Assert.Equal(1, bridge.ReloadLinksCallCount);
        Assert.Equal(ToolTier.L2, tool.Tier);
        Assert.False(tool.RequiresWriteConfirm);
        Assert.Null(new FakePluginContext(bridge).FullAccess);
    }

    [Fact]
    public async Task ReloadLinks_Success_WithoutFullAccess()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = DocumentTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "reload_links");

        var text = await tool.Handler("{}");

        Assert.Equal("Reloaded 0/0 RVT links", text);
        Assert.Equal(1, bridge.ReloadLinksCallCount);
    }

    [Fact]
    public async Task WorksetAssign_InvalidJson_DoesNotCallBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = DocumentTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "workset_assign");

        var text = await tool.Handler("not-json");

        Assert.StartsWith("Invalid args:", text);
        Assert.Empty(bridge.AssignWorksetCalls);
    }

    [Fact]
    public async Task WorksetAssign_NoDocument_ReturnsBridgeMessage()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = DocumentTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "workset_assign");

        var text = await tool.Handler("""{"elementIds":["1"],"worksetName":"Workset1"}""");

        Assert.Equal("Please open a model first.", text);
        Assert.Single(bridge.AssignWorksetCalls);
        Assert.Equal(ToolTier.L2, tool.Tier);
        Assert.False(tool.RequiresWriteConfirm);
    }

    [Fact]
    public async Task WorksetAssign_NotWorkshared_ReturnsBridgeMessage()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            SyncInfo = new("", false, false, "", 0)
        };
        var tool = DocumentTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "workset_assign");

        var text = await tool.Handler("""{"elementIds":["1"],"worksetName":"Workset1"}""");

        Assert.Equal("Document is not workshared.", text);
        Assert.Single(bridge.AssignWorksetCalls);
    }

    [Fact]
    public async Task WorksetAssign_Success_WithoutFullAccess()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            SyncInfo = new(@"\\central\hq.rvt", false, true, "alice", 2)
        };
        var ctx = new FakePluginContext(bridge);
        Assert.Null(ctx.FullAccess);
        var tool = DocumentTools.Create(ctx)
            .Single(t => t.Definition.Function.Name == "workset_assign");

        var text = await tool.Handler("""{"elementIds":["1","2"],"worksetName":"Shared"}""");

        Assert.Equal("Assigned 0/2 to workset \"Shared\"", text);
        Assert.Single(bridge.AssignWorksetCalls);
        Assert.Equal(new[] { "1", "2" }, bridge.AssignWorksetCalls[0].Ids);
        Assert.Equal("Shared", bridge.AssignWorksetCalls[0].WorksetName);
    }

    [Fact]
    public async Task SyncInfo_NoDocument_AsksToOpenModel()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = QueryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "sync_info");

        var text = await tool.Handler("{}");

        Assert.Equal("Please open a model first.", text);
        Assert.Equal(ToolTier.L1, tool.Tier);
        Assert.False(tool.RequiresWriteConfirm);
    }

    [Fact]
    public async Task SyncInfo_Success_WithoutFullAccess_KeepsCompatFormat()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            ModelSummary = new("HQ", @"C:\models\hq.rvt", true, 12, 34, 5, 6),
            SyncInfo = new(@"\\central\hq.rvt", false, true, "alice", 2)
        };
        var ctx = new FakePluginContext(bridge);
        Assert.Null(ctx.FullAccess);
        var tool = QueryTools.Create(ctx)
            .Single(t => t.Definition.Function.Name == "sync_info");

        var text = await tool.Handler("{}");

        Assert.Equal("Workshared: True\nCentral: \\\\central\\hq.rvt\nTitle: HQ\nPath: C:\\models\\hq.rvt", text);
    }

    [Fact]
    public async Task AuditWarningsFix_DefaultLimit_CallsBridge()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            WarningsRaw = new[]
            {
                new WarningItemDto("Overlapping walls", new[] { "11", "22", "33" })
            }
        };
        var tool = QueryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "audit_warnings_fix");

        var text = await tool.Handler("{}");

        Assert.Equal("Warnings 1 (top 10):\n1. Overlapping walls - Suggest: check 11,22\n", text);
        Assert.Equal(new[] { 10 }, bridge.GetWarningsRawCalls);
        Assert.Equal(ToolTier.L1, tool.Tier);
        Assert.False(tool.RequiresWriteConfirm);
    }

    [Fact]
    public async Task AuditWarningsFix_InvalidJson_StillCallsBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = QueryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "audit_warnings_fix");

        var text = await tool.Handler("not-json");

        Assert.Equal("No warnings.", text);
        Assert.Equal(new[] { 10 }, bridge.GetWarningsRawCalls);
    }

    [Fact]
    public async Task AuditWarningsFix_Empty_ReturnsNoWarnings()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = QueryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "audit_warnings_fix");

        var text = await tool.Handler("""{"limit":5}""");

        Assert.Equal("No warnings.", text);
        Assert.Equal(new[] { 5 }, bridge.GetWarningsRawCalls);
    }

    [Fact]
    public async Task QuantityTakeoff_NoDocument_AsksToOpenModel()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = QueryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "quantity_takeoff_by_category");

        var text = await tool.Handler("""{"category":"Walls"}""");

        Assert.Equal("Please open a model first.", text);
        Assert.Empty(bridge.QuantityTakeoffCalls);
        Assert.Equal(ToolTier.L1, tool.Tier);
        Assert.False(tool.RequiresWriteConfirm);
        Assert.Null(new FakePluginContext(bridge).FullAccess);
    }

    [Fact]
    public async Task QuantityTakeoff_UnknownCategory_ReturnsErrorWithoutFailedPrefix()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            NextQuantityTakeoff = new QuantityTakeoffDto(
                "Foo", 0, 0, 0, 0, Array.Empty<QuantityTakeoffRowDto>(), "Unknown category Foo")
        };
        var tool = QueryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "quantity_takeoff_by_category");

        var text = await tool.Handler("""{"category":"Foo"}""");

        Assert.Equal("Unknown category Foo", text);
        Assert.DoesNotContain("Failed:", text);
        Assert.Equal(new[] { "Foo" }, bridge.QuantityTakeoffCalls);
    }

    [Fact]
    public async Task QuantityTakeoff_InvalidJson_DoesNotCallBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = QueryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "quantity_takeoff_by_category");

        var text = await tool.Handler("{");

        Assert.StartsWith("Invalid args:", text);
        Assert.Empty(bridge.QuantityTakeoffCalls);
    }

    [Fact]
    public async Task QuantityTakeoff_WritesCsvUnderExportsRoot()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            NextQuantityTakeoff = new QuantityTakeoffDto(
                "Walls",
                1,
                10.0,
                2.0,
                3.0,
                new[] { new QuantityTakeoffRowDto("42", "W1", "Level 1", 10.0, 2.0, 3.0) })
        };
        var tool = QueryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "quantity_takeoff_by_category");

        var text = await tool.Handler("""{"category":"Walls"}""");

        Assert.Contains("Walls takeoff:", text);
        Assert.Contains("CSV:", text);
        Assert.Contains("Preview:", text);
        Assert.Contains("42", text);
        var csvLine = text.Split('\n').First(l => l.StartsWith("CSV: "));
        Assert.True(ExportPaths.IsUnderRoot(csvLine.Substring("CSV: ".Length)));
        Assert.Equal(new[] { "Walls" }, bridge.QuantityTakeoffCalls);
        Assert.Equal(ToolTier.L1, tool.Tier);
        Assert.False(tool.RequiresWriteConfirm);
    }

    [Fact]
    public async Task GetWorksets_NoDocument_AsksToOpenModel()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = QueryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "get_worksets");

        var text = await tool.Handler("{}");

        Assert.Equal("Please open a model first.", text);
        Assert.Equal(ToolTier.L1, tool.Tier);
        Assert.False(tool.RequiresWriteConfirm);
        Assert.Null(new FakePluginContext(bridge).FullAccess);
    }

    [Fact]
    public async Task GetWorksets_NotWorkshared_PrefixesHeader()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            Worksets = new WorksetListDto(false, new[] { new WorksetDto("1", "Workset1", true, true) })
        };
        var tool = QueryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "get_worksets");

        var text = await tool.Handler("{}");

        Assert.Equal("(Not workshared) Worksets (1):\n  1 \"Workset1\" Open:True Vis:True", text);
    }

    [Fact]
    public async Task GetPhases_FormatsIdAndName()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            Phases = new[] { new PhaseDto("10", "New Construction") }
        };
        var tool = QueryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "get_phases");

        var text = await tool.Handler("{}");

        Assert.Equal("Phases (1):\n  10 \"New Construction\"", text);
    }

    [Fact]
    public async Task GetClashByBb_InvalidJson_NeverCallsBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = QueryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "get_clash_by_bb");

        var text = await tool.Handler("{");

        Assert.StartsWith("Invalid args:", text);
        Assert.Empty(bridge.ClashCalls);
    }

    [Fact]
    public async Task GetClashByBb_UnknownCategories_PrintsErrorWithoutFailedPrefix()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            NextClashResult = new ClashResultDto("Foo", "Bar", 0, false, Array.Empty<ClashPairDto>(), "Unknown categories Foo/Bar")
        };
        var tool = QueryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "get_clash_by_bb");

        var text = await tool.Handler("""{"category1":"Foo","category2":"Bar"}""");

        Assert.Equal("Unknown categories Foo/Bar", text);
        Assert.Single(bridge.ClashCalls);
        Assert.Equal(("Foo", "Bar", 20), bridge.ClashCalls[0]);
    }

    [Fact]
    public async Task GetClashByBb_EmptyHits_KeepsSampledMessage()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = QueryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "get_clash_by_bb");

        var text = await tool.Handler("""{"category1":"Walls","category2":"Doors","limit":5}""");

        Assert.Equal("No clashes found between Walls and Doors (sampled 200 each).", text);
        Assert.Equal(5, bridge.ClashCalls[0].Limit);
    }

    [Fact]
    public async Task GetModelHealthScore_FormatsCompatString()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            ModelHealth = new ModelHealthDto(73.4, 10, 80, 40, true, "HQ")
        };
        var tool = QueryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "get_model_health_score");

        var text = await tool.Handler("{}");

        Assert.Equal("Health score: 73/100\nWarnings: 10 (score 80)\nViews: 40\nWorkshared: True\nTitle: HQ", text);
    }

    [Fact]
    public async Task GetRoomArea_FormatsMetricWithoutMergingGetRooms()
    {
        var room = new RoomDto("7", "101", "Office", "Level 1", 107.639, false);
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            RoomArea = new RoomAreaDto(1, 107.639, new[] { room })
        };
        var tools = QueryTools.Create(new FakePluginContext(bridge)).ToList();
        var area = tools.Single(t => t.Definition.Function.Name == "get_room_area");
        var rooms = tools.Single(t => t.Definition.Function.Name == "get_rooms");

        var text = await area.Handler("{}");

        Assert.StartsWith("Rooms: 1 Total area:", text);
        Assert.Contains("7 \"Office\" Level:Level 1 Area:", text);
        Assert.NotEqual("get_rooms", area.Definition.Function.Name);
        Assert.Equal("get_rooms", rooms.Definition.Function.Name);
    }

    [Fact]
    public async Task GetFamilyTypes_InvalidJson_StillCallsBridge()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            FamilyTypes = new FamilyTypeListDto(1, new[] { new FamilyTypeDto("3", "Door", "Single", "Doors") })
        };
        var tool = QueryTools.Create(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "get_family_types");

        var text = await tool.Handler("{");

        Assert.StartsWith("FamilySymbols (1):", text);
        Assert.Contains("3 \"Door:Single\" Cat:Doors", text);
        Assert.Single(bridge.FamilyTypeCalls);
        Assert.Null(bridge.FamilyTypeCalls[0]);
    }

    [Fact]
    public async Task LoadFamily_MissingPath_DoesNotCallBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = new LoadingAI.Plugins.ModelingTools.ModelingToolsPlugin()
            .CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "load_family");

        var text = await tool.Handler("{}");

        Assert.Equal("path is required.", text);
        Assert.Empty(bridge.LoadFamilyCalls);
    }

    [Fact]
    public async Task LoadFamily_MissingFile_DoesNotCallBridge()
    {
        var missing = Path.Combine(Path.GetTempPath(), "LoadingAI.Tests", "missing-family.rfa");
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = new LoadingAI.Plugins.ModelingTools.ModelingToolsPlugin()
            .CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "load_family");

        var text = await tool.Handler($$"""{"path":{{JsonEncode(missing)}}}""");

        Assert.Equal($"File not found: {missing}", text);
        Assert.Empty(bridge.LoadFamilyCalls);
    }

    [Fact]
    public async Task LoadFamily_NoDocument_AsksToOpenModel()
    {
        var path = WriteTempRfa();
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = new LoadingAI.Plugins.ModelingTools.ModelingToolsPlugin()
            .CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "load_family");

        var text = await tool.Handler($$"""{"path":{{JsonEncode(path)}}}""");

        Assert.Equal("Please open a model first.", text);
        Assert.Single(bridge.LoadFamilyCalls);
        Assert.Equal(path, bridge.LoadFamilyCalls[0]);
    }

    [Fact]
    public async Task LoadFamily_OpenDocument_ReturnsBridgeMessage()
    {
        var path = WriteTempRfa();
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = new LoadingAI.Plugins.ModelingTools.ModelingToolsPlugin()
            .CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "load_family");

        var text = await tool.Handler($$"""{"path":{{JsonEncode(path)}}}""");

        Assert.Contains("Loaded family", text);
        Assert.Single(bridge.LoadFamilyCalls);
        Assert.Equal(path, bridge.LoadFamilyCalls[0]);
    }

    [Fact]
    public async Task ChangeElementType_MissingId_DoesNotCallBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = new LoadingAI.Plugins.ModelingTools.ModelingToolsPlugin()
            .CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "change_element_type");

        var text = await tool.Handler("{}");

        Assert.Equal("elementId is required.", text);
        Assert.Empty(bridge.ChangeElementTypeCalls);
    }

    [Fact]
    public async Task ChangeElementType_NoDocument_AsksToOpenModel()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = new LoadingAI.Plugins.ModelingTools.ModelingToolsPlugin()
            .CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "change_element_type");

        var text = await tool.Handler("""{"elementId":"42","typeName":"Basic Wall"}""");

        Assert.Equal("Please open a model first.", text);
        Assert.Single(bridge.ChangeElementTypeCalls);
        Assert.Equal("42", bridge.ChangeElementTypeCalls[0].ElementId);
        Assert.Equal("Basic Wall", bridge.ChangeElementTypeCalls[0].TypeName);
    }

    [Fact]
    public async Task ChangeElementType_OpenDocument_ReturnsBridgeMessage()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = new LoadingAI.Plugins.ModelingTools.ModelingToolsPlugin()
            .CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "change_element_type");

        var text = await tool.Handler("""{"elementId":"42","typeName":"Basic Wall"}""");

        Assert.Contains("Changed element 42 type to \"Basic Wall\"", text);
        Assert.Single(bridge.ChangeElementTypeCalls);
    }

    [Fact]
    public async Task CreateTopography_FewerThanThreePoints_DoesNotCallBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = new LoadingAI.Plugins.ModelingTools.ModelingToolsPlugin()
            .CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_topography");

        var text = await tool.Handler("""{"points":[[0,0,0],[1000,0,0]]}""");

        Assert.Equal("Provide at least 3 points [x,y,z] in mm.", text);
        Assert.Empty(bridge.CreateTopographyCalls);
    }

    [Fact]
    public async Task CreateTopography_NoDocument_AsksToOpenModel()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = new LoadingAI.Plugins.ModelingTools.ModelingToolsPlugin()
            .CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_topography");

        var text = await tool.Handler("""{"points":[[0,0,0],[1000,0,0],[0,1000,0]],"levelName":"Level 1"}""");

        Assert.Equal("Please open a model first.", text);
        Assert.Single(bridge.CreateTopographyCalls);
        Assert.Equal(3, bridge.CreateTopographyCalls[0].Points.Count);
        Assert.Equal(0, bridge.CreateTopographyCalls[0].Points[0].X);
        Assert.Equal(1000, bridge.CreateTopographyCalls[0].Points[1].X);
        Assert.Equal("Level 1", bridge.CreateTopographyCalls[0].LevelName);
    }

    [Fact]
    public async Task CreateTopography_OpenDocument_PassesMmPointsWithoutConverting()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = new LoadingAI.Plugins.ModelingTools.ModelingToolsPlugin()
            .CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_topography");

        var text = await tool.Handler("""{"points":[[0,0,0],[1000,0,0],[0,1000,50]],"levelName":"Level 1"}""");

        Assert.Equal("Created toposolid 801 from 3 points on Level 1.", text);
        Assert.Single(bridge.CreateTopographyCalls);
        Assert.Equal(1000, bridge.CreateTopographyCalls[0].Points[1].X);
        Assert.Equal(50, bridge.CreateTopographyCalls[0].Points[2].Z);
    }

    [Fact]
    public async Task ExportPdf_FolderOutsideExportsRoot_DoesNotCallBridge()
    {
        var outside = Path.Combine(Path.GetTempPath(), "LoadingAI.Tests", "outside-root");
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ExportPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "export_pdf");

        var text = await tool.Handler($$"""{"folder":{{JsonEncode(outside)}}}""");

        Assert.Equal($"Failed: Folder must be under {ExportPaths.Root}", text);
        Assert.Empty(bridge.ExportPdfCalls);
    }

    [Fact]
    public async Task ExportPdf_NoFolder_UsesExportsRootAndCombines()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ExportPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "export_pdf");

        var text = await tool.Handler("{}");

        Assert.Equal($"Exported 1 sheet(s)/view(s) to PDF: {Path.Combine(ExportPaths.Root, "export.pdf")}", text);
        var call = Assert.Single(bridge.ExportPdfCalls);
        Assert.Equal(ExportPaths.Root, call.Directory);
        Assert.True(call.Combine);
        Assert.Null(call.FileName);
        Assert.Null(call.SheetIds);
    }

    [Fact]
    public async Task ExportPdf_ExplicitSheetsAndName_ForwardsArgs()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ExportPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "export_pdf");

        var text = await tool.Handler("""{"sheetIds":["101","102"],"name":"HQ Set","combine":false}""");

        Assert.Equal($"Exported 2 sheet(s)/view(s) to PDF: {ExportPaths.Root}", text);
        var call = Assert.Single(bridge.ExportPdfCalls);
        Assert.Equal(new[] { "101", "102" }, call.SheetIds);
        Assert.Equal("HQ Set", call.FileName);
        Assert.False(call.Combine);
    }

    [Fact]
    public async Task ExportPdf_NoDocument_AsksToOpenModel()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = ExportPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "export_pdf");

        var text = await tool.Handler("{}");

        Assert.Equal("Please open a model first.", text);
        Assert.Single(bridge.ExportPdfCalls);
    }

    [Fact]
    public async Task ExportDwg_OpenDocument_PassesViewsAndPrefix()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ExportPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "export_dwg");

        var text = await tool.Handler("""{"viewIds":["7"],"name":"Plan"}""");

        Assert.Equal($"Exported 1 view(s) to DWG in {ExportPaths.Root} (prefix \"Plan\")", text);
        var call = Assert.Single(bridge.ExportDwgCalls);
        Assert.Equal(new[] { "7" }, call.ViewIds);
        Assert.Equal("Plan", call.FileName);
    }

    [Fact]
    public async Task ExportDwg_NoDocument_AsksToOpenModel()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = ExportPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "export_dwg");

        var text = await tool.Handler("{}");

        Assert.Equal("Please open a model first.", text);
        Assert.Single(bridge.ExportDwgCalls);
    }

    [Fact]
    public async Task ExportIfc_OpenDocument_PassesVersionAndName()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ExportPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "export_ifc");

        var text = await tool.Handler("""{"version":"IFC4RV","name":"HQ"}""");

        Assert.Equal($"Exported IFC: {Path.Combine(ExportPaths.Root, "HQ.ifc")}", text);
        var call = Assert.Single(bridge.ExportIfcCalls);
        Assert.Equal(ExportPaths.Root, call.Directory);
        Assert.Equal("HQ", call.FileName);
        Assert.Equal("IFC4RV", call.Version);
    }

    [Fact]
    public async Task ExportIfc_NoDocument_AsksToOpenModel()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = ExportPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "export_ifc");

        var text = await tool.Handler("{}");

        Assert.Equal("Please open a model first.", text);
        Assert.Single(bridge.ExportIfcCalls);
    }

    [Fact]
    public async Task PurgeUnused_NoDocument_ReturnsBridgeError()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = ExportPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "purge_unused");

        var text = await tool.Handler("{}");

        Assert.Equal("Please open a model first.", text);
        Assert.Equal(new[] { true }, bridge.PurgeUnusedCalls);
    }

    [Fact]
    public async Task PurgeUnused_NothingUnused_SaysNothingToPurge()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            NextPurgeUnusedResult = new PurgeUnusedResultDto(true, 0, 0, 0, Array.Empty<string>())
        };
        var tool = ExportPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "purge_unused");

        var text = await tool.Handler("{}");

        Assert.Equal("Nothing to purge.", text);
    }

    [Fact]
    public async Task PurgeUnused_Preview_ListsSampleAndDeleteHint()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            NextPurgeUnusedResult = new PurgeUnusedResultDto(
                true, 3, 0, 0, new[] { "Walls/Basic Wall (101)" })
        };
        var tool = ExportPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "purge_unused");

        var text = await tool.Handler("{}");

        Assert.Equal(
            "Purge preview: 3 unused elements that can be deleted.\n"
            + "Sample:\nWalls/Basic Wall (101)\n"
            + "Run with preview:false to delete.",
            text);
        Assert.Equal(new[] { true }, bridge.PurgeUnusedCalls);
    }

    [Fact]
    public async Task PurgeUnused_Delete_ReportsCountAndPasses()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            NextPurgeUnusedResult = new PurgeUnusedResultDto(false, 5, 5, 2, Array.Empty<string>())
        };
        var tool = ExportPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "purge_unused");

        var text = await tool.Handler("""{"preview":false}""");

        Assert.Equal("Purged 5 unused element(s) in 2 pass(es).", text);
        Assert.Equal(new[] { false }, bridge.PurgeUnusedCalls);
    }

    [Fact]
    public async Task CreateSchedule_ExplicitName_SendsCategoryAndNameToken()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            NextCreateScheduleResult = new ViewRefDto(
                "7001", "Wall Schedule", "Created schedule 7001 \"Wall Schedule\" with 2 field(s).")
        };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_schedule");

        var text = await tool.Handler(
            """{"category":"Walls","name":"Wall Schedule","fields":["Type","Length"]}""");

        Assert.Equal("Created schedule 7001 \"Wall Schedule\" with 2 field(s).", text);
        var call = Assert.Single(bridge.CreateScheduleCalls);
        Assert.Equal("Walls:Wall Schedule", call.Name);
        Assert.Equal(new[] { "Type", "Length" }, call.Fields);
    }

    [Fact]
    public async Task CreateSchedule_CategoryOnly_SendsBareCategoryToken()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_schedule");

        var text = await tool.Handler("""{"category":"Walls"}""");

        Assert.Equal("Created schedule 5001 \"Walls Schedule\" with 0 field(s).", text);
        Assert.Equal("Walls", Assert.Single(bridge.CreateScheduleCalls).Name);
    }

    [Fact]
    public async Task CreateSchedule_MissingCategory_DoesNotCallBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_schedule");

        var text = await tool.Handler("{}");

        Assert.Equal("category is required.", text);
        Assert.Empty(bridge.CreateScheduleCalls);
    }

    [Fact]
    public async Task CreateSchedule_NoDocument_AsksToOpenModel()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_schedule");

        var text = await tool.Handler("""{"category":"Walls"}""");

        Assert.Equal("Please open a model first.", text);
        Assert.Single(bridge.CreateScheduleCalls);
    }

    [Fact]
    public async Task GetScheduleData_ForwardsIdNameAndRowLimit()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "get_schedule_data");

        var text = await tool.Handler("""{"scheduleId":"1001","name":"Door Schedule","rowLimit":2}""");

        Assert.Equal(
            new[] { "Door Schedule (1001) rows=3 cols=2 showing=2", "W-1\t200", "W-1\t200", "" },
            text.Replace("\r\n", "\n").Split('\n'));
        var call = Assert.Single(bridge.GetScheduleDataCalls);
        Assert.Equal("1001", call.ScheduleId);
        Assert.Equal("Door Schedule", call.ScheduleName);
        Assert.Equal(2, call.RowLimit);
    }

    [Fact]
    public async Task GetScheduleData_ZeroRowLimit_LetsBridgeApplyItsDefault()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "get_schedule_data");

        await tool.Handler("{}");

        Assert.Equal(0, Assert.Single(bridge.GetScheduleDataCalls).RowLimit);
    }

    [Fact]
    public async Task GetScheduleData_BridgeError_ReturnsErrorText()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            NextScheduleData = new ScheduleDataDto(
                string.Empty, string.Empty, 0, 0, 0, Array.Empty<IReadOnlyList<string>>(),
                "Schedule not found. Pass scheduleId or name.")
        };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "get_schedule_data");

        var text = await tool.Handler("{}");

        Assert.Equal("Schedule not found. Pass scheduleId or name.", text);
    }

    [Fact]
    public async Task GetScheduleData_NoDocument_AsksToOpenModel()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "get_schedule_data");

        var text = await tool.Handler("{}");

        Assert.Equal("Please open a model first.", text);
    }

    [Fact]
    public async Task CreateFloorPlan_LevelIdWinsOverLevelName()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_floor_plan");

        var text = await tool.Handler("""{"levelId":"42","levelName":"Level 9","name":"Rooftop"}""");

        Assert.Equal("Created floor plan 5002 \"Rooftop\" on 42", text);
        var call = Assert.Single(bridge.CreateFloorPlanCalls);
        Assert.Equal("42", call.LevelIdOrName);
        Assert.Equal("Rooftop", call.Name);
    }

    [Fact]
    public async Task CreateFloorPlan_UnsavedDocument_ReturnsBridgeError()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            NextCreateFloorPlanResult = new ViewRefDto(
                string.Empty, string.Empty, string.Empty,
                "Level not found. Pass levelId or levelName.")
        };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_floor_plan");

        var text = await tool.Handler("""{"levelName":"Nope"}""");

        Assert.Equal("Level not found. Pass levelId or levelName.", text);
    }

    [Fact]
    public async Task CreateFloorPlan_NoDocument_AsksToOpenModel()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_floor_plan");

        var text = await tool.Handler("{}");

        Assert.Equal("Please open a model first.", text);
        Assert.Single(bridge.CreateFloorPlanCalls);
    }

    [Fact]
    public async Task Create3DView_AlwaysRequestsIsometric()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_3d_view");

        var text = await tool.Handler("""{"name":"Overview"}""");

        Assert.Equal("Created 3D view 5003 \"Overview\"", text);
        var call = Assert.Single(bridge.Create3DViewCalls);
        Assert.Equal("Overview", call.Name);
        Assert.True(call.Isometric);
    }

    [Fact]
    public async Task Create3DView_NoDocument_AsksToOpenModel()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_3d_view");

        var text = await tool.Handler("{}");

        Assert.Equal("Please open a model first.", text);
        Assert.Single(bridge.Create3DViewCalls);
    }

    [Fact]
    public async Task CreateSectionView_PassesMmBoxWithoutConverting()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_section_view");

        var text = await tool.Handler("""{"x1":0,"y1":1000,"x2":5000,"y2":1000,"z":250,"name":"Section A"}""");

        Assert.Equal("Created section 5004 \"Section A\"", text);
        var call = Assert.Single(bridge.CreateSectionViewCalls);
        Assert.Equal("Section A", call.Name);
        Assert.Equal(0, call.Min.X);
        Assert.Equal(1000, call.Min.Y);
        Assert.Equal(250, call.Min.Z);
        Assert.Equal(5000, call.Max.X);
        Assert.Equal(250, call.Max.Z);
        Assert.Null(call.ViewDirection);
    }

    [Fact]
    public async Task CreateSectionView_ElementId_RejectedWithoutCallingBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_section_view");

        var text = await tool.Handler("""{"elementId":"123456"}""");

        Assert.Equal(
            "elementId sections need explicit coordinates: pass x1,y1,x2,y2 (mm) instead of 123456.",
            text);
        Assert.Empty(bridge.CreateSectionViewCalls);
    }

    [Fact]
    public async Task CreateSectionView_CustomHeight_RejectedWithoutCallingBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_section_view");

        var text = await tool.Handler("""{"x1":0,"y1":0,"x2":5000,"y2":0,"height":4000}""");

        Assert.Equal(
            "custom height/depth are not available; the section box is fixed at 3000 x 2500 mm.",
            text);
        Assert.Empty(bridge.CreateSectionViewCalls);
    }

    [Fact]
    public async Task CreateSectionView_DefaultHeightAndDepth_StillCallsBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_section_view");

        var text = await tool.Handler("""{"x1":0,"y1":0,"x2":5000,"y2":0,"height":3000,"depth":2500}""");

        Assert.Equal("Created section 5004 \"Section 1\"", text);
        Assert.Single(bridge.CreateSectionViewCalls);
    }

    [Fact]
    public async Task CreateSectionView_MissingCoordinates_DoesNotCallBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_section_view");

        var text = await tool.Handler("""{"x1":0,"y1":0}""");

        Assert.Equal("Pass x1,y1,x2,y2 (mm).", text);
        Assert.Empty(bridge.CreateSectionViewCalls);
    }

    [Fact]
    public async Task CreateSectionView_NoDocument_AsksToOpenModel()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "create_section_view");

        var text = await tool.Handler("""{"x1":0,"y1":0,"x2":5000,"y2":0}""");

        Assert.Equal("Please open a model first.", text);
        Assert.Single(bridge.CreateSectionViewCalls);
    }

    [Fact]
    public async Task PathOfTravel_PointShorthand_PassesOnePair()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "path_of_travel");

        var text = await tool.Handler("""{"viewId":"9","startX":0,"startY":0,"endX":3000,"endY":500}""");

        Assert.Equal("Created 1/1 path(s) in 9: 9001", text);
        var call = Assert.Single(bridge.CreatePathOfTravelCalls);
        Assert.Equal("9", call.ViewIdOrName);
        var pair = Assert.Single(call.Pairs);
        Assert.Equal(0, pair.Start.X);
        Assert.Equal(0, pair.Start.Y);
        Assert.Equal(3000, pair.End.X);
        Assert.Equal(500, pair.End.Y);
    }

    [Fact]
    public async Task PathOfTravel_StartsAndEndsArrays_PassesEveryPair()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "path_of_travel");

        var text = await tool.Handler(
            """{"starts":[[0,0],[1000,0]],"ends":[[0,4000],[1000,4000]]}""");

        Assert.Equal("Created 2/2 path(s) in Level 1: 9001, 9002", text);
        var call = Assert.Single(bridge.CreatePathOfTravelCalls);
        Assert.Equal(string.Empty, call.ViewIdOrName);
        Assert.Equal(2, call.Pairs.Count);
        Assert.Equal(4000, call.Pairs[1].End.Y);
    }

    [Fact]
    public async Task PathOfTravel_PairCountMismatch_DoesNotCallBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "path_of_travel");

        var text = await tool.Handler("""{"starts":[[0,0],[1000,0]],"ends":[[0,4000]]}""");

        Assert.Equal("starts and ends must have the same count.", text);
        Assert.Empty(bridge.CreatePathOfTravelCalls);
    }

    [Fact]
    public async Task PathOfTravel_NoInput_DoesNotCallBridge()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "path_of_travel");

        var text = await tool.Handler("{}");

        Assert.Equal("Pass startX/startY/endX/endY or starts/ends arrays.", text);
        Assert.Empty(bridge.CreatePathOfTravelCalls);
    }

    [Fact]
    public async Task PathOfTravel_PairCapSurfacesBridgeError()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            NextCreatePathOfTravelResult = new PathOfTravelResultDto(
                string.Empty, 6, 0, Array.Empty<string>(), Array.Empty<string>(),
                "Max 5 path pairs per call.")
        };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "path_of_travel");

        var text = await tool.Handler(
            """{"starts":[[0,0],[1,0],[2,0],[3,0],[4,0],[5,0]],"ends":[[0,9],[1,9],[2,9],[3,9],[4,9],[5,9]]}""");

        Assert.Equal("Max 5 path pairs per call.", text);
    }

    [Fact]
    public async Task PathOfTravel_PartialFailures_AppendFailedList()
    {
        var bridge = new FakeRevitBridge
        {
            HasActiveDocument = true,
            NextCreatePathOfTravelResult = new PathOfTravelResultDto(
                "Level 1", 2, 1, new[] { "9001" }, new[] { "#1:PathNotFound" })
        };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "path_of_travel");

        var text = await tool.Handler("""{"starts":[[0,0],[1000,0]],"ends":[[0,4000],[1000,4000]]}""");

        Assert.Equal("Created 1/2 path(s) in Level 1: 9001\nFailed: #1:PathNotFound", text);
    }

    [Fact]
    public async Task PathOfTravel_NoDocument_AsksToOpenModel()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = false };
        var tool = ViewPlugin().CreateTools(new FakePluginContext(bridge))
            .Single(t => t.Definition.Function.Name == "path_of_travel");

        var text = await tool.Handler("""{"startX":0,"startY":0,"endX":10,"endY":10}""");

        Assert.Equal("Please open a model first.", text);
        Assert.Single(bridge.CreatePathOfTravelCalls);
    }

    [Fact]
    public async Task ViewTools_DeclareFrozenNamesAndTiers()
    {
        var tools = ViewPlugin().CreateTools(new FakePluginContext(new FakeRevitBridge()))
            .Select(t => (t.Definition.Function.Name, t.Tier))
            .ToDictionary(x => x.Name, x => x.Tier);

        Assert.Equal(6, tools.Count);
        Assert.Equal(ToolTier.L3, tools["create_schedule"]);
        Assert.Equal(ToolTier.L1, tools["get_schedule_data"]);
        Assert.Equal(ToolTier.L3, tools["create_floor_plan"]);
        Assert.Equal(ToolTier.L3, tools["create_3d_view"]);
        Assert.Equal(ToolTier.L3, tools["create_section_view"]);
        Assert.Equal(ToolTier.L3, tools["path_of_travel"]);
    }

    private static LoadingAI.Plugins.ExportTools.ExportToolsPlugin ExportPlugin() => new();

    private static LoadingAI.Plugins.ViewTools.ViewToolsPlugin ViewPlugin() => new();

    private static string WriteTempRfa()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LoadingAI.Tests");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "family.rfa");
        File.WriteAllText(path, "rfa");
        return path;
    }

    private static string JsonEncode(string value)
        => System.Text.Json.JsonSerializer.Serialize(value);
}
