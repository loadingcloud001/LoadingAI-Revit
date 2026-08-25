using Autodesk.Revit.UI;

namespace LoadingAI.Revit;

public sealed class RevitBridge : IDisposable
{
    private readonly ExternalEvent _externalEvent;
    private readonly BridgeHandler _handler;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
    private volatile bool _executing;

    public RevitBridge()
    {
        _handler = new BridgeHandler(this);
        _externalEvent = ExternalEvent.Create(_handler);
    }

    public Task<T> RunAsync<T>(Func<UIApplication, T> func, CancellationToken ct = default)
        => RunAsync(func, DefaultTimeout, ct);

    public async Task<T> RunAsync<T>(Func<UIApplication, T> func, TimeSpan timeout, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? reg = null;
        if (ct.CanBeCanceled)
            reg = ct.Register(() => tcs.TrySetCanceled(ct));

        _handler.Enqueue(app =>
        {
            try
            {
                var result = func(app);
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        var started = DateTime.UtcNow;
        var raise = _externalEvent.Raise();
        if (raise == ExternalEventRequest.Denied || raise == ExternalEventRequest.TimedOut)
        {
            reg?.Dispose();
            DebugLog.Write("bridge.raise_denied", new { raise = raise.ToString(), executing = _executing });
            throw new InvalidOperationException($"Revit ExternalEvent not accepted ({raise}). Close any open dialog and retry.");
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, timeoutCts.Token));
        reg?.Dispose();
        if (completed != tcs.Task)
        {
            DebugLog.Write("bridge.timeout", new
            {
                timeoutSec = timeout.TotalSeconds,
                elapsedMs = (DateTime.UtcNow - started).TotalMilliseconds,
                executing = _executing,
                raise = raise.ToString()
            });
            throw new TimeoutException(_executing
                ? $"Revit is still running the operation after {timeout.TotalSeconds:0}s (large exports can take several minutes). Wait for it to finish, then retry."
                : "Revit did not start the request in time. Close modal dialogs and retry.");
        }
        return await tcs.Task;
    }

    public Task RunAsync(Action<UIApplication> action, CancellationToken ct = default)
        => RunAsync(app => { action(app); return true; }, ct);

    public void Dispose()
    {
        _externalEvent.Dispose();
    }

    private sealed class BridgeHandler : IExternalEventHandler
    {
        private readonly RevitBridge _owner;
        private readonly Queue<Action<UIApplication>> _queue = new();
        private readonly object _lock = new();

        public BridgeHandler(RevitBridge owner) => _owner = owner;

        public void Enqueue(Action<UIApplication> action)
        {
            lock (_lock) _queue.Enqueue(action);
        }

        public void Execute(UIApplication app)
        {
            RevitContext.Set(app);
            _owner._executing = true;
            try
            {
                Queue<Action<UIApplication>> local;
                lock (_lock)
                {
                    local = new Queue<Action<UIApplication>>(_queue);
                    _queue.Clear();
                }
                while (local.Count > 0)
                {
                    var a = local.Dequeue();
                    try { a(app); }
                    catch { }
                }
            }
            finally { _owner._executing = false; }
        }

        public string GetName() => "LoadingAI Bridge";
    }
}
