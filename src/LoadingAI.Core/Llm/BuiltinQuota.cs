using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoadingAI.Llm;

/// <summary>Result of a gateway quota call (or of a failed attempt to reach it).</summary>
public sealed class QuotaStatus
{
    [JsonPropertyName("limit")] public int Limit { get; set; } = BuiltinQuota.DailyLimit;
    [JsonPropertyName("used")] public int Used { get; set; }
    [JsonPropertyName("remaining")] public int Remaining { get; set; } = BuiltinQuota.DailyLimit;
    [JsonPropertyName("resetUtc")] public string? ResetUtc { get; set; }
    [JsonPropertyName("ticket")] public string? Ticket { get; set; }
    [JsonPropertyName("error")] public QuotaError? Error { get; set; }

    /// <summary>The gateway answered and (for a consume) granted the request.</summary>
    public bool Ok { get; set; } = true;

    /// <summary>The device has spent its allowance for the current UTC day.</summary>
    public bool Exhausted { get; set; }

    /// <summary>User-facing explanation when <see cref="Ok"/> is false.</summary>
    public string? Message { get; set; }

    public string Describe()
        => Ok
            ? $"{DefaultLlm.DisplayName} · {Remaining} / {Limit} free requests left today"
            : Message ?? $"{DefaultLlm.DisplayName} unavailable.";
}

public sealed class QuotaError
{
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
}

/// <summary>
/// Client state for the built-in provider's free allowance.
///
/// The gateway counts one unit per chat send, so the host consumes before
/// starting a run and stores the returned ticket; every upstream call inside
/// that run reuses the ticket instead of spending more quota. A ticket is kept
/// in memory only: it is never written to settings.json and never leaves the
/// host process for the web pane.
/// </summary>
public static class BuiltinQuota
{
    public const int DailyLimit = 100;
    public const string DeviceHeader = "X-LoadingAI-Device";
    public const string TicketHeader = "X-LoadingAI-Ticket";

    static readonly object Gate = new();
    static string? _deviceId;
    static string? _ticket;

    public static string DeviceId
    {
        get
        {
            lock (Gate)
            {
                return _deviceId ??= LoadOrCreateDeviceId();
            }
        }
    }

    public static string? Ticket
    {
        get { lock (Gate) return _ticket; }
    }

    public static string DeviceFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LoadingAI",
        "device.json");

    public static void SetTicket(string? ticket)
    {
        lock (Gate) _ticket = string.IsNullOrWhiteSpace(ticket) ? null : ticket;
    }

    public static void ClearTicket() => SetTicket(null);

    /// <summary>Text used when the gateway says the day's allowance is spent.</summary>
    public static string LimitMessage(QuotaStatus status)
        => status.Message ??
           $"{DefaultLlm.DisplayName} daily limit reached ({status.Limit} requests/day). Try again tomorrow or add your own API key in Settings.";

    public static bool IsValidDeviceId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id!.Length < 8 || id.Length > 64) return false;
        foreach (var c in id)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return false;
        }
        return true;
    }

    static string LoadOrCreateDeviceId()
    {
        try
        {
            if (File.Exists(DeviceFilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(DeviceFilePath));
                if (doc.RootElement.TryGetProperty("id", out var el) && el.ValueKind == JsonValueKind.String)
                {
                    var existing = el.GetString();
                    if (IsValidDeviceId(existing)) return existing!;
                }
            }
        }
        catch
        {
        }

        var created = Guid.NewGuid().ToString("N");
        try
        {
            var dir = Path.GetDirectoryName(DeviceFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(DeviceFilePath, "{\"id\":\"" + created + "\"}", Encoding.UTF8);
        }
        catch
        {
            // A read-only profile still gets a usable (per-process) identity.
        }
        return created;
    }
}
