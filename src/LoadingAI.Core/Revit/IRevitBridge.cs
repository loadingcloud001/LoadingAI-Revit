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

    /// <summary>
    /// Who the open model is, in the terms that stay the same across sessions (cloud model
    /// GUID, worksharing central path, or local path plus project information id). Null when
    /// no document is open. Used to key the saved notes per project.
    /// </summary>
    Task<ProjectIdentityDto?> GetProjectIdentityAsync(CancellationToken ct = default);

    /// <summary>The project index for name matching (P2). Cached by the host until the model changes.</summary>
    Task<ProjectIndex> GetProjectIndexAsync(CancellationToken ct = default);

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

    /// <summary>
    /// Read-only documentation check: unplaced views, missing templates, duplicate numbers and
    /// marks, bad rooms, imported CAD, in-place families, and an optional missing parameter.
    /// Null when no document is open.
    /// </summary>
    Task<IssueAuditDto?> GetIssueAuditAsync(
        string? category = null, string? parameter = null, int limit = 40, CancellationToken ct = default);
    Task<QuantityTakeoffDto?> GetQuantityTakeoffAsync(string category, CancellationToken ct = default);

    /// <summary>
    /// Schedule table body cells. <paramref name="rowLimit"/> is clamped Adapter-side
    /// (default 25, max 40) so the bound holds for any caller. Null when no document is
    /// open; not-found and failures come back in <see cref="ScheduleDataDto.Error"/>.
    /// </summary>
    Task<ScheduleDataDto?> GetScheduleDataAsync(
        string? scheduleId, string? scheduleName, int rowLimit, CancellationToken ct = default);

    /// <summary>
    /// One page of host elements for the structure-cuts algorithms, selected by
    /// <paramref name="scope"/> (<c>selection</c> / <c>visible</c> / <c>all</c>).
    /// <paramref name="categories"/> empty falls back to every splittable host category.
    /// </summary>
    Task<ElementIdListDto?> CollectHostsAsync(
        string scope, IReadOnlyList<string>? categories, int limit, int offset, CancellationToken ct = default);
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

    /// <summary>
    /// Puts back the graphic overrides the active view had before the colour tools first changed them
    /// in this Revit session. Overrides that predate the session, or were set by hand, are untouched.
    /// </summary>
    Task<CommandResultDto> ResetViewOverridesAsync(CancellationToken ct = default);

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

    /// <summary>
    /// Apply one template to one or more views in a single transaction.
    /// <paramref name="viewIds"/> wins when it has an id; otherwise <paramref name="viewId"/>; otherwise the active view.
    /// <paramref name="templateName"/> wins when it is not null: empty clears, otherwise the name is resolved.
    /// A null name keeps the <paramref name="templateId"/> path. <paramref name="dryRun"/> writes nothing.
    /// </summary>
    Task<CommandResultDto> SetViewTemplateAsync(
        string viewId, string? templateId = null,
        IReadOnlyList<string>? viewIds = null,
        string? templateName = null,
        bool dryRun = false,
        CancellationToken ct = default);

    /// <summary>
    /// <paramref name="viewIds"/> wins when it has an id; otherwise <paramref name="viewId"/>; otherwise the active view.
    /// </summary>
    Task<CommandResultDto> SetCropBoxAsync(
        string viewId, bool enabled,
        IReadOnlyList<string>? viewIds = null,
        CancellationToken ct = default);

    Task<CommandResultDto> CreateDimensionBetweenWallsAsync(
        string wallId1, string wallId2, string? viewId = null,
        CancellationToken ct = default);

    Task<CommandResultDto> TagAllInActiveViewAsync(
        string category,
        bool dryRun = false,
        CancellationToken ct = default);

    /// <summary>
    /// Create a category schedule. <paramref name="name"/> is a single token:
    /// "&lt;category&gt;" creates "&lt;category&gt; Schedule", and
    /// "&lt;category&gt;:&lt;schedule name&gt;" creates the schedule under the given name.
    /// The category also resolves from the token's leading words, so "Door List" works.
    /// </summary>
    Task<ViewRefDto?> CreateScheduleAsync(
        string name, IReadOnlyList<string> fields,
        CancellationToken ct = default);

    /// <summary>Create a floor plan on a level. <paramref name="levelIdOrName"/> blank picks the lowest level.</summary>
    Task<ViewRefDto?> CreateFloorPlanAsync(
        string levelIdOrName, string name,
        CancellationToken ct = default);

    /// <summary>Create a 3D view: isometric when <paramref name="isometric"/>, else perspective.</summary>
    Task<ViewRefDto?> Create3DViewAsync(
        string name, bool isometric,
        CancellationToken ct = default);

    /// <summary>
    /// Create a section view. <paramref name="min"/> / <paramref name="max"/> are the section
    /// line end points in millimetres; <paramref name="viewDirection"/> is an axis token
    /// (x / -y / z) and, when blank, the direction is derived from the line.
    /// </summary>
    Task<ViewRefDto?> CreateSectionViewAsync(
        string name, PointMmDto min, PointMmDto max,
        string? viewDirection, CancellationToken ct = default);

    /// <summary>
    /// Create paths of travel in a floor plan. <paramref name="viewIdOrName"/> blank uses the
    /// active view; the 5-pair cap is enforced Adapter-side.
    /// </summary>
    Task<PathOfTravelResultDto?> CreatePathOfTravelAsync(
        string viewIdOrName, IReadOnlyList<PathPairDto> pairs,
        CancellationToken ct = default);

    /// <summary>
    /// Exports a PNG of a view for the model to look at (P1). With ids and isolate, the elements are isolated
    /// for the shot only: the change is rolled back, so the view and the undo list are left as they were.
    /// </summary>
    Task<SnapshotDto> CaptureViewAsync(
        string? view, IReadOnlyList<string>? elementIds, bool isolate, int size, CancellationToken ct = default);
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
        bool dryRun = false,
        CancellationToken ct = default);

    Task<CommandResultDto> CopyParamsAsync(
        string category,
        string sourceParam,
        string targetParam,
        int limit = 200,
        bool dryRun = false,
        CancellationToken ct = default);

    Task<CommandResultDto> ReplaceTextInParamAsync(
        string category,
        string paramName,
        string pattern,
        string replacement,
        int limit = 200,
        bool dryRun = false,
        CancellationToken ct = default);

    Task<CommandResultDto> NumberElementsAsync(
        string? category,
        string prefix,
        int start,
        int pad,
        string? order = null,
        string? levelName = null,
        bool dryRun = false,
        CancellationToken ct = default);
}

