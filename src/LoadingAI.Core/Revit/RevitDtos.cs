using System.Collections.Generic;

namespace LoadingAI.Revit;

// Revit-free DTOs that cross the bridge boundary into Core / LLM tools.
// No Autodesk.Revit types here on purpose: Core (netstandard2.0) must stay
// referenceable from plugins and the LLM layer without a RevitAPI dependency.

public sealed record ModelSummaryDto(
    string Title,
    string Path,
    bool IsSaved,
    int ViewCount,
    int WallCount,
    int DoorCount,
    int WindowCount);

public sealed record SelectionSummaryDto(
    int ElementCount,
    IReadOnlyList<string> CategoryNames,
    IReadOnlyList<string> ElementIds);

public sealed record ViewInfoDto(
    string Name,
    string ViewType,
    bool IsVisible,
    int VisibleElementCount);

public sealed record ElementSummaryDto(
    string Id,
    string Category,
    string Family,
    string Type,
    string Name);

public sealed record WarningDto(
    string Description,
    int Count,
    string Severity,
    IReadOnlyList<string> FailingElementIds);

/// <summary>One ungrouped document warning (audit list, not grouped by description).</summary>
public sealed record WarningItemDto(
    string Description,
    IReadOnlyList<string> FailingElementIds);

public sealed record LevelDto(string Name, double Elevation, string Id);

public sealed record GridDto(string Name, string Id);

public sealed record QuantityDto(
    string Category,
    int Count,
    double TotalArea,
    double TotalVolume,
    double TotalLength);

public sealed record SyncInfoDto(
    string CentralPath,
    bool IsCentral,
    bool IsWorkshared,
    string LastSavedBy,
    int OpenWorksets);

public sealed record WallMetricsDto(
    int Count,
    int Sampled,
    string? LevelName,
    double TotalLengthFt,
    double TotalAreaSqFt,
    double TotalVolumeCuFt,
    bool Truncated);

public sealed record VisibleElementsDto(
    string ViewName,
    int Count,
    string? Category,
    string? Error,
    IReadOnlyList<string> SampleIds);

public sealed record ParameterValueDto(
    string Name,
    string Value,
    bool IsReadOnly,
    string StorageType);

public sealed record ElementDetailsDto(
    string Id,
    string Name,
    string Category,
    string Type,
    string Level,
    string Location,
    IReadOnlyList<ParameterValueDto> Parameters,
    int ParameterTotal,
    string? Error);

public sealed record FindResultDto(
    int Total,
    int Offset,
    string? Error,
    IReadOnlyList<ElementSummaryDto> Items);

public sealed record ElementQuantityDto(
    string Id,
    string Category,
    string Name,
    double? LengthFt,
    double? AreaSqFt,
    double? VolumeCuFt,
    string? BoundingBox);

public sealed record CategoryCountDto(string Name, int Count);

public sealed record ActiveViewDetailsDto(
    string Name,
    string ViewType,
    string ViewId,
    int Scale,
    string DetailLevel,
    int VisibleElementCount,
    IReadOnlyList<CategoryCountDto> TopCategories,
    IReadOnlyList<string> SampleIds);

public sealed record RoomDto(
    string Id,
    string Number,
    string Name,
    string LevelName,
    double AreaSqFt,
    bool Unplaced);

public sealed record WorksetDto(
    string Id,
    string Name,
    bool IsOpen,
    bool IsVisibleByDefault);

public sealed record WorksetListDto(
    bool IsWorkshared,
    IReadOnlyList<WorksetDto> Worksets);

public sealed record PhaseDto(string Id, string Name);

public sealed record DesignOptionDto(string Id, string Name, bool IsPrimary);

public sealed record SheetSummaryDto(string Id, string Name, string SheetNumber);

public sealed record SheetsViewsDto(
    int SheetCount,
    int ViewCount,
    IReadOnlyList<SheetSummaryDto> Sheets,
    IReadOnlyList<CategoryCountDto> ViewsByType);

public sealed record FamilyTypeDto(
    string Id,
    string FamilyName,
    string Name,
    string Category);

public sealed record FamilyTypeListDto(int Total, IReadOnlyList<FamilyTypeDto> Sample);

public sealed record MaterialDto(string Id, string Name, string MaterialClass);

public sealed record MaterialListDto(int Total, IReadOnlyList<MaterialDto> Sample);

public sealed record RoomAreaDto(
    int RoomCount,
    double TotalAreaSqFt,
    IReadOnlyList<RoomDto> Sample);

public sealed record ClashPairDto(string IdA, string CategoryA, string IdB, string CategoryB);

public sealed record ClashResultDto(
    string Category1,
    string Category2,
    int ClashCount,
    bool Sampled,
    IReadOnlyList<ClashPairDto> Clashes,
    string? Error = null);

public sealed record ModelHealthDto(
    double Score,
    int WarningCount,
    int WarningScore,
    int ViewCount,
    bool IsWorkshared,
    string Title);

