using System.Text;

namespace LoadingAI.Llm;

/// <summary>
/// Built-in free provider. UI shows "LoadingAI"; the wire format is an
/// OpenAI-compatible upstream. The token is never stored in settings.json
/// and is never sent to the web pane.
/// </summary>
public static class DefaultLlm
{
    public const string Id = "loadingai";
    public const string DisplayName = "LoadingAI";
    public const string DisplayModel = "LoadingAI";
    public const string ApiBaseUrl = "https://api.minimaxi.com/v1";
    public const string ApiModel = "MiniMax-M3";
    public const string ApiKind = "openai";
    public const string Group = "cloud";
    public const string Icon = "✦";

    public static Func<string?> KeySource { get; set; } = DefaultLlmKey.TryGet;

    public static bool IsBuiltin(string? id)
        => Id.Equals(id, StringComparison.OrdinalIgnoreCase);

    public static bool HasKey
    {
        get
        {
            try
            {
                return !string.IsNullOrWhiteSpace(KeySource?.Invoke());
            }
            catch
            {
                return false;
            }
        }
    }

    public static ProviderConfig ForSettings() => new()
    {
        Id = Id,
        Name = DisplayName,
        BaseUrl = "",
        Model = DisplayModel,
        ApiKey = "",
        ApiKind = ApiKind,
        Group = Group,
        Icon = Icon
    };

    public static ProviderConfig BindForCall(ProviderConfig p)
    {
        if (!IsBuiltin(p.Id)) return p;
        return new ProviderConfig
        {
            Id = Id,
            Name = DisplayName,
            BaseUrl = ApiBaseUrl.TrimEnd('/'),
            Model = ApiModel,
            ApiKey = KeySource?.Invoke() ?? "",
            ApiKind = ApiKind,
            Group = Group,
            Icon = Icon
        };
    }

    public static ProviderConfig ForWeb(ProviderConfig p)
    {
        if (!IsBuiltin(p.Id)) return p;
        return new ProviderConfig
        {
            Id = Id,
            Name = DisplayName,
            BaseUrl = "",
            Model = DisplayModel,
            ApiKey = HasKey ? ProviderSettings.KeyMask : "",
            ApiKind = ApiKind,
            Group = Group,
            Icon = Icon
        };
    }

    public static void SanitizeStored(ProviderConfig p)
    {
        if (p == null || !IsBuiltin(p.Id)) return;
        p.Id = Id;
        p.Name = DisplayName;
        p.BaseUrl = "";
        p.Model = DisplayModel;
        p.ApiKey = "";
        p.ApiKind = ApiKind;
        p.Group = Group;
        if (string.IsNullOrWhiteSpace(p.Icon)) p.Icon = Icon;
    }
}

internal static partial class DefaultLlmKey
{
    internal const string Salt = "LoadingAI.DefaultLlm.v1";

    public static string? TryGet()
    {
        byte[]? packed = null;
        GetPacked(ref packed);
        if (packed == null || packed.Length == 0) return null;
        var salt = Encoding.UTF8.GetBytes(Salt);
        var bytes = new byte[packed.Length];
        for (int i = 0; i < packed.Length; i++)
            bytes[i] = (byte)(packed[i] ^ salt[i % salt.Length]);
        return Encoding.UTF8.GetString(bytes);
    }

    static partial void GetPacked(ref byte[]? packed);
}
