namespace LoadingAI.Tests;

using LoadingAI.Tools;

public class ToolExecutionResultTests
{
    private static ToolEntry Entry(ToolTier tier, Func<string, Task<string>> handler, Func<string, Task<ToolExecutionResult>>? structured = null)
        => new()
        {
            Tier = tier,
            Definition = new LoadingAI.Llm.ToolDefinition
            {
                Function = new LoadingAI.Llm.ToolFunction { Name = "tool" }
            },
            Handler = handler,
            StructuredHandler = structured
        };

    [Fact]
    public async Task LegacyReadHandlerIsSucceededNoWrite()
    {
        var entry = Entry(ToolTier.L1, _ => Task.FromResult("ok"));
        var result = await entry.ExecuteAsync("{}");
        Assert.Equal("ok", result.Text);
        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.NoWrite, result.WriteCertainty);
    }

    [Fact]
    public async Task LegacyWriteHandlerPreservesCompatibilityByAssumingCommit()
    {
        var entry = Entry(ToolTier.L3, _ => Task.FromResult("changed"));
        var result = await entry.ExecuteAsync("{}");
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task LegacyFailureTextDoesNotClaimCommit()
    {
        var entry = Entry(ToolTier.L3, _ => Task.FromResult("Failed: nothing changed"));
        var result = await entry.ExecuteAsync("{}");
        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.Unknown, result.WriteCertainty);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task EmptyLegacyResultIsFailure()
    {
        var entry = Entry(ToolTier.L1, _ => Task.FromResult(""));
        var result = await entry.ExecuteAsync("{}");
        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task CancelledLegacyResultIsCancelled()
    {
        var entry = Entry(ToolTier.L3, _ => Task.FromResult("Cancelled by user."));
        var result = await entry.ExecuteAsync("{}");
        Assert.Equal(ToolExecutionStatus.Cancelled, result.Status);
        Assert.False(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task StructuredHandlerWinsOverLegacyText()
    {
        var entry = Entry(
            ToolTier.L3,
            _ => Task.FromResult("legacy"),
            _ => Task.FromResult(ToolExecutionResult.Partial("created 1, failed 1", ToolWriteCertainty.Committed, 1, 1)));
        var result = await entry.ExecuteAsync("{}");
        Assert.Equal("created 1, failed 1", result.Text);
        Assert.Equal(ToolExecutionStatus.Partial, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task StructuredSuccessCanClaimCommit()
    {
        var entry = Entry(
            ToolTier.L3,
            _ => Task.FromResult("legacy"),
            _ => Task.FromResult(ToolExecutionResult.Succeeded("created", ToolWriteCertainty.Committed, 1)));
        var result = await entry.ExecuteAsync("{}");
        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ToolWriteCertainty.Committed, result.WriteCertainty);
        Assert.True(result.IsSafeToAssimilate);
    }

    [Fact]
    public async Task UnknownBridgeOutcomeIsPreserved()
    {
        var entry = Entry(
            ToolTier.L3,
            _ => Task.FromException<string>(new BridgeOperationUnknownException("action-1", "Outcome unknown.")));
        var result = await entry.ExecuteAsync("{}");
        Assert.Equal(ToolExecutionStatus.Unknown, result.Status);
        Assert.Equal(ToolWriteCertainty.Unknown, result.WriteCertainty);
        Assert.Contains("Outcome unknown.", result.Text);
        Assert.False(result.IsSafeToAssimilate);
    }
    [Fact]
    public async Task ThrownHandlerBecomesFailedUnknown()
    {
        var entry = Entry(ToolTier.L3, _ => Task.FromException<string>(new InvalidOperationException("boom")));
        var result = await entry.ExecuteAsync("{}");
        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal(ToolWriteCertainty.Unknown, result.WriteCertainty);
        Assert.Contains("boom", result.Text);
    }
}



