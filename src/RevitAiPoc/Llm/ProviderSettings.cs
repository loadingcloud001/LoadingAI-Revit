using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitAiPoc.Llm;

public sealed class ProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

public sealed class AppSettings
{
    public string ActiveProviderId { get; set; } = "deepseek";
    public List<ProviderConfig> Providers { get; set; } = new();
    /// <summary>Per-tool enabled map. Missing entry => use tool's EnabledByDefault (L3=false).</summary>
    public Dictionary<string, bool> Plugins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class ProviderSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitAiPoc", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (s != null && s.Providers.Count > 0)
                {
                    if (s.Plugins == null) s.Plugins = new(StringComparer.OrdinalIgnoreCase);
                    else s.Plugins = new Dictionary<string, bool>(s.Plugins, StringComparer.OrdinalIgnoreCase);
                    return s;
                }
            }
        }
        catch { }
        return CreateDefault();
    }

    public static bool IsToolEnabled(AppSettings s, string toolName, bool defaultEnabled)
        => s.Plugins.TryGetValue(toolName, out var v) ? v : defaultEnabled;

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(FilePath, json);
    }

    public static AppSettings CreateDefault()
    {
        var s = new AppSettings
        {
            ActiveProviderId = "deepseek",
            Providers = new List<ProviderConfig>
            {
                new() { Id = "deepseek", Name = "DeepSeek", BaseUrl = "https://api.deepseek.com", Model = "deepseek-chat", ApiKey = "" },
                new() { Id = "openai", Name = "OpenAI", BaseUrl = "https://api.openai.com/v1", Model = "gpt-4o-mini", ApiKey = "" },
                new() { Id = "ollama", Name = "Ollama", BaseUrl = "http://localhost:11434/v1", Model = "llama3.1", ApiKey = "ollama" }
            }
        };
        return s;
    }

    public static ProviderConfig? GetActive(AppSettings s)
        => s.Providers.FirstOrDefault(p => p.Id == s.ActiveProviderId) ?? s.Providers.FirstOrDefault();

    public static string NormalizeBaseUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        url = url.Trim().TrimEnd('/');
        // If path has no /v1, append /v1 for OpenAI-compatible. DeepSeek without /v1 is also ok, but appending is safe if missing.
        // Heuristic: if url ends with exactly domain or without /v1 suffix, add /v1 when no version segment.
        // We do: if url does not contain "/v1" and does not end with known non-v1 providers, append.
        // Simplest: if url contains "/v1" keep, else append "/v1" unless it's exactly "https://api.deepseek.com" which works both ways (keep as is, client will try /chat/completions).
        // To be safe, don't auto-append if user already specified versioned path like /v1, /v2, /api.
        // We will only append if last segment has no version pattern.
        // For POC, keep user's value as-is; normalization only trims slash and ensures no double slash when building URL.
        return url;
    }
}
