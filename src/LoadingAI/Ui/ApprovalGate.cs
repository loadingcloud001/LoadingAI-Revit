using LoadingAI.Gate;
using LoadingAI.Protocol;
using LoadingAI.Revit;
using LoadingAI.Tools;

namespace LoadingAI.Ui;

internal enum ReviewOutcome
{
    Approved,
    /// <summary>The user pressed Deny, with or without a reason.</summary>
    Denied,
    /// <summary>Stop, Clear session, or the pane closed. Nobody said no; the run was ended.</summary>
    Cancelled,
    /// <summary>No answer in time. A timeout never approves.</summary>
    TimedOut
}

/// <summary>
/// The answer to one review card. <see cref="ArgsJson"/> is what to run: the model's arguments, the
/// arguments after the user edited them, or after the Run form was merged in.
/// </summary>
internal sealed record ReviewResult(ReviewOutcome Outcome, string ArgsJson, ApprovalScope Scope, string? Reason, bool Edited);

/// <summary>What the card should say that the tool and its arguments do not.</summary>
internal sealed record ReviewContext(bool TurnHasWrites);

/// <summary>
/// The single in-chat gate (ADR-0017). Every L2 or L3 call that the policy sends to the user gets one card in
/// the pane, answered in the pane. There is no native dialog behind it. The host owns the wait: only a message
/// from the pane completes it, a timeout cancels, and Stop cancels.
/// </summary>
internal sealed class ApprovalGate
{
    private sealed class PendingReview
    {
        private long _deadlineTicks;

        public PendingReview(string callId, ToolEntry entry, string name, string argsJson, TimeSpan idle, bool turnHasWrites)
        {
            CallId = callId;
            Entry = entry;
            Name = name;
            ArgsJson = argsJson;
            Idle = idle;
            TurnHasWrites = turnHasWrites;
            Plain = !ToolPresets.HasPresets(entry);
            Touch();
        }

        public string CallId { get; }
        public ToolEntry Entry { get; }
        public string Name { get; }
        public string ArgsJson { get; set; }
        public TimeSpan Idle { get; }
        public bool TurnHasWrites { get; }
        /// <summary>No Run form: the card is an Allow / Deny question about the arguments as given.</summary>
        public bool Plain { get; }
        public bool Edited { get; set; }
        public TaskCompletionSource<ReviewResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DateTime Deadline => new DateTime(Interlocked.Read(ref _deadlineTicks), DateTimeKind.Utc);

        /// <summary>Editing inputs or picking in the model is activity: the timeout starts again.</summary>
        public void Touch() => Interlocked.Exchange(ref _deadlineTicks, (DateTime.UtcNow + Idle).Ticks);

        public bool CanOfferAlways => GatePolicy.CanOfferAlways(Entry.Destructive, !Plain);
    }

    private readonly Dictionary<string, PendingReview> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly WebHostBridge _web;
    private readonly Action<Action> _onUi;

    public ApprovalGate(WebHostBridge web, Action<Action> onUi)
    {
        _web = web;
        _onUi = onUi;
    }

    /// <summary>The user answered a plain card. Scope beyond Once is honoured only where the card offered it.</summary>
    public void CompleteApproval(string toolCallId, bool approved, ApprovalScope scope, string? reason)
    {
        PendingReview? p;
        lock (_lock)
        {
            if (!_pending.TryGetValue(toolCallId, out p) || !p.Plain) return;
            _pending.Remove(toolCallId);
        }
        var granted = approved && p.CanOfferAlways ? scope : ApprovalScope.Once;
        p.Completion.TrySetResult(new ReviewResult(
            approved ? ReviewOutcome.Approved : ReviewOutcome.Denied,
            p.ArgsJson,
            granted,
            approved ? null : ReviewText.CleanReason(reason),
            p.Edited));
    }

    /// <summary>The user answered a card with a Run form. The form values are merged over the model's arguments.</summary>
    public void CompletePreset(string toolCallId, bool approved, string? valuesJson)
    {
        PendingReview? p;
        lock (_lock)
        {
            if (!_pending.TryGetValue(toolCallId, out p) || p.Plain) return;
            _pending.Remove(toolCallId);
        }
        var args = approved ? ToolPresets.Merge(p.ArgsJson, valuesJson) : p.ArgsJson;
        p.Completion.TrySetResult(new ReviewResult(
            approved ? ReviewOutcome.Approved : ReviewOutcome.Denied, args, ApprovalScope.Once, null, false));
    }

