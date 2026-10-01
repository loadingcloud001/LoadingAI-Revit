using System.Text.Json;
using System.Text.Json.Serialization;
using LoadingAI.Llm;
using LoadingAI.Progress;

namespace LoadingAI.Protocol;

/// <summary>
/// Wire names for web → host. Must match <c>ToHost</c> in web/src/bridge.ts.
/// </summary>
public static class ToHostKind
{
    public const string Send = "send";
    public const string Clear = "clear";
    public const string Cancel = "cancel";
    public const string SaveSettings = "saveSettings";
    public const string RequestSettings = "requestSettings";
    public const string GetCatalog = "getCatalog";
    public const string TestProvider = "testProvider";
    public const string ListModels = "listModels";
    public const string ListPlugins = "listPlugins";
    public const string ApproveTool = "approveTool";
    public const string SubmitPreset = "submitPreset";
    public const string PresetPick = "presetPick";
    public const string SetPluginEnabled = "setPluginEnabled";
    public const string SetPluginGroupEnabled = "setPluginGroupEnabled";
    public const string SetPluginTierEnabled = "setPluginTierEnabled";
    public const string SaveToolSettings = "saveToolSettings";
    public const string RemovePlugin = "removePlugin";
    public const string ReloadPlugins = "reloadPlugins";
    public const string TrustPlugin = "trustPlugin";
    public const string ListCheckpoints = "listCheckpoints";
    public const string ResumeCheckpoint = "resumeCheckpoint";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Send, Clear, Cancel, SaveSettings, RequestSettings, GetCatalog, TestProvider, ListModels,
        ListPlugins, ApproveTool, SubmitPreset, PresetPick, SetPluginEnabled, SetPluginGroupEnabled,
        SetPluginTierEnabled, SaveToolSettings, RemovePlugin, ReloadPlugins, TrustPlugin, ListCheckpoints, ResumeCheckpoint
    };
}

/// <summary>
/// Wire names for host → web. Must match <c>FromHost</c> in web/src/bridge.ts.
/// Extra payload fields (e.g. plugins.loadIssues) are allowed.
/// </summary>
public static class FromHostKind
{
    public const string Status = "status";
    public const string Trace = "trace";
    public const string ToolCall = "tool_call";
    public const string ToolResult = "tool_result";
    public const string ToolApproval = "tool_approval";
    public const string ToolPreset = "tool_preset";
    public const string PresetPickResult = "presetPickResult";
    public const string Plan = "plan";
    public const string Todo = "todo";
    public const string Progress = "progress";
    public const string Context = "context";
    public const string Checkpoints = "checkpoints";
    public const string Reasoning = "reasoning";
    public const string Suggestions = "suggestions";
    public const string Assistant = "assistant";
    public const string Settings = "settings";
    public const string Catalog = "catalog";
    public const string TestResult = "testResult";
    public const string Models = "models";
    public const string Plugins = "plugins";
    public const string Quota = "quota";
    public const string History = "history";
    public const string Error = "error";
    public const string Delta = "delta";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Status, Trace, ToolCall, ToolResult, ToolApproval, ToolPreset, PresetPickResult, Plan, Todo,
        Progress, Context, Checkpoints, Reasoning, Suggestions, Assistant, Settings, Catalog, TestResult,
        Models, Plugins, Quota, History, Error, Delta
    };
}

/// <summary>
/// Parsed web → host message. HostPage dispatches on Kind; L3 / TransactionGroup stay in ApprovalGate / ChatSession.
/// </summary>
public abstract record ToHostMessage(string Kind)
{
    public const int MaxAttachments = 5;
    public const double MaxAttachmentBytes = 4.5 * 1024 * 1024;
    public const string OversizedImageError = "Image too large (>4MB) — please compress or crop.";

    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    public sealed record Send(
        string Text,
        string Mode,
        string Slash,
        IReadOnlyList<string> Mentions,
        IReadOnlyList<AttachmentDto> Attachments,
        IReadOnlyList<string> AttachmentErrors) : ToHostMessage(ToHostKind.Send);