public sealed record CommandResultDto(
    bool Ok,
    string Message,
    int AffectedCount = 0,
    string? ViewName = null,
    IReadOnlyList<string>? ElementIds = null);

/// <summary>One millimetre-space point at the Bridge boundary. Adapter converts with <c>Metric.MmToFt</c>.</summary>
public sealed record PointMmDto(double X, double Y, double Z);

public sealed record ParamExportRowDto(
    string Id,
    IReadOnlyList<string> Values);

public sealed record ParamExportDto(
    string Category,
    IReadOnlyList<string> Columns,
    IReadOnlyList<ParamExportRowDto> Rows,
    string? Error = null);

public sealed record QuantityTakeoffRowDto(
    string Id,
    string Name,
    string? Level,
    double AreaFt2,
    double VolumeFt3,
    double LengthFt);

public sealed record QuantityTakeoffDto(
    string Category,
    int Count,
    double TotalAreaFt2,
    double TotalVolumeFt3,
    double TotalLengthFt,
    IReadOnlyList<QuantityTakeoffRowDto> Rows,
    string? Error = null);

public sealed record ParamImportRowDto(
    string Id,
    string Value);

/// <summary>
/// Outcome of a purge run. <c>DeletedCount</c> and <c>Passes</c> stay 0 when
/// <c>Preview</c> is true, so a preview can never be mistaken for a delete.
/// Same partial-failure convention as <see cref="QuantityTakeoffDto"/>.
/// </summary>
public sealed record PurgeUnusedResultDto(
    bool Preview,
    int UnusedCount,
    int DeletedCount,
    int Passes,
    IReadOnlyList<string> Sample,
    string? Error = null);

/// <summary>
/// Handle on a view or schedule the Adapter just created. <c>Message</c> is the
/// human-readable outcome composed Adapter-side, same convention as
/// <see cref="CommandResultDto.Message"/>, so a plugin handler stays plumbing.
/// </summary>
public sealed record ViewRefDto(
    string Id,
    string Name,
    string Message,
    string? Error = null);

/// <summary>
/// Schedule table body, already clamped to the requested row window. Cell text
/// is tab-scrubbed Adapter-side so a row can be joined for display. Same
/// nullable-DTO / trailing <c>Error</c> convention as <see cref="ElementDetailsDto"/>.
/// </summary>
public sealed record ScheduleDataDto(
    string Id,
    string Name,
    int TotalRows,
    int TotalColumns,
    int ShownRows,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    string? Error = null);

/// <summary>One path-of-travel start/end pair. Millimetres; Adapter converts.</summary>
public sealed record PathPairDto(PointMmDto Start, PointMmDto End);

/// <summary>
/// Outcome of a path-of-travel run. <c>CreatedIds</c> holds only the paths that
/// were actually created; <c>Failures</c> holds one <c>#index:status</c> entry per
/// pair that did not solve, so a partial success is never mistaken for a full one.
/// </summary>
public sealed record PathOfTravelResultDto(
    string ViewName,
    int Requested,
    int Created,
    IReadOnlyList<string> CreatedIds,
    IReadOnlyList<string> Failures,
    string? Error = null);

/// <summary>
/// One page of host element ids, in the order the host collected them.
/// <c>Offset</c> echoes the requested window and <c>Total</c> is the size of the
/// whole collection, so a caller can page without re-collecting.
/// </summary>
public sealed record ElementIdListDto(
    IReadOnlyList<string> Ids,
    int Offset,
    int Total,
    string? Error = null);

/// <summary>
/// Input for one structure-cuts run. <c>HostIds</c> and <c>Cutters</c> are the two
/// id filters the algorithms take; <b>null means every element</b>, while an empty
/// list is honoured as-is, so a caller holding an empty list must pass null to ask
/// for the default. <c>Categories</c> follows the same rule, and <c>Scope</c> is
/// only consulted when <c>HostIds</c> is null. <c>DryRun</c> short-circuits before
/// any transaction, which is why a dry run always reports zero writes.
/// </summary>
public sealed record SplitRequestDto(
    IReadOnlyList<string>? HostIds,
    IReadOnlyList<string>? Cutters,
    IReadOnlyList<string>? Categories,
    string Scope,
    int ContinueFrom,
    int Limit,
    bool DryRun,
    bool UniqueMark,
    bool CopyParams,
    bool IncludeLinks = false);

/// <summary>
/// Outcome of one structure-cuts run. <c>Report</c> is the algorithms' own prose,
/// verbatim; the counters are kept separate from it so a partial run cannot read as
/// a whole one. <c>Created</c> counts hosts replaced by more than one piece and
/// <c>Skipped</c> counts hosts left alone for any reason.
/// </summary>
public sealed record SplitResultDto(
    string Report,
    int Created,
    int Openings,
    int Skipped,
    IReadOnlyList<string> NewIds,
    string? Error = null);

/// <summary>
/// Input for one formwork setup run. The tool takes no arguments today; the type exists
/// so the slice keeps the same request/result shape as its siblings and a future knob
/// does not need a signature change.
/// </summary>
public sealed record FormworkSetupRequestDto();

