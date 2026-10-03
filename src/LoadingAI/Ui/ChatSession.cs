using System.Text.Json;
using LoadingAI.Llm;
using LoadingAI.Plugins.Core;
using LoadingAI.Progress;
using LoadingAI.Protocol;
using LoadingAI.Revit;
using LoadingAI.Tools;

namespace LoadingAI.Ui;

/// <summary>Agent run/stop, tool execute, turn TransactionGroup. Not a WPF type.</summary>
internal sealed class ChatSession
{
    private readonly WebHostBridge _web;
    private readonly ApprovalGate _approvals;
    private readonly Action<Action> _onUi;
    private readonly OpenAiCompatibleClient _client = new();

    private AppSettings _settings = ProviderSettings.Load();
    private ToolRegistry _registry = new();
    private List<DiscoveredPlugin> _discovered = new();
    private AgentLoop? _agent;
    private bool _isRunning;
    private bool _turnWrote;
    private bool _turnFailed;
    private CancellationTokenSource? _cts;
    private string _lastUserText = "";

    public ChatSession(WebHostBridge web, ApprovalGate approvals, Action<Action> onUi)
    {
        _web = web;
        _approvals = approvals;
        _onUi = onUi;
    }

    public AppSettings Settings
    {
        get => _settings;
        set => _settings = value;
    }

    public OpenAiCompatibleClient Client => _client;
    public ToolRegistry Registry => _registry;
    public IReadOnlyList<DiscoveredPlugin> Discovered => _discovered;
    public AgentLoop? Agent => _agent;
    public bool IsRunning => _isRunning;

    public void ReloadSettings() => _settings = ProviderSettings.Load();

    public void CancelAndDeny()
    {
        _cts?.Cancel();
        _approvals.DenyAllPending();
    }

    public void DisposeCts()
    {
        try { _cts?.Cancel(); _cts?.Dispose(); _cts = null; } catch { }
    }

    public void InitTools()
    {
        // The pane can raise Loaded again after Unloaded; re-discovery would unload live plugin contexts.
        if (_agent != null && _discovered.Count > 0) return;
        var bridge = App.Bridge;
        if (bridge == null)
        {
            _agent = null;
            return;
        }
        _discovered = PluginLoader.Discover(bridge, new PluginContextFactory(bridge));
        RebuildRegistry();
    }

    public void RebuildRegistry()
    {
        var reg = new ToolRegistry();
        foreach (var plugin in _discovered)
        {
            foreach (var entry in plugin.Tools)
            {
                bool defaultEnabled = entry.Tier != ToolTier.L3;
                bool enabled = ProviderSettings.IsToolEnabled(_settings, entry.Definition.Function.Name, defaultEnabled);
                if (enabled) reg.Register(entry);
            }
        }
        _registry = reg;
        if (_agent == null)
            _agent = new AgentLoop(_client, _registry);
        else
            _agent.ReplaceRegistry(_registry);
        ApplyPluginLoadWarnings();
    }

    public void ApplyPluginLoadWarnings()
    {
        if (_agent == null) return;
        var issues = PluginLoader.Issues;
        if (issues.Count == 0)
        {
            _agent.EnvironmentWarnings = null;
            return;
        }
        _agent.EnvironmentWarnings = string.Join("; ", issues.Select(i =>
        {
            var id = string.IsNullOrWhiteSpace(i.PluginId) ? i.Folder : i.PluginId;
            var hint = string.IsNullOrWhiteSpace(i.Hint) ? "" : $" ({i.Hint})";
            return $"{id}: {i.Summary}{hint}";
        }));
    }

    public object GetPluginsPayload()
    {
        var list = _discovered.Select(p => new
        {
            id = p.Meta.Id,
            name = p.Meta.Name,
            version = p.Meta.Version,
            license = p.Meta.License,
            author = p.Meta.Author,
            verified = p.Meta.Verified,
            folderPath = p.Meta.FolderPath,
            tools = p.Meta.Tools.Select(t => new
            {
                name = t.Name,
                tier = t.Tier,
                description = t.Description,
                enabledByDefault = t.EnabledByDefault,
                enabled = ProviderSettings.IsToolEnabled(_settings, t.Name, t.EnabledByDefault)
            }).ToList()
        }).ToList();
        return list;
    }

    public string GetStatusText()
    {
        var active = ProviderSettings.GetActive(_settings);
        if (active == null) return "No provider";
        if (DefaultLlm.IsBuiltin(active.Id))
            return $"Active: {DefaultLlm.DisplayName} | {DefaultLlm.DisplayModel}";
        return $"Active: {active.Name} | {active.BaseUrl} | {active.Model}";
    }

