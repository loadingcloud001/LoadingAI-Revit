using System.IO;
using LoadingAI.Revit;

namespace LoadingAI.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IRevitBridge"/> for host tool-handler tests.
/// Mirrors adapter user-facing messages so handlers stay honest without Revit.
/// </summary>
internal sealed class FakeRevitBridge : IRevitBridge
{
    public bool HasActiveDocument { get; set; }

    public ModelSummaryDto? ModelSummary { get; set; }

    public SelectionSummaryDto Selection { get; set; } =
        new(0, Array.Empty<string>(), Array.Empty<string>());

    public CommandResultDto? NextIsolateResult { get; set; }

    public CommandResultDto? NextSetParameterResult { get; set; }

    public List<(IReadOnlyList<string>? Ids, string? Category, bool Isolate)> IsolateCalls { get; } = new();

    public List<(string ElementId, string ParamName, string Value)> SetParameterCalls { get; } = new();

    public Task<ModelSummaryDto?> GetModelSummaryAsync(CancellationToken ct = default)
        => Task.FromResult(HasActiveDocument ? ModelSummary : null);

    public Task<SelectionSummaryDto> GetSelectionSummaryAsync(CancellationToken ct = default)
        => Task.FromResult(HasActiveDocument
            ? Selection
            : new SelectionSummaryDto(0, Array.Empty<string>(), Array.Empty<string>()));

    public Task<ViewInfoDto?> GetActiveViewInfoAsync(CancellationToken ct = default)
        => Task.FromResult<ViewInfoDto?>(null);

