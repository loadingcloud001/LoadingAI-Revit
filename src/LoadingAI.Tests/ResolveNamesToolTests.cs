using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

/// <summary>
/// The model asks in its own words ("beam", "L3"), Revit answers with exact names
/// ("Structural Framing", "Level 3"). <c>resolve_names</c> is the bridge between the two,
/// so the model never has to guess a name it then passes to a query tool.
/// </summary>
public class ResolveNamesToolTests
{
    [Fact]
    public async Task ReturnsExactNamesPerTerm()
    {
        var bridge = new FakeRevitBridge
        {
            Index = ProjectIndex.Empty with
            {
                Levels = new[] { new IndexLevel("Level 3", 7000) },
                Categories = new[] { new IndexCategory("Structural Framing", 40) }
            }
        };
        var tool = QueryTools.Create(new FakePluginContext(bridge)).Single(t => t.Definition.Function.Name == "resolve_names");

        var r = await tool.ExecuteAsync("{\"category\":\"beam\",\"level\":\"L3\"}");

        Assert.Contains("category \"beam\" → Structural Framing", r.Text);
        Assert.Contains("level \"L3\" → Level 3", r.Text);
    }

    [Fact]
    public async Task NoTerms_IsAnArgumentError()
    {
        var tool = QueryTools.Create(new FakePluginContext(new FakeRevitBridge())).Single(t => t.Definition.Function.Name == "resolve_names");

        var r = await tool.ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Failed, r.Status);
    }
}
