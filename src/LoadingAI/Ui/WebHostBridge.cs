using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using LoadingAI.Llm;
using LoadingAI.Progress;
using LoadingAI.Protocol;

namespace LoadingAI.Ui;

/// <summary>WebView2 post/receive. Empty catch on Post is intentional (pane may be gone).</summary>
internal sealed class WebHostBridge
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Func<CoreWebView2?> _web;

    public WebHostBridge(Func<CoreWebView2?> web) => _web = web;

    public void Post(object payload)
    {
        try
        {
            var json = payload is FromHostMessage msg
                ? FromHostMessage.Serialize(msg)
                : JsonSerializer.Serialize(payload, JsonOptions);
            _web()?.PostWebMessageAsJson(json);
        }
        catch { }
    }

    public string? TryRead(CoreWebView2WebMessageReceivedEventArgs e, out string? error)
    {
        error = null;
        string? json = null;
        try
        {
            try { json = e.TryGetWebMessageAsString(); } catch { json = null; }
            if (string.IsNullOrEmpty(json))
            {
                try { json = e.WebMessageAsJson; } catch { json = null; }
            }
            if (string.IsNullOrEmpty(json)) return null;
            if (json.Length > 2 && json[0] == '"' && json[json.Length - 1] == '"')
            {
                try { json = JsonSerializer.Deserialize<string>(json) ?? json; } catch { }
            }
            return json;
        }
        catch (Exception ex)
        {
            error = $"Bridge receive error: {ex.Message}";
            return null;
        }
    }

    public void PostQuota(QuotaStatus quota) => Post(FromHostMessage.QuotaOf(quota, DefaultLlm.Id));

    public void PostProgress(ProgressSnapshot snap) => Post(new FromHostMessage.Progress(snap));

    public static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "...";
}
