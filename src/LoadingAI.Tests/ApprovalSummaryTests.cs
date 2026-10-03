using System.IO;
using System.Text.Json;
using LoadingAI.Protocol;
using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

public class ApprovalSummaryTests
{
    private static FakeRevitBridge Open() => new() { HasActiveDocument = true };

    private static ToolEntry Geo(string name, FakeRevitBridge bridge)
        => GeometryTools.Create(new FakePluginContext(bridge)).First(e => e.Definition.Function.Name == name);

    [Fact]
    public void ApprovalPayload_CarriesTheSummary_AndOmitsItWhenNull()
    {
        var with = FromHostMessage.Serialize(new FromHostMessage.ToolApproval(
            new FromHostMessage.ApprovalPayload("id", "delete_elements", "L3", "{}", "d", "Delete 3 elements")));
        Assert.Contains("\"summary\":\"Delete 3 elements\"", with);

        var without = FromHostMessage.Serialize(new FromHostMessage.ToolApproval(
            new FromHostMessage.ApprovalPayload("id", "t", "L3", "{}", "d")));
        Assert.DoesNotContain("summary", without);
    }

    [Fact]
    public async Task DeleteElements_NamesWhatTheSelectionContains()
    {
        var bridge = Open();
        bridge.Selection = new SelectionSummaryDto(25, new[] { "Walls", "Doors" }, Enumerable.Range(1, 25).Select(i => i.ToString()).ToArray());
        var text = await Geo("delete_elements", bridge).Describe!("{}");
        Assert.Contains("25 selected", text);
        Assert.Contains("Walls", text);
        Assert.Contains("Hosted elements", text);
    }

    [Fact]
    public async Task DeleteElements_ListsExplicitIdsByCategoryAndName()
    {
        var bridge = Open();
        bridge.NextElementDetails = new ElementDetailsDto("7", "Level 2", "Levels", "Level", "-", "-", Array.Empty<ParameterValueDto>(), 0, null);
        var text = await Geo("delete_elements", bridge).Describe!("""{"elementIds":["7"]}""");
        Assert.Contains("Delete 1 element", text);
        Assert.Contains("Levels \"Level 2\" (7)", text);
    }

    [Fact]
    public async Task MoveElements_StatesTheVectorInMillimetres()
    {
        var bridge = Open();
        bridge.Selection = new SelectionSummaryDto(2, new[] { "Walls" }, new[] { "1", "2" });
        var text = await Geo("move_elements", bridge).Describe!("""{"dx":500,"dy":0,"dz":-250}""");
        Assert.Contains("2", text);
        Assert.Contains("500", text);
        Assert.Contains("-250", text);
        Assert.Contains("mm", text);
    }

    [Fact]
    public async Task BatchSetParameter_ShowsMatchedAndTouched()
    {
        var bridge = Open();
        bridge.NextFind = new FindResultDto(1200, 0, null, Array.Empty<ElementSummaryDto>());
        var entry = ParamTools.Create(new FakePluginContext(bridge)).First(e => e.Definition.Function.Name == "batch_set_parameter");
        var text = await entry.Describe!("""{"category":"Walls","targetParam":"Comments","targetValue":"x","limit":200}""");
        Assert.Contains("200 of 1200", text);
        Assert.Contains("1000 are not touched", text);
    }

    [Fact]
    public void ChatSession_DescribesBeforeBothL3Gates()
    {
        var src = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "src", "LoadingAI", "Ui", "ChatSession.cs"));
        var method = src.Substring(src.IndexOf("public async Task<string> ExecuteToolAsync", StringComparison.Ordinal));

        var plainDescribe = method.IndexOf("var summary = await ToolGuards.DescribeAsync(entry, argsJson);", StringComparison.Ordinal);
        var web = method.IndexOf("AwaitL3WebAsync(entry, name, argsJson, summary)", StringComparison.Ordinal);
        Assert.True(plainDescribe >= 0 && web > plainDescribe);

        Assert.Contains("ConfirmL3NativeAsync(name, argsJson, summary)", method);
        Assert.Contains("ConfirmL3NativeAsync(name, argsJson, presetSummary)", method);
    }

    [Fact]
    public void ApprovalGate_PutsTheSummaryInBothDialogs()
    {
        var gate = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "src", "LoadingAI", "Ui", "ApprovalGate.cs"));
        Assert.Contains("AwaitL3WebAsync(ToolEntry entry, string name, string argsJson, string? summary = null)", gate);
        Assert.Contains("entry.Definition.Function.Description, summary)", gate);
        Assert.Contains("ConfirmL3NativeAsync(string name, string argsJson, string? summary = null)", gate);
        Assert.Contains("string.IsNullOrWhiteSpace(summary)", gate);
    }
}
