using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LoadingAI.Gate;

namespace LoadingAI.Llm;

public sealed class ProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ApiKind { get; set; } = "openai";
    public string Group { get; set; } = "custom";
    public string? Icon { get; set; }

    /// <summary>Null: guessed from the model name (<see cref="VisionSupport"/>). The user sets it in Settings → Models.</summary>
    public bool? SupportsImages { get; set; }
}

public sealed class AppSettings
{
    public string ActiveProviderId { get; set; } = "deepseek";
    public List<ProviderConfig> Providers { get; set; } = new();
    public Dictionary<string, bool> Plugins { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Kept for settings files and panes from before permission levels. It is true exactly when
    /// <see cref="PermissionLevel"/> is Bypass: the host skips every confirm and dismisses Revit failure
    /// dialogs during a LoadingAI turn. The model cannot set this. Default false.
    /// </summary>
    public bool AutoApprove { get; set; }

    /// <summary>
    /// readonly, plan, confirm, autoEdit or bypass (ADR-0017). Null means a file from before levels: read
    /// it through <see cref="ProviderSettings.LevelOf"/>. The model cannot set this.
    /// </summary>
    public string? PermissionLevel { get; set; }

    /// <summary>
    /// Tools the user chose "Always allow" for. Only the host adds to this (from a review card); the pane
    /// may remove entries. Destructive tools are ignored here at decision time.
    /// </summary>
    public List<string> AlwaysAllowTools { get; set; } = new();

    /// <summary>After a turn that changed the model, select the added and modified elements in Revit.</summary>
    public bool SelectChangedAfterTurn { get; set; } = true;

    /// <summary>Standing instructions for the agent (style, naming, units). Injected into the system prompt.</summary>
    public string? Instructions { get; set; }
}

/// <summary>Live copy of <see cref="AppSettings.AutoApprove"/> for call sites that do not hold settings.</summary>
public static class AutoApproveGate
{
    public static bool Enabled { get; set; }
}

public static class ProviderCatalog
{
    public sealed record Preset(string Id, string Name, string BaseUrl, string Model, string ApiKind, string Group, string Icon, string Description);

    public static readonly IReadOnlyList<Preset> Presets = new List<Preset>
    {
        new("deepseek", "DeepSeek", "https://api.deepseek.com", "deepseek-chat", "openai", "cloud", "◆", "DeepSeek V3 / R1"),
        new("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4o-mini", "openai", "cloud", "◉", "GPT-4o / o1 / 5"),
        new("anthropic", "Anthropic (via OpenRouter)", "https://openrouter.ai/api/v1", "anthropic/claude-sonnet-4", "openai", "gateway", "◇", "Claude via OpenRouter. Anthropic's direct /messages API is not supported — use OpenRouter for Claude."),
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
        EnsureBuiltin(s);
        DropUpstreamRows(s);
        s.PermissionLevel = LevelOf(s);
        s.AutoApprove = s.PermissionLevel == PermissionLevels.Bypass;
        s.AlwaysAllowTools = CleanNames(s.AlwaysAllowTools);
        s.Instructions = UserInstructions.Sanitize(s.Instructions);
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

    static void DropUpstreamRows(AppSettings s)
    {
        s.Providers.RemoveAll(DefaultLlm.ExposesUpstream);
        if (!s.Providers.Any(p => p.Id == s.ActiveProviderId))
            s.ActiveProviderId = DefaultLlm.Id;
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

    /// <summary>The permission level in force: the stored level, else Bypass for an old auto-approve file, else Ask before changes.</summary>
    public static string LevelOf(AppSettings s) => PermissionLevels.Resolve(s.PermissionLevel, s.AutoApprove);

    /// <summary>Names without blanks or duplicates, in first-seen order.</summary>
    internal static List<string> CleanNames(IEnumerable<string>? names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var n in names ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            var t = n.Trim();
            if (seen.Add(t)) list.Add(t);
        }
        return list;
    }

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
            ActiveProviderId = DefaultLlm.Id,
            PermissionLevel = PermissionLevels.Confirm,
            Providers = new List<ProviderConfig>
            {
                DefaultLlm.ForSettings(),
                ProviderCatalog.ToConfig(ProviderCatalog.Find("deepseek")!),
                ProviderCatalog.ToConfig(ProviderCatalog.Find("openai")!),
                ProviderCatalog.ToConfig(ProviderCatalog.Find("ollama")!, "ollama"),
                ProviderCatalog.ToConfig(ProviderCatalog.Find("openrouter")!),
                ProviderCatalog.ToConfig(ProviderCatalog.Find("opencode")!),
            }
        };
    }

