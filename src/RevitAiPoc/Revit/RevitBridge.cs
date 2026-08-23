using Autodesk.Revit.UI;

namespace RevitAiPoc.Revit;

public sealed class RevitBridge : IDisposable
{
    private readonly ExternalEvent _externalEvent;
    private readonly BridgeHandler _handler;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    public RevitBridge()
    {
        _handler = new BridgeHandler();
        _externalEvent = ExternalEvent.Create(_handler);
    }

    public async Task<T> RunAsync<T>(Func<UIApplication, T> func, CancellationToken ct = default)
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

        var raise = _externalEvent.Raise();
        if (raise == ExternalEventRequest.Denied || raise == ExternalEventRequest.TimedOut)
        {
            reg?.Dispose();
            throw new InvalidOperationException($"Revit ExternalEvent not accepted ({raise}). Close any open dialog and retry.");
        }

        using var timeoutCts = new CancellationTokenSource(DefaultTimeout);
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(DefaultTimeout, timeoutCts.Token));
        reg?.Dispose();
        if (completed != tcs.Task)
            throw new TimeoutException("Revit did not process the request in time. Close modal dialogs and retry.");
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
        private readonly Queue<Action<UIApplication>> _queue = new();
        private readonly object _lock = new();

        public void Enqueue(Action<UIApplication> action)
        {
            lock (_lock) _queue.Enqueue(action);
        }

        public void Execute(UIApplication app)
        {
            RevitContext.Set(app);

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

        public string GetName() => "RevitAiPoc Bridge";
    }
}
