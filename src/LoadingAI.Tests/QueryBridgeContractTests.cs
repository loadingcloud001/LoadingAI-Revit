using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using LoadingAI.Revit;

namespace LoadingAI.Tests;

public class QueryBridgeContractTests
{
    [Fact]
    public void WallMetricsDto_RoundTrips()
    {
        var dto = new WallMetricsDto(12, 12, "Level 1", 40.5, 120.0, 15.25, false);
        var back = JsonSerializer.Deserialize<WallMetricsDto>(JsonSerializer.Serialize(dto));
        Assert.Equal(dto, back);
        Assert.Equal(12, back!.Count);
        Assert.Equal("Level 1", back.LevelName);
    }

    [Fact]
    public void VisibleElementsDto_RoundTrips()
    {
        var dto = new VisibleElementsDto("Level 1", 3, "Walls", null, new[] { "1", "2", "3" });
        var back = JsonSerializer.Deserialize<VisibleElementsDto>(JsonSerializer.Serialize(dto));
        Assert.Equal(3, back!.Count);
        Assert.Equal("Walls", back.Category);
        Assert.Equal(new[] { "1", "2", "3" }, back.SampleIds);
        Assert.Null(back.Error);
    }

    [Fact]
    public void ElementDetailsDto_RoundTrips()
    {
        var dto = new ElementDetailsDto(
            "42", "Wall 1", "Walls", "Basic Wall", "Level 1", "Point (0,0,0)",
            new[] { new ParameterValueDto("Comments", "x", false, "String") },
            1, null);
        var back = JsonSerializer.Deserialize<ElementDetailsDto>(JsonSerializer.Serialize(dto));
        Assert.Equal("42", back!.Id);
        Assert.Single(back.Parameters);
        Assert.Equal("Comments", back.Parameters[0].Name);
        Assert.Null(back.Error);
    }

    [Fact]
    public void FindResultDto_RoundTrips()
    {
        var item = new ElementSummaryDto("1", "Walls", "Basic", "Type A", "W1");
        var dto = new FindResultDto(1, 0, null, new[] { item });
        var back = JsonSerializer.Deserialize<FindResultDto>(JsonSerializer.Serialize(dto));
        Assert.Equal(1, back!.Total);
        Assert.Null(back.Error);
        Assert.Equal("1", back.Items[0].Id);
    }

    [Fact]
    public void ElementQuantityDto_RoundTrips()
    {
        var dto = new ElementQuantityDto("9", "Walls", "W1", 10.0, 20.0, 3.0, "BB");
        var back = JsonSerializer.Deserialize<ElementQuantityDto>(JsonSerializer.Serialize(dto));
        Assert.Equal(10.0, back!.LengthFt);
        Assert.Equal("9", back.Id);
    }

    [Fact]
    public void ActiveViewDetailsDto_RoundTrips()
    {
        var dto = new ActiveViewDetailsDto(
            "Level 1", "FloorPlan", "100", 100, "Fine", 12,
            new[] { new CategoryCountDto("Walls", 8) },
            new[] { "1", "2" });
        var back = JsonSerializer.Deserialize<ActiveViewDetailsDto>(JsonSerializer.Serialize(dto));
        Assert.Equal(100, back!.Scale);
        Assert.Equal("Fine", back.DetailLevel);
        Assert.Equal(8, back.TopCategories[0].Count);
    }

    [Fact]
    public void IRevitQuery_DeclaresPhase1Methods()
    {
        var names = typeof(IRevitQuery).GetMethods().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("GetWallMetricsAsync", names);
        Assert.Contains("GetVisibleElementsInViewAsync", names);
        Assert.Contains("GetElementDetailsAsync", names);
        Assert.Contains("FindElementsFilteredAsync", names);
        Assert.Contains("GetElementQuantitiesAsync", names);
        Assert.Contains("GetActiveViewDetailsAsync", names);
        Assert.Contains("GetWorksetsAsync", names);
        Assert.Contains("GetPhasesAsync", names);
        Assert.Contains("GetDesignOptionsAsync", names);
        Assert.Contains("GetSheetsViewsAsync", names);
        Assert.Contains("GetFamilyTypesAsync", names);
        Assert.Contains("GetMaterialsAsync", names);
        Assert.Contains("GetRoomAreaAsync", names);
        Assert.Contains("GetClashByBoundingBoxAsync", names);
        Assert.Contains("GetModelHealthScoreAsync", names);
        Assert.Contains("GetWarningsRawAsync", names);
        Assert.Contains("GetQuantityTakeoffAsync", names);
        Assert.DoesNotContain("ReloadLinksAsync", names);
        Assert.DoesNotContain("AssignWorksetAsync", names);
        Assert.DoesNotContain("ExportParamsAsync", names);
        Assert.DoesNotContain("ImportParamsAsync", names);
        Assert.DoesNotContain("CopyParamsAsync", names);
        Assert.DoesNotContain("NumberElementsAsync", names);
        Assert.DoesNotContain("CreateSheetAsync", names);
        Assert.DoesNotContain("PlaceViewOnSheetAsync", names);
        Assert.DoesNotContain("DuplicateViewAsync", names);
        Assert.DoesNotContain("SetViewTemplateAsync", names);
        Assert.DoesNotContain("SetCropBoxAsync", names);
        Assert.DoesNotContain("CreateDimensionBetweenWallsAsync", names);
        Assert.DoesNotContain("TagAllInActiveViewAsync", names);
    }

