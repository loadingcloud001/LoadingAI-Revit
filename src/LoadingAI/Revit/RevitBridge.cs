using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace LoadingAI.Revit;

public sealed class RevitBridge : IDisposable
{
    private readonly ExternalEvent _externalEvent;
    private readonly BridgeHandler _handler;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
    private volatile bool _executing;
    private volatile bool _hasActiveDocument;
    private readonly object _turnLock = new();
    private TransactionGroup? _turnGroup;
    private Document? _turnDocument;

    /// <summary>
    /// Last-known document-open flag, refreshed on the Revit UI thread.
    /// Safe to read from any thread — never pumps ExternalEvent.
    /// </summary>
    public bool HasActiveDocument => _hasActiveDocument;

    /// <summary>True when this turn has an open TransactionGroup on the UI thread.</summary>
    public bool HasActiveTurnGroup
    {
        get { lock (_turnLock) return _turnGroup != null; }
    }

    /// <summary>Call on the Revit UI thread when a live UIApplication is in hand.</summary>
    public void NoteUiApplication(UIApplication app)
        => _hasActiveDocument = app.ActiveUIDocument?.Document != null;

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

    /// <summary>
    /// Starts a turn-level TransactionGroup on the Revit UI thread if one is
    /// not already open for the active document. Nested Transactions from
    /// tools fold into one undo item when assimilated.
    /// </summary>
    public Task EnsureTurnGroupAsync(CancellationToken ct = default)
        => RunAsync(app =>
        {
            EnsureTurnGroupOnUiThread(app);
            return true;
        }, ct);

    /// <summary>
    /// Folds committed nested transactions into one undo item. No-op when
    /// no group was started or no writes landed.
    /// </summary>
    public Task AssimilateTurnGroupAsync(CancellationToken ct = default)
        => RunAsync(app =>
        {
            FinishTurnGroupOnUiThread(assimilate: true);
            return true;
        }, ct);

    /// <summary>
    /// Rolls the turn group back (Cancel / L3 deny after writes / tool
    /// exception). No-op when no group was started.
    /// </summary>
    public Task RollbackTurnGroupAsync(CancellationToken ct = default)
        => RunAsync(app =>
        {
            FinishTurnGroupOnUiThread(assimilate: false);
            return true;
        }, ct);

    private void EnsureTurnGroupOnUiThread(UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document;
        if (doc == null)
            throw new InvalidOperationException("Please open a model first.");

        lock (_turnLock)
        {
            if (_turnGroup != null && _turnGroup.HasStarted() && !_turnGroup.HasEnded())
            {
                if (!ReferenceEquals(_turnDocument, doc))
                    throw new InvalidOperationException("Turn TransactionGroup is bound to a different document.");
                return;
            }

            var group = new TransactionGroup(doc, "LoadingAI turn");
            group.Start();
            _turnGroup = group;
            _turnDocument = doc;
        }
    }

    private void FinishTurnGroupOnUiThread(bool assimilate)
    {
        TransactionGroup? group;
        lock (_turnLock)
        {
            group = _turnGroup;
            _turnGroup = null;
            _turnDocument = null;
        }

        if (group == null) return;
        try
        {
            if (!group.HasStarted() || group.HasEnded())
                return;

            if (assimilate)
                group.Assimilate();
            else
                group.RollBack();
        }
        catch (Exception ex)
        {
            DebugLog.Write("bridge.turn_group_finish_failed", new { assimilate, error = ex.Message });
            HostLog.Write($"Turn TransactionGroup finish failed (assimilate={assimilate}): {ex}");
            try { if (group.HasStarted() && !group.HasEnded()) group.RollBack(); } catch { }
            throw;
        }
        finally
        {
            try { group.Dispose(); } catch { }
        }
    }

    public void Dispose()
    {
        try
        {
            TransactionGroup? group;
            lock (_turnLock)
            {
                group = _turnGroup;
                _turnGroup = null;
                _turnDocument = null;
            }
            if (group != null && group.HasStarted() && !group.HasEnded())
            {
                try { group.RollBack(); } catch { }
            }
            try { group?.Dispose(); } catch { }
        }
        finally
        {
            _externalEvent.Dispose();
        }
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
            _owner.NoteUiApplication(app);
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
                    try
                    {
                        a(app);
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Write("bridge.execute_unhandled", new { error = ex.Message, type = ex.GetType().FullName });
                        HostLog.Write($"RevitBridge.Execute unhandled: {ex}");
                        throw;
                    }
                }
            }
            finally { _owner._executing = false; }
        }

        public string GetName() => "LoadingAI Bridge";
    }
}