    /// <summary>Stop, Clear session, or the pane going away: every open card ends as cancelled.</summary>
    public void DenyAllPending()
    {
        List<PendingReview> all;
        lock (_lock)
        {
            all = _pending.Values.ToList();
            _pending.Clear();
        }
        foreach (var p in all)
            p.Completion.TrySetResult(new ReviewResult(ReviewOutcome.Cancelled, p.ArgsJson, ApprovalScope.Once, null, p.Edited));
    }

    /// <summary>
    /// The user edited a plain card's inputs. The new arguments are checked against the tool's schema; the card is
    /// rebuilt from them and posted again. The decision is still open: only Allow or Deny completes the wait.
    /// </summary>
    public async Task ApplyEditAsync(string toolCallId, string editedJson)
    {
        PendingReview? p;
        lock (_lock)
        {
            if (!_pending.TryGetValue(toolCallId, out p) || !p.Plain) return;
        }
        p.Touch();
        var schema = p.Entry.Definition.Function.Parameters;
        var coerced = ToolArgumentCoercer.Coerce(schema, editedJson);
        if (!ToolArgumentValidator.Validate(schema, coerced, out var normalized, out var error))
        {
            await PostReviewAsync(p, $"Those inputs were not accepted: {error}. The card still shows the last accepted inputs.");
            return;
        }
        p.ArgsJson = normalized;
        p.Edited = true;
        await PostReviewAsync(p, null);
    }

    public async Task<ReviewResult> ReviewAsync(string callId, ToolEntry entry, string name, string argsJson, ReviewContext ctx)
    {
        var plain = !ToolPresets.HasPresets(entry);
        // A plain question gets 180 s. A Run form can need a pick in the model, so it gets longer.
        var idle = plain ? TimeSpan.FromSeconds(ReviewText.ApprovalTimeoutSeconds) : TimeSpan.FromMinutes(15);
        var p = new PendingReview(callId, entry, name, argsJson, idle, ctx.TurnHasWrites);
        lock (_lock) _pending[callId] = p;
        try
        {
            // The review must be seen: a hidden pane is shown, never replaced by a dialog.
            await PaneVisibility.ShowIfHiddenAsync();
            await PostReviewAsync(p, null);
            return await WaitAsync(p);
        }
        finally
        {
            lock (_lock) _pending.Remove(callId);
        }
    }

    private static async Task<ReviewResult> WaitAsync(PendingReview p)
    {
        while (true)
        {
            var remaining = p.Deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return new ReviewResult(ReviewOutcome.TimedOut, p.ArgsJson, ApprovalScope.Once, null, p.Edited);
            using var cts = new CancellationTokenSource();
            var delay = Task.Delay(remaining, cts.Token);
            var done = await Task.WhenAny(p.Completion.Task, delay);
            if (done == p.Completion.Task)
            {
                cts.Cancel();
                return await p.Completion.Task;
            }
        }
    }

    private async Task PostReviewAsync(PendingReview p, string? extraWarning)
    {
        FromHostMessage.ReviewPayload payload;
        try
        {
            payload = await BuildPayloadAsync(p, extraWarning);
        }
        catch (Exception ex)
        {
            // A card that cannot be built still has to ask. Fall back to the raw arguments.
            HostLog.Write($"Review payload for {p.Name} failed: {ex.Message}");
            payload = new FromHostMessage.ReviewPayload(
                p.CallId, p.Name, p.Entry.Tier.ToString(), p.Entry.Destructive, p.Entry.Definition.Function.Description,
                $"Run {p.Name}", ReviewText.RowsFromArgs(p.ArgsJson).Select(r => new FromHostMessage.PreviewRowPayload(r.Label, r.Detail)).ToList(),
                Array.Empty<string>(), p.CanOfferAlways, p.CanOfferAlways, p.ArgsJson);
        }
        try { _onUi(() => _web.Post(new FromHostMessage.ToolReview(payload))); } catch { }
    }

