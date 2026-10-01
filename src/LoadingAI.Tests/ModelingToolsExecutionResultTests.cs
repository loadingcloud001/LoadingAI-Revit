using System.IO;
using System.Text.Json;
using LoadingAI.Llm;
using LoadingAI.Plugins.ModelingTools;
using LoadingAI.Revit;
using LoadingAI.Tests.Fakes;
using LoadingAI.Tools;

namespace LoadingAI.Tests;

/// <summary>
/// Pins the status and write certainty modeling-tools reports through
/// <see cref="ToolEntry.StructuredHandler"/>. Each tool is one atomic Revit transaction, so
/// <c>Ok</c> is the commit authority and every rejection the plugin makes before reaching the
/// Bridge must report <see cref="ToolWriteCertainty.NotCommitted"/> — under the legacy string
/// path those messages did not start with "Failed:" and were read as Succeeded + Committed.
/// </summary>
public class ModelingToolsExecutionResultTests
{
    [Fact]
    public async Task LoadFamilyWithoutADocumentDoesNotClaimCommit()
    {
        var path = WriteTempRfa();
        var (_, context) = Setup(hasDocument: false);

        var result = await CreateTool(context, "load_family")
            .ExecuteAsync(JsonSerializer.Serialize(new { path }));

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Please open a model first.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task LoadFamilyMissingFileDoesNotClaimCommit()
    {
        var path = MissingRfaPath();
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "load_family")
            .ExecuteAsync(JsonSerializer.Serialize(new { path }));

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.StartsWith("File not found:", result.Text);
        Assert.Empty(bridge.LoadFamilyCalls);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task LoadFamilyOutsideSandboxDoesNotClaimCommit()
    {
        var outside = Path.Combine(Path.GetTempPath(), "LoadingAI.Tests", "evil.rfa");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "rfa");
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "load_family")
            .ExecuteAsync(JsonSerializer.Serialize(new { path = outside }));

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.StartsWith("Failed:", result.Text);
        Assert.Empty(bridge.LoadFamilyCalls);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task LoadFamilySuccessCommitsOneChange()
    {
        var path = WriteTempRfa();
        var (bridge, context) = Setup();
        bridge.NextLoadFamilyResult = new CommandResultDto(true, "Loaded family \"Door\"", 1);

        var result = await CreateTool(context, "load_family")
            .ExecuteAsync(JsonSerializer.Serialize(new { path }));

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(1, result.ChangedCount);
        Assert.Single(bridge.LoadFamilyCalls);
        Assert.True(result.IsSafeToAssimilate);
    }

    // An empty required value can be rejected by ToolArgumentValidator before the handler
    // runs, so assert the safety property rather than which layer produced the message.
    [Fact]
    public async Task ChangeElementTypeMissingIdDoesNotClaimCommit()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "change_element_type")
            .ExecuteAsync("""{"elementId":""}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Contains("elementId", result.Text);
        Assert.Empty(bridge.ChangeElementTypeCalls);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task ChangeElementTypeFailureDoesNotClaimCommit()
    {
        var (bridge, context) = Setup();
        bridge.NextChangeElementTypeResult = new CommandResultDto(false, "Element 99 not found.");

        var result = await CreateTool(context, "change_element_type")
            .ExecuteAsync("""{"elementId":"99"}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Equal("Element 99 not found.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task ChangeElementTypeSuccessCommitsOneChange()
    {
        var (bridge, context) = Setup();
        bridge.NextChangeElementTypeResult = new CommandResultDto(true, "Changed element 99 type to \"Generic\"", 1);

        var result = await CreateTool(context, "change_element_type")
            .ExecuteAsync("""{"elementId":"99","typeName":"Generic"}""");

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(1, result.ChangedCount);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task CreateTopographyTooFewPointsDoesNotClaimCommit()
    {
        var (bridge, context) = Setup();

        var result = await CreateTool(context, "create_topography")
            .ExecuteAsync("""{"points":[[0,0,0],[1000,1000,0]]}""");

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.Contains("at least 3 points", result.Text);
        Assert.Empty(bridge.CreateTopographyCalls);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task CreateTopographyFailureDoesNotClaimCommit()
    {
        var (bridge, context) = Setup();
        bridge.NextCreateTopographyResult =
            new CommandResultDto(false, "No ToposolidType found in the model.");

        var result = await CreateTool(context, "create_topography")
            .ExecuteAsync(ThreePointArgs());

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.NotCommitted, result.WriteCertainty);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task CreateTopographySuccessCommitsOneChange()
    {
        var (bridge, context) = Setup();
        bridge.NextCreateTopographyResult =
            new CommandResultDto(true, "Created toposolid 801 from 3 points on Level 1.", 1);

        var result = await CreateTool(context, "create_topography").ExecuteAsync(ThreePointArgs());

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.Equal(1, result.ChangedCount);
        Assert.Single(bridge.CreateTopographyCalls);
        Assert.True(result.IsSafeToAssimilate);
    }

    private static string ThreePointArgs()
        => """{"points":[[0,0,0],[1000,0,50],[2000,1000,0]]}""";

    private static string WriteTempRfa()
    {
        Directory.CreateDirectory(FamilyPaths.Root);
        var path = Path.Combine(FamilyPaths.Root, $"modeling-{Guid.NewGuid():N}.rfa");
        File.WriteAllText(path, "rfa");
        return path;
    }

    private static string MissingRfaPath()
    {
        Directory.CreateDirectory(FamilyPaths.Root);
        return Path.Combine(FamilyPaths.Root, $"missing-{Guid.NewGuid():N}.rfa");
    }

    private static (FakeRevitBridge Bridge, FakePluginContext Context) Setup(bool hasDocument = true)
    {
        var bridge = new FakeRevitBridge { HasActiveDocument = hasDocument };
        return (bridge, new FakePluginContext(bridge));
    }

    private static ToolEntry CreateTool(FakePluginContext context, string name)
        => new ModelingToolsPlugin().CreateTools(context).Single(t => t.Definition.Function.Name == name);
}
