using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoadingAI.Llm;

public sealed class ProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ApiKind { get; set; } = "openai";
    public string Group { get; set; } = "custom";
    public string? Icon { get; set; }
}

public sealed class AppSettings
{
    public string ActiveProviderId { get; set; } = "deepseek";
    public List<ProviderConfig> Providers { get; set; } = new();
    public Dictionary<string, bool> Plugins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class ProviderCatalog
{
    public sealed record Preset(string Id, string Name, string BaseUrl, string Model, string ApiKind, string Group, string Icon, string Description);

    public static readonly IReadOnlyList<Preset> Presets = new List<Preset>
    {
        new("deepseek", "DeepSeek", "https://api.deepseek.com", "deepseek-chat", "openai", "cloud", "◆", "DeepSeek V3 / R1"),
        new("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4o-mini", "openai", "cloud", "◉", "GPT-4o / o1 / 5"),
        new("anthropic", "Anthropic", "https://api.anthropic.com", "claude-sonnet-4-20250514", "anthropic", "cloud", "◇", "Claude Sonnet / Opus via direct API"),
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "anthropic/claude-sonnet-4", "openai", "gateway", "◎", "300+ models gateway (Claude, Gemini, etc.)"),
        new("opencode", "OpenCode Zen", "https://opencode.ai/zen/go/v1", "muse-spark-1.2-contributor", "responses", "gateway", "⚡", "Muse / Grok / Luna via Responses API"),
        new("ollama", "Ollama", "http://localhost:11434/v1", "llama3.1", "openai", "local", "⬢", "Local models via Ollama"),
        new("lmstudio", "LM Studio", "http://localhost:1234/v1", "llama3.1", "openai", "local", "⬣", "Local models via LM Studio"),
        new("gemini", "Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.0-flash", "openai", "cloud", "✦", "Google Gemini via OpenAI compat"),
        new("custom", "Custom", "https://api.example.com/v1", "my-model", "openai", "custom", "＋", "Any OpenAI-compatible endpoint"),
    };

    public static Preset? Find(string id) => Presets.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    public static ProviderConfig ToConfig(Preset p, string? apiKey = null) => new()
    {
        Id = p.Id,
        Name = p.Name,
        BaseUrl = p.BaseUrl,
        Model = p.Model,
        ApiKey = apiKey ?? "",
        ApiKind = p.ApiKind,
        Group = p.Group,
        Icon = p.Icon
    };
}

