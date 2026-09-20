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

    [Fact]
    public void TryAddTrustedHash_CreatesFileWhenMissing_ThenThatDllIsAllowed()
    {
        using var t = new TempAllowlist();
        var dll = t.WriteDll(t.RuntimeDir, "trusted.dll", 3, 1, 4);
        var other = t.WriteDll(t.RuntimeDir, "still-unknown.dll", 3, 1, 5);
        var hash = PluginAllowlist.HashFile(dll);
        Assert.False(File.Exists(t.AllowlistPath));
        Assert.False(t.Allowlist.IsAllowed(dll, out _, out _));

        Assert.True(t.Allowlist.TryAddTrustedHash(hash, out var error), error);
        Assert.Equal("", error);
        Assert.True(File.Exists(t.AllowlistPath));
        Assert.True(t.Allowlist.IsAllowed(dll, out var actual, out var reason));
        Assert.Equal(hash, actual);
        Assert.Equal("listed", reason);
        Assert.False(t.Allowlist.IsAllowed(other, out _, out _));
    }

    [Fact]
    public void TryAddTrustedHash_AppendsUnion_PreservesExistingEntries_Dedups()
    {
        using var t = new TempAllowlist();
        var first = t.WriteDll(t.RuntimeDir, "first.dll", 1, 1, 1);
        var second = t.WriteDll(t.RuntimeDir, "second.dll", 2, 2, 2);
        var handEdited = new string('b', 64);
        File.WriteAllText(t.AllowlistPath, $"[\"{PluginAllowlist.HashFile(first)}\",\"{handEdited}\"]");

        var added = PluginAllowlist.HashFile(second);
        Assert.True(t.Allowlist.TryAddTrustedHash(added, out var error), error);
        Assert.True(t.Allowlist.TryAddTrustedHash(added, out error), error);

        var json = File.ReadAllText(t.AllowlistPath);
        Assert.Contains(PluginAllowlist.HashFile(first), json);
        Assert.Contains(handEdited, json);
        Assert.Equal(1, json.Split(new[] { added }, StringSplitOptions.None).Length - 1);
        Assert.Contains("\n", json);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(t.AllowlistPath)!, "*.tmp"));

        Assert.True(t.Allowlist.IsAllowed(first, out _, out _));
        Assert.True(t.Allowlist.IsAllowed(second, out _, out _));
    }

    [Fact]
    public void TryAddTrustedHash_UnreadableExisting_AbortsAndLeavesFileBytes()
    {
        using var t = new TempAllowlist();
        File.WriteAllText(t.AllowlistPath, "{not json");
        var before = File.ReadAllText(t.AllowlistPath);

        var ok = t.Allowlist.TryAddTrustedHash(new string('c', 64), out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(before, File.ReadAllText(t.AllowlistPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(t.AllowlistPath)!, "*.tmp"));
    }

    [Fact]
    public void TryAddTrustedHash_EmptyExistingFile_IsTreatedAsNoEntries()
    {
        using var t = new TempAllowlist();
        File.WriteAllText(t.AllowlistPath, "   ");
        Assert.True(t.Allowlist.TryAddTrustedHash(new string('d', 64), out var error), error);
        Assert.Contains(new string('d', 64), File.ReadAllText(t.AllowlistPath));
    }

    [Fact]
    public void TryAddTrustedHash_RejectsNonSha256_AndWritesNothing()
    {
        using var t = new TempAllowlist();
        Assert.False(t.Allowlist.TryAddTrustedHash("abc", out var shortError));
        Assert.False(string.IsNullOrWhiteSpace(shortError));
        Assert.False(t.Allowlist.TryAddTrustedHash(new string('z', 64), out var nonHexError));
        Assert.False(string.IsNullOrWhiteSpace(nonHexError));
        Assert.False(t.Allowlist.TryAddTrustedHash("", out var emptyError));
        Assert.False(string.IsNullOrWhiteSpace(emptyError));
        Assert.False(File.Exists(t.AllowlistPath));
    }

    [Fact]
    public void TryAddTrustedHash_UppercaseHex_IsNormalized()
    {
        using var t = new TempAllowlist();
        var dll = t.WriteDll(t.RuntimeDir, "upper.dll", 7, 7);
        var hash = PluginAllowlist.HashFile(dll);

        Assert.True(t.Allowlist.TryAddTrustedHash(hash.ToUpperInvariant(), out var error), error);

        var json = File.ReadAllText(t.AllowlistPath);
        Assert.Contains(hash, json);
        Assert.DoesNotContain(hash.ToUpperInvariant(), json);
        Assert.True(t.Allowlist.IsAllowed(dll, out _, out _));
    }

    [Fact]
    public void TryAddTrustedHash_EmptyAllowlist_ShippedHashStillAllowed()
    {
        using var t = new TempAllowlist();
        var shipped = t.WriteDll(t.RuntimeDir, "core.dll", 10, 11, 13);
        var shippedHash = PluginAllowlist.HashFile(shipped);
        File.WriteAllText(t.ShippedSetPath, $"[\"{shippedHash}\"]");
        Assert.True(t.Allowlist.IsAllowed(shipped, out _, out var shippedReason));
        Assert.Equal("listed", shippedReason);

        Assert.True(t.Allowlist.TryAddTrustedHash(new string('e', 64), out var error), error);

        Assert.True(t.Allowlist.IsAllowed(shipped, out _, out _));
        Assert.Contains(shippedHash, File.ReadAllText(t.ShippedSetPath));
    }

    [Fact]
    public void TryAddTrustedHash_CreatesAllowlistParent_ButNotDevPluginsDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "loadingai-allowlist-writer-" + Guid.NewGuid().ToString("N"));
        try
        {
            var devDir = Path.Combine(root, "dev");
            var allowlistDir = Path.Combine(root, "appdata", "LoadingAI");
            var allowlist = new PluginAllowlist(
                Path.Combine(allowlistDir, PluginAllowlist.AllowlistFileName),
                Path.Combine(root, PluginAllowlist.ShippedHashesFileName),
                devDir);

            Assert.True(allowlist.TryAddTrustedHash(new string('f', 64), out var error), error);

            Assert.True(File.Exists(allowlist.AllowlistPath));
            Assert.False(Directory.Exists(devDir));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void TryAddTrustedHash_ThroughInjectedCurrent_IsHonoredByGate()
    {
        using var t = new TempAllowlist();
        var dll = t.WriteDll(t.RuntimeDir, "via-current.dll", 4, 4, 4);
        var previous = PluginAllowlist.Current;
        try
        {
            PluginAllowlist.Current = t.Allowlist;
            Assert.False(PluginAllowlist.Current.IsAllowed(dll, out var refused, out _));
            Assert.True(PluginAllowlist.Current.TryAddTrustedHash(refused, out var error), error);
            Assert.True(PluginAllowlist.Current.IsAllowed(dll, out _, out _));
        }
        finally
        {
            PluginAllowlist.Current = previous;
        }
    }

    [Fact]
    public void Source_WriterOnlyCreatesTheAllowlistParentDirectory()
    {
        var allow = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Plugins", "Core", "PluginAllowlist.cs"));
        Assert.Contains("Path.GetDirectoryName(Path.GetFullPath(AllowlistPath))", allow);
        Assert.DoesNotContain("Directory.CreateDirectory(DevPluginsDir)", allow);

        var creates = allow.Split(new[] { "Directory.CreateDirectory(" }, StringSplitOptions.None)
            .Skip(1)
            .Select(rest => rest.Substring(0, rest.IndexOf(')')))
            .ToList();
        Assert.Equal(new[] { "dir" }, creates);
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