    public sealed record Clear() : ToHostMessage(ToHostKind.Clear);
    public sealed record Cancel() : ToHostMessage(ToHostKind.Cancel);
    public sealed record SaveSettings(AppSettings? Settings) : ToHostMessage(ToHostKind.SaveSettings);
    public sealed record RequestSettings() : ToHostMessage(ToHostKind.RequestSettings);
    public sealed record GetCatalog() : ToHostMessage(ToHostKind.GetCatalog);
    public sealed record TestProvider(string? ProviderId, ProviderConfig? Provider) : ToHostMessage(ToHostKind.TestProvider);
    public sealed record ListModels(string? ProviderId, ProviderConfig? Provider) : ToHostMessage(ToHostKind.ListModels);
    public sealed record ListPlugins() : ToHostMessage(ToHostKind.ListPlugins);
    public sealed record ApproveTool(string ToolCallId, bool Approved) : ToHostMessage(ToHostKind.ApproveTool);
    public sealed record SubmitPreset(string ToolCallId, bool Approved, string? ValuesJson) : ToHostMessage(ToHostKind.SubmitPreset);
    public sealed record PresetPick(string ToolCallId, string FieldId) : ToHostMessage(ToHostKind.PresetPick);
    public sealed record SetPluginEnabled(string ToolName, bool Enabled) : ToHostMessage(ToHostKind.SetPluginEnabled);
    public sealed record SetPluginGroupEnabled(string PluginId, bool Enabled) : ToHostMessage(ToHostKind.SetPluginGroupEnabled);
    public sealed record SetPluginTierEnabled(string Tier, bool Enabled) : ToHostMessage(ToHostKind.SetPluginTierEnabled);
    public sealed record SaveToolSettings() : ToHostMessage(ToHostKind.SaveToolSettings);
    public sealed record RemovePlugin(string PluginId) : ToHostMessage(ToHostKind.RemovePlugin);
    public sealed record ReloadPlugins() : ToHostMessage(ToHostKind.ReloadPlugins);

    /// <summary>
    /// Stage 2 trust click. Carries the plugin identifier only — never a hash: the host re-hashes
    /// the DLL and compares it to the hash recorded by the scan that refused it.
    /// </summary>
    public sealed record TrustPlugin(string Folder, string Assembly) : ToHostMessage(ToHostKind.TrustPlugin);
    public sealed record ListCheckpoints() : ToHostMessage(ToHostKind.ListCheckpoints);
    public sealed record ResumeCheckpoint(string Id) : ToHostMessage(ToHostKind.ResumeCheckpoint);

    /// <summary>
    /// Unknown <c>type</c> yields <c>true</c> with <c>message == null</c> (ignored, same as the old switch).
    /// Missing/invalid JSON yields <c>false</c> and an error string for a Bridge error post.
    /// </summary>
    public static bool TryParse(string json, out ToHostMessage? message, out string? error)
    {
        message = null;
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            switch (type)
            {
                case ToHostKind.Send:
                    message = ParseSend(root);
                    return true;
                case ToHostKind.Clear:
                    message = new Clear();
                    return true;
                case ToHostKind.Cancel:
                    message = new Cancel();
                    return true;
                case ToHostKind.SaveSettings:
                    message = new SaveSettings(ReadSettings(root));
                    return true;
                case ToHostKind.RequestSettings:
                    message = new RequestSettings();
                    return true;
                case ToHostKind.GetCatalog:
                    message = new GetCatalog();
                    return true;
                case ToHostKind.TestProvider:
                    message = new TestProvider(ReadString(root, "providerId"), ReadProvider(root));
                    return true;
                case ToHostKind.ListModels:
                    message = new ListModels(ReadString(root, "providerId"), ReadProvider(root));
                    return true;
                case ToHostKind.ListPlugins:
                    message = new ListPlugins();
                    return true;
                case ToHostKind.ApproveTool:
                    message = new ApproveTool(ReadString(root, "toolCallId") ?? "", ReadTrue(root, "approved"));
                    return true;
                case ToHostKind.SubmitPreset:
                    message = new SubmitPreset(ReadString(root, "toolCallId") ?? "", ReadTrue(root, "approved"), ReadObjectRaw(root, "values"));
                    return true;
                case ToHostKind.PresetPick:
                    message = new PresetPick(ReadString(root, "toolCallId") ?? "", ReadString(root, "fieldId") ?? "");
                    return true;
                case ToHostKind.SetPluginEnabled:
                    message = new SetPluginEnabled(ReadString(root, "toolName") ?? "", ReadTrue(root, "enabled"));
                    return true;
                case ToHostKind.SetPluginGroupEnabled:
                    message = new SetPluginGroupEnabled(ReadString(root, "pluginId") ?? "", ReadTrue(root, "enabled"));
                    return true;
                case ToHostKind.SetPluginTierEnabled:
                    message = new SetPluginTierEnabled(ReadString(root, "tier") ?? "", ReadTrue(root, "enabled"));
                    return true;
                case ToHostKind.SaveToolSettings:
                    message = new SaveToolSettings();
                    return true;
                case ToHostKind.RemovePlugin:
                    message = new RemovePlugin(ReadString(root, "pluginId") ?? "");
                    return true;
                case ToHostKind.ReloadPlugins:
                    message = new ReloadPlugins();
                    return true;
                case ToHostKind.TrustPlugin:
                    message = new TrustPlugin(ReadString(root, "folder") ?? "", ReadString(root, "assembly") ?? "");
                    return true;
                case ToHostKind.ListCheckpoints:
                    message = new ListCheckpoints();
                    return true;
                case ToHostKind.ResumeCheckpoint:
                    message = new ResumeCheckpoint(ReadString(root, "id") ?? "");
                    return true;
                default:
                    return true;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static Send ParseSend(JsonElement root)
    {
        var text = ReadString(root, "text") ?? "";
        var mode = ReadString(root, "mode") ?? "auto";
        var slash = ReadString(root, "slash") ?? "";
        var mentions = new List<string>();
        if (root.TryGetProperty("mentions", out var men) && men.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in men.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString()))
                    mentions.Add(el.GetString()!.ToLowerInvariant());
            }
        }

