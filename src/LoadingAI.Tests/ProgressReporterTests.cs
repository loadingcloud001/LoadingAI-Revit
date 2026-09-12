using LoadingAI.Progress;

namespace LoadingAI.Tests;

public class ProgressReporterTests
{
    [Fact]
    public void Thinking_IsIndeterminate()
    {
        var snap = ProgressSnapshot.Thinking();
        Assert.Equal(ProgressSnapshot.PhaseThinking, snap.Phase);
        Assert.Null(snap.Percent);
        Assert.True(snap.Indeterminate);
        Assert.Equal("Thinking…", snap.Label);
        Assert.Equal("Thinking (round 2)…", ProgressSnapshot.Thinking(2).Label);
    }

    [Fact]
    public void ForTool_SingleTool_IsIndeterminate()
    {
        var snap = ProgressSnapshot.ForTool("hello");
        Assert.Equal(ProgressSnapshot.PhaseTool, snap.Phase);
        Assert.Equal("hello", snap.Tool);
        Assert.Null(snap.Percent);
        Assert.True(snap.Indeterminate);
        Assert.Equal("Running hello", snap.Label);
    }

    [Fact]
    public void ForTool_Batch_UsesCoarsePercent()
    {
        var first = ProgressSnapshot.ForTool("a", 1, 4);
        var last = ProgressSnapshot.ForTool("d", 4, 4);
        Assert.Equal(0, first.Percent);
        Assert.Equal(75, last.Percent);
        Assert.False(first.Indeterminate);
        Assert.Equal("Running a (1/4)", first.Label);
    }

    [Fact]
    public void ForTool_InnerPercent_Wins()
    {
        var snap = ProgressSnapshot.ForTool("export_ifc", 1, 1, 40);
        Assert.Equal(40, snap.Percent);
        Assert.False(snap.Indeterminate);
    }

    [Fact]
    public void Complete_Is100()
    {
        var snap = ProgressSnapshot.Complete();
        Assert.Equal(100, snap.Percent);
        Assert.False(snap.Indeterminate);
        Assert.Equal(ProgressSnapshot.PhaseComplete, snap.Phase);
    }

    [Fact]
    public void Clamp_Percent_And_InnerReport()
    {
        var received = new List<ProgressSnapshot>();
        var reporter = new ProgressReporter(received.Add);
        reporter.Report(ProgressSnapshot.ForTool("t", innerPercent: 150));
        Assert.Equal(100, received[^1].Percent);

        reporter.Report("halfway", 50);
        Assert.Equal("halfway", received[^1].Label);
        Assert.Equal(50, received[^1].Percent);
        Assert.Equal(ProgressSnapshot.PhaseTool, received[^1].Phase);
    }

    [Fact]
    public void Current_IsNoOp_WhenIdle()
    {
        ProgressReporter.Current.Report("ignored", 10);
        Assert.Same(ProgressReporter.Current, ProgressReporter.Current);
    }

    [Fact]
    public void Push_MakesCurrentReport()
    {
        var received = new List<ProgressSnapshot>();
        var reporter = new ProgressReporter(received.Add);
        using (ProgressReporter.Push(reporter))
        {
            ProgressReporter.Current.Report("inside", 20);
        }
        Assert.Single(received);
        Assert.Equal("inside", received[0].Label);
        ProgressReporter.Current.Report("after", 99);
        Assert.Single(received);
    }

    [Fact]
    public void Error_And_Cancelled_HavePhases()
    {
        Assert.Equal(ProgressSnapshot.PhaseCancelled, ProgressSnapshot.Cancelled().Phase);
        Assert.Equal("boom", ProgressSnapshot.Error("boom").Label);
        Assert.Equal("Error", ProgressSnapshot.Error("").Label);
    }
}
