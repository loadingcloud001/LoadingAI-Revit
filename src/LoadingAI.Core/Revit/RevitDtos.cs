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

public sealed record CommandResultDto(
    bool Ok,
    string Message,
    int AffectedCount = 0,
    string? ViewName = null);