    internal static AppSettings EnsureBuiltin(AppSettings s)
    {
        if (s.Providers == null) s.Providers = new List<ProviderConfig>();
        var existing = s.Providers.FirstOrDefault(p => DefaultLlm.IsBuiltin(p.Id));
        if (existing == null) s.Providers.Insert(0, DefaultLlm.ForSettings());
        else DefaultLlm.SanitizeStored(existing);

        var active = s.Providers.FirstOrDefault(p => p.Id == s.ActiveProviderId);
        if (active != null && !DefaultLlm.IsBuiltin(active.Id) && !string.IsNullOrWhiteSpace(active.ApiKey))
            return s;

        var missingActive = active == null
            || string.IsNullOrWhiteSpace(s.ActiveProviderId)
            || !s.Providers.Any(p => p.Id == s.ActiveProviderId);
        if (DefaultLlm.HasKey || missingActive)
            s.ActiveProviderId = DefaultLlm.Id;
        return s;
    }

    public static ProviderConfig? GetActive(AppSettings s)
        => s.Providers.FirstOrDefault(p => p.Id == s.ActiveProviderId) ?? s.Providers.FirstOrDefault();

    public static ProviderConfig? GetActiveForCall(AppSettings s)
    {
        var p = GetActive(s);
        return p == null ? null : DefaultLlm.BindForCall(p);
    }

    public static bool IsMaskedKey(string? key)
        => !string.IsNullOrEmpty(key) && key.All(c => c is '•' or '*');

