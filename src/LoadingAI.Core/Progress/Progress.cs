using System.Threading;

namespace LoadingAI.Progress;

/// <summary>
/// Host-owned progress for the active AI run. Every tool (current and future)
/// is covered automatically; long-running tools may also call
/// <see cref="IProgressReporter.Report(string, int?)"/> for a determinate percent.
/// </summary>
public sealed record ProgressSnapshot(
    string Phase,
    string Label,
    string? Tool = null,
    int? Percent = null,
    int Current = 0,
    int Total = 0)
{
    public const string PhaseIdle = "idle";
    public const string PhaseThinking = "thinking";
    public const string PhaseTool = "tool";
    public const string PhaseComplete = "complete";
    public const string PhaseCancelled = "cancelled";
    public const string PhaseError = "error";

    public bool Indeterminate => Percent is null && (Phase == PhaseThinking || Phase == PhaseTool);

    public static ProgressSnapshot Idle { get; } = new(PhaseIdle, "");

    public static ProgressSnapshot Thinking(int round = 1)
        => new(PhaseThinking, round > 1 ? $"Thinking (round {round})…" : "Thinking…");

    public static ProgressSnapshot ForTool(string name, int index = 1, int total = 1, int? innerPercent = null)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "tool";
        if (index < 1) index = 1;
        if (total < 0) total = 0;
        int? pct = innerPercent;
        if (pct is int p) pct = Clamp(p, 0, 100);
        else if (total > 1) pct = Clamp((int)Math.Round(100.0 * (index - 1) / total), 0, 99);
        var label = total > 1 ? $"Running {name} ({index}/{total})" : $"Running {name}";
        return new ProgressSnapshot(PhaseTool, label, name, pct, index, total);
    }

    public static ProgressSnapshot Complete() => new(PhaseComplete, "Done", Percent: 100, Current: 1, Total: 1);
    public static ProgressSnapshot Cancelled() => new(PhaseCancelled, "Cancelled");
    public static ProgressSnapshot Error(string message)
        => new(PhaseError, string.IsNullOrWhiteSpace(message) ? "Error" : message);

    public ProgressSnapshot WithInner(string label, int? percent)
    {
        int? pct = percent is int p ? Clamp(p, 0, 100) : Percent;
        var text = string.IsNullOrWhiteSpace(label) ? Label : label;
        return this with { Label = text, Percent = pct };
    }

    internal static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
}

/// <summary>
/// Optional determinate updates from a tool. Host always reports at least
/// thinking / running / done around every function, so tools need not call this.
/// </summary>
public interface IProgressReporter
{
    void Report(string label, int? percent = null);
    void Report(ProgressSnapshot snapshot);
}

public sealed class ProgressReporter : IProgressReporter
{
    private static readonly AsyncLocal<IProgressReporter?> CurrentLocal = new();
    private static readonly IProgressReporter Null = new NullProgressReporter();
    private static IProgressReporter? _activeRun;

    /// <summary>
    /// Reporter for the active AI/tool scope, or a no-op if none.
    /// AsyncLocal first (flows with the agent task); falls back to the
    /// process-wide run reporter so Revit ExternalEvent threads can report too.
    /// </summary>
    public static IProgressReporter Current => CurrentLocal.Value ?? _activeRun ?? Null;

    public static IDisposable Push(IProgressReporter reporter)
    {
        var prevLocal = CurrentLocal.Value;
        var prevRun = _activeRun;
        var next = reporter ?? Null;
        CurrentLocal.Value = next;
        _activeRun = next;
        return new PopScope(prevLocal, prevRun);
    }

    private readonly Action<ProgressSnapshot> _sink;
    private readonly object _lock = new();
    private ProgressSnapshot _latest = ProgressSnapshot.Idle;

    public ProgressReporter(Action<ProgressSnapshot> sink)
    {
        _sink = sink ?? (_ => { });
    }

    public ProgressSnapshot Latest
    {
        get { lock (_lock) return _latest; }
    }

    public void Report(string label, int? percent = null)
    {
        ProgressSnapshot next;
        lock (_lock)
        {
            var baseSnap = _latest.Phase == ProgressSnapshot.PhaseIdle
                ? ProgressSnapshot.ForTool("tool")
                : _latest;
            next = baseSnap.WithInner(label ?? "", percent);
            _latest = next;
        }
        Publish(next);
    }

    public void Report(ProgressSnapshot snapshot)
    {
        if (snapshot is null) return;
        if (snapshot.Percent is int p)
            snapshot = snapshot with { Percent = ProgressSnapshot.Clamp(p, 0, 100) };
        lock (_lock) _latest = snapshot;
        Publish(snapshot);
    }

    private void Publish(ProgressSnapshot snapshot)
    {
        try { _sink(snapshot); } catch { }
    }

    private sealed class PopScope : IDisposable
    {
        private readonly IProgressReporter? _prevLocal;
        private readonly IProgressReporter? _prevRun;
        private int _disposed;
        public PopScope(IProgressReporter? prevLocal, IProgressReporter? prevRun)
        {
            _prevLocal = prevLocal;
            _prevRun = prevRun;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            CurrentLocal.Value = _prevLocal;
            _activeRun = _prevRun;
        }
    }

    private sealed class NullProgressReporter : IProgressReporter
    {
        public void Report(string label, int? percent = null) { }
        public void Report(ProgressSnapshot snapshot) { }
    }
}
