using LoadingAI.Revit;

namespace LoadingAI.Tests;

public class BridgeActionRegistryTests
{
    [Fact]
    public void TracksQueuedRunningAndSucceeded()
    {
        var registry = new BridgeActionRegistry();
        var id = registry.Begin();
        Assert.Equal(BridgeActionState.Queued, registry.Get(id).State);

        registry.MarkRunning(id);
        Assert.Equal(BridgeActionState.Running, registry.Get(id).State);

        registry.MarkSucceeded(id);
        var snapshot = registry.Get(id);
        Assert.Equal(BridgeActionState.Succeeded, snapshot.State);
        Assert.False(snapshot.WasAbandoned);
    }

    [Fact]
    public void TracksFailureMessage()
    {
        var registry = new BridgeActionRegistry();
        var id = registry.Begin();
        registry.MarkFailed(id, "boom");
        var snapshot = registry.Get(id);
        Assert.Equal(BridgeActionState.Failed, snapshot.State);
        Assert.Equal("boom", snapshot.Error);
    }

    [Fact]
    public void AbandonedActionCanBeCompletedButRetainsAbandonedFlag()
    {
        var registry = new BridgeActionRegistry();
        var id = registry.Begin();
        registry.MarkAbandoned(id, "timeout");
        Assert.Equal(BridgeActionState.Abandoned, registry.Get(id).State);

        registry.MarkSucceeded(id);
        var snapshot = registry.Get(id);
        Assert.Equal(BridgeActionState.Succeeded, snapshot.State);
        Assert.True(snapshot.WasAbandoned);
    }

    [Fact]
    public void CannotCancelActionThatStarted()
    {
        var registry = new BridgeActionRegistry();
        var id = registry.Begin();
        registry.MarkRunning(id);
        Assert.False(registry.TryCancelQueued(id, "caller cancelled"));
        Assert.Equal(BridgeActionState.Running, registry.Get(id).State);
    }

    [Fact]
    public void CancelsQueuedAction()
    {
        var registry = new BridgeActionRegistry();
        var id = registry.Begin();
        Assert.True(registry.TryCancelQueued(id, "raise denied"));
        var snapshot = registry.Get(id);
        Assert.Equal(BridgeActionState.Cancelled, snapshot.State);
        Assert.Equal("raise denied", snapshot.Error);
    }

    [Fact]
    public void PrunesOldTerminalActions()
    {
        var registry = new BridgeActionRegistry();
        var now = DateTime.UtcNow;
        var first = registry.Begin(now.AddHours(-25));
        registry.MarkSucceeded(first, now.AddHours(-25));
        var second = registry.Begin(now);
        registry.Prune(now);

        Assert.False(registry.TryGet(first, out _));
        Assert.True(registry.TryGet(second, out _));
    }

    [Fact]
    public void PrunesToBoundedCapacity()
    {
        var registry = new BridgeActionRegistry();
        for (var i = 0; i < 505; i++)
        {
            var id = registry.Begin();
            registry.MarkSucceeded(id);
        }

        Assert.True(registry.Count <= 500);
    }

}