    private static async Task<FromHostMessage.ReviewPayload> BuildPayloadAsync(PendingReview p, string? extraWarning)
    {
        var entry = p.Entry;
        var warnings = new List<string>();
        if (entry.Destructive)
            warnings.Add("This tool deletes or replaces elements, or writes a file. It always asks.");
        if (p.TurnHasWrites)
            warnings.Add("Deny also undoes the changes already made in this turn.");
        if (!string.IsNullOrWhiteSpace(extraWarning))
            warnings.Add(extraWarning!);

        string summary;
        List<FromHostMessage.PreviewRowPayload> rows;
        IReadOnlyList<FromHostMessage.PresetFieldPayload>? fields = null;
        object? values = null;

        if (p.Plain)
        {
            var described = await ToolGuards.DescribeAsync(entry, p.ArgsJson);
            summary = string.IsNullOrWhiteSpace(described) ? $"Run {p.Name}" : described!;
            // The tool's own summary says what will change; without one, show the arguments as rows.
            rows = string.IsNullOrWhiteSpace(described)
                ? ReviewText.RowsFromArgs(p.ArgsJson).Select(r => new FromHostMessage.PreviewRowPayload(r.Label, r.Detail)).ToList()
                : new List<FromHostMessage.PreviewRowPayload>();
        }
        else
        {
            fields = entry.Presets.Select(f => new FromHostMessage.PresetFieldPayload(
                f.Id,
                string.IsNullOrWhiteSpace(f.Label) ? f.Id : f.Label,
                ToolPresets.KindName(f.Kind),
                f.Required,
                f.Options,
                f.Category,
                f.Prompt,
                f.Warning)).ToList();
            values = ToolPresets.Prefill(entry.Presets, p.ArgsJson);
            // Preview is enrichment, never a precondition: TryBuildPreview swallows, so a value that cannot
            // be rendered still gets the user a form to correct (ADR-014).
            var preview = ToolPresets.TryBuildPreview(entry.Presets, p.ArgsJson);
            summary = string.IsNullOrWhiteSpace(preview?.Summary) ? $"Run {p.Name}" : preview!.Summary;
            rows = preview == null
                ? new List<FromHostMessage.PreviewRowPayload>()
                : preview.Rows.Select(r => new FromHostMessage.PreviewRowPayload(r.Label, r.Detail)).ToList();
            if (preview != null) warnings.AddRange(preview.Warnings);
        }

        var offer = p.CanOfferAlways;
        return new FromHostMessage.ReviewPayload(
            p.CallId,
            p.Name,
            entry.Tier.ToString(),
            entry.Destructive,
            entry.Definition.Function.Description,
            summary,
            rows,
            warnings,
            offer,
            offer,
            p.ArgsJson,
            fields,
            values,
            p.Edited ? true : null);
    }

    public async Task RunPresetPickAsync(string toolCallId, string fieldId)
    {
        PendingReview? pending;
        lock (_lock)
        {
            _pending.TryGetValue(toolCallId, out pending);
        }
        var entry = pending is { Plain: false } ? pending.Entry : null;
        var field = entry?.Presets.FirstOrDefault(f => string.Equals(f.Id, fieldId, StringComparison.OrdinalIgnoreCase));
        if (entry == null || field == null)
        {
            _web.Post(new FromHostMessage.PresetPickResult(toolCallId, fieldId, Ok: false, ErrorText: "No matching preset field."));
            return;
        }
        if (field.Kind is not PresetFieldKind.PickElements and not PresetFieldKind.PickBox)
        {
            _web.Post(new FromHostMessage.PresetPickResult(toolCallId, fieldId, Ok: false, ErrorText: "Field is not a pick."));
            return;
        }

        var bridge = App.Bridge;
        if (bridge == null)
        {
            _web.Post(new FromHostMessage.PresetPickResult(toolCallId, fieldId, Ok: false, ErrorText: "No Revit bridge."));
            return;
        }

        pending!.Touch();
        object result;
        try
        {
            if (field.Kind == PresetFieldKind.PickElements)
                result = await bridge.RunAsync(app => PresetPickHost.PickElements(app, field.Category, field.Prompt), PresetPickHost.PickTimeout);
            else
                result = await bridge.RunAsync(app => PresetPickHost.PickBox(app, field.Prompt), PresetPickHost.PickTimeout);
        }
        catch (Exception ex)
        {
            _web.Post(new FromHostMessage.PresetPickResult(toolCallId, fieldId, Ok: false, ErrorText: ex.Message));
            return;
        }

        pending.Touch();
        _web.Post(new FromHostMessage.PresetPickResult(toolCallId, fieldId, result));
    }
}
