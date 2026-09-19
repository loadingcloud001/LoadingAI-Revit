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
    Task<IReadOnlyList<WarningItemDto>> GetWarningsRawAsync(int limit, CancellationToken ct = default);
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
    Task<IReadOnlyList<RoomDto>> GetRoomsAsync(string? levelName = null, int limit = 200, CancellationToken ct = default);

    Task<WorksetListDto?> GetWorksetsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PhaseDto>> GetPhasesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DesignOptionDto>> GetDesignOptionsAsync(CancellationToken ct = default);
    Task<SheetsViewsDto?> GetSheetsViewsAsync(CancellationToken ct = default);
    Task<FamilyTypeListDto?> GetFamilyTypesAsync(string? category = null, CancellationToken ct = default);
    Task<MaterialListDto?> GetMaterialsAsync(CancellationToken ct = default);
    Task<RoomAreaDto?> GetRoomAreaAsync(string? levelName = null, CancellationToken ct = default);
    Task<ClashResultDto?> GetClashByBoundingBoxAsync(
        string category1, string category2, int limit = 20, CancellationToken ct = default);
    Task<ModelHealthDto?> GetModelHealthScoreAsync(CancellationToken ct = default);
    Task<QuantityTakeoffDto?> GetQuantityTakeoffAsync(string category, CancellationToken ct = default);
}

/// <summary>
/// Temporary view visibility and graphic overrides (isolate / hide / reset / color).
/// Overrides must run inside a Transaction.
/// </summary>
public interface IRevitViewCommand
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

    Task<CommandResultDto> OverrideColorInViewAsync(
        IReadOnlyList<string>? elementIds = null,
        string? category = null,
        string? color = null,
        bool reset = false,
        CancellationToken ct = default);

    Task<CommandResultDto> ColorByConstructionScheduleAsync(
        string startDate,
        int intervalDays = 14,
        IReadOnlyList<string>? categories = null,
        string? palette = null,
        CancellationToken ct = default);

    Task<CommandResultDto> ColorByParameterAsync(
        string paramName,
        string? category = null,
        IReadOnlyList<string>? elementIds = null,
        string? palette = null,
        CancellationToken ct = default);

    Task<CommandResultDto> CreateSheetAsync(
        string sheetNumber, string sheetName, string? titleBlockName = null,
        CancellationToken ct = default);

    /// <summary><paramref name="xMm"/> / <paramref name="yMm"/> are millimetres; Adapter converts.</summary>
    Task<CommandResultDto> PlaceViewOnSheetAsync(
        string viewId, string sheetId, double xMm = 0, double yMm = 0,
        CancellationToken ct = default);

    Task<CommandResultDto> DuplicateViewAsync(
        string viewId, bool withDetailing = false,
        CancellationToken ct = default);

    Task<CommandResultDto> SetViewTemplateAsync(
        string viewId, string? templateId = null,
        CancellationToken ct = default);

    Task<CommandResultDto> SetCropBoxAsync(
        string viewId, bool enabled,
        CancellationToken ct = default);

    Task<CommandResultDto> CreateDimensionBetweenWallsAsync(
        string wallId1, string wallId2, string? viewId = null,
        CancellationToken ct = default);

    Task<CommandResultDto> TagAllInActiveViewAsync(
        string category,
        CancellationToken ct = default);
}

/// <summary>Instance parameter writes, rename, bulk dates, and ParamExcel collect/set.</summary>
public interface IRevitParamCommand
{
    Task<CommandResultDto> SetParameterAsync(
        string elementId, string paramName, string value,
        CancellationToken ct = default);

    Task<CommandResultDto> SetParametersAsync(
        IReadOnlyList<string> elementIds, string paramName, string value,
        CancellationToken ct = default);

    Task<CommandResultDto> RenameElementsAsync(
        IReadOnlyList<string> elementIds,
        IReadOnlyList<string>? newNames = null,
        string? newNamePattern = null,
        CancellationToken ct = default);

    Task<CommandResultDto> ApplyConstructionDatesAsync(
        string startDate,
        int intervalDays = 14,
        string? targetParam = null,
        IReadOnlyList<string>? categories = null,
        CancellationToken ct = default);

    Task<ParamExportDto?> ExportParamsAsync(
        string category,
        IReadOnlyList<string> columns,
        int limit = 200,
        CancellationToken ct = default);

    Task<CommandResultDto> ImportParamsAsync(
        string paramName,
        IReadOnlyList<ParamImportRowDto> rows,
        string sourcePath,
        CancellationToken ct = default);

