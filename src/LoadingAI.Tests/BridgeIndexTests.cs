using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;

namespace LoadingAI.Tests;

/// <summary>
/// The bridge carries the model index (P2) and the full id list behind a filtered
/// find, so later steps can match names and act on every hit, not just the page.
/// </summary>
public class BridgeIndexTests
{
    private static string Source(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { AdapterSource.RepoRoot() }.Concat(parts).ToArray()));

    [Fact]
    public void FindResultDto_RoundTripsAllIds()
    {
        var item = new ElementSummaryDto("1", "Walls", "Basic", "Type A", "W1");
        var dto = new FindResultDto(3, 0, null, new[] { item }, new[] { "1", "2", "3" });
        var back = JsonSerializer.Deserialize<FindResultDto>(JsonSerializer.Serialize(dto));
        Assert.Equal(3, back!.Total);
        Assert.Equal(new[] { "1", "2", "3" }, back.AllIds);
    }

    [Fact]
    public void FindResultDto_AllIdsIsOptional()
    {
        var dto = new FindResultDto(0, 0, null, Array.Empty<ElementSummaryDto>());
        Assert.Null(dto.AllIds);
    }

    [Fact]
    public void IRevitQuery_DeclaresGetProjectIndexAsync()
    {
        var names = typeof(IRevitQuery).GetMethods().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("GetProjectIndexAsync", names);
    }

    [Fact]
    public async Task FakeBridge_ReturnsTheConfiguredIndex()
    {
        var fake = new FakeRevitBridge();
        Assert.Same(ProjectIndex.Empty, await fake.GetProjectIndexAsync());

        var index = new ProjectIndex(
            new[] { new IndexLevel("Level 1", 0) },
            new[] { "A" },
            new[] { new IndexCategory("Walls", 12) },
            new[] { new IndexFamily("Doors", "Single", new[] { "Single-Flush" }) },
            Array.Empty<string>(),
            Array.Empty<string>(),
            new Dictionary<string, IReadOnlyList<string>> { ["Walls"] = new[] { "Mark" } },
            Array.Empty<string>(),
            Array.Empty<string>());
        fake.Index = index;
        Assert.Same(index, await fake.GetProjectIndexAsync());
    }

    [Fact]
    public void Adapter_FindByFilter_ReturnsEveryHitId()
    {
        var src = AdapterSource.Containing("public Task<FindResultDto> FindElementsFilteredAsync");
        Assert.Contains("AllIds:", src);
        Assert.Contains("ResultSetStore.MaxTotalIds", src);
    }

    [Fact]
    public void Adapter_BuildsTheIndexAndDropsItWhenTheModelChanges()
    {
        var src = AdapterSource.Containing("public Task<ProjectIndex> GetProjectIndexAsync");
        Assert.Contains("MarkIndexStale", src);
        Assert.Contains("_indexStale", src);
        // The index is built from the model, not from a canned list.
        Assert.Contains("FilteredElementCollector", src);
    }

    [Fact]
    public void App_MarksTheIndexStaleWhenTheDocumentChanges()
    {
        var src = Source("src", "LoadingAI", "App.cs");
        Assert.Contains("AdapterRevitBridge.MarkIndexStale", src);
        // Subscribed once, unsubscribed on shutdown.
        Assert.Contains("DocumentChanged +=", src);
        Assert.Contains("DocumentChanged -=", src);
    }
}