    [Fact]
    public void ParamExportDto_RoundTripsErrorAndRows()
    {
        var dto = new ParamExportDto(
            "Walls",
            new[] { "Comments", "Mark" },
            new[] { new ParamExportRowDto("42", new[] { "ok", "A1" }) },
            null);
        var back = JsonSerializer.Deserialize<ParamExportDto>(JsonSerializer.Serialize(dto));
        Assert.Equal("Walls", back!.Category);
        Assert.Equal("42", back.Rows[0].Id);
        Assert.Equal("A1", back.Rows[0].Values[1]);
        Assert.Null(back.Error);

        var err = new ParamExportDto("Foo", new[] { "Comments" }, Array.Empty<ParamExportRowDto>(), "Unknown category Foo");
        var errBack = JsonSerializer.Deserialize<ParamExportDto>(JsonSerializer.Serialize(err));
        Assert.Equal("Unknown category Foo", errBack!.Error);
        Assert.Empty(errBack.Rows);
    }

    [Fact]
    public void WorksetListDto_RoundTrips()
    {
        var dto = new WorksetListDto(true, new[] { new WorksetDto("1", "Shared Levels", true, false) });
        var back = JsonSerializer.Deserialize<WorksetListDto>(JsonSerializer.Serialize(dto));
        Assert.True(back!.IsWorkshared);
        Assert.Equal("Shared Levels", back.Worksets[0].Name);
        Assert.True(back.Worksets[0].IsOpen);
    }

    [Fact]
    public void ClashResultDto_UnknownCategory_RoundTripsError()
    {
        var dto = new ClashResultDto("Foo", "Bar", 0, false, Array.Empty<ClashPairDto>(), "Unknown categories Foo/Bar");
        var back = JsonSerializer.Deserialize<ClashResultDto>(JsonSerializer.Serialize(dto));
        Assert.Equal("Unknown categories Foo/Bar", back!.Error);
        Assert.Equal(0, back.ClashCount);
    }

    [Fact]
    public void ModelHealthDto_RoundTripsScore()
    {
        var dto = new ModelHealthDto(73.5, 10, 80, 40, true, "HQ");
        var back = JsonSerializer.Deserialize<ModelHealthDto>(JsonSerializer.Serialize(dto));
        Assert.Equal(73.5, back!.Score);
        Assert.Equal(80, back.WarningScore);
        Assert.Equal("HQ", back.Title);
    }

    [Fact]
    public void FamilyTypeListDto_RoundTripsTotalAndSample()
    {
        var dto = new FamilyTypeListDto(25, new[] { new FamilyTypeDto("9", "Door", "Single", "Doors") });
        var back = JsonSerializer.Deserialize<FamilyTypeListDto>(JsonSerializer.Serialize(dto));
        Assert.Equal(25, back!.Total);
        Assert.Equal("Door", back.Sample[0].FamilyName);
    }

    [Fact]
    public void QuantityTakeoffDto_RoundTripsErrorAndRows()
    {
        var dto = new QuantityTakeoffDto(
            "Walls",
            1,
            10.0,
            2.0,
            3.0,
            new[] { new QuantityTakeoffRowDto("42", "W1", "Level 1", 10.0, 2.0, 3.0) },
            null);
        var back = JsonSerializer.Deserialize<QuantityTakeoffDto>(JsonSerializer.Serialize(dto));
        Assert.Equal("Walls", back!.Category);
        Assert.Equal(1, back.Count);
        Assert.Equal("42", back.Rows[0].Id);
        Assert.Equal("Level 1", back.Rows[0].Level);
        Assert.Null(back.Error);

        var err = new QuantityTakeoffDto("Foo", 0, 0, 0, 0, Array.Empty<QuantityTakeoffRowDto>(), "Unknown category Foo");
        var errBack = JsonSerializer.Deserialize<QuantityTakeoffDto>(JsonSerializer.Serialize(err));
        Assert.Equal("Unknown category Foo", errBack!.Error);
        Assert.Empty(errBack.Rows);
    }
}
