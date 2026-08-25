using System.Security.Cryptography;
using System.Text;
using LoadingAI.Llm;

namespace LoadingAI.Llm;

/// <summary>
/// Windows DPAPI-backed key protector used by the host application on
/// Windows. Registered into <see cref="ProviderSettings.KeyProtector"/>
/// at startup so API keys are encrypted at rest (Core's own fallback is a
/// no-op that keeps the netstandard2.0 build viable off-Windows).
/// </summary>
public sealed class WinKeyProtector : IKeyProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LoadingAI");

    public static readonly WinKeyProtector Instance = new();

    public string ProtectKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser);
        return "enc:" + Convert.ToBase64String(bytes);
    }

    public string UnprotectKey(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith("enc:", StringComparison.Ordinal)) return stored;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(4)), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }
}