    public ProviderConfig? ResolveProviderForTest(string? providerId, ProviderConfig? provider)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(providerId))
            {
                var found = _settings.Providers.FirstOrDefault(p => p.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
                if (found != null) return DefaultLlm.BindForCall(found);
            }
            if (provider != null)
            {
                if (ProviderSettings.IsMaskedKey(provider.ApiKey) || string.IsNullOrWhiteSpace(provider.ApiKey))
                {
                    var stored = _settings.Providers.FirstOrDefault(p => p.Id.Equals(provider.Id, StringComparison.OrdinalIgnoreCase));
                    if (stored != null)
                        provider.ApiKey = stored.ApiKey;
                }
                if (string.IsNullOrWhiteSpace(provider.ApiKind)) provider.ApiKind = ProviderSettings.InferApiKind(provider);
                return DefaultLlm.BindForCall(provider);
            }
        }
        catch { }
        return null;
    }

    public bool TryReloadPlugins(out string message)
    {
        var bridge = App.Bridge;
        if (bridge == null)
        {
            message = "Please open a model first.";
            return false;
        }
        if (_isRunning)
        {
            message = "Stop the current run before reloading plugins.";
            return false;
        }
        _discovered = PluginLoader.Discover(bridge, new PluginContextFactory(bridge));
        RebuildRegistry();
        message = $"Reloaded {_discovered.Count} plugins, {_registry.GetToolDefinitions().Count} tools enabled.";
        if (PluginLoader.Issues.Count > 0)
            message += $" {PluginLoader.Issues.Count} plugin(s) failed to load.";
        return true;
    }

    public DiscoveredPlugin? FindPlugin(string pluginId)
        => _discovered.FirstOrDefault(p => p.Meta.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<string> EnabledL3Names()
        => _registry.Entries.Where(e => e.Tier == ToolTier.L3).Select(e => e.Definition.Function.Name).OrderBy(s => s);

    public int SetTierEnabled(string tier, bool enabled)
    {
        var n = 0;
        foreach (var plugin in _discovered)
        {
            foreach (var entry in plugin.Tools)
            {
                if (!entry.Tier.ToString().Equals(tier, StringComparison.OrdinalIgnoreCase)) continue;
                _settings.Plugins[entry.Definition.Function.Name] = enabled;
                n++;
            }
        }
        return n;
    }

    public void RestoreCheckpoint(List<ChatMessage> msgs) => _agent?.RestoreHistory(msgs);

    public void ClearHistory()
    {
        CancelAndDeny();
        _agent?.ClearHistory();
        try { HistoryStore.Append("system", "Cleared."); } catch { }
    }

    public async Task HandleSendAsync(string text, string mode, string slash, List<string> mentions, List<AttachmentDto> attachments)
    {
        if (!string.IsNullOrWhiteSpace(slash))
        {
            if (slash.Equals("plan", StringComparison.OrdinalIgnoreCase)) mode = "plan";
            else if (slash.Equals("ask", StringComparison.OrdinalIgnoreCase)) mode = "ask";
            else if (slash.Equals("build", StringComparison.OrdinalIgnoreCase)) mode = "build";
            else if (slash.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                if (_isRunning)
                {
                    _web.Post(new FromHostMessage.Error("Stop the current run before /clear."));
                    return;
                }
                _cts?.Cancel();
                _agent?.ClearHistory();
                try { HistoryStore.Append("system", "Cleared."); } catch { }
                _web.Post(new FromHostMessage.Status("Cleared."));
                return;
            }
            else if (slash.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                var help = "Commands: /plan <goal> read-only plan | /build <goal> write and execute | /ask <question> read-only Q&A | /clear clear session | /help help | /export ifc/dwg/pdf | /isolate isolate selection | /hide hide selection | /resume resume | /checkpoints list checkpoints\nMentions: @selection @activeView @visible @warnings @levels @sheets";
                _web.Post(new FromHostMessage.Assistant(help));
                return;
            }
            else if (slash.Equals("checkpoints", StringComparison.OrdinalIgnoreCase))
            {
                var cps = CheckpointStore.List();
                var txt = cps.Count == 0 ? "No checkpoints." : string.Join("\n", cps.Select(c => $"{c.Id} {c.CreatedAt:MM-dd HH:mm} {c.Mode} {c.Preview} ({c.MessageCount} msgs)"));
                _web.Post(new FromHostMessage.Checkpoints(cps));
                _web.Post(new FromHostMessage.Assistant(txt));
                return;
            }
            else if (slash.Equals("resume", StringComparison.OrdinalIgnoreCase))
            {
                if (_isRunning)
                {
                    _web.Post(new FromHostMessage.Error("Stop the current run before /resume."));
                    return;
                }
                var cps2 = CheckpointStore.List();
                var last = cps2.FirstOrDefault();
                if (last == null) { _web.Post(new FromHostMessage.Error("No checkpoints to resume.")); return; }
                var msgs2 = CheckpointStore.Load(last.Id);
                if (msgs2 != null)
                {
                    _agent?.RestoreHistory(msgs2);
                    _web.Post(new FromHostMessage.Status($"Resumed {last.Id}"));
                    _web.Post(new FromHostMessage.History(msgs2.Select(m => new FromHostMessage.HistoryMessage(m.Role, LlmJson.GetContentString(m.Content))).ToList()));
                }
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0) return;
        if (_isRunning)
        {
            _web.Post(new FromHostMessage.Error("Already working, please wait or press Stop."));
            return;
        }

        var provider = ProviderSettings.GetActiveForCall(_settings);
        if (provider == null)
        {
            _web.Post(new FromHostMessage.Error("No provider configured."));
            return;
        }
        if (DefaultLlm.IsBuiltin(provider.Id))
        {
            if (!DefaultLlm.HasKey)
            {
                _web.Post(new FromHostMessage.Error($"{DefaultLlm.DisplayName} is not available in this build."));
                return;
            }
        }
        else if (string.IsNullOrWhiteSpace(provider.ApiKey) && provider.BaseUrl.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) < 0)
        {
            _web.Post(new FromHostMessage.Error($"Provider '{provider.Name}' has no API key. Open Settings and paste your key."));
            return;
        }

        if (_agent == null)
        {
            _web.Post(new FromHostMessage.Error("Agent not ready. Restart Revit and reopen the pane."));
            return;
        }

        _isRunning = true;
        _lastUserText = text;
        // Created before the quota wait so Stop during "Checking allowance" is not lost.
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        if (DefaultLlm.IsBuiltin(provider.Id))
        {
            _web.Post(new FromHostMessage.Status($"Checking {DefaultLlm.DisplayName} allowance..."));
            var quota = await _client.ConsumeBuiltinQuotaAsync();
            _web.PostQuota(quota);
            if (!quota.Ok)
            {
                _isRunning = false;
                _web.Post(new FromHostMessage.Error(quota.Message ?? $"{DefaultLlm.DisplayName} is unavailable."));
                return;
            }
            if (ct.IsCancellationRequested)
            {
                BuiltinQuota.ClearTicket();
                _isRunning = false;
                _web.Post(new FromHostMessage.Error("Cancelled."));
                return;
            }
        }

        var agent = _agent;
        var progress = new ProgressReporter(snap =>
        {
            try { _onUi(() => _web.PostProgress(snap)); } catch { }
        });
        progress.Report(ProgressSnapshot.Thinking());
        _web.Post(new FromHostMessage.Status(mode == "plan" ? "Planning..." : mode == "ask" ? "Thinking..." : "Working..."));
        ContextSnapshot? ctxSnap = null;
        try
        {
            if (App.Bridge != null)
            {
                ctxSnap = await ContextCollector.CollectAsync(App.Bridge, ct);
                _onUi(() =>
                {
                    try { _web.Post(new FromHostMessage.Context(new FromHostMessage.ContextPayload(ctxSnap.SelectionCount, ctxSnap.SelectionPreview, ctxSnap.ActiveViewName, ctxSnap.VisibleCount, ctxSnap.WarningCount, ctxSnap.WarningPreview))); } catch { }
                });
            }
        }
        catch { }

        _ = Task.Run(async () =>
        {
            using (ProgressReporter.Push(progress))
            {
                _turnWrote = false;
                _turnFailed = false;
                try
                {
                    var enrichedText = text;
                    if (ctxSnap != null && mentions.Count > 0)
                        enrichedText = text + "\n\n" + ctxSnap.ToPromptString(mentions);
                    else if (ctxSnap != null && (mode == "plan" || mode == "build"))
                        enrichedText = text + "\n\n" + ctxSnap.ToPromptString(new[] { "selection", "activeView", "warnings" });
                    var userMsg = attachments.Count > 0 ? ChatMessage.FromMultimodal("user", enrichedText, attachments) : ChatMessage.FromText("user", enrichedText);
                    var effectiveMode = mode == "auto" ? null : mode;
                    string result = await agent.RunAsync(provider, userMsg, effectiveMode,
                        ExecuteToolAsync,
                        trace =>
                        {
                            try { _onUi(() => _web.Post(new FromHostMessage.Trace(trace))); } catch { }
                        },
                        delta =>
                        {
                            try { _onUi(() => _web.Post(new FromHostMessage.Delta(delta))); } catch { }
                        },
                        todos =>
                        {
                            try { _onUi(() => _web.Post(new FromHostMessage.Todo(todos))); } catch { }
                        },
                        snap => progress.Report(snap),
                        reasoning =>
                        {
                            if (string.IsNullOrEmpty(reasoning)) return;
                            try { _onUi(() => _web.Post(new FromHostMessage.Reasoning(reasoning, IsStreaming: true))); } catch { }
                        },
                        ct);

                    var finalStrip = ThinkTagStripper.Strip(result);
                    if (!string.IsNullOrEmpty(finalStrip.Reasoning))
                    {
                        try { _onUi(() => _web.Post(new FromHostMessage.Reasoning(finalStrip.Reasoning))); } catch { }
                    }
                    result = finalStrip.Visible;

                    await FinishTurnGroupAsync(ShouldAssimilateTurn(result));

                    _onUi(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(result))
                        {
                            _web.Post(new FromHostMessage.Assistant(result));
                            try { HistoryStore.Append("assistant", result); HistoryStore.Trim(); } catch { }
                            try { CheckpointStore.Save(agent.History, mode); } catch { }
                            if (string.Equals(mode, "plan", StringComparison.OrdinalIgnoreCase))
                            {
                                var draft = PlanParser.TryParse(result);
                                if (draft != null)
                                {
                                    _web.Post(new FromHostMessage.Plan(new FromHostMessage.PlanPayload(
                                        draft.Title,
                                        draft.Steps.Select(s => new FromHostMessage.PlanStepPayload(s.Title)).ToList())));
                                }
                            }
                        }
                        _web.Post(new FromHostMessage.Status(GetStatusText()));
                        _web.PostProgress(ProgressSnapshot.Complete());
                    });
                }
                catch (OperationCanceledException)
                {
                    await FinishTurnGroupAsync(assimilate: false);
                    progress.Report(ProgressSnapshot.Cancelled());
                    _onUi(() =>
                    {
                        _web.Post(new FromHostMessage.Error("Cancelled."));
                        _web.Post(new FromHostMessage.Status(GetStatusText()));
                    });
                }
                catch (Exception ex)
                {
                    await FinishTurnGroupAsync(assimilate: false);
                    progress.Report(ProgressSnapshot.Error(ex.Message));
                    _onUi(() => _web.Post(new FromHostMessage.Error(ex.Message)));
                }
                finally
                {
                    await FinishTurnGroupAsync(assimilate: false);
                    BuiltinQuota.ClearTicket();
                    _onUi(() =>
                    {
                        _isRunning = false;
                        try { _cts?.Dispose(); _cts = null; } catch { }
                    });
                }
            }
        });
    }

    public async Task<string> ExecuteToolAsync(string name, string argsJson)
    {
        if (!_registry.TryGet(name, out var entry))
        {
            var hint = ToolDiagnostics.DescribeMissing(
                name,
                _registry.GetToolDefinitions().Select(d => d.Function.Name).ToList(),
                _discovered.SelectMany(p => p.Tools.Select(t => (p.Meta.Id, t.Definition.Function.Name))).ToList());
            try { _onUi(() => _web.Post(new FromHostMessage.ToolResult(hint))); } catch { }
            return hint;
        }

        if (!ToolArgumentValidator.Validate(entry.Definition.Function.Parameters, argsJson, out argsJson, out var validationError))
        {
            var invalid = $"Tool argument error: {validationError}";
            try { _onUi(() => _web.Post(new FromHostMessage.ToolResult(invalid))); } catch { }
            return $"Tool argument error: {validationError}";
        }

        if (ToolPresets.HasPresets(entry) && !ToolPresets.IsDryRun(argsJson))
        {
            var preset = await _approvals.AwaitPresetAsync(entry, name, argsJson);
            if (!preset.approved)
            {
                try { _onUi(() => _web.Post(new FromHostMessage.ToolResult($"{name} {preset.message}"))); } catch { }
                MarkTurnFailedIfGroupActive();
                return preset.message;
            }
            argsJson = ToolPresets.Merge(argsJson, preset.valuesJson);
            if (!ToolArgumentValidator.Validate(entry.Definition.Function.Parameters, argsJson, out argsJson, out var presetValidationError))
            {
                var invalid = $"Tool argument error: {presetValidationError}";
                try { _onUi(() => _web.Post(new FromHostMessage.ToolResult(invalid))); } catch { }
                return invalid;
            }
            // dryRun is read-only, so the write confirm is skipped. Generic arg check, not a tool-name list.
            if (entry.Tier == ToolTier.L3 && !ToolPresets.IsDryRun(argsJson))
            {
                if (!await _approvals.ConfirmL3NativeAsync(name, argsJson))
                {
                    const string cancelled = "Cancelled by user.";
                    try { _onUi(() => _web.Post(new FromHostMessage.ToolResult($"{name} {cancelled}"))); } catch { }
                    MarkTurnFailedIfGroupActive();
                    return cancelled;
                }
            }
            else
            {
                try { _onUi(() => _web.Post(new FromHostMessage.ToolCall($"{name}: {argsJson}"))); } catch { }
            }
        }
        // L3: one host gate — Web approval (TCS) then native TaskDialog. Tools must not dialog.
        else if (entry.Tier == ToolTier.L3 && !ToolPresets.IsDryRun(argsJson))
        {
            var denied = await _approvals.AwaitL3WebAsync(entry, name, argsJson);
            if (denied != null)
            {
                MarkTurnFailedIfGroupActive();
                return denied;
            }

            if (!await _approvals.ConfirmL3NativeAsync(name, argsJson))
            {
                const string cancelled = "Cancelled by user.";
                try { _onUi(() => _web.Post(new FromHostMessage.ToolResult($"{name} {cancelled}"))); } catch { }
                MarkTurnFailedIfGroupActive();
                return cancelled;
            }
        }
        else
        {
            try { _onUi(() => _web.Post(new FromHostMessage.ToolCall($"{name}: {argsJson}"))); } catch { }
        }

        var dryRun = ToolPresets.IsDryRun(argsJson);
        var isWrite = entry.Tier == ToolTier.L2 || (entry.Tier == ToolTier.L3 && !dryRun);
        if (isWrite)
        {
            var bridge = App.Bridge;
            if (bridge == null)
                return "Please open a model first.";
            await bridge.EnsureTurnGroupAsync(_cts?.Token ?? default);
        }

        ProgressReporter.Current.Report($"Running {name}");
        var execution = await entry.ExecuteAsync(argsJson);
        var result = execution.Text;

        if (isWrite)
        {
            if (execution.WriteCertainty == ToolWriteCertainty.Committed)
                _turnWrote = true;
            if (!execution.IsSafeToAssimilate)
                MarkTurnFailedIfGroupActive();
        }
        else if (execution.Status is ToolExecutionStatus.Failed or ToolExecutionStatus.Cancelled or ToolExecutionStatus.Unknown)
        {
            MarkTurnFailedIfGroupActive();
        }

        try { _onUi(() => _web.Post(new FromHostMessage.ToolResult($"{name} result: {result}"))); } catch { }

        if (entry.Tier != ToolTier.L1)
        {
            try
            {
                string ids = "";
                try
                {
                    using var d = JsonDocument.Parse(argsJson);
                    if (d.RootElement.TryGetProperty("elementIds", out var eids)) ids = eids.GetRawText();
                    else if (d.RootElement.TryGetProperty("elementId", out var eid)) ids = eid.ToString();
                }
                catch { }
                AuditLog.Append(name, _lastUserText, ids, result, entry.Tier.ToString());
            }
            catch { }
        }
        return result;
    }

    private void MarkTurnFailedIfGroupActive()
    {
        if (App.Bridge is { HasActiveTurnGroup: true })
            _turnFailed = true;
    }

    private bool ShouldAssimilateTurn(string result)
        => _turnWrote && !_turnFailed && !IsCancelResult(result) && !IsAbortOrLlmError(result);

    private static bool IsCancelResult(string result)
        => result.StartsWith("Cancelled", StringComparison.OrdinalIgnoreCase);

    private static bool IsAbortOrLlmError(string result)
        => result.StartsWith("Max tool rounds", StringComparison.Ordinal)
           || result.StartsWith("LLM error:", StringComparison.Ordinal);

    /// <summary>
    /// Always finishes with <see cref="CancellationToken.None"/> so Cancel cannot
    /// skip rollback. No-op when the group was already assimilated or never started.
    /// </summary>
    private static async Task FinishTurnGroupAsync(bool assimilate)
    {
        var bridge = App.Bridge;
        if (bridge is not { HasActiveTurnGroup: true }) return;
        try
        {
            if (assimilate)
                await bridge.AssimilateTurnGroupAsync(CancellationToken.None);
            else
                await bridge.RollbackTurnGroupAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            HostLog.Write($"Turn TransactionGroup finish from HostPage failed: {ex}");
        }
    }
}
