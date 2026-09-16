using System.IO;
using Xunit;

namespace LoadingAI.Tests;

public class RevitBridgeContractTests
{
    [Fact]
    public void Execute_LogsAndPropagates_WithoutThrowingIntoRevit()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Revit", "RevitBridge.cs"));
        Assert.Contains("bridge.execute_failed", src);
        Assert.Contains("bridge.action_failed", src);
        Assert.Contains("RevitBridge.Execute failed:", src);
        Assert.Contains("RevitBridge action failed:", src);

        // IExternalEventHandler.Execute must not throw into Revit, and must not
        // drop the rest of the batch (waiters would sit until DefaultTimeout).
        var execute = Slice(src, "public void Execute(UIApplication app)", "public string GetName()");
        Assert.DoesNotContain("throw;", execute);
        Assert.DoesNotContain("catch { }", execute);
        Assert.DoesNotContain("catch{}", execute);
        Assert.Contains("DrainTo(local);", execute);
        Assert.Contains("InvokeAll(app, local);", execute);

        // Fail-loud lives in the RunAsync wrapper: the awaiter gets the real
        // exception instead of a silent no-op. That is what makes a failed write
        // visible, so it is the invariant worth locking down.
        var runAsync = Slice(src, "public async Task<T> RunAsync<T>(Func<UIApplication, T> func, TimeSpan timeout", "var started = DateTime.UtcNow;");
        Assert.Contains("tcs.TrySetException(ex);", runAsync);
        Assert.DoesNotContain("catch { }", runAsync);
        Assert.DoesNotContain("catch{}", runAsync);
    }

    [Fact]
    public void TurnGroup_UsesHasEnded_NotIsDone()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Revit", "RevitBridge.cs"));
        Assert.Contains("HasEnded()", src);
        Assert.Contains("HasStarted()", src);
        Assert.DoesNotContain("IsDone", src);
        Assert.Contains("EnsureTurnGroupAsync", src);
        Assert.Contains("AssimilateTurnGroupAsync", src);
        Assert.Contains("RollbackTurnGroupAsync", src);
        Assert.Contains("group.Assimilate()", src);
        Assert.Contains("group.RollBack()", src);
    }

    [Fact]
    public void HostPage_WiresTurnGroup_SkipsL1AndDryRun()
    {
        var src = UiSrc();
        Assert.Contains("EnsureTurnGroupAsync", src);
        Assert.Contains("AssimilateTurnGroupAsync", src);
        Assert.Contains("RollbackTurnGroupAsync", src);
        Assert.Contains("CancellationToken.None", src);
        Assert.Contains("ShouldAssimilateTurn", src);
        Assert.Contains("Max tool rounds", src);
        Assert.Contains("LLM error:", src);

        // Writes only: L2, or L3 that is not dry-run. L1 never starts a group.
        Assert.Contains("entry.Tier == ToolTier.L2", src);
        Assert.Contains("entry.Tier == ToolTier.L3 && !dryRun", src);
        Assert.Contains("ToolPresets.IsDryRun(argsJson)", src);
        Assert.DoesNotContain("entry.Tier == ToolTier.L1", src);
    }

    [Fact]
    public void Contributing_DocumentsFailLoudAndTurnGroup()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "CONTRIBUTING.md"));
        Assert.Contains("Fail-loud", src);
        Assert.Contains("TransactionGroup", src);
        Assert.Contains("Assimilate", src);
    }

    private static string UiSrc()
    {
        var dir = Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui");
        return string.Join("\n", Directory.GetFiles(dir, "*.cs").Select(File.ReadAllText));
    }

    private static string Slice(string src, string start, string end)
    {
        var i = src.IndexOf(start, StringComparison.Ordinal);
        Assert.True(i >= 0, "missing " + start);
        var j = src.IndexOf(end, i, StringComparison.Ordinal);
        Assert.True(j > i, "missing " + end);
        return src.Substring(i, j - i);
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
