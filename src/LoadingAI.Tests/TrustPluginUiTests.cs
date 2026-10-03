using System.IO;
using Xunit;

namespace LoadingAI.Tests;

/// <summary>
/// Source-level locks for the stage 2 Trust click (ADR-0011). There is no web test runner and the WPF
/// host cannot be constructed under test, so the click path is locked as text: the web sends an
/// identifier only, the host re-hashes the DLL and confirms with a default-No dialog, and a writer
/// abort is surfaced verbatim with an offered retry rather than swallowed.
/// </summary>
public class TrustPluginUiTests
{
    [Fact]
    public void WebTrustMessage_SendsIdentifierOnly()
    {
        var bridge = Source("web", "src", "bridge.ts");
        var line = bridge.Split('\n').First(l => l.Contains("type: 'trustPlugin'"));
        Assert.Contains("folder: string", line);
        Assert.Contains("assembly: string", line);
        Assert.DoesNotContain("hash", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sha", line, StringComparison.OrdinalIgnoreCase);

        var shelf = Source("web", "src", "plugins", "PluginShelf.tsx");
        Assert.Contains("type: 'trustPlugin'", shelf);
        Assert.Contains("folder: issue.folder", shelf);
        Assert.Contains("assembly: issue.assembly", shelf);
        Assert.DoesNotContain("sha256", shelf);
    }

    [Fact]
    public void TrustAffordance_OnlyOnAllowlistRefusalRows()
    {
        var shelf = Source("web", "src", "plugins", "PluginShelf.tsx");
        Assert.Contains("Trust this plugin…", shelf);
        Assert.Contains("canTrust(issue) &&", shelf);

        // The pane keys on the host's stable code, never on the wording of the summary.
        var view = Source("web", "src", "plugins", "pluginView.ts");
        Assert.Contains("issue.code === 'allowlist-refusal'", view);
        Assert.Contains("\"allowlist-refusal\"", Source("src", "LoadingAI", "Plugins", "Core", "PluginLoader.cs"));
    }

    [Fact]
    public void HostTrustHandler_Dispatched_ConfirmsDefaultNo_WritesOnce_AndReloads()
    {
        var host = Source("src", "LoadingAI", "Ui", "HostPage.xaml.cs");
        Assert.Contains("case ToHostMessage.TrustPlugin", host);

        var trust = Slice(host, "private void TrustPlugin(", "private static string TrustRetryHint(");

        // Two-step: the web affordance posts, the native dialog decides.
        Assert.Contains("TaskDialog", trust);
        Assert.Contains("DefaultButton = TaskDialogResult.No", trust);
        Assert.Contains("PostToWeb(new FromHostMessage.Status(\"Trust cancelled.\"))", trust);

        // TOCTOU: re-hash the bytes now and require the hash the refusal recorded.
        Assert.Contains("PluginAllowlist.HashFile(", trust);
        Assert.Contains("recorded.Sha256", trust);
        Assert.Contains("RuntimePluginsDir", trust);

        // One write, then the rescan that makes the tools appear without restarting Revit.
        Assert.Contains("PluginAllowlist.Current.TryAddTrustedHash(", trust);
        Assert.Contains("_session.TryReloadPlugins(", trust);
        Assert.Contains("PostPlugins($\"Trusted {name}. {reloadMsg}\")", trust);

        // A false must reach the engineer verbatim, and the write is never re-attempted here.
        Assert.Contains("PostToWeb(new FromHostMessage.Error(error))", trust);
        Assert.Equal(1, CountOccurrences(trust, "TryAddTrustedHash("));
        Assert.DoesNotContain("Thread.Sleep", trust);
    }

    [Fact]
    public void HostTrustHandler_RetryHint_ClassifiesOnTheSharedWriterPrefix()
    {
        var host = Source("src", "LoadingAI", "Ui", "HostPage.xaml.cs");
        var hint = Slice(host, "private static string TrustRetryHint(", "private async void OnWebMessageReceived(");
        Assert.Contains("StartsWith(\"refusing to overwrite\"", hint);
        Assert.Contains("Click Trust this plugin… again to retry.", hint);
        // The abort paths are classified by the shared prefix only — never by matching this word.
        Assert.DoesNotContain("unreadable", hint);

        // Both file-state aborts share the prefix, so prefix-present alone is the classifier. If this
        // assertion fails the writer's wording changed: re-check the classifier, do not edit the writer.
        var writer = Source("src", "LoadingAI", "Plugins", "Core", "PluginAllowlist.cs");
        Assert.Contains("\"refusing to overwrite unreadable", writer);
        Assert.Contains("\"refusing to overwrite {AllowlistPath}", writer);
    }

    private static string Source(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    private static string Slice(string src, string start, string end)
    {
        var i = src.IndexOf(start, StringComparison.Ordinal);
        Assert.True(i >= 0, start);
        var j = src.IndexOf(end, i, StringComparison.Ordinal);
        Assert.True(j > i, end);
        return src.Substring(i, j - i);
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
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
}
