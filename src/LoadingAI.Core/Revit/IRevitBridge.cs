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
}

/// <summary>One step in a batched bridge dispatch.</summary>
public sealed record BatchOperation(
    string Operation,
    string ArgsJson);

/// <summary>
/// Mutating / multi-step operations. Each command is funneled through the
/// App's approval gate (confirm-then-run) in production.
/// </summary>
public interface IRevitCommand
{
    /// <summary>
    /// Executes a sequence of operations in a single Revit modal dispatch
    /// (good for consistency across many steps and for batch performance).
    /// Returns a JSON object mapping each operation's result.
    /// </summary>
    Task<string> RunBatchAsync(IReadOnlyList<BatchOperation> operations, CancellationToken ct = default);
}

/// <summary>Unified bridge contract used by tools and the LLM layer.</summary>
public interface IRevitBridge : IRevitQuery, IRevitCommand
{
    /// <summary>True when a valid Revit document is currently open.</summary>
    bool HasActiveDocument { get; }
}
