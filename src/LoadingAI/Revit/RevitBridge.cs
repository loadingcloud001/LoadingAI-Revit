using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LoadingAI.Tools;

namespace LoadingAI.Revit;

public sealed class RevitBridge : IDisposable
{
    private readonly ExternalEvent _externalEvent;
    private readonly BridgeHandler _handler;
    private readonly BridgeActionRegistry _actions = new();
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Waits on a human (TaskDialog). Matches the 15-minute preset form timeout.</summary>
    public static readonly TimeSpan UserDialogTimeout = TimeSpan.FromMinutes(15);
    private volatile bool _executing;
    private volatile bool _hasActiveDocument;
    private volatile int _uiThreadId;
    /// <summary>True only while this thread is inside BridgeHandler.Execute (a valid Revit API context).</summary>
    [ThreadStatic] private static bool t_inHandler;
    /// <summary>Set by in-Revit test hosts that already own the API thread.</summary>
    private volatile bool _attached;
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
        _attached = true;
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
        var actionId = _actions.Begin();

        // Same thread is not enough: a WPF/WebView handler on the UI thread is outside API
        // context. Run inline only when nested inside Execute or on an attached test thread.
        if (_uiThreadId != 0
            && Environment.CurrentManagedThreadId == _uiThreadId
            && (t_inHandler || _attached)
            && RevitContext.UiApplication is { } current)
        {
            _actions.MarkRunning(actionId);
            try
            {
                var direct = func(current);
                _actions.MarkSucceeded(actionId);
                return direct;
            }
            catch (Exception ex)
            {
                _actions.MarkFailed(actionId, ex.Message);
                throw;
            }
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? reg = null;
        if (ct.CanBeCanceled)
        {
            reg = ct.Register(() =>
            {
                _actions.MarkAbandoned(actionId, "Caller cancelled before the Revit action completed.");
                tcs.TrySetCanceled(ct);
            });
        }

        _handler.Enqueue(app =>
        {
            if (_actions.TryGet(actionId, out var pending) && pending.State == BridgeActionState.Cancelled)
                return;

            _actions.MarkRunning(actionId);
            try
            {
                var result = func(app);
                _actions.MarkSucceeded(actionId);
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                _actions.MarkFailed(actionId, ex.Message);
                tcs.TrySetException(ex);
            }
        });

        var started = DateTime.UtcNow;
        var raise = _externalEvent.Raise();
        if (raise == ExternalEventRequest.Denied || raise == ExternalEventRequest.TimedOut)
        {
            if (!_actions.TryCancelQueued(actionId, $"Revit ExternalEvent not accepted ({raise})."))
            {
                _actions.MarkAbandoned(actionId, $"Revit ExternalEvent rejected after the action started ({raise}).");
                reg?.Dispose();
                throw new BridgeOperationUnknownException(actionId, "Revit rejected the event after the operation may have started. Do not retry automatically.");
            }

            reg?.Dispose();
            DebugLog.Write("bridge.raise_denied", new { raise = raise.ToString(), executing = _executing, actionId });
            throw new InvalidOperationException($"Revit ExternalEvent not accepted ({raise}). Close any open dialog and retry.");
        }

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
        reg?.Dispose();
        if (completed != tcs.Task)
        {
            _actions.MarkAbandoned(actionId, $"Caller stopped waiting after {timeout.TotalSeconds:0}s.");
            DebugLog.Write("bridge.timeout", new
            {
                timeoutSec = timeout.TotalSeconds,
                elapsedMs = (DateTime.UtcNow - started).TotalMilliseconds,
                executing = _executing,
                raise = raise.ToString(),
                actionId
            });
            throw new BridgeOperationUnknownException(
                actionId,
                _executing
                    ? $"Revit is still running the operation after {timeout.TotalSeconds:0}s. Do not retry until its outcome is known."
                    : "Revit did not complete the request in time. The queued operation may still run; do not retry automatically.");
        }

        try
        {
            return await tcs.Task;
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            _actions.MarkAbandoned(actionId, "Caller cancelled before the Revit action completed.");
            throw new BridgeOperationUnknownException(
                actionId,
                "Caller cancelled while the Revit action may still run. Do not retry automatically.",
                ex);
        }
    }

    public bool TryGetAction(string id, out BridgeActionSnapshot snapshot)
        => _actions.TryGet(id, out snapshot);

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
            try { if (group.HasStarted() && !group.HasEnded()) group.RollBack(); }
            catch (Exception rollbackEx) { HostLog.Write($"Turn TransactionGroup rollback after failed finish threw: {rollbackEx}"); }
            throw;
        }
        finally
        {
            try { group.Dispose(); }
            catch (Exception disposeEx) { HostLog.Write($"Turn TransactionGroup dispose failed: {disposeEx}"); }
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
                try { group.RollBack(); }
                catch (Exception rollbackEx) { HostLog.Write($"Turn TransactionGroup rollback on dispose threw: {rollbackEx}"); }
            }
            try { group?.Dispose(); }
            catch (Exception disposeEx) { HostLog.Write($"Turn TransactionGroup dispose on dispose threw: {disposeEx}"); }
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
            // IExternalEventHandler.Execute must not throw. Per-action failures
            // are completed by the RunAsync wrapper (TrySetException). An empty
            // catch here used to drop the rest of the batch so waiters sat until
            // DefaultTimeout (120s). Drain leftover actions instead.
            _owner._executing = true;
            t_inHandler = true;
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
                HostLog.Write($"RevitBridge.Execute failed: {ex}");
                DrainTo(local);
                InvokeAll(app, local);
            }
            finally
            {
                t_inHandler = false;
                _owner._executing = false;
            }
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
                    HostLog.Write($"RevitBridge action failed: {ex}");
                }
            }
        }

        public string GetName() => "LoadingAI Bridge";
    }
}
