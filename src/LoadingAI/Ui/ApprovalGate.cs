using Autodesk.Revit.UI;
using LoadingAI.Protocol;
using LoadingAI.Revit;
using LoadingAI.Tools;

namespace LoadingAI.Ui;

/// <summary>L3 web TCS + native TaskDialog + chat presets. Host owns every confirm.</summary>
internal sealed class ApprovalGate
{
    private readonly Dictionary<string, TaskCompletionSource<bool>> _pendingApprovals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskCompletionSource<(bool approved, string? valuesJson)>> _pendingPresets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ToolEntry> _pendingPresetTools = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _approvalLock = new();
    private readonly WebHostBridge _web;
    private readonly Action<Action> _onUi;

    public ApprovalGate(WebHostBridge web, Action<Action> onUi)
    {
        _web = web;
        _onUi = onUi;
    }

    public void CompleteApproval(string toolCallId, bool approved)
    {
        lock (_approvalLock)
        {
            if (_pendingApprovals.TryGetValue(toolCallId, out var tcs))
            {
                _pendingApprovals.Remove(toolCallId);
                try { tcs.TrySetResult(approved); } catch { }
            }
        }
    }

    public void CompletePreset(string toolCallId, bool approved, string? valuesJson)
    {
        lock (_approvalLock)
        {
            _pendingPresetTools.Remove(toolCallId);
            if (_pendingPresets.TryGetValue(toolCallId, out var tcs))
            {
                _pendingPresets.Remove(toolCallId);
                try { tcs.TrySetResult((approved, valuesJson)); } catch { }
            }
        }
    }

    public void DenyAllPending()
    {
        lock (_approvalLock)
        {
            foreach (var kv in _pendingApprovals.ToList())
            {
                _pendingApprovals.Remove(kv.Key);
                try { kv.Value.TrySetResult(false); } catch { }
            }
            foreach (var kv in _pendingPresets.ToList())
            {
                _pendingPresets.Remove(kv.Key);
                _pendingPresetTools.Remove(kv.Key);
                try { kv.Value.TrySetResult((false, null)); } catch { }
            }
        }
    }

    public async Task<(bool approved, string? valuesJson, string message)> AwaitPresetAsync(ToolEntry entry, string name, string argsJson)
    {
        var callId = Guid.NewGuid().ToString("N").Substring(0, 8);
        var tcs = new TaskCompletionSource<(bool approved, string? valuesJson)>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_approvalLock)
        {
            _pendingPresets[callId] = tcs;
            _pendingPresetTools[callId] = entry;
        }

        var fields = entry.Presets.Select(f => new FromHostMessage.PresetFieldPayload(
            f.Id,
            string.IsNullOrWhiteSpace(f.Label) ? f.Id : f.Label,
            ToolPresets.KindName(f.Kind),
            f.Required,
            f.Options,
            f.Category,
            f.Prompt)).ToList();
        var values = ToolPresets.Prefill(entry.Presets, argsJson);

        try
        {
            _onUi(() => _web.Post(new FromHostMessage.ToolPreset(new FromHostMessage.PresetPayload(
                callId, name, entry.Tier.ToString(), entry.Definition.Function.Description, fields, values))));
        }
        catch { }
        try { _onUi(() => _web.Post(new FromHostMessage.ToolCall($"{name}: {argsJson} (awaiting preset {callId})"))); } catch { }

        var timeout = Task.Delay(TimeSpan.FromMinutes(15));
        Task completed;
        try
        {
            completed = await Task.WhenAny(tcs.Task, timeout);
        }
        catch (OperationCanceledException)
        {
            CompletePreset(callId, false, null);
            return (false, null, "Cancelled by user.");
        }

        if (completed != tcs.Task)
        {
            CompletePreset(callId, false, null);
            return (false, null, "Cancelled: preset timeout.");
        }

        var (approved, valuesJson) = await tcs.Task;
        return approved
            ? (true, valuesJson, "")
            : (false, null, "Cancelled by user.");
    }

    public async Task<string?> AwaitL3WebAsync(ToolEntry entry, string name, string argsJson)
    {
        var callId = Guid.NewGuid().ToString("N").Substring(0, 8);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_approvalLock) _pendingApprovals[callId] = tcs;
        try { _onUi(() => _web.Post(new FromHostMessage.ToolApproval(new FromHostMessage.ApprovalPayload(callId, name, entry.Tier.ToString(), argsJson, entry.Definition.Function.Description)))); } catch { }
        try { _onUi(() => _web.Post(new FromHostMessage.ToolCall($"{name}: {argsJson} (awaiting approval {callId})"))); } catch { }
        var timeout = Task.Delay(TimeSpan.FromSeconds(180));
        var completed = await Task.WhenAny(tcs.Task, timeout);
        bool approved = false;
        if (completed == tcs.Task) approved = await tcs.Task;
        else
        {
            lock (_approvalLock) _pendingApprovals.Remove(callId);
            try { _onUi(() => _web.Post(new FromHostMessage.ToolResult($"{name} timed out waiting for approval (180s)"))); } catch { }
            return "Cancelled: approval timeout (180s).";
        }
        if (!approved)
        {
            try { _onUi(() => _web.Post(new FromHostMessage.ToolResult($"{name} denied by user"))); } catch { }
            return "Cancelled by user (denied).";
        }
        return null;
    }

    public async Task<bool> ConfirmL3NativeAsync(string name, string argsJson)
    {
        var raw = App.Bridge;
        if (raw == null) return false;
        return await raw.RunAsync(_ =>
        {
            var td = new TaskDialog("LoadingAI for Revit")
            {
                MainInstruction = $"Confirm L3: {name}?",
                MainContent = WebHostBridge.Truncate(argsJson, 400) + "\n\nUndoable transaction. Default No.",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No
            };
            return td.Show() == TaskDialogResult.Yes;
        });
    }

    public async Task RunPresetPickAsync(string toolCallId, string fieldId)
    {
        ToolEntry? entry = null;
        lock (_approvalLock)
        {
            _pendingPresetTools.TryGetValue(toolCallId, out entry);
        }
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

        _web.Post(new FromHostMessage.PresetPickResult(toolCallId, fieldId, result));
    }
}
