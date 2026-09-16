using System;
using System.Collections.Generic;
using System.IO;
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
    public void BridgeContract_SurfaceExists()
    {
        // Compile-time guard: unified contract inherits query + capability slices.
        var interfaces = typeof(IRevitBridge).GetInterfaces();
        Assert.Contains(typeof(IRevitQuery), interfaces);
        Assert.Contains(typeof(IRevitCommand), interfaces);
        Assert.Contains(typeof(IRevitViewCommand), interfaces);
        Assert.Contains(typeof(IRevitParamCommand), interfaces);
        Assert.Contains(typeof(IRevitElementCommand), interfaces);
        Assert.Contains(typeof(IRevitCreateCommand), interfaces);

        var commandBases = typeof(IRevitCommand).GetInterfaces();
        Assert.Contains(typeof(IRevitViewCommand), commandBases);
        Assert.Contains(typeof(IRevitParamCommand), commandBases);
        Assert.Contains(typeof(IRevitElementCommand), commandBases);
        Assert.Contains(typeof(IRevitCreateCommand), commandBases);
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

    [Fact]
    public void RevitBridge_Execute_DoesNotDropWaitersOnActionFailure()
    {
        // Empty catch around queued actions left RunAsync sitting until DefaultTimeout.
        // Execute must drain leftovers so each Enqueue wrapper can TrySetException.
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Revit", "RevitBridge.cs"));
        Assert.DoesNotContain("try { a(app); }", src);
        Assert.DoesNotContain("catch { }", src);
        Assert.Contains("tcs.TrySetException", src);
        Assert.Contains("bridge.action_failed", src);
        Assert.Contains("DrainTo", src);
        Assert.Contains("DefaultTimeout", src);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LoadingAI.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find LoadingAI.sln from " + AppContext.BaseDirectory);
    }
}
