using System.IO;
using System.Runtime.InteropServices;
using LoadingAI.Plugins.Core;

namespace LoadingAI.Tests;

public class PluginRemovalTombstoneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lai-remove-" + Guid.NewGuid().ToString("N"));

    public PluginRemovalTombstoneTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private string Plugin(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), $"{{\"id\":\"{name}\",\"entryAssembly\":\"a.dll\"}}");
        File.WriteAllBytes(Path.Combine(dir, "a.dll"), new byte[] { 1 });
        return dir;
    }

    [Fact]
    public void TryRemove_DeletesTheFolder()
    {
        var dir = Plugin("hello");
        Assert.True(PluginLoader.TryRemove("hello", dir, new[] { _root }, out var message));
        Assert.False(Directory.Exists(dir));
        Assert.Contains("Removed", message);
    }

    [Fact]
    public void TryRemove_StillRefusesTheCoreRootAndOutsiders()
    {
        Assert.False(PluginLoader.TryRemove("core-tools", Plugin("core-tools"), new[] { _root }, out _));
        Assert.False(PluginLoader.TryRemove("loose", _root, new[] { _root }, out _));
        Assert.False(PluginLoader.TryRemove("x", Path.Combine(Path.GetTempPath(), "elsewhere"), new[] { _root }, out _));
    }

    [Fact]
    public void ALockedDllLeavesAMarkedFolderTheScanSkips_AndPurgeRemovesLater()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return; // an open file only blocks a delete on Windows
        var dir = Plugin("locked");
        string message;
        using (new FileStream(Path.Combine(dir, "a.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(PluginLoader.TryRemove("locked", dir, new[] { _root }, out message));
            Assert.Contains("next time", message);
            Assert.True(File.Exists(Path.Combine(dir, PluginLoader.RemoveMarkerName)));

            Assert.False(PluginLoader.TryReadManifest(dir, out _, out _, out var issue));
            Assert.Null(issue); // skipped silently, not reported as broken
        }

        Assert.Equal(1, PluginLoader.PurgeMarked(new[] { _root }));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void PurgeMarked_LeavesUnmarkedFoldersAlone()
    {
        var keep = Plugin("keep");
        var drop = Plugin("drop");
        File.WriteAllText(Path.Combine(drop, PluginLoader.RemoveMarkerName), "x");

        Assert.Equal(1, PluginLoader.PurgeMarked(new[] { _root }));
        Assert.True(Directory.Exists(keep));
        Assert.False(Directory.Exists(drop));
    }

    [Fact]
    public void DescribeForTrust_ShowsTheClaimsAndTheRealRisk()
    {
        var dir = Plugin("claimed");
        File.WriteAllText(Path.Combine(dir, "plugin.json"),
            """{"id":"claimed","name":"Claimed Tools","version":"2.1.0","author":"Someone","entryAssembly":"a.dll","tools":[{"name":"wipe","tier":"L3"},{"name":"peek","tier":"L1"}]}""");

        var text = PluginLoader.DescribeForTrust(Path.Combine(dir, "a.dll"), new string('a', 64), dir);

        Assert.Contains(new string('a', 64), text);
        Assert.Contains("Claimed Tools", text);
        Assert.Contains("wipe (L3)", text);
        Assert.Contains("nothing has verified", text);
        Assert.Contains("can do anything you can", text);
        Assert.DoesNotContain("tiers and Confirm L3 are unchanged", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeForTrust_WithoutAManifestStillWarns()
    {
        var text = PluginLoader.DescribeForTrust(Path.Combine(_root, "x.dll"), new string('b', 64), _root);
        Assert.Contains("can do anything you can", text);
    }

    [Fact]
    public void AllowlistSource_NamesWhereTrustComesFrom()
    {
        var dev = Path.Combine(_root, "dev");
        var runtime = Path.Combine(_root, "runtime");
        Directory.CreateDirectory(Path.Combine(dev, "p"));
        Directory.CreateDirectory(runtime);
        var devDll = Path.Combine(dev, "p", "a.dll");
        var shipped = Path.Combine(runtime, "s.dll");
        var trusted = Path.Combine(runtime, "t.dll");
        var unknown = Path.Combine(runtime, "u.dll");
        File.WriteAllBytes(devDll, new byte[] { 1 });
        File.WriteAllBytes(shipped, new byte[] { 2 });
        File.WriteAllBytes(trusted, new byte[] { 3 });
        File.WriteAllBytes(unknown, new byte[] { 4 });

        var shippedList = Path.Combine(_root, PluginAllowlist.ShippedHashesFileName);
        var trustedList = Path.Combine(_root, PluginAllowlist.AllowlistFileName);
        File.WriteAllText(shippedList, $"[\"{PluginAllowlist.HashFile(shipped)}\"]");
        File.WriteAllText(trustedList, $"[\"{PluginAllowlist.HashFile(trusted)}\"]");
        var allowlist = new PluginAllowlist(trustedList, shippedList, dev);

        Assert.Equal("dev", allowlist.Source(devDll));
        Assert.Equal("shipped", allowlist.Source(shipped));
        Assert.Equal("trusted", allowlist.Source(trusted));
        Assert.Equal("unknown", allowlist.Source(unknown));
        Assert.Equal("unknown", allowlist.Source(Path.Combine(runtime, "missing.dll")));
    }

    [Fact]
    public void Source_HostRemoveGoesThroughTheTombstoneAndRescans()
    {
        var host = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "src", "LoadingAI", "Ui", "HostPage.xaml.cs"));
        var start = host.IndexOf("case ToHostMessage.RemovePlugin", StringComparison.Ordinal);
        var end = host.IndexOf("case ToHostMessage.ReloadPlugins", start, StringComparison.Ordinal);
        var remove = host.Substring(start, end - start);
        Assert.Contains("PluginLoader.TryRemove(", remove);
        Assert.Contains("_session.IsRunning", remove);
        Assert.Contains("_session.TryReloadPlugins(", remove);
        Assert.DoesNotContain("Directory.Delete", remove);
        Assert.Contains("DefaultButton = TaskDialogResult.No", remove);

        var trust = host.Substring(host.IndexOf("private void TrustPlugin(", StringComparison.Ordinal));
        Assert.Contains("PluginLoader.DescribeForTrust(dll, sha256, pluginFolder)", trust);

        var loader = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "src", "LoadingAI", "Plugins", "Core", "PluginLoader.cs"));
        Assert.Contains("PurgeMarked(new[] { DevPluginsDir, RuntimePluginsDir });", loader);
        Assert.Contains("RemoveMarkerName", loader);

        var session = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "src", "LoadingAI", "Ui", "ChatSession.cs"));
        Assert.Contains("public bool IsRunning => _isRunning;", session);
        Assert.Contains("source = PluginAllowlist.Current.Source(", session);
        Assert.Contains("removable = PluginLoader.CanRemove(", session);
    }
}
