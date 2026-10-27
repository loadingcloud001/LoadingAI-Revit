using System.IO;
using System.Linq;

namespace LoadingAI.Tests;

public class ChatSessionContractTests
{
    private const string CoreMethod = "private async Task<string> ExecuteToolCoreAsync";

    [Fact]
    public void ExecuteTool_ValidatesArgumentsBeforeTheGateOrExecution()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "ChatSession.cs"));
        var start = src.IndexOf(CoreMethod, StringComparison.Ordinal);
        Assert.True(start >= 0);

        var validation = src.IndexOf("ToolArgumentValidator.Validate(", start, StringComparison.Ordinal);
        var gate = src.IndexOf("GatePolicy.Decide(", start, StringComparison.Ordinal);
        var review = src.IndexOf("_approvals.ReviewAsync", start, StringComparison.Ordinal);
        var execute = src.IndexOf("await entry.ExecuteAsync", start, StringComparison.Ordinal);
        var turnGroup = src.IndexOf("EnsureTurnGroupAsync", start, StringComparison.Ordinal);

        Assert.True(validation >= 0);
        Assert.True(validation < gate);
        Assert.True(validation < review);
        Assert.True(validation < execute);
        Assert.True(validation < turnGroup);
    }

    [Fact]
    public void ExecuteTool_RevalidatesArgumentsAfterTheReview()
    {
        // A Run form merge or an edited input changes the arguments after the first check.
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "ChatSession.cs"));
        var start = src.IndexOf(CoreMethod, StringComparison.Ordinal);
        var review = src.IndexOf("_approvals.ReviewAsync", start, StringComparison.Ordinal);
        var secondValidation = src.IndexOf("ToolArgumentValidator.Validate(", review, StringComparison.Ordinal);
        var execution = src.IndexOf("await entry.ExecuteAsync", review, StringComparison.Ordinal);

        Assert.True(review >= 0);
        Assert.True(secondValidation > review);
        Assert.True(secondValidation < execution);
    }

    [Fact]
    public void ExecuteTool_InvalidArgumentsNeverExecuteOrOpenTurnGroup()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "ChatSession.cs"));
        var start = src.IndexOf(CoreMethod, StringComparison.Ordinal);
        var method = src.Substring(start);
        var validationBlockEnd = method.IndexOf("var decision = GatePolicy.Decide", StringComparison.Ordinal);
        var validationBlock = method.Substring(0, validationBlockEnd);

        Assert.Contains("$\"Tool argument error: {validationError}\"", validationBlock);
        Assert.DoesNotContain("EnsureTurnGroupAsync", validationBlock);
        Assert.DoesNotContain("entry.ExecuteAsync", validationBlock);
    }

    [Fact]
    public void ExecuteTool_OneGate_FromThePolicy_WithTheHostsOwnLevel()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "ChatSession.cs"));
        var start = src.IndexOf(CoreMethod, StringComparison.Ordinal);
        var method = src.Substring(start);

        // The level is the user's setting held by the host. Neither the tool arguments nor the model reach it.
        Assert.Contains("Level = ProviderSettings.LevelOf(_settings)", method);
        Assert.Contains("DryRun = ToolPresets.IsDryRun(argsJson)", method);

        // Block and every non-approval answer return before the turn group opens or the tool runs.
        var block = method.IndexOf("GateDecision.Block", StringComparison.Ordinal);
        var review = method.IndexOf("_approvals.ReviewAsync", StringComparison.Ordinal);
        var turnGroup = method.IndexOf("EnsureTurnGroupAsync", StringComparison.Ordinal);
        Assert.True(block >= 0 && block < review && review < turnGroup);
        foreach (var outcome in new[] { "ReviewOutcome.Denied", "ReviewOutcome.Cancelled", "ReviewOutcome.TimedOut" })
        {
            var at = method.IndexOf(outcome, StringComparison.Ordinal);
            Assert.True(at > review && at < turnGroup, outcome);
        }
        // A Deny, Stop or timeout still cancels the writes the turn already made, as before.
        Assert.True(CountOccurrences(method.Substring(review, turnGroup - review), "MarkTurnFailedIfGroupActive();") >= 3);
    }

    [Fact]
    public void Gate_HasNoNativeDialog_TheCardIsTheOnlyConfirm()
    {
        foreach (var file in new[] { "Ui/ChatSession.cs", "Ui/ApprovalGate.cs", "Tools/ToolGuards.cs" })
        {
            var src = File.ReadAllText(Path.Combine(new[] { RepoRoot(), "src", "LoadingAI" }.Concat(file.Split('/')).ToArray()));
            Assert.DoesNotContain("new TaskDialog", src);
            Assert.DoesNotContain("ConfirmL3Native", src);
            Assert.DoesNotContain("Confirm L3:", src);
        }
    }

    [Fact]
    public void Gate_TimeoutAndStopNeverApprove()
    {
        var gate = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "ApprovalGate.cs"));
        var wait = gate.Substring(gate.IndexOf("private static async Task<ReviewResult> WaitAsync", StringComparison.Ordinal));
        wait = wait.Substring(0, wait.IndexOf("private async Task PostReviewAsync", StringComparison.Ordinal));
        Assert.Contains("ReviewOutcome.TimedOut", wait);
        Assert.DoesNotContain("ReviewOutcome.Approved", wait);

        var deny = gate.Substring(gate.IndexOf("public void DenyAllPending", StringComparison.Ordinal));
        deny = deny.Substring(0, deny.IndexOf("public async Task ApplyEditAsync", StringComparison.Ordinal));
        Assert.Contains("ReviewOutcome.Cancelled", deny);
        Assert.DoesNotContain("ReviewOutcome.Approved", deny);
    }

    [Fact]
    public void Gate_ShowsAHiddenPane_InsteadOfFallingBackToADialog()
    {
        var gate = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "ApprovalGate.cs"));
        var review = gate.Substring(gate.IndexOf("public async Task<ReviewResult> ReviewAsync", StringComparison.Ordinal));
        var show = review.IndexOf("PaneVisibility.ShowIfHiddenAsync", StringComparison.Ordinal);
        var post = review.IndexOf("await PostReviewAsync(p, null);", StringComparison.Ordinal);
        Assert.True(show >= 0 && post > show);
        var vis = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "PaneVisibility.cs"));
        Assert.Contains("pane.Show()", vis);
        Assert.DoesNotContain("TaskDialog", vis);
    }

    [Fact]
    public void EveryCallIsBracketedByAStartAndAnEnd()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LoadingAI", "Ui", "ChatSession.cs"));
        var start = src.IndexOf(CoreMethod, StringComparison.Ordinal);
        var method = src.Substring(start);
        method = method.Substring(0, method.IndexOf("private void StartStep", StringComparison.Ordinal));
        Assert.Contains("StartStep(callId, name, entry.Tier.ToString()", method);
        // Every return is an EndStep, so no step is left running in the pane.
        foreach (var line in method.Split('\n').Where(l => l.TrimStart().StartsWith("return ")))
            Assert.Contains("EndStep(", line);
    }

    [Fact]
    public void TurnLifecycle_StartsBeforeTheRun_AndEndsAfterTheGroupIsFinished()
    {
        var src = Session();
        var start = src.IndexOf("TurnStart(turnId", StringComparison.Ordinal);
        var run = src.IndexOf("await agent.RunAsync", StringComparison.Ordinal);
        var changes = src.IndexOf("await ReportChangesAsync(turnId)", StringComparison.Ordinal);
        var end = src.IndexOf("FromHostMessage.TurnEnd(turnId", StringComparison.Ordinal);
        Assert.True(start >= 0 && start < run);
        Assert.True(changes > run && changes < end);
        // The receipt is only for a turn that was kept. A rolled-back turn changed nothing.
        Assert.Contains("if (assimilate) await ReportChangesAsync(turnId);", src);
        // The change tracker is bound for the turn and released in finally.
        Assert.Contains("TurnChangeTracker.Begin(_changes);", src);
        Assert.Contains("TurnChangeTracker.End();", src);
    }

    [Fact]
    public void ExecuteTool_ExpandsTheResultSetsBeforeTheArgumentsAreChecked()
    {
        var src = Session();
        var start = src.IndexOf(CoreMethod, StringComparison.Ordinal);
        var expand = src.IndexOf("ResultSetExpander.TryExpand", start, StringComparison.Ordinal);
        var coerce = src.IndexOf("ToolArgumentCoercer.Coerce(", start, StringComparison.Ordinal);
        Assert.True(expand >= 0 && expand < coerce);
        // The edited arguments of the review card are expanded too.
        Assert.Contains("ResultSetExpander.TryExpand(review.ArgsJson", src);
    }

    [Fact]
    public void ExecuteTool_SavesTheResultIds_AndTellsTheModelTheHandle()
    {
        var src = Session();
        var start = src.IndexOf(CoreMethod, StringComparison.Ordinal);
        var execute = src.IndexOf("await entry.ExecuteAsync", start, StringComparison.Ordinal);
        var save = src.IndexOf("_sets.Save(", start, StringComparison.Ordinal);
        Assert.True(execute >= 0 && save > execute);
        // The model is told the handle, so it can pass "@rN" back instead of repeating the ids.
        Assert.Contains("Saved as {handle}", src);
    }

    [Fact]
    public void Clear_EmptiesTheSavedResultSetsWithTheSessionAllowList()
    {
        var src = Session();
        var count = 0;
        var at = src.IndexOf("_allow.ClearSession();", StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            var sets = src.IndexOf("_sets.Clear();", at, StringComparison.Ordinal);
            var next = src.IndexOf("_allow.ClearSession();", at + 1, StringComparison.Ordinal);
            Assert.True(sets >= 0 && (next < 0 || sets < next));
            at = next;
        }
        Assert.True(count >= 2);
    }

    [Fact]
    public void TurnStart_SetsTheModelSummaryBeforeTheRun()
    {
        var src = Session();
        var summary = src.IndexOf("CollectPromptSummaryAsync(ct)", StringComparison.Ordinal);
        var run = src.IndexOf("await agent.RunAsync", StringComparison.Ordinal);
        Assert.True(summary >= 0 && summary < run);
    }

    [Fact]
    public void HostTools_WriteFiles_NotTheModel_SoTheyOpenNoTurnGroup()
    {
        var src = Session();
        // save_project_note is L2, but it writes a notes file: no open model needed, no transaction group.
        Assert.Contains("var isWrite = !_hostToolNames.Contains(name) &&", src);
        Assert.Contains("HostTools.Create(_notes, () => _projectKey, () => _playbooks)", src);
    }

    [Fact]
    public void PlaybookSlash_NeverShadowsABuiltinCommand_AndPinsItsTools()
    {
        var src = Session();
        Assert.Contains("!BuiltinSlash.Contains(slashName) && (playbook = _playbooks.Get(slashName)) != null", src);
        var pin = src.IndexOf("agent.PinnedTools = playbook?.Tools;", StringComparison.Ordinal);
        var run = src.IndexOf("await agent.RunAsync", StringComparison.Ordinal);
        Assert.True(pin >= 0 && pin < run);
        Assert.Contains("agent.PinnedTools = null;", src);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
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
