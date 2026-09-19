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
