using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LoadingAI.Revit;

// The revit-free contract between the LLM/tool layer and the App's bridge.
// All signatures return/accept only Core DTOs, never Autodesk.Revit types,
// so this interface can live in LoadingAI.Core (netstandard2.0) and be
// referenced by plugins and Core tool shells without a RevitAPI dependency.

/// <summary>Read-only operations against the active Revit model.</summary>
public interface IRevitQuery
{
    Task<ModelSummaryDto?> GetModelSummaryAsync(CancellationToken ct = default);
    Task<SelectionSummaryDto> GetSelectionSummaryAsync(CancellationToken ct = default);
    Task<ViewInfoDto?> GetActiveViewInfoAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ElementSummaryDto>> FindElementsAsync(
        string? family = null, string? category = null, string? typeName = null,
        CancellationToken ct = default);
    Task<IReadOnlyList<WarningDto>> GetWarningsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LevelDto>> GetLevelsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<GridDto>> GetGridsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<QuantityDto>> GetQuantitiesAsync(CancellationToken ct = default);
    Task<SyncInfoDto?> GetSyncInfoAsync(CancellationToken ct = default);

    Task<WallMetricsDto?> GetWallMetricsAsync(string? levelName = null, int limit = 500, CancellationToken ct = default);
    Task<VisibleElementsDto?> GetVisibleElementsInViewAsync(string? category = null, int limit = 100, CancellationToken ct = default);
    Task<ElementDetailsDto?> GetElementDetailsAsync(string elementId, CancellationToken ct = default);
    Task<FindResultDto> FindElementsFilteredAsync(
        string category,
        string? paramName = null, string? op = null, string? value = null,
        int limit = 100, int offset = 0,
        CancellationToken ct = default);
    Task<IReadOnlyList<ElementQuantityDto>> GetElementQuantitiesAsync(
        IReadOnlyList<string>? elementIds = null, CancellationToken ct = default);
    Task<ActiveViewDetailsDto?> GetActiveViewDetailsAsync(int limit = 200, CancellationToken ct = default);
}

/// <summary>
/// Mutating / multi-step operations. Each command is funneled through the
/// App's approval gate (confirm-then-run) in production.
/// </summary>
public interface IRevitCommand
{
    Task<CommandResultDto> IsolateInViewAsync(
        IReadOnlyList<string>? elementIds = null,
        string? category = null,
        bool isolate = true,
        CancellationToken ct = default);

    Task<CommandResultDto> HideInViewAsync(
        IReadOnlyList<string>? elementIds = null,
        string? category = null,
        CancellationToken ct = default);

    Task<CommandResultDto> ResetViewVisibilityAsync(CancellationToken ct = default);

    Task<CommandResultDto> SetParameterAsync(
        string elementId, string paramName, string value,
        CancellationToken ct = default);

    Task<CommandResultDto> SetParametersAsync(
        IReadOnlyList<string> elementIds, string paramName, string value,
        CancellationToken ct = default);

    Task<CommandResultDto> DeleteElementsAsync(
        IReadOnlyList<string> elementIds, CancellationToken ct = default);

    Task<CommandResultDto> MoveElementsAsync(
        IReadOnlyList<string> elementIds, double dx, double dy, double dz,
        CancellationToken ct = default);
}

/// <summary>Unified bridge contract used by tools and the LLM layer.</summary>
public interface IRevitBridge : IRevitQuery, IRevitCommand
{
    /// <summary>
    /// Last-known flag: true when a valid Revit document was open the last
    /// time the host observed the UI thread. Never pumps ExternalEvent.
    /// </summary>
    bool HasActiveDocument { get; }
}
