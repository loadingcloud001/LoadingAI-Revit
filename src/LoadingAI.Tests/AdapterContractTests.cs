using System;
using System.Collections.Generic;
using System.Text.Json;
using LoadingAI.Revit;

namespace LoadingAI.Tests;

public class AdapterContractTests
{
    [Fact]
    public void ModelSummaryDto_RoundTrips()
    {
        var dto = new ModelSummaryDto("Test", @"C:\model.rvt", true, 12, 34, 5, 6);
        var json = JsonSerializer.Serialize(dto);
        var back = JsonSerializer.Deserialize<ModelSummaryDto>(json);
        Assert.Equal(dto, back);
        Assert.Equal(34, back!.WallCount);
    }

    [Fact]
    public void Dtos_AreRevitFree_PlainData()
    {
        // These DTOs must carry only JSON-serializable primitives so they can
        // cross the bridge into Core/plugins without a RevitAPI reference.
        var selection = new SelectionSummaryDto(3, new[] { "Walls", "Doors" }, new[] { "id1", "id2", "id3" });
        var json = JsonSerializer.Serialize(selection);
        Assert.Contains("\"ElementCount\":3", json);
        var back = JsonSerializer.Deserialize<SelectionSummaryDto>(json)!;
        Assert.Equal(selection.ElementCount, back.ElementCount);
        Assert.Equal(selection.CategoryNames, back.CategoryNames);
        Assert.Equal(selection.ElementIds, back.ElementIds);
    }

    [Fact]
    public void BatchOperation_Serializes()
    {
        var op = new BatchOperation("get_model_summary", "{}");
        var json = JsonSerializer.Serialize(op);
        var back = JsonSerializer.Deserialize<BatchOperation>(json);
        Assert.NotNull(back);
        Assert.Equal("get_model_summary", back!.Operation);
        Assert.Equal("{}", back.ArgsJson);
    }

    [Fact]
    public void BridgeContract_SurfaceExists()
    {
        // Compile-time guard that the unified contract inherits query + command.
        var interfaces = typeof(IRevitBridge).GetInterfaces();
        Assert.Contains(typeof(IRevitQuery), interfaces);
        Assert.Contains(typeof(IRevitCommand), interfaces);
    }

    [Fact]
    public void ViewInfoDto_IsVisible_IsARealField()
    {
        var hidden = new ViewInfoDto("Level 1", "FloorPlan", false, 12);
        var json = JsonSerializer.Serialize(hidden);
        Assert.Contains("\"IsVisible\":false", json);
        var back = JsonSerializer.Deserialize<ViewInfoDto>(json)!;
        Assert.False(back.IsVisible);
    }
}
