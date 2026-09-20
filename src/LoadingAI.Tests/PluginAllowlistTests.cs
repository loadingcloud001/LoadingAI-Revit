using System.IO;
using System.Security.Cryptography;
using System.Text;
using LoadingAI.Plugins.Core;
using Xunit;

namespace LoadingAI.Tests;

public class PluginAllowlistTests
{
    [Fact]
    public void UnknownRuntimeHash_IsRefused()
    {
        using var t = new TempAllowlist();
        var dll = t.WriteDll(t.RuntimeDir, "evil.dll", 1, 2, 3, 4);
        Assert.False(t.Allowlist.IsAllowed(dll, out var hash, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(hash));
        Assert.Contains("not in the shipped set", reason);
        Assert.DoesNotContain("verified", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DevPluginsDir_IsAllowedWithoutLists()
    {
        using var t = new TempAllowlist();
        var dll = t.WriteDll(Path.Combine(t.DevDir, "hello"), "LoadingAI.Plugins.Hello.dll", 9, 8, 7);
        Assert.True(t.Allowlist.IsAllowed(dll, out _, out var reason));
        Assert.Equal("dev", reason);
    }

    [Fact]
    public void ListedAllowlistHash_IsAllowed()
    {
        using var t = new TempAllowlist();
        var dll = t.WriteDll(t.RuntimeDir, "listed.dll", 5, 5, 5);
        var hash = PluginAllowlist.HashFile(dll);
        File.WriteAllText(t.AllowlistPath, $"[\"{hash}\"]");
        Assert.True(t.Allowlist.IsAllowed(dll, out var actual, out var reason));
        Assert.Equal(hash, actual);
        Assert.Equal("listed", reason);
    }

    [Fact]
    public void EmptyAllowlist_ShippedHash_IsAllowed()
    {
        using var t = new TempAllowlist();
        var dll = t.WriteDll(t.RuntimeDir, "core.dll", 10, 11, 12);
        var hash = PluginAllowlist.HashFile(dll);
        File.WriteAllText(t.ShippedSetPath, $"[\"{hash}\"]");
        Assert.True(t.Allowlist.IsAllowed(dll, out _, out var reason));
        Assert.Equal("listed", reason);
        Assert.False(File.Exists(t.AllowlistPath));
    }

    [Fact]
    public void MissingShippedSetAndAllowlist_Unknown_IsRefused()
    {
        using var t = new TempAllowlist();
        var dll = t.WriteDll(t.RuntimeDir, "unknown.dll", 1);
        Assert.False(File.Exists(t.AllowlistPath));
        Assert.False(File.Exists(t.ShippedSetPath));
        Assert.False(t.Allowlist.IsAllowed(dll, out _, out _));
    }

    [Fact]
    public void InvalidAllowlistJson_IsEmptySet_UnknownRefused()
    {
        using var t = new TempAllowlist();
        File.WriteAllText(t.AllowlistPath, "{not json");
        var dll = t.WriteDll(t.RuntimeDir, "badjson.dll", 2, 2);
        Assert.False(t.Allowlist.IsAllowed(dll, out _, out _));
    }

    [Fact]
    public void HashFile_IsSha256OfBytes_LowercaseHex()
    {
        using var t = new TempAllowlist();
        var payload = new byte[] { 1, 2, 3, 4 };
        var dll = t.WriteDll(t.RuntimeDir, "hash.dll", payload);
        using var sha = SHA256.Create();
        var expected = ToHex(sha.ComputeHash(payload));
        Assert.Equal(expected, PluginAllowlist.HashFile(dll));
        Assert.Equal(expected, expected.ToLowerInvariant());
    }

    [Fact]
    public void Source_DoesNotGateOnPluginMetaVerified()
    {
        var allow = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Plugins", "Core", "PluginAllowlist.cs"));
        var loader = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Plugins", "Core", "PluginLoader.cs"));
        Assert.DoesNotContain("meta.Verified", allow);
        Assert.DoesNotContain("if (meta.Verified", loader);
        Assert.Contains("PluginAllowlist.Current.IsAllowed", loader);
        var gateAt = loader.IndexOf("PluginAllowlist.Current.IsAllowed", StringComparison.Ordinal);
        var loadAt = loader.IndexOf("Assembly.LoadFrom(dll)", StringComparison.Ordinal);
        Assert.True(gateAt > 0 && loadAt > gateAt, "LoadOne must refuse before LoadFrom");
    }

    [Fact]
    public void Source_HostRecordsShippedHashesAfterBuildShippedPlugins()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "LoadingAI.csproj"));
        Assert.Contains("Name=\"RecordShippedHashes\" AfterTargets=\"BuildShippedPlugins\"", csproj);
        Assert.Contains("BeforeTargets=\"CopyAddin\"", csproj);
        Assert.Contains("plugin-shipped-hashes.json", csproj);
        Assert.Contains("record-shipped-plugin-hashes.ps1", csproj);
        Assert.Contains("Name=\"TrustAddin\" AfterTargets=\"CopyAddin\" Condition=\"'$(SkipCopyAddin)' != 'true' And '$(SkipTrustAddin)' != 'true'\"", csproj);
        Assert.Contains("$(TargetDir)plugin-shipped-hashes.json", csproj);
        Assert.Contains("Condition=\"Exists('$(TargetDir)plugin-shipped-hashes.json')\"", csproj);
    }

    private static string ToHex(byte[] hash)
    {
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LoadingAI.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find LoadingAI.sln from " + AppContext.BaseDirectory);
    }

    private sealed class TempAllowlist : IDisposable
    {
        private readonly string _root;
        public string RuntimeDir { get; }
        public string DevDir { get; }
        public string AllowlistPath { get; }
        public string ShippedSetPath { get; }
        public PluginAllowlist Allowlist { get; }

        public TempAllowlist()
        {
            _root = Path.Combine(Path.GetTempPath(), "loadingai-plugin-allowlist-" + Guid.NewGuid().ToString("N"));
            RuntimeDir = Path.Combine(_root, "runtime");
            DevDir = Path.Combine(_root, "dev");
            Directory.CreateDirectory(RuntimeDir);
            Directory.CreateDirectory(DevDir);
            AllowlistPath = Path.Combine(_root, PluginAllowlist.AllowlistFileName);
            ShippedSetPath = Path.Combine(_root, PluginAllowlist.ShippedHashesFileName);
            Allowlist = new PluginAllowlist(AllowlistPath, ShippedSetPath, DevDir);
        }

        public string WriteDll(string folder, string name, params byte[] bytes)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }
    }
}
