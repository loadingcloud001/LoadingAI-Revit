using System.IO;
using System.Security.Cryptography;
using System.Text;
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
    public Dictionary<string, bool> Plugins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitAiPoc", "settings.json");

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
        return s;
    }

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
                new() { Id = "deepseek", Name = "DeepSeek", BaseUrl = "https://api.deepseek.com", Model = "deepseek-chat", ApiKey = "" },
                new() { Id = "openai", Name = "OpenAI", BaseUrl = "https://api.openai.com/v1", Model = "gpt-4o-mini", ApiKey = "" },
                new() { Id = "ollama", Name = "Ollama", BaseUrl = "http://localhost:11434/v1", Model = "llama3.1", ApiKey = "ollama" }
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
                ApiKey = string.IsNullOrEmpty(p.ApiKey) ? "" : KeyMask
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
                ApiKey = key
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
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes("RevitAiPoc"), DataProtectionScope.CurrentUser);
        return EncPrefix + Convert.ToBase64String(bytes);
    }

    public static string UnprotectKey(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(EncPrefix, StringComparison.Ordinal)) return stored;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored[EncPrefix.Length..]), Encoding.UTF8.GetBytes("RevitAiPoc"), DataProtectionScope.CurrentUser);
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
                Id = p.Id, Name = p.Name, BaseUrl = p.BaseUrl, Model = p.Model, ApiKey = ProtectKey(p.ApiKey)
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
        Id = p.Id, Name = p.Name, BaseUrl = p.BaseUrl, Model = p.Model, ApiKey = p.ApiKey
    };
}
