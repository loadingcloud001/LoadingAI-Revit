using System;
using System.Linq;
using System.Text.Json;
using LoadingAI.Revit;

namespace LoadingAI.Tests;

public class ViewCommandContractTests
{
    [Fact]
    public void CommandResultDto_RoundTrips()
    {
        var dto = new CommandResultDto(true, "Hid 3 Walls in 'Level 1' (temporary).", 3, "Level 1");
        var back = JsonSerializer.Deserialize<CommandResultDto>(JsonSerializer.Serialize(dto));
        Assert.True(back!.Ok);
        Assert.Equal(3, back.AffectedCount);
        Assert.Equal("Level 1", back.ViewName);
        Assert.Null(back.ElementIds);
    }

    [Fact]
    public void CommandResultDto_Create_RoundTripsElementIds()
    {
        var dto = new CommandResultDto(true, "Created wall 101 on Level 1.", 1, ElementIds: new[] { "101" });
        var back = JsonSerializer.Deserialize<CommandResultDto>(JsonSerializer.Serialize(dto));
        Assert.True(back!.Ok);
        Assert.Equal(new[] { "101" }, back.ElementIds);
    }

    [Fact]
    public void CommandResultDto_Failure_HasNoView()
    {
        var dto = new CommandResultDto(false, "Please open a model first.");
        Assert.False(dto.Ok);
        Assert.Equal(0, dto.AffectedCount);
        Assert.Null(dto.ViewName);
    }

    [Fact]
    public void IRevitViewCommand_DeclaresPhase2ViewMethods()
    {
        var names = typeof(IRevitViewCommand).GetMethods().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("IsolateInViewAsync", names);
        Assert.Contains("HideInViewAsync", names);
        Assert.Contains("ResetViewVisibilityAsync", names);
        Assert.Contains("OverrideColorInViewAsync", names);
        Assert.Contains("ColorByConstructionScheduleAsync", names);
        Assert.Contains("ColorByParameterAsync", names);
        Assert.Contains("CreateSheetAsync", names);
        Assert.Contains("PlaceViewOnSheetAsync", names);
        Assert.Contains("DuplicateViewAsync", names);
        Assert.Contains("SetViewTemplateAsync", names);
        Assert.Contains("SetCropBoxAsync", names);
        Assert.Contains("CreateDimensionBetweenWallsAsync", names);
        Assert.Contains("TagAllInActiveViewAsync", names);
        Assert.DoesNotContain("RunBatchAsync", names);
        Assert.DoesNotContain("SetParameterAsync", names);
        Assert.DoesNotContain("DeleteElementsAsync", names);
        Assert.DoesNotContain("RenameElementsAsync", names);
        Assert.DoesNotContain("ApplyConstructionDatesAsync", names);
        Assert.DoesNotContain("GetWorksetsAsync", names);
        Assert.DoesNotContain("GetClashByBoundingBoxAsync", names);
        Assert.DoesNotContain("GetModelHealthScoreAsync", names);
        Assert.DoesNotContain("ExportParamsAsync", names);
        Assert.DoesNotContain("ImportParamsAsync", names);
        Assert.DoesNotContain("CopyParamsAsync", names);
        Assert.DoesNotContain("ReplaceTextInParamAsync", names);
        Assert.DoesNotContain("NumberElementsAsync", names);
        Assert.DoesNotContain("ReloadLinksAsync", names);
        Assert.DoesNotContain("AssignWorksetAsync", names);
        Assert.DoesNotContain("GetQuantityTakeoffAsync", names);
    }
}
