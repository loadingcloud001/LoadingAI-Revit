using Autodesk.Revit.UI;

namespace RevitAiPoc.Revit;

/// <summary>
/// Marshals all Revit API calls through ExternalEvent.
/// </summary>
public sealed class RevitBridge : IDisposable
{
    private readonly ExternalEvent _externalEvent;
    private readonly BridgeHandler _handler;

    public RevitBridge()
    {
        _handler = new BridgeHandler();
        _externalEvent = ExternalEvent.Create(_handler);
    }

    public Task<T> RunAsync<T>(Func<UIApplication, T> func, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.CanBeCanceled)
            ct.Register(() => tcs.TrySetCanceled(ct));

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

        _externalEvent.Raise();
        return tcs.Task;
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
            // Ensure RevitContext is set when we have a UIApplication
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
                catch { /* per-item exception handled via TCS */ }
            }
        }

        public string GetName() => "RevitAiPoc Bridge";
    }
}
