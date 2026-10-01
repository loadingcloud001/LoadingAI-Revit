using System.Text;

namespace LoadingAI.Llm;

/// <summary>
/// Built-in free provider. UI shows "LoadingAI"; calls go to the public
/// gateway, which holds the upstream credential server-side and enforces the
/// free daily allowance. No upstream token is compiled into this assembly, and
/// no credential is ever stored in settings.json or sent to the web pane.
/// </summary>
public static class DefaultLlm
{
    public const string Id = "loadingai";
    public const string DisplayName = "LoadingAI";
    public const string DisplayModel = "LoadingAI";
    public const string ApiModel = "MiniMax-M3.1-Flash-Preview";
    public const string ApiKind = "openai";
    public const string Group = "cloud";
    public const string Icon = "✦";

    /// <summary>
    /// Shipped gateway endpoint: Cloudflare in front of the LoadingAI VM, which holds the upstream
    /// key. Bump this when the hostname changes.
    /// </summary>
    public const string PublicGatewayBaseUrl = "https://gateway.loadingtechnology.app/v1";

    /// <summary>Upstream called directly only by the development escape hatch below.</summary>
    public const string DirectApiBaseUrl = "https://api.minimaxi.com/v1";

    /// <summary>Environment override so a local gateway can be used without rebuilding.</summary>
    public const string GatewayUrlEnvVar = "LOADINGAI_GATEWAY_URL";

    /// <summary>Environment flag that calls the upstream directly with a locally supplied key.</summary>
    public const string DirectModeEnvVar = "LOADINGAI_DIRECT_MINIMAX";

    static string _gatewayBaseUrl = ResolveGatewayBaseUrl();

    public static Func<string?> KeySource { get; set; } = DefaultLlmKey.TryGet;

    /// <summary>Gateway base URL, including the version segment (…/v1).</summary>
    public static string GatewayBaseUrl
    {
        get => _gatewayBaseUrl;
        set => _gatewayBaseUrl = string.IsNullOrWhiteSpace(value) ? PublicGatewayBaseUrl : value!.Trim().TrimEnd('/');
    }

    /// <summary>
    /// Development only: talk to the upstream directly using a locally supplied key.
    /// Release builds never compile the key in, so this stays inert for users.
    /// </summary>
    public static bool DirectUpstreamEnabled
        => IsEnabled(Environment.GetEnvironmentVariable(DirectModeEnvVar)) && PackedKey() != null;

    /// <summary>The URL the add-in actually calls for the built-in provider.</summary>
    public static string ApiBaseUrl => (DirectUpstreamEnabled ? DirectApiBaseUrl : GatewayBaseUrl).TrimEnd('/');

    public static bool IsBuiltin(string? id)
        => Id.Equals(id, StringComparison.OrdinalIgnoreCase);

    /// <summary>Non-builtin row that would show the upstream vendor in Settings.</summary>
    public static bool ExposesUpstream(ProviderConfig? p)
    {
        if (p == null || IsBuiltin(p.Id)) return false;
        return NamesUpstream(p.Name) || NamesUpstream(p.Model) || NamesUpstream(p.BaseUrl);
    }

    static bool NamesUpstream(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        return s!.IndexOf("minimax", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>True when the built-in provider has somewhere to send calls.</summary>
    public static bool HasKey
    {
        get
        {
            try
            {
                if (DirectUpstreamEnabled) return true;
                return !string.IsNullOrWhiteSpace(GatewayBaseUrl);
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
        if (p == null || !IsBuiltin(p.Id))
            return Copy(p);
        var direct = DirectUpstreamEnabled;
        var c = ForSettings();
        c.BaseUrl = ApiBaseUrl;
        c.Model = ApiModel;
        // The gateway carries its own credential; only the dev escape hatch needs one here.
        c.ApiKey = direct ? PackedKey() ?? "" : "";
        return c;
    }

    public static ProviderConfig ForWeb(ProviderConfig p)
    {
        if (!IsBuiltin(p.Id)) return Copy(p);
        var w = ForSettings();
        w.ApiKey = HasKey ? ProviderSettings.KeyMask : "";
        return w;
    }

    public static void SanitizeStored(ProviderConfig p)
    {
        if (p == null || !IsBuiltin(p.Id)) return;
        var icon = p.Icon;
        var blank = ForSettings();
        p.Id = blank.Id;
        p.Name = blank.Name;
        p.BaseUrl = blank.BaseUrl;
        p.Model = blank.Model;
        p.ApiKey = blank.ApiKey;
        p.ApiKind = blank.ApiKind;
        p.Group = blank.Group;
        p.Icon = string.IsNullOrWhiteSpace(icon) ? blank.Icon : icon;
    }

    static string ResolveGatewayBaseUrl()
    {
        try
        {
            var configured = Environment.GetEnvironmentVariable(GatewayUrlEnvVar);
            if (!string.IsNullOrWhiteSpace(configured)) return configured!.Trim().TrimEnd('/');
        }
        catch
        {
        }
        return PublicGatewayBaseUrl;
    }

    static string? PackedKey()
    {
        try
        {
            var key = KeySource?.Invoke();
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch
        {
            return null;
        }
    }

    static bool IsEnabled(string? value)
        => value != null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    static ProviderConfig Copy(ProviderConfig? p)
    {
        if (p == null) return ForSettings();
        return ProviderSettings.Clone(p);
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
