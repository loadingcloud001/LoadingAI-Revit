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
/// <see cref="TryAddTrustedHash"/> is the stage 2 host-owned writer (ADR-0011) for the AppData list.
/// </summary>
public sealed class PluginAllowlist
{
    public const string ShippedHashesFileName = "plugin-shipped-hashes.json";
    public const string AllowlistFileName = "plugin-allowlist.json";

    private static readonly object Gate = new();

    /// <summary>
    /// Serialises <see cref="TryAddTrustedHash"/> only. Deliberately not <see cref="Gate"/>, which is
    /// released when <see cref="Current"/> evaluates and therefore cannot exclude a concurrent
    /// <see cref="IsAllowed"/> read; holding it across a write would stall every plugin scan on the
    /// filesystem for no exclusion benefit.
    /// </summary>
    private static readonly object WriteGate = new();
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
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

    /// <summary>
    /// Stage 2 host-owned writer (ADR-0011). Union-appends <paramref name="sha256"/> to
    /// <see cref="AllowlistPath"/> — one plain JSON <c>string[]</c>, the same file the gate reads.
    /// Existing entries survive (hand edits included), a repeat is a no-op, output is indented.
    /// An existing file that cannot be parsed aborts the write: <see cref="LoadHashes"/> reports an
    /// empty set for such a file, so overwriting it would silently un-trust every other plugin on it.
    /// Only the parent directory of <see cref="AllowlistPath"/> may be created — never <see cref="DevPluginsDir"/>.
    /// Writes are serialised on <see cref="WriteGate"/>, held across the whole read-modify-write.
    /// </summary>
    public bool TryAddTrustedHash(string sha256, out string error)
    {
        error = "";
        var hash = (sha256 ?? "").Trim().ToLowerInvariant();
        if (!IsSha256Hex(hash))
        {
            error = "expected 64 hex characters (SHA-256)";
            return false;
        }

        // WriteGate is held across the whole read-modify-write, read included: two callers that both
        // read before either writes each append to a stale list and one append is lost. The
        // File.Exists check below is inside the region too, or Exists-then-Replace is itself a race.
        lock (WriteGate)
        {
            List<string> entries;
            try
            {
                entries = ReadEntriesForWrite(AllowlistPath);
            }
            catch (Exception ex)
            {
                error = $"refusing to overwrite unreadable {AllowlistPath}: {ex.Message}";
                return false;
            }

            if (entries.Contains(hash, StringComparer.OrdinalIgnoreCase)) return true;

            entries.Add(hash);
            var dir = Path.GetDirectoryName(Path.GetFullPath(AllowlistPath)) ?? ".";
            var temp = Path.Combine(dir, $".{Path.GetFileName(AllowlistPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(temp, JsonSerializer.Serialize(entries, IndentedJson) + Environment.NewLine);
                // File.Move(source, destination, overwrite) is Core-only; Revit 2024 builds net48.
                if (File.Exists(AllowlistPath)) File.Replace(temp, AllowlistPath, null);
                else File.Move(temp, AllowlistPath);
            }
            catch (Exception ex)
            {
                error = $"refusing to overwrite {AllowlistPath}: {ex.Message}";
                return false;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }

            return true;
        }
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

    internal static bool IsSha256Hex(string value)
    {
        if (value.Length != 64) return false;
        foreach (var c in value)
        {
            if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')) continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Read for the writer only. Missing or empty file means "no entries yet"; a file that exists
    /// but cannot be parsed throws, so the caller aborts instead of replacing it with a short list.
    /// </summary>
    private static List<string> ReadEntriesForWrite(string path)
    {
        var entries = new List<string>();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return entries;

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json)) return entries;

        var items = JsonSerializer.Deserialize<string[]>(json)
                    ?? throw new InvalidDataException("expected a JSON array of SHA-256 strings");
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            var value = item.Trim();
            if (!entries.Contains(value, StringComparer.OrdinalIgnoreCase)) entries.Add(value);
        }
        return entries;
    }

    private static string ToHex(byte[] hash)
    {
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