/// <summary>
/// Outcome of one formwork setup run. <c>Report</c> is the algorithm's own prose, verbatim.
/// </summary>
public sealed record FormworkSetupResultDto(string Report, string? Error = null);

/// <summary>
/// Input for one formwork panel generation run. <c>Ids</c> null means the current selection;
/// an empty list is honoured as-is. <c>FaceTypes</c> null or empty means every face type the
/// generator supports, and <c>ThicknessMm</c> is in millimetres like the tool argument.
/// </summary>
public sealed record FormworkPanelsRequestDto(
    IReadOnlyList<string>? Ids,
    double ThicknessMm,
    IReadOnlyList<string>? FaceTypes,
    bool Collision,
    bool DeleteExisting,
    bool IncludeUnselectedLinks);

/// <summary>
/// Outcome of one formwork panel generation run. The generator's counters are already
/// folded into <c>Report</c>, so only the prose and a failure message cross the Bridge.
/// </summary>
public sealed record FormworkPanelsResultDto(string Report, string? Error = null);

/// <summary>
/// Input for one formwork cleanup run. <c>Action</c> is already normalised to lower case
/// by the caller and is one of <c>stats</c>, <c>delete_all</c>, <c>delete_by_type</c> or
/// <c>cleanup_orphans</c>; <c>FaceType</c> is only consulted by <c>delete_by_type</c>.
/// </summary>
public sealed record CleanupRequestDto(string Action, string? FaceType);

/// <summary>
/// Outcome of one formwork cleanup run. <c>Report</c> is the algorithm's own prose, verbatim.
/// </summary>
public sealed record CleanupResultDto(string Report, string? Error = null);

/// <summary>
/// Input for one parent-parameter copy run. <c>Ids</c> null or empty means every formwork
/// panel in the model; an empty <c>Names</c> asks for the parameter catalogue instead of a copy.
/// </summary>
public sealed record CopyParamsRequestDto(IReadOnlyList<string>? Ids, IReadOnlyList<string>? Names);

/// <summary>
/// Outcome of one parent-parameter copy run. The copied/skipped counters are already
/// folded into <c>Report</c>, so only the prose and a failure message cross the Bridge.
/// </summary>
public sealed record CopyParamsResultDto(string Report, string? Error = null);

/// <summary>
/// Input for one opening-tools clash probe or place. <c>MepIds</c> and <c>HostIds</c>
/// empty means the current selection (a link selection is a valid MEP seed).
/// <c>ClearanceMm</c> is millimetres at the tool boundary. <c>Shape</c> is
/// <c>rectangle</c>, <c>circle</c>, or <c>auto</c>. <c>DryRun</c> is the plugin's
/// concern — the probe slice never writes either way.
/// </summary>
public sealed record OpeningClashRequestDto(
    IReadOnlyList<string> MepIds,
    IReadOnlyList<string> HostIds,
    string Shape,
    double ClearanceMm,
    string? FamilyName,
    string? LevelName,
    int Limit,
    bool DryRun);

/// <summary>
/// One planned void. Coordinates are feet (the host's internal unit) so a later
/// place call can replay the probe without re-deriving the clash.
/// </summary>
public sealed record OpeningPlanHitDto(
    string HostId,
    string SourceId,
    double XFt,
    double YFt,
    double ZFt,
    double WidthMm,
    double HeightMm,
    double DepthMm,
    string Shape,
    bool Combined,
    int CombinedCount,
    string CombinedSources);

/// <summary>
/// Probe outcome. <c>Report</c> is the skip reason when nothing can be planned;
/// <c>Hits</c> is empty in that case and on a scope refusal. Never a write.
/// </summary>
public sealed record OpeningPlanResultDto(
    string Report,
    int Count,
    int CombinedClusters,
    int LargeClusters,
    IReadOnlyList<OpeningPlanHitDto> Hits,
    string? Error = null);

/// <summary>
/// Place outcome. <c>InstanceIds</c> and <c>Points</c> are parallel lists
/// (<c>id(x, y, z ft)</c> lives in <c>Report</c> as well). <c>JoinsLost</c> is 0
/// when the host API does not expose a join delta — that residual is stated in
/// <c>Report</c> rather than dropped.
/// </summary>
public sealed record OpeningPlaceResultDto(
    string Report,
    int Placed,
    int Punched,
    int Failed,
    int Skipped,
    int JoinsLost,
    IReadOnlyList<string> InstanceIds,
    IReadOnlyList<string> Points,
    string? Error = null);

/// <summary>Repair-cut input. Empty lists fall through to the current selection.</summary>
public sealed record OpeningCutRequestDto(
    IReadOnlyList<string> OpeningIds,
    IReadOnlyList<string> HostIds);

/// <summary>Repair-cut outcome. Linked hosts are counted in the report, never cut.</summary>
public sealed record OpeningCutResultDto(
    string Report,
    int Cut,
    int SkippedLinked,
    int SkippedCannot,
    int Failed,
    string? Error = null);