    public static AppSettings RedactForWeb(AppSettings s)
    {
        return new AppSettings
        {
            ActiveProviderId = s.ActiveProviderId,
            AutoApprove = LevelOf(s) == PermissionLevels.Bypass,
            PermissionLevel = LevelOf(s),
            AlwaysAllowTools = CleanNames(s.AlwaysAllowTools),
            SelectChangedAfterTurn = s.SelectChangedAfterTurn,
            Instructions = s.Instructions,
            Plugins = new Dictionary<string, bool>(s.Plugins, StringComparer.OrdinalIgnoreCase),
            Providers = s.Providers.Where(p => !DefaultLlm.ExposesUpstream(p)).Select(p =>
            {
                if (DefaultLlm.IsBuiltin(p.Id)) return DefaultLlm.ForWeb(p);
                var copy = Clone(p);
                copy.ApiKey = string.IsNullOrEmpty(p.ApiKey) ? "" : KeyMask;
                if (string.IsNullOrWhiteSpace(copy.ApiKind)) copy.ApiKind = InferApiKind(p);
                return copy;
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
            PermissionLevel = MergeLevel(current, incoming),
            AlwaysAllowTools = ToolAllowlist.MergeAlways(current.AlwaysAllowTools, incoming.AlwaysAllowTools),
            SelectChangedAfterTurn = incoming.SelectChangedAfterTurn,
            Instructions = UserInstructions.Sanitize(incoming.Instructions),
            Plugins = new Dictionary<string, bool>(
                current.Plugins != null && current.Plugins.Count > 0
                    ? current.Plugins
                    : incoming.Plugins ?? new Dictionary<string, bool>(),
                StringComparer.OrdinalIgnoreCase),
            Providers = new List<ProviderConfig>()
        };

        foreach (var inc in incoming.Providers ?? new())
        {
            var prev = current.Providers.FirstOrDefault(p => p.Id == inc.Id);
            var key = inc.ApiKey;
            if (string.IsNullOrWhiteSpace(key) || IsMaskedKey(key))
                key = prev?.ApiKey ?? "";
            var cfg = Clone(inc);
            cfg.ApiKey = key;
            if (string.IsNullOrWhiteSpace(cfg.ApiKind))
                cfg.ApiKind = prev?.ApiKind ?? InferApiKind(inc);
            if (string.IsNullOrWhiteSpace(cfg.Group))
                cfg.Group = prev?.Group ?? InferGroup(inc);
            if (string.IsNullOrWhiteSpace(cfg.Icon))
                cfg.Icon = prev?.Icon ?? ProviderCatalog.Find(inc.Id)?.Icon ?? DefaultIcon(cfg.Group ?? InferGroup(inc));
            if (DefaultLlm.IsBuiltin(cfg.Id)) DefaultLlm.SanitizeStored(cfg);
            merged.Providers.Add(cfg);
        }

        if (merged.Providers.Count == 0)
            merged.Providers = current.Providers.Select(Clone).ToList();

        merged.AutoApprove = merged.PermissionLevel == PermissionLevels.Bypass;
        EnsureBuiltin(merged);
        DropUpstreamRows(merged);
        return merged;
    }

    /// <summary>
    /// A pane that sends a valid level wins. An older pane that only sends autoApprove turns Bypass on
    /// or off and leaves the other levels as they were.
    /// </summary>
    private static string MergeLevel(AppSettings current, AppSettings incoming)
    {
        if (PermissionLevels.IsValid(incoming.PermissionLevel)) return incoming.PermissionLevel!;
        var cur = LevelOf(current);
        if (incoming.AutoApprove) return PermissionLevels.Bypass;
        return cur == PermissionLevels.Bypass ? PermissionLevels.Confirm : cur;
    }

    public static IKeyProtector KeyProtector { get; set; } = new NoOpKeyProtector();

    public static string ProtectKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (key.StartsWith(EncPrefix, StringComparison.Ordinal)) return key;
        return KeyProtector.ProtectKey(key);
    }

    public static string UnprotectKey(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(EncPrefix, StringComparison.Ordinal)) return stored;
        return KeyProtector.UnprotectKey(stored);
    }

    private sealed class NoOpKeyProtector : IKeyProtector
    {
        public string ProtectKey(string? key) => EncPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(key ?? ""));
        public string UnprotectKey(string? stored) => stored.StartsWith(EncPrefix, StringComparison.Ordinal)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(stored.Substring(EncPrefix.Length)))
            : stored ?? "";
    }

    private static AppSettings EncryptCopy(AppSettings s)
    {
        return new AppSettings
        {
            ActiveProviderId = s.ActiveProviderId,
            AutoApprove = LevelOf(s) == PermissionLevels.Bypass,
            PermissionLevel = LevelOf(s),
            AlwaysAllowTools = CleanNames(s.AlwaysAllowTools),
            SelectChangedAfterTurn = s.SelectChangedAfterTurn,
            Instructions = s.Instructions,
            Plugins = new Dictionary<string, bool>(s.Plugins, StringComparer.OrdinalIgnoreCase),
            Providers = s.Providers.Select(p =>
            {
                var copy = Clone(p);
                copy.ApiKey = ProtectKey(p.ApiKey);
                if (DefaultLlm.IsBuiltin(copy.Id)) DefaultLlm.SanitizeStored(copy);
                return copy;
            }).ToList()
        };
    }

    private static AppSettings DecryptInPlace(AppSettings s)
    {
        foreach (var p in s.Providers)
            p.ApiKey = UnprotectKey(p.ApiKey);
        return s;
    }

    internal static ProviderConfig Clone(ProviderConfig p) => new()
    {
        Id = p.Id, Name = p.Name, BaseUrl = p.BaseUrl, Model = p.Model, ApiKey = p.ApiKey,
        ApiKind = p.ApiKind, Group = p.Group, Icon = p.Icon, SupportsImages = p.SupportsImages
    };
}