    public Task<IReadOnlyList<ElementSummaryDto>> FindElementsAsync(
        string? family = null, string? category = null, string? typeName = null,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ElementSummaryDto>>(Array.Empty<ElementSummaryDto>());

    public Task<IReadOnlyList<WarningDto>> GetWarningsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WarningDto>>(GroupedWarnings);

    public IReadOnlyList<WarningItemDto> WarningsRaw { get; set; } = Array.Empty<WarningItemDto>();

    public List<int> GetWarningsRawCalls { get; } = new();

    public Task<IReadOnlyList<WarningItemDto>> GetWarningsRawAsync(int limit, CancellationToken ct = default)
    {
        GetWarningsRawCalls.Add(limit);
        if (!HasActiveDocument)
            return Task.FromResult<IReadOnlyList<WarningItemDto>>(Array.Empty<WarningItemDto>());
        return Task.FromResult(WarningsRaw);
    }

    public Task<IReadOnlyList<LevelDto>> GetLevelsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LevelDto>>(Array.Empty<LevelDto>());

    public Task<IReadOnlyList<GridDto>> GetGridsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<GridDto>>(Array.Empty<GridDto>());

    public Task<IReadOnlyList<QuantityDto>> GetQuantitiesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<QuantityDto>>(Array.Empty<QuantityDto>());

    public SyncInfoDto? SyncInfo { get; set; }

    public IReadOnlyList<WarningDto> GroupedWarnings { get; set; } = Array.Empty<WarningDto>();

    public WallMetricsDto? NextWallMetrics { get; set; }

    public ElementDetailsDto? NextElementDetails { get; set; }

    public FindResultDto? NextFind { get; set; }

    public Task<SyncInfoDto?> GetSyncInfoAsync(CancellationToken ct = default)
        => Task.FromResult(SyncInfo);

    public Task<WallMetricsDto?> GetWallMetricsAsync(string? levelName = null, int limit = 500, CancellationToken ct = default)
        => Task.FromResult(NextWallMetrics);

    public Task<VisibleElementsDto?> GetVisibleElementsInViewAsync(string? category = null, int limit = 100, CancellationToken ct = default)
        => Task.FromResult<VisibleElementsDto?>(null);

    public Task<ElementDetailsDto?> GetElementDetailsAsync(string elementId, CancellationToken ct = default)
        => Task.FromResult(NextElementDetails);

    public Task<FindResultDto> FindElementsFilteredAsync(
        string category,
        string? paramName = null, string? op = null, string? value = null,
        int limit = 100, int offset = 0,
        CancellationToken ct = default)
        => Task.FromResult(NextFind ?? new FindResultDto(0, offset, null, Array.Empty<ElementSummaryDto>()));

    public Task<IReadOnlyList<ElementQuantityDto>> GetElementQuantitiesAsync(
        IReadOnlyList<string>? elementIds = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ElementQuantityDto>>(Array.Empty<ElementQuantityDto>());

    public Task<ActiveViewDetailsDto?> GetActiveViewDetailsAsync(int limit = 200, CancellationToken ct = default)
        => Task.FromResult<ActiveViewDetailsDto?>(null);

    public Task<IReadOnlyList<RoomDto>> GetRoomsAsync(string? levelName = null, int limit = 200, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RoomDto>>(Array.Empty<RoomDto>());

    public WorksetListDto? Worksets { get; set; }
    public IReadOnlyList<PhaseDto> Phases { get; set; } = Array.Empty<PhaseDto>();
    public IReadOnlyList<DesignOptionDto> DesignOptions { get; set; } = Array.Empty<DesignOptionDto>();
    public SheetsViewsDto? SheetsViews { get; set; }
    public FamilyTypeListDto? FamilyTypes { get; set; }
    public MaterialListDto? Materials { get; set; }
    public RoomAreaDto? RoomArea { get; set; }
    public ClashResultDto? NextClashResult { get; set; }
    public ModelHealthDto? ModelHealth { get; set; }
    public List<(string Category1, string Category2, int Limit)> ClashCalls { get; } = new();
    public List<string?> FamilyTypeCalls { get; } = new();

    public Task<WorksetListDto?> GetWorksetsAsync(CancellationToken ct = default)
        => Task.FromResult(HasActiveDocument ? Worksets ?? new WorksetListDto(false, Array.Empty<WorksetDto>()) : null);

    public Task<IReadOnlyList<PhaseDto>> GetPhasesAsync(CancellationToken ct = default)
        => Task.FromResult(HasActiveDocument ? Phases : Array.Empty<PhaseDto>());

    public Task<IReadOnlyList<DesignOptionDto>> GetDesignOptionsAsync(CancellationToken ct = default)
        => Task.FromResult(HasActiveDocument ? DesignOptions : Array.Empty<DesignOptionDto>());

    public Task<SheetsViewsDto?> GetSheetsViewsAsync(CancellationToken ct = default)
        => Task.FromResult(HasActiveDocument ? SheetsViews ?? new SheetsViewsDto(0, 0, Array.Empty<SheetSummaryDto>(), Array.Empty<CategoryCountDto>()) : null);

    public Task<FamilyTypeListDto?> GetFamilyTypesAsync(string? category = null, CancellationToken ct = default)
    {
        if (!HasActiveDocument) return Task.FromResult<FamilyTypeListDto?>(null);
        FamilyTypeCalls.Add(category);
        return Task.FromResult<FamilyTypeListDto?>(FamilyTypes ?? new FamilyTypeListDto(0, Array.Empty<FamilyTypeDto>()));
    }

    public Task<MaterialListDto?> GetMaterialsAsync(CancellationToken ct = default)
        => Task.FromResult(HasActiveDocument ? Materials ?? new MaterialListDto(0, Array.Empty<MaterialDto>()) : null);

    public Task<RoomAreaDto?> GetRoomAreaAsync(string? levelName = null, CancellationToken ct = default)
        => Task.FromResult(HasActiveDocument ? RoomArea ?? new RoomAreaDto(0, 0, Array.Empty<RoomDto>()) : null);

    public Task<ClashResultDto?> GetClashByBoundingBoxAsync(
        string category1, string category2, int limit = 20, CancellationToken ct = default)
    {
        if (!HasActiveDocument) return Task.FromResult<ClashResultDto?>(null);
        ClashCalls.Add((category1, category2, limit));
        if (NextClashResult != null) return Task.FromResult<ClashResultDto?>(NextClashResult);
        return Task.FromResult<ClashResultDto?>(new ClashResultDto(category1, category2, 0, true, Array.Empty<ClashPairDto>()));
    }

    public Task<ModelHealthDto?> GetModelHealthScoreAsync(CancellationToken ct = default)
        => Task.FromResult(HasActiveDocument ? ModelHealth ?? new ModelHealthDto(0, 0, 100, 0, false, "") : null);

    public QuantityTakeoffDto? NextQuantityTakeoff { get; set; }

    public List<string> QuantityTakeoffCalls { get; } = new();

    public ScheduleDataDto? NextScheduleData { get; set; }

    public List<(string? ScheduleId, string? ScheduleName, int RowLimit)> GetScheduleDataCalls { get; } = new();

    public Task<QuantityTakeoffDto?> GetQuantityTakeoffAsync(string category, CancellationToken ct = default)
    {
        if (!HasActiveDocument)
            return Task.FromResult<QuantityTakeoffDto?>(null);
        QuantityTakeoffCalls.Add(category);
        if (NextQuantityTakeoff != null)
            return Task.FromResult<QuantityTakeoffDto?>(NextQuantityTakeoff);
        return Task.FromResult<QuantityTakeoffDto?>(
            new QuantityTakeoffDto(category, 0, 0, 0, 0, Array.Empty<QuantityTakeoffRowDto>()));
    }

    public Task<ScheduleDataDto?> GetScheduleDataAsync(
        string? scheduleId, string? scheduleName, int rowLimit, CancellationToken ct = default)
    {
        if (!HasActiveDocument)
            return Task.FromResult<ScheduleDataDto?>(new ScheduleDataDto(
                string.Empty, string.Empty, 0, 0, 0, Array.Empty<IReadOnlyList<string>>(),
                "Please open a model first."));
        GetScheduleDataCalls.Add((scheduleId, scheduleName, rowLimit));
        if (NextScheduleData != null)
            return Task.FromResult<ScheduleDataDto?>(NextScheduleData);
        var rows = new List<IReadOnlyList<string>>();
        var shown = Math.Min(3, rowLimit);
        for (var r = 0; r < shown; r++)
            rows.Add(new[] { "W-1", "200" });
        return Task.FromResult<ScheduleDataDto?>(new ScheduleDataDto(
            "1001", scheduleName ?? "Wall Schedule", 3, 2, shown, rows));
    }

    public Task<CommandResultDto> IsolateInViewAsync(
        IReadOnlyList<string>? elementIds = null,
        string? category = null,
        bool isolate = true,
        CancellationToken ct = default)
    {
        IsolateCalls.Add((elementIds, category, isolate));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if ((elementIds == null || elementIds.Count == 0) && string.IsNullOrWhiteSpace(category))
            return Task.FromResult(new CommandResultDto(false, "Provide elementIds or category."));
        if (NextIsolateResult != null)
            return Task.FromResult(NextIsolateResult);

        var affected = elementIds?.Count ?? 0;
        var msg = isolate
            ? $"Isolated {(category ?? affected + " elements")} in view 'Level 1' (temporary). Use reset_temporary_view to restore."
            : "Reset isolate in 'Level 1'.";
        return Task.FromResult(new CommandResultDto(true, msg, affected, "Level 1"));
    }

    public Task<CommandResultDto> HideInViewAsync(
        IReadOnlyList<string>? elementIds = null,
        string? category = null,
        CancellationToken ct = default)
        => Task.FromResult(FailIfClosed("Provide elementIds or category.", elementIds, category));

    public Task<CommandResultDto> ResetViewVisibilityAsync(CancellationToken ct = default)
        => Task.FromResult(HasActiveDocument
            ? new CommandResultDto(true, "Reset temporary view in 'Level 1'.", 0, "Level 1")
            : new CommandResultDto(false, "Please open a model first."));

    public int ResetViewOverridesCallCount { get; private set; }

    public CommandResultDto? NextResetViewOverridesResult { get; set; }

    public Task<CommandResultDto> ResetViewOverridesAsync(CancellationToken ct = default)
    {
        ResetViewOverridesCallCount++;
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        return Task.FromResult(NextResetViewOverridesResult
            ?? new CommandResultDto(true, "Restored 3 override(s) in 'Level 1'.", 3, "Level 1"));
    }

    public CommandResultDto? NextOverrideColorResult { get; set; }

    public CommandResultDto? NextColorByScheduleResult { get; set; }

    public CommandResultDto? NextColorByParameterResult { get; set; }

    public CommandResultDto? NextCreateSheetResult { get; set; }

    public CommandResultDto? NextPlaceViewOnSheetResult { get; set; }

    public CommandResultDto? NextDuplicateViewResult { get; set; }

    public CommandResultDto? NextSetViewTemplateResult { get; set; }

    public CommandResultDto? NextSetCropBoxResult { get; set; }

    public CommandResultDto? NextCreateDimensionResult { get; set; }

    public CommandResultDto? NextTagAllResult { get; set; }

    public List<(IReadOnlyList<string>? Ids, string? Category, string? Color, bool Reset)> OverrideColorCalls { get; } = new();

    public List<(string StartDate, int IntervalDays, IReadOnlyList<string>? Categories, string? Palette)> ColorByScheduleCalls { get; } = new();

    public List<(string ParamName, string? Category, IReadOnlyList<string>? Ids, string? Palette)> ColorByParameterCalls { get; } = new();

    public Task<CommandResultDto> OverrideColorInViewAsync(
        IReadOnlyList<string>? elementIds = null,
        string? category = null,
        string? color = null,
        bool reset = false,
        CancellationToken ct = default)
    {
        OverrideColorCalls.Add((elementIds, category, color, reset));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if ((elementIds == null || elementIds.Count == 0) && string.IsNullOrWhiteSpace(category))
            return Task.FromResult(new CommandResultDto(false, "Provide elementIds or category."));
        if (NextOverrideColorResult != null)
            return Task.FromResult(NextOverrideColorResult);

        var colorName = string.IsNullOrWhiteSpace(color) ? "red" : color;
        if (!string.IsNullOrWhiteSpace(category) && (elementIds == null || elementIds.Count == 0))
        {
            var catMsg = reset
                ? $"Reset color for {category} in 'Level 1'."
                : $"Set {colorName} override for {category} in 'Level 1'.";
            return Task.FromResult(new CommandResultDto(true, catMsg, 0, "Level 1"));
        }

        var n = elementIds?.Count ?? 0;
        var msg = reset
            ? $"Reset color for {n} element(s) in 'Level 1'."
            : $"Set {colorName} override for {n} element(s) in 'Level 1'.";
        return Task.FromResult(new CommandResultDto(true, msg, n, "Level 1"));
    }

    public Task<CommandResultDto> ColorByConstructionScheduleAsync(
        string startDate,
        int intervalDays = 14,
        IReadOnlyList<string>? categories = null,
        string? palette = null,
        CancellationToken ct = default)
    {
        ColorByScheduleCalls.Add((startDate, intervalDays, categories, palette));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextColorByScheduleResult != null)
            return Task.FromResult(NextColorByScheduleResult);

        var paletteName = string.IsNullOrWhiteSpace(palette) ? "rainbow" : palette;
        var catCount = categories == null || categories.Count == 0
            ? 1
            : (categories.Count == 1 && string.Equals(categories[0], "all", StringComparison.OrdinalIgnoreCase) ? 10 : categories.Count);
        var msg = $"Colored 0 element(s) in 'Level 1' by construction schedule bottom-up {startDate} (+{intervalDays}d) across 0 levels, {catCount} categories. Palette {paletteName}.";
        return Task.FromResult(new CommandResultDto(true, msg, 0, "Level 1"));
    }

    public Task<CommandResultDto> ColorByParameterAsync(
        string paramName,
        string? category = null,
        IReadOnlyList<string>? elementIds = null,
        string? palette = null,
        CancellationToken ct = default)
    {
        ColorByParameterCalls.Add((paramName, category, elementIds, palette));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (string.IsNullOrWhiteSpace(paramName))
            return Task.FromResult(new CommandResultDto(false, "Provide paramName."));
        if (NextColorByParameterResult != null)
            return Task.FromResult(NextColorByParameterResult);

        var paletteName = string.IsNullOrWhiteSpace(palette) ? "rainbow" : palette;
        var n = elementIds?.Count ?? 0;
        var msg = $"Colored {n} element(s) in 'Level 1' by '{paramName}' (0 values). Palette {paletteName}.";
        return Task.FromResult(new CommandResultDto(true, msg, n, "Level 1"));
    }

    public List<(string SheetNumber, string SheetName, string? TitleBlockName)> CreateSheetCalls { get; } = new();

    public List<(string ViewId, string SheetId, double XMm, double YMm)> PlaceViewOnSheetCalls { get; } = new();

    public List<(string ViewId, bool WithDetailing)> DuplicateViewCalls { get; } = new();

    public List<(string ViewId, string? TemplateId)> SetViewTemplateCalls { get; } = new();

    public List<(string ViewId, bool Enabled)> SetCropBoxCalls { get; } = new();

    public List<(string WallId1, string WallId2, string? ViewId)> CreateDimensionCalls { get; } = new();

    public List<string> TagAllCalls { get; } = new();

    public Task<CommandResultDto> CreateSheetAsync(
        string sheetNumber, string sheetName, string? titleBlockName = null,
        CancellationToken ct = default)
    {
        CreateSheetCalls.Add((sheetNumber, sheetName, titleBlockName));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextCreateSheetResult != null)
            return Task.FromResult(NextCreateSheetResult);
        return Task.FromResult(new CommandResultDto(true, $"Created sheet 1 {sheetNumber} \"{sheetName}\"", 1, ElementIds: new[] { "1" }));
    }

    public Task<CommandResultDto> PlaceViewOnSheetAsync(
        string viewId, string sheetId, double xMm = 0, double yMm = 0,
        CancellationToken ct = default)
    {
        PlaceViewOnSheetCalls.Add((viewId, sheetId, xMm, yMm));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextPlaceViewOnSheetResult != null)
            return Task.FromResult(NextPlaceViewOnSheetResult);
        var x = Metric.MmToFt(xMm);
        var y = Metric.MmToFt(yMm);
        return Task.FromResult(new CommandResultDto(true, $"Placed view {viewId} on sheet {sheetId} at ({x},{y}) viewport 9", 1, ElementIds: new[] { "9" }));
    }

