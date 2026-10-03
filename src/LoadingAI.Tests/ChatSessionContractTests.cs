using System.IO;

namespace LoadingAI.Tests;

public class ChatSessionContractTests
{
    [Fact]
    public void ExecuteTool_ValidatesArgumentsBeforeApprovalOrExecution()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "ChatSession.cs"));
        var start = src.IndexOf("public async Task<string> ExecuteToolAsync", StringComparison.Ordinal);
        Assert.True(start >= 0);

        var validation = src.IndexOf("ToolArgumentValidator.Validate(", start, StringComparison.Ordinal);
        var preset = src.IndexOf("if (ToolPresets.HasPresets", start, StringComparison.Ordinal);
        var webApproval = src.IndexOf("AwaitL3WebAsync", start, StringComparison.Ordinal);
        var execute = src.IndexOf("await entry.ExecuteAsync", start, StringComparison.Ordinal);
        var turnGroup = src.IndexOf("EnsureTurnGroupAsync", start, StringComparison.Ordinal);

        Assert.True(validation >= 0);
        Assert.True(validation < preset);
        Assert.True(validation < webApproval);
        Assert.True(validation < execute);
        Assert.True(validation < turnGroup);
    }

    [Fact]
    public void ExecuteTool_RevalidatesArgumentsAfterPresetMerge()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "ChatSession.cs"));
        var start = src.IndexOf("public async Task<string> ExecuteToolAsync", StringComparison.Ordinal);
        var merge = src.IndexOf("argsJson = ToolPresets.Merge", start, StringComparison.Ordinal);
        var secondValidation = src.IndexOf("ToolArgumentValidator.Validate(", merge, StringComparison.Ordinal);
        var execution = src.IndexOf("await entry.ExecuteAsync", merge, StringComparison.Ordinal);

        Assert.True(merge >= 0);
        Assert.True(secondValidation > merge);
        Assert.True(secondValidation < execution);
    }

    [Fact]
    public void ExecuteTool_InvalidArgumentsNeverExecuteOrOpenTurnGroup()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "ChatSession.cs"));
        var start = src.IndexOf("public async Task<string> ExecuteToolAsync", StringComparison.Ordinal);
        var method = src.Substring(start);
        var validationBlockEnd = method.IndexOf("if (ToolPresets.HasPresets", StringComparison.Ordinal);
        var validationBlock = method.Substring(0, validationBlockEnd);

        Assert.Contains("return $\"Tool argument error: {validationError}\"", validationBlock);
        Assert.DoesNotContain("EnsureTurnGroupAsync", validationBlock);
        Assert.DoesNotContain("entry.ExecuteAsync", validationBlock);
    }

    private static string Session() => File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "ChatSession.cs"));

    [Fact]
    public void Source_RunTaskIsNotTiedToTheCancelToken()
    {
        // Task.Run(..., ct) never runs its delegate if ct is already cancelled, so the
        // finally that clears _isRunning never runs and the pane is stuck "Already working".
        Assert.DoesNotContain("}, ct);", Session());
    }

    [Fact]
    public void Source_CancelTokenExistsBeforeQuotaWait()
    {
        var src = Session();
        var start = src.IndexOf("public async Task HandleSendAsync", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var cts = src.IndexOf("_cts = new CancellationTokenSource();", start, StringComparison.Ordinal);
        var quota = src.IndexOf("ConsumeBuiltinQuotaAsync", start, StringComparison.Ordinal);
        Assert.True(cts >= 0 && quota >= 0 && cts < quota);
    }

    [Fact]
    public void Source_LastUserTextSetOnlyForAcceptedSend()
    {
        var src = Session();
        var start = src.IndexOf("public async Task HandleSendAsync", StringComparison.Ordinal);
        Assert.True(start >= 0);
        // The /clear and /resume branches have their own running checks; anchor on the send path's one.
        var guard = src.IndexOf("Already working", start, StringComparison.Ordinal);
        var running = src.IndexOf("_isRunning = true;", start, StringComparison.Ordinal);
        var assign = src.IndexOf("_lastUserText = text;", start, StringComparison.Ordinal);
        Assert.True(guard >= 0 && running > guard);
        Assert.True(assign > running);
    }

    [Fact]
    public void Source_ClearAndResumeRefusedWhileRunning()
    {
        var src = Session();
        var start = src.IndexOf("public async Task HandleSendAsync", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var clearGuard = src.IndexOf("Stop the current run before /clear.", start, StringComparison.Ordinal);
        var clearWork = src.IndexOf("_agent?.ClearHistory();", start, StringComparison.Ordinal);
        var resumeGuard = src.IndexOf("Stop the current run before /resume.", start, StringComparison.Ordinal);
        var resumeWork = src.IndexOf("_agent?.RestoreHistory(msgs2);", start, StringComparison.Ordinal);
        Assert.True(clearGuard >= 0 && clearGuard < clearWork);
        Assert.True(resumeGuard >= 0 && resumeGuard < resumeWork);
    }

    [Fact]
    public void Source_ReloadRefusedWhileRunning()
    {
        var src = Session();
        var start = src.IndexOf("public bool TryReloadPlugins", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var guard = src.IndexOf("if (_isRunning)", start, StringComparison.Ordinal);
        var discover = src.IndexOf("PluginLoader.Discover", start, StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < discover);
    }

    [Fact]
    public void Source_HostPageUnsubscribesTheControlNavigationEvent()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "HostPage.xaml.cs"));
        var start = src.IndexOf("private void OnUnloaded", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = src.IndexOf("private void ShowFallback", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var unloaded = src.Substring(start, end - start);
        Assert.Contains("Web.NavigationCompleted -= OnNavigationCompleted;", unloaded);
        // The CoreWebView2 object has no such event subscribed; unsubscribing it was a no-op.
        Assert.DoesNotContain("CoreWebView2.NavigationCompleted", src);
    }

    [Fact]
    public void Source_InitToolsIsIdempotent()
    {
        var src = Session();
        var start = src.IndexOf("public void InitTools()", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = src.IndexOf("public void RebuildRegistry()", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var init = src.Substring(start, end - start);
        var guard = init.IndexOf("if (_agent != null && _discovered.Count > 0) return;", StringComparison.Ordinal);
        var discover = init.IndexOf("PluginLoader.Discover", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < discover);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "LoadingAI.sln")))
            dir = dir.Parent!;
        return dir!.FullName;
    }
}
