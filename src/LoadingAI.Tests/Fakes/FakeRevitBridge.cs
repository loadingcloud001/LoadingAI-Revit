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
        => Task.FromResult<IReadOnlyList<WarningDto>>(Array.Empty<WarningDto>());

    public Task<IReadOnlyList<LevelDto>> GetLevelsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LevelDto>>(Array.Empty<LevelDto>());

    public Task<IReadOnlyList<GridDto>> GetGridsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<GridDto>>(Array.Empty<GridDto>());

    public Task<IReadOnlyList<QuantityDto>> GetQuantitiesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<QuantityDto>>(Array.Empty<QuantityDto>());

    public Task<SyncInfoDto?> GetSyncInfoAsync(CancellationToken ct = default)
        => Task.FromResult<SyncInfoDto?>(null);

    public Task<WallMetricsDto?> GetWallMetricsAsync(string? levelName = null, int limit = 500, CancellationToken ct = default)
        => Task.FromResult<WallMetricsDto?>(null);

    public Task<VisibleElementsDto?> GetVisibleElementsInViewAsync(string? category = null, int limit = 100, CancellationToken ct = default)
        => Task.FromResult<VisibleElementsDto?>(null);

    public Task<ElementDetailsDto?> GetElementDetailsAsync(string elementId, CancellationToken ct = default)
        => Task.FromResult<ElementDetailsDto?>(null);

    public Task<FindResultDto> FindElementsFilteredAsync(
        string category,
        string? paramName = null, string? op = null, string? value = null,
        int limit = 100, int offset = 0,
        CancellationToken ct = default)
        => Task.FromResult(new FindResultDto(0, offset, null, Array.Empty<ElementSummaryDto>()));

    public Task<IReadOnlyList<ElementQuantityDto>> GetElementQuantitiesAsync(
        IReadOnlyList<string>? elementIds = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ElementQuantityDto>>(Array.Empty<ElementQuantityDto>());

    public Task<ActiveViewDetailsDto?> GetActiveViewDetailsAsync(int limit = 200, CancellationToken ct = default)
        => Task.FromResult<ActiveViewDetailsDto?>(null);

    public Task<IReadOnlyList<RoomDto>> GetRoomsAsync(string? levelName = null, int limit = 200, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RoomDto>>(Array.Empty<RoomDto>());

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

    public Task<CommandResultDto> DeleteElementsAsync(
        IReadOnlyList<string> elementIds, CancellationToken ct = default)
        => Task.FromResult(new CommandResultDto(false, "L3 not implemented on fake."));

    public Task<CommandResultDto> MoveElementsAsync(
        IReadOnlyList<string> elementIds, double dx, double dy, double dz,
        CancellationToken ct = default)
        => Task.FromResult(new CommandResultDto(false, "L3 not implemented on fake."));

    private CommandResultDto FailIfClosed(string emptyMessage, IReadOnlyList<string>? elementIds, string? category)
    {
        if (!HasActiveDocument)
            return new CommandResultDto(false, "Please open a model first.");
        if ((elementIds == null || elementIds.Count == 0) && string.IsNullOrWhiteSpace(category))
            return new CommandResultDto(false, emptyMessage);
        return new CommandResultDto(true, "ok", elementIds?.Count ?? 0);
    }
}
