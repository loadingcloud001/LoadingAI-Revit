using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LoadingAI.Plugins.Core;

/// <summary>
/// Stage 1 plugin load gate: SHA-256 of DLL bytes, checked before Assembly.LoadFrom.
/// Allow if the file is under <see cref="DevPluginsDir"/>, or the hash is in the
/// host-owned AppData allowlist, or the hash is in the shipped set recorded at host build.
/// Empty/missing files are empty sets — they must not lock out shipped plugins.
/// <see cref="PluginMeta.Verified"/> is display-only and is not consulted.
/// </summary>
public sealed class PluginAllowlist
{
    public const string ShippedHashesFileName = "plugin-shipped-hashes.json";
    public const string AllowlistFileName = "plugin-allowlist.json";

    private static readonly object Gate = new();
    private static PluginAllowlist? _current;

    public string AllowlistPath { get; }
    public string ShippedSetPath { get; }
    public string DevPluginsDir { get; }

    public PluginAllowlist(string allowlistPath, string shippedSetPath, string devPluginsDir)
    {
        AllowlistPath = allowlistPath ?? throw new ArgumentNullException(nameof(allowlistPath));
        ShippedSetPath = shippedSetPath ?? throw new ArgumentNullException(nameof(shippedSetPath));
        DevPluginsDir = devPluginsDir ?? throw new ArgumentNullException(nameof(devPluginsDir));
    }

    public static PluginAllowlist ForRuntime()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LoadingAI");
        var hostDir = Path.GetDirectoryName(typeof(PluginLoader).Assembly.Location)!;
        return new PluginAllowlist(
            Path.Combine(appData, AllowlistFileName),
            Path.Combine(hostDir, ShippedHashesFileName),
            PluginLoader.DevPluginsDir);
    }

    /// <summary>Loader uses this. Tests inject a temp instance; do not point CI at live AppData.</summary>
    public static PluginAllowlist Current
    {
        get { lock (Gate) return _current ??= ForRuntime(); }
        set { lock (Gate) _current = value ?? throw new ArgumentNullException(nameof(value)); }
    }

    public bool IsAllowed(string dllPath, out string sha256, out string reason)
    {
        sha256 = "";
        reason = "";
        if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
        {
            reason = "DLL not found";
            return false;
        }

        var full = Path.GetFullPath(dllPath);
        sha256 = HashFile(full);

        if (!string.IsNullOrWhiteSpace(DevPluginsDir) && IsUnderDirectory(full, DevPluginsDir))
        {
            reason = "dev";
            return true;
        }

        var allowed = LoadHashes(ShippedSetPath);
        allowed.UnionWith(LoadHashes(AllowlistPath));
        if (allowed.Contains(sha256))
        {
            reason = "listed";
            return true;
        }

        reason = $"SHA-256 {sha256} is not in the shipped set or {AllowlistPath}";
        return false;
    }

    public static string HashFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var sha = SHA256.Create();
        return ToHex(sha.ComputeHash(bytes));
    }

    internal static bool IsUnderDirectory(string fullPath, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(fullPath);
        if (target.Equals(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase)) return true;
        return target.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    internal static HashSet<string> LoadHashes(string path)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return set;
        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return set;
            var items = JsonSerializer.Deserialize<string[]>(json);
            if (items == null) return set;
            foreach (var item in items)
            {
                if (!string.IsNullOrWhiteSpace(item)) set.Add(item.Trim());
            }
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        return set;
    }

    private static string ToHex(byte[] hash)
    {
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
