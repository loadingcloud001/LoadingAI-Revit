using System.IO;
using System.Security.Cryptography;
using LoadingAI.Plugins.Core;

namespace LoadingAI.Tests;

public class PluginLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lai-loader-" + Guid.NewGuid().ToString("N"));

    public PluginLoaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private string Folder(string name, string? manifest, params string[] files)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        if (manifest != null) File.WriteAllText(Path.Combine(dir, "plugin.json"), manifest);
        foreach (var f in files) File.WriteAllBytes(Path.Combine(dir, f), new byte[] { 1, 2, 3 });
        return dir;
    }

    [Fact]
    public void AFolderWithoutADllIsNotAPluginAndIsNotReported()
    {
        var dir = Folder("empty", manifest: null);
        Assert.False(PluginLoader.TryReadManifest(dir, out _, out _, out var issue));
        Assert.Null(issue);
    }

    [Fact]
    public void ADllWithoutAManifestIsNamedNotGuessed()
    {
        var dir = Folder("nomanifest", manifest: null, "a.dll", "b.dll");
        Assert.False(PluginLoader.TryReadManifest(dir, out _, out _, out var issue));
        Assert.Equal(PluginIssueCodes.ManifestMissing, issue!.Code);
        Assert.False(issue.Notice);
    }

    [Fact]
    public void BadJsonIsNamed()
    {
        var dir = Folder("badjson", "{ not json", "a.dll");
        Assert.False(PluginLoader.TryReadManifest(dir, out _, out _, out var issue));
        Assert.Equal(PluginIssueCodes.ManifestUnreadable, issue!.Code);
    }

    [Theory]
    [InlineData("""{"id":"x"}""")]
    [InlineData("""{"id":"x","entryAssembly":"..\\evil.dll"}""")]
    [InlineData("""{"id":"x","entryAssembly":"sub/evil.dll"}""")]
    [InlineData("""{"id":"x","entryAssembly":"C:\\evil.dll"}""")]
    public void EntryAssemblyMustBeABareFileName(string manifest)
    {
        var dir = Folder("entry" + Guid.NewGuid().ToString("N").Substring(0, 6), manifest, "a.dll");
        Assert.False(PluginLoader.TryReadManifest(dir, out _, out _, out var issue));
        Assert.Equal(PluginIssueCodes.ManifestUnreadable, issue!.Code);
    }

    [Fact]
    public void AMissingEntryAssemblyIsNamed()
    {
        var dir = Folder("noentry", """{"id":"x","entryAssembly":"x.dll"}""", "other.dll");
        Assert.False(PluginLoader.TryReadManifest(dir, out _, out _, out var issue));
        Assert.Contains("not found", issue!.Summary);
    }

    [Fact]
    public void AGoodManifestYieldsTheEntryDll_AndTheFolderIsRecorded()
    {
        var dir = Folder("good", """{"id":"good","name":"Good","entryAssembly":"LoadingAI.Plugins.Good.dll"}""", "LoadingAI.Plugins.Good.dll", "Other.dll");
        Assert.True(PluginLoader.TryReadManifest(dir, out var meta, out var dll, out var issue));
        Assert.Null(issue);
        Assert.Equal(Path.Combine(dir, "LoadingAI.Plugins.Good.dll"), dll);
        Assert.Equal(dir, meta.FolderPath);
        Assert.Equal("good", meta.Id);
    }

    [Fact]
    public void AMissingIdFallsBackToTheFolderName()
    {
        var dir = Folder("fallback", """{"entryAssembly":"a.dll"}""", "a.dll");
        Assert.True(PluginLoader.TryReadManifest(dir, out var meta, out _, out _));
        Assert.Equal("fallback", meta.Id);
    }

    [Fact]
    public void TheDevCopyWinsOverAnInstalledOne_AndTheLoserIsANotice()
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dev = Path.Combine(PluginLoader.DevPluginsDir, "hello");
        var installed = Path.Combine(PluginLoader.RuntimePluginsDir, "hello");
        var meta = new PluginMeta { Id = "hello", EntryAssembly = "a.dll" };

        Assert.True(PluginLoader.TryClaimId(seen, meta, dev, out _));
        Assert.False(PluginLoader.TryClaimId(seen, meta, installed, out var issue));
        Assert.Equal(PluginIssueCodes.Shadowed, issue!.Code);
        Assert.True(issue.Notice);
        Assert.Contains(dev, issue.Detail);
    }

    [Fact]
    public void TwoInstalledCopiesAreADuplicateFailure()
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var meta = new PluginMeta { Id = "twin", EntryAssembly = "a.dll" };
        Assert.True(PluginLoader.TryClaimId(seen, meta, Path.Combine(_root, "a"), out _));
        Assert.False(PluginLoader.TryClaimId(seen, meta, Path.Combine(_root, "b"), out var issue));
        Assert.Equal(PluginIssueCodes.DuplicatePlugin, issue!.Code);
        Assert.False(issue.Notice);
    }

    [Fact]
    public void ACorruptAllowlistIsNamedInTheRefusal()
    {
        var list = Path.Combine(_root, PluginAllowlist.AllowlistFileName);
        File.WriteAllText(list, "{ this is not a json array");
        var dll = Path.Combine(_root, "plugin", "x.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        File.WriteAllBytes(dll, new byte[] { 9, 9, 9 });
        var allowlist = new PluginAllowlist(list, Path.Combine(_root, "none.json"), Path.Combine(_root, "dev"));

        Assert.False(allowlist.IsAllowed(dll, out _, out var reason));
        Assert.Contains("is unreadable", reason);
    }

    [Fact]
    public void AMissingAllowlistIsNotCalledUnreadable()
    {
        var dll = Path.Combine(_root, "plugin", "y.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        File.WriteAllBytes(dll, new byte[] { 8 });
        var allowlist = new PluginAllowlist(Path.Combine(_root, "absent.json"), Path.Combine(_root, "none.json"), Path.Combine(_root, "dev"));

        Assert.False(allowlist.IsAllowed(dll, out _, out var reason));
        Assert.DoesNotContain("unreadable", reason);
    }

    [Fact]
    public void Source_LoaderIsDeterministicAndBounded()
    {
        var src = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "src", "LoadingAI", "Plugins", "Core", "PluginLoader.cs"));
        Assert.Contains("new[] { DevPluginsDir, RuntimePluginsDir }", src);
        Assert.Contains("OrderBy(p => p, StringComparer.OrdinalIgnoreCase)", src);
        Assert.DoesNotContain("Directory.GetFiles(folder, \"*.dll\").FirstOrDefault()", src);
        Assert.DoesNotContain("File.WriteAllText(LogPath", src);
        Assert.Contains("PluginLogFile.Append", src);

        var logs = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "src", "LoadingAI", "Plugins", "Core", "PluginLogs.cs"));
        Assert.DoesNotContain("File.AppendAllText", logs);
        Assert.Contains("PluginLogFile.Append", logs);

        var context = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "src", "LoadingAI", "Plugins", "Core", "PluginContext.cs"));
        Assert.Contains("TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)", context);
    }

    [Fact]
    public void Source_ChatSessionIgnoresNoticesWhenWarningTheModel()
    {
        var src = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "src", "LoadingAI", "Ui", "ChatSession.cs"));
        Assert.Contains("PluginLoader.Issues.Where(i => !i.Notice)", src);
        Assert.Contains("PluginLoader.FailureCount", src);
        var host = File.ReadAllText(Path.Combine(AdapterSource.RepoRoot(), "src", "LoadingAI", "Ui", "HostPage.xaml.cs"));
        Assert.Contains("PluginLoader.FailureCount", host);
    }
}
