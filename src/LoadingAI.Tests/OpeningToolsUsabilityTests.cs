using System.IO;
using LoadingAI.Plugins.OpeningTools;
using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class OpeningToolsUsabilityTests
{
    [Fact]
    public async Task DryRunTextLeadsWithNoWriteOutcome()
    {
        var (bridge, context) = Setup();
        bridge.NextOpeningProbeResult = new OpeningPlanResultDto(
            "Planned 2 opening(s). Combined 1 cluster(s).", 2, 1, 0,
            Array.Empty<OpeningPlanHitDto>());

        var result = await CreateTool(context, "create_openings_from_clash").ExecuteAsync("""{"dryRun":true}""");

        Assert.StartsWith("Dry run:", result.Text, StringComparison.Ordinal);
        Assert.Contains("No model changes", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Planned 2 opening(s).", result.Text);
    }

    [Fact]
    public async Task PartialPlaceTextSummarizesCommitThenDetails()
    {
        var (bridge, context) = Setup();
        bridge.NextPlaceOpeningsResult = new OpeningPlaceResultDto(
            "Placed 1. Failed 1.", 1, 1, 1, 2, 0,
            new[] { "42" }, new[] { "1,2,3" }, "Host 99 could not be cut.");

        var result = await CreateTool(context, "create_openings_from_clash").ExecuteAsync("{}");

        Assert.StartsWith("Partial:", result.Text, StringComparison.Ordinal);
        Assert.Contains("1 created", result.Text);
        Assert.Contains("1 cut", result.Text);
        Assert.Contains("1 failed", result.Text);
        Assert.Contains("2 skipped", result.Text);
        Assert.Contains("Host 99 could not be cut.", result.Text);
        Assert.Contains("Placed 1. Failed 1.", result.Text);
    }

    [Fact]
    public async Task CutTextSummarizesEveryOutcome()
    {
        var (bridge, context) = Setup();
        bridge.NextCutHostsResult = new OpeningCutResultDto(
            "Cut 2. Skipped linked: 1. Skipped cannot-cut: 3. Failed 4.",
            2, 1, 3, 4, "One cut failed.");

        var result = await CreateTool(context, "cut_hosts_by_openings").ExecuteAsync("{}");

        Assert.StartsWith("Partial:", result.Text, StringComparison.Ordinal);
        Assert.Contains("2 cut", result.Text);
        Assert.Contains("1 linked host skipped", result.Text);
        Assert.Contains("3 unsupported hosts skipped", result.Text);
        Assert.Contains("4 failed", result.Text);
        Assert.Contains("One cut failed.", result.Text);
    }

    [Fact]
    public void DescriptionsLeadWithActionAndHonestScope()
    {
        var plugin = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "opening-tools", "OpeningToolsPlugin.cs"));
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "Plugins", "opening-tools", "plugin.json"));
        Assert.Contains("Create and cut openings", plugin);
        Assert.Contains("Repair local host cuts", plugin);
        Assert.Contains("No model changes", plugin);
        Assert.Contains("Create and cut openings", json);
        Assert.Contains("Repair local host cuts", json);
        Assert.Contains("No model changes", json);
    }

    private static (FakeRevitBridge Bridge, FakePluginContext Context) Setup()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        return (bridge, new FakePluginContext(bridge));
    }

    private static ToolEntry CreateTool(FakePluginContext context, string name)
        => new OpeningToolsPlugin().CreateTools(context).Single(t => t.Definition.Function.Name == name);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LoadingAI.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find LoadingAI.sln");
    }
}
