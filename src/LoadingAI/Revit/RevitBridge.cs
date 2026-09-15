using Autodesk.Revit.UI;

namespace LoadingAI.Revit;

public sealed class RevitBridge : IDisposable
{
    private readonly ExternalEvent _externalEvent;
    private readonly BridgeHandler _handler;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
    private volatile bool _executing;
    private volatile bool _hasActiveDocument;
    private volatile int _uiThreadId;

    /// <summary>
    /// Last-known document-open flag, refreshed on the Revit UI thread.
    /// Safe to read from any thread — never pumps ExternalEvent.
    /// </summary>
    public bool HasActiveDocument => _hasActiveDocument;

    /// <summary>Call on the Revit UI thread when a live UIApplication is in hand.</summary>
    public void NoteUiApplication(UIApplication app)
        => _hasActiveDocument = app.ActiveUIDocument?.Document != null;

    /// <summary>
    /// Bind this bridge to a UIApplication already on the Revit API thread.
    /// Later <see cref="RunAsync{T}(Func{UIApplication, T}, CancellationToken)"/>
    /// calls on that same thread run immediately (no ExternalEvent), which is
    /// required for in-Revit test hosts that already occupy the UI thread.
    /// Production ExternalEvent.Execute also records the UI thread so nested
    /// RunAsync from the same thread cannot deadlock.
    /// </summary>
    public void AttachCurrentThread(UIApplication app)
    {
        RevitContext.Set(app);
        _uiThreadId = Environment.CurrentManagedThreadId;
        NoteUiApplication(app);
    }

    public RevitBridge()
    {
        _handler = new BridgeHandler(this);
        _externalEvent = ExternalEvent.Create(_handler);
    }

    public Task<T> RunAsync<T>(Func<UIApplication, T> func, CancellationToken ct = default)
        => RunAsync(func, DefaultTimeout, ct);

    public async Task<T> RunAsync<T>(Func<UIApplication, T> func, TimeSpan timeout, CancellationToken ct = default)
    {
        if (_uiThreadId != 0
            && Environment.CurrentManagedThreadId == _uiThreadId
            && RevitContext.UiApplication is { } current)
        {
            try { return func(current); }
            catch (Exception ex) { return await Task.FromException<T>(ex); }
        }

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
            // IExternalEventHandler.Execute must not throw. Per-action failures
            // are completed by the RunAsync wrapper (TrySetException). An empty
            // catch here used to drop the rest of the batch so waiters sat until
            // DefaultTimeout (120s). Drain leftover actions instead.
            _owner._executing = true;
            var local = new Queue<Action<UIApplication>>();
            try
            {
                RevitContext.Set(app);
                _owner._uiThreadId = Environment.CurrentManagedThreadId;
                _owner.NoteUiApplication(app);
                DrainTo(local);
                InvokeAll(app, local);
            }
            catch (Exception ex)
            {
                DebugLog.Write("bridge.execute_failed", new
                {
                    error = ex.ToString(),
                    remaining = local.Count
                });
                DrainTo(local);
                InvokeAll(app, local);
            }
            finally { _owner._executing = false; }
        }

        private void DrainTo(Queue<Action<UIApplication>> dest)
        {
            lock (_lock)
            {
                while (_queue.Count > 0)
                    dest.Enqueue(_queue.Dequeue());
            }
        }

        private static void InvokeAll(UIApplication app, Queue<Action<UIApplication>> local)
        {
            while (local.Count > 0)
            {
                var a = local.Dequeue();
                try
                {
                    a(app);
                }
                catch (Exception ex)
                {
                    DebugLog.Write("bridge.action_failed", new { error = ex.ToString() });
                }
            }
        }

        public string GetName() => "LoadingAI Bridge";
    }
}