public static class ProviderSettings
{
    public const string KeyMask = "••••••••";
    private const string EncPrefix = "enc:";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LoadingAI", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = ReadFile(FilePath);
                if (s != null) return DecryptInPlace(Normalize(s));
            }
        }
        catch { }

        var bak = FilePath + ".bak";
        try
        {
            if (File.Exists(bak))
            {
                var s = ReadFile(bak);
                if (s != null) return DecryptInPlace(Normalize(s));
            }
        }
        catch { }

        try
        {
            if (File.Exists(FilePath))
                File.Copy(FilePath, FilePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), true);
        }
        catch { }

        return CreateDefault();
    }

    private static AppSettings? ReadFile(string path)
    {
        var json = File.ReadAllText(path);
        var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        if (s == null || s.Providers.Count == 0) return null;
        return s;
    }

    public static AppSettings Normalize(AppSettings s)
    {
        s.Plugins = s.Plugins == null
            ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, bool>(s.Plugins, StringComparer.OrdinalIgnoreCase);
        // Backfill new fields for old settings
        foreach (var p in s.Providers)
        {
            if (string.IsNullOrWhiteSpace(p.ApiKind))
                p.ApiKind = InferApiKind(p);
            if (string.IsNullOrWhiteSpace(p.Group))
            {
                var preset = ProviderCatalog.Find(p.Id);
                p.Group = preset?.Group ?? InferGroup(p);
            }
            if (string.IsNullOrWhiteSpace(p.Icon))
                p.Icon = ProviderCatalog.Find(p.Id)?.Icon ?? DefaultIcon(p.Group);
        }
        // Ensure unique ids
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in s.Providers)
        {
            var orig = p.Id;
            var id = orig;
            int n = 2;
            while (!seen.Add(id))
                id = $"{orig}-{n++}";
            p.Id = id;
        }
        return s;
    }

    public static string InferApiKind(ProviderConfig p)
    {
        if (!string.IsNullOrWhiteSpace(p.ApiKind) && p.ApiKind != "openai")
            return p.ApiKind;
        // responses models use responses endpoint
        var m = p.Model?.Trim() ?? "";
        if (m.Equals("muse-spark-1.2-contributor", StringComparison.OrdinalIgnoreCase)
            || m.Equals("grok-4.5", StringComparison.OrdinalIgnoreCase)
            || m.Equals("gpt-5.6-luna", StringComparison.OrdinalIgnoreCase))
            return "responses";
        if (p.BaseUrl.Contains("anthropic.com", StringComparison.OrdinalIgnoreCase))
            return "anthropic";
        return "openai";
    }

    private static string InferGroup(ProviderConfig p)
    {
        if (p.BaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) || p.BaseUrl.Contains("127.0.0.1"))
            return "local";
        if (p.BaseUrl.Contains("openrouter", StringComparison.OrdinalIgnoreCase) || p.BaseUrl.Contains("opencode", StringComparison.OrdinalIgnoreCase))
            return "gateway";
        if (p.Id is "deepseek" or "openai" or "anthropic" or "gemini")
            return "cloud";
        return "custom";
    }

    private static string DefaultIcon(string group) => group switch
    {
        "cloud" => "☁",
        "local" => "⬢",
        "gateway" => "◎",
        _ => "＋"
    };

    public static bool IsToolEnabled(AppSettings s, string toolName, bool defaultEnabled)
        => s.Plugins.TryGetValue(toolName, out var v) ? v : defaultEnabled;

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var toWrite = EncryptCopy(settings);
        var json = JsonSerializer.Serialize(toWrite, JsonOptions);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(FilePath))
            File.Replace(tmp, FilePath, FilePath + ".bak");
        else
            File.Move(tmp, FilePath);
    }

    public static AppSettings CreateDefault()
    {
        return new AppSettings
        {
            ActiveProviderId = "deepseek",
            Providers = new List<ProviderConfig>
            {
                ProviderCatalog.ToConfig(ProviderCatalog.Presets[0]),
                ProviderCatalog.ToConfig(ProviderCatalog.Presets[1]),
                ProviderCatalog.ToConfig(ProviderCatalog.Presets[5], "ollama"),
                ProviderCatalog.ToConfig(ProviderCatalog.Presets[3]),
                ProviderCatalog.ToConfig(ProviderCatalog.Presets[4]),
            }
        };
    }

    public static ProviderConfig? GetActive(AppSettings s)
        => s.Providers.FirstOrDefault(p => p.Id == s.ActiveProviderId) ?? s.Providers.FirstOrDefault();

    public static bool IsMaskedKey(string? key)
        => !string.IsNullOrEmpty(key) && key.All(c => c is '•' or '*');

    public static AppSettings RedactForWeb(AppSettings s)
    {
        return new AppSettings
        {
            ActiveProviderId = s.ActiveProviderId,
            Plugins = new Dictionary<string, bool>(s.Plugins, StringComparer.OrdinalIgnoreCase),
            Providers = s.Providers.Select(p => new ProviderConfig
            {
                Id = p.Id,
                Name = p.Name,
                BaseUrl = p.BaseUrl,
                Model = p.Model,
                ApiKey = string.IsNullOrEmpty(p.ApiKey) ? "" : KeyMask,
                ApiKind = string.IsNullOrWhiteSpace(p.ApiKind) ? InferApiKind(p) : p.ApiKind,
                Group = p.Group,
                Icon = p.Icon
            }).ToList()
        };
    }

    public static AppSettings MergeFromWeb(AppSettings current, AppSettings incoming)
    {
        var merged = new AppSettings
        {
            ActiveProviderId = string.IsNullOrWhiteSpace(incoming.ActiveProviderId)
                ? current.ActiveProviderId
                : incoming.ActiveProviderId,
            Plugins = incoming.Plugins != null && incoming.Plugins.Count > 0
                ? new Dictionary<string, bool>(incoming.Plugins, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(current.Plugins, StringComparer.OrdinalIgnoreCase),
            Providers = new List<ProviderConfig>()
        };

        foreach (var inc in incoming.Providers ?? new())
        {
            var prev = current.Providers.FirstOrDefault(p => p.Id == inc.Id);
            var key = inc.ApiKey;
            if (string.IsNullOrWhiteSpace(key) || IsMaskedKey(key))
                key = prev?.ApiKey ?? "";
            merged.Providers.Add(new ProviderConfig
            {
                Id = inc.Id,
                Name = inc.Name,
                BaseUrl = inc.BaseUrl,
                Model = inc.Model,
                ApiKey = key,
                ApiKind = string.IsNullOrWhiteSpace(inc.ApiKind) ? (prev?.ApiKind ?? InferApiKind(inc)) : inc.ApiKind,
                Group = string.IsNullOrWhiteSpace(inc.Group) ? (prev?.Group ?? InferGroup(inc)) : inc.Group,
                Icon = string.IsNullOrWhiteSpace(inc.Icon) ? (prev?.Icon ?? ProviderCatalog.Find(inc.Id)?.Icon ?? DefaultIcon(inc.Group ?? InferGroup(inc))) : inc.Icon
            });
        }

        if (merged.Providers.Count == 0)
            merged.Providers = current.Providers.Select(Clone).ToList();

        return merged;
    }

    public static string ProtectKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (key.StartsWith(EncPrefix, StringComparison.Ordinal)) return key;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes("LoadingAI"), DataProtectionScope.CurrentUser);
        return EncPrefix + Convert.ToBase64String(bytes);
    }

    public static string UnprotectKey(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(EncPrefix, StringComparison.Ordinal)) return stored;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored[EncPrefix.Length..]), Encoding.UTF8.GetBytes("LoadingAI"), DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }

    private static AppSettings EncryptCopy(AppSettings s)
    {
        return new AppSettings
        {
            ActiveProviderId = s.ActiveProviderId,
            Plugins = new Dictionary<string, bool>(s.Plugins, StringComparer.OrdinalIgnoreCase),
            Providers = s.Providers.Select(p => new ProviderConfig
            {
                Id = p.Id, Name = p.Name, BaseUrl = p.BaseUrl, Model = p.Model, ApiKey = ProtectKey(p.ApiKey),
                ApiKind = p.ApiKind, Group = p.Group, Icon = p.Icon
            }).ToList()
        };
    }

    private static AppSettings DecryptInPlace(AppSettings s)
    {
        foreach (var p in s.Providers)
            p.ApiKey = UnprotectKey(p.ApiKey);
        return s;
    }

    private static ProviderConfig Clone(ProviderConfig p) => new()
    {
        Id = p.Id, Name = p.Name, BaseUrl = p.BaseUrl, Model = p.Model, ApiKey = p.ApiKey,
        ApiKind = p.ApiKind, Group = p.Group, Icon = p.Icon
    };
}