    public Task<CommandResultDto> DuplicateViewAsync(
        string viewId, bool withDetailing = false,
        CancellationToken ct = default)
    {
        DuplicateViewCalls.Add((viewId, withDetailing));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextDuplicateViewResult != null)
            return Task.FromResult(NextDuplicateViewResult);
        return Task.FromResult(new CommandResultDto(true, $"Duplicated {viewId} -> 2 {(withDetailing ? "with detailing" : "")}", 1, ElementIds: new[] { "2" }));
    }

    public Task<CommandResultDto> SetViewTemplateAsync(
        string viewId, string? templateId = null,
        CancellationToken ct = default)
    {
        SetViewTemplateCalls.Add((viewId, templateId));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextSetViewTemplateResult != null)
            return Task.FromResult(NextSetViewTemplateResult);
        var msg = string.IsNullOrWhiteSpace(templateId)
            ? $"Cleared template for {viewId}"
            : $"Set template {templateId} for {viewId}";
        return Task.FromResult(new CommandResultDto(true, msg, 1));
    }

    public Task<CommandResultDto> SetCropBoxAsync(
        string viewId, bool enabled,
        CancellationToken ct = default)
    {
        SetCropBoxCalls.Add((viewId, enabled));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextSetCropBoxResult != null)
            return Task.FromResult(NextSetCropBoxResult);
        return Task.FromResult(new CommandResultDto(true, $"Set crop {(enabled ? "on" : "off")} for {viewId}", 1));
    }

    public Task<CommandResultDto> CreateDimensionBetweenWallsAsync(
        string wallId1, string wallId2, string? viewId = null,
        CancellationToken ct = default)
    {
        CreateDimensionCalls.Add((wallId1, wallId2, viewId));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextCreateDimensionResult != null)
            return Task.FromResult(NextCreateDimensionResult);
        return Task.FromResult(new CommandResultDto(true, $"Created dimension 3 between {wallId1} and {wallId2} in Level 1", 1, ElementIds: new[] { "3" }));
    }

    public Task<CommandResultDto> TagAllInActiveViewAsync(
        string category,
        CancellationToken ct = default)
    {
        TagAllCalls.Add(category);
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextTagAllResult != null)
            return Task.FromResult(NextTagAllResult);
        return Task.FromResult(new CommandResultDto(true, $"Tagged 0/0 {category} in Level 1", 0));
    }

    public Task<CommandResultDto> SetParameterAsync(
        string elementId, string paramName, string value,
        CancellationToken ct = default)
        => SetParametersAsync(new[] { elementId }, paramName, value, ct);

    public Task<CommandResultDto> SetParametersAsync(
        IReadOnlyList<string> elementIds, string paramName, string value,
        CancellationToken ct = default)
    {
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (elementIds == null || elementIds.Count == 0)
            return Task.FromResult(new CommandResultDto(false, "Please select elements first or provide elementIds."));

        foreach (var id in elementIds)
            SetParameterCalls.Add((id, paramName, value));

        if (NextSetParameterResult != null)
            return Task.FromResult(NextSetParameterResult);

        return Task.FromResult(new CommandResultDto(true, $"Updated {elementIds.Count} element(s).", elementIds.Count));
    }

    public CommandResultDto? NextRenameElementsResult { get; set; }

    public CommandResultDto? NextApplyConstructionDatesResult { get; set; }

    public List<(IReadOnlyList<string> Ids, IReadOnlyList<string>? NewNames, string? Pattern)> RenameElementsCalls { get; } = new();

    public List<(string StartDate, int IntervalDays, string? TargetParam, IReadOnlyList<string>? Categories)> ApplyConstructionDatesCalls { get; } = new();

    public Task<CommandResultDto> RenameElementsAsync(
        IReadOnlyList<string> elementIds,
        IReadOnlyList<string>? newNames = null,
        string? newNamePattern = null,
        CancellationToken ct = default)
    {
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));

        RenameElementsCalls.Add((elementIds, newNames, newNamePattern));
        if (NextRenameElementsResult != null)
            return Task.FromResult(NextRenameElementsResult);

        var n = elementIds?.Count ?? 0;
        return Task.FromResult(new CommandResultDto(true, $"Renamed {n}/{n} element(s).", n));
    }

    public Task<CommandResultDto> ApplyConstructionDatesAsync(
        string startDate,
        int intervalDays = 14,
        string? targetParam = null,
        IReadOnlyList<string>? categories = null,
        CancellationToken ct = default)
    {
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));

        ApplyConstructionDatesCalls.Add((startDate, intervalDays, targetParam, categories));
        if (NextApplyConstructionDatesResult != null)
            return Task.FromResult(NextApplyConstructionDatesResult);

        var paramName = string.IsNullOrWhiteSpace(targetParam) ? "Construction Date" : targetParam;
        var interval = intervalDays < 1 ? 1 : (intervalDays > 365 ? 365 : intervalDays);
        var parsed = startDate ?? "";
        if (DateTime.TryParseExact(parsed.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt)
            || DateTime.TryParseExact(parsed.Trim(), "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out dt)
            || DateTime.TryParse(parsed, out dt))
            parsed = dt.ToString("yyyy-MM-dd");
        var msg = $"Applied {paramName} bottom-up {parsed} (+{interval}d) to 0 element(s) across 0 categories, 0 levels.";
        return Task.FromResult(new CommandResultDto(true, msg, 0));
    }

    public ParamExportDto? NextExportParams { get; set; }

    public CommandResultDto? NextImportParams { get; set; }

    public CommandResultDto? NextCopyParams { get; set; }

    public CommandResultDto? NextReplaceText { get; set; }

    public CommandResultDto? NextNumberElements { get; set; }

    public List<(string Category, IReadOnlyList<string> Columns, int Limit)> ExportParamsCalls { get; } = new();

    public List<(string ParamName, IReadOnlyList<ParamImportRowDto> Rows, string SourcePath)> ImportParamsCalls { get; } = new();

    public List<(string Category, string SourceParam, string TargetParam, int Limit)> CopyParamsCalls { get; } = new();

    public List<(string Category, string ParamName, string Pattern, string Replacement, int Limit)> ReplaceTextCalls { get; } = new();

    public List<(string? Category, string Prefix, int Start, int Pad)> NumberElementsCalls { get; } = new();

    public Task<ParamExportDto?> ExportParamsAsync(
        string category,
        IReadOnlyList<string> columns,
        int limit = 200,
        CancellationToken ct = default)
    {
        if (!HasActiveDocument)
            return Task.FromResult<ParamExportDto?>(null);

        ExportParamsCalls.Add((category, columns, limit));
        if (NextExportParams != null)
            return Task.FromResult<ParamExportDto?>(NextExportParams);

        var cols = columns == null || columns.Count == 0
            ? new[] { "Comments", "Mark" }
            : columns.ToArray();
        return Task.FromResult<ParamExportDto?>(new ParamExportDto(
            category,
            cols,
            new[] { new ParamExportRowDto("1", cols.Select(_ => "").ToArray()) }));
    }

    public Task<CommandResultDto> ImportParamsAsync(
        string paramName,
        IReadOnlyList<ParamImportRowDto> rows,
        string sourcePath,
        CancellationToken ct = default)
    {
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));

        ImportParamsCalls.Add((paramName, rows, sourcePath));
        if (NextImportParams != null)
            return Task.FromResult(NextImportParams);

        var n = rows?.Count ?? 0;
        return Task.FromResult(new CommandResultDto(true, $"Imported {n}/{n} (0 skipped) {paramName} from {sourcePath}", n));
    }

    public Task<CommandResultDto> CopyParamsAsync(
        string category,
        string sourceParam,
        string targetParam,
        int limit = 200,
        CancellationToken ct = default)
    {
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));

        CopyParamsCalls.Add((category, sourceParam, targetParam, limit));
        if (NextCopyParams != null)
            return Task.FromResult(NextCopyParams);

        return Task.FromResult(new CommandResultDto(true, $"Copied 0/0 {sourceParam}->{targetParam} for {category}", 0));
    }

    public Task<CommandResultDto> ReplaceTextInParamAsync(
        string category,
        string paramName,
        string pattern,
        string replacement,
        int limit = 200,
        CancellationToken ct = default)
    {
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));

        ReplaceTextCalls.Add((category, paramName, pattern, replacement, limit));
        if (NextReplaceText != null)
            return Task.FromResult(NextReplaceText);

        return Task.FromResult(new CommandResultDto(true, $"Replaced 0/0 {paramName} /{pattern}/ -> {replacement}", 0));
    }

    public Task<CommandResultDto> NumberElementsAsync(
        string? category,
        string prefix,
        int start,
        int pad,
        CancellationToken ct = default)
    {
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));

        NumberElementsCalls.Add((category, prefix, start, pad));
        if (NextNumberElements != null)
            return Task.FromResult(NextNumberElements);

        var pref = prefix ?? "";
        return Task.FromResult(new CommandResultDto(true, $"Numbered 0/0 Mark from {pref}{start.ToString().PadLeft(pad, '0')}", 0));
    }

    public CommandResultDto? NextCreateWallResult { get; set; }

    public CommandResultDto? NextDeleteElementsResult { get; set; }

    public CommandResultDto? NextMoveElementsResult { get; set; }

    public List<IReadOnlyList<string>> DeleteElementsCalls { get; } = new();

    public List<(IReadOnlyList<string> Ids, double Dx, double Dy, double Dz)> MoveElementsCalls { get; } = new();

    public CommandResultDto? NextCreateFamilyInstanceResult { get; set; }

    public CommandResultDto? NextDuplicateElementResult { get; set; }

    public CommandResultDto? NextCreateFloorResult { get; set; }

    public CommandResultDto? NextCreateRoofResult { get; set; }

    public CommandResultDto? NextCreateGridLevelResult { get; set; }

    public CommandResultDto? NextCreateFamilyInstanceArrayResult { get; set; }

    public CommandResultDto? NextRotateElementsResult { get; set; }

    public CommandResultDto? NextMirrorElementsResult { get; set; }

    public CommandResultDto? NextSplitWallResult { get; set; }

    public CommandResultDto? NextJoinGeometryResult { get; set; }

    public CommandResultDto? NextSaveDocumentResult { get; set; }

    public int SaveDocumentCallCount { get; private set; }

    public CommandResultDto? NextReloadLinksResult { get; set; }

    public int ReloadLinksCallCount { get; private set; }

    public CommandResultDto? NextAssignWorksetResult { get; set; }

    public List<(IReadOnlyList<string> Ids, string WorksetName)> AssignWorksetCalls { get; } = new();

    public List<(string LevelName, double X1, double Y1, double X2, double Y2, string? WallTypeName)> CreateWallCalls { get; } = new();

    /// <summary>The <c>heightMm</c> of each <c>CreateWallAsync</c> call, parallel to <see cref="CreateWallCalls"/>.</summary>
    public List<double?> CreateWallHeights { get; } = new();

    public List<(string SymbolName, double X, double Y, string? FamilyName, string? LevelName, double Z)> CreateFamilyInstanceCalls { get; } = new();

    public List<string> DuplicateElementCalls { get; } = new();

    public List<(string LevelName, double X1, double Y1, double X2, double Y2, string? FloorTypeName)> CreateFloorCalls { get; } = new();

    public List<(string LevelName, double X1, double Y1, double X2, double Y2)> CreateRoofCalls { get; } = new();

    public List<(string Kind, string Name, double X1, double Y1, double X2, double Y2, double Elevation)> CreateGridLevelCalls { get; } = new();

    public List<(string SymbolName, double StartX, double StartY, double EndX, double EndY, int Count, string? LevelName)> CreateFamilyInstanceArrayCalls { get; } = new();

    public List<(IReadOnlyList<string> Ids, double AngleDegrees, double? OriginX, double? OriginY)> RotateElementsCalls { get; } = new();

    public List<(IReadOnlyList<string> Ids, double X1, double Y1, double X2, double Y2)> MirrorElementsCalls { get; } = new();

    public List<(string WallId, double X, double Y)> SplitWallCalls { get; } = new();

    public List<(string Id1, string Id2, bool Join)> JoinGeometryCalls { get; } = new();

    public CommandResultDto? NextChangeElementTypeResult { get; set; }

    public List<(string ElementId, string? TypeName, string? TypeId)> ChangeElementTypeCalls { get; } = new();

    public CommandResultDto? NextCreateTopographyResult { get; set; }

    public List<(IReadOnlyList<PointMmDto> Points, string? LevelName, string? LevelId)> CreateTopographyCalls { get; } = new();

    public CommandResultDto? NextLoadFamilyResult { get; set; }

    public List<string> LoadFamilyCalls { get; } = new();

    public CommandResultDto? NextExportPdfResult { get; set; }

    public List<(string Directory, IReadOnlyList<string>? SheetIds, string? FileName, bool Combine)> ExportPdfCalls { get; } = new();

    public CommandResultDto? NextExportDwgResult { get; set; }

    public List<(string Directory, IReadOnlyList<string>? ViewIds, string? FileName)> ExportDwgCalls { get; } = new();

    public CommandResultDto? NextExportIfcResult { get; set; }

    public List<(string Directory, string? FileName, string? Version)> ExportIfcCalls { get; } = new();

    public PurgeUnusedResultDto? NextPurgeUnusedResult { get; set; }

    public List<bool> PurgeUnusedCalls { get; } = new();

    public ViewRefDto? NextCreateScheduleResult { get; set; }

    public List<(string Name, IReadOnlyList<string> Fields)> CreateScheduleCalls { get; } = new();

    public ViewRefDto? NextCreateFloorPlanResult { get; set; }

    public List<(string LevelIdOrName, string Name)> CreateFloorPlanCalls { get; } = new();

    public ViewRefDto? NextCreate3DViewResult { get; set; }

    public List<(string Name, bool Isometric)> Create3DViewCalls { get; } = new();

    public ViewRefDto? NextCreateSectionViewResult { get; set; }

    public List<(string Name, PointMmDto Min, PointMmDto Max, string? ViewDirection)> CreateSectionViewCalls { get; } = new();

    public PathOfTravelResultDto? NextCreatePathOfTravelResult { get; set; }

    public List<(string ViewIdOrName, IReadOnlyList<PathPairDto> Pairs)> CreatePathOfTravelCalls { get; } = new();

    public ElementIdListDto? NextCollectHostsResult { get; set; }

    public List<(string Scope, IReadOnlyList<string>? Categories, int Limit, int Offset)> CollectHostsCalls { get; } = new();

    public SplitResultDto? NextSplitBeamsResult { get; set; }

    public List<SplitRequestDto> SplitBeamsCalls { get; } = new();

    public SplitResultDto? NextSplitSecondaryByMainResult { get; set; }

    public List<SplitRequestDto> SplitSecondaryByMainCalls { get; } = new();

    public SplitResultDto? NextSplitFloorsResult { get; set; }

    public List<SplitRequestDto> SplitFloorsCalls { get; } = new();

    public SplitResultDto? NextSplitByLevelsResult { get; set; }

    public List<SplitRequestDto> SplitByLevelsCalls { get; } = new();

    public SplitResultDto? NextSplitByGridsResult { get; set; }

    public List<SplitRequestDto> SplitByGridsCalls { get; } = new();

    public FormworkSetupResultDto? NextFormworkSetupResult { get; set; }

    public List<FormworkSetupRequestDto> FormworkSetupCalls { get; } = new();

    public FormworkPanelsResultDto? NextFormworkPanelsResult { get; set; }

    public List<FormworkPanelsRequestDto> FormworkPanelsCalls { get; } = new();

    public CleanupResultDto? NextCleanupFormworkResult { get; set; }

    public List<CleanupRequestDto> CleanupFormworkCalls { get; } = new();

    public CopyParamsResultDto? NextCopyFormworkParentParamsResult { get; set; }

    public List<CopyParamsRequestDto> CopyFormworkParentParamsCalls { get; } = new();

    public List<OpeningClashRequestDto> OpeningProbeCalls { get; } = new();

    public List<OpeningClashRequestDto> PlaceOpeningsCalls { get; } = new();

    public List<OpeningCutRequestDto> CutHostsByOpeningsCalls { get; } = new();

    public OpeningPlanResultDto? NextOpeningProbeResult { get; set; }

    public OpeningPlaceResultDto? NextPlaceOpeningsResult { get; set; }

    public OpeningCutResultDto? NextCutHostsResult { get; set; }

    public Task<CommandResultDto> DeleteElementsAsync(
        IReadOnlyList<string> elementIds, CancellationToken ct = default)
    {
        DeleteElementsCalls.Add(elementIds);
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (elementIds == null || elementIds.Count == 0)
            return Task.FromResult(new CommandResultDto(false, "Please select elements first or provide elementIds."));
        return Task.FromResult(NextDeleteElementsResult
            ?? new CommandResultDto(true, $"Deleted {elementIds.Count} element(s).", elementIds.Count));
    }

    public Task<CommandResultDto> MoveElementsAsync(
        IReadOnlyList<string> elementIds, double dxMm, double dyMm, double dzMm,
        CancellationToken ct = default)
    {
        MoveElementsCalls.Add((elementIds, dxMm, dyMm, dzMm));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (elementIds == null || elementIds.Count == 0)
            return Task.FromResult(new CommandResultDto(false, "Please select elements first or provide elementIds."));
        return Task.FromResult(NextMoveElementsResult
            ?? new CommandResultDto(true, $"Moved {elementIds.Count} element(s).", elementIds.Count));
    }

    public Task<CommandResultDto> ChangeElementTypeAsync(
        string elementId, string? typeName = null, string? typeId = null,
        CancellationToken ct = default)
    {
        ChangeElementTypeCalls.Add((elementId, typeName, typeId));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextChangeElementTypeResult != null)
            return Task.FromResult(NextChangeElementTypeResult);
        var label = !string.IsNullOrWhiteSpace(typeName) ? typeName : typeId ?? "";
        return Task.FromResult(new CommandResultDto(true, $"Changed element {elementId} type to \"{label}\" ({typeId ?? "1"})", 1));
    }

    public Task<CommandResultDto> CreateWallAsync(
        string levelName,
        double x1, double y1, double x2, double y2,
        string? wallTypeName = null,
        double? heightMm = null,
        CancellationToken ct = default)
    {
        CreateWallCalls.Add((levelName, x1, y1, x2, y2, wallTypeName));
        CreateWallHeights.Add(heightMm);
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextCreateWallResult != null)
            return Task.FromResult(NextCreateWallResult);
        return Task.FromResult(new CommandResultDto(
            true,
            $"Created wall 101 on {levelName} from ({x1:F0},{y1:F0}) to ({x2:F0},{y2:F0}) mm type 1.",
            1,
            ElementIds: new[] { "101" }));
    }

    public Task<CommandResultDto> CreateFamilyInstanceAsync(
        string symbolName,
        double x, double y,
        string? familyName = null,
        string? levelName = null,
        double z = 0,
        CancellationToken ct = default)
    {
        CreateFamilyInstanceCalls.Add((symbolName, x, y, familyName, levelName, z));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextCreateFamilyInstanceResult != null)
            return Task.FromResult(NextCreateFamilyInstanceResult);
        var lvl = string.IsNullOrWhiteSpace(levelName) ? "Level 1" : levelName;
        return Task.FromResult(new CommandResultDto(
            true,
            $"Created instance 202 '{symbolName}' at ({x:F0},{y:F0},{z:F0}) mm on {lvl}.",
            1,
            ElementIds: new[] { "202" }));
    }

    public Task<CommandResultDto> DuplicateElementAsync(
        string elementId,
        CancellationToken ct = default)
    {
        DuplicateElementCalls.Add(elementId);
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (string.IsNullOrWhiteSpace(elementId))
            return Task.FromResult(new CommandResultDto(false, "Provide elementId."));
        if (NextDuplicateElementResult != null)
            return Task.FromResult(NextDuplicateElementResult);
        return Task.FromResult(new CommandResultDto(
            true,
            $"Duplicated {elementId} -> 303",
            1,
            ElementIds: new[] { "303" }));
    }

    public Task<CommandResultDto> CreateFloorByOutlineAsync(
        string levelName,
        double x1, double y1, double x2, double y2,
        string? floorTypeName = null,
        CancellationToken ct = default)
    {
        CreateFloorCalls.Add((levelName, x1, y1, x2, y2, floorTypeName));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextCreateFloorResult != null)
            return Task.FromResult(NextCreateFloorResult);
        return Task.FromResult(new CommandResultDto(
            true,
            $"Created floor 401 on {levelName} rect ({x1},{y1})-({x2},{y2})",
            1,
            ElementIds: new[] { "401" }));
    }

    public Task<CommandResultDto> CreateRoofAsync(
        string levelName,
        double x1, double y1, double x2, double y2,
        CancellationToken ct = default)
    {
        CreateRoofCalls.Add((levelName, x1, y1, x2, y2));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextCreateRoofResult != null)
            return Task.FromResult(NextCreateRoofResult);
        return Task.FromResult(new CommandResultDto(true, $"Created roof 501 on {levelName}", 1, ElementIds: new[] { "501" }));
    }

    public Task<CommandResultDto> CreateGridLevelAsync(
        string kind,
        string name,
        double x1, double y1, double x2, double y2,
        double elevation,
        CancellationToken ct = default)
    {
        CreateGridLevelCalls.Add((kind, name, x1, y1, x2, y2, elevation));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextCreateGridLevelResult != null)
            return Task.FromResult(NextCreateGridLevelResult);
        if (kind.Equals("level", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new CommandResultDto(true, $"Created level 601 \"{name}\" elev {elevation}", 1, ElementIds: new[] { "601" }));
        return Task.FromResult(new CommandResultDto(true, $"Created grid 602 \"{name}\"", 1, ElementIds: new[] { "602" }));
    }

    public Task<CommandResultDto> CreateFamilyInstanceArrayAsync(
        string symbolName,
        double startX, double startY, double endX, double endY,
        int count,
        string? levelName = null,
        string? familyName = null,
        CancellationToken ct = default)
    {
        CreateFamilyInstanceArrayCalls.Add((symbolName, startX, startY, endX, endY, count, levelName));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextCreateFamilyInstanceArrayResult != null)
            return Task.FromResult(NextCreateFamilyInstanceArrayResult);
        var cnt = count < 1 ? 1 : (count > 50 ? 50 : count);
        var ids = Enumerable.Range(0, cnt).Select(i => (701 + i).ToString()).ToList();
        return Task.FromResult(new CommandResultDto(
            true,
            $"Created {cnt} {symbolName} array {string.Join(",", ids.Take(5))}",
            cnt,
            ElementIds: ids));
    }

    public Task<CommandResultDto> RotateElementsAsync(
        IReadOnlyList<string> elementIds,
        double angleDegrees,
        double? originX = null,
        double? originY = null,
        CancellationToken ct = default)
    {
        RotateElementsCalls.Add((elementIds, angleDegrees, originX, originY));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextRotateElementsResult != null)
            return Task.FromResult(NextRotateElementsResult);
        return Task.FromResult(new CommandResultDto(true, $"Rotated {elementIds.Count} elements {angleDegrees}° around origin", elementIds.Count));
    }

    public Task<CommandResultDto> MirrorElementsAsync(
        IReadOnlyList<string> elementIds,
        double x1, double y1, double x2, double y2,
        CancellationToken ct = default)
    {
        MirrorElementsCalls.Add((elementIds, x1, y1, x2, y2));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextMirrorElementsResult != null)
            return Task.FromResult(NextMirrorElementsResult);
        return Task.FromResult(new CommandResultDto(true, $"Mirrored {elementIds.Count} -> {elementIds.Count} new elements", elementIds.Count));
    }

    public Task<CommandResultDto> SplitWallAsync(
        string wallId,
        double x, double y,
        CancellationToken ct = default)
    {
        SplitWallCalls.Add((wallId, x, y));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextSplitWallResult != null)
            return Task.FromResult(NextSplitWallResult);
        return Task.FromResult(new CommandResultDto(false, $"Split not yet implemented for {wallId} at ({x},{y}) — use split via Dynamo or manual. Wall curve: Line"));
    }

    public Task<CommandResultDto> JoinGeometryAsync(
        string elementId1,
        string elementId2,
        bool join = true,
        CancellationToken ct = default)
    {
        JoinGeometryCalls.Add((elementId1, elementId2, join));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextJoinGeometryResult != null)
            return Task.FromResult(NextJoinGeometryResult);
        return Task.FromResult(new CommandResultDto(true, join ? $"Joined {elementId1} and {elementId2}" : $"Unjoined {elementId1} and {elementId2}"));
    }

    public Task<CommandResultDto> CreateTopographyAsync(
        IReadOnlyList<PointMmDto> points,
        string? levelName = null, string? levelId = null,
        CancellationToken ct = default)
    {
        CreateTopographyCalls.Add((points, levelName, levelId));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (points == null || points.Count < 3)
            return Task.FromResult(new CommandResultDto(false, "Provide at least 3 points [x,y,z] in mm."));
        if (NextCreateTopographyResult != null)
            return Task.FromResult(NextCreateTopographyResult);
        var lvl = string.IsNullOrWhiteSpace(levelName) ? "Level 1" : levelName;
        return Task.FromResult(new CommandResultDto(true, $"Created toposolid 801 from {points.Count} points on {lvl}.", 1));
    }

    public Task<CommandResultDto> SaveDocumentAsync(CancellationToken ct = default)
    {
        SaveDocumentCallCount++;
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextSaveDocumentResult != null)
            return Task.FromResult(NextSaveDocumentResult);
        if (ModelSummary == null || string.IsNullOrEmpty(ModelSummary.Path) || !ModelSummary.IsSaved)
            return Task.FromResult(new CommandResultDto(false, "Cannot save: document has no path (unsaved)."));
        var ws = SyncInfo != null && SyncInfo.IsWorkshared ? "Workshared" : "Not workshared";
        var central = SyncInfo?.CentralPath ?? "";
        return Task.FromResult(new CommandResultDto(
            true,
            $"Saved {ModelSummary.Title} to {ModelSummary.Path} ({ws}) central: {central}"));
    }

    public Task<CommandResultDto> ReloadLinksAsync(CancellationToken ct = default)
    {
        ReloadLinksCallCount++;
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextReloadLinksResult != null)
            return Task.FromResult(NextReloadLinksResult);
        return Task.FromResult(new CommandResultDto(true, "Reloaded 0/0 RVT links", 0));
    }

    public Task<CommandResultDto> AssignWorksetAsync(
        IReadOnlyList<string> elementIds,
        string worksetName,
        CancellationToken ct = default)
    {
        AssignWorksetCalls.Add((elementIds, worksetName));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (SyncInfo == null || !SyncInfo.IsWorkshared)
            return Task.FromResult(new CommandResultDto(false, "Document is not workshared."));
        if (NextAssignWorksetResult != null)
            return Task.FromResult(NextAssignWorksetResult);
        return Task.FromResult(new CommandResultDto(
            true,
            $"Assigned 0/{elementIds.Count} to workset \"{worksetName}\"",
            0));
    }

    public Task<CommandResultDto> LoadFamilyAsync(string path, CancellationToken ct = default)
    {
        LoadFamilyCalls.Add(path);
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextLoadFamilyResult != null)
            return Task.FromResult(NextLoadFamilyResult);
        var name = Path.GetFileNameWithoutExtension(path);
        return Task.FromResult(new CommandResultDto(
            true,
            $"Loaded family \"{name}\" (901) with 1 symbol(s): {name}",
            1));
    }

    public Task<CommandResultDto> ExportPdfAsync(
        string directory,
        IReadOnlyList<string>? sheetIds = null,
        string? fileName = null,
        bool combine = true,
        CancellationToken ct = default)
    {
        ExportPdfCalls.Add((directory, sheetIds, fileName, combine));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextExportPdfResult != null)
            return Task.FromResult(NextExportPdfResult);
        var views = sheetIds?.Count ?? 1;
        var dest = combine ? Path.Combine(directory, (fileName ?? "export") + ".pdf") : directory;
        return Task.FromResult(new CommandResultDto(
            true, $"Exported {views} sheet(s)/view(s) to PDF: {dest}", views));
    }

    public Task<CommandResultDto> ExportDwgAsync(
        string directory,
        IReadOnlyList<string>? viewIds = null,
        string? fileName = null,
        CancellationToken ct = default)
    {
        ExportDwgCalls.Add((directory, viewIds, fileName));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextExportDwgResult != null)
            return Task.FromResult(NextExportDwgResult);
        var views = viewIds?.Count ?? 1;
        return Task.FromResult(new CommandResultDto(
            true,
            $"Exported {views} view(s) to DWG in {directory} (prefix \"{fileName ?? "export"}\")",
            views));
    }

    public Task<CommandResultDto> ExportIfcAsync(
        string directory,
        string? fileName = null,
        string? version = null,
        CancellationToken ct = default)
    {
        ExportIfcCalls.Add((directory, fileName, version));
        if (!HasActiveDocument)
            return Task.FromResult(new CommandResultDto(false, "Please open a model first."));
        if (NextExportIfcResult != null)
            return Task.FromResult(NextExportIfcResult);
        var fname = fileName ?? "export";
        return Task.FromResult(new CommandResultDto(
            true, $"Exported IFC: {Path.Combine(directory, fname + ".ifc")}", 1));
    }

    public Task<PurgeUnusedResultDto> PurgeUnusedAsync(bool preview = true, CancellationToken ct = default)
    {
        PurgeUnusedCalls.Add(preview);
        if (!HasActiveDocument)
            return Task.FromResult(new PurgeUnusedResultDto(
                preview, 0, 0, 0, Array.Empty<string>(), "Please open a model first."));
        if (NextPurgeUnusedResult != null)
            return Task.FromResult(NextPurgeUnusedResult);
        return Task.FromResult(new PurgeUnusedResultDto(preview, 0, 0, 0, Array.Empty<string>()));
    }

    public Task<ViewRefDto?> CreateScheduleAsync(
        string name, IReadOnlyList<string> fields, CancellationToken ct = default)
    {
        CreateScheduleCalls.Add((name, fields));
        if (!HasActiveDocument)
            return Task.FromResult<ViewRefDto?>(new ViewRefDto(
                string.Empty, string.Empty, string.Empty, "Please open a model first."));
        if (NextCreateScheduleResult != null)
            return Task.FromResult<ViewRefDto?>(NextCreateScheduleResult);
        return Task.FromResult<ViewRefDto?>(new ViewRefDto(
            "5001", $"{name.Trim()} Schedule",
            $"Created schedule 5001 \"{name.Trim()} Schedule\" with {fields?.Count ?? 0} field(s)."));
    }

    public Task<ViewRefDto?> CreateFloorPlanAsync(
        string levelIdOrName, string name, CancellationToken ct = default)
    {
        CreateFloorPlanCalls.Add((levelIdOrName, name));
        if (!HasActiveDocument)
            return Task.FromResult<ViewRefDto?>(new ViewRefDto(
                string.Empty, string.Empty, string.Empty, "Please open a model first."));
        if (NextCreateFloorPlanResult != null)
            return Task.FromResult<ViewRefDto?>(NextCreateFloorPlanResult);
        var level = string.IsNullOrWhiteSpace(levelIdOrName) ? "Level 1" : levelIdOrName;
        return Task.FromResult<ViewRefDto?>(new ViewRefDto(
            "5002", string.IsNullOrWhiteSpace(name) ? level : name,
            $"Created floor plan 5002 \"{(string.IsNullOrWhiteSpace(name) ? level : name)}\" on {level}"));
    }

    public Task<ViewRefDto?> Create3DViewAsync(
        string name, bool isometric, CancellationToken ct = default)
    {
        Create3DViewCalls.Add((name, isometric));
        if (!HasActiveDocument)
            return Task.FromResult<ViewRefDto?>(new ViewRefDto(
                string.Empty, string.Empty, string.Empty, "Please open a model first."));
        if (NextCreate3DViewResult != null)
            return Task.FromResult<ViewRefDto?>(NextCreate3DViewResult);
        var viewName = string.IsNullOrWhiteSpace(name) ? "3D View 1" : name;
        return Task.FromResult<ViewRefDto?>(new ViewRefDto(
            "5003", viewName, $"Created 3D view 5003 \"{viewName}\""));
    }

    public Task<ViewRefDto?> CreateSectionViewAsync(
        string name, PointMmDto min, PointMmDto max, string? viewDirection, CancellationToken ct = default)
    {
        CreateSectionViewCalls.Add((name, min, max, viewDirection));
        if (!HasActiveDocument)
            return Task.FromResult<ViewRefDto?>(new ViewRefDto(
                string.Empty, string.Empty, string.Empty, "Please open a model first."));
        if (NextCreateSectionViewResult != null)
            return Task.FromResult<ViewRefDto?>(NextCreateSectionViewResult);
        var viewName = string.IsNullOrWhiteSpace(name) ? "Section 1" : name;
        return Task.FromResult<ViewRefDto?>(new ViewRefDto(
            "5004", viewName, $"Created section 5004 \"{viewName}\""));
    }

    public Task<PathOfTravelResultDto?> CreatePathOfTravelAsync(
        string viewIdOrName, IReadOnlyList<PathPairDto> pairs, CancellationToken ct = default)
    {
        CreatePathOfTravelCalls.Add((viewIdOrName, pairs));
        if (!HasActiveDocument)
            return Task.FromResult<PathOfTravelResultDto?>(new PathOfTravelResultDto(
                string.Empty, 0, 0, Array.Empty<string>(), Array.Empty<string>(), "Please open a model first."));
        if (NextCreatePathOfTravelResult != null)
            return Task.FromResult<PathOfTravelResultDto?>(NextCreatePathOfTravelResult);
        var ids = Enumerable.Range(1, pairs?.Count ?? 0).Select(i => (9000 + i).ToString()).ToList();
        return Task.FromResult<PathOfTravelResultDto?>(new PathOfTravelResultDto(
            string.IsNullOrWhiteSpace(viewIdOrName) ? "Level 1" : viewIdOrName,
            ids.Count, ids.Count, ids, Array.Empty<string>()));
    }

    /// <summary>
    /// Read-shaped, so it guards before recording: a closed model returns the DTO with the
    /// no-document error and records no call, matching the other read fakes.
    /// </summary>
    public Task<ElementIdListDto?> CollectHostsAsync(
        string scope, IReadOnlyList<string>? categories, int limit, int offset, CancellationToken ct = default)
    {
        if (!HasActiveDocument)
            return Task.FromResult<ElementIdListDto?>(
                new ElementIdListDto(Array.Empty<string>(), offset, 0, "Please open a model first."));

        CollectHostsCalls.Add((scope, categories, limit, offset));
        if (NextCollectHostsResult != null)
            return Task.FromResult<ElementIdListDto?>(NextCollectHostsResult);

        var total = Math.Max(0, limit) + Math.Max(0, offset);
        var ids = Enumerable.Range(Math.Max(0, offset), Math.Max(0, limit))
            .Select(i => (20000 + i).ToString())
            .ToList();
        return Task.FromResult<ElementIdListDto?>(new ElementIdListDto(ids, Math.Max(0, offset), total));
    }

    public Task<SplitResultDto?> SplitBeamsByColumnsAsync(
        SplitRequestDto request, CancellationToken ct = default)
    {
        SplitBeamsCalls.Add(request);
        if (!HasActiveDocument)
            return Task.FromResult<SplitResultDto?>(ClosedSplit());
        if (NextSplitBeamsResult != null)
            return Task.FromResult<SplitResultDto?>(NextSplitBeamsResult);
        return Task.FromResult<SplitResultDto?>(Split(request, "Split 2 beams into 5 pieces."));
    }

    public Task<SplitResultDto?> SplitSecondaryBeamsByMainBeamsAsync(
        SplitRequestDto request, CancellationToken ct = default)
    {
        SplitSecondaryByMainCalls.Add(request);
        if (!HasActiveDocument)
            return Task.FromResult<SplitResultDto?>(ClosedSplit());
        if (NextSplitSecondaryByMainResult != null)
            return Task.FromResult<SplitResultDto?>(NextSplitSecondaryByMainResult);
        return Task.FromResult<SplitResultDto?>(Split(request, "Split 1 secondary beams into 2 pieces."));
    }

    public Task<SplitResultDto?> SplitFloorsByBeamsAsync(
        SplitRequestDto request, CancellationToken ct = default)
    {
        SplitFloorsCalls.Add(request);
        if (!HasActiveDocument)
            return Task.FromResult<SplitResultDto?>(ClosedSplit());
        if (NextSplitFloorsResult != null)
            return Task.FromResult<SplitResultDto?>(NextSplitFloorsResult);
        return Task.FromResult<SplitResultDto?>(Split(request, "Split 1 floors into 3 pieces. Openings: 2."));
    }

    public Task<SplitResultDto?> SplitByLevelsAsync(
        SplitRequestDto request, CancellationToken ct = default)
    {
        SplitByLevelsCalls.Add(request);
        if (!HasActiveDocument)
            return Task.FromResult<SplitResultDto?>(ClosedSplit());
        if (NextSplitByLevelsResult != null)
            return Task.FromResult<SplitResultDto?>(NextSplitByLevelsResult);
        return Task.FromResult<SplitResultDto?>(Split(request, "Split 3 elements into 7 pieces."));
    }

    public Task<SplitResultDto?> SplitByGridsAsync(
        SplitRequestDto request, CancellationToken ct = default)
    {
        SplitByGridsCalls.Add(request);
        if (!HasActiveDocument)
            return Task.FromResult<SplitResultDto?>(ClosedSplit());
        if (NextSplitByGridsResult != null)
            return Task.FromResult<SplitResultDto?>(NextSplitByGridsResult);
        return Task.FromResult<SplitResultDto?>(Split(request, "Split 4 elements into 9 pieces."));
    }

    private static SplitResultDto ClosedSplit()
        => new("Please open a model first.", 0, 0, 0, Array.Empty<string>());

    /// <summary>
    /// Write-shaped, so it records before guarding: a closed model still shows up in the
    /// call list, matching the other write fakes.
    /// </summary>
    public Task<FormworkSetupResultDto?> FormworkSetupAsync(
        FormworkSetupRequestDto request, CancellationToken ct = default)
    {
        FormworkSetupCalls.Add(request);
        if (!HasActiveDocument)
            return Task.FromResult<FormworkSetupResultDto?>(ClosedFormworkSetup());
        if (NextFormworkSetupResult != null)
            return Task.FromResult<FormworkSetupResultDto?>(NextFormworkSetupResult);
        return Task.FromResult<FormworkSetupResultDto?>(new FormworkSetupResultDto(
            "Formwork setup complete. Linked models: 2. Parameters bound: 12, failed: 0 (none). Existing panels: 3."));
    }

    public Task<FormworkPanelsResultDto?> CreateFormworkPanelsAsync(
        FormworkPanelsRequestDto request, CancellationToken ct = default)
    {
        FormworkPanelsCalls.Add(request);
        if (!HasActiveDocument)
            return Task.FromResult<FormworkPanelsResultDto?>(ClosedFormworkPanels());
        if (NextFormworkPanelsResult != null)
            return Task.FromResult<FormworkPanelsResultDto?>(NextFormworkPanelsResult);
        return Task.FromResult<FormworkPanelsResultDto?>(new FormworkPanelsResultDto(
            $"Formwork panels: selected {request.Ids?.Count ?? 0}, candidates 4, created 4, updated 0, duplicates 0, "
            + "coverage skipped 1, panel overlap skipped 0, trimmed 0, obsolete deleted 0, failed 0."));
    }

    public Task<CleanupResultDto?> CleanupFormworkAsync(
        CleanupRequestDto request, CancellationToken ct = default)
    {
        CleanupFormworkCalls.Add(request);
        if (!HasActiveDocument)
            return Task.FromResult<CleanupResultDto?>(ClosedCleanupFormwork());
        if (NextCleanupFormworkResult != null)
            return Task.FromResult<CleanupResultDto?>(NextCleanupFormworkResult);
        return Task.FromResult<CleanupResultDto?>(new CleanupResultDto(
            request.Action is "stats" or ""
                ? "Formwork panels: 3 total. By type: wall=2, beam=1."
                : $"Deleted 2 formwork panel(s) ({request.Action})."));
    }

    public Task<CopyParamsResultDto?> CopyFormworkParentParamsAsync(
        CopyParamsRequestDto request, CancellationToken ct = default)
    {
        CopyFormworkParentParamsCalls.Add(request);
        if (!HasActiveDocument)
            return Task.FromResult<CopyParamsResultDto?>(ClosedCopyFormworkParentParams());
        if (NextCopyFormworkParentParamsResult != null)
            return Task.FromResult<CopyParamsResultDto?>(NextCopyFormworkParentParamsResult);
        return Task.FromResult<CopyParamsResultDto?>(new CopyParamsResultDto(
            request.Names == null || request.Names.Count == 0
                ? "Copyable parent parameters: FW_ConcreteGrade, FW_Thickness."
                : $"Copied {request.Names.Count} parent parameter value(s) onto 3 panel(s). Empty skipped 0, type mismatch 0, unresolved parent 0, unloaded link 0."));
    }

    private static FormworkSetupResultDto ClosedFormworkSetup()
        => new("Please open a model first.");

    private static FormworkPanelsResultDto ClosedFormworkPanels()
        => new("Please open a model first.");

    private static CleanupResultDto ClosedCleanupFormwork()
        => new("Please open a model first.");

    private static CopyParamsResultDto ClosedCopyFormworkParentParams()
        => new("Please open a model first.");

    /// <summary>Write-shaped: records before the closed-document guard.</summary>
    public Task<OpeningPlanResultDto?> OpeningProbeClashAsync(
        OpeningClashRequestDto request, CancellationToken ct = default)
    {
        OpeningProbeCalls.Add(request);
        if (!HasActiveDocument)
            return Task.FromResult<OpeningPlanResultDto?>(
                new OpeningPlanResultDto("Please open a model first.", 0, 0, 0, Array.Empty<OpeningPlanHitDto>()));
        if (NextOpeningProbeResult != null)
            return Task.FromResult<OpeningPlanResultDto?>(NextOpeningProbeResult);
        return Task.FromResult<OpeningPlanResultDto?>(
            new OpeningPlanResultDto("Planned 0 opening(s).", 0, 0, 0, Array.Empty<OpeningPlanHitDto>()));
    }

    public Task<OpeningPlaceResultDto?> PlaceOpeningsAsync(
        OpeningClashRequestDto request, CancellationToken ct = default)
    {
        PlaceOpeningsCalls.Add(request);
        if (!HasActiveDocument)
            return Task.FromResult<OpeningPlaceResultDto?>(
                new OpeningPlaceResultDto("Please open a model first.", 0, 0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>()));
        if (request.DryRun)
            return Task.FromResult<OpeningPlaceResultDto?>(
                new OpeningPlaceResultDto("Planned 2 opening(s). Dry run — no write.", 0, 0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>()));
        if (NextPlaceOpeningsResult != null)
            return Task.FromResult<OpeningPlaceResultDto?>(NextPlaceOpeningsResult);
        return Task.FromResult<OpeningPlaceResultDto?>(new OpeningPlaceResultDto(
            "Placed 1 opening(s) in the active document. 9001(1.0, 2.0, 3.0 ft). Punched 1. Failed 0.",
            1, 1, 0, 0, 0, new[] { "9001" }, new[] { "1.0,2.0,3.0" }));
    }

    public Task<OpeningCutResultDto?> CutHostsByOpeningsAsync(
        OpeningCutRequestDto request, CancellationToken ct = default)
    {
        CutHostsByOpeningsCalls.Add(request);
        if (!HasActiveDocument)
            return Task.FromResult<OpeningCutResultDto?>(
                new OpeningCutResultDto("Please open a model first.", 0, 0, 0, 0));
        if (NextCutHostsResult != null)
            return Task.FromResult<OpeningCutResultDto?>(NextCutHostsResult);
        return Task.FromResult<OpeningCutResultDto?>(
            new OpeningCutResultDto("Cut 1. Skipped linked: 0. Skipped cannot-cut: 0. Failed 0.", 1, 0, 0, 0));
    }

    /// <summary>A dry run reports no writes, so the counters stay at zero.</summary>
    private static SplitResultDto Split(SplitRequestDto request, string report)
    {
        if (request.DryRun)
            return new SplitResultDto(report, 0, 0, 0, Array.Empty<string>());

        var created = Enumerable.Range(1, 3).Select(i => (7000 + i).ToString()).ToList();
        return new SplitResultDto(report, created.Count, 0, 0, created);
    }

    private CommandResultDto FailIfClosed(string emptyMessage, IReadOnlyList<string>? elementIds, string? category)
    {
        if (!HasActiveDocument)
            return new CommandResultDto(false, "Please open a model first.");
        if ((elementIds == null || elementIds.Count == 0) && string.IsNullOrWhiteSpace(category))
            return new CommandResultDto(false, emptyMessage);
        return new CommandResultDto(true, "ok", elementIds?.Count ?? 0);
    }
}
