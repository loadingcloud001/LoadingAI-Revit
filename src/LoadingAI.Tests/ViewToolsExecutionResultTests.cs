using LoadingAI.Plugins.ViewTools;
using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

/// <summary>
/// Pins the status and write certainty view-tools reports through
/// <see cref="ToolEntry.StructuredHandler"/>. Five of the six tools are L3 and each creates
/// exactly one view, so a non-empty Id means a real write; <c>get_schedule_data</c> is the only
/// reader and must never claim a model change. <c>path_of_travel</c> is the one tool that can
/// genuinely half-succeed, so its per-pair failures have to survive as a partial result.
/// </summary>
public class ViewToolsExecutionResultTests
{
    private static readonly string SectionArgs = """{"x1":0,"y1":0,"x2":5000,"y2":0}""";
    private static readonly string PathArgs = """{"startX":0,"startY":0,"endX":5000,"endY":0}""";

    [Fact]
    public async Task CreateScheduleSuccessCommitsOneView()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "create_schedule")
            .ExecuteAsync("""{"category":"Walls"}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(1, result.ChangedCount);
        Assert.Single(bridge.CreateScheduleCalls);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task CreateScheduleWithoutADocumentDoesNotClaimCommit()
    {
        var (_, context) = Setup(hasDocument: false);

        var result = await CreateTool(context, "create_schedule")
            .ExecuteAsync("""{"category":"Walls"}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Please open a model first.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task CreateScheduleErrorDoesNotClaimCommit()
    {
        var (bridge, context) = Setup();
        bridge.NextCreateScheduleResult =
            new ViewRefDto("", "", "No category matches Walls.", "No category matches Walls.");

        var result = await CreateTool(context, "create_schedule")
            .ExecuteAsync("""{"category":"Walls"}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task CreateFloorPlanSuccessCommitsOneView()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "create_floor_plan")
            .ExecuteAsync("""{"levelName":"Level 1"}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(1, result.ChangedCount);
        Assert.Single(bridge.CreateFloorPlanCalls);
    }

    [Fact]
    public async Task Create3dViewSuccessCommitsOneView()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "create_3d_view").ExecuteAsync("{}");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Single(bridge.Create3DViewCalls);
    }

    [Fact]
    public async Task CreateSectionViewSuccessCommitsOneView()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "create_section_view").ExecuteAsync(SectionArgs);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(1, result.ChangedCount);
        Assert.Single(bridge.CreateSectionViewCalls);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task CreateSectionViewWithoutADocumentDoesNotClaimCommit()
    {
        var (_, context) = Setup(hasDocument: false);

        var result = await CreateTool(context, "create_section_view").ExecuteAsync(SectionArgs);

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Please open a model first.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    // The schema no longer advertises elementId, so the validator rejects it before the handler.
    // Either way nothing is created and the turn group must roll back.
    [Fact]
    public async Task CreateSectionViewRejectsElementIdWithoutTouchingTheModel()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "create_section_view")
            .ExecuteAsync("""{"x1":0,"y1":0,"x2":5000,"y2":0,"elementId":"123456"}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Contains("elementId", result.Text);
        Assert.Empty(bridge.CreateSectionViewCalls);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task CreateSectionViewRejectsCustomHeightWithoutTouchingTheModel()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "create_section_view")
            .ExecuteAsync("""{"x1":0,"y1":0,"x2":5000,"y2":0,"height":4000}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Empty(bridge.CreateSectionViewCalls);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task CreateSectionViewMissingCoordinatesDoesNotTouchTheModel()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "create_section_view")
            .ExecuteAsync("""{"x1":0,"y1":0}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Empty(bridge.CreateSectionViewCalls);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task GetScheduleDataIsAlwaysNoWrite()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "get_schedule_data")
            .ExecuteAsync("""{"scheduleId":"7001"}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.Equal(0, result.ChangedCount);
        Assert.Single(bridge.GetScheduleDataCalls);
    }

    [Fact]
    public async Task GetScheduleDataErrorIsNoWriteNotAWrite()
    {
        var (bridge, context) = Setup();
        bridge.NextScheduleData = new ScheduleDataDto("", "", 0, 0, 0, Array.Empty<IReadOnlyList<string>>(), "No schedule found.");

        var result = await CreateTool(context, "get_schedule_data")
            .ExecuteAsync("""{"scheduleId":"nope"}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
        Assert.Equal("No schedule found.", result.Text);
    }

    [Fact]
    public async Task PathOfTravelSuccessCommitsEveryPair()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "path_of_travel").ExecuteAsync(PathArgs);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(1, result.ChangedCount);
        Assert.Single(bridge.CreatePathOfTravelCalls);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task PathOfTravelPartialKeepsSolvedPathsAndNamesTheFailure()
    {
        var (bridge, context) = Setup();
        bridge.NextCreatePathOfTravelResult = new PathOfTravelResultDto(
            "Level 1", 3, 2, new[] { "9001", "9002" }, new[] { "#2:unreachable" });

        var result = await CreateTool(context, "path_of_travel")
            .ExecuteAsync("""{"starts":[[0,0],[10,10],[20,20]],"ends":[[1,1],[2,2],[3,3]]}""");

        Assert.Equal(ToolExecutionStatus.Partial, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(2, result.ChangedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains("#2:unreachable", result.Text);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task PathOfTravelAllPairsFailedDoesNotClaimCommit()
    {
        var (bridge, context) = Setup();
        bridge.NextCreatePathOfTravelResult = new PathOfTravelResultDto(
            "Level 1", 2, 0, Array.Empty<string>(), new[] { "#0:no view", "#1:no view" });

        var result = await CreateTool(context, "path_of_travel")
            .ExecuteAsync("""{"starts":[[0,0],[10,10]],"ends":[[1,1],[2,2]]}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal(0, result.ChangedCount);
        Assert.Equal(2, result.FailedCount);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task PathOfTravelWithoutADocumentDoesNotClaimCommit()
    {
        var (_, context) = Setup(hasDocument: false);

        var result = await CreateTool(context, "path_of_travel").ExecuteAsync(PathArgs);

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Please open a model first.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task PathOfTravelMismatchedPairCountsDoesNotTouchTheModel()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "path_of_travel")
            .ExecuteAsync("""{"starts":[[0,0],[10,10]],"ends":[[1,1]]}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Empty(bridge.CreatePathOfTravelCalls);
        Assert.False(result.IsSafeToAssimilate);
    }

    private static (FakeRevitBridge Bridge, FakePluginContext Context) Setup(bool hasDocument = true)
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = hasDocument };
        return (bridge, new FakePluginContext(bridge));
    }

    private static ToolEntry CreateTool(FakePluginContext context, string name)
        => new ViewToolsPlugin().CreateTools(context).Single(t => t.Definition.Function.Name == name);
}
