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
    public void ApprovalGate_DescribesBeforeAskingTheUser()
    {
        var gate = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "src", "LoadingAI", "Ui", "ApprovalGate.cs"));
        var build = gate.Substring(gate.IndexOf("BuildPayloadAsync(PendingReview p", StringComparison.Ordinal));
        build = build.Substring(0, build.IndexOf("public async Task RunPresetPickAsync", StringComparison.Ordinal));

        var describe = build.IndexOf("ToolGuards.DescribeAsync(entry, p.ArgsJson)", StringComparison.Ordinal);
        var payload = build.IndexOf("return new FromHostMessage.ReviewPayload(", StringComparison.Ordinal);
        Assert.True(describe >= 0 && payload > describe);
        Assert.Matches(@"summary,\s+rows,", build);
    }

    [Fact]
    public void ApprovalGate_RebuildsTheSummaryFromTheEditedArguments()
    {
        var gate = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "src", "LoadingAI", "Ui", "ApprovalGate.cs"));
        var edit = gate.Substring(gate.IndexOf("public async Task ApplyEditAsync", StringComparison.Ordinal));
        edit = edit.Substring(0, edit.IndexOf("public async Task<ReviewResult> ReviewAsync", StringComparison.Ordinal));

        // New arguments are checked against the tool's schema first; the card is then rebuilt from them,
        // which describes the call again, and the wait stays open until Allow or Deny.
        Assert.Contains("ToolArgumentValidator.Validate(schema, coerced", edit);
        Assert.Contains("p.ArgsJson = normalized;", edit);
        Assert.Contains("PostReviewAsync(p, null)", edit);
        Assert.DoesNotContain("TrySetResult", edit);
    }
}
