using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class QueryHonestyTests
{
    private static ToolEntry Query(string name, FakeRevitBridge bridge)
        => QueryTools.Create(new FakePluginContext(bridge)).First(e => e.Definition.Function.Name == name);

    private static FakeRevitBridge Open() => new() { HasActiveDocument = true };

    [Fact]
    public async Task GetWarnings_CountsWarningsNotKinds()
    {
        var bridge = Open();
        bridge.GroupedWarnings = new[]
        {
            new WarningDto("Walls overlap", 3000, "warning", new[] { "1", "2" }),
            new WarningDto("Room not enclosed", 12, "warning", new[] { "9" })
        };
        var result = await Query("get_warnings", bridge).ExecuteAsync("{}");
        Assert.Contains("Warnings: 3012 in 2 kind(s)", result.Text);
        Assert.Contains("x3000 Walls overlap", result.Text);
        Assert.True(result.Text.IndexOf("x3000", StringComparison.Ordinal) < result.Text.IndexOf("x12", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetWallMetrics_DoesNotCallATotalFromASampleASample()
    {
        var bridge = Open();
        bridge.NextWallMetrics = new WallMetricsDto(1200, 1200, null, 3048.0, 1000.0, 100.0, false);
        var result = await Query("get_wall_metrics", bridge).ExecuteAsync("{}");
        Assert.StartsWith("Walls: 1200", result.Text);
        Assert.DoesNotContain("sampled", result.Text);
        Assert.DoesNotContain("limited to", result.Text);
    }

    [Theory]
    [InlineData("get_clash_by_bb", "bounding box")]
    [InlineData("audit_warnings_fix", "fixes nothing")]
    [InlineData("sync_info", "last sync")]
    [InlineData("get_model_health_score", "warning count")]
    public void Descriptions_SayWhatTheToolDoes(string tool, string phrase)
    {
        var description = Query(tool, Open()).Definition.Function.Description ?? "";
        Assert.True(description.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0, tool + ": " + description);
    }

    [Fact]
    public async Task GetClashByBb_SaysWhenTheLimitCutTheList()
    {
        var bridge = Open();
        bridge.NextClashResult = new ClashResultDto("Walls", "Doors", 2, true, new[]
        {
            new ClashPairDto("1", "Walls", "2", "Doors"),
            new ClashPairDto("3", "Walls", "4", "Doors")
        });
        var result = await Query("get_clash_by_bb", bridge).ExecuteAsync("""{"category1":"Walls","category2":"Doors","limit":2}""");
        Assert.Contains("limit reached", result.Text);
        Assert.Contains("first 200 of each category", result.Text);
    }

    [Fact]
    public void Source_AdapterQueriesAreHonest()
    {
        var src = AdapterSource.Containing("public Task<WallMetricsDto?> GetWallMetricsAsync");
        Assert.DoesNotContain("walls.Take(limit)", src);
        Assert.Contains("HasParameterValue(p)", src);
        Assert.Contains("private static bool HasParameterValue(Parameter p)", src);
        Assert.Contains("(doc.GetElement(el.GetTypeId()) as ElementType)?.LookupParameter(paramName!)", src);
    }
}