        var attachments = new List<AttachmentDto>();
        var errors = new List<string>();
        if (root.TryGetProperty("attachments", out var atts) && atts.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in atts.EnumerateArray())
            {
                try
                {
                    var dataUrl = el.TryGetProperty("dataUrl", out var du) ? du.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(dataUrl)) continue;
                    if (!dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (dataUrl.Length * 0.75 > MaxAttachmentBytes)
                    {
                        errors.Add(OversizedImageError);
                        continue;
                    }
                    var id = el.TryGetProperty("id", out var ide) ? ide.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(id)) id = Guid.NewGuid().ToString("N").Substring(0, 8);
                    var name = el.TryGetProperty("name", out var ne) ? ne.GetString() ?? "image.png" : "image.png";
                    var mt = el.TryGetProperty("mediaType", out var me) ? me.GetString() ?? "image/png" : "image/png";
                    attachments.Add(new AttachmentDto(id, name, mt, dataUrl));
                    if (attachments.Count >= MaxAttachments) break;
                }
                catch { }
            }
        }

        return new Send(text, mode, slash, mentions, attachments, errors);
    }

    private static AppSettings? ReadSettings(JsonElement root)
        => JsonSerializer.Deserialize<AppSettings>(root.GetProperty("settings").GetRawText(), CaseInsensitive);

    private static ProviderConfig? ReadProvider(JsonElement root)
    {
        if (!root.TryGetProperty("provider", out var provEl) || provEl.ValueKind != JsonValueKind.Object)
            return null;
        return JsonSerializer.Deserialize<ProviderConfig>(provEl.GetRawText(), CaseInsensitive);
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) ? el.GetString() : null;

    private static bool ReadTrue(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;

    private static string? ReadObjectRaw(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Object ? el.GetRawText() : null;
}

/// <summary>
/// Host → web payload. Serialized with camelCase; extra fields (e.g. plugins.loadIssues) are allowed.
/// Must match <c>FromHost</c> in web/src/bridge.ts. Use runtime-type serialize — never
/// <c>JsonSerializer.Serialize&lt;FromHostMessage&gt;</c> or only <c>type</c> is written.
/// </summary>
public abstract record FromHostMessage(string Type)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(FromHostMessage message)
        => JsonSerializer.Serialize(message, message.GetType(), JsonOptions);

    public sealed record Status(string Text) : FromHostMessage(FromHostKind.Status);
    public sealed record Trace(string Text) : FromHostMessage(FromHostKind.Trace);
    public sealed record ToolCall(string Text) : FromHostMessage(FromHostKind.ToolCall);
    public sealed record ToolResult(string Text) : FromHostMessage(FromHostKind.ToolResult);
    public sealed record ToolApproval(ApprovalPayload Approval) : FromHostMessage(FromHostKind.ToolApproval);
    public sealed record ToolPreset(PresetPayload Preset) : FromHostMessage(FromHostKind.ToolPreset);
    public sealed record PresetPickResult(
        string ToolCallId,
        string FieldId,
        object? Result = null,
        bool? Ok = null,
        [property: JsonPropertyName("error")] string? ErrorText = null) : FromHostMessage(FromHostKind.PresetPickResult);
    public sealed record Plan([property: JsonPropertyName("plan")] PlanPayload Data) : FromHostMessage(FromHostKind.Plan);
    public sealed record Todo(IReadOnlyList<TodoItem> Todos) : FromHostMessage(FromHostKind.Todo);
    public sealed record Progress([property: JsonPropertyName("progress")] ProgressSnapshot Snapshot) : FromHostMessage(FromHostKind.Progress);
    public sealed record Context([property: JsonPropertyName("context")] ContextPayload Payload) : FromHostMessage(FromHostKind.Context);
    public sealed record Checkpoints([property: JsonPropertyName("checkpoints")] IReadOnlyList<CheckpointInfo> Items) : FromHostMessage(FromHostKind.Checkpoints);
    public sealed record Reasoning(string Text, bool? IsStreaming = null) : FromHostMessage(FromHostKind.Reasoning);
    public sealed record Suggestions([property: JsonPropertyName("suggestions")] IReadOnlyList<string> Items) : FromHostMessage(FromHostKind.Suggestions);
    public sealed record Assistant(string Text, object? Attachments = null) : FromHostMessage(FromHostKind.Assistant);
    public sealed record Settings([property: JsonPropertyName("settings")] object Payload) : FromHostMessage(FromHostKind.Settings);
    public sealed record Catalog(object Presets) : FromHostMessage(FromHostKind.Catalog);
    public sealed record TestResult(bool Ok, string Message, string? ProviderId = null, [property: JsonPropertyName("models")] IReadOnlyList<string>? ModelList = null)
        : FromHostMessage(FromHostKind.TestResult);
    public sealed record Models([property: JsonPropertyName("models")] IReadOnlyList<string> Names, string? ProviderId = null, [property: JsonPropertyName("error")] string? ErrorText = null)
        : FromHostMessage(FromHostKind.Models);
    public sealed record Plugins([property: JsonPropertyName("plugins")] object Items, object? LoadIssues = null) : FromHostMessage(FromHostKind.Plugins);
    public sealed record Quota([property: JsonPropertyName("quota")] QuotaPayload Payload) : FromHostMessage(FromHostKind.Quota);
    public sealed record History(IReadOnlyList<HistoryMessage> Messages) : FromHostMessage(FromHostKind.History);
    public sealed record Error(string Text) : FromHostMessage(FromHostKind.Error);
    public sealed record Delta(string Text) : FromHostMessage(FromHostKind.Delta);

    public sealed record ApprovalPayload(string ToolCallId, string ToolName, string Tier, string Args, string? Description);
    public sealed record PresetFieldPayload(
        string Id,
        string Label,
        string Kind,
        bool Required,
        IReadOnlyList<string>? Options,
        string? Category,
        string? Prompt);

    /// <summary>One line of the pre-run preview. Mirrored by web/src/bridge.ts.</summary>
    public sealed record PreviewRowPayload(string Label, string Detail);

    /// <summary>
    /// Read-only preview of what a preset tool call is set up to do. Derived from the tool's
    /// <c>PresetField</c> declarations plus the resolved arguments; not a gate (ADR-014).
    /// Absent means "no preview available", which the pane must tolerate.
    /// </summary>
    public sealed record PreviewPayload(
        string Summary,
        IReadOnlyList<PreviewRowPayload> Rows,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// The preset form shown before a tool runs. <see cref="Preview"/> is optional and appended
    /// last so existing positional call sites are not renumbered.
    /// </summary>
    public sealed record PresetPayload(
        string ToolCallId,
        string ToolName,
        string? Tier,
        string? Description,
        IReadOnlyList<PresetFieldPayload> Fields,
        object? Values,
        PreviewPayload? Preview = null);
    public sealed record PlanStepPayload(string Title, string Status = "pending");
    public sealed record PlanPayload(string Title, IReadOnlyList<PlanStepPayload> Steps, bool IsStreaming = false);
    public sealed record ContextPayload(
        int SelectionCount,
        string? SelectionPreview,
        string? ActiveView,
        int? VisibleCount,
        int? WarningCount,
        string? WarningPreview);
    public sealed record QuotaPayload(
        string? ProviderId,
        int Limit,
        int Used,
        int Remaining,
        string? ResetUtc,
        bool Exhausted,
        bool Ok,
        string? Message);
    public sealed record HistoryMessage(string Role, string Text, object? Attachments = null);

    public static Quota QuotaOf(QuotaStatus quota, string? providerId)
        => new(new QuotaPayload(
            providerId,
            quota.Limit,
            quota.Used,
            quota.Remaining,
            quota.ResetUtc,
            quota.Exhausted,
            quota.Ok,
            quota.Message));
}
