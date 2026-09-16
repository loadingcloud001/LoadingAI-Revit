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
        Assert.DoesNotContain("RunBatchAsync", names);
        Assert.DoesNotContain("SetParameterAsync", names);
        Assert.DoesNotContain("DeleteElementsAsync", names);
    }
}