/// <summary>
/// Destructive element mutations (delete / move / type change / purge).
/// </summary>
public interface IRevitElementCommand
{
    Task<CommandResultDto> DeleteElementsAsync(
        IReadOnlyList<string> elementIds, CancellationToken ct = default);

    /// <summary>
    /// Moves the whole set in one call, so a host and the elements hosted on it move once. The vector
    /// is millimetres, like every other method on this bridge; the adapter converts.
    /// </summary>
    Task<CommandResultDto> MoveElementsAsync(
        IReadOnlyList<string> elementIds, double dxMm, double dyMm, double dzMm,
        CancellationToken ct = default);

    Task<CommandResultDto> ChangeElementTypeAsync(
        string elementId, string? typeName = null, string? typeId = null,
        CancellationToken ct = default);

    /// <summary>
    /// <c>Document.GetUnusedElements</c>. When <paramref name="preview"/> is true
    /// nothing is deleted and <see cref="PurgeUnusedResultDto.Sample"/> describes
    /// the first few candidates.
    /// </summary>
    Task<PurgeUnusedResultDto> PurgeUnusedAsync(
        bool preview = true, CancellationToken ct = default);
}

/// <summary>
/// Geometry create / transform. Coordinates are millimetres; the adapter
/// converts to feet for the Revit API.
/// </summary>
public interface IRevitCreateCommand
{
    Task<CommandResultDto> CreateWallAsync(
        string levelName,
        double x1, double y1, double x2, double y2,
        string? wallTypeName = null,
        double? heightMm = null,
        CancellationToken ct = default);

    /// <summary>
    /// Places a level-based family. <paramref name="z"/> is millimetres <b>above the level</b>, not an
    /// absolute elevation. A blank level is accepted only when the model has one level.
    /// </summary>
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
        string? familyName = null,
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

    /// <summary>
    /// Split structural framing at the columns that cross it. <c>HostIds</c> null means
    /// every beam in <c>Scope</c>; <c>Cutters</c> null means the current selection.
    /// </summary>
    Task<SplitResultDto?> SplitBeamsByColumnsAsync(SplitRequestDto request, CancellationToken ct = default);

