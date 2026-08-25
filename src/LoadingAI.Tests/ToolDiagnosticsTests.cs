using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class ToolDiagnosticsTests
{
    private static readonly string[] Enabled =
    {
        "get_model_summary", "hide_in_view", "isolate_in_view", "create_wall"
    };

    private static readonly (string PluginId, string ToolName)[] Discovered =
    {
        ("core", "get_model_summary"),
        ("core", "hide_in_view"),
        ("core", "delete_elements"),
        ("myplugin", "export_revit_schedules")
    };

    [Fact]
    public void DisabledTool_MentionsPluginAndHowToEnable()
    {
        var msg = ToolDiagnostics.DescribeMissing("delete_elements", Enabled, Discovered);
        Assert.Contains("DISABLED", msg);
        Assert.Contains("core", msg);
        Assert.Contains("Plugins tab", msg);
    }

    [Fact]
    public void UnknownTypo_SuggestsClosest()
    {
        var msg = ToolDiagnostics.DescribeMissing("hide_in_veiw", Enabled, Discovered);
        Assert.Contains("Did you mean 'hide_in_view'", msg);
    }

    [Fact]
    public void CompletelyUnknown_SaysDoesNotExist()
    {
        var msg = ToolDiagnostics.DescribeMissing("fly_to_moon", Enabled, Discovered);
        Assert.Contains("does not exist", msg);
        Assert.DoesNotContain("DISABLED", msg);
    }

    [Fact]
    public void SimilarityBounds()
    {
        Assert.Equal(100, ToolDiagnostics.Similarity("abc", "abc"));
        Assert.True(ToolDiagnostics.Similarity("hide_in_view", "hide_in_veiw") > 80);
        Assert.True(ToolDiagnostics.Similarity("wall", "moonshot") < ToolDiagnostics.SuggestionThreshold);
    }
}
