using LoadingAI.Plugins.OpeningTools;
using LoadingAI.Core.Openings;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class OpeningToolsContractTests
{
    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"shape":"square"}""")]
    [InlineData("""{"clearanceMm":-1}""")]
    [InlineData("""{"limit":0}""")]
    [InlineData("""{"limit":201}""")]
    [InlineData("""{"familyName":"../secret"}""")]
    public async Task InvalidArgumentsFailWithoutCallingBridge(string json)
    {
        var (bridge, context) = Setup();
        var tool = CreateTool(context, "create_openings_from_clash");

        var result = await tool.ExecuteAsync(json);

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Empty(bridge.PlaceOpeningsCalls);
        Assert.Empty(bridge.OpeningProbeCalls);
        Assert.Contains("Invalid", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AutoLoadIsLimitedToShippedFamilies()
    {
        foreach (var name in new[]
        {
            "LoadingAI_Opening_Horizontal_Rectangle",
            "LoadingAI_Opening_Horizontal_Circle",
            "LoadingAI_Opening_Vertical_Rectangle",
            "LoadingAI_Opening_Vertical_Circle",
        })
            Assert.True(OpeningFamilyRules.IsSupportedAutoLoadFamily(name));

        Assert.False(OpeningFamilyRules.IsSupportedAutoLoadFamily("LoadingAI_Opening_Horizontal"));
        Assert.False(OpeningFamilyRules.IsSupportedAutoLoadFamily("Custom_Opening"));
        Assert.False(OpeningFamilyRules.IsSupportedAutoLoadFamily("../LoadingAI_Opening_Horizontal_Rectangle"));
    }

    [Fact]
    public void PluginFamilyDetectionIsExact()
    {
        Assert.True(OpeningFamilyRules.IsKnownPluginFamily("LoadingAI_Opening"));
        Assert.True(OpeningFamilyRules.IsKnownPluginFamily("LoadingAI_Opening_Horizontal"));
        Assert.True(OpeningFamilyRules.IsKnownPluginFamily("LoadingAI_Opening_Vertical"));
        Assert.True(OpeningFamilyRules.IsKnownPluginFamily("LoadingAI_Opening_Vertical_Circle"));
        Assert.False(OpeningFamilyRules.IsKnownPluginFamily("LoadingAI_OpeningEvil"));
        Assert.False(OpeningFamilyRules.IsKnownPluginFamily("Other_Opening"));
    }

    [Theory]
    [InlineData("My Custom Opening", true)]
    [InlineData("Opening.V2", true)]
    [InlineData("../Opening", false)]
    [InlineData("folder/Opening", false)]
    [InlineData(@"folder\Opening", false)]
    [InlineData(@"C:\Temp\Opening", false)]
    public void CustomFamilyNamesCannotResolvePaths(string name, bool expected)
        => Assert.Equal(expected, OpeningFamilyRules.IsSafeCustomFamilyName(name));

    [Fact]
    public void PlanAndPlaceUseTheSameLevelFilter()
    {
        var filtered = OpeningLevelFilter.FilterByKey(
            new[] { ("other", "99"), ("wanted", "42") },
            item => item.Item2,
            "42");

        var hit = Assert.Single(filtered);
        Assert.Equal("wanted", hit.Item1);
    }

    private static (FakeRevitBridge Bridge, FakePluginContext Context) Setup()
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = true };
        return (bridge, new FakePluginContext(bridge));
    }

    private static ToolEntry CreateTool(FakePluginContext context, string name)
        => new OpeningToolsPlugin().CreateTools(context).Single(t => t.Definition.Function.Name == name);
}