    /// <summary>
    /// Split a strictly shallower structural beam at a same-height pass-through of a
    /// strictly deeper main. Not cope/notch. Empty <c>Scope</c> is refused; never
    /// degrades to every beam in the model. Depth ranking is type nominal section
    /// height only. One-sided framing is skipped, not stubbed.
    /// </summary>
    Task<SplitResultDto?> SplitSecondaryBeamsByMainBeamsAsync(SplitRequestDto request, CancellationToken ct = default);

    /// <summary>
    /// Split floors along the beams and columns that cross them, cutting an opening
    /// where a column passes through. <c>Cutters</c> holds beams and columns together
    /// and is separated by category Adapter-side.
    /// </summary>
    Task<SplitResultDto?> SplitFloorsByBeamsAsync(SplitRequestDto request, CancellationToken ct = default);

    /// <summary>Split walls, beams, columns and floors at the levels that cross them.</summary>
    Task<SplitResultDto?> SplitByLevelsAsync(SplitRequestDto request, CancellationToken ct = default);

    /// <summary>Split walls, beams, columns and floors at the grids that cross them.</summary>
    Task<SplitResultDto?> SplitByGridsAsync(SplitRequestDto request, CancellationToken ct = default);

    /// <summary>
    /// Bind the formwork shared parameters and create the panel schema. Run once per
    /// project before generating panels.
    /// </summary>
    Task<FormworkSetupResultDto?> FormworkSetupAsync(FormworkSetupRequestDto request, CancellationToken ct = default);

    /// <summary>
    /// Create DirectShape formwork panels from the linked (or host) structural elements
    /// in <c>Ids</c>, or from the current selection when <c>Ids</c> is null.
    /// </summary>
    Task<FormworkPanelsResultDto?> CreateFormworkPanelsAsync(FormworkPanelsRequestDto request, CancellationToken ct = default);

    /// <summary>Report or delete formwork panels by <c>Action</c>.</summary>
    Task<CleanupResultDto?> CleanupFormworkAsync(CleanupRequestDto request, CancellationToken ct = default);

    /// <summary>
    /// Copy named parameters from each panel's parent host onto the matching <c>FW_*</c>
    /// instance parameters. An empty <c>Names</c> lists the catalogue instead.
    /// </summary>
    Task<CopyParamsResultDto?> CopyFormworkParentParamsAsync(CopyParamsRequestDto request, CancellationToken ct = default);

    /// <summary>
    /// Plan voids where linked or local MEP solid-intersects the selected host.
    /// Nearby same-host hits combine when leftover web cannot act as a pier.
    /// Does not write. A section box is not a host.
    /// </summary>
    Task<OpeningPlanResultDto?> OpeningProbeClashAsync(OpeningClashRequestDto request, CancellationToken ct = default);

    /// <summary>
    /// Load the four opening families and place plus punch the probe result.
    /// One undo group. Rolls back when fewer pieces than required, or when the family is missing.
    /// </summary>
    Task<OpeningPlaceResultDto?> PlaceOpeningsAsync(OpeningClashRequestDto request, CancellationToken ct = default);

    /// <summary>
    /// Repair the void cut after a family reload. Linked hosts are skipped, not cut.
    /// </summary>
    Task<OpeningCutResultDto?> CutHostsByOpeningsAsync(OpeningCutRequestDto request, CancellationToken ct = default);
}

/// <summary>
/// Document-level writes. Do not wrap <c>Save</c> in a Transaction —
/// Revit rejects that. <c>LoadFamily</c> does need a Transaction.
/// Exports are not Transactions either. <c>directory</c> is always an
/// absolute path already validated by the caller against
/// <c>ExportPaths</c>; the adapter never touches the file system itself.
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

    /// <summary>
    /// Sheets (or the given views) to a combined/split PDF. <paramref name="fileName"/>
    /// is sanitised against the model title when omitted.
    /// </summary>
    Task<CommandResultDto> ExportPdfAsync(
        string directory,
        IReadOnlyList<string>? sheetIds = null,
        string? fileName = null,
        bool combine = true,
        CancellationToken ct = default);

    /// <summary>Views (or the active view) to DWG, AutoCAD 2018 format.</summary>
    Task<CommandResultDto> ExportDwgAsync(
        string directory,
        IReadOnlyList<string>? viewIds = null,
        string? fileName = null,
        CancellationToken ct = default);

    /// <summary>Whole model to IFC. <paramref name="version"/> is an <c>IFCVersion</c> name, ignored when unknown.</summary>
    Task<CommandResultDto> ExportIfcAsync(
        string directory,
        string? fileName = null,
        string? version = null,
        CancellationToken ct = default);
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
