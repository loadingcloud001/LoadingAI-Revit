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
    }
}
