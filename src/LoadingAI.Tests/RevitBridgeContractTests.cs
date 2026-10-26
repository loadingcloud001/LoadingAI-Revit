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

    [Fact]
    public void RunAsync_TracksActionAndReportsUnknownOutcomeAfterCallerStopsWaiting()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Revit", "RevitBridge.cs"));
        var runAsync = Slice(src, "public async Task<T> RunAsync<T>(Func<UIApplication, T> func, TimeSpan timeout", "public Task RunAsync(Action<UIApplication> action");

        Assert.Contains("var actionId = _actions.Begin();", runAsync);
        Assert.Contains("_actions.MarkRunning(actionId);", runAsync);
        Assert.Contains("_actions.MarkSucceeded(actionId);", runAsync);
        Assert.Contains("_actions.MarkFailed(actionId", runAsync);
        Assert.Contains("_actions.MarkAbandoned(actionId", runAsync);
        Assert.Contains("BridgeOperationUnknownException", runAsync);
        Assert.Contains("TryGetAction", src);
    }

    [Fact]
    public void RaiseDenied_CancelsQueuedActionInsteadOfLeavingItRunnable()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Revit", "RevitBridge.cs"));
        var runAsync = Slice(src, "public async Task<T> RunAsync<T>(Func<UIApplication, T> func, TimeSpan timeout", "public Task RunAsync(Action<UIApplication> action");
        Assert.Contains("_actions.TryCancelQueued(actionId", runAsync);
    }

    [Fact]
    public void RaiseDeniedWhenAlreadyRunning_ReportsUnknownOutcome()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Revit", "RevitBridge.cs"));
        var runAsync = Slice(src, "public async Task<T> RunAsync<T>(Func<UIApplication, T> func, TimeSpan timeout", "public Task RunAsync(Action<UIApplication> action");
        Assert.Contains("if (!_actions.TryCancelQueued(actionId", runAsync);
        Assert.Contains("throw new BridgeOperationUnknownException(actionId", runAsync);
    }
    [Fact]
    public void SameThreadExecutionAlsoRecordsActionOutcome()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Revit", "RevitBridge.cs"));
        var runAsync = Slice(src, "public async Task<T> RunAsync<T>(Func<UIApplication, T> func, TimeSpan timeout", "var tcs = new TaskCompletionSource<T>");
        Assert.Contains("_actions.Begin();", runAsync);
        Assert.Contains("_actions.MarkRunning(actionId);", runAsync);
        Assert.Contains("_actions.MarkSucceeded(actionId);", runAsync);
        Assert.Contains("_actions.MarkFailed(actionId", runAsync);
    }

    [Fact]
    public void ChatSession_UnknownWriteForcesRollback()
    {
        var src = UiSrc();
        Assert.Contains("!execution.IsSafeToAssimilate", src);
        Assert.Contains("ToolExecutionStatus.Unknown", src);
        Assert.Contains("MarkTurnFailedIfGroupActive();", src);
        Assert.DoesNotContain("result.StartsWith($\"Tool {name} failed:\"", src);
    }

    [Fact]
    public void Source_DirectPathRequiresApiContextNotJustUiThread()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Revit", "RevitBridge.cs"));
        var gate = Slice(src, "public async Task<T> RunAsync<T>(Func<UIApplication, T> func, TimeSpan timeout", "_handler.Enqueue(");
        Assert.Contains("t_inHandler || _attached", gate);
    }

    [Fact]
    public void Source_ExecuteMarksAndClearsTheApiContext()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Revit", "RevitBridge.cs"));
        var execute = Slice(src, "public void Execute(UIApplication app)", "public string GetName()");
        Assert.Contains("t_inHandler = true;", execute);
        Assert.Contains("t_inHandler = false;", execute);
    }

    [Fact]
    public void Source_AttachCurrentThreadEnablesDirectPath()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Revit", "RevitBridge.cs"));
        var attach = Slice(src, "public void AttachCurrentThread", "public RevitBridge()");
        Assert.Contains("_attached = true;", attach);
    }

    private static string UiSrc()
    {
        var dir = Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui");
        return string.Join("\n", Directory.GetFiles(dir, "*.cs").Select(File.ReadAllText));
    }

    [Fact]
    public void Source_TheGateWaitsInThePane_NotInANativeDialog()
    {
        var gate = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "ApprovalGate.cs"));
        Assert.DoesNotContain("TaskDialog", gate);
        Assert.DoesNotContain("UserDialogTimeout", gate);
        Assert.Contains("TimeSpan.FromSeconds(ReviewText.ApprovalTimeoutSeconds)", gate);

        var guards = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Tools", "ToolGuards.cs"));
        Assert.DoesNotContain("UserDialogTimeout", guards);
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