    Task<CommandResultDto> CopyParamsAsync(
        string category,
        string sourceParam,
        string targetParam,
        int limit = 200,
        CancellationToken ct = default);

    Task<CommandResultDto> ReplaceTextInParamAsync(
        string category,
        string paramName,
        string pattern,
        string replacement,
        int limit = 200,
        CancellationToken ct = default);

    Task<CommandResultDto> NumberElementsAsync(
        string? category,
        string prefix,
        int start,
        int pad,
        CancellationToken ct = default);
}

/// <summary>
/// Destructive element mutations (delete / move / type change). Export
/// stays on <c>IRawRevitAccess</c> until it has DTOs.
/// </summary>
public interface IRevitElementCommand
{
    Task<CommandResultDto> DeleteElementsAsync(
        IReadOnlyList<string> elementIds, CancellationToken ct = default);

    Task<CommandResultDto> MoveElementsAsync(
        IReadOnlyList<string> elementIds, double dx, double dy, double dz,
        CancellationToken ct = default);

    Task<CommandResultDto> ChangeElementTypeAsync(
        string elementId, string? typeName = null, string? typeId = null,
        CancellationToken ct = default);
}

/// <summary>
/// Geometry create / transform. Coordinates are millimetres; the adapter
/// converts to feet for the Revit API. Export stays on
/// <c>IRawRevitAccess</c>.
/// </summary>
public interface IRevitCreateCommand
{
    Task<CommandResultDto> CreateWallAsync(
        string levelName,
        double x1, double y1, double x2, double y2,
        string? wallTypeName = null,
        CancellationToken ct = default);

    Task<CommandResultDto> CreateFamilyInstanceAsync(
        string symbolName,
        double x, double y,
        string? familyName = null,
        string? levelName = null,
        double z = 0,
        CancellationToken ct = default);

    Task<CommandResultDto> DuplicateElementAsync(
        string elementId,
        CancellationToken ct = default);

    Task<CommandResultDto> CreateFloorByOutlineAsync(
        string levelName,
        double x1, double y1, double x2, double y2,
        string? floorTypeName = null,
        CancellationToken ct = default);

    Task<CommandResultDto> CreateRoofAsync(
        string levelName,
        double x1, double y1, double x2, double y2,
        CancellationToken ct = default);

    Task<CommandResultDto> CreateGridLevelAsync(
        string kind,
        string name,
        double x1, double y1, double x2, double y2,
        double elevation,
        CancellationToken ct = default);

    Task<CommandResultDto> CreateFamilyInstanceArrayAsync(
        string symbolName,
        double startX, double startY, double endX, double endY,
        int count,
        string? levelName = null,
        CancellationToken ct = default);

    Task<CommandResultDto> RotateElementsAsync(
        IReadOnlyList<string> elementIds,
        double angleDegrees,
        double? originX = null,
        double? originY = null,
        CancellationToken ct = default);

    Task<CommandResultDto> MirrorElementsAsync(
        IReadOnlyList<string> elementIds,
        double x1, double y1, double x2, double y2,
        CancellationToken ct = default);

    Task<CommandResultDto> SplitWallAsync(
        string wallId,
        double x, double y,
        CancellationToken ct = default);

    Task<CommandResultDto> JoinGeometryAsync(
        string elementId1,
        string elementId2,
        bool join = true,
        CancellationToken ct = default);

    Task<CommandResultDto> CreateTopographyAsync(
        IReadOnlyList<PointMmDto> points,
        string? levelName = null, string? levelId = null,
        CancellationToken ct = default);
}

/// <summary>
/// Document-level writes. Do not wrap <c>Save</c> in a Transaction —
/// Revit rejects that. <c>LoadFamily</c> does need a Transaction.
/// Export stays on <c>IRawRevitAccess</c>.
/// </summary>
public interface IRevitDocumentCommand
{
    Task<CommandResultDto> SaveDocumentAsync(CancellationToken ct = default);
    Task<CommandResultDto> ReloadLinksAsync(CancellationToken ct = default);
    Task<CommandResultDto> AssignWorksetAsync(
        IReadOnlyList<string> elementIds,
        string worksetName,
        CancellationToken ct = default);
    Task<CommandResultDto> LoadFamilyAsync(string path, CancellationToken ct = default);
}

/// <summary>
/// Composite mutating contract. Each command is funneled through the
/// App's approval gate (confirm-then-run) in production. Prefer the
/// narrower capability interfaces in tool shells.
/// </summary>
public interface IRevitCommand : IRevitViewCommand, IRevitParamCommand, IRevitElementCommand, IRevitCreateCommand, IRevitDocumentCommand
{
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
